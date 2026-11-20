// 30초 시네마틱 카메라 (blender/airrob_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
// 동작을 따라붙는 근접 샷 위주: AirRob 이 달려오며 오름 → 맨 위에서 훅으로 토트를 당김 → 내려옴 → 랙 아래 바닥 로봇에 넘김 → 로봇 출발
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AirRob
{
    public class AirRobCamera : MonoBehaviour
    {
        public AirRobView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        /// <summary>시네마틱 동선 시작 후 지난 시간 (캡션 타이밍용, 0 ~ duration)</summary>
        public float Elapsed { get; private set; }

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
            // blender/airrob_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 3, warmup 1342 s 기준)
            keys = new[]
            {
                K(0.000f, 20.5f, -8.5f, 7.0f, 7.0f, 4.0f, 2.2f, 24),        // 전경: 앞 오른쪽 높이
                K(0.100f, 17.2f, 1.10f, 3.2f, 6.0f, 1.085f, 2.6f, 50),      // 통로 0 끝 (망원): AirRob 이 달려오며 올라감
                K(0.200f, 16.9f, 1.15f, 4.8f, 9.5f, 1.0f, 4.3f, 45),
                K(0.300f, 16.6f, 0.90f, 6.5f, 14.0f, 0.90f, 5.8f, 36),      // 9 s: 맨 위 단 도착
                K(0.350f, 15.9f, 0.05f, 6.7f, 14.17f, 0.70f, 5.80f, 38),    // 랙 끝 바깥: 훅이 토트를 걸어 당김
                K(0.433f, 15.9f, 0.30f, 6.5f, 14.17f, 0.95f, 5.80f, 38),
                K(0.467f, 16.2f, 1.45f, 5.4f, 14.1f, 0.95f, 5.1f, 36),      // 14 s: 내려가기 시작
                K(0.600f, 16.4f, 1.30f, 2.6f, 14.0f, 0.95f, 2.0f, 34),
                K(0.680f, 16.2f, -0.6f, 1.1f, 14.0f, 0.60f, 0.75f, 30),     // 모서리를 돌아 터널 앞
                K(0.750f, 15.6f, -2.05f, 1.00f, 13.95f, 0.33f, 0.50f, 28),  // 랙 아래 바닥 로봇 위로 토트를 밀어 줌
                K(0.833f, 16.0f, -2.10f, 1.05f, 14.0f, 0.33f, 0.50f, 30),
                K(0.900f, 17.4f, -2.6f, 1.4f, 14.85f, -0.10f, 0.45f, 28),   // 로봇이 터널을 나와 앞쪽으로
                K(1.000f, 16.5f, -7.0f, 3.6f, 8.0f, -0.8f, 1.0f, 24),       // 스테이션 쪽 전경
            };
            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, 3f, (float)view.Sim.depthY / 2);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 랙·기둥에 닿지 않게. Blender 의 auto-clamped 보간과 비슷)
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
                Elapsed = u * duration;
                int i = 0; while (i < keys.Length - 2 && u > keys[i + 1].f) i++;
                var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(keys.Length - 1, i + 2)];
                float s = Mathf.InverseLerp(k1.f, k2.f, u);
                if (i == 0) s = Mathf.SmoothStep(0, 1, s * 0.5f) * 2f;
                transform.position = Clamp(CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, s), k1.pos, k2.pos);
                var look = Clamp(CatmullRom(k0.look, k1.look, k2.look, k3.look, s), k1.look, k2.look);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = Mathf.Lerp(k1.lens, k2.lens, Mathf.SmoothStep(0, 1, s));
                if (target) target.position = look;
            }
            else { Elapsed = -1; Orbit(); }
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
            if (EquipFX.HudInput.PointerOverUi) { drag = false; scroll = 0; }   // 통합 HUD 위에서는 오비트 안 함
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, 2, 85); }
            dist = Mathf.Clamp(dist - scroll, 2, 50);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
