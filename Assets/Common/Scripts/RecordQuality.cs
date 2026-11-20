// 모든 설비 장면의 30초 영상 녹화가 함께 쓰는 고화질 설정.
//  - 해상도 프리셋: 메뉴 애니메이션 > 녹화 화질 > ... 에서 고르고, 에디터에 저장됩니다 (EditorPrefs).
//      1080p 표준   : 1920×1080, MSAA 4x                       (가장 빠름)
//      1080p 고화질 : 3840×2160 으로 렌더 후 1920×1080 으로 축소 (2x 슈퍼샘플링) + MSAA 2x  ← 기본값
//      4K UHD      : 3840×2160, MSAA 4x                        (파일 이름에 _4k, 녹화 시간·용량 약 4배)
//  - 녹화하는 동안만 그림자 맵 4096, 이방성 필터링, LOD 바이어스를 올리고 끝나면 (플레이 모드 중단 포함) 원래대로 돌립니다.
// 사용: var rs = EquipFX.RecordSession.Begin(cam);  cam.targetTexture = rs.CameraTarget;
//       매 프레임 WaitForEndOfFrame 뒤 rs.Resolve() → rs.Frame (출력 해상도) 에 자막 합성·ReadPixels.  끝나면 rs.End().
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace EquipFX
{
    public enum RecordPreset { FHD = 0, FHDSuper = 1, UHD4K = 2 }

    public static class RecordQuality
    {
        public const string PrefKey = "EquipFX.RecordPreset";
        public const RecordPreset Default = RecordPreset.FHDSuper;

        public static RecordPreset Preset
        {
#if UNITY_EDITOR
            get => (RecordPreset)EditorPrefs.GetInt(PrefKey, (int)Default);
            set => EditorPrefs.SetInt(PrefKey, (int)value);
#else
            get => Default;
            set { }
#endif
        }

        public static Vector2Int OutputSize => Preset == RecordPreset.UHD4K ? new Vector2Int(3840, 2160) : new Vector2Int(1920, 1080);
        public static int SuperSample => Preset == RecordPreset.FHDSuper ? 2 : 1;
        public static int Msaa => Preset == RecordPreset.FHDSuper ? 2 : 4;

        public static string Label(RecordPreset p) => p switch
        {
            RecordPreset.FHD => "1080p 표준",
            RecordPreset.FHDSuper => "1080p 고화질 (2x 슈퍼샘플링)",
            _ => "4K UHD",
        };

        // 4K 는 1080p 영상을 덮어쓰지 않도록 파일 이름에 _4k 를 붙입니다.
        public static string DecoratePath(string path)
        {
            if (Preset != RecordPreset.UHD4K) return path;
            var dir = Path.GetDirectoryName(path) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_4k" + Path.GetExtension(path));
        }
    }

    public sealed class RecordSession
    {
        public int Width { get; private set; }        // 출력(영상) 해상도
        public int Height { get; private set; }
        public RenderTexture CameraTarget { get; private set; }   // 카메라가 그리는 곳 (슈퍼샘플·MSAA)
        public RenderTexture Frame { get; private set; }          // 출력 해상도로 해상된 프레임
        public RecordPreset Preset { get; private set; }

        Camera cam;
        bool restored;
        // 되돌릴 값
        UniversalRenderPipelineAsset urp;
        int oldMsaa, oldShadowRes = -1; float oldRenderScale;
        AnisotropicFiltering oldAniso; float oldLodBias;
        bool oldAllowMsaa;

        public static RecordSession Begin(Camera cam)
        {
            var s = new RecordSession { cam = cam, Preset = RecordQuality.Preset };
            var size = RecordQuality.OutputSize; int ss = RecordQuality.SuperSample, msaa = RecordQuality.Msaa;
            s.Width = size.x; s.Height = size.y;

            s.CameraTarget = CameraFX.MakeRecordTarget(size.x * ss, size.y * ss, msaa);
            s.CameraTarget.filterMode = FilterMode.Bilinear;   // 정확히 2배 축소 → 이중선형 1회 = 2×2 평균
            s.Frame = CameraFX.MakeRecordTarget(size.x, size.y);
            s.Frame.filterMode = FilterMode.Bilinear;

            s.Apply(msaa);
            Application.quitting += s.Restore;   // 녹화 도중 플레이 모드를 멈춰도 설정 복원
            Debug.Log($"[녹화 화질] {RecordQuality.Label(s.Preset)}: 출력 {size.x}×{size.y}, 렌더 {size.x * ss}×{size.y * ss}, MSAA {msaa}x, 그림자 맵 4096");
            return s;
        }

        void Apply(int msaa)
        {
            oldAllowMsaa = cam.allowMSAA; cam.allowMSAA = true;
            oldAniso = QualitySettings.anisotropicFiltering; QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            oldLodBias = QualitySettings.lodBias; QualitySettings.lodBias = Mathf.Max(oldLodBias, 2f);

            urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) return;
            // 렌더 텍스처 대상 카메라의 MSAA 샘플 수는 대상 텍스처를 따르지만, 파이프라인 에셋 쪽 MSAA 도 켜져 있어야 적용됩니다.
            oldMsaa = urp.msaaSampleCount; urp.msaaSampleCount = msaa;
            // 슈퍼샘플링은 직접 큰 텍스처로 하므로 렌더 스케일은 1 로 고정
            oldRenderScale = urp.renderScale; urp.renderScale = 1f;
            oldShadowRes = GetShadowRes(urp);
            if (oldShadowRes > 0 && oldShadowRes < 4096) SetShadowRes(urp, 4096);
        }

        // 카메라 출력(슈퍼샘플·MSAA)을 출력 해상도 Frame 으로 해상·축소. WaitForEndOfFrame 뒤, 자막 합성·ReadPixels 전에 호출.
        public RenderTexture Resolve()
        {
            Graphics.Blit(CameraTarget, Frame);
            return Frame;
        }

        public void End()
        {
            if (cam != null && cam.targetTexture == CameraTarget) cam.targetTexture = null;
            Restore();
            Application.quitting -= Restore;
        }

        void Restore()
        {
            if (restored) return; restored = true;
            if (cam != null) cam.allowMSAA = oldAllowMsaa;
            QualitySettings.anisotropicFiltering = oldAniso; QualitySettings.lodBias = oldLodBias;
            if (urp != null)
            {
                urp.msaaSampleCount = oldMsaa; urp.renderScale = oldRenderScale;
                if (oldShadowRes > 0) SetShadowRes(urp, oldShadowRes);
            }
        }

        // URP 버전에 따라 mainLightShadowmapResolution 에 setter 가 없을 수 있어 리플렉션으로 처리
        static int GetShadowRes(UniversalRenderPipelineAsset a)
        {
            try { return a.mainLightShadowmapResolution; } catch (Exception) { return -1; }
        }

        static void SetShadowRes(UniversalRenderPipelineAsset a, int res)
        {
            try
            {
                var t = typeof(UniversalRenderPipelineAsset);
                var p = t.GetProperty("mainLightShadowmapResolution", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(a, res); return; }
                var f = t.GetField("m_MainLightShadowmapResolution", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) f.SetValue(a, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, res) : (object)res);
            }
            catch (Exception e) { Debug.LogWarning($"[녹화 화질] 그림자 해상도 변경 실패: {e.Message}"); }
        }
    }
}
