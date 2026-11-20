// 모던 자막 오버레이: 설비명 타이틀 카드 + 왼쪽 위 설비 태그 + 샷별 설명 자막 + 진행 바 + 시작/끝 페이드.
// 후처리(DOF, 모션 블러, 필름 그레인)를 받지 않도록 카메라 렌더가 끝난 뒤 GL 로 직접 그립니다.
//  - 녹화: 각 MovieCapture 가 ReadPixels 직전에 CaptionPresenter.DrawInto(rt) 호출 → 영상 프레임에 들어감
//  - 일반 플레이: OnGUI(Repaint) 에서 화면에 그림
// 글꼴: Windows 의 맑은 고딕(한글) → Segoe UI → Arial 순으로 OS 글꼴 사용. 추가 패키지 없음.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EquipFX
{
    [Serializable]
    public class CaptionInfo
    {
        public string category = "AUTOMATED STORAGE";    // 작은 영문 분류 (자간 넓게)
        public string name = "Equipment";                 // 큰 설비명
        public string korean = "";                        // 한글 설명
        public string spec = "";                          // 타이틀 카드 아래 한 줄 사양 (선택)
        public string tag = "";                           // 오른쪽 위 태그의 짧은 한글 (비우면 korean)
        public Color accent = new Color(0.25f, 0.78f, 1f);
    }

    public interface ICaptionSource
    {
        ShotSequencer Seq { get; }
        float VideoTime { get; }      // 영상 시작 후 초 (시네마틱 카메라 기준)
        bool CinematicActive { get; }
    }

    public class CaptionPresenter : MonoBehaviour
    {
        public CaptionInfo info = new CaptionInfo();
        public bool show = true;
        public ICaptionSource source;
        // 녹화 중이면 화면용 OnGUI 그리기를 끔 (녹화 미리보기에 이미 들어 있음)
        public static bool Recording;

        public void DrawInto(RenderTexture rt)
        {
            if (!show || source == null || !source.CinematicActive) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            CaptionOverlay.Draw(info, source.Seq, source.VideoTime, rt.width, rt.height);
            RenderTexture.active = prev;
        }

        void OnGUI()
        {
            if (Recording || !show || source == null || !source.CinematicActive) return;
            if (Event.current.type != EventType.Repaint) return;
            CaptionOverlay.Draw(info, source.Seq, source.VideoTime, Screen.width, Screen.height);
        }
    }

    public static class CaptionOverlay
    {
        static Font font;
        static Material textMat, solidMat;
        static float S;                 // 1080p 기준 배율
        static int W, H;
        static bool render;             // false = 글자만 요청 (아틀라스 갱신), true = 그리기
        static readonly List<(string s, int size, FontStyle st)> requests = new List<(string, int, FontStyle)>();

        static void Ensure()
        {
            if (font == null)
            {
                try { font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Segoe UI", "Arial" }, 48); } catch { font = null; }
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            if (textMat == null)
            {
                textMat = new Material(Shader.Find("GUI/Text Shader")) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (solidMat == null)
            {
                solidMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                solidMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                solidMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                solidMat.SetInt("_Cull", (int)CullMode.Off);
                solidMat.SetInt("_ZWrite", 0);
                solidMat.SetInt("_ZTest", (int)CompareFunction.Always);
            }
        }

        // 선형 색공간 프로젝트에서 GL 정점 색은 변환되지 않으므로 직접 선형으로 바꿔 줌
        static Color C(Color c, float a)
        {
            var o = QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
            o.a = c.a * a; return o;
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }
        // 등장 a0..a1, 퇴장 b0..b1 의 알파
        static float Window(float t, float a0, float a1, float b0, float b1) => Smooth(a0, a1, t) * (1 - Smooth(b0, b1, t));

        public static void Draw(CaptionInfo info, ShotSequencer seq, float t, int w, int h)
        {
            Ensure();
            W = w; H = h; S = h / 1080f;
            float dur = seq != null ? seq.Duration : 30f;
            // 1) 글자 요청 (아틀라스가 다시 만들어져도 UV 가 어긋나지 않게 먼저 전부 요청)
            requests.Clear(); render = false; Layout(info, seq, t, dur);
            foreach (var r in requests) font.RequestCharactersInTexture(r.s, r.size, r.st);
            // 2) 그리기
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, h, 0);       // 왼쪽 위 원점, y 아래로
            Shader.SetGlobalInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            render = true; Layout(info, seq, t, dur);
            GL.PopMatrix();
        }

        static void Layout(CaptionInfo info, ShotSequencer seq, float t, float dur)
        {
            var white = Color.white;
            var grey = new Color(0.82f, 0.85f, 0.88f);
            var acc = info.accent;

            // ---- 시작/끝 페이드 (검정)
            float fadeIn = 1 - Smooth(0f, 0.7f, t), fadeOut = Smooth(dur - 0.8f, dur, t);
            float black = Mathf.Max(fadeIn, fadeOut);
            if (black > 0.001f) Rect(0, 0, W, H, C(Color.black, black));

            // ---- 타이틀 카드 (0.4 ~ 5.6 s): 왼쪽 아래
            float ta = Window(t, 0.4f, 1.2f, 4.8f, 5.6f);
            if (ta > 0.001f)
            {
                float slide = (1 - Smooth(0.4f, 1.4f, t)) * 40 * S;
                float x = 110 * S, yb = H - 150 * S + slide;
                // 밝은 배경에서도 읽히도록: 아래 왼쪽 그라데이션 + 글자 뒤 반투명 패널
                Gradient(0, H - 470 * S, W * 0.62f, 470 * S, C(new Color(0, 0, 0, 0.5f), ta));
                float tw = Mathf.Max(Measure(info.name, 92, FontStyle.Bold, 0.5f), Mathf.Max(Measure(info.korean, 34, FontStyle.Normal, 0.5f), Measure(info.spec, 22, FontStyle.Normal, 1.5f)));
                Rect(x - 34 * S, yb - 196 * S, tw + 34 * S + 56 * S, (string.IsNullOrEmpty(info.spec) ? 200 : 250) * S, C(new Color(0.02f, 0.025f, 0.035f, 0.58f), ta));
                // 세로 강조 바 (자라나는 애니메이션)
                float barH = 190 * S * Smooth(0.5f, 1.3f, t);
                Rect(x - 34 * S, yb - barH, 6 * S, barH, C(acc, ta));
                Text(info.category.ToUpperInvariant(), x, yb - 172 * S, 22, FontStyle.Bold, C(acc, ta), 5.5f);
                Text(info.name, x - 4 * S, yb - 150 * S, 92, FontStyle.Bold, C(white, ta), 0.5f);
                Text(info.korean, x, yb - 38 * S, 34, FontStyle.Normal, C(grey, ta), 0.5f);
                if (!string.IsNullOrEmpty(info.spec))
                    Text(info.spec, x, yb + 10 * S, 22, FontStyle.Normal, C(new Color(0.65f, 0.7f, 0.75f), ta * Smooth(1.0f, 1.8f, t)), 1.5f);
            }

            // ---- 오른쪽 위 설비 태그 (타이틀 카드가 사라진 뒤 계속. 왼쪽 위는 플레이 중 HUD 자리)
            float tg = Window(t, 5.2f, 6.0f, dur - 1.2f, dur - 0.6f);
            if (tg > 0.001f)
            {
                string ko = string.IsNullOrEmpty(info.tag) ? info.korean : info.tag;
                float wName = Measure(info.name.ToUpperInvariant(), 24, FontStyle.Bold, 3f);
                float wKo = Measure(ko, 21, FontStyle.Normal, 0.3f);
                float pw = wName + wKo + 90 * S;
                float x = W - 46 * S - pw + 18 * S, y = 54 * S;
                Rect(x - 18 * S, y - 12 * S, pw, 50 * S, C(new Color(0.03f, 0.04f, 0.05f, 0.5f), tg));
                Rect(x - 18 * S, y - 12 * S, 4 * S, 50 * S, C(acc, tg));
                Text(info.name.ToUpperInvariant(), x, y, 24, FontStyle.Bold, C(white, tg), 3f);
                Rect(x + wName + 18 * S, y + 4 * S, 1.5f * S, 20 * S, C(new Color(1, 1, 1, 0.45f), tg));
                Text(ko, x + wName + 36 * S, y + 2 * S, 21, FontStyle.Normal, C(grey, tg), 0.3f);
            }

            // ---- 샷 설명 자막 (첫 샷은 타이틀 카드가 대신함)
            if (seq != null)
                for (int k = 0; k < seq.shots.Count; k++)
                {
                    var s = seq.shots[k];
                    if (string.IsNullOrEmpty(s.title) || k == 0) continue;
                    // 타이틀 카드(~5.6 s)가 사라진 뒤에 나오도록
                    float a0 = Mathf.Max(s.start + 0.25f, 5.5f), b1 = s.end - 0.05f;
                    if (a0 > b1 - 1.0f) continue;
                    float sa = Window(t, a0, a0 + 0.55f, b1 - 0.45f, b1);
                    if (sa <= 0.001f) continue;
                    float slide = (1 - Smooth(a0, a0 + 0.7f, t)) * 26 * S;
                    float x = 110 * S, y = H - 178 * S + slide;
                    Gradient(0, H - 330 * S, W * 0.5f, 330 * S, C(new Color(0, 0, 0, 0.35f), sa));
                    float cw = Mathf.Max(Measure(s.title, 44, FontStyle.Bold, 0.3f), Measure(s.detail, 25, FontStyle.Normal, 0.3f));
                    Rect(x - 30 * S, y - 14 * S, cw + 30 * S + 44 * S, 128 * S, C(new Color(0.02f, 0.025f, 0.035f, 0.58f), sa));
                    Rect(x - 30 * S, y - 14 * S, 4 * S, 128 * S, C(acc, sa));
                    Text(s.title, x, y, 44, FontStyle.Bold, C(white, sa), 0.3f);
                    if (!string.IsNullOrEmpty(s.detail)) Text(s.detail, x, y + 60 * S, 25, FontStyle.Normal, C(grey, sa), 0.3f);
                    // 샷 번호 (예: 03 / 07)
                    string idx = $"{k + 1:00} / {seq.shots.Count:00}";
                    Text(idx, W - 64 * S - Measure(idx, 20, FontStyle.Bold, 3f), H - 82 * S, 20, FontStyle.Bold, C(new Color(1, 1, 1, 0.7f), sa), 3f);
                }

            // ---- 아래 진행 바
            float pa = Window(t, 0.6f, 1.4f, dur - 1.0f, dur - 0.5f) * 0.85f;
            if (pa > 0.001f)
            {
                float y = H - 40 * S, x0 = 64 * S, x1 = W - 64 * S;
                Rect(x0, y, x1 - x0, 2 * S, C(new Color(1, 1, 1, 0.18f), pa));
                Rect(x0, y, (x1 - x0) * Mathf.Clamp01(t / dur), 2 * S, C(acc, pa));
            }
        }

        // 장비 표면에 붙일 번호판 텍스처 (예: AutoStore 로봇 상판 번호). 투명 배경 + 글자, 밉맵 포함.
        public static Texture2D MakeLabelTexture(string text, int w, int h, Color fg, float fill = 0.78f)
        {
            Ensure();
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(fg.r, fg.g, fg.b, 0));
            W = w; H = h; S = 1f; render = false;
            int px = Mathf.RoundToInt(h * fill * 1.25f);
            font.RequestCharactersInTexture(text, px, FontStyle.Bold);
            render = true;
            float tw = Measure(text, px, FontStyle.Bold, px * 0.04f);
            GL.PushMatrix(); GL.LoadPixelMatrix(0, w, h, 0);
            Shader.SetGlobalInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            // 글자 상자 높이 ≈ px, 세로 가운데 정렬
            Text(text, (w - tw) / 2, (h - px) / 2f - px * 0.04f, px, FontStyle.Bold, fg, px * 0.04f);
            GL.PopMatrix();
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, anisoLevel = 8, name = "Label_" + text };
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0, true);
            tex.Apply(true);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        // ------------------------------------------------------------------ 기본 도형 / 글자
        static void Rect(float x, float y, float w, float h, Color c)
        {
            if (!render) return;
            solidMat.SetPass(0);
            GL.Begin(GL.QUADS); GL.Color(c);
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
            GL.End();
        }

        // 왼쪽 아래가 가장 진하고 오른쪽/위로 갈수록 투명해지는 그라데이션
        static void Gradient(float x, float y, float w, float h, Color c)
        {
            if (!render) return;
            var z = c; z.a = 0;
            solidMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(z); GL.Vertex3(x, y, 0);
            GL.Color(z); GL.Vertex3(x + w, y, 0);
            GL.Color(z); GL.Vertex3(x + w, y + h, 0);
            GL.Color(c); GL.Vertex3(x, y + h, 0);
            GL.End();
        }

        static int Px(int size) => Mathf.Max(8, Mathf.RoundToInt(size * S));

        static float Measure(string s, int size, FontStyle st, float tracking)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int px = Px(size);
            if (!render) { requests.Add((s, px, st)); }
            float w = 0;
            foreach (char ch in s)
                if (font.GetCharacterInfo(ch, out var ci, px, st)) w += ci.advance + tracking * S;
            return w;
        }

        // (x, y) = 글자 상자 왼쪽 위. tracking = 자간 (1080p 기준 px)
        static void Text(string s, float x, float y, int size, FontStyle st, Color c, float tracking)
        {
            if (string.IsNullOrEmpty(s)) return;
            int px = Px(size);
            if (!render) { requests.Add((s, px, st)); return; }
            float baseY = y + px * 0.92f;   // 대략적인 어센더 높이
            textMat.mainTexture = font.material.mainTexture;
            textMat.SetPass(0);
            GL.Begin(GL.QUADS); GL.Color(c);
            float pen = x;
            foreach (char ch in s)
            {
                if (!font.GetCharacterInfo(ch, out var ci, px, st)) continue;
                float x0 = pen + ci.minX, x1 = pen + ci.maxX;
                float y0 = baseY - ci.maxY, y1 = baseY - ci.minY;
                GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y0, 0);
                GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y0, 0);
                GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y1, 0);
                GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y1, 0);
                pen += ci.advance + tracking * S;
            }
            GL.End();
        }
    }
}
