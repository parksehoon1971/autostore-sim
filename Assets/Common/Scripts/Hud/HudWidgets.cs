// 통합 HUD 에 쓰는 작은 UI Toolkit 요소들: 타임라인 바, 처리량 스파크라인, 아이콘 단추.
// 글꼴에 없는 기호(⏸ 등) 대신 Painter2D 로 직접 그려 어떤 PC 에서도 똑같이 보입니다.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace EquipFX
{
    // 라이트 / 다크 테마 색. 테마를 바꾸면 HUD 를 다시 만들어 새 색을 씀 (EquipHud.SetTheme)
    public static class HudStyle
    {
        public const string PrefTheme = "EquipFX.Hud.Theme";   // 0 다크, 1 라이트
        public static bool Dark = true;

        static Color C(int rgb, float a = 1f) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

        public static Color Panel => Dark ? C(0x16181D, 0.94f) : C(0xFFFFFF, 0.95f);
        public static Color PanelLine => Dark ? C(0xFFFFFF, 0.06f) : C(0x0F172A, 0.08f);
        public static Color Tile => Dark ? C(0xFFFFFF, 0.04f) : C(0x0F172A, 0.035f);
        public static Color TileHover => Dark ? C(0xFFFFFF, 0.08f) : C(0x0F172A, 0.07f);
        public static Color Accent => Dark ? C(0x4C8DFF) : C(0x2563EB);
        public static Color AccentDim => Dark ? C(0x4C8DFF, 0.20f) : C(0x2563EB, 0.12f);
        public static Color OnAccent => C(0xFFFFFF);
        public static Color Text => Dark ? C(0xE7E9EE) : C(0x111827);
        public static Color Strong => Dark ? C(0xFFFFFF) : C(0x030712);
        public static Color Muted => Dark ? C(0x8A93A3) : C(0x6B7280);
        public static Color Track => Dark ? C(0xFFFFFF, 0.12f) : C(0x0F172A, 0.10f);
        public static Color Field => Dark ? C(0x000000, 0.30f) : C(0xF3F4F6);
        public static Color Rec => C(0xEF4444);
        public static readonly Color Ok = new Color(0.16f, 0.80f, 0.45f);
        public static readonly Color Bad = new Color(0.95f, 0.27f, 0.27f);

        public static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        public static void Border(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w; e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c; e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        public static void Pad(VisualElement e, float v, float h)
        {
            e.style.paddingTop = v; e.style.paddingBottom = v; e.style.paddingLeft = h; e.style.paddingRight = h;
        }

        public static void Margin(VisualElement e, float t, float r, float b, float l)
        {
            e.style.marginTop = t; e.style.marginRight = r; e.style.marginBottom = b; e.style.marginLeft = l;
        }

        public static Label MakeLabel(string text, float size, Color color, bool bold = false)
        {
            var l = new Label(text);
            l.style.fontSize = size; l.style.color = color;
            l.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            Margin(l, 0, 0, 0, 0); Pad(l, 0, 0);
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        // 테마 없이도 똑같이 보이도록 직접 꾸민 단추
        public static Button MakeButton(string text, Action onClick, float minWidth = 0)
        {
            var b = new Button(onClick) { text = text, focusable = false };   // Space 키가 단추를 누르지 않도록
            b.style.fontSize = 13; b.style.color = Text;
            b.style.backgroundColor = Tile; Border(b, 0, Color.clear); Radius(b, 8);
            b.style.height = 32; Pad(b, 0, 12); Margin(b, 0, 2, 0, 2);
            if (minWidth > 0) b.style.minWidth = minWidth;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            b.RegisterCallback<PointerEnterEvent>(_ => { if (!b.ClassListContains("on")) b.style.backgroundColor = TileHover; });
            b.RegisterCallback<PointerLeaveEvent>(_ => { if (!b.ClassListContains("on")) b.style.backgroundColor = Tile; });
            return b;
        }

        // 선택 상태 표시 (속도 칩, 카메라 모드, 토글)
        public static void SetOn(Button b, bool on)
        {
            if (on) b.AddToClassList("on"); else b.RemoveFromClassList("on");
            b.style.backgroundColor = on ? Accent : Tile;
            b.style.color = on ? OnAccent : Text;
        }
    }

    // ------------------------------------------------------------------ 아이콘 단추
    public class IconButton : VisualElement
    {
        public enum Kind { Play, Pause, Restart, Back, Forward, Close, Hide }
        Kind kind; bool hover;
        readonly float size;
        public Action clicked;
        public Color color = HudStyle.Text;
        public Color? hoverBg;

        public Kind Icon { get => kind; set { if (kind != value) { kind = value; MarkDirtyRepaint(); } } }

        public IconButton(Kind k, float size, Action onClick, bool primary = false)
        {
            kind = k; this.size = size; clicked = onClick;
            style.width = size; style.height = size; style.flexShrink = 0;
            HudStyle.Radius(this, size / 2);
            HudStyle.Margin(this, 0, 3, 0, 3);
            var bg = primary ? HudStyle.Accent : HudStyle.Tile;
            var bgHover = primary ? Color.Lerp(HudStyle.Accent, Color.white, 0.18f) : HudStyle.TileHover;
            if (primary) color = HudStyle.OnAccent;
            style.backgroundColor = bg;
            RegisterCallback<PointerEnterEvent>(_ => { hover = true; style.backgroundColor = hoverBg ?? bgHover; });
            RegisterCallback<PointerLeaveEvent>(_ => { hover = false; style.backgroundColor = bg; });
            RegisterCallback<ClickEvent>(_ => clicked?.Invoke());
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            float s = size, c = s / 2, u = s * 0.18f;
            p.fillColor = color; p.strokeColor = color;
            switch (kind)
            {
                case Kind.Play:
                    Poly(p, new Vector2(c - u * 0.8f, c - u * 1.15f), new Vector2(c + u * 1.25f, c), new Vector2(c - u * 0.8f, c + u * 1.15f));
                    break;
                case Kind.Pause:
                    Box(p, c - u * 1.0f, c - u * 1.1f, u * 0.7f, u * 2.2f);
                    Box(p, c + u * 0.3f, c - u * 1.1f, u * 0.7f, u * 2.2f);
                    break;
                case Kind.Restart:
                    Box(p, c - u * 1.25f, c - u * 1.0f, u * 0.45f, u * 2.0f);
                    Poly(p, new Vector2(c + u * 1.1f, c - u * 1.0f), new Vector2(c - u * 0.6f, c), new Vector2(c + u * 1.1f, c + u * 1.0f));
                    break;
                case Kind.Back:
                    Poly(p, new Vector2(c, c - u * 0.9f), new Vector2(c - u * 1.2f, c), new Vector2(c, c + u * 0.9f));
                    Poly(p, new Vector2(c + u * 1.2f, c - u * 0.9f), new Vector2(c, c), new Vector2(c + u * 1.2f, c + u * 0.9f));
                    break;
                case Kind.Close:
                    p.lineWidth = Mathf.Max(1.6f, s * 0.06f); p.lineCap = LineCap.Round;
                    p.BeginPath(); p.MoveTo(new Vector2(c - u, c - u)); p.LineTo(new Vector2(c + u, c + u)); p.Stroke();
                    p.BeginPath(); p.MoveTo(new Vector2(c + u, c - u)); p.LineTo(new Vector2(c - u, c + u)); p.Stroke();
                    break;
                case Kind.Hide:   // 눈 + 사선
                    p.lineWidth = Mathf.Max(1.5f, s * 0.055f); p.lineCap = LineCap.Round; p.lineJoin = LineJoin.Round;
                    p.BeginPath(); p.MoveTo(new Vector2(c - u * 1.4f, c));
                    p.BezierCurveTo(new Vector2(c - u * 0.6f, c - u * 1.2f), new Vector2(c + u * 0.6f, c - u * 1.2f), new Vector2(c + u * 1.4f, c));
                    p.BezierCurveTo(new Vector2(c + u * 0.6f, c + u * 1.2f), new Vector2(c - u * 0.6f, c + u * 1.2f), new Vector2(c - u * 1.4f, c));
                    p.Stroke();
                    p.BeginPath(); p.MoveTo(new Vector2(c - u * 1.1f, c + u * 1.1f)); p.LineTo(new Vector2(c + u * 1.1f, c - u * 1.1f)); p.Stroke();
                    break;
                case Kind.Forward:
                    Poly(p, new Vector2(c, c - u * 0.9f), new Vector2(c + u * 1.2f, c), new Vector2(c, c + u * 0.9f));
                    Poly(p, new Vector2(c - u * 1.2f, c - u * 0.9f), new Vector2(c, c), new Vector2(c - u * 1.2f, c + u * 0.9f));
                    break;
            }
        }

        static void Poly(Painter2D p, params Vector2[] pts)
        {
            p.BeginPath(); p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Length; i++) p.LineTo(pts[i]);
            p.ClosePath(); p.Fill();
        }

        static void Box(Painter2D p, float x, float y, float w, float h) =>
            Poly(p, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h));
    }

    // ------------------------------------------------------------------ 타임라인 (0 ~ 30 s, 샷 경계 눈금, 끌어서 이동)
    public class TimelineBar : VisualElement
    {
        public float length = 30f;
        public Action<float> onSeek;          // 손을 뗐을 때 이동할 시각
        public bool Dragging { get; private set; }

        readonly VisualElement track, fill, knob, marks, hoverLine;
        float value, dragValue;
        const float PadX = 8f;

        public TimelineBar()
        {
            style.flexGrow = 1; style.height = 30; style.minWidth = 160;
            pickingMode = PickingMode.Position;

            track = new VisualElement { pickingMode = PickingMode.Ignore };
            track.style.position = Position.Absolute; track.style.left = PadX; track.style.right = PadX;
            track.style.top = 13; track.style.height = 4; track.style.backgroundColor = HudStyle.Track;
            HudStyle.Radius(track, 2);
            Add(track);

            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.style.position = Position.Absolute; fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
            fill.style.backgroundColor = HudStyle.Accent; HudStyle.Radius(fill, 2);
            track.Add(fill);

            marks = new VisualElement { pickingMode = PickingMode.Ignore };
            marks.style.position = Position.Absolute; marks.style.left = 0; marks.style.right = 0; marks.style.top = -6; marks.style.height = 16;
            track.Add(marks);

            hoverLine = new VisualElement { pickingMode = PickingMode.Ignore };
            hoverLine.style.position = Position.Absolute; hoverLine.style.width = 2; hoverLine.style.top = -5; hoverLine.style.height = 14;
            hoverLine.style.backgroundColor = HudStyle.Muted; hoverLine.style.display = DisplayStyle.None;
            track.Add(hoverLine);

            knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.style.position = Position.Absolute; knob.style.width = 14; knob.style.height = 14; knob.style.top = -5;
            knob.style.marginLeft = -7; knob.style.backgroundColor = HudStyle.OnAccent; HudStyle.Radius(knob, 7);
            HudStyle.Border(knob, 2, HudStyle.Accent);
            track.Add(knob);

            RegisterCallback<PointerDownEvent>(e =>
            {
                Dragging = true; this.CapturePointer(e.pointerId);
                dragValue = ToTime(e.localPosition.x); Refresh(dragValue);
                e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                float t = ToTime(e.localPosition.x);
                hoverLine.style.display = DisplayStyle.Flex; hoverLine.style.left = Length.Percent(t / length * 100f);
                if (Dragging && this.HasPointerCapture(e.pointerId)) { dragValue = t; Refresh(dragValue); }
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (!Dragging) return;
                Dragging = false;
                if (this.HasPointerCapture(e.pointerId)) this.ReleasePointer(e.pointerId);
                onSeek?.Invoke(dragValue);
            });
            RegisterCallback<PointerLeaveEvent>(_ => hoverLine.style.display = DisplayStyle.None);
        }

        float ToTime(float x)
        {
            float w = Mathf.Max(1f, layout.width - 2 * PadX);
            return Mathf.Clamp01((x - PadX) / w) * length;
        }

        public void SetValue(float t)
        {
            if (Dragging) return;
            value = t; Refresh(value);
        }

        void Refresh(float t)
        {
            float pct = Mathf.Clamp01(t / length) * 100f;
            fill.style.width = Length.Percent(pct);
            knob.style.left = Length.Percent(pct);
        }

        // 샷 시작 시각 눈금
        public void SetMarks(IList<float> starts)
        {
            marks.Clear();
            if (starts == null) return;
            foreach (var s in starts)
            {
                if (s <= 0.01f || s >= length - 0.01f) continue;
                var m = new VisualElement { pickingMode = PickingMode.Ignore };
                m.style.position = Position.Absolute; m.style.width = 2; m.style.marginLeft = -1;
                m.style.top = 0; m.style.height = 4; m.style.left = Length.Percent(s / length * 100f);
                m.style.backgroundColor = HudStyle.Muted;
                marks.Add(m);
            }
        }
    }

    // ------------------------------------------------------------------ 처리량 스파크라인
    public class Sparkline : VisualElement
    {
        readonly List<float> values = new List<float>();
        public float Max { get; private set; } = 1f;

        public Sparkline()
        {
            style.height = 84; style.flexShrink = 0;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        public void SetValues(IList<float> v)
        {
            values.Clear(); values.AddRange(v);
            float m = 1f; foreach (var x in values) m = Mathf.Max(m, x);
            Max = NiceCeil(m * 1.1f);
            MarkDirtyRepaint();
        }

        static float NiceCeil(float v)
        {
            float e = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(Mathf.Max(v, 1e-3f))));
            foreach (var k in new[] { 1f, 2f, 2.5f, 5f, 10f }) if (k * e >= v) return k * e;
            return 10 * e;
        }

        void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect; float w = r.width, h = r.height;
            if (w < 4 || h < 4) return;
            var p = mgc.painter2D;
            // 기준선 (0, 절반, 최대)
            p.lineWidth = 1f; p.strokeColor = HudStyle.PanelLine;
            for (int i = 0; i <= 2; i++)
            {
                float y = Mathf.Round(h * i / 2f) + 0.5f;
                p.BeginPath(); p.MoveTo(new Vector2(0, y)); p.LineTo(new Vector2(w, y)); p.Stroke();
            }
            int n = values.Count;
            if (n < 2) return;
            Vector2 P(int i) => new Vector2(w * i / (n - 1f), h - 2 - (h - 6) * Mathf.Clamp01(values[i] / Max));
            // 면
            p.fillColor = new Color(HudStyle.Accent.r, HudStyle.Accent.g, HudStyle.Accent.b, 0.14f);
            p.BeginPath(); p.MoveTo(new Vector2(0, h));
            for (int i = 0; i < n; i++) p.LineTo(P(i));
            p.LineTo(new Vector2(w, h)); p.ClosePath(); p.Fill();
            // 선
            p.lineWidth = 2f; p.strokeColor = HudStyle.Accent; p.lineJoin = LineJoin.Round; p.lineCap = LineCap.Round;
            p.BeginPath(); p.MoveTo(P(0));
            for (int i = 1; i < n; i++) p.LineTo(P(i));
            p.Stroke();
        }
    }
}
