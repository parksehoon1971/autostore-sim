// 30초 영상 녹화 (Unity 에디터 내장 MediaEncoder → H.264 MP4). 별도 패키지 불필요.
// 메뉴 애니메이션 > Skypod > Record 30s Movie 로 실행하면 플레이 모드에 들어가 고정 30fps로 렌더하고 자동 종료합니다.
using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Media;
#endif

namespace Skypod
{
    public class SkypodMovieCapture : MonoBehaviour
    {
        public bool recordOnPlay = false;
        public float seconds = 30f;
        public int fps = 30;
        public int width = 1920, height = 1080;
        [Tooltip("프로젝트 폴더 기준 경로")]
        public string outputPath = "Recordings/skypod_unity_30s.mp4";
        public bool hideHud = true;

        public static bool IsRecording { get; private set; }
        public static bool HideHudWhileRecording { get; private set; }
        public const string SessionKey = "Skypod.RecordOnPlay";

        RenderTexture rt; int frame, total;

        IEnumerator Start()
        {
            bool go = recordOnPlay;
#if UNITY_EDITOR
            if (SessionState.GetBool(SessionKey, false)) { go = true; SessionState.SetBool(SessionKey, false); }
#endif
            if (!go) yield break;
#if UNITY_EDITOR
            var view = GetComponent<SkypodView>();
            // 안정화 프레임 동안 시뮬레이션을 멈춰 녹화 첫 프레임 = 워밍업 직후 (Blender 궤적과 같은 시점)
            view.simSpeed = 0f;
            while (view.Cam == null) yield return null;
            var cam = view.Cam;
            view.cinematicCamera = true;
            Time.captureFramerate = fps;
            HideHudWhileRecording = hideHud; IsRecording = true;
            // 공용 녹화 화질 (메뉴 애니메이션 > 녹화 화질): 해상도·MSAA·슈퍼샘플링·그림자. 32비트 깊이 유지 (원거리 깜빡임 방지)
            var rs = EquipFX.RecordSession.Begin(cam);
            width = rs.Width; height = rs.Height; rt = rs.Frame;
            var caption = GetComponent<EquipFX.CaptionPresenter>();
            EquipFX.CaptionPresenter.Recording = true;
            cam.targetTexture = rs.CameraTarget;
            // 반사 프로브/그림자 안정화용 프레임
            for (int i = 0; i < 5; i++) yield return null;
            var cc = cam.GetComponent<SkypodCamera>(); if (cc != null) cc.Restart();
            view.simSpeed = 1f;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", EquipFX.RecordQuality.DecoratePath(outputPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var attr = new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps), width = (uint)width, height = (uint)height,
                includeAlpha = false, bitRateMode = UnityEditor.VideoBitrateMode.High
            };
            total = Mathf.RoundToInt(seconds * fps);
            using (var enc = new MediaEncoder(path, attr))
            {
                for (frame = 0; frame < total; frame++)
                {
                    yield return new WaitForEndOfFrame();
                    rs.Resolve();   // 슈퍼샘플·MSAA → 출력 해상도
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    if (caption != null) caption.DrawInto(rt);   // 설비명·샷 자막을 프레임에 합성 (후처리 뒤)
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); tex.Apply(false);
                    RenderTexture.active = prev;
                    enc.AddFrame(tex);
                }
            }
            rs.End(); IsRecording = false; EquipFX.CaptionPresenter.Recording = false; Time.captureFramerate = 0;
            Debug.Log($"[Skypod] 영상 저장 완료: {path}");
            EditorUtility.RevealInFinder(path);
            EditorApplication.isPlaying = false;
#endif
        }

        void OnGUI()
        {
            if (!IsRecording || rt == null) return;
            // 녹화 중 미리보기 + 진행률
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rt, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(12, Screen.height - 30, 400, 24), $"REC  {frame}/{total} frames  ({frame / (float)fps:0.0}s / {seconds:0}s)");
        }
    }
}
