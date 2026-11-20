// 메뉴 애니메이션 > 녹화 화질 > ... : 모든 설비의 "Record 30s Movie" 가 함께 쓰는 해상도·안티에일리어싱 프리셋 (EquipFX.RecordQuality)
using UnityEditor;
using EquipFX;

namespace EquipFXEditor
{
    static class RecordQualityMenu
    {
        const string Root = "애니메이션/녹화 화질/";
        const string FHD = Root + "1080p 표준 (빠름, MSAA 4x)";
        const string FHDSuper = Root + "1080p 고화질 (2x 슈퍼샘플링, 기본)";
        const string UHD = Root + "4K UHD 3840x2160 (MSAA 4x, 약 4배 느림)";

        [MenuItem(FHD, priority = 1000)] static void SetFHD() => Set(RecordPreset.FHD);
        [MenuItem(FHDSuper, priority = 1001)] static void SetFHDSuper() => Set(RecordPreset.FHDSuper);
        [MenuItem(UHD, priority = 1002)] static void SetUHD() => Set(RecordPreset.UHD4K);

        [MenuItem(FHD, true)] static bool CheckFHD() => Check();
        [MenuItem(FHDSuper, true)] static bool CheckFHDSuper() => Check();
        [MenuItem(UHD, true)] static bool CheckUHD() => Check();

        static void Set(RecordPreset p)
        {
            RecordQuality.Preset = p;
            UnityEngine.Debug.Log($"[녹화 화질] {RecordQuality.Label(p)} 로 설정. 다음 'Record 30s Movie' 부터 적용됩니다.");
        }

        static bool Check()
        {
            var p = RecordQuality.Preset;
            Menu.SetChecked(FHD, p == RecordPreset.FHD);
            Menu.SetChecked(FHDSuper, p == RecordPreset.FHDSuper);
            Menu.SetChecked(UHD, p == RecordPreset.UHD4K);
            return true;
        }
    }
}
