// 장비명 캡션 오버레이 (녹화 영상에도 들어감)
// 화면 왼쪽 아래: 회사명 · 장비명 · 한 줄 설명 + 진행 막대, 그 위: 지금 장면 설명 (바뀔 때 페이드·슬라이드), 오른쪽 위: 시뮬레이션 표시.
// 구현: 메인 카메라의 URP 카메라 스택에 UI 전용 오버레이 카메라(직교)를 붙이고, 그 앞에 월드 공간 캔버스를 둡니다.
//       오버레이 카메라는 후처리를 하지 않아 글자가 피사계 심도·모션 블러에 흐려지지 않고,
//       메인 카메라가 RenderTexture 로 녹화될 때도 같은 대상에 그려집니다.
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace AirRob
{
    public class AirRobCaption : MonoBehaviour
    {
        public AirRobCamera cine;
        public string company = "LIBIAO ROBOTICS";
        public string title = "AirRob";
        public string subtitle = "랙 면 레일 로봇 + 바닥 이송 로봇 토트 창고";
        public Color accent = new Color(0.12f, 0.50f, 1.0f, 1f);

        // 장면 설명 (시네마틱 카메라 시간, 초). AirRobCamera 동선과 맞춤
        static readonly (float t, string text)[] Phases =
        {
            (0.0f, "통로 4개 · 13단 랙 · 통로마다 AirRob 1대 + 바닥 로봇 16대"),
            (3.0f, "AirRob: 수평 2 m/s · 수직 1 m/s 를 동시에"),
            (9.3f, "양방향 훅으로 맨 위 단 토트를 당겨 옴 (4.5 s)"),
            (14.0f, "토트를 싣고 터널 높이로 내려옴"),
            (20.5f, "랙 아래 터널에서 기다리는 바닥 로봇 위로 밀어 줌"),
            (25.5f, "바닥 로봇이 작업 스테이션으로 운반"),
        };

        const int UILayer = 5;
        const float RefH = 1080f, Ortho = 5.4f;   // 캔버스 1 단위 = 기준 1080p 의 1 px
        Camera baseCam, uiCam;
        RectTransform canvasRT, lowerThird, phaseRoot, progress;
        CanvasGroup all, phaseGroup;
        Text phaseText;
        int phaseIdx = -2; float phaseT0, startT;

        void Start()
        {
            baseCam = GetComponent<Camera>();
            Build();
            startT = Time.time;
        }

        void Build()
        {
            baseCam.cullingMask &= ~(1 << UILayer);
            var cg = new GameObject("CaptionCamera"); cg.transform.SetParent(transform, false);
            uiCam = cg.AddComponent<Camera>();
            uiCam.orthographic = true; uiCam.orthographicSize = Ortho;
            uiCam.nearClipPlane = 0.01f; uiCam.farClipPlane = 10f;
            uiCam.cullingMask = 1 << UILayer; uiCam.clearFlags = CameraClearFlags.Depth;
            var ud = uiCam.GetUniversalAdditionalCameraData();
            ud.renderType = CameraRenderType.Overlay; ud.renderPostProcessing = false; ud.renderShadows = false;
            var bd = baseCam.GetUniversalAdditionalCameraData();
            if (!bd.cameraStack.Contains(uiCam)) bd.cameraStack.Add(uiCam);

            var font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Arial" }, 32);

            var cgo = new GameObject("CaptionCanvas", typeof(RectTransform));
            cgo.layer = UILayer;
            cgo.transform.SetParent(uiCam.transform, false);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = uiCam;
            cgo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            canvasRT = (RectTransform)cgo.transform;
            canvasRT.localPosition = new Vector3(0, 0, 1f);
            canvasRT.localScale = Vector3.one * (2f * Ortho / RefH);
            canvasRT.sizeDelta = new Vector2(RefH * 16f / 9f, RefH);
            all = cgo.AddComponent<CanvasGroup>();

            // 왼쪽 아래 장비명 패널
            lowerThird = MakeRect("LowerThird", canvasRT, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(64, 72), new Vector2(760, 168));
            Img("Panel", lowerThird, new Color(0.03f, 0.05f, 0.09f, 0.62f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Img("Accent", lowerThird, accent, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(8, 0), new Vector2(0, 0.5f));
            Txt("Company", lowerThird, font, company, 18, FontStyle.Bold, accent, new Vector2(34, -20), new Vector2(700, 26));
            Txt("Title", lowerThird, font, title, 58, FontStyle.Bold, Color.white, new Vector2(32, -44), new Vector2(700, 70));
            Txt("Subtitle", lowerThird, font, subtitle, 24, FontStyle.Normal, new Color(0.82f, 0.86f, 0.92f), new Vector2(34, -116), new Vector2(720, 34));
            Img("Track", lowerThird, new Color(1, 1, 1, 0.12f), new Vector2(0, 0), new Vector2(1, 0), new Vector2(8, 0), new Vector2(-8, 3), new Vector2(0, 0));
            progress = Img("Progress", lowerThird, accent, new Vector2(0, 0), new Vector2(0, 0), new Vector2(8, 0), new Vector2(0, 3), new Vector2(0, 0));

            // 장면 설명 (패널 위)
            phaseRoot = MakeRect("Phase", canvasRT, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(64, 72 + 168 + 12), new Vector2(760, 46));
            phaseGroup = phaseRoot.gameObject.AddComponent<CanvasGroup>();
            Img("PhaseBg", phaseRoot, new Color(0.03f, 0.05f, 0.09f, 0.45f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Img("PhaseDot", phaseRoot, accent, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(18, 0), new Vector2(10, 10), new Vector2(0, 0.5f));
            phaseText = Txt("PhaseText", phaseRoot, font, "", 26, FontStyle.Normal, Color.white, new Vector2(40, -6), new Vector2(712, 36));

            // 오른쪽 위 표시
            var tag = MakeRect("Tag", canvasRT, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-64, -52), new Vector2(330, 40));
            Img("TagBg", tag, new Color(0.03f, 0.05f, 0.09f, 0.45f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var tt = Txt("TagText", tag, font, "SIMULATION  ·  실시간 1×", 19, FontStyle.Bold, new Color(1, 1, 1, 0.85f), new Vector2(0, -7), new Vector2(330, 28));
            tt.alignment = TextAnchor.MiddleCenter;
            SetLayer(cgo.transform);
        }

        static void SetLayer(Transform t) { t.gameObject.layer = UILayer; foreach (Transform c in t) SetLayer(c); }

        static RectTransform MakeRect(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        static RectTransform Img(string name, RectTransform parent, Color c, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            var rt = MakeRect(name, parent, aMin, aMax, pivot ?? new Vector2(0.5f, 0.5f), pos, size);
            var im = rt.gameObject.AddComponent<Image>(); im.color = c; im.raycastTarget = false;
            return rt;
        }

        static Text Txt(string name, RectTransform parent, Font f, string s, int size, FontStyle st, Color c, Vector2 pos, Vector2 box)
        {
            var rt = MakeRect(name, parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), pos, box);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = f; t.text = s; t.fontSize = size; t.fontStyle = st; t.color = c; t.raycastTarget = false;
            t.alignment = TextAnchor.UpperLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = rt.gameObject.AddComponent<Shadow>(); sh.effectColor = new Color(0, 0, 0, 0.35f); sh.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        void LateUpdate()
        {
            if (canvasRT == null) return;
            // 화면(또는 녹화 RenderTexture) 비율에 맞춰 캔버스 폭 조정
            float aspect = baseCam.targetTexture ? baseCam.targetTexture.width / (float)baseCam.targetTexture.height : baseCam.aspect;
            canvasRT.sizeDelta = new Vector2(RefH * aspect, RefH);

            float te = cine != null ? cine.Elapsed : -1f;
            // 처음 0.7 s: 왼쪽에서 밀려 들어오며 나타남 (녹화 시작 때 카메라 시간이 0 으로 돌아가면 다시)
            float since = te >= 0 ? te : Time.time - startT;
            float a = Mathf.SmoothStep(0, 1, since / 0.7f);
            all.alpha = a;
            lowerThird.anchoredPosition = new Vector2(64 - 40 * (1 - a), 72);

            int idx = -1;
            if (te >= 0) for (int i = 0; i < Phases.Length; i++) if (te >= Phases[i].t) idx = i;
            if (idx != phaseIdx)
            {
                phaseIdx = idx; phaseT0 = Time.time;
                phaseText.text = idx >= 0 ? Phases[idx].text : "자유 시점  ·  마우스로 돌려 보기";
            }
            float p = Mathf.SmoothStep(0, 1, (Time.time - phaseT0) / 0.35f);
            phaseGroup.alpha = p;
            phaseRoot.anchoredPosition = new Vector2(64, 72 + 168 + 12 - 12 * (1 - p));
            if (progress)
            {
                float w = lowerThird.sizeDelta.x - 8;
                float frac = te >= 0 && cine != null ? Mathf.Clamp01(te / cine.duration) : 0f;
                progress.sizeDelta = new Vector2(w * frac, 3);
            }
        }

        void OnDestroy()
        {
            if (baseCam != null && uiCam != null)
            {
                var bd = baseCam.GetUniversalAdditionalCameraData();
                if (bd != null) bd.cameraStack.Remove(uiCam);
            }
        }
    }
}
