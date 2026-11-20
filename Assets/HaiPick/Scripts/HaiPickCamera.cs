// 30초 시네마틱 카메라 (blender/haipick_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HaiPick
{
    public class HaiPickCamera : MonoBehaviour
    {
        public HaiPickView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 35, pitch = 25, dist = 22;
        Vector3 orbitCenter;

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/haipick_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 10, warmup 600 s 기준)
            keys = new[]
            {
                K(0.000f, 17.5f, -7.0f, 8.0f, 6.0f, 4.0f, 4.0f, 24),        // 전경: 앞 오른쪽 높이 (12 m 랙)
                K(0.117f, 13.6f, 4.2f, 2.4f, 9.1f, 5.63f, 2.0f, 30),        // 뒤쪽 통로 끝으로
                K(0.200f, 12.6f, 5.63f, 4.9f, 9.075f, 5.63f, 4.9f, 35),     // 통로 안: 로봇이 레일을 타고 올라감
                K(0.290f, 11.2f, 5.55f, 7.7f, 9.075f, 5.9f, 7.55f, 35),     // 7.2 m: 포크로 토트를 꺼냄
                K(0.367f, 10.4f, 5.45f, 8.3f, 9.075f, 6.1f, 7.5f, 30),
                K(0.433f, 12.8f, 5.63f, 9.8f, 9.075f, 5.63f, 6.4f, 28),     // 위로 빠지며 내려가는 로봇
                K(0.500f, 12.0f, -3.6f, 4.0f, 9.0f, -0.4f, 1.0f, 28),       // 앞으로 날아와 작업대
                K(0.567f, 10.9f, -3.3f, 2.0f, 9.075f, -0.5f, 1.0f, 35),     // 작업대: 로봇이 올라가 토트를 내밂
                K(0.640f, 7.8f, -3.2f, 1.9f, 9.1f, -0.4f, 0.95f, 38),
                K(0.680f, 7.975f, -1.5f, 0.48f, 7.975f, 3.0f, 0.42f, 24),   // 랙 밑으로 들어감
                K(0.733f, 7.975f, 3.3f, 0.45f, 9.075f, 4.9f, 0.38f, 24),    // 통로 4: 로봇이 랙 밑을 지나 나옴
                K(0.800f, 8.2f, 3.49f, 0.5f, 10.3f, 3.49f, 0.42f, 26),
                K(0.867f, 8.6f, 3.49f, 0.55f, 10.725f, 2.2f, 0.42f, 28),    // 앞쪽 랙 밑으로 다시 들어가는 로봇
                K(0.933f, 13.5f, 3.49f, 1.6f, 9.0f, 3.49f, 1.0f, 26),       // 통로 끝으로 빠져나옴
                K(1.000f, 18.0f, -6.0f, 7.5f, 6.0f, 3.5f, 3.5f, 24),        // 빠지며 전경
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

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 랙·토트에 닿지 않게. Blender 의 auto-clamped 보간과 비슷)
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
