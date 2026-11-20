// 30초 시네마틱 카메라 (blender/rapyuta_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Rapyuta
{
    public class RapyutaCamera : MonoBehaviour
    {
        public RapyutaView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 35, pitch = 30, dist = 15;
        Vector3 orbitCenter;

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/rapyuta_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 3, warmup 600 s 기준)
            keys = new[]
            {
                K(0.000f, 14.5f, -7.0f, 6.2f, 5.6f, 3.0f, 1.8f, 24),      // 전경: 앞 오른쪽 높이
                K(0.133f, 8.8f, -4.4f, 2.6f, 5.0f, -0.2f, 1.1f, 28),      // 피킹 스테이션 쪽으로
                K(0.267f, 4.6f, -3.4f, 2.2f, 3.6f, -0.3f, 1.1f, 32),      // 스테이션 근접
                K(0.367f, -3.4f, 0.6f, 1.3f, -0.35f, 3.25f, 1.0f, 28),    // 왼쪽 리프트
                K(0.450f, -3.6f, 1.3f, 2.0f, -0.35f, 3.25f, 1.7f, 28),    // 리프트와 함께 상승
                K(0.517f, -3.0f, 2.3f, 3.2f, -0.2f, 3.25f, 2.8f, 28),
                K(0.567f, -0.9f, 3.25f, 2.95f, 1.2f, 3.25f, 2.80f, 24),   // 리프트 바깥 면으로 4층 통로 진입
                K(0.633f, 1.2f, 3.25f, 2.92f, 3.5f, 3.25f, 2.78f, 24),    // 층 사이 통로
                K(0.700f, 5.0f, 3.25f, 2.92f, 7.8f, 3.25f, 2.78f, 24),
                K(0.767f, 7.8f, 3.25f, 2.95f, 11.0f, 3.1f, 2.8f, 24),
                K(0.800f, 11.4f, 3.25f, 2.98f, 11.8f, 2.0f, 2.85f, 24),   // 오른쪽 리프트 사이로 빠져나옴
                K(0.850f, 13.2f, 1.9f, 3.05f, 10.2f, 1.35f, 2.8f, 30),    // 앞쪽 빈을 들어 옮기는 장면
                K(0.900f, 13.0f, 1.0f, 3.1f, 10.3f, 1.4f, 2.85f, 32),
                K(1.000f, 16.5f, -4.0f, 6.0f, 6.5f, 3.0f, 2.2f, 24),      // 빠지며 전경
            };
            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, (float)(c.baseZ + c.nf * c.floorH) * 0.5f, c.ny * (float)c.cellY / 2);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 빈·기둥에 닿지 않게. Blender 의 auto-clamped 보간과 비슷)
        static Vector3 Clamp(Vector3 v, Vector3 a, Vector3 b) =>
            new Vector3(Mathf.Clamp(v.x, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
                        Mathf.Clamp(v.y, Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)),
                        Mathf.Clamp(v.z, Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z)));

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
                transform.position = Clamp(CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, s), k1.pos, k2.pos);
                var look = Clamp(CatmullRom(k0.look, k1.look, k2.look, k3.look, s), k1.look, k2.look);
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
