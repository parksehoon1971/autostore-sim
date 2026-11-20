// 30초 시네마틱 카메라 (blender/tsorter_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
// 움직임을 살리는 동선: 로봇과 나란히 달리는 트래킹, 핑거 사이 틈으로 파고드는 샷, 위로 솟아오르는 크레인 샷.
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TSorter
{
    public class TSorterCamera : MonoBehaviour
    {
        public TSorterView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 30, pitch = 35, dist = 18;
        Vector3 orbitCenter;

        /// <summary>시네마틱 동선 안의 진행 비율 0..1 (캡션 타이밍에 사용)</summary>
        public float Progress { get; private set; }

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/tsorter_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 3, warmup 600 s 기준)
            keys = new[]
            {
                K(0.000f, -5.5f, -6.0f, 7.0f, 6.0f, 7.0f, 0.8f, 22),     // 전경: 앞 왼쪽 높이
                K(0.100f, -4.4f, 1.6f, 2.7f, -0.6f, 5.6f, 1.25f, 30),    // 투입 스테이션으로 내려감
                K(0.200f, -2.5f, 4.0f, 1.9f, -0.2f, 6.1f, 1.25f, 32),    // 작업자가 소포를 로봇에 올림
                K(0.267f, 1.2f, 4.3f, 2.0f, 3.5f, 5.7f, 1.3f, 28),       // 로봇 29 가 핑거 2 로 들어감
                K(0.300f, 4.4f, 4.75f, 1.85f, 6.4f, 5.7f, 1.3f, 26),     // 로봇과 나란히 달림
                K(0.350f, 8.4f, 4.75f, 1.85f, 10.2f, 5.7f, 1.3f, 26),
                K(0.400f, 10.0f, 4.6f, 2.2f, 11.0f, 5.65f, 1.25f, 32),
                K(0.450f, 11.2f, 3.6f, 2.3f, 11.1f, 5.2f, 0.9f, 30),     // 12.9 s 틸트 투하
                K(0.500f, 13.4f, 6.8f, 2.0f, 9.0f, 7.2f, 0.9f, 24),      // 핑거 끝을 돌아 틈으로
                K(0.567f, 10.2f, 7.2f, 1.65f, 7.4f, 7.4f, 0.95f, 26),    // 핑거 2·3 사이: 17.1 s 로봇 25 투하
                K(0.617f, 9.2f, 7.2f, 1.6f, 7.0f, 7.45f, 0.85f, 28),
                K(0.700f, 9.5f, 6.0f, 4.5f, 6.5f, 7.6f, 1.1f, 28),       // 위로 솟아오름: 21.8 s 로봇 12 투하
                K(0.767f, 8.5f, 5.0f, 6.5f, 6.0f, 8.0f, 1.1f, 24),
                K(0.850f, 11.5f, 13.5f, 4.2f, 8.0f, 8.4f, 1.2f, 28),     // 뒤쪽으로 돌아 핑거 3
                K(0.900f, 10.4f, 11.0f, 2.9f, 8.7f, 8.0f, 1.2f, 32),     // 27.0 s 로봇 17 투하
                K(1.000f, 15.5f, 16.0f, 7.0f, 5.5f, 7.0f, 0.8f, 22),     // 빠지며 전경
            };
            var c = view.config;
            orbitCenter = new Vector3((c.mainX + c.fingerLen) * (float)c.cell / 2, 1.1f, 4 * c.fingers * (float)c.cell / 2);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 데크·칸막이에 닿지 않게. Blender 의 auto-clamped 보간과 비슷)
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
                Progress = u;
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
            else { Progress = -1; Orbit(); }
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
