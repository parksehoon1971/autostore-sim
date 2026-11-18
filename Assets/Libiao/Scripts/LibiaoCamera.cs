// 30초 시네마틱 카메라 (Blender 스크립트 CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Libiao
{
    public class LibiaoCamera : MonoBehaviour
    {
        public LibiaoView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 35, pitch = 30, dist = 16;
        Vector3 orbitCenter;

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/libiao_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정 nx=19, ny=13, cell=0.7 기준)
            keys = new[]
            {
                K(0.00f, 15.6f, -9.0f, 7.6f, 6.6f, 4.5f, 1.8f, 24),   // 전경
                K(0.13f, 9.2f, -4.6f, 3.7f, 5.95f, -1.7f, 2.6f, 30),  // 앞쪽 투입 스테이션
                K(0.22f, 3.2f, -3.6f, 3.0f, 1.6f, 1.0f, 2.0f, 26),
                K(0.28f, -1.9f, -3.2f, 1.9f, 1.0f, 1.5f, 1.6f, 24),   // 데크 왼쪽 모서리 바깥
                K(0.35f, -1.4f, 4.55f, 1.6f, 3.0f, 4.0f, 1.4f, 22),   // 데크 아래 점검 통로
                K(0.45f, 4.0f, 4.55f, 1.6f, 8.0f, 3.2f, 1.3f, 22),    // 자루 사이
                K(0.55f, 13.7f, 4.55f, 1.7f, 10.0f, 6.6f, 1.6f, 24),
                K(0.63f, 15.4f, 6.6f, 3.7f, 10.0f, 5.4f, 2.4f, 24),   // 바깥으로 나와 상승
                K(0.72f, 11.0f, 9.6f, 3.6f, 7.0f, 7.3f, 2.5f, 28),
                K(0.78f, 8.4f, 8.8f, 3.2f, 6.8f, 7.35f, 2.55f, 35),   // 투하 근접
                K(0.84f, 8.0f, 8.4f, 3.3f, 6.6f, 7.2f, 2.5f, 35),
                K(0.91f, 12.6f, 11.5f, 5.0f, 7.0f, 5.0f, 2.4f, 26),
                K(1.00f, -3.5f, 16.0f, 7.8f, 6.6f, 4.5f, 1.8f, 24),   // 뒤쪽 전경
            };
            var c = view.config;
            orbitCenter = new Vector3(c.nx * c.cell / 2, c.deckZ * 0.6f, c.ny * c.cell / 2);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        void LateUpdate()
        {
            if (view == null || view.Sim == null) return;
            if (keys == null) BuildKeys();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera)
            {
                if (t0 < 0) t0 = Time.time;
                float u = (Time.time - t0) / duration;
                u = loop ? Mathf.Repeat(u, 1f) : Mathf.Clamp01(u);
                int i = 0; while (i < keys.Length - 2 && u > keys[i + 1].f) i++;
                var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(keys.Length - 1, i + 2)];
                // 구간 사이 속도가 이어지도록 선형 파라미터 + 처음/끝만 부드럽게
                float s = Mathf.InverseLerp(k1.f, k2.f, u);
                if (i == 0) s = Mathf.SmoothStep(0, 1, s * 0.5f) * 2f;
                transform.position = CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, s);
                var look = CatmullRom(k0.look, k1.look, k2.look, k3.look, s);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = Mathf.Lerp(k1.lens, k2.lens, Mathf.SmoothStep(0, 1, s));
                if (target) target.position = look;
            }
            else Orbit();
            if (dof != null && target) dof.focusDistance.value = Vector3.Distance(transform.position, target.position);
        }

        void Orbit()
        {
            Vector2 delta = Vector2.zero; float scroll = 0; bool drag = false;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) { drag = m.rightButton.isPressed || m.leftButton.isPressed; delta = m.delta.ReadValue() * 0.2f; scroll = m.scroll.ReadValue().y * 0.01f; }
#elif ENABLE_LEGACY_INPUT_MANAGER
            drag = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            delta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 3f; scroll = Input.mouseScrollDelta.y * 0.5f;
#endif
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, 2, 85); }
            dist = Mathf.Clamp(dist - scroll, 2, 45);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
