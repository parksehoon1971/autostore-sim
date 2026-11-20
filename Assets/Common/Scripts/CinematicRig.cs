// 장면 카메라 공통 부분: 샷 시퀀서 재생, DOF 초점, 컷 프레임 모션 블러 끄기, 마우스 오비트, 자막 시간 제공.
// 장면별 카메라는 BuildShots() 에서 샷만 정의합니다.
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace EquipFX
{
    public abstract class CinematicRig : MonoBehaviour, ICaptionSource
    {
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;
        [HideInInspector] public MotionBlur motionBlur;

        protected float yaw = 35, pitch = 30, dist = 16, minPitch = 2, maxDist = 45;
        protected Vector3 orbitCenter;
        float t0 = -1, mbBase = -1;
        ShotSequencer seq;

        public ShotSequencer Seq => seq;
        public float VideoTime { get; private set; }
        public bool CinematicActive => Ready && Cinematic && seq != null;

        protected abstract bool Ready { get; }        // 시뮬레이션이 만들어졌는지
        protected abstract bool Cinematic { get; }    // 시네마틱 모드인지 (끄면 마우스 오비트)
        protected abstract void BuildShots(ShotSequencer s);
        // 장면이 자체 시계를 쓰면 (예: 제함·봉함 라인의 view.T) 그 값을 돌려줌. null 이면 Restart() 기준 경과 시간
        protected virtual float? ExternalTime => null;

        // 시뮬레이션 좌표 (x, y, z-up) → Unity (x, z, y)
        protected static Vector3 V(float x, float y, float z) => new Vector3(x, z, y);
        protected static Vector3 V(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);

        public void Restart() { t0 = Time.time; if (seq != null) seq.Reset(); }

        protected virtual void LateUpdate()
        {
            if (!Ready) return;
            if (seq == null) { seq = new ShotSequencer(); BuildShots(seq); }
            var cam = GetComponent<Camera>();
            bool cut = false;
            if (Cinematic)
            {
                if (t0 < 0) t0 = Time.time;
                float tt = ExternalTime ?? (Time.time - t0);
                VideoTime = loop ? Mathf.Repeat(tt, duration) : Mathf.Min(tt, duration - 1e-3f);
                var p = seq.Evaluate(VideoTime, Time.deltaTime, out cut);
                transform.position = p.pos;
                var dir = p.look - p.pos;
                if (dir.sqrMagnitude > 1e-8f) transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                cam.focalLength = p.lens;
                if (target) target.position = p.look;
            }
            else Orbit();
            if (dof != null && target) dof.focusDistance.value = Vector3.Distance(transform.position, target.position);
            // 컷 프레임에는 모션 블러를 끔 (이전 프레임과의 카메라 차이로 화면 전체가 번지는 것 방지)
            if (motionBlur != null)
            {
                if (mbBase < 0) mbBase = motionBlur.intensity.value;
                motionBlur.intensity.value = cut ? 0f : mbBase;
            }
        }

        void Orbit()
        {
            Vector2 delta = Vector2.zero; float scroll = 0; bool drag = false;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) { drag = m.rightButton.isPressed || m.leftButton.isPressed; delta = m.delta.ReadValue() * 0.2f; scroll = EquipFX.HudInput.WheelNotches(m.scroll.ReadValue().y); }
#elif ENABLE_LEGACY_INPUT_MANAGER
            drag = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            delta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 3f; scroll = Input.mouseScrollDelta.y;
#endif
            if (HudInput.PointerOverUi) { drag = false; scroll = 0; }   // 통합 HUD 위에서는 오비트 안 함
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, minPitch, 85); }
            dist = Mathf.Clamp(dist * Mathf.Pow(0.88f, scroll), 2, maxDist);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }

        // 자주 쓰는 샷 생성 도우미
        protected static PathShot Path(float start, float end, string title = null, string detail = null)
            => new PathShot { start = start, end = end, title = title, detail = detail };
    }
}
