// 장비 이름 캡션 오버레이 (모던 로우어 서드 + 장면별 챕터 + 엔드 카드)
// 캡션은 별도 카메라가 투명 배경 RenderTexture 에 그리고, 녹화 때 프레임 위에 합성합니다.
// (메인 카메라의 피사계 심도·모션 블러·필름 그레인이 글자에 걸리지 않게 하려고 분리)
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

namespace TSorter
{
    public class TSorterCaption : MonoBehaviour
    {
        public const int Layer = 5;                 // 내장 "UI" 레이어
        public int width = 1920, height = 1080;

        public RenderTexture RT { get; private set; }
        public Material CompositeMat { get; private set; }

        static readonly Color Accent = new Color(1.0f, 0.74f, 0.05f);      // Libiao 노랑
        static readonly Color Panel = new Color(0.02f, 0.025f, 0.03f, 0.55f);
        static readonly Color Sub = new Color(0.80f, 0.82f, 0.85f);

        TSorterView view;
        Camera capCam;
        Font font;
        Block title, endCard, tag;
        Block[] chapters;

        // 장면 시간표 (초). 카메라 동선(TSorterCamera / blender CAM_KEYS)과 맞춤
        static readonly (float t0, float t1, string no, string head, string sub)[] Chapters =
        {
            (6.2f, 9.6f,   "01", "투입 스테이션", "작업자가 바코드를 스캔해 로봇 트레이에 올립니다"),
            (9.8f, 15.6f,  "02", "핑거 주행 → 틸트 투하", "목적지 슈트 옆에서 트레이를 기울여 소포를 떨어뜨립니다"),
            (15.8f, 21.0f, "03", "슈트 통으로 분류", "핑거 사이 틈 아래에 목적지별 통이 두 줄로 놓여 있습니다"),
            (21.2f, 26.4f, "04", "동시 다목적지 분류", "로봇 30대가 일방통행 차선과 셀 예약으로 서로 비켜 갑니다"),
        };

        class Block
        {
            public RectTransform rt; public CanvasGroup cg; public RectTransform bar; public float barH;
            public float t0, t1, slide = 48f; public Vector2 home;
        }

        public void Init(TSorterView v)
        {
            view = v;
            { var sz = EquipFX.RecordQuality.OutputSize; width = sz.x; height = sz.y; }   // 녹화 화질 프리셋과 같은 해상도 (4K 자막 선명)
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Segoe UI", "Arial" }, 48);
            RT = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "TSorterCaptionRT" };
            RT.Create();
            var sh = Resources.Load<Shader>("TSorterCaptionOver");
            if (sh) CompositeMat = new Material(sh);

            var cg = new GameObject("CaptionCamera"); cg.layer = Layer;
            cg.transform.position = new Vector3(0, -200, 0);
            capCam = cg.AddComponent<Camera>();
            capCam.clearFlags = CameraClearFlags.SolidColor; capCam.backgroundColor = new Color(0, 0, 0, 0);
            capCam.cullingMask = 1 << Layer; capCam.depth = -10; capCam.orthographic = true;
            capCam.nearClipPlane = 0.1f; capCam.farClipPlane = 10f; capCam.targetTexture = RT;
            var ucd = capCam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = false; ucd.antialiasing = AntialiasingMode.None; ucd.renderShadows = false;

            var canvasGo = new GameObject("CaptionCanvas"); canvasGo.layer = Layer;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = capCam; canvas.planeDistance = 1f;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            // 1) 타이틀 (로우어 서드)
            title = MakeBlock(root, "Title", new Vector2(96, 112), 0.6f, 6.0f, 760, 176);
            Label(title.rt, "L I B I A O   R O B O T I C S", 20, Accent, FontStyle.Bold, new Vector2(40, 132), 700, 30);
            Label(title.rt, "T-Sort  핑거형 로봇 소터", 56, Color.white, FontStyle.Bold, new Vector2(38, 62), 720, 70);
            Label(title.rt, "Finger-type platform robotic sorting system", 24, Sub, FontStyle.Normal, new Vector2(40, 22), 720, 34);

            // 2) 챕터
            chapters = new Block[Chapters.Length];
            for (int i = 0; i < Chapters.Length; i++)
            {
                var ch = Chapters[i];
                var b = MakeBlock(root, "Chapter" + ch.no, new Vector2(96, 112), ch.t0, ch.t1, 820, 112);
                Label(b.rt, ch.no, 34, Accent, FontStyle.Bold, new Vector2(38, 52), 70, 46);
                Label(b.rt, ch.head, 34, Color.white, FontStyle.Bold, new Vector2(104, 52), 700, 46);
                Label(b.rt, ch.sub, 21, Sub, FontStyle.Normal, new Vector2(40, 16), 770, 32);
                chapters[i] = b;
            }

            // 3) 엔드 카드
            var c = v.config;
            endCard = MakeBlock(root, "EndCard", new Vector2(96, 112), 26.8f, 30.5f, 900, 176);
            Label(endCard.rt, "L I B I A O   T - S O R T", 20, Accent, FontStyle.Bold, new Vector2(40, 132), 800, 30);
            Label(endCard.rt, $"로봇 {c.robots}대 · 슈트 {v.Sim.chutes.Count}개 · 투입 {c.stations.Length}곳", 44, Color.white, FontStyle.Bold, new Vector2(38, 66), 860, 60);
            Label(endCard.rt, "시뮬레이션 처리량 약 2,100건/h (가정값 기반, 실제 설비 수치 아님)", 22, Sub, FontStyle.Normal, new Vector2(40, 24), 860, 34);

            // 4) 오른쪽 위 작은 태그 (항상)
            tag = new Block { t0 = 0.3f, t1 = 1e9f };
            var tgo = new GameObject("Tag", typeof(RectTransform)); tgo.layer = Layer; tgo.transform.SetParent(root, false);
            tag.rt = (RectTransform)tgo.transform; tag.cg = tgo.AddComponent<CanvasGroup>();
            Anchor(tag.rt, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-64, -52), new Vector2(420, 34));
            var dot = Img(tag.rt, Accent, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0), new Vector2(10, 10));
            dot.pivot = new Vector2(0, 0.5f);
            var tl = Label(tag.rt, "LIBIAO T-SORT   |   SIMULATION", 18, new Color(1, 1, 1, 0.9f), FontStyle.Bold, new Vector2(22, 0), 400, 34);
            tl.alignment = TextAnchor.MiddleLeft;
            tag.home = tag.rt.anchoredPosition; tag.slide = 0;
        }

        Block MakeBlock(Transform root, string name, Vector2 pos, float t0, float t1, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.layer = Layer; go.transform.SetParent(root, false);
            var b = new Block { rt = (RectTransform)go.transform, t0 = t0, t1 = t1 };
            b.cg = go.AddComponent<CanvasGroup>();
            Anchor(b.rt, Vector2.zero, Vector2.zero, pos, new Vector2(w, h));
            b.rt.pivot = Vector2.zero; b.rt.anchoredPosition = pos; b.home = pos;
            // 반투명 패널 (오른쪽으로 갈수록 옅어지는 그라디언트 대신 두 장 겹침)
            var p1 = Img(b.rt, Panel, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            p1.offsetMin = Vector2.zero; p1.offsetMax = new Vector2(-w * 0.35f, 0);
            var p2 = Img(b.rt, new Color(Panel.r, Panel.g, Panel.b, Panel.a * 0.55f), new Vector2(0.65f, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            p2.offsetMin = Vector2.zero; p2.offsetMax = Vector2.zero;
            // 노란 세로 막대 (커지며 등장)
            b.bar = Img(b.rt, Accent, Vector2.zero, Vector2.zero, new Vector2(0, 0), new Vector2(8, h));
            b.bar.pivot = Vector2.zero; b.barH = h;
            b.cg.alpha = 0;
            return b;
        }

        static void Anchor(RectTransform r, Vector2 amin, Vector2 amax, Vector2 pos, Vector2 size)
        {
            r.anchorMin = amin; r.anchorMax = amax; r.pivot = amin; r.sizeDelta = size; r.anchoredPosition = pos;
        }

        RectTransform Img(RectTransform parent, Color col, Vector2 amin, Vector2 amax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Img", typeof(RectTransform)); go.layer = Layer; go.transform.SetParent(parent, false);
            var im = go.AddComponent<Image>(); im.color = col; im.raycastTarget = false;
            var r = (RectTransform)go.transform; r.anchorMin = amin; r.anchorMax = amax; r.pivot = amin; r.anchoredPosition = pos; r.sizeDelta = size;
            return r;
        }

        Text Label(RectTransform parent, string s, int size, Color col, FontStyle st, Vector2 pos, float w, float h)
        {
            var go = new GameObject("Text", typeof(RectTransform)); go.layer = Layer; go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>(); t.font = font; t.text = s; t.fontSize = size; t.color = col; t.fontStyle = st;
            t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var sh = go.AddComponent<Shadow>(); sh.effectColor = new Color(0, 0, 0, 0.45f); sh.effectDistance = new Vector2(1.5f, -1.5f);
            var r = (RectTransform)go.transform; r.anchorMin = r.anchorMax = r.pivot = Vector2.zero; r.anchoredPosition = pos; r.sizeDelta = new Vector2(w, h);
            return t;
        }

        static float Ease(float u) { u = Mathf.Clamp01(u); return 1 - Mathf.Pow(1 - u, 3); }

        void Animate(Block b, float time)
        {
            if (b == null) return;
            float a = Mathf.Min(Ease((time - b.t0) / 0.6f), 1 - Mathf.Clamp01((time - (b.t1 - 0.45f)) / 0.45f));
            a = Mathf.Clamp01(a);
            b.cg.alpha = a;
            b.rt.anchoredPosition = b.home + new Vector2(-b.slide * (1 - Ease((time - b.t0) / 0.7f)), 0);
            if (b.bar) b.bar.sizeDelta = new Vector2(8, b.barH * Ease((time - b.t0 - 0.1f) / 0.5f));
        }

        void LateUpdate()
        {
            if (view == null || view.Cam == null) return;
            var cine = view.Cam.GetComponent<TSorterCamera>();
            bool cinematic = view.cinematicCamera && cine != null && cine.Progress >= 0;
            float time = cinematic ? cine.Progress * cine.duration : -1;
            Animate(title, cinematic ? time : -1);
            Animate(endCard, cinematic ? time : -1);
            foreach (var b in chapters) Animate(b, cinematic ? time : -1);
            Animate(tag, cinematic ? time : 99f);
        }

        // 녹화 프레임(RenderTexture)에 캡션을 합성
        public void CompositeInto(RenderTexture dst)
        {
            if (RT == null || CompositeMat == null || !enabled) return;
            Graphics.Blit(RT, dst, CompositeMat);
        }

        // 플레이 화면 미리보기 (녹화 중에는 TSorterMovieCapture 가 합성된 프레임을 보여 줌)
        void OnGUI()
        {
            if (RT == null || CompositeMat == null || TSorterMovieCapture.IsRecording || Event.current.type != EventType.Repaint) return;
            float sw = Screen.width, sh = Screen.height, s = Mathf.Min(sw / width, sh / height);
            var r = new Rect((sw - width * s) / 2, (sh - height * s) / 2, width * s, height * s);
            Graphics.DrawTexture(r, RT, CompositeMat);
        }

        void OnDestroy() { if (RT) RT.Release(); }
    }
}
