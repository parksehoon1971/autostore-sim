// 30초 시네마틱 카메라 + 수동 오비트 카메라
// 동선은 tools/export_shuttle_trace.js 가 계산해 Resources/ShuttleCamera.json 에 프레임별로 저장한 값을 그대로 씁니다
// (Blender 장면과 같은 카메라). 주인공 토트 하나를 셔틀 → 출고 버퍼 → 리프트 → 1층 컨베이어 → 스테이션까지 따라갑니다.
// 시뮬레이션 설정·warmup(1200 s)·seed(3) 를 바꾸면 장면 속 움직임이 달라지므로 내보내기를 다시 실행하세요.
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ShuttleRack
{
    [System.Serializable]
    public class ShuttleCamTrack
    {
        public float fps = 30, warmup = 1200; public int seed = 3, hero = -1;
        public float[] times;   // 셔틀 출발, 출고 버퍼, 리프트, 컨베이어, 피킹 시작, 피킹 끝 (초)
        public float[] data;    // 프레임마다 위치 xyz, 바라보는 점 xyz, 렌즈 mm (시뮬레이션 좌표)
        public int Frames => data == null ? 0 : data.Length / 7;
    }

    public class ShuttleCamera : MonoBehaviour
    {
        public ShuttleView view;
        public Transform target;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        public static ShuttleCamTrack LoadTrack()
        {
            var ta = Resources.Load<TextAsset>("ShuttleCamera");
            return ta ? JsonUtility.FromJson<ShuttleCamTrack>(ta.text) : null;
        }

        ShuttleCamTrack track;
        float t0 = -1;
        float yaw = 35, pitch = 22, dist = 16;
        Vector3 orbitCenter;

        public float Elapsed => t0 < 0 ? 0 : Time.time - t0;
        public void Restart() { t0 = Time.time; }

        void Init()
        {
            track = LoadTrack();
            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.bayW * 0.4f, 2.8f, (float)view.Sim.depthY / 2);
            if (track == null || track.Frames < 2) Debug.LogWarning("[ShuttleRack] Resources/ShuttleCamera.json 이 없어 오비트 카메라만 씁니다 (node tools/export_shuttle_trace.js).");
        }

        void Frame(int f, out Vector3 pos, out Vector3 look, out float lens)
        {
            int i = Mathf.Clamp(f, 0, track.Frames - 1) * 7; var d = track.data;
            pos = ShuttleView.U(d[i], d[i + 1], d[i + 2]); look = ShuttleView.U(d[i + 3], d[i + 4], d[i + 5]); lens = d[i + 6];
        }

        void LateUpdate()
        {
            if (view == null || view.Sim == null) return;
            if (orbitCenter == Vector3.zero) Init();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera && track != null && track.Frames > 1)
            {
                if (t0 < 0) t0 = Time.time;
                float dur = (track.Frames - 1) / track.fps;
                float u = Elapsed;
                u = loop ? Mathf.Repeat(u, dur) : Mathf.Min(u, dur);
                float fr = u * track.fps; int f0 = Mathf.FloorToInt(fr); float w = fr - f0;
                Frame(f0, out var p0, out var l0, out var n0);
                Frame(f0 + 1, out var p1, out var l1, out var n1);
                var look = Vector3.Lerp(l0, l1, w);
                transform.position = Vector3.Lerp(p0, p1, w);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = Mathf.Lerp(n0, n1, w);
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
            if (EquipFX.HudInput.PointerOverUi) { drag = false; scroll = 0; }   // 통합 HUD 위에서는 오비트 안 함
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, 2, 85); }
            dist = Mathf.Clamp(dist - scroll, 2, 45);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
