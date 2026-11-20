// 30초 영상 녹화 (Unity 에디터 내장 MediaEncoder → H.264 MP4). 별도 패키지 불필요.
// 메뉴 애니메이션 > 스태커크레인 > Record 30s Movie 로 실행하면 플레이 모드에 들어가 고정 30fps로 렌더하고 자동 종료합니다.
// 화질:
//  - 슈퍼샘플링: 출력 해상도의 supersample 배(기본 2배)로 렌더한 뒤 줄여서 저장 → 계단 현상·얇은 선 반짝임이 크게 줄어듦
//    (FHD 출력 = 3840x2160 으로 렌더, 4K 출력 = 3840x2160 그대로 렌더)
//  - 녹화 동안만 URP 품질 올림: 그림자 거리 90 m, 그림자 맵 4096. 원본 PC_RPAsset 파일은 건드리지 않고 복제본을 잠시 씁니다.
//  - 실시간 렌더라 GPU 가 약해도 시간만 더 걸릴 뿐 결과는 같습니다 (고정 30fps 프레임 단위 녹화).
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Media;
#endif

namespace StackerCrane
{
    public class StackerCraneMovieCapture : MonoBehaviour
    {
        public bool recordOnPlay = false;
        public float seconds = 30f;
        public int fps = 30;
        public int width = 1920, height = 1080;
        [Tooltip("출력 해상도의 몇 배로 렌더한 뒤 줄일지 (1 = 끔, 2 = 4배 픽셀)")]
        [Range(1, 2)] public int supersample = 2;
        [Tooltip("녹화 동안 그림자 거리/해상도를 올림")]
        public bool highQualityShadows = true;
        [Tooltip("프로젝트 폴더 기준 경로")]
        public string outputPath = "Recordings/stackercrane_unity_30s.mp4";
        public bool hideHud = true;

        public static bool IsRecording { get; private set; }
        public static bool HideHudWhileRecording { get; private set; }
        public const string SessionKey = "StackerCrane.RecordOnPlay";
        public const string SessionHeightKey = "StackerCrane.RecordHeight";   // 1080 또는 2160 (메뉴에서 지정)

        RenderTexture rt, outRt; int frame, total;
        RenderPipelineAsset savedPipeline, tempPipeline; bool pipelineSwapped;

        IEnumerator Start()
        {
            bool go = recordOnPlay;
#if UNITY_EDITOR
            if (SessionState.GetBool(SessionKey, false))
            {
                go = true; SessionState.SetBool(SessionKey, false);
                int h = SessionState.GetInt(SessionHeightKey, 0);
                if (h == 2160) { width = 3840; height = 2160; supersample = 1; outputPath = "Recordings/stackercrane_unity_30s_4k.mp4"; }
                else if (h == 1080) { width = 1920; height = 1080; supersample = 2; outputPath = "Recordings/stackercrane_unity_30s.mp4"; }
            }
#endif
            if (!go) yield break;
#if UNITY_EDITOR
            var view = GetComponent<StackerCraneView>();
            // 안정화 프레임 동안 시뮬레이션을 멈춰 녹화 첫 프레임 = 워밍업 직후 (Blender 궤적·카메라·자막과 같은 시점)
            view.simSpeed = 0f;
            while (view.Cam == null) yield return null;
            var cam = view.Cam;
            view.cinematicCamera = true;
            Time.captureFramerate = fps;
            HideHudWhileRecording = hideHud; IsRecording = true;
            if (highQualityShadows) BoostPipeline();
            int ss = Mathf.Clamp(supersample, 1, 2);
            int rw = width * ss, rh = height * ss;
            rt = EquipFX.CameraFX.MakeRecordTarget(rw, rh);   // 32비트 깊이 (원거리 깜빡임 방지)
            rt.filterMode = FilterMode.Bilinear;
            if (ss > 1)
            {
                outRt = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = true, msaaSamples = 1 });
                outRt.Create();
            }
            else outRt = rt;
            cam.targetTexture = rt;
            // 반사 프로브/그림자 안정화용 프레임
            for (int i = 0; i < 5; i++) yield return null;
            var cc = cam.GetComponent<StackerCraneCamera>(); if (cc != null) cc.Restart();
            view.simSpeed = 1f;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var attr = new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps), width = (uint)width, height = (uint)height,
                includeAlpha = false, bitRateMode = UnityEditor.VideoBitrateMode.High
            };
            total = Mathf.RoundToInt(seconds * fps);
            Debug.Log($"[StackerCrane] 녹화 시작: {width}x{height} (렌더 {rw}x{rh}), {fps}fps, {seconds:0}s → {path}");
            using (var enc = new MediaEncoder(path, attr))
            {
                for (frame = 0; frame < total; frame++)
                {
                    yield return new WaitForEndOfFrame();
                    // 2배 → 1배 쌍선형 축소 = 정확히 2x2 픽셀 평균 (박스 필터)
                    if (outRt != rt) Graphics.Blit(rt, outRt);
                    var prev = RenderTexture.active; RenderTexture.active = outRt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); tex.Apply(false);
                    RenderTexture.active = prev;
                    enc.AddFrame(tex);
                }
            }
            cam.targetTexture = null; IsRecording = false; Time.captureFramerate = 0;
            RestorePipeline();
            Debug.Log($"[StackerCrane] 영상 저장 완료: {path}");
            EditorUtility.RevealInFinder(path);
            EditorApplication.isPlaying = false;
#endif
        }

#if UNITY_EDITOR
        // 현재 품질 단계의 URP 에셋을 복제해 그림자 설정만 올린 뒤 잠시 교체 (원본 파일은 수정하지 않음)
        void BoostPipeline()
        {
            var src = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
            if (src == null) return;
            tempPipeline = Instantiate(src); tempPipeline.name = src.name + " (Recording)";
            var so = new SerializedObject(tempPipeline);
            SetProp(so, "m_ShadowDistance", 90f);
            SetProp(so, "m_MainLightShadowmapResolution", 4096);
            SetProp(so, "m_AdditionalLightsShadowmapResolution", 4096);
            SetProp(so, "m_ShadowCascadeCount", 4);
            so.ApplyModifiedPropertiesWithoutUndo();
            savedPipeline = QualitySettings.renderPipeline;
            QualitySettings.renderPipeline = tempPipeline; pipelineSwapped = true;
        }

        static void SetProp(SerializedObject so, string prop, float v)
        {
            var p = so.FindProperty(prop); if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Float) p.floatValue = v; else p.intValue = (int)v;
        }

        void RestorePipeline()
        {
            if (!pipelineSwapped) return;
            QualitySettings.renderPipeline = savedPipeline; pipelineSwapped = false;
            if (tempPipeline != null) Destroy(tempPipeline);
        }
#else
        void RestorePipeline() { }
#endif

        void OnDestroy()
        {
            // 녹화 도중 플레이를 멈춰도 원래 URP 에셋으로 되돌림
            RestorePipeline();
            IsRecording = false;
        }

        void OnGUI()
        {
            if (!IsRecording || outRt == null) return;
            // 녹화 중 미리보기 + 진행률
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), outRt, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(12, Screen.height - 30, 500, 24), $"REC  {width}x{height}  {frame}/{total} frames  ({frame / (float)fps:0.0}s / {seconds:0}s)");
        }
    }
}
