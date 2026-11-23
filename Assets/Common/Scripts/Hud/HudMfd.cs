// 물류흐름도(MFD, Material Flow Diagram) 화면: 배치한 출입문 · 하역 수단 · 설비로부터 자동으로 만들어지는 흐름 그래프.
// 열(단계)마다 상자를 세로로 놓고, 상자 사이를 곡선 화살표로 잇습니다. 값(처리 수 · 처리량)은 매번 갱신.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace EquipFX
{
    public class MfdNode
    {
        public string id, title, sub;
        public int col;
        public Color color;
        public bool dim;              // 흐름에 참여하지 않음 (처리 불가 · 수단 없음)
    }

    public class MfdEdge
    {
        public string a, b;
        public Color color;
        public bool dashed;
        public string label;          // 화살표 가운데 글자 (화물 · 물동량)
    }

    public class MfdView : VisualElement
    {
        const float MaxW = 168f, MinW = 96f, MaxH = 54f, MinH = 30f, MinGapX = 30f, Pad = 24f;
        float nodeW = MaxW, nodeH = MaxH;
        readonly List<MfdNode> nodes = new List<MfdNode>();
        readonly List<MfdEdge> edges = new List<MfdEdge>();
        readonly Dictionary<string, VisualElement> boxes = new Dictionary<string, VisualElement>();
        readonly Dictionary<string, Label> subs = new Dictionary<string, Label>();
        readonly Dictionary<string, Label> edgeLabels = new Dictionary<string, Label>();
        public System.Action<string> clicked;

        public MfdView()
        {
            style.flexGrow = 1;
            generateVisualContent += DrawEdges;
            RegisterCallback<GeometryChangedEvent>(_ => Arrange());
        }

        public void SetGraph(List<MfdNode> ns, List<MfdEdge> es)
        {
            nodes.Clear(); nodes.AddRange(ns);
            edges.Clear(); edges.AddRange(es);
            Clear(); boxes.Clear(); subs.Clear(); edgeLabels.Clear();
            foreach (var n in nodes)
            {
                var b = new VisualElement();
                b.style.position = Position.Absolute; b.style.width = nodeW; b.style.height = nodeH;
                b.style.backgroundColor = HudStyle.Dark ? new Color(0.13f, 0.14f, 0.17f, 1f) : Color.white;
                HudStyle.Radius(b, 10); HudStyle.Border(b, 1, HudStyle.PanelLine);
                b.style.borderLeftWidth = 4; b.style.borderLeftColor = n.dim ? HudStyle.Muted : n.color;
                HudStyle.Pad(b, 7, 10);
                b.style.opacity = n.dim ? 0.55f : 1f;
                var t = HudStyle.MakeLabel(n.title, 12, HudStyle.Strong, true);
                t.style.overflow = Overflow.Hidden; t.style.textOverflow = TextOverflow.Ellipsis; t.style.whiteSpace = WhiteSpace.NoWrap;
                var s = HudStyle.MakeLabel(n.sub ?? "", 11, HudStyle.Muted);
                s.style.overflow = Overflow.Hidden; s.style.textOverflow = TextOverflow.Ellipsis; s.style.whiteSpace = WhiteSpace.NoWrap;
                HudStyle.Margin(s, 3, 0, 0, 0);
                b.Add(t); b.Add(s);
                var id = n.id;
                b.RegisterCallback<ClickEvent>(_ => clicked?.Invoke(id));
                b.RegisterCallback<PointerEnterEvent>(_ => b.style.backgroundColor = HudStyle.Dark ? new Color(0.17f, 0.19f, 0.23f, 1f) : new Color(0.96f, 0.97f, 0.99f));
                b.RegisterCallback<PointerLeaveEvent>(_ => b.style.backgroundColor = HudStyle.Dark ? new Color(0.13f, 0.14f, 0.17f, 1f) : Color.white);
                Add(b);
                boxes[n.id] = b; subs[n.id] = s;
            }
            // 화살표 글자는 상자 위에 오도록 나중에 추가
            foreach (var e in edges)
            {
                if (e.label == null) continue;
                var l = HudStyle.MakeLabel(e.label, 10, HudStyle.Text);
                l.pickingMode = PickingMode.Ignore;
                l.style.position = Position.Absolute;
                l.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                l.style.backgroundColor = HudStyle.Dark ? new Color(0.09f, 0.10f, 0.12f, 0.92f) : new Color(1f, 1f, 1f, 0.92f);
                HudStyle.Pad(l, 1, 5); HudStyle.Radius(l, 6);
                l.style.whiteSpace = WhiteSpace.NoWrap;
                var c = e.color; c.a = 0.8f; HudStyle.Border(l, 1, c);
                Add(l);
                edgeLabels[e.a + ">" + e.b] = l;
            }
            Arrange();
        }

        public void SetEdgeLabel(string a, string b, string text)
        {
            if (edgeLabels.TryGetValue(a + ">" + b, out var l) && l.text != text) l.text = text;
        }

        public void SetSub(string id, string text)
        {
            if (subs.TryGetValue(id, out var l) && l.text != text) l.text = text;
        }

        // 열마다 고르게, 열 안에서는 세로 가운데 정렬. 화면이 좁거나 상자가 많으면 상자를 줄임
        void Arrange()
        {
            var r = contentRect;
            if (r.width <= 0 || nodes.Count == 0) { MarkDirtyRepaint(); return; }
            int cols = 0; foreach (var n in nodes) cols = Mathf.Max(cols, n.col + 1);
            var perCol = new Dictionary<int, List<MfdNode>>();
            int most = 1;
            foreach (var n in nodes)
            {
                if (!perCol.TryGetValue(n.col, out var l)) perCol[n.col] = l = new List<MfdNode>();
                l.Add(n); most = Mathf.Max(most, l.Count);
            }
            nodeW = Mathf.Clamp((r.width - Pad * 2 - (cols - 1) * MinGapX) / Mathf.Max(1, cols), MinW, MaxW);
            float gapY = Mathf.Clamp((r.height - Pad * 2 - most * MaxH) / Mathf.Max(1, most - 1), 6f, 16f);
            nodeH = Mathf.Clamp((r.height - Pad * 2 - (most - 1) * gapY) / most, MinH, MaxH);
            bool showSub = nodeH >= 44f;
            float usable = Mathf.Max(0, r.width - Pad * 2 - nodeW);
            foreach (var kv in perCol)
            {
                float x = Pad + (cols > 1 ? usable * kv.Key / (cols - 1) : usable / 2);
                float total = kv.Value.Count * nodeH + (kv.Value.Count - 1) * gapY;
                float y0 = Mathf.Max(Pad, (r.height - total) / 2);
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var b = boxes[kv.Value[i].id];
                    b.style.left = x; b.style.top = y0 + i * (nodeH + gapY);
                    b.style.width = nodeW; b.style.height = nodeH;
                    if (subs.TryGetValue(kv.Value[i].id, out var sl)) sl.style.display = showSub ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
            // 화살표 가운데 (곡선의 중간점 = 두 열 사이 가운데, 두 상자 높이의 가운데)
            foreach (var e in edges)
            {
                if (!edgeLabels.TryGetValue(e.a + ">" + e.b, out var l)) continue;
                if (!boxes.TryGetValue(e.a, out var ba) || !boxes.TryGetValue(e.b, out var bb)) continue;
                float ax = ba.style.left.value.value + nodeW, ay = ba.style.top.value.value + nodeH / 2;
                float bx = bb.style.left.value.value, by = bb.style.top.value.value + nodeH / 2;
                l.style.left = (ax + bx) / 2; l.style.top = (ay + by) / 2;
                l.style.display = bx > ax + 20 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            MarkDirtyRepaint();
        }

        void DrawEdges(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            foreach (var e in edges)
            {
                if (!boxes.TryGetValue(e.a, out var a) || !boxes.TryGetValue(e.b, out var b)) continue;
                float ax = a.style.left.value.value + nodeW, ay = a.style.top.value.value + nodeH / 2;
                float bx = b.style.left.value.value, by = b.style.top.value.value + nodeH / 2;
                if (float.IsNaN(ax) || float.IsNaN(bx) || bx <= ax) continue;
                var c = e.color; c.a = e.dashed ? 0.55f : 0.9f;
                p.strokeColor = c; p.lineWidth = 2f; p.lineCap = LineCap.Round;
                float mx = (ax + bx) / 2;
                var p0 = new Vector2(ax, ay); var p1 = new Vector2(mx, ay); var p2 = new Vector2(mx, by); var p3 = new Vector2(bx - 6, by);
                if (e.dashed)
                {
                    // 점선: 곡선을 잘게 나눠 번갈아 그림
                    Vector2 prev = p0;
                    for (int i = 1; i <= 24; i++)
                    {
                        var q = Bez(p0, p1, p2, p3, i / 24f);
                        if (i % 2 == 1) { p.BeginPath(); p.MoveTo(prev); p.LineTo(q); p.Stroke(); }
                        prev = q;
                    }
                }
                else
                {
                    p.BeginPath(); p.MoveTo(p0); p.BezierCurveTo(p1, p2, p3); p.Stroke();
                }
                p.fillColor = c;
                p.BeginPath(); p.MoveTo(new Vector2(bx, by)); p.LineTo(new Vector2(bx - 8, by - 5)); p.LineTo(new Vector2(bx - 8, by + 5)); p.ClosePath(); p.Fill();
            }
        }

        static Vector2 Bez(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }
    }
}
