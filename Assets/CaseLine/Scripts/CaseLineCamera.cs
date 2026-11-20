// 30초 시네마틱 카메라 (blender/caseline_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CaseLine
{
    public class CaseLineCamera : MonoBehaviour
    {
        public CaseLineView view;
        public Transform target;
        public float duration = 30f;
        [HideInInspector] public DepthOfField dof;

        float yaw = 20, pitch = 28, dist = 7;
        Vector3 orbitCenter;

        struct Key { public float t; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // Blender 좌표 (x, y, z-up) → Unity
        static Key Kf(float t, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { t = t, pos = new Vector3(x, z, y), look = new Vector3(lx, lz, ly), lens = lens };

        void BuildKeys()
        {
            // blender/caseline_blender.py 의 CAM_KEYS 와 같은 값 (초 단위). 박스 1 (t = 5 s 에 집힘) 을 따라감
            keys = new[]
            {
                Kf(0.0f, -3.6f, -4.4f, 3.0f, 2.6f, 0.6f, 0.7f, 24),     // 전경: 라인 전체
                Kf(4.0f, -1.9f, -2.8f, 2.3f, -0.1f, 0.4f, 0.9f, 26),    // 제함기 쪽으로
                Kf(6.5f, -0.6f, -1.9f, 1.95f, 0.0f, 0.2f, 0.95f, 28),   // 블랭크 펼침 + 바닥 날개 접힘
                Kf(9.0f, 0.6f, -1.9f, 1.85f, 0.6f, 0.0f, 0.85f, 28),    // 푸셔가 컨베이어로 밀어냄
                Kf(12.5f, 1.5f, -1.9f, 2.0f, 1.9f, 0.3f, 0.95f, 28),    // 충전 슈트
                Kf(15.5f, 2.3f, -1.8f, 1.9f, 2.5f, 0.0f, 0.95f, 28),
                Kf(18.5f, 2.9f, -1.7f, 1.85f, 3.2f, 0.0f, 1.00f, 30),   // 상부 날개 접기
                Kf(21.0f, 3.3f, -1.5f, 2.1f, 4.2f, 0.0f, 1.00f, 30),    // 봉함기 진입, 테이프 헤드
                Kf(23.5f, 5.4f, -1.3f, 1.95f, 4.6f, 0.0f, 1.00f, 30),   // 테이핑된 박스 배출
                Kf(26.5f, 6.6f, -2.6f, 2.0f, 5.4f, 0.2f, 0.95f, 26),
                Kf(30.0f, 8.0f, -4.4f, 2.9f, 4.2f, 0.5f, 0.9f, 24),     // 빠지며 라인 전경
            };
            orbitCenter = new Vector3(4.2f, 0.9f, 0.3f);
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (Blender auto-clamped 보간과 비슷하게 곡선이 튀지 않게)
        static Vector3 Clamp(Vector3 v, Vector3 a, Vector3 b) =>
            new Vector3(Mathf.Clamp(v.x, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
                        Mathf.Clamp(v.y, Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)),
                        Mathf.Clamp(v.z, Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z)));

        void LateUpdate()
        {
            if (view == null) return;
            if (keys == null) BuildKeys();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera)
            {
                // 장면 상태는 5 s 주기라 30 s 마다 같은 장면 → 카메라도 30 s 반복
                float u = Mathf.Repeat((float)view.T, duration);
                int i = 0; while (i < keys.Length - 2 && u > keys[i + 1].t) i++;
                var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(keys.Length - 1, i + 2)];
                float s = Mathf.InverseLerp(k1.t, k2.t, u);
                // Blender 기본 베지어 키처럼 각 키에서 천천히 출발/도착
                float se = Mathf.SmoothStep(0, 1, s);
                float sm = Mathf.Lerp(s, se, 0.6f);
                transform.position = Clamp(CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, sm), k1.pos, k2.pos);
                var look = Clamp(CatmullRom(k0.look, k1.look, k2.look, k3.look, sm), k1.look, k2.look);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = Mathf.Lerp(k1.lens, k2.lens, se);
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
            dist = Mathf.Clamp(dist - scroll, 1, 25);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
