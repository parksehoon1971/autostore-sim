// 30초 시네마틱 카메라 (blender/stackercrane_blender.py CAM_KEYS 와 같은 동선) + 수동 오비트 카메라
// rel 키는 크레인 0 의 (x, 통로 중심 y, 포크 윗면 z) 기준 상대 위치라서, 카메라가 크레인을 따라 달리고 올라갑니다.
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StackerCrane
{
    public class StackerCraneCamera : MonoBehaviour
    {
        public StackerCraneView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float yaw = 35, pitch = 22, dist = 34;
        Vector3 orbitCenter;

        struct Key { public float f; public bool rel; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // 시뮬레이션 좌표 (x, y, z-up) 그대로 저장. 평가할 때 Unity 좌표로 바꿈
        static Key K(float f, int rel, float x, float y, float z, float lx, float ly, float lz, float lens) =>
            new Key { f = f, rel = rel != 0, pos = new Vector3(x, y, z), look = new Vector3(lx, ly, lz), lens = lens };

        void BuildKeys()
        {
            // blender/stackercrane_blender.py 의 CAM_KEYS 와 같은 값 (기본 설정, seed 6, warmup 624 s 기준)
            keys = new[]
            {
                K(0.000f, 0, -14.0f, -10.0f, 9.5f, 6.0f, 4.0f, 6.0f, 22),     // 전경: 앞 왼쪽 높이
                K(0.050f, 0, -10.5f, -6.5f, 5.5f, -2.0f, 1.2f, 1.8f, 24),
                K(0.120f, 0, -5.2f, -1.6f, 4.6f, -1.8f, 0.9f, 1.3f, 26),       // 입고 컨베이어 위: 포크가 팔레트를 받음
                K(0.200f, 0, -6.2f, 1.5f, 3.4f, -1.8f, 1.6f, 1.5f, 26),        // 크레인 뒤로
                K(0.250f, 1, -4.6f, -0.25f, 1.0f, 0.8f, 0.1f, 0.5f, 24),       // 출발: 크레인 뒤를 따라감
                K(0.400f, 1, -4.2f, -0.30f, 1.2f, 1.0f, 0.1f, 0.4f, 22),       // 최고 속도 4 m/s
                K(0.540f, 1, -3.6f, -0.35f, 0.9f, 0.3f, 0.4f, 0.2f, 24),       // 도착, 위치 보정
                K(0.640f, 1, -3.0f, -0.45f, 0.7f, 0.0f, 0.9f, 0.1f, 26),       // 포크가 랙으로 들어가 내려놓음
                K(0.720f, 1, -3.2f, -0.40f, 0.9f, 0.4f, 0.3f, 0.0f, 24),
                K(0.800f, 1, -3.8f, -0.40f, 1.6f, 1.0f, 0.2f, 0.0f, 22),       // 다음 칸으로 이동
                K(0.880f, 0, 10.0f, 1.75f, 8.6f, 24.4f, 1.85f, 8.4f, 26),      // 통로를 거슬러 물러남
                K(1.000f, 0, 2.5f, 1.85f, 8.9f, 24.4f, 1.85f, 8.3f, 45),       // 통로 끝에서 길게
            };
            var c = view.config;
            orbitCenter = StackerCraneView.U(c.nx * c.slotW / 2 - 2, view.Sim.depthY / 2, 7);
        }

        public void Restart() { }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // 축마다 두 키 사이로 제한 (곡선이 키 밖으로 튀어 랙에 닿지 않게)
        static Vector3 Clamp(Vector3 v, Vector3 a, Vector3 b) =>
            new Vector3(Mathf.Clamp(v.x, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
                        Mathf.Clamp(v.y, Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)),
                        Mathf.Clamp(v.z, Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z)));

        /// <summary>u (0..1) 와 크레인 0 기준점 ref (시뮬레이션 좌표) 에서 위치·바라보는 점(시뮬레이션 좌표)·렌즈. Blender cam_eval 과 같은 계산</summary>
        public void Evaluate(float u, Vector3 refPt, out Vector3 pos, out Vector3 look, out float lens)
        {
            int n = keys.Length, i = 0;
            while (i < n - 2 && u > keys[i + 1].f) i++;
            var ks = keys;
            System.Func<int, Vector3> P = j => ks[j].rel ? ks[j].pos + refPt : ks[j].pos;
            System.Func<int, Vector3> L = j => ks[j].rel ? ks[j].look + refPt : ks[j].look;
            int j0 = Mathf.Max(0, i - 1), j3 = Mathf.Min(n - 1, i + 2);
            float s = Mathf.Clamp01((u - keys[i].f) / (keys[i + 1].f - keys[i].f));
            if (i == 0) s = Mathf.SmoothStep(0, 1, s * 0.5f) * 2f;
            if (i == n - 2) s = 1f - Mathf.SmoothStep(0, 1, (1f - s) * 0.5f) * 2f;
            pos = Clamp(CatmullRom(P(j0), P(i), P(i + 1), P(j3), s), P(i), P(i + 1));
            look = Clamp(CatmullRom(L(j0), L(i), L(i + 1), L(j3), s), L(i), L(i + 1));
            lens = Mathf.Lerp(keys[i].lens, keys[i + 1].lens, Mathf.SmoothStep(0, 1, s));
        }

        void LateUpdate()
        {
            if (view == null || view.Sim == null) return;
            if (keys == null) BuildKeys();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera)
            {
                // 영상 시간 = 시뮬레이션 시간 - 워밍업 (녹화·Blender 궤적과 같은 기준)
                float u = view.MovieTime / duration;
                u = loop ? Mathf.Repeat(u, 1f) : Mathf.Clamp01(u);
                var cr = view.Sim.cranes[0];
                var refPt = new Vector3((float)cr.x, (float)view.Sim.AisleY(cr.aisle), (float)cr.z);
                Evaluate(u, refPt, out var p, out var l, out var lens);
                transform.position = StackerCraneView.U(p.x, p.y, p.z);
                var lu = StackerCraneView.U(l.x, l.y, l.z);
                transform.rotation = Quaternion.LookRotation(lu - transform.position, Vector3.up);
                cam.focalLength = lens;
                if (target) target.position = lu;
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
            if (EquipFX.HudInput.PointerOverUi) { drag = false; scroll = 0; }   // 통합 HUD 위에서는 오비트 안 함
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, 2, 85); }
            dist = Mathf.Clamp(dist - scroll, 3, 80);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
