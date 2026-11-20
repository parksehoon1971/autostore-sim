// 30초 영상 녹화 (Unity 에디터 내장 MediaEncoder → H.264 MP4). 별도 패키지 불필요.
// 메뉴 애니메이션 > 셔틀랙 > Record 30s Movie 로 실행하면 플레이 모드에 들어가 고정 30fps로 렌더하고 자동 종료합니다.
// 장비 이름·단계 자막은 프레임에 직접 합성합니다 (ShuttleCaption).
using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Media;
#endif

namespace ShuttleRack
{
    public class ShuttleMovieCapture : MonoBehaviour
    {
        public bool recordOnPlay = false;
        public float seconds = 30f;
        public int fps = 30;
        public int width = 1920, height = 1080;
        [Tooltip("프로젝트 폴더 기준 경로")]
        public string outputPath = "Recordings/shuttlerack_unity_30s.mp4";
        public bool captions = true;

        public static bool IsRecording { get; private set; }
        public const string SessionKey = "ShuttleRack.RecordOnPlay";

        RenderTexture rt; Texture2D tex; int frame, total;

        IEnumerator Start()
        {
            bool go = recordOnPlay;
#if UNITY_EDITOR
            if (SessionState.GetBool(SessionKey, false)) { go = true; SessionState.SetBool(SessionKey, false); }
#endif
            if (!go) yield break;
#if UNITY_EDITOR
            var view = GetComponent<ShuttleView>();
            // 안정화 프레임 동안 시뮬레이션을 멈춰 녹화 첫 프레임 = 워밍업 직후 (Blender 궤적·카메라와 같은 시점)
            view.simSpeed = 0f;
            while (view.Cam == null) yield return null;
            var cam = view.Cam;
            view.cinematicCamera = true;
            Time.captureFramerate = fps;
            IsRecording = true;
            rt = EquipFX.CameraFX.MakeRecordTarget(width, height);   // 32비트 깊이 (원거리 깜빡임 방지)
            cam.targetTexture = rt;
            ShuttleCaption cap = captions ? BuildCaptions() : null;
            for (int i = 0; i < 5; i++) yield return null;
            var cc = cam.GetComponent<ShuttleCamera>(); if (cc != null) cc.Restart();
            view.simSpeed = 1f;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
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
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    RenderTexture.active = prev;
                    if (cap != null)
                    {
                        var px = tex.GetPixels32();
                        cap.Composite(px, (frame + 1) / (float)fps);
                        tex.SetPixels32(px);
                    }
                    tex.Apply(false);
                    enc.AddFrame(tex);
                }
            }
            cam.targetTexture = null; IsRecording = false; Time.captureFramerate = 0;
            Debug.Log($"[ShuttleRack] 영상 저장 완료: {path}");
            EditorUtility.RevealInFinder(path);
            EditorApplication.isPlaying = false;
#endif
        }

        // 자막 내용과 시점 (단계 시점은 ShuttleCamera.json 의 주인공 토트 기준)
        ShuttleCaption BuildCaptions()
        {
            var view = GetComponent<ShuttleView>(); var c = view.config;
            var cap = new ShuttleCaption(width, height);
            var L = new ShuttleCaption.Line[]
            {
                new ShuttleCaption.Line { text = "AUTOMATED STORAGE  ·  GOODS-TO-PERSON", size = 20, bold = true, shade = 0.75f, y = 0 },
                new ShuttleCaption.Line { text = "셔틀랙  Shuttle AS/RS", size = 54, bold = true, shade = 1f, y = 30 },
                new ShuttleCaption.Line { text = $"통로 {c.aisles}개 · {c.levels}단 · 셔틀 {view.Sim.shuttles.Count}대 · 리프트 {view.Sim.lifts.Count}대 · 토트 {view.Sim.totes:N0}개", size = 24, bold = false, shade = 0.88f, y = 100 },
            };
            cap.Add(L, 900, 136, 0.055f, 0.085f, 0.6f, 7.0f, true);
            var tr = ShuttleCamera.LoadTrack();
            float[] t = tr != null && tr.times != null && tr.times.Length >= 6 ? tr.times : new[] { 7.6f, 11.9f, 14.9f, 18.7f, 22.5f, 27.0f };
            string[] steps =
            {
                "① 셔틀이 토트를 꺼냄  ·  최고 4 m/s",
                "② 출고 버퍼 → 리프트",
                "③ 리프트가 1층 컨베이어로 내림",
                "④ 컨베이어로 피킹 스테이션까지",
                "⑤ 작업자 피킹 후 다시 입고",
            };
            float[] s0 = { t[0] - 0.2f, t[1] - 0.3f, t[2], t[3] + 0.3f, t[4] - 0.3f };
            float[] s1 = { t[1] - 0.4f, t[2] - 0.2f, t[3] - 0.1f, t[4] - 0.5f, t[5] + 0.3f };
            for (int i = 0; i < steps.Length; i++)
                cap.Add(new[] { new ShuttleCaption.Line { text = steps[i], size = 30, bold = true, shade = 1f, y = 0 } },
                        760, 44, 0.055f, 0.885f, s0[i], s1[i], false);
            cap.Add(new[]
            {
                new ShuttleCaption.Line { text = "셔틀랙  Shuttle AS/RS", size = 40, bold = true, shade = 1f, y = 0 },
                new ShuttleCaption.Line { text = $"시뮬레이션 처리량 약 {view.Sim.ThroughputPerHour:N0} 토트/시간", size = 24, bold = false, shade = 0.88f, y = 54 },
            }, 760, 88, 0.055f, 0.085f, t[5] + 0.4f, seconds + 1f, true);
            return cap;
        }

        void OnGUI()
        {
            if (!IsRecording || tex == null) return;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(12, Screen.height - 30, 400, 24), $"REC  {frame}/{total} frames  ({frame / (float)fps:0.0}s / {seconds:0}s)");
        }
    }
}
