// 30초 시네마틱 카메라: 컷 기반 샷 구성 (로봇 동작 강조) + 수동 오비트 카메라
// 기본 설정(seed 3, warmup 600 s)에서 시뮬레이션을 돌려 확인한 로봇 10 의 동작에 맞췄습니다:
//  11~12.7 s 왼쪽 리프트(셀 -1, 3)로 진입 → 12.7~15.2 s 리프트를 타고 2층 → 4층 (z 0.8 → 2.6 m)
//  15.3~25.6 s 4층 통로를 따라 오른쪽으로 주행 → 25.6~26.5 s (9.4, 1.2) 에서 빈을 들어 올림
// 경로 키는 blender/rapyuta_blender.py CAM_KEYS 를 바탕으로 하되, 바라보는 점을 로봇 10 에 고정해 동작이 화면 가운데 오게 했습니다.
using UnityEngine;
using EquipFX;

namespace Rapyuta
{
    public class RapyutaCamera : CinematicRig
    {
        public RapyutaView view;
        public int heroRobot = 10;

        protected override bool Ready => view != null && view.Sim != null;
        protected override bool Cinematic => view.cinematicCamera;

        Robot Rb(int i) { var L = view.Sim.robots; return L[((i % L.Count) + L.Count) % L.Count]; }
        Vector3 Hero(float up = 0.06f) { var r = Rb(heroRobot); return V(r.px, r.py, r.pz + up); }

        protected override void BuildShots(ShotSequencer seq)
        {
            // 1) 전경 (타이틀 카드)
            seq.Add(Path(0f, 4.2f)
                .Add(0, V(14.5f, -7.0f, 6.2f), V(5.6f, 3.0f, 1.8f), 24)
                .Add(1, V(10.6f, -5.2f, 3.6f), V(5.2f, 0.8f, 1.3f), 26));

            // 2) 피킹 스테이션 근접
            seq.Add(Path(4.2f, 10.8f, "피킹 스테이션", "로봇이 빈을 작업자 앞 스테이션까지 가져오고, 프로젝터가 꺼낼 칸을 비춥니다")
                .Add(0, V(8.8f, -4.4f, 2.6f), V(5.0f, -0.2f, 1.1f), 28)
                .Add(1, V(4.6f, -3.4f, 2.2f), V(3.4f, -0.3f, 1.05f), 34));

            // 3) 리프트: 바깥 열린 면에서 로봇과 같은 높이로 함께 올라감
            seq.Add(new FollowShot
            {
                start = 10.8f, end = 15.6f, target = () => Hero(0.1f),
                offset0 = new Vector3(-3.0f, 0.55f, -2.5f), offset1 = new Vector3(-2.7f, 0.65f, -1.2f),
                lookOffset = Vector3.zero, lens0 = 30, lens1 = 34, smooth = 0.3f,
                constrain = p => new Vector3(Mathf.Min(p.x, -1.6f), Mathf.Max(p.y, 0.5f), p.z),
                title = "층간 리프트", detail = "로봇이 리프트를 타고 다른 층으로 올라갑니다",
            });

            // 4) 4층 통로 안: 층 사이 낮은 공간을 로봇 뒤에서 따라감 (경로는 기둥을 피하도록 고정, 시선은 로봇)
            seq.Add(new PathShot { start = 15.6f, end = 24.4f, lookTarget = () => Hero(), lookLock = 0.55f,
                    title = "층 내부 주행", detail = "높이 80 mm 로봇이 빈 밑 통로를 따라 목적지 칸까지 달립니다" }
                .Add(0, V(-3.0f, 2.3f, 3.2f), V(-0.2f, 3.25f, 2.8f), 28)
                .Add(0.11f, V(-0.9f, 3.25f, 2.95f), V(1.2f, 3.25f, 2.80f), 24)
                .Add(0.34f, V(1.2f, 3.25f, 2.92f), V(3.5f, 3.25f, 2.78f), 24)
                .Add(0.57f, V(5.0f, 3.25f, 2.92f), V(7.8f, 3.25f, 2.78f), 24)
                .Add(0.80f, V(7.8f, 3.25f, 2.95f), V(11.0f, 3.1f, 2.8f), 24)
                .Add(1f, V(11.4f, 3.25f, 2.98f), V(11.8f, 2.0f, 2.85f), 24));

            // 5) 빈을 들어 올리는 순간: 오른쪽 바깥에서 근접
            seq.Add(new PathShot { start = 24.4f, end = 28.0f, lookTarget = () => Hero(0.2f), lookLock = 0.6f,
                    title = "빈 인출", detail = "빈 아래로 들어간 로봇이 상판을 들어 빈을 싣고 출발합니다" }
                .Add(0, V(13.2f, 1.9f, 3.05f), V(10.2f, 1.35f, 2.8f), 32)
                .Add(1, V(13.0f, 1.0f, 3.1f), V(10.3f, 1.4f, 2.85f), 36));

            // 6) 빠지며 전경
            seq.Add(Path(28.0f, 30f)
                .Add(0, V(14.2f, -1.2f, 4.2f), V(7.5f, 2.5f, 2.3f), 28)
                .Add(1, V(16.5f, -4.0f, 6.0f), V(6.5f, 3.0f, 2.2f), 24));

            var c = view.config;
            orbitCenter = new Vector3(c.nx * (float)c.cellX / 2, (float)(c.baseZ + c.nf * c.floorH) * 0.5f, c.ny * (float)c.cellY / 2);
            yaw = 35; pitch = 30; dist = 15;
        }
    }
}
