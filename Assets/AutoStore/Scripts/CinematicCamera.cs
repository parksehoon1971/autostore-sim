// 30초 시네마틱 카메라: 컷 기반 샷 구성 (설비 동작 강조) + 수동 오비트 카메라
// 샷 시점은 기본 설정(seed, 워밍업 900 s)에서 시뮬레이션을 돌려 찾은 실제 동작에 맞췄습니다:
//   5~11 s 로봇 7 이 앞줄을 달려 오른쪽 앞 모서리 스택에서 빈을 집음
//  11~15 s 로봇 3 이 왼쪽 가장자리 스택(0,5)에서 디깅 → 빈이 스택 안에서 올라옴 (그리드 옆이 열려 있어 보임)
//  18~24 s 로봇 1 이 가운데 포트로 빈을 내려 줌
//  24~27 s 로봇 0 이 빈을 싣고 그리드 안쪽으로 이동
// 설정을 바꿔 다른 동작이 나오더라도 추적 샷은 해당 로봇을 계속 따라가므로 화면은 유지됩니다.
using UnityEngine;
using EquipFX;

namespace AutoStore
{
    public class CinematicCamera : CinematicRig
    {
        public AutoStoreView view;

        protected override bool Ready => view != null && view.Sim != null;
        protected override bool Cinematic => view.cinematicCamera;

        // Blender 좌표 (x, y, z-up) → Unity (x, z, y)
        static Vector3 B(float x, float y, float z) => new Vector3(x, z, y);

        Robot Rb(int i) => view.Sim.robots[((i % view.Sim.robots.Count) + view.Sim.robots.Count) % view.Sim.robots.Count];
        Vector3 RobotTop(int i) { var r = Rb(i); return B((float)r.px, (float)r.py, (float)view.Sim.topZ + 0.35f); }
        // 그리퍼에 매달린 빈(또는 빈 그리퍼)의 중심
        Vector3 GripLoad(int i) { var r = Rb(i); return B((float)r.px, (float)r.py, (float)(view.Sim.restZ - r.g) + 0.17f); }

        protected override void BuildShots(ShotSequencer seq)
        {
            var c = view.config;
            float cx = c.nx * c.cellX / 2, cy = c.ny * c.cellY / 2, T = (float)view.Sim.topZ;
            float portX = (float)view.Sim.WorldX(c.ports[c.ports.Length / 2]), portY = (float)view.Sim.WorldY(-1);

            // 1) 전경: 천천히 다가감 (타이틀 카드)
            seq.Add(new PathShot { start = 0f, end = 5f, easeEnds = false }
                .Add(0, B(cx + 11f, cy - 12f, T + 3.6f), B(cx, cy, T * 0.5f), 24)
                .Add(1, B(cx + 7.5f, cy - 8.8f, T + 2.4f), B(cx, cy, T * 0.55f), 26));

            // 2) 로봇 추적: 앞줄을 달리는 로봇 옆에서 함께 이동, 끝에서 앞쪽으로 돌아 들어가며 집는 동작
            seq.Add(new FollowShot
            {
                start = 5f, end = 11.3f, target = () => RobotTop(7),
                offset0 = new Vector3(-1.5f, 0.85f, -2.0f), offset1 = new Vector3(0.9f, 0.75f, -1.7f),
                lookOffset = new Vector3(0, -0.12f, 0), lens0 = 32, lens1 = 36, smooth = 0.25f,
                title = "상부 그리드 주행", detail = "로봇이 레일 위를 셀 단위로 달려 목표 스택 위에 정확히 멈춥니다  ·  최고 3.1 m/s",
            });

            // 3) 디깅: 왼쪽 가장자리 스택 옆에서 빈과 같은 높이로 따라 올라감 (엘리베이터 샷)
            seq.Add(new FollowShot
            {
                start = 11.3f, end = 15f, target = () => GripLoad(3),
                offset0 = new Vector3(-2.4f, 0.35f, -0.6f), offset1 = new Vector3(-2.15f, 0.65f, -0.95f),
                lookOffset = Vector3.zero, lens0 = 35, lens1 = 32, smooth = 0.3f,
                constrain = p => new Vector3(Mathf.Min(p.x, -1.2f), Mathf.Max(p.y, 0.6f), p.z),
                title = "빈 인출 (디깅)", detail = "그리퍼가 스택 안으로 내려가 빈을 잡고 위로 끌어올립니다",
            });

            // 4) 위에서 내려다본 전체 운영
            seq.Add(new PathShot { start = 15f, end = 17.8f }
                .Add(0, B(cx - 1.0f, cy - 2.0f, T + 4.5f), B(cx, cy, T), 18)      // 천장(11 m) 조명 아래
                .Add(1, B(cx + 1.2f, cy - 1.5f, T + 4.1f), B(cx + 0.2f, cy + 0.1f, T), 18));
            seq.shots[seq.shots.Count - 1].title = "동시 운영";
            seq.shots[seq.shots.Count - 1].detail = $"로봇 {c.robots}대가 경로를 예약하며 서로 비켜 다닙니다";

            // 5) 포트: 로봇이 내려 주는 빈을 따라 시선이 내려옴 → 작업자 높이에서 도착
            int portBin = -1;
            var port = new PathShot { start = 17.8f, end = 24.2f, lookLock = 0.75f, onBegin = () => portBin = -1 };
            port.lookTarget = () =>
            {
                var r = Rb(1);
                if (portBin < 0 && r.carry >= 0) portBin = r.carry;
                if (portBin >= 0) { var p = view.Sim.BinPose(portBin); return B((float)p.x, (float)p.y, (float)p.h + 0.17f); }
                return GripLoad(1);
            };
            port.Add(0, B(portX + 1.6f, portY - 3.3f, 4.3f), B(portX, portY, 3.5f), 30)
                .Add(0.5f, B(portX + 1.3f, portY - 2.8f, 2.8f), B(portX, portY, 1.6f), 33)
                .Add(1, B(portX + 1.0f, portY - 2.3f, 2.0f), B(portX, portY - 0.1f, 1.15f), 35);
            port.title = "포트로 전달"; port.detail = "빈이 작업자 포트까지 내려와 피킹을 기다립니다";
            seq.Add(port);

            // 6) 빈을 싣고 이동하는 로봇: 뒤에서 옆으로 돌며 따라감
            seq.Add(new FollowShot
            {
                start = 24.2f, end = 27.2f, target = () => RobotTop(0),
                offset0 = new Vector3(0.9f, 1.3f, -1.0f), offset1 = new Vector3(0.9f, 1.1f, 0.9f),
                lookOffset = new Vector3(0, -0.15f, 0), lens0 = 30, lens1 = 32, smooth = 0.25f,
                title = "반송", detail = "꺼낸 빈을 싣고 다음 목적지로 이동합니다",
            });

            // 7) 빠지며 전경 (끝 페이드)
            seq.Add(new PathShot { start = 27.2f, end = 30f }
                .Add(0, B(cx - 5.5f, cy + 6.5f, T + 2.6f), B(cx, cy, T * 0.6f), 26)
                .Add(1, B(cx - 9.5f, cy + 9.5f, T + 4.2f), B(cx, cy, T * 0.5f), 24));

            orbitCenter = B(cx, cy, T * 0.5f); yaw = 35; pitch = 25; dist = 14; minPitch = 5; maxDist = 40;
        }
    }
}
