// 30초 시네마틱 카메라: 컷 기반 샷 구성 (로봇 동작 강조) + 수동 오비트 카메라
// 샷 시점은 기본 설정(warmup 600 s)에서 시뮬레이션을 돌려 찾은 실제 동작에 맞췄습니다:
//   5~11.5 s 로봇 27 이 앞쪽 투입 스테이션에서 받은 소포를 싣고 달려 왼쪽 슈트(1.05, 2.45)에 떨어뜨림 (10.9 s)
//  11.5~17.6 s 데크 아래 점검 통로: 소포가 쌓이는 자루 사이를 지나감
//  17.6~22.6 s 위에서 본 30대 군집 주행
//  22.6~27.4 s 로봇 4 가 슈트(8.05, 6.65)에서 트레이를 기울여 투하 (25.0 s)
// 설정을 바꿔 다른 동작이 나오더라도 추적 샷은 해당 로봇을 계속 따라가므로 화면은 유지됩니다.
using UnityEngine;
using EquipFX;

namespace Libiao
{
    public class LibiaoCamera : CinematicRig
    {
        public LibiaoView view;

        protected override bool Ready => view != null && view.Sim != null;
        protected override bool Cinematic => view.cinematicCamera;

        SorterRobot Rb(int i) { var L = view.Sim.robots; return L[((i % L.Count) + L.Count) % L.Count]; }
        Vector3 RobotAt(int i, float up) { var r = Rb(i); return V(r.x, r.y, view.config.deckZ + up); }

        protected override void BuildShots(ShotSequencer seq)
        {
            var c = view.config; float Z = c.deckZ, cx = c.nx * c.cell / 2, cy = c.ny * c.cell / 2;

            // 1) 전경 (타이틀 카드)
            seq.Add(Path(0f, 4.8f)
                .Add(0, V(15.6f, -9.0f, 7.6f), V(6.6f, 4.5f, 1.8f), 24)
                .Add(1, V(12.2f, -6.6f, 5.4f), V(6.2f, 2.6f, 2.3f), 26));

            // 2) 소포를 실은 로봇을 바짝 따라가 슈트에 떨어뜨리는 순간까지
            seq.Add(new FollowShot
            {
                start = 4.8f, end = 11.5f, target = () => RobotAt(27, 0.32f),
                offset0 = new Vector3(-1.4f, 0.9f, -1.0f), offset1 = new Vector3(0.35f, 0.75f, -1.3f),
                lookOffset = new Vector3(0, -0.05f, 0), lens0 = 30, lens1 = 38, smooth = 0.3f,
                title = "투입 → 슈트 주행", detail = "투입대에서 소포를 받은 로봇이 목적지 슈트까지 최단 경로로 달립니다",
            });

            // 3) 데크 아래 점검 통로: 자루 사이를 지나감
            seq.Add(Path(11.5f, 17.6f, "자루 집하", $"슈트 {view.Sim.chutes.Count}개 아래 자루에 목적지별로 소포가 쌓입니다")
                .Add(0, V(-1.4f, 4.55f, 1.6f), V(3.0f, 4.0f, 1.4f), 22)
                .Add(0.5f, V(3.0f, 4.55f, 1.6f), V(8.0f, 3.9f, 1.3f), 22)
                .Add(1, V(8.0f, 4.55f, 1.65f), V(12.5f, 5.0f, 1.9f), 24));

            // 4) 위에서 본 군집 주행
            seq.Add(Path(17.6f, 22.6f, "군집 주행", $"로봇 {c.robots}대가 데크 위 통로를 동시에 오가며 분류합니다")
                .Add(0, V(cx - 2.6f, cy - 4.2f, Z + 6.4f), V(cx, cy, Z), 18)       // 천장(10 m) 조명 아래
                .Add(1, V(cx + 2.4f, cy - 3.4f, Z + 6.0f), V(cx + 0.3f, cy + 0.3f, Z), 18));

            // 5) 투하 근접: 다가오는 로봇 앞에서 트레이가 기울어지는 순간
            seq.Add(new FollowShot
            {
                start = 22.6f, end = 27.4f, target = () => RobotAt(4, 0.30f),
                offset0 = new Vector3(-1.3f, 0.8f, -1.1f), offset1 = new Vector3(-0.75f, 0.62f, -1.15f),
                lookOffset = new Vector3(0, -0.04f, 0), lens0 = 35, lens1 = 42, smooth = 0.3f,
                title = "틸트 트레이 투하", detail = "슈트 옆에 멈춘 로봇이 트레이를 기울여 소포를 아래 자루로 떨어뜨립니다",
            });

            // 6) 빠지며 뒤쪽 전경
            seq.Add(Path(27.4f, 30f)
                .Add(0, V(12.6f, 11.5f, 5.0f), V(7.0f, 5.0f, 2.4f), 26)
                .Add(1, V(-3.5f, 16.0f, 7.8f), V(6.6f, 4.5f, 1.8f), 24));

            orbitCenter = new Vector3(c.nx * c.cell / 2, c.deckZ * 0.6f, c.ny * c.cell / 2);
            yaw = 35; pitch = 30; dist = 16;
        }
    }
}
