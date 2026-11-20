// 30초 영상 녹화 (Unity 에디터 내장 MediaEncoder → H.264 MP4). 별도 패키지 불필요.
// 메뉴 애니메이션 > AutoStore > Record 30s Movie 로 실행하면 플레이 모드에 들어가 고정 30fps로 렌더하고 자동 종료합니다.
using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Media;
#endif

namespace AutoStore
{
    public class MovieCapture : MonoBehaviour
    {
        public bool recordOnPlay = false;
        public float seconds = 30f;
        public int fps = 30;
        public int width = 1920, height = 1080;
        [Tooltip("프로젝트 폴더 기준 경로")]
        public string outputPath = "Recordings/autostore_unity_30s.mp4";
        public bool hideHud = true;

        public static bool IsRecording { get; private set; }
        public static bool HideHudWhileRecording { get; private set; }
        public const string SessionKey = "AutoStore.RecordOnPlay";

        RenderTexture rt; int frame, total;

        IEnumerator Start()
        {
            bool go = recordOnPlay;
#if UNITY_EDITOR
            if (SessionState.GetBool(SessionKey, false)) { go = true; SessionState.SetBool(SessionKey, false); }
#endif
            if (!go) yield break;
#if UNITY_EDITOR
            var view = GetComponent<AutoStoreView>();
            while (view.Cam == null) yield return null;
            var cam = view.Cam;
            view.cinematicCamera = true; view.simSpeed = 1f;
            Time.captureFramerate = fps;
            HideHudWhileRecording = hideHud; IsRecording = true;
            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            rt.Create();
            cam.targetTexture = rt;
            // 반사 프로브/그림자 안정화용 프레임
            for (int i = 0; i < 5; i++) yield return null;
            var cc = cam.GetComponent<CinematicCamera>(); if (cc != null) cc.Restart();
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputPath));
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
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); tex.Apply(false);
                    RenderTexture.active = prev;
                    enc.AddFrame(tex);
                }
            }
            cam.targetTexture = null; IsRecording = false; Time.captureFramerate = 0;
            Debug.Log($"[AutoStore] 영상 저장 완료: {path}");
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
