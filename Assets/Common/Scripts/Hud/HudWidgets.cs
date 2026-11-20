// 통합 HUD 에 쓰는 작은 UI Toolkit 요소들: 타임라인 바, 처리량 스파크라인, 아이콘 단추.
// 글꼴에 없는 기호(⏸ 등) 대신 Painter2D 로 직접 그려 어떤 PC 에서도 똑같이 보입니다.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace EquipFX
{
    public static class HudStyle
    {
        public static readonly Color Panel = new Color(0.055f, 0.07f, 0.09f, 0.86f);
        public static readonly Color PanelLine = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Tile = new Color(1f, 1f, 1f, 0.045f);
        public static readonly Color TileHover = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color Accent = new Color(0.25f, 0.78f, 1f);
        public static readonly Color AccentDim = new Color(0.25f, 0.78f, 1f, 0.22f);
        public static readonly Color Text = new Color(0.91f, 0.93f, 0.95f);
        public static readonly Color Muted = new Color(0.58f, 0.63f, 0.69f);
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Rec = new Color(1f, 0.33f, 0.33f);

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
            b.style.backgroundColor = Tile; Border(b, 1, PanelLine); Radius(b, 6);
            b.style.height = 30; Pad(b, 0, 10); Margin(b, 0, 3, 0, 3);
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
            b.style.backgroundColor = on ? AccentDim : Tile;
            b.style.color = on ? Color.white : Text;
            Border(b, 1, on ? new Color(Accent.r, Accent.g, Accent.b, 0.7f) : PanelLine);
        }
    }

    // ------------------------------------------------------------------ 아이콘 단추
    public class IconButton : VisualElement
    {
        public enum Kind { Play, Pause, Restart, Back, Forward }
        Kind kind; bool hover;
        readonly float size;
        public Action clicked;
        public Color color = HudStyle.Text;

        public Kind Icon { get => kind; set { if (kind != value) { kind = value; MarkDirtyRepaint(); } } }

        public IconButton(Kind k, float size, Action onClick, bool primary = false)
        {
            kind = k; this.size = size; clicked = onClick;
            style.width = size; style.height = size; style.flexShrink = 0;
            HudStyle.Radius(this, size / 2);
            HudStyle.Margin(this, 0, 3, 0, 3);
            var bg = primary ? HudStyle.Accent : HudStyle.Tile;
            var bgHover = primary ? new Color(0.45f, 0.85f, 1f) : HudStyle.TileHover;
            if (primary) color = new Color(0.03f, 0.08f, 0.12f);
            style.backgroundColor = bg;
            RegisterCallback<PointerEnterEvent>(_ => { hover = true; style.backgroundColor = bgHover; });
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
            hoverLine.style.backgroundColor = new Color(1, 1, 1, 0.35f); hoverLine.style.display = DisplayStyle.None;
            track.Add(hoverLine);

            knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.style.position = Position.Absolute; knob.style.width = 14; knob.style.height = 14; knob.style.top = -5;
            knob.style.marginLeft = -7; knob.style.backgroundColor = Color.white; HudStyle.Radius(knob, 7);
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
                m.style.backgroundColor = new Color(1, 1, 1, 0.5f);
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
            p.lineWidth = 1f; p.strokeColor = new Color(1, 1, 1, 0.07f);
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
