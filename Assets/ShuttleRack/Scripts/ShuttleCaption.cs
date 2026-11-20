// 영상 자막: 장비 이름 (왼쪽 아래) + 단계 표시 (왼쪽 위). 녹화 프레임에 직접 합성하므로 후처리(피사계 심도 등)에 흐려지지 않습니다.
// 글자는 처음 한 번만 UI Text 를 별도 카메라로 흰 글씨 / 검은 배경에 렌더해 밝기를 마스크로 씁니다 (한글: 맑은 고딕).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

namespace ShuttleRack
{
    public class ShuttleCaption
    {
        public struct Line { public string text; public int size; public bool bold; public float shade; public float y; }

        class Card
        {
            public float[] mask; public int w, h;      // 글자 마스크 (0~1)
            public int px, py;                         // 프레임 안 왼쪽 아래 위치 (픽셀)
            public int panelW, panelH;                 // 반투명 판 크기
            public float t0, t1;                       // 보이는 시간 (초)
            public bool accent;
        }

        readonly List<Card> cards = new List<Card>();
        readonly int W, H;
        static readonly Color32 Accent = new Color32(242, 140, 13, 255);

        public ShuttleCaption(int frameW, int frameH) { W = frameW; H = frameH; }

        static Font KoreanFont(int size) =>
            Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "Arial" }, size);

        // 글자 마스크 렌더 (w x h 픽셀)
        static float[] RenderMask(int w, int h, Line[] lines)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var camGo = new GameObject("CaptionCam");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << ShuttleView.CaptionLayer; cam.orthographic = true; cam.targetTexture = rt;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
            camGo.transform.position = new Vector3(0, -500, 0);
            var ucd = cam.GetUniversalAdditionalCameraData(); ucd.renderPostProcessing = false; ucd.antialiasing = AntialiasingMode.None;
            var cgo = new GameObject("CaptionCanvas"); cgo.layer = ShuttleView.CaptionLayer;
            var canvas = cgo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 1f;
            foreach (var ln in lines)
            {
                var tgo = new GameObject("Text"); tgo.layer = ShuttleView.CaptionLayer; tgo.transform.SetParent(cgo.transform, false);
                var tx = tgo.AddComponent<Text>();
                tx.font = KoreanFont(ln.size); tx.fontSize = ln.size; tx.fontStyle = ln.bold ? FontStyle.Bold : FontStyle.Normal;
                tx.color = new Color(ln.shade, ln.shade, ln.shade, 1); tx.text = ln.text;
                tx.alignment = TextAnchor.UpperLeft; tx.horizontalOverflow = HorizontalWrapMode.Overflow; tx.verticalOverflow = VerticalWrapMode.Overflow;
                var r = tx.rectTransform; r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(0, -ln.y); r.sizeDelta = new Vector2(w, ln.size * 1.4f);
            }
            Canvas.ForceUpdateCanvases();
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); tex.Apply(false);
            RenderTexture.active = prev;
            var px = tex.GetPixels32(); var mask = new float[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)) / 255f;
            Object.Destroy(tex); Object.Destroy(cgo); Object.Destroy(camGo); rt.Release(); Object.Destroy(rt);
            return mask;
        }

        // 카드 추가: 왼쪽 아래 기준 위치 (화면 비율), 보이는 시간
        public void Add(Line[] lines, int w, int h, float fx, float fy, float t0, float t1, bool accent)
        {
            float s = H / 1080f;
            int ww = Mathf.RoundToInt(w * s), hh = Mathf.RoundToInt(h * s);
            var sl = new Line[lines.Length];
            for (int i = 0; i < lines.Length; i++) { sl[i] = lines[i]; sl[i].size = Mathf.RoundToInt(lines[i].size * s); sl[i].y = lines[i].y * s; }
            float[] mask;
            try { mask = RenderMask(ww, hh, sl); }
            catch (System.Exception e) { Debug.LogWarning("[ShuttleRack] 자막을 만들지 못했습니다: " + e.Message); return; }
            int pad = Mathf.RoundToInt(22 * s);
            cards.Add(new Card { mask = mask, w = ww, h = hh, px = Mathf.RoundToInt(fx * W), py = Mathf.RoundToInt(fy * H),
                                 panelW = ww + 2 * pad, panelH = hh + pad, t0 = t0, t1 = t1, accent = accent });
        }

        // 프레임 픽셀(아래에서 위로)에 합성. t = 영상 시간(초)
        public void Composite(Color32[] frame, float t)
        {
            float s = H / 1080f; int pad = Mathf.RoundToInt(22 * s), bar = Mathf.Max(3, Mathf.RoundToInt(6 * s));
            foreach (var c in cards)
            {
                float a = Mathf.Clamp01((t - c.t0) / 0.5f) * Mathf.Clamp01((c.t1 - t) / 0.5f);
                if (a <= 0) continue;
                float e = a * a * (3 - 2 * a);
                int slide = Mathf.RoundToInt((1 - e) * 40 * s);           // 왼쪽에서 살짝 밀려 들어옴
                int x0 = c.px - pad + slide - (c.accent ? bar + pad / 2 : 0), y0 = c.py - pad / 2;
                int pw = c.panelW + (c.accent ? bar + pad / 2 : 0);
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(H, y0 + c.panelH); y++)
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(W, x0 + pw); x++)
                    {
                        int i = y * W + x; var p = frame[i];
                        float k = 1 - 0.55f * e;                        // 어두운 반투명 판
                        float r = p.r * k, g = p.g * k, b = p.b * k;
                        if (c.accent && x - x0 < bar) { r = Mathf.Lerp(r, Accent.r, e); g = Mathf.Lerp(g, Accent.g, e); b = Mathf.Lerp(b, Accent.b, e); }
                        int mx = x - (c.px + slide), my = (c.h - 1) - (y - c.py);   // 마스크는 위에서 아래
                        if (mx >= 0 && mx < c.w && my >= 0 && my < c.h)
                        {
                            float m = c.mask[(c.h - 1 - my) * c.w + mx] * e;
                            r = Mathf.Lerp(r, 255, m); g = Mathf.Lerp(g, 255, m); b = Mathf.Lerp(b, 255, m);
                        }
                        frame[i] = new Color32((byte)r, (byte)g, (byte)b, 255);
                    }
            }
        }
    }
}
