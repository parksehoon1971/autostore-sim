// 30초 시네마틱 카메라 (blender/carousel_blender.py CAM_KEYS / cam_eval 와 같은 식) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Carousel
{
    public class CarouselCamera : MonoBehaviour
    {
        public CarouselView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        float yaw = 30, pitch = 25, dist = 14;
        Vector3 orbitCenter;

        // 자막이 쓰는 영상 시간 (0..duration)
        public float Elapsed { get; private set; }

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) 그대로 보관, 위치 계산 뒤 Unity 로 변환
        static Key K(float f, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, pos = new Vector3(x, y, z), look = new Vector3(lx, ly, lz), lens = lens };

        void BuildKeys()
        {
            // blender/carousel_blender.py 의 CAM_KEYS 와 같은 값 (seed 22, warmup 300 s 기준)
            keys = new[]
            {
                K(0.000f, -5.2f, -6.8f, 4.6f, 3.4f, 2.2f, 1.4f, 24),   // 전경: 왼쪽 수평형 포드 2대 + 오른쪽 수직형
                K(0.110f, -3.6f, -4.2f, 2.6f, 0.3f, 0.6f, 1.2f, 26),   // 수평형 피킹면으로 다가감
                K(0.200f, -1.9f, -1.6f, 1.2f, 0.0f, 0.5f, 1.1f, 24),   // 낮게: 앞쪽 끝에서 캐리어가 돌아 나옴
                K(0.300f, -1.5f, 1.4f, 1.5f, 0.0f, 4.5f, 1.2f, 22),    // 옆을 따라가며 흘러가는 캐리어
                K(0.400f, -1.5f, 5.0f, 1.6f, 0.2f, 7.5f, 1.3f, 22),
                K(0.440f, -2.8f, 1.5f, 3.8f, 0.6f, 2.5f, 1.0f, 24),    // 옆으로 비켜 올라감
                K(0.480f, 1.15f, -3.2f, 5.2f, 1.15f, 2.2f, 0.9f, 24),  // 위로 빠져 포드 전체
                K(0.567f, 4.4f, -4.2f, 3.4f, 6.6f, 0.6f, 2.4f, 24),    // 수직형으로 넘어감
                K(0.650f, 5.6f, -1.9f, 1.5f, 6.6f, 0.3f, 1.1f, 30),    // 작업창: 피킹 라이트 + 물건을 꺼내 주문 토트로
                K(0.750f, 5.0f, -2.4f, 2.0f, 6.6f, 0.3f, 2.0f, 26),    // 앞 유리창으로 트레이가 내려오기 시작
                K(0.867f, 5.4f, -2.6f, 4.7f, 6.6f, 0.3f, 4.4f, 26),    // 카메라는 올라가고 트레이는 내려감
                K(0.933f, 4.6f, -4.6f, 4.6f, 6.6f, 0.6f, 3.0f, 24),
                K(1.000f, 12.0f, -7.5f, 5.0f, 3.6f, 2.4f, 2.2f, 24),   // 빠지며 전경
            };
            orbitCenter = new Vector3(3.4f, 1.5f, 2.5f);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 장비에 닿지 않게)
        static Vector3 Clamp(Vector3 v, Vector3 a, Vector3 b) =>
            new Vector3(Mathf.Clamp(v.x, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
                        Mathf.Clamp(v.y, Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)),
                        Mathf.Clamp(v.z, Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z)));

        // u (0..1) → 위치, 바라보는 점 (시뮬 좌표), 렌즈 mm. Blender cam_eval 과 같은 식
        public void Evaluate(float u, out Vector3 pos, out Vector3 look, out float lens)
        {
            if (keys == null) BuildKeys();
            int n = keys.Length, i = 0;
            while (i < n - 2 && u > keys[i + 1].f) i++;
            var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(n - 1, i + 2)];
            float s = Mathf.Clamp01(Mathf.InverseLerp(k1.f, k2.f, u));
            if (i == 0) s = Mathf.SmoothStep(0, 1, s * 0.5f) * 2f;
            if (i == n - 2) s = Mathf.SmoothStep(0, 1, 0.5f + s * 0.5f) * 2f - 1f;
            pos = Clamp(CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, s), k1.pos, k2.pos);
            look = Clamp(CatmullRom(k0.look, k1.look, k2.look, k3.look, s), k1.look, k2.look);
            lens = Mathf.Lerp(k1.lens, k2.lens, Mathf.SmoothStep(0, 1, s));
        }

        static Vector3 ToUnity(Vector3 v) => new Vector3(v.x, v.z, v.y);

        void LateUpdate()
        {
            if (view == null || view.Sim == null) return;
            if (keys == null) BuildKeys();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera)
            {
                if (t0 < 0) t0 = Time.time;
                float raw = (Time.time - t0) / duration;
                float u = loop ? Mathf.Repeat(raw, 1f) : Mathf.Clamp01(raw);
                Elapsed = u * duration;
                Evaluate(u, out var p, out var l, out var lens);
                transform.position = ToUnity(p);
                var look = ToUnity(l);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = lens;
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
            dist = Mathf.Clamp(dist - scroll, 2, 40);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            var c = ToUnity(orbitCenter);
            transform.position = c + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = c;
        }
    }
}
