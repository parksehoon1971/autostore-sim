// 장비명 자막 오버레이 (영상에도 찍힘): 왼쪽 아래 장비명 타이틀 → 단계 자막, 오른쪽 위 실시간 주행·승강 수치
// URP 오버레이 카메라 + Screen Space - Camera 캔버스로 그려 녹화 RenderTexture 에 같이 들어가고, 후처리(DOF·모션 블러)는 받지 않습니다.
// 시간 기준은 StackerCraneView.MovieTime (워밍업 끝 = 0). 단계 시간은 seed 6, warmup 624 s 궤적의 크레인 0 동작에 맞춤.
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

namespace StackerCrane
{
    public class StackerCraneCaption : MonoBehaviour
    {
        const int UiLayer = 5;   // 기본 "UI" 레이어
        static readonly Color Accent = new Color(1.00f, 0.66f, 0.10f);

        StackerCraneView view;
        Font font;
        RectTransform titleGroup, stepGroup, telemetry, tag;
        CanvasGroup titleCg, stepCg, telCg, tagCg;
        Text stepNo, stepText, telText;
        int shownStep = -1; float stepSince;
        double lastX, lastZ, lastT = -1; float vx, vz;

        // 단계 자막 (시작 s, 끝 s, 번호, 문구)
        static readonly (float t0, float t1, string no, string text)[] Steps =
        {
            (5.0f, 7.6f, "01", "입고 컨베이어의 팔레트를 텔레스코픽 포크로 인수"),
            (7.6f, 16.6f, "02", "주행 최고 4 m/s · 승강 1.2 m/s 동시 이동"),
            (16.8f, 22.6f, "03", "6단 랙 (9.6 m) 에 입고"),
            (23.0f, 30.5f, "04", "다음 작업: 출고 팔레트 인출"),
        };

        public void Init(StackerCraneView v, Camera baseCam)
        {
            view = v;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Segoe UI", "Noto Sans CJK KR", "Arial" }, 48);
            // 오버레이 카메라 (UI 레이어만, 후처리 없음)
            var oc = new GameObject("CaptionCamera").AddComponent<Camera>();
            oc.transform.SetParent(baseCam.transform, false);
            oc.clearFlags = CameraClearFlags.Depth; oc.cullingMask = 1 << UiLayer;
            oc.nearClipPlane = 0.1f; oc.farClipPlane = 10f;
            var od = oc.GetUniversalAdditionalCameraData();
            od.renderType = CameraRenderType.Overlay; od.renderPostProcessing = false;
            baseCam.cullingMask &= ~(1 << UiLayer);
            baseCam.GetUniversalAdditionalCameraData().cameraStack.Add(oc);

            var cgo = new GameObject("CaptionCanvas"); cgo.layer = UiLayer;
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = oc; canvas.planeDistance = 1f;
            var sc = cgo.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 1f;
            var root = cgo.transform;

            // --- 타이틀 (왼쪽 아래)
            titleGroup = MakeRect("Title", root, new Vector2(0, 0), new Vector2(0, 0), new Vector2(96, 104), new Vector2(980, 170));
            titleCg = titleGroup.gameObject.AddComponent<CanvasGroup>();
            Panel(titleGroup, new Color(0.04f, 0.05f, 0.06f, 0.55f), new Vector2(0, 0), new Vector2(980, 170));
            Panel(titleGroup, Accent, new Vector2(0, 0), new Vector2(8, 170));
            Label(titleGroup, "AUTOMATED STORAGE & RETRIEVAL", 20, FontStyle.Normal, new Color(1, 1, 1, 0.65f), new Vector2(40, 122), new Vector2(900, 30));
            Label(titleGroup, "STACKER CRANE", 64, FontStyle.Bold, Color.white, new Vector2(38, 50), new Vector2(900, 76));
            Label(titleGroup, "스태커크레인  ·  팔레트 자동창고 (Unit-load AS/RS)", 26, FontStyle.Normal, new Color(1, 1, 1, 0.85f), new Vector2(40, 16), new Vector2(900, 36));

            // --- 작은 장비명 태그 (왼쪽 위, 타이틀이 사라진 뒤)
            tag = MakeRect("Tag", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(64, -96), new Vector2(520, 44));
            tagCg = tag.gameObject.AddComponent<CanvasGroup>();
            Panel(tag, new Color(0.04f, 0.05f, 0.06f, 0.5f), Vector2.zero, new Vector2(520, 44));
            Panel(tag, Accent, Vector2.zero, new Vector2(5, 44));
            Label(tag, "STACKER CRANE   |   스태커크레인", 22, FontStyle.Bold, Color.white, new Vector2(22, 4), new Vector2(490, 36));

            // --- 단계 자막 (왼쪽 아래)
            stepGroup = MakeRect("Step", root, new Vector2(0, 0), new Vector2(0, 0), new Vector2(96, 96), new Vector2(1000, 64));
            stepCg = stepGroup.gameObject.AddComponent<CanvasGroup>();
            Panel(stepGroup, new Color(0.04f, 0.05f, 0.06f, 0.55f), Vector2.zero, new Vector2(1000, 64));
            Panel(stepGroup, Accent, Vector2.zero, new Vector2(70, 64));
            stepNo = Label(stepGroup, "01", 30, FontStyle.Bold, new Color(0.08f, 0.08f, 0.08f), new Vector2(0, 10), new Vector2(70, 44));
            stepNo.alignment = TextAnchor.MiddleCenter;
            stepText = Label(stepGroup, "", 30, FontStyle.Normal, Color.white, new Vector2(92, 10), new Vector2(900, 44));

            // --- 실시간 수치 (오른쪽 위)
            telemetry = MakeRect("Telemetry", root, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-64, -96), new Vector2(470, 120));
            telCg = telemetry.gameObject.AddComponent<CanvasGroup>();
            Panel(telemetry, new Color(0.04f, 0.05f, 0.06f, 0.5f), Vector2.zero, new Vector2(470, 120));
            Panel(telemetry, Accent, new Vector2(0, 116), new Vector2(470, 4));
            telText = Label(telemetry, "", 24, FontStyle.Normal, Color.white, new Vector2(22, 8), new Vector2(440, 104));
            telText.lineSpacing = 1.15f;

            titleCg.alpha = stepCg.alpha = telCg.alpha = tagCg.alpha = 0;
        }

        // ------------------------------------------------------------------ UI 도우미
        static RectTransform MakeRect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.layer = UiLayer;
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }
        static Image Panel(RectTransform parent, Color c, Vector2 pos, Vector2 size)
        {
            var rt = MakeRect("Panel", parent, Vector2.zero, Vector2.zero, pos, size);
            var im = rt.gameObject.AddComponent<Image>(); im.color = c; im.raycastTarget = false;
            return im;
        }
        Text Label(RectTransform parent, string s, int size, FontStyle style, Color c, Vector2 pos, Vector2 box)
        {
            var rt = MakeRect("Text", parent, Vector2.zero, Vector2.zero, pos, box);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.fontStyle = style; t.color = c; t.raycastTarget = false;
            t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = s;
            return t;
        }
        static float Ease(float x) { x = Mathf.Clamp01(x); return 1f - Mathf.Pow(1f - x, 3f); }
        static float Window(float t, float t0, float t1, float fade = 0.45f) =>
            Mathf.Min(Ease((t - t0) / fade), Ease((t1 - t) / fade));

        // ------------------------------------------------------------------ 매 프레임
        void LateUpdate()
        {
            if (view == null || view.Sim == null || titleGroup == null) return;
            bool on = view.showCaptions;
            float t = view.MovieTime;

            // 타이틀: 0.3 s 에 왼쪽에서 밀려 들어오고 5 s 에 사라짐
            float ta = on ? Window(t, 0.3f, 5.0f, 0.6f) : 0f;
            titleCg.alpha = ta;
            titleGroup.anchoredPosition = new Vector2(96 - 60f * (1f - Ease((t - 0.3f) / 0.8f)), 104);
            tagCg.alpha = on ? Ease((t - 5.2f) / 0.6f) : 0f;

            // 단계 자막
            int k = -1;
            for (int i = 0; i < Steps.Length; i++) if (t >= Steps[i].t0 && t < Steps[i].t1) k = i;
            if (k != shownStep) { shownStep = k; stepSince = t; if (k >= 0) { stepNo.text = Steps[k].no; stepText.text = Steps[k].text; } }
            stepCg.alpha = on && k >= 0 ? Window(t, Steps[k].t0, Steps[k].t1, 0.35f) : 0f;
            if (k >= 0) stepGroup.anchoredPosition = new Vector2(96 - 30f * (1f - Ease((t - stepSince) / 0.5f)), 96);

            // 실시간 수치 (크레인 0): 속도는 시뮬레이션 위치 차분, 살짝 평활
            var cr = view.Sim.cranes[0];
            double st = view.Sim.t;
            if (lastT >= 0 && st > lastT + 1e-6)
            {
                float dt = (float)(st - lastT);
                vx = Mathf.Lerp(vx, (float)((cr.x - lastX) / dt), 0.5f);
                vz = Mathf.Lerp(vz, (float)((cr.z - lastZ) / dt), 0.5f);
            }
            lastX = cr.x; lastZ = cr.z; lastT = st;
            telCg.alpha = on ? Ease((t - 5.6f) / 0.6f) : 0f;
            string arrow = cr.fork > 0.02 ? "▶ 펴짐" : cr.fork < -0.02 ? "◀ 펴짐" : "접힘";
            telText.text = $"<b>주행</b>  {Mathf.Abs(vx),4:0.0} m/s     <b>승강</b>  {Mathf.Abs(vz),3:0.0} m/s\n" +
                           $"<b>높이</b>  {cr.z,4:0.0} m       <b>포크</b>  {arrow}";
            telText.supportRichText = true;
        }
    }
}
