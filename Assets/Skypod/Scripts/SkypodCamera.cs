// 30초 시네마틱 카메라: 컷 기반 샷 구성 (로봇 동작 강조) + 수동 오비트 카메라
// 기본 설정(seed 3, warmup 680 s)에서 시뮬레이션을 돌려 확인한 동작에 맞췄습니다:
//   4.6~9.7 s 로봇 6 이 왼쪽 스테이션에서 토트를 내밂
//  13.0~17.1 s 로봇 5 가 앞쪽 통로(x 6.1 m)에서 랙을 타고 6.7 m 까지 올라감 → 17.1~19.3 s 토트 인출 → 19.3~23.4 s 하강
// 경로 키는 blender/skypod_blender.py CAM_KEYS 를 바탕으로 하되, 시선을 해당 로봇에 고정하고 샷을 나눴습니다.
using UnityEngine;
using EquipFX;

namespace Skypod
{
    public class SkypodCamera : CinematicRig
    {
        public SkypodView view;
        public int climber = 5, stationRobot = 6;

        protected override bool Ready => view != null && view.Sim != null;
        protected override bool Cinematic => view.cinematicCamera;

        Robot Rb(int i) { var L = view.Sim.robots; return L[((i % L.Count) + L.Count) % L.Count]; }
        Vector3 RobotAt(int i, float up) { var r = Rb(i); return V(r.px, r.py, r.pz + up); }

        protected override void BuildShots(ShotSequencer seq)
        {
            float ay = (float)view.Sim.RowY(1);   // 앞쪽 통로 중심 y (≈ 0.89 m)

            // 1) 전경 (타이틀 카드)
            seq.Add(Path(0f, 3.8f)
                .Add(0, V(-6.5f, -9.5f, 7.5f), V(6.0f, 3.2f, 3.8f), 24)
                .Add(1, V(-2.0f, -7.8f, 4.0f), V(3.4f, -0.4f, 1.8f), 26));

            // 2) 스테이션: 토트를 작업자에게 내미는 로봇
            seq.Add(new PathShot { start = 3.8f, end = 10.0f, lookTarget = () => RobotAt(stationRobot, 0.7f), lookLock = 0.45f,
                    title = "피킹 스테이션", detail = "로봇이 토트를 작업자 높이로 직접 가져와 내밉니다" }
                .Add(0, V(3.4f, -5.4f, 2.4f), V(1.1f, -1.8f, 0.85f), 30)
                .Add(1, V(2.2f, -4.4f, 1.75f), V(1.1f, -1.7f, 0.95f), 36));

            // 3) 통로 입구에서 랙을 타기 시작하는 로봇 (시선 고정)
            seq.Add(new PathShot { start = 10.0f, end = 15.2f, lookTarget = () => RobotAt(climber, 0.55f), lookLock = 0.65f,
                    title = "랙 등반", detail = "바닥을 달리던 로봇이 랙 기둥의 톱니 레일을 물고 그대로 올라갑니다" }
                .Add(0, V(-1.0f, -1.6f, 1.8f), V(1.5f, ay, 1.4f), 26)
                .Add(0.33f, V(-1.4f, ay, 1.8f), V(6.1f, ay, 1.6f), 24)
                .Add(0.62f, V(1.2f, ay, 2.3f), V(6.1f, ay, 2.8f), 26)
                .Add(1, V(2.8f, ay, 4.6f), V(6.12f, ay, 5.2f), 28));

            // 4) 6.7 m 높이: 팔을 뻗어 토트를 꺼내는 순간 근접
            seq.Add(new PathShot { start = 15.2f, end = 19.6f, lookTarget = () => RobotAt(climber, 0.6f), lookLock = 0.8f,
                    title = "토트 인출", detail = "목표 칸 앞에서 멈춰 팔을 뻗어 토트를 몸 안으로 끌어옵니다" }
                .Add(0, V(4.3f, ay, 6.0f), V(6.12f, ay, 6.6f), 32)
                .Add(1, V(4.7f, ay, 7.0f), V(6.12f, ay - 0.3f, 7.2f), 38));

            // 5) 토트를 싣고 내려오는 로봇과 같은 높이로 함께 하강
            seq.Add(new FollowShot
            {
                start = 19.6f, end = 23.8f, target = () => RobotAt(climber, 0.55f),
                offset0 = new Vector3(-2.6f, -0.9f, 0f), offset1 = new Vector3(-2.9f, 0.8f, 0f),   // 아래에서 올려다보다 위로 (앞 샷과 다른 각도)
                lookOffset = Vector3.zero, lens0 = 26, lens1 = 28, smooth = 0.3f,
                constrain = p => new Vector3(p.x, Mathf.Max(p.y, 0.8f), ay),   // 통로 가운데에서만 움직임
                title = "하강 · 반송", detail = "토트를 싣고 내려와 바로 스테이션으로 향합니다",
            });

            // 6) 앞쪽 주행로 전체
            seq.Add(Path(23.8f, 27.4f, "동시 운영", $"로봇 {view.config.robots}대가 랙 앞 주행로와 통로를 오가며 토트를 나릅니다")
                .Add(0, V(-0.8f, ay - 1.2f, 2.6f), V(5.0f, ay - 0.6f, 1.0f), 24)
                .Add(1, V(-4.0f, -4.5f, 4.0f), V(4.0f, 1.5f, 2.5f), 24));

            // 7) 빠지며 전경
            seq.Add(Path(27.4f, 30f)
                .Add(0, V(4.0f, -9.0f, 5.5f), V(6.0f, 3.0f, 3.2f), 24)
                .Add(1, V(14.5f, -10.0f, 8.0f), V(6.0f, 3.4f, 3.5f), 24));

            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, 4f, (float)view.Sim.depthY / 2);
            yaw = 30; pitch = 25; dist = 20;
        }
    }
}
