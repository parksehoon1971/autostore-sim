// CAD 도면 화면: 지금 배치(건물 · 벽 · 문 · 대기 구역 · 평치 · 설비 · 하역 수단)를 위에서 본 2D 도면으로 그림.
// 북쪽이 위. 휠로 확대·축소, 끌어서 이동, '맞춤' 으로 전체 보기. 배치가 바뀌면 자동으로 다시 그림.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace EquipFX
{
    public class CadView : VisualElement
    {
        LayoutWorld world;
        float k = 1f;                // px / m
        Vector2 origin;              // 도면 왼쪽 위에 오는 월드 점 (x, z 최대)
        bool fitted, dragging;
        Vector2 dragLast;
        readonly List<Label> labels = new List<Label>();
        int labelUsed;
        public System.Action<PlacedModule> clicked;

        static Color Ink => HudStyle.Dark ? new Color(0.88f, 0.90f, 0.94f) : new Color(0.12f, 0.14f, 0.18f);
        static Color Thin => HudStyle.Dark ? new Color(1f, 1f, 1f, 0.10f) : new Color(0f, 0f, 0f, 0.08f);
        static Color Fill => HudStyle.Dark ? new Color(1f, 1f, 1f, 0.04f) : new Color(0f, 0f, 0f, 0.03f);
        static Color Pave => HudStyle.Dark ? new Color(1f, 1f, 1f, 0.07f) : new Color(0f, 0f, 0f, 0.06f);

        public CadView()
        {
            style.flexGrow = 1; style.overflow = Overflow.Hidden;
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => { if (!fitted) Fit(); Refresh(); });
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 && e.button != 2) return;
                dragging = true; dragLast = e.localPosition; this.CapturePointer(e.pointerId);
                if (e.button == 0 && e.clickCount == 1) TryClick(e.localPosition);
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                var d = (Vector2)e.localPosition - dragLast; dragLast = e.localPosition;
                origin += new Vector2(-d.x / k, d.y / k);
                Refresh();
            });
            RegisterCallback<PointerUpEvent>(e => { dragging = false; this.ReleasePointer(e.pointerId); });
        }

        public void Bind(LayoutWorld w) { world = w; fitted = false; Fit(); Refresh(); }
        public bool Bound => world != null;

        // 월드 (x, z) → 화면 px
        Vector2 P(float x, float z) => new Vector2((x - origin.x) * k, (origin.y - z) * k);
        Vector2 P(Vector2 w) => P(w.x, w.y);
        Vector2 W(Vector2 px) => new Vector2(origin.x + px.x / k, origin.y - px.y / k);

        // 건물과 트럭 마당 · 진입로가 다 보이게
        public void Fit()
        {
            var r = contentRect;
            if (world == null || r.width < 10 || r.height < 10) return;
            var s = world.Site;
            float x0 = Mathf.Min(s.x, Plan.Gate.x) - 4, x1 = s.x + s.w + 10, z0 = s.z - 12, z1 = s.z + s.d + 12;
            foreach (var d in s.doors) { var a = Plan.Apron(s, d.side); x0 = Mathf.Min(x0, a.xMin - 2); x1 = Mathf.Max(x1, a.xMax + 2); z0 = Mathf.Min(z0, a.yMin - 2); z1 = Mathf.Max(z1, a.yMax + 2); }
            k = Mathf.Min((r.width - 40) / (x1 - x0), (r.height - 40) / (z1 - z0));
            k = Mathf.Max(k, 0.5f);
            float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
            origin = new Vector2(cx - r.width / 2 / k, cz + r.height / 2 / k);
            fitted = true;
        }

        void OnWheel(WheelEvent e)
        {
            var mp = (Vector2)e.localMousePosition;
            var before = W(mp);
            float f = Mathf.Pow(0.88f, HudInput.WheelNotches(e.delta.y));   // 아래로 굴리면 축소
            k = Mathf.Clamp(k * f, 0.3f, 60f);
            var after = W(mp);
            origin += before - after;
            Refresh();
            e.StopPropagation();
        }

        void TryClick(Vector2 px)
        {
            if (world == null) return;
            var w = W(px);
            foreach (var m in world.modules)
            {
                if (m == null) continue;
                var mn = m.Min; var sz = m.Size;
                if (w.x >= mn.x && w.x <= mn.x + sz.x && w.y >= mn.y && w.y <= mn.y + sz.y) { clicked?.Invoke(m); return; }
            }
        }

        // 이름표는 Label 로 (Painter2D 는 글자를 못 그림)
        public void Refresh()
        {
            labelUsed = 0;
            if (world != null && contentRect.width > 10)
            {
                var s = world.Site;
                Text(P(s.x + s.w / 2, s.z + s.d) + new Vector2(0, -22), $"{s.w:0} m", 11, Ink);
                Text(P(s.x + s.w, s.z + s.d / 2) + new Vector2(26, 0), $"{s.d:0} m", 11, Ink);
                for (int i = 0; i < s.doors.Count; i++)
                {
                    var d = s.doors[i];
                    var p = Plan.DoorPoint(s, d) + Plan.Outward(d.side) * 3f;
                    Text(P(p), Plan.DoorName(s, i), 10, d.kind == 0 ? FloorPlan.InCol : FloorPlan.OutCol);
                }
                if (s.doors.Count > 0)
                {
                    var br = Plan.BulkRect(s);
                    Text(P(br.center), $"평치 구역  {world.Traffic.Stock}/{world.Traffic.StockSlots}", 11, FloorPlan.BulkCol);
                    var yard = new HashSet<int>();
                    foreach (var d in s.doors) if (yard.Add(d.side)) { var a = Plan.Apron(s, d.side); Text(P(a.center), "트럭 주차장", 10, HudStyle.Muted); }
                }
                foreach (var m in world.modules)
                {
                    if (m == null) continue;
                    var c = m.Min + m.Size / 2;
                    bool ok = world.CanHandle(m.entry);
                    Text(P(c), $"{m.entry.name}\n{m.Size.x:0} × {m.Size.y:0} m", 11, ok ? HudStyle.Accent : HudStyle.Bad);
                }
                Text(P(Plan.Gate) + new Vector2(0, -16), "진입로 (입고 · 출고)", 10, HudStyle.Muted);
                Text(new Vector2(70, contentRect.height - 30), "10 m", 10, Ink);
            }
            for (int i = labelUsed; i < labels.Count; i++) labels[i].style.display = DisplayStyle.None;
            MarkDirtyRepaint();
        }

        void Text(Vector2 px, string text, float size, Color c)
        {
            if (px.x < -200 || px.y < -60 || px.x > contentRect.width + 200 || px.y > contentRect.height + 60) return;
            Label l;
            if (labelUsed < labels.Count) l = labels[labelUsed];
            else
            {
                l = new Label { pickingMode = PickingMode.Ignore };
                l.style.position = Position.Absolute; l.style.unityTextAlign = TextAnchor.MiddleCenter;
                l.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                l.style.whiteSpace = WhiteSpace.NoWrap;
                Add(l); labels.Add(l);
            }
            labelUsed++;
            l.style.display = DisplayStyle.Flex;
            l.text = text; l.style.fontSize = size; l.style.color = c;
            l.style.left = px.x; l.style.top = px.y;
        }

        // ---------------------------------------------------------------- 그리기
        void Draw(MeshGenerationContext mgc)
        {
            if (world == null) return;
            var p = mgc.painter2D;
            var s = world.Site;
            p.lineCap = LineCap.Butt; p.lineJoin = LineJoin.Miter;

            // 10 m 격자 (건물 둘레)
            var r = contentRect;
            var wmin = W(new Vector2(0, r.height)); var wmax = W(new Vector2(r.width, 0));
            p.strokeColor = Thin; p.lineWidth = 1f;
            for (float x = Mathf.Floor(wmin.x / 10) * 10; x <= wmax.x; x += 10) Line(p, P(x, wmin.y), P(x, wmax.y));
            for (float z = Mathf.Floor(wmin.y / 10) * 10; z <= wmax.y; z += 10) Line(p, P(wmin.x, z), P(wmax.x, z));

            // 진입로 · 트럭 마당
            p.fillColor = Pave;
            var g = Plan.Gate;
            FillRect(p, Rect.MinMaxRect(g.x - 2, g.y - Plan.RoadHalf, s.x - Plan.AisleDist - Plan.OutLaneOff - 2, g.y + Plan.RoadHalf));
            var sides = new HashSet<int>();
            foreach (var d in s.doors) if (sides.Add(d.side)) FillRect(p, Plan.Apron(s, d.side));

            // 건물 바닥
            p.fillColor = Fill;
            FillRect(p, new Rect(s.x, s.z, s.w, s.d));

            // 대기 구역 · 출고장 · 평치 칸
            foreach (var z in Plan.Zones(s))
            {
                var c = z.kind == 0 ? FloorPlan.InCol : z.kind == 2 ? FloorPlan.OutCol : FloorPlan.BulkCol;
                c.a = 0.75f; p.strokeColor = c; p.lineWidth = 1f;
                foreach (var sl in z.slots)
                {
                    bool fx = Mathf.Abs(sl.face.x) > 0.5f;
                    float hx = fx ? 0.6f : 0.5f, hz = fx ? 0.5f : 0.6f;
                    StrokeRect(p, Rect.MinMaxRect(sl.pos.x - hx, sl.pos.y - hz, sl.pos.x + hx, sl.pos.y + hz));
                }
            }
            foreach (var d in s.doors)
            {
                var c = d.kind == 0 ? FloorPlan.InCol : FloorPlan.OutCol;
                p.strokeColor = c; p.lineWidth = 1.5f;
                Dashed(p, Plan.StageRect(s, d));
            }
            if (s.doors.Count > 0) { p.strokeColor = FloorPlan.BulkCol; p.lineWidth = 1.5f; Dashed(p, Plan.BulkRect(s)); }

            // 기둥 20 m
            p.fillColor = Ink;
            for (float x = s.x + 20; x < s.x + s.w - 3; x += 20)
                for (float z = s.z + 20; z < s.z + s.d - 3; z += 20)
                    FillRect(p, Rect.MinMaxRect(x - 0.35f, z - 0.35f, x + 0.35f, z + 0.35f));

            // 벽 (문 자리 비움) + 문
            p.strokeColor = Ink; p.lineWidth = Mathf.Max(2f, Plan.Wall * k * 1.4f);
            for (int side = 0; side < 4; side++)
            {
                var cuts = new List<float>();
                foreach (var d in s.doors) if (d.side == side) cuts.Add(d.pos);
                cuts.Sort();
                float len = Plan.WallLen(s, side), from = 0;
                foreach (var c in cuts) { WallSeg(p, s, side, from, c - Plan.DoorW / 2); from = c + Plan.DoorW / 2; }
                WallSeg(p, s, side, from, len);
            }
            foreach (var d in s.doors)
            {
                var c = Plan.DoorPoint(s, d); var n = Plan.Outward(d.side); var tg = new Vector2(-n.y, n.x);
                p.strokeColor = d.kind == 0 ? FloorPlan.InCol : FloorPlan.OutCol; p.lineWidth = 3f;
                Line(p, P(c - tg * Plan.DoorW / 2), P(c + tg * Plan.DoorW / 2));
                // 트럭 자리
                p.lineWidth = 1f;
                var e = c + n * Plan.BayLen; float hw = Plan.DoorW / 2;
                Line(p, P(c + tg * hw), P(e + tg * hw)); Line(p, P(c - tg * hw), P(e - tg * hw));
            }

            // 설비
            foreach (var m in world.modules)
            {
                if (m == null) continue;
                bool ok = world.CanHandle(m.entry);
                var c = ok ? HudStyle.Accent : HudStyle.Bad;
                var rr = new Rect(m.Min, m.Size);
                var f = c; f.a = 0.14f; p.fillColor = f; FillRect(p, rr);
                p.strokeColor = c; p.lineWidth = world.Selected == m ? 3f : 1.6f; StrokeRect(p, rr);
            }

            // 하역 수단
            foreach (var mv in world.Traffic.movers)
            {
                var q = P(mv.t.position.x, mv.t.position.z);
                p.fillColor = mv.kind == MoverInfo.Forklift ? new Color(0.98f, 0.72f, 0.10f) : new Color(1f, 0.45f, 0.10f);
                float rad = Mathf.Max(3f, (mv.kind == MoverInfo.Forklift ? 0.9f : 0.4f) * k);
                p.BeginPath(); p.Arc(q, rad, 0f, 360f); p.Fill();
            }

            // 치수선
            p.strokeColor = Ink; p.lineWidth = 1f;
            var a0 = P(s.x, s.z + s.d) + new Vector2(0, -12); var a1 = P(s.x + s.w, s.z + s.d) + new Vector2(0, -12);
            Line(p, a0, a1); Line(p, a0 + new Vector2(0, -5), a0 + new Vector2(0, 5)); Line(p, a1 + new Vector2(0, -5), a1 + new Vector2(0, 5));
            var b0 = P(s.x + s.w, s.z + s.d) + new Vector2(12, 0); var b1 = P(s.x + s.w, s.z) + new Vector2(12, 0);
            Line(p, b0, b1); Line(p, b0 + new Vector2(-5, 0), b0 + new Vector2(5, 0)); Line(p, b1 + new Vector2(-5, 0), b1 + new Vector2(5, 0));

            // 축척 막대 10 m · 북쪽 표시
            var sb = new Vector2(20, r.height - 18);
            p.lineWidth = 3f; Line(p, sb, sb + new Vector2(10 * k, 0));
            var nc = new Vector2(r.width - 28, 34);
            p.fillColor = Ink;
            p.BeginPath(); p.MoveTo(nc + new Vector2(0, -14)); p.LineTo(nc + new Vector2(7, 8)); p.LineTo(nc + new Vector2(0, 3)); p.LineTo(nc + new Vector2(-7, 8)); p.ClosePath(); p.Fill();
        }

        void WallSeg(Painter2D p, SiteDef s, int side, float a, float b)
        {
            if (b - a < 0.05f) return;
            Vector2 u, v;
            switch (side)
            {
                case 0: u = new Vector2(s.x, s.z + a); v = new Vector2(s.x, s.z + b); break;
                case 1: u = new Vector2(s.x + s.w, s.z + a); v = new Vector2(s.x + s.w, s.z + b); break;
                case 2: u = new Vector2(s.x + a, s.z); v = new Vector2(s.x + b, s.z); break;
                default: u = new Vector2(s.x + a, s.z + s.d); v = new Vector2(s.x + b, s.z + s.d); break;
            }
            Line(p, P(u), P(v));
        }

        static void Line(Painter2D p, Vector2 a, Vector2 b) { p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke(); }

        void FillRect(Painter2D p, Rect w)
        {
            var a = P(w.xMin, w.yMax); var b = P(w.xMax, w.yMin);
            p.BeginPath(); p.MoveTo(a); p.LineTo(new Vector2(b.x, a.y)); p.LineTo(b); p.LineTo(new Vector2(a.x, b.y)); p.ClosePath(); p.Fill();
        }

        void StrokeRect(Painter2D p, Rect w)
        {
            var a = P(w.xMin, w.yMax); var b = P(w.xMax, w.yMin);
            p.BeginPath(); p.MoveTo(a); p.LineTo(new Vector2(b.x, a.y)); p.LineTo(b); p.LineTo(new Vector2(a.x, b.y)); p.ClosePath(); p.Stroke();
        }

        void Dashed(Painter2D p, Rect w)
        {
            var c = new[] { P(w.xMin, w.yMax), P(w.xMax, w.yMax), P(w.xMax, w.yMin), P(w.xMin, w.yMin) };
            for (int i = 0; i < 4; i++)
            {
                var a = c[i]; var b = c[(i + 1) % 4];
                float len = (b - a).magnitude; if (len < 1f) continue;
                var d = (b - a) / len;
                for (float t = 0; t < len; t += 9f) Line(p, a + d * t, a + d * Mathf.Min(len, t + 5f));
            }
        }
    }
}
