// 장비 이름 자막 (blender/carousel_blender.py CAPTIONS 와 같은 시간·문구)
// 왼쪽 아래 반투명 패널 + 파란 세로 막대 + 굵은 제목 + 작은 설명, 0.5초 동안 옆으로 밀려 들어오며 나타남.
// UI 전용 Overlay 카메라(후처리 끔)에 그려서 피사계 심도·모션 블러·블룸이 글자에 걸리지 않고, 녹화 영상에도 그대로 들어갑니다.
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

namespace Carousel
{
    public class CarouselCaption : MonoBehaviour
    {
        public const int UiLayer = 5;   // 기본 "UI" 레이어

        struct Cap { public float t0, t1; public string title, sub; }
        static readonly Cap[] Caps =
        {
            new Cap { t0 = 0.3f, t1 = 3.4f, title = "CAROUSEL STORAGE", sub = "Horizontal pick pod  +  vertical carousel  ·  goods-to-person" },
            new Cap { t0 = 3.8f, t1 = 14.6f, title = "HORIZONTAL CAROUSEL", sub = "2 units × 26 carriers  ·  bidirectional  ·  pick-to-light pod" },
            new Cap { t0 = 17.2f, t1 = 28.0f, title = "VERTICAL CAROUSEL", sub = "20 trays  ·  2.45 m wide  ·  ergonomic window at 1.05 m" },
        };

        CarouselCamera clock;
        RectTransform panel; CanvasGroup group; Text title, sub;
        Camera uiCam;

        public void Init(Camera mainCam, CarouselCamera cine)
        {
            clock = cine;
            // UI 전용 Overlay 카메라를 메인 카메라 스택에 붙임 (메인이 RenderTexture 로 녹화할 때도 같이 그려짐)
            var cg = new GameObject("CaptionCamera");
            cg.transform.SetParent(mainCam.transform, false);
            uiCam = cg.AddComponent<Camera>();
            uiCam.cullingMask = 1 << UiLayer; uiCam.clearFlags = CameraClearFlags.Depth;
            uiCam.orthographic = true; uiCam.nearClipPlane = 0.1f; uiCam.farClipPlane = 10f;
            var ud = uiCam.GetUniversalAdditionalCameraData();
            ud.renderType = CameraRenderType.Overlay; ud.renderPostProcessing = false;
            var md = mainCam.GetUniversalAdditionalCameraData();
            if (!md.cameraStack.Contains(uiCam)) md.cameraStack.Add(uiCam);

            var cgo = new GameObject("CaptionCanvas"); cgo.layer = UiLayer;
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = uiCam; canvas.planeDistance = 1f;
            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight; scaler.matchWidthOrHeight = 1f;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            panel = NewRect("Panel", cgo.transform);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0, 0);
            panel.anchoredPosition = new Vector2(56, 86); panel.sizeDelta = new Vector2(860, 126);
            group = panel.gameObject.AddComponent<CanvasGroup>();
            var bg = panel.gameObject.AddComponent<Image>(); bg.color = new Color(0.05f, 0.065f, 0.09f, 0.62f);
            var bar = NewRect("Accent", panel); bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(0, 1); bar.pivot = new Vector2(0, 0.5f);
            bar.sizeDelta = new Vector2(7, 0); bar.anchoredPosition = Vector2.zero;
            bar.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.63f, 1f, 1f);
            title = NewText("Title", panel, font, 48, FontStyle.Bold, Color.white, new Vector2(34, -14), 64);
            sub = NewText("Sub", panel, font, 26, FontStyle.Normal, new Color(0.80f, 0.86f, 0.92f, 0.95f), new Vector2(36, -78), 36);
            group.alpha = 0;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.layer = UiLayer;
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false); return rt;
        }

        static Text NewText(string name, Transform parent, Font font, int size, FontStyle style, Color col, Vector2 pos, float h)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(-40, h);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.fontStyle = style; t.color = col;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.alignment = TextAnchor.UpperLeft;
            return t;
        }

        void LateUpdate()
        {
            if (group == null || clock == null) return;
            float t = clock.Elapsed;
            Cap? cur = null;
            foreach (var c in Caps) if (t >= c.t0 && t <= c.t1) { cur = c; break; }
            if (cur == null) { group.alpha = 0; return; }
            var cp = cur.Value;
            float a = Mathf.Clamp01(Mathf.Min((t - cp.t0) / 0.5f, (cp.t1 - t) / 0.5f));
            float e = Mathf.SmoothStep(0, 1, a);
            group.alpha = e;
            title.text = cp.title; sub.text = cp.sub;
            // 글자 폭에 맞춰 패널 너비 조정
            float w = Mathf.Max(title.preferredWidth + 34, sub.preferredWidth + 36) + 44;
            panel.sizeDelta = new Vector2(w, 126);
            panel.anchoredPosition = new Vector2(56 - (1 - e) * 30, 86);
        }
    }
}
