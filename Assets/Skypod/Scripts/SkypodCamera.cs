// 30초 시네마틱 카메라 (blender/skypod_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Skypod
{
    public class SkypodCamera : MonoBehaviour
    {
        public SkypodView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 30, pitch = 25, dist = 20;
        Vector3 orbitCenter;

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/skypod_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 3, warmup 680 s 기준)
            keys = new[]
            {
                K(0.000f, -6.5f, -9.5f, 7.5f, 6.0f, 3.2f, 3.8f, 24),      // 전경: 앞 왼쪽 높이
                K(0.100f, -1.0f, -7.5f, 3.6f, 3.0f, -0.5f, 1.6f, 26),
                K(0.200f, 3.4f, -5.4f, 2.4f, 1.1f, -1.8f, 0.85f, 30),     // 왼쪽 스테이션
                K(0.267f, 1.2f, -4.8f, 2.0f, -0.2f, -1.0f, 1.1f, 28),
                K(0.333f, -1.0f, -1.6f, 1.8f, 1.5f, 0.89f, 1.4f, 26),     // 왼쪽 모서리를 돌아
                K(0.390f, -1.4f, 0.89f, 1.8f, 6.1f, 0.89f, 1.6f, 24),     // 앞쪽 통로 입구
                K(0.440f, 1.2f, 0.89f, 2.3f, 6.1f, 0.89f, 2.8f, 26),      // 로봇이 올라가기 시작 (13 s)
                K(0.533f, 3.3f, 0.89f, 5.4f, 6.12f, 0.89f, 6.6f, 28),     // 함께 올라감
                K(0.600f, 3.9f, 0.89f, 7.0f, 6.12f, 0.55f, 7.25f, 30),    // 6.7 m: 토트를 꺼냄
                K(0.667f, 3.9f, 0.89f, 6.6f, 6.12f, 0.89f, 6.4f, 30),
                K(0.733f, 2.6f, 0.89f, 3.0f, 6.12f, 0.89f, 1.4f, 28),     // 싣고 내려옴
                K(0.800f, -0.8f, 0.89f, 2.0f, 5.0f, 0.89f, 1.0f, 24),     // 통로 입구로 물러남
                K(0.867f, -4.0f, -4.5f, 4.0f, 4.0f, 1.5f, 2.5f, 24),
                K(1.000f, 14.5f, -10.0f, 8.0f, 6.0f, 3.4f, 3.5f, 24),     // 빠지며 전경
            };
            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, 4f, (float)view.Sim.depthY / 2);
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
            dist = Mathf.Clamp(dist - scroll, 2, 50);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
