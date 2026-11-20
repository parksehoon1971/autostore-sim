// 30초 시네마틱 카메라: 컷 기반 샷 구성 (로봇 동작 강조) + 수동 오비트 카메라
// 기본 설정(seed 10, warmup 600 s)에서 시뮬레이션을 돌려 확인한 동작에 맞췄습니다:
//   0.8~9.0 s 로봇 10 이 뒤쪽 통로(x 9.1 m)에서 7.2 m 까지 등반 → 9.0~11.4 s 포크로 토트 인출 → 11.4~19.6 s 하강
//  13.8~20.3 s 로봇 2 가 작업대(x 9.1 m)에서 토트를 내밂
//  20.2 s~ 로봇 10 이 랙 밑을 지나 앞쪽으로 이동
// 경로 키는 blender/haipick_blender.py CAM_KEYS 를 바탕으로 하되, 시선을 해당 로봇에 고정하고 샷을 나눴습니다.
using UnityEngine;
using EquipFX;

namespace HaiPick
{
    public class HaiPickCamera : CinematicRig
    {
        public HaiPickView view;
        public int climber = 10, presenter = 2;

        protected override bool Ready => view != null && view.Sim != null;
        protected override bool Cinematic => view.cinematicCamera;

        Robot Rb(int i) { var L = view.Sim.robots; return L[((i % L.Count) + L.Count) % L.Count]; }
        Vector3 RobotAt(int i, float up) { var r = Rb(i); return V(r.px, r.py, r.pz + up); }

        protected override void BuildShots(ShotSequencer seq)
        {
            float ay = 5.63f;   // 뒤쪽 등반 통로 중심 y

            // 1) 전경 (타이틀 카드)
            seq.Add(Path(0f, 3.5f)
                .Add(0, V(17.5f, -7.0f, 8.0f), V(6.0f, 4.0f, 4.0f), 24)
                .Add(1, V(15.6f, -2.0f, 5.0f), V(8.6f, 5.0f, 2.8f), 26));

            // 2) 통로 안에서 레일을 타고 올라가는 로봇
            seq.Add(new PathShot { start = 3.5f, end = 8.6f, lookTarget = () => RobotAt(climber, 0.4f), lookLock = 0.6f,
                    title = "랙 등반", detail = "로봇이 랙 앞 레일을 물고 최고 12 m 까지 그대로 올라갑니다" }
                .Add(0, V(13.6f, 4.2f, 2.4f), V(9.1f, ay, 2.0f), 30)
                .Add(0.5f, V(12.6f, ay, 4.9f), V(9.075f, ay, 4.9f), 35)
                .Add(1, V(11.2f, ay - 0.08f, 7.7f), V(9.075f, ay + 0.27f, 7.55f), 35));

            // 3) 7.2 m: 포크로 토트를 꺼내는 순간 근접
            seq.Add(new PathShot { start = 8.6f, end = 12.6f, lookTarget = () => RobotAt(climber, 0.45f), lookLock = 0.5f,
                    title = "토트 인출", detail = "포크가 선반 안으로 뻗어 토트를 끌어와 로봇 위에 싣습니다" }
                .Add(0, V(11.0f, ay - 0.1f, 7.9f), V(9.075f, ay + 0.3f, 7.55f), 36)
                .Add(1, V(10.4f, ay - 0.18f, 8.3f), V(9.075f, ay + 0.47f, 7.5f), 40));

            // 4) 토트를 싣고 내려오는 로봇과 함께 하강 (통로 가운데에서)
            seq.Add(new FollowShot
            {
                start = 12.6f, end = 15.8f, target = () => RobotAt(climber, 0.45f),
                offset0 = new Vector3(2.6f, -0.8f, 0f), offset1 = new Vector3(3.0f, 0.6f, 0f),   // 아래에서 올려다보다 위로 (앞 샷과 다른 각도)
                lookOffset = Vector3.zero, lens0 = 26, lens1 = 28, smooth = 0.3f,
                constrain = p => new Vector3(p.x, Mathf.Max(p.y, 0.8f), ay),
                title = "하강", detail = "토트를 실은 채 내려와 작업대로 향합니다",
            });

            // 5) 작업대: 토트를 작업자에게 내미는 로봇
            seq.Add(new PathShot { start = 15.8f, end = 20.2f, lookTarget = () => RobotAt(presenter, 0.8f), lookLock = 0.4f,
                    title = "작업대 제시", detail = "로봇이 몸을 들어 올려 토트를 작업자 손 높이로 내밉니다" }
                .Add(0, V(11.6f, -3.6f, 2.6f), V(9.0f, -0.4f, 1.0f), 30)
                .Add(0.4f, V(10.9f, -3.3f, 2.0f), V(9.075f, -0.5f, 1.0f), 35)
                .Add(1, V(7.8f, -3.2f, 1.9f), V(9.1f, -0.4f, 0.95f), 38));

            // 6) 랙 밑: 낮은 공간을 가로질러 나오는 로봇
            seq.Add(new PathShot { start = 20.2f, end = 27.6f, lookTarget = () => RobotAt(climber, 0.35f), lookLock = 0.5f,
                    title = "랙 밑 통과", detail = "로봇이 랙 아래 공간을 가로질러 다른 통로로 빠르게 이동합니다" }
                .Add(0, V(7.975f, -1.5f, 0.48f), V(7.975f, 3.0f, 0.42f), 24)
                .Add(0.24f, V(7.975f, 3.3f, 0.45f), V(9.075f, 4.9f, 0.38f), 24)
                .Add(0.51f, V(8.2f, 3.49f, 0.5f), V(10.3f, 3.49f, 0.42f), 26)
                .Add(0.78f, V(8.6f, 3.49f, 0.55f), V(10.725f, 2.2f, 0.42f), 28)
                .Add(1, V(13.5f, 3.49f, 1.6f), V(9.0f, 3.49f, 1.0f), 26));

            // 7) 빠지며 전경
            seq.Add(Path(27.6f, 30f)
                .Add(0, V(15.5f, 0.5f, 3.6f), V(8.0f, 3.5f, 2.2f), 26)
                .Add(1, V(18.0f, -6.0f, 7.5f), V(6.0f, 3.5f, 3.5f), 24));

            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, 4f, (float)view.Sim.depthY / 2);
            yaw = 35; pitch = 25; dist = 22;
        }
    }
}
