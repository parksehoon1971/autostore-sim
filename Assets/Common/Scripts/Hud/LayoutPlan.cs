// 배치 게임 메인 공간의 '도면'.
//  - 물류센터 건물: 사용자가 바닥을 끌어 그린 직사각형. 바닥(슬래브)은 트럭 상차 높이(도크 높이)만큼 올라가 있음
//  - 벽체·기둥·기둥 중심선·치수선을 슬래브 위에 CAD 도면처럼 그림
//  - 입고 / 출고 포인트(부지 바깥 트럭 진입·출차 지점)와 건물이 만나는 벽에 도크 출입문. 문은 끌어서 옮기거나 속성 패널에서 개수·위치 수정
//  - 도크 대기 구역에 선택한 화물을 쌓아 보여 주고, 화물을 처리할 수 있는 설비마다 입고 → 설비 → 출고 동선을 움직이는 화살표 띠로 표시
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace EquipFX
{
    [Serializable]
    public class DoorDef
    {
        public int kind;      // 0 입고, 1 출고
        public int side;      // 0 서, 1 동, 2 남, 3 북
        public float pos;     // 벽의 최소 모서리부터 문 가운데까지 (m)
    }

    [Serializable]
    public class SiteDef
    {
        public float x = 0f, z = 0f, w = 140f, d = 80f;
        public float h = 1.2f;            // 바닥 높이 = 트럭 상차 높이 (m)
        public bool drawn;                // 사용자가 영역을 그렸는지
        public List<DoorDef> doors = new List<DoorDef>();

        public SiteDef Clone()
        {
            var c = (SiteDef)MemberwiseClone();
            c.doors = new List<DoorDef>();
            foreach (var d0 in doors) c.doors.Add(new DoorDef { kind = d0.kind, side = d0.side, pos = d0.pos });
            return c;
        }
    }

    public static class Plan
    {
        public const float Wall = 0.35f;
        public const float DoorW = 3.6f;                    // 도크 문 폭
        public const float DoorSpacing = 6f;                // 문 사이 기본 간격
        public const float StageDepth = 6f;                 // 문 안쪽 대기 구역 깊이 (설비 금지)
        public const float BayLen = 16f;                    // 문 밖 트럭 자리 길이
        public const float Margin = 1f;                     // 벽과 설비 사이 최소 간격
        public const float MinSize = 20f;
        // 부지 바깥의 트럭 진입 / 출차 지점 (고정)
        public static readonly Vector2 InPoint = new Vector2(-60f, 40f);
        public static readonly Vector2 OutPoint = new Vector2(220f, 40f);
        // 건물을 그릴 수 있는 범위 (입·출고 지점과 바닥 끝에서 떨어지게)
        public static readonly Rect Allowed = new Rect(-36f, -50f, 232f, 180f);
        public static readonly string[] SideNames = { "서쪽", "동쪽", "남쪽", "북쪽" };

        public static float WallLen(SiteDef s, int side) => side < 2 ? s.d : s.w;

        public static Vector2 Outward(int side) => side switch { 0 => Vector2.left, 1 => Vector2.right, 2 => Vector2.down, _ => Vector2.up };

        // 문 가운데의 벽 위 점 (바깥 벽면)
        public static Vector2 DoorPoint(SiteDef s, DoorDef d) => d.side switch
        {
            0 => new Vector2(s.x, s.z + d.pos),
            1 => new Vector2(s.x + s.w, s.z + d.pos),
            2 => new Vector2(s.x + d.pos, s.z),
            _ => new Vector2(s.x + d.pos, s.z + s.d),
        };

        public static Vector2 DoorInside(SiteDef s, DoorDef d) => DoorPoint(s, d) - Outward(d.side) * (Wall + 0.2f);

        public static Rect StageRect(SiteDef s, DoorDef d)
        {
            var p = DoorInside(s, d); var n = -Outward(d.side);
            float half = DoorW / 2 + 1f;
            var a = p + n * StageDepth;
            float x0 = Mathf.Min(p.x, a.x), x1 = Mathf.Max(p.x, a.x), z0 = Mathf.Min(p.y, a.y), z1 = Mathf.Max(p.y, a.y);
            if (d.side < 2) { z0 = p.y - half; z1 = p.y + half; } else { x0 = p.x - half; x1 = p.x + half; }
            return Rect.MinMaxRect(x0, z0, x1, z1);
        }

        public static bool Inside(SiteDef s, Vector2 min, Vector2 size) =>
            min.x >= s.x + Wall + Margin && min.y >= s.z + Wall + Margin &&
            min.x + size.x <= s.x + s.w - Wall - Margin && min.y + size.y <= s.z + s.d - Wall - Margin;

        public static bool HitsStage(SiteDef s, Vector2 min, Vector2 size, float gap)
        {
            foreach (var d in s.doors)
            {
                var r = StageRect(s, d);
                if (min.x < r.xMax + gap && r.xMin < min.x + size.x + gap && min.y < r.yMax + gap && r.yMin < min.y + size.y + gap) return true;
            }
            return false;
        }

        // 건물 가운데에서 본 방향으로 어느 벽이 그 점을 마주하는지
        public static int FacingSide(SiteDef s, Vector2 p)
        {
            var c = new Vector2(s.x + s.w / 2, s.z + s.d / 2);
            var v = p - c;
            if (Mathf.Abs(v.x) / s.w >= Mathf.Abs(v.y) / s.d) return v.x < 0 ? 0 : 1;
            return v.y < 0 ? 2 : 3;
        }

        // 한 종류의 문을 n 개로, 벽 가운데를 중심으로 고르게
        public static void SetDoorCount(SiteDef s, int kind, int n)
        {
            n = Mathf.Clamp(n, 0, 12);
            int side = -1;
            foreach (var d in s.doors) if (d.kind == kind) { side = d.side; break; }
            if (side < 0) side = FacingSide(s, kind == 0 ? InPoint : OutPoint);
            s.doors.RemoveAll(d => d.kind == kind);
            float len = WallLen(s, side);
            float span = (n - 1) * DoorSpacing;
            for (int i = 0; i < n; i++)
                s.doors.Add(new DoorDef { kind = kind, side = side, pos = ClampPos(s, side, len / 2 - span / 2 + i * DoorSpacing) });
        }

        public static float ClampPos(SiteDef s, int side, float pos) => Mathf.Clamp(pos, DoorW / 2 + 1.5f, WallLen(s, side) - DoorW / 2 - 1.5f);

        public static void DefaultDoors(SiteDef s)
        {
            s.doors.Clear();
            SetDoorCount(s, 0, 2);
            SetDoorCount(s, 1, 2);
        }

        // 마우스가 가리키는 점에 가장 가까운 벽 위 자리
        public static void NearestWall(SiteDef s, Vector2 p, out int side, out float pos)
        {
            float dw = Mathf.Abs(p.x - s.x), de = Mathf.Abs(p.x - (s.x + s.w)), ds = Mathf.Abs(p.y - s.z), dn = Mathf.Abs(p.y - (s.z + s.d));
            float m = Mathf.Min(Mathf.Min(dw, de), Mathf.Min(ds, dn));
            side = m == dw ? 0 : m == de ? 1 : m == ds ? 2 : 3;
            pos = ClampPos(s, side, side < 2 ? p.y - s.z : p.x - s.x);
        }

        public static string DoorName(SiteDef s, int index)
        {
            var d = s.doors[index]; int n = 0;
            for (int i = 0; i <= index; i++) if (s.doors[i].kind == d.kind) n++;
            return (d.kind == 0 ? "입고문 " : "출고문 ") + n;
        }
    }

    // 평평한 사각형들을 한 메시로 모아 그림 (색마다 한 메시)
    class FlatMesh
    {
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int> tri = new List<int>();
        public readonly GameObject go;
        readonly Mesh mesh;
        public readonly Material mat;

        public FlatMesh(string name, Transform parent, Color c, Texture tex = null)
        {
            go = new GameObject(name); go.transform.SetParent(parent, false);
            mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name };
            mat.SetColor("_BaseColor", c);
            if (tex != null) mat.SetTexture("_BaseMap", tex);
            r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        }

        public void Clear() { v.Clear(); uv.Clear(); tri.Clear(); }

        public void Quad(float x0, float z0, float x1, float z1, float y)
        {
            if (x1 < x0) { var t = x0; x0 = x1; x1 = t; }
            if (z1 < z0) { var t = z0; z0 = z1; z1 = t; }
            int b = v.Count;
            v.Add(new Vector3(x0, y, z0)); v.Add(new Vector3(x0, y, z1)); v.Add(new Vector3(x1, y, z1)); v.Add(new Vector3(x1, y, z0));
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(1, 0));
            tri.Add(b); tri.Add(b + 1); tri.Add(b + 2); tri.Add(b); tri.Add(b + 2); tri.Add(b + 3);
        }

        public void Outline(Rect r, float lw, float y)
        {
            Quad(r.xMin, r.yMin, r.xMax, r.yMin + lw, y); Quad(r.xMin, r.yMax - lw, r.xMax, r.yMax, y);
            Quad(r.xMin, r.yMin, r.xMin + lw, r.yMax, y); Quad(r.xMax - lw, r.yMin, r.xMax, r.yMax, y);
        }

        // a → b 띠 (u 는 거리 / tile, 텍스처가 진행 방향을 가리키도록)
        public void Band(Vector2 a, Vector2 b, float width, float y, float tile, float u0)
        {
            var d = b - a; float len = d.magnitude; if (len < 0.01f) return;
            d /= len; var n = new Vector2(-d.y, d.x) * (width / 2);
            int i = v.Count;
            v.Add(new Vector3(a.x - n.x, y, a.y - n.y)); v.Add(new Vector3(a.x + n.x, y, a.y + n.y));
            v.Add(new Vector3(b.x + n.x, y, b.y + n.y)); v.Add(new Vector3(b.x - n.x, y, b.y - n.y));
            float u1 = u0 + len / tile;
            uv.Add(new Vector2(u0, 0)); uv.Add(new Vector2(u0, 1)); uv.Add(new Vector2(u1, 1)); uv.Add(new Vector2(u1, 0));
            tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
        }

        public void Dashed(Vector2 a, Vector2 b, float width, float y, float dash, float gap)
        {
            var d = b - a; float len = d.magnitude; if (len < 0.01f) return;
            d /= len;
            for (float s = 0; s < len; s += dash + gap) Band(a + d * s, a + d * Mathf.Min(len, s + dash), width, y, 1f, 0f);
        }

        public void Apply()
        {
            mesh.Clear();
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
        }
    }

    public class FloorPlan
    {
        public static readonly Color InCol = new Color(0.10f, 0.62f, 0.38f);
        public static readonly Color OutCol = new Color(0.93f, 0.50f, 0.10f);

        readonly Transform root, cargoRoot;
        readonly GameObject slab;
        readonly Material slabMat, edgeMat;
        readonly FlatMesh walls, axes, dockIn, dockOut, ground, selMesh, flowIn, flowOut;
        float flowSig = float.NaN;
        string drawnSig, cargoSig;
        readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        public FloorPlan(Transform parent, Texture gridTex)
        {
            root = new GameObject("FloorPlan").transform; root.SetParent(parent, false);
            cargoRoot = new GameObject("DockCargo").transform; cargoRoot.SetParent(root, false);
            // 올린 바닥 (콘크리트 슬래브)
            slab = GameObject.CreatePrimitive(PrimitiveType.Cube); slab.name = "CenterSlab";
            Object.Destroy(slab.GetComponent<Collider>());
            slab.transform.SetParent(root, false);
            slabMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Slab" };
            slabMat.SetColor("_BaseColor", new Color(0.93f, 0.93f, 0.92f)); slabMat.SetFloat("_Smoothness", 0.3f);
            if (gridTex != null) slabMat.SetTexture("_BaseMap", gridTex);
            slab.GetComponent<MeshRenderer>().sharedMaterial = slabMat;
            edgeMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "SlabEdge" };
            edgeMat.SetColor("_BaseColor", new Color(0.55f, 0.56f, 0.58f));

            var ink = new Color(0.20f, 0.22f, 0.26f);
            ground = new FlatMesh("SiteMarks", root, new Color(0.40f, 0.42f, 0.46f));
            walls = new FlatMesh("Walls", root, ink);
            axes = new FlatMesh("ColumnAxes", root, new Color(0.45f, 0.55f, 0.70f));
            dockIn = new FlatMesh("InDocks", root, InCol);
            dockOut = new FlatMesh("OutDocks", root, OutCol);
            selMesh = new FlatMesh("DoorSelection", root, HudStyle.Accent);
            flowIn = new FlatMesh("FlowIn", root, Color.white, ArrowTexture(InCol));
            flowOut = new FlatMesh("FlowOut", root, Color.white, ArrowTexture(OutCol));
        }

        static string Sig(SiteDef s)
        {
            var b = new System.Text.StringBuilder();
            b.Append(s.x).Append(',').Append(s.z).Append(',').Append(s.w).Append(',').Append(s.d).Append(',').Append(s.h);
            foreach (var d in s.doors) b.Append('|').Append(d.kind).Append(d.side).Append(':').Append(d.pos.ToString("0.0"));
            return b.ToString();
        }

        // ------------------------------------------------------------------ 도면 (건물이 바뀔 때만 다시 그림)
        public void UpdateDrawing(SiteDef s, int selectedDoor)
        {
            string sig = Sig(s) + "#" + selectedDoor;
            if (sig == drawnSig) return;
            drawnSig = sig;
            float h = s.h, yW = h + 0.006f, yA = h + 0.004f, yM = h + 0.008f;
            float x0 = s.x, z0 = s.z, x1 = s.x + s.w, z1 = s.z + s.d, t = Plan.Wall;

            slab.transform.position = new Vector3(x0 + s.w / 2, h / 2, z0 + s.d / 2);
            slab.transform.localScale = new Vector3(s.w, h, s.d);
            slabMat.SetTextureScale("_BaseMap", new Vector2(s.w / 10f, s.d / 10f));

            // 벽체: 문 자리는 비움
            walls.Clear();
            for (int side = 0; side < 4; side++)
            {
                var cuts = new List<float>();
                foreach (var d in s.doors) if (d.side == side) cuts.Add(d.pos);
                cuts.Sort();
                float len = Plan.WallLen(s, side), from = 0;
                foreach (var c in cuts) { WallPiece(s, side, from, c - Plan.DoorW / 2, yW); from = c + Plan.DoorW / 2; }
                WallPiece(s, side, from, len, yW);
            }
            // 기둥 20 m 간격
            for (float x = x0 + 20; x < x1 - 3; x += 20)
                for (float z = z0 + 20; z < z1 - 3; z += 20)
                    walls.Quad(x - 0.3f, z - 0.3f, x + 0.3f, z + 0.3f, yW);
            walls.Apply();

            // 기둥 중심선
            axes.Clear();
            for (float x = x0 + 20; x < x1 - 3; x += 20) axes.Dashed(new Vector2(x, z0 + t), new Vector2(x, z1 - t), 0.05f, yA, 1.6f, 0.6f);
            for (float z = z0 + 20; z < z1 - 3; z += 20) axes.Dashed(new Vector2(x0 + t, z), new Vector2(x1 - t, z), 0.05f, yA, 1.6f, 0.6f);
            axes.Apply();

            // 바닥(땅) 표시: 치수선, 트럭 자리, 입·출고 지점
            ground.Clear();
            const float yG = 0.01f;
            ground.Quad(x0, z0 - 4.04f, x1, z0 - 3.96f, yG);
            ground.Quad(x0 - 4.04f, z0, x0 - 3.96f, z1, yG);
            for (float x = x0; x <= x1 + 0.01f; x += 10) ground.Quad(x - 0.03f, z0 - 4.6f, x + 0.03f, z0 - 3.4f, yG);
            for (float z = z0; z <= z1 + 0.01f; z += 10) ground.Quad(x0 - 4.6f, z - 0.03f, x0 - 3.4f, z + 0.03f, yG);
            ground.Apply();

            dockIn.Clear(); dockOut.Clear(); selMesh.Clear();
            for (int i = 0; i < s.doors.Count; i++)
            {
                var d = s.doors[i];
                var m = d.kind == 0 ? dockIn : dockOut;
                var p = Plan.DoorPoint(s, d); var n = Plan.Outward(d.side); var tg = new Vector2(-n.y, n.x);
                float hw = Plan.DoorW / 2;
                // 문 밖 트럭 자리 (두 줄 + 정지선) — 땅 높이
                var e = p + n * Plan.BayLen;
                m.Band(p + tg * hw, e + tg * hw, 0.15f, yG, 1f, 0f);
                m.Band(p - tg * hw, e - tg * hw, 0.15f, yG, 1f, 0f);
                m.Band(p + n * 0.6f + tg * hw, p + n * 0.6f - tg * hw, 0.25f, yG, 1f, 0f);
                // 문틀 (벽 끝 두 점)
                m.Band(p + tg * (hw + 0.25f) - n * t, p + tg * (hw + 0.25f), 0.5f, yM, 1f, 0f);
                m.Band(p - tg * (hw + 0.25f) - n * t, p - tg * (hw + 0.25f), 0.5f, yM, 1f, 0f);
                // 안쪽 대기 구역 테두리
                m.Outline(Plan.StageRect(s, d), 0.1f, yM);
                if (i == selectedDoor)
                {
                    var r = Plan.StageRect(s, d);
                    r.xMin -= 0.4f; r.yMin -= 0.4f; r.xMax += 0.4f; r.yMax += 0.4f;
                    selMesh.Outline(r, 0.18f, yM + 0.002f);
                }
            }
            // 입·출고 지점 표시 (땅 위 원 대신 마름모)
            Diamond(dockIn, Plan.InPoint, 2.2f, yG);
            Diamond(dockOut, Plan.OutPoint, 2.2f, yG);
            dockIn.Apply(); dockOut.Apply(); selMesh.Apply();
            flowSig = float.NaN;
        }

        void WallPiece(SiteDef s, int side, float a, float b, float y)
        {
            if (b - a < 0.05f) return;
            float t = Plan.Wall;
            switch (side)
            {
                case 0: walls.Quad(s.x, s.z + a, s.x + t, s.z + b, y); break;
                case 1: walls.Quad(s.x + s.w - t, s.z + a, s.x + s.w, s.z + b, y); break;
                case 2: walls.Quad(s.x + a, s.z, s.x + b, s.z + t, y); break;
                default: walls.Quad(s.x + a, s.z + s.d - t, s.x + b, s.z + s.d, y); break;
            }
        }

        static void Diamond(FlatMesh m, Vector2 c, float r, float y)
        {
            for (int i = 0; i < 10; i++)
            {
                float f = (i + 0.5f) / 10f * 2f - 1f, hw = (1 - Mathf.Abs(f)) * r, z = c.y + f * r;
                m.Quad(c.x - hw, z - r / 10f, c.x + hw, z + r / 10f, y);
            }
        }

        static Texture2D ArrowTexture(Color c)
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4, name = "FlowArrow" };
            var bg = Color.Lerp(c, Color.white, 0.55f);
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float v = Mathf.Abs(y - (N - 1) / 2f) / (N / 2f);
                    float k = Mathf.Repeat(x / (float)N + v * 0.35f, 1f);
                    bool on = k > 0.35f && k < 0.62f && v < 0.82f;
                    px[y * N + x] = on || v > 0.88f ? c : bg;
                }
            tex.SetPixels(px); tex.Apply(true);
            return tex;
        }

        // ------------------------------------------------------------------ 동선
        // 입고 지점 → 가장 가까운 입고문 → 설비(문 쪽 면) / 설비(출고문 쪽 면) → 가장 가까운 출고문 → 출고 지점
        public void UpdateFlows(SiteDef s, List<PlacedModule> modules, CargoKind cargo, bool show)
        {
            float sig = show ? 1f : -1f;
            sig += (int)cargo * 7.13f + modules.Count * 101.7f + (drawnSig != null ? drawnSig.GetHashCode() * 0.001f : 0f);
            foreach (var m in modules)
            {
                if (m == null) continue;
                sig += (m.Min.x * 3.1f + m.Min.y * 5.7f + m.Size.x * 1.3f + m.Size.y * 0.7f) * (m.built ? 1f : -1f) + (m.entry.Handles(cargo) ? 13f : 0f);
            }
            if (sig != flowSig)
            {
                flowSig = sig;
                flowIn.Clear(); flowOut.Clear();
                // 입고 지점 → 모든 입고문, 모든 출고문 → 출고 지점 (트럭 길)
                if (show) foreach (var d in s.doors) Outside(d.kind == 0 ? flowIn : flowOut, s, d, d.kind == 0);
                if (show)
                    foreach (var m in modules)
                    {
                        if (m == null || !m.built || !m.entry.Handles(cargo)) continue;
                        var din = Nearest(s, 0, m); var dout = Nearest(s, 1, m);
                        if (din != null)
                        {
                            var port = Port(m, Plan.DoorInside(s, din), out int ps);
                            Inside(flowIn, s, din, port, ps, true);
                        }
                        if (dout != null)
                        {
                            var port = Port(m, Plan.DoorInside(s, dout), out int ps);
                            Inside(flowOut, s, dout, port, ps, false);
                        }
                    }
                flowIn.Apply(); flowOut.Apply();
            }
            float off = -Mathf.Repeat(Time.unscaledTime * 0.9f, 1f);
            flowIn.mat.SetTextureOffset("_BaseMap", new Vector2(off, 0));
            flowOut.mat.SetTextureOffset("_BaseMap", new Vector2(off, 0));
        }

        const float FlowW = 0.7f, Tile = 1.4f;

        static DoorDef Nearest(SiteDef s, int kind, PlacedModule m)
        {
            var c = m.Min + m.Size / 2; DoorDef best = null; float bd = float.MaxValue;
            foreach (var d in s.doors)
            {
                if (d.kind != kind) continue;
                float dd = (Plan.DoorInside(s, d) - c).sqrMagnitude;
                if (dd < bd) { bd = dd; best = d; }
            }
            return best;
        }

        // 설비 네 면 가운데 중 p 에 가장 가까운 곳 (side: 0 서 1 동 2 남 3 북)
        static Vector2 Port(PlacedModule m, Vector2 p, out int side)
        {
            var mn = m.Min; var sz = m.Size;
            var pts = new[]
            {
                new Vector2(mn.x, mn.y + sz.y / 2), new Vector2(mn.x + sz.x, mn.y + sz.y / 2),
                new Vector2(mn.x + sz.x / 2, mn.y), new Vector2(mn.x + sz.x / 2, mn.y + sz.y),
            };
            side = 0; float bd = float.MaxValue;
            for (int i = 0; i < 4; i++) { float d = (pts[i] - p).sqrMagnitude; if (d < bd) { bd = d; side = i; } }
            return pts[side];
        }

        // 부지 바깥: 입고 지점 → 트럭 자리 끝 → 문 (또는 반대로)
        static void Outside(FlatMesh m, SiteDef s, DoorDef d, bool inbound)
        {
            var door = Plan.DoorPoint(s, d); var n = Plan.Outward(d.side);
            var bay = door + n * Plan.BayLen;
            var pt = inbound ? Plan.InPoint : Plan.OutPoint;
            var corner = d.side < 2 ? new Vector2(pt.x, bay.y) : new Vector2(bay.x, pt.y);
            if (d.side < 2 && Mathf.Sign(bay.x - pt.x) != Mathf.Sign(n.x) && Mathf.Abs(bay.x - pt.x) > 0.1f) corner = new Vector2(bay.x, pt.y);
            if (inbound) Path(m, 0.015f, pt, corner, bay, door);
            else Path(m, 0.015f, door, bay, corner, pt);
        }

        // 건물 안: 문 → 안쪽으로 몇 m → 직각으로 꺾어 설비 면 (또는 반대로)
        static void Inside(FlatMesh m, SiteDef s, DoorDef d, Vector2 port, int portSide, bool inbound)
        {
            float y = s.h + 0.015f;
            var p0 = Plan.DoorInside(s, d);
            var p1 = p0 - Plan.Outward(d.side) * (Plan.StageDepth + 1.5f);
            var pa = port + Plan.Outward(portSide) * 1.5f;              // 설비 면 앞
            var corner = portSide < 2 ? new Vector2(p1.x, pa.y) : new Vector2(pa.x, p1.y);
            if (inbound) Path(m, y, p0, p1, corner, pa, port);
            else Path(m, y, port, pa, corner, p1, p0);
        }

        static void Path(FlatMesh m, float y, params Vector2[] pts)
        {
            float u = 0;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                if ((b - a).sqrMagnitude < 0.0001f) continue;
                var d = (b - a).normalized;
                var a2 = i > 0 ? a - d * (FlowW / 2) : a;     // 꺾이는 곳이 이어 보이도록
                m.Band(a2, b, FlowW, y, Tile, u);
                u += (b - a2).magnitude / Tile;
            }
        }

        // ------------------------------------------------------------------ 출고 대기 구역의 화물 (출고 트럭에 실을 물량)
        public void UpdateCargo(SiteDef s, CargoKind k)
        {
            string sig = (int)k + "#" + Sig(s);
            if (sig == cargoSig) return;
            cargoSig = sig;
            for (int i = cargoRoot.childCount - 1; i >= 0; i--) Object.Destroy(cargoRoot.GetChild(i).gameObject);
            int seed = 0;
            foreach (var d in s.doors)
            {
                if (d.kind != 1) continue;
                var r = Plan.StageRect(s, d);
                var c = r.center; var n = -Plan.Outward(d.side); var tg = new Vector2(-n.y, n.x);
                for (int i = 0; i < 2; i++)
                {
                    var p = c + n * (i - 0.5f) * 1.6f + tg * 1.2f;
                    var unit = new GameObject("Cargo").transform; unit.SetParent(cargoRoot, false);
                    unit.position = new Vector3(p.x, s.h, p.y);
                    unit.rotation = Quaternion.LookRotation(new Vector3(n.x, 0, n.y));
                    CargoModels.Build(unit, k, seed++);
                }
            }
        }
    }

    // 화물 한 단위의 간단한 모형 (파렛트 + 박스, 토트 묶음, 카톤 묶음, 드럼 4개)
    public static class CargoModels
    {
        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => mats.Clear();

        public static void Build(Transform t, CargoKind k, int seed)
        {
            var wood = Mat("Wood", new Color(0.70f, 0.53f, 0.34f), 0.2f);
            var card = Mat("Carton", new Color(0.78f, 0.62f, 0.42f), 0.15f);
            var tape = Mat("Tape", new Color(0.92f, 0.86f, 0.70f), 0.3f);
            var tote = Mat("Tote", new Color(0.16f, 0.48f, 0.78f), 0.45f);
            var drum = Mat("Drum", new Color(0.18f, 0.36f, 0.70f), 0.55f, 0.6f);
            switch (k)
            {
                case CargoKind.Pallet:
                    Pallet(t, wood);
                    Box(t, new Vector3(0, 0.15f + 0.6f, 0), new Vector3(1.04f, 1.2f, 1.04f), card);
                    Box(t, new Vector3(0, 0.15f + 1.205f, 0), new Vector3(0.1f, 0.01f, 1.05f), tape);
                    break;
                case CargoKind.Drum:
                    Pallet(t, wood);
                    for (int i = 0; i < 4; i++)
                    {
                        var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        Object.Destroy(c.GetComponent<Collider>());
                        c.transform.SetParent(t, false);
                        c.transform.localPosition = new Vector3((i % 2 - 0.5f) * 0.56f, 0.15f + 0.44f, (i / 2 - 0.5f) * 0.56f);
                        c.transform.localScale = new Vector3(0.54f, 0.44f, 0.54f);
                        c.GetComponent<MeshRenderer>().sharedMaterial = drum;
                    }
                    break;
                case CargoKind.Tote:
                    for (int l = 0; l < 4; l++)
                        for (int i = 0; i < 2; i++)
                            Box(t, new Vector3((i - 0.5f) * 0.42f, 0.16f + l * 0.33f, 0), new Vector3(0.40f, 0.31f, 0.6f), tote);
                    break;
                case CargoKind.Carton:
                    for (int l = 0; l < 3; l++)
                        for (int i = 0; i < 2; i++)
                        {
                            var p = new Vector3((i - 0.5f) * 0.52f, 0.2f + l * 0.41f, ((seed + l + i) % 2 - 0.5f) * 0.06f);
                            Box(t, p, new Vector3(0.5f, 0.4f, 0.6f), card);
                            Box(t, p + new Vector3(0, 0.2f, 0), new Vector3(0.08f, 0.005f, 0.61f), tape);
                        }
                    break;
            }
        }

        static void Pallet(Transform t, Material wood)
        {
            Box(t, new Vector3(0, 0.13f, 0), new Vector3(1.1f, 0.04f, 1.1f), wood);
            for (int i = -1; i <= 1; i++) Box(t, new Vector3(i * 0.48f, 0.055f, 0), new Vector3(0.12f, 0.11f, 1.1f), wood);
        }

        static void Box(Transform parent, Vector3 pos, Vector3 size, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localScale = size;
            g.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        static Material Mat(string name, Color c, float smooth, float metal = 0f)
        {
            if (mats.TryGetValue(name, out var m)) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
            mats[name] = m;
            return m;
        }
    }
}
