// 30초 시네마틱 카메라: 공정 단계별 컷 + 수동 오비트 카메라
// 장면 시계(view.T, 5 s 주기 × 6)에 맞춰 blender/caseline_blender.py CAM_KEYS 의 동선을 공정 단계별 샷으로 나누고,
// 단계마다 렌즈를 조금 더 당겨 기계 동작이 화면을 채우게 했습니다. 충전 단계만 높은 각도로 컷하고 나머지는 이어지는 동선입니다.
// (기계 동작 로직은 바꾸지 않음)
using UnityEngine;
using EquipFX;

namespace CaseLine
{
    public class CaseLineCamera : CinematicRig
    {
        public CaseLineView view;

        protected override bool Ready => view != null;
        protected override bool Cinematic => view.cinematicCamera;
        // 장면 상태는 5 s 주기라 30 s 마다 같은 장면 → 카메라도 장면 시계로 30 s 반복
        protected override float? ExternalTime => (float)view.T;

        protected override void BuildShots(ShotSequencer seq)
        {
            // 1) 라인 전경 (타이틀 카드)
            seq.Add(Path(0f, 4.0f)
                .Add(0, V(-3.6f, -4.4f, 3.0f), V(2.6f, 0.6f, 0.7f), 24)
                .Add(1, V(-1.9f, -2.8f, 2.3f), V(-0.1f, 0.4f, 0.9f), 28));

            // 2) 제함기: 블랭크 펼침 → 바닥 날개 접힘 → 푸셔
            seq.Add(Path(4.0f, 9.0f, "제함", "흡착 암이 접힌 블랭크를 펼치고 바닥 날개를 접어 박스를 세웁니다")
                .Add(0, V(-1.9f, -2.8f, 2.3f), V(-0.1f, 0.4f, 0.9f), 28)
                .Add(0.5f, V(-0.6f, -1.9f, 1.95f), V(0.0f, 0.2f, 0.95f), 32)
                .Add(1, V(0.6f, -1.9f, 1.85f), V(0.6f, 0.0f, 0.85f), 34));

            // 3) 충전 슈트: 조금 높은 곳에서 박스 안을 내려다봄
            seq.Add(Path(9.0f, 15.0f, "충전", "컨베이어로 옮겨진 박스에 슈트를 통해 제품이 채워집니다")
                .Add(0, V(1.0f, -2.5f, 2.7f), V(1.4f, 0.2f, 0.9f), 30)
                .Add(0.6f, V(1.6f, -2.2f, 2.45f), V(1.9f, 0.3f, 0.95f), 34)
                .Add(1, V(2.3f, -1.9f, 2.2f), V(2.5f, 0.0f, 0.95f), 34));

            // 4) 상부 날개 접기
            seq.Add(Path(15.0f, 21.0f, "상부 날개 접기", "가이드 바가 앞·뒤·옆 날개를 차례로 눌러 접습니다")
                .Add(0, V(2.3f, -1.9f, 2.2f), V(2.5f, 0.0f, 0.95f), 34)
                .Add(0.5f, V(2.9f, -1.7f, 1.85f), V(3.2f, 0.0f, 1.0f), 32)
                .Add(1, V(3.3f, -1.5f, 2.1f), V(4.2f, 0.0f, 1.0f), 32));

            // 5) 봉함기: 테이프 헤드 → 배출
            seq.Add(Path(21.0f, 26.5f, "봉함", "봉함기 테이프 헤드가 박스 윗면을 따라 테이프를 붙이고 잘라냅니다")
                .Add(0, V(3.3f, -1.5f, 2.1f), V(4.2f, 0.0f, 1.0f), 32)
                .Add(0.45f, V(5.4f, -1.3f, 1.95f), V(4.6f, 0.0f, 1.0f), 34)
                .Add(1, V(6.6f, -2.6f, 2.0f), V(5.4f, 0.2f, 0.95f), 30));

            // 6) 빠지며 라인 전경
            seq.Add(Path(26.5f, 30f)
                .Add(0, V(6.6f, -2.6f, 2.0f), V(5.4f, 0.2f, 0.95f), 30)
                .Add(1, V(8.0f, -4.4f, 2.9f), V(4.2f, 0.5f, 0.9f), 24));

            orbitCenter = new Vector3(4.2f, 0.9f, 0.3f);
            yaw = 20; pitch = 28; dist = 7; maxDist = 25;
        }
    }
}
