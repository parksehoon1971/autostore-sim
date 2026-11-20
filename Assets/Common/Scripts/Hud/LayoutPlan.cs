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
        // 센터로 들어오는 도로는 하나: 부지 서쪽 끝의 진입로. 우측 통행이라 들어오는 차로(입고)가 남쪽, 나가는 차로(출고)가 북쪽
        public static readonly Vector2 Gate = new Vector2(-78f, 40f);
        public const float LaneGap = 2f;                    // 도로 가운데에서 차로 가운데까지
        public const float RoadHalf = 4.5f;                 // 도로 반폭
        public static readonly Vector2 InPoint = Gate + new Vector2(0f, -LaneGap);
        public static readonly Vector2 OutPoint = Gate + new Vector2(0f, LaneGap);
        // 트럭이 문 앞에서 후진을 시작하는 거리 (트럭 반 길이 + 트럭 자리 + 여유), 나가는 차로는 그보다 바깥
        public const float AisleDist = 6f + BayLen + 3f;
        public const float OutLaneOff = 3.5f;
        public const float ApronDepth = AisleDist + OutLaneOff + 4f;   // 문 앞 트럭 마당(주차장) 깊이
        // 건물을 그릴 수 있는 범위 (진입로와 트럭 마당, 바닥 끝에서 떨어지게)
        public static readonly Rect Allowed = new Rect(-24f, -50f, 220f, 180f);
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

        // 도크 대기 구역 · 평치 구역(과 그 앞 통로)과 겹치는지 (설비 · 하역 수단 놓기 금지)
        public static bool HitsStage(SiteDef s, Vector2 min, Vector2 size, float gap)
        {
            bool Hit(Rect r) => min.x < r.xMax + gap && r.xMin < min.x + size.x + gap && min.y < r.yMax + gap && r.yMin < min.y + size.y + gap;
            foreach (var d in s.doors) if (Hit(StageRect(s, d))) return true;
            return s.doors.Count > 0 && Hit(BulkRect(s));
        }

        // ------------------------------------------------------------------ 센터 안 화물 자리
        public const float CrossAisle = 4f;     // 대기 구역과 평치 사이 통로 폭
        public const float BulkDepth = 14f;     // 평치 구역 깊이
        public const float BulkAisle = 5.2f;    // 평치 통로 하나(양쪽 화물 줄 포함) 폭
        public const float SlotRow = 1.5f;      // 화물 줄 간격 (통로 방향)
        public const float SlotSide = 1.9f;     // 통로 가운데에서 화물 가운데까지

        // 벽 기준 좌표: point = origin + along * a + inward * d (a 는 DoorDef.pos 와 같은 방향)
        public static void WallFrame(SiteDef s, int side, out Vector2 origin, out Vector2 along, out Vector2 inward)
        {
            switch (side)
            {
                case 0: origin = new Vector2(s.x, s.z); along = Vector2.up; inward = Vector2.right; break;
                case 1: origin = new Vector2(s.x + s.w, s.z); along = Vector2.up; inward = Vector2.left; break;
                case 2: origin = new Vector2(s.x, s.z); along = Vector2.right; inward = Vector2.up; break;
                default: origin = new Vector2(s.x, s.z + s.d); along = Vector2.right; inward = Vector2.down; break;
            }
        }

        static float NearBulk => Wall + 0.2f + StageDepth + CrossAisle;          // 벽에서 평치 시작까지
        public static float CrossDepth => Wall + 0.2f + StageDepth + CrossAisle / 2;   // 통로 가운데 (벽에서)

        static void BulkSpan(SiteDef s, out int side, out float a0, out float a1, out float depth)
        {
            side = DockSide(s);
            float wl = WallLen(s, side);
            float len = Mathf.Max(BulkAisle, Mathf.Min(wl - 8f, 42f));
            len = Mathf.Floor(len / BulkAisle) * BulkAisle;
            a0 = wl / 2 - len / 2; a1 = a0 + len;
            float avail = (side < 2 ? s.w : s.d) - NearBulk - 4f;
            depth = Mathf.Clamp(avail, 4f, BulkDepth);
        }

        public static Rect BulkRect(SiteDef s)
        {
            BulkSpan(s, out int side, out float a0, out float a1, out float depth);
            WallFrame(s, side, out var o, out var al, out var iw);
            var p = o + al * a0 + iw * NearBulk; var q = o + al * a1 + iw * (NearBulk + depth);
            return Rect.MinMaxRect(Mathf.Min(p.x, q.x), Mathf.Min(p.y, q.y), Mathf.Max(p.x, q.x), Mathf.Max(p.y, q.y));
        }

        public struct SlotGeo { public Vector2 pos, aisle, face; }

        // kind 0 입고 대기(검수) 1 평치 통로 2 출고장. 통로로 들어가 옆(face 방향)의 화물 자리를 씀
        public class ZoneGeo
        {
            public int kind, door = -1;
            public Vector2 entryA, entryB;          // A: 문(또는 통로 앞) 쪽, B: 안쪽(평치 쪽 통로)
            public Vector2 axis;
            public readonly List<SlotGeo> slots = new List<SlotGeo>();
        }

        static void AddRows(ZoneGeo z, Vector2 start, Vector2 axis, Vector2 tg, float from, float to)
        {
            for (float d = from; d <= to + 0.01f; d += SlotRow)
                for (int k = -1; k <= 1; k += 2)
                {
                    var a = start + axis * d;
                    z.slots.Add(new SlotGeo { aisle = a, face = tg * k, pos = a + tg * (k * SlotSide) });
                }
        }

        public static List<ZoneGeo> Zones(SiteDef s)
        {
            var list = new List<ZoneGeo>();
            for (int i = 0; i < s.doors.Count; i++)
            {
                var d = s.doors[i];
                var n = -Outward(d.side); var tg = new Vector2(-n.y, n.x);
                var p = DoorInside(s, d);
                var z = new ZoneGeo { kind = d.kind == 0 ? 0 : 2, door = i, axis = n, entryA = p, entryB = p + n * (StageDepth + 1.2f) };
                AddRows(z, p, n, tg, 1.4f, StageDepth - 1.4f);
                list.Add(z);
            }
            if (s.doors.Count == 0) return list;
            BulkSpan(s, out int side, out float a0, out float a1, out float depth);
            WallFrame(s, side, out var o, out var al, out var iw);
            for (float a = a0 + BulkAisle / 2; a < a1; a += BulkAisle)
            {
                var start = o + al * a + iw * NearBulk;
                var z = new ZoneGeo { kind = 1, axis = iw, entryA = start - iw * 1.0f, entryB = start - iw * 1.0f };
                AddRows(z, start, iw, al, 1.0f, depth - 0.9f);
                list.Add(z);
            }
            return list;
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
            if (side < 0) side = DockSide(s);
            s.doors.RemoveAll(d => d.kind == kind);
            for (int i = 0; i < n; i++) s.doors.Add(new DoorDef { kind = kind, side = side });
            LayoutWall(s, side);
        }

        // 문을 새로 둘 벽: 이미 문이 있는 벽, 없으면 진입로를 마주한 벽
        public static int DockSide(SiteDef s)
        {
            if (s.doors.Count > 0) return s.doors[0].side;
            return FacingSide(s, Gate);
        }

        // 한 벽의 문을 입고문 → 출고문 순서로 벽 가운데에 고르게 (두 무리 사이는 한 칸 더 띄움)
        public static void LayoutWall(SiteDef s, int side)
        {
            var list = new List<DoorDef>();
            foreach (var d in s.doors) if (d.side == side) list.Add(d);
            if (list.Count == 0) return;
            list.Sort((a, b) => a.kind != b.kind ? a.kind.CompareTo(b.kind) : a.pos.CompareTo(b.pos));
            bool both = list[0].kind != list[list.Count - 1].kind;
            int slots = list.Count + (both ? 1 : 0);
            float len = WallLen(s, side);
            float step = slots > 1 ? Mathf.Min(DoorSpacing, (len - DoorW - 3f) / (slots - 1)) : DoorSpacing;
            step = Mathf.Max(step, DoorW + 0.4f);
            float start = len / 2 - (slots - 1) * step / 2;
            int slot = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0 && list[i].kind != list[i - 1].kind) slot++;
                list[i].pos = ClampPos(s, side, start + slot * step);
                slot++;
            }
        }

        public static Vector2 BayFront(SiteDef s, DoorDef d) => DoorPoint(s, d) + Outward(d.side) * AisleDist;

        // 트럭 길: 들어올 때는 입고 차로 → 건물 서쪽 통로 → 문 앞 (BayFront), 나갈 때는 문 앞 → 바깥 차로 → 출고 차로
        public static List<Vector2> Route(SiteDef s, DoorDef d, bool arriving)
        {
            float da = AisleDist + (arriving ? 0f : OutLaneOff);
            var P = arriving ? InPoint : OutPoint;
            var A = BayFront(s, d);
            float cx = s.x - da;
            var pts = new List<Vector2> { P, new Vector2(cx, P.y) };
            switch (d.side)
            {
                case 0:
                    pts.Add(new Vector2(cx, A.y));
                    break;
                case 2:
                case 3:
                {
                    float az = d.side == 2 ? s.z - da : s.z + s.d + da;
                    pts.Add(new Vector2(cx, az)); pts.Add(new Vector2(A.x, az));
                    break;
                }
                default:
                {
                    // 뒷벽: 건물을 돌아서 (남쪽과 북쪽 중 진입로에 가까운 쪽)
                    bool south = Mathf.Abs(P.y - s.z) <= Mathf.Abs(P.y - (s.z + s.d));
                    float az = south ? s.z - da : s.z + s.d + da, ex = s.x + s.w + da;
                    pts.Add(new Vector2(cx, az)); pts.Add(new Vector2(ex, az)); pts.Add(new Vector2(ex, A.y));
                    break;
                }
            }
            pts.Add(A);
            for (int i = pts.Count - 1; i > 0; i--) if ((pts[i] - pts[i - 1]).sqrMagnitude < 0.01f) pts.RemoveAt(i);
            if (!arriving) pts.Reverse();
            return pts;
        }

        // 문이 있는 벽마다 트럭 마당(주차장) 사각형
        public static Rect Apron(SiteDef s, int side)
        {
            const float ext = 8f;
            float x0 = s.x, z0 = s.z, x1 = s.x + s.w, z1 = s.z + s.d, a = ApronDepth;
            return side switch
            {
                0 => Rect.MinMaxRect(x0 - a, z0 - ext, x0, z1 + ext),
                1 => Rect.MinMaxRect(x1, z0 - ext, x1 + a, z1 + ext),
                2 => Rect.MinMaxRect(x0 - ext, z0 - a, x1 + ext, z0),
                _ => Rect.MinMaxRect(x0 - ext, z1, x1 + ext, z1 + a),
            };
        }

        public static float ClampPos(SiteDef s, int side, float pos) => Mathf.Clamp(pos, DoorW / 2 + 1.5f, WallLen(s, side) - DoorW / 2 - 1.5f);

        public static void DefaultDoors(SiteDef s)
        {
            s.doors.Clear();
            SetDoorCount(s, 0, 2);
            SetDoorCount(s, 1, 2);      // 입고문과 같은 벽에
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
        readonly FlatMesh walls, axes, dockIn, dockOut, ground, selMesh, flowIn, flowOut, asphalt, paint, yellow, bulkMesh;
        public static readonly Color BulkCol = new Color(0.50f, 0.38f, 0.80f);
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
            asphalt = new FlatMesh("Asphalt", root, new Color(0.27f, 0.28f, 0.30f));
            paint = new FlatMesh("RoadPaint", root, new Color(0.93f, 0.93f, 0.90f));
            yellow = new FlatMesh("RoadCenter", root, new Color(0.96f, 0.76f, 0.16f));
            ground = new FlatMesh("SiteMarks", root, new Color(0.40f, 0.42f, 0.46f));
            walls = new FlatMesh("Walls", root, ink);
            axes = new FlatMesh("ColumnAxes", root, new Color(0.45f, 0.55f, 0.70f));
            dockIn = new FlatMesh("InDocks", root, InCol);
            dockOut = new FlatMesh("OutDocks", root, OutCol);
            selMesh = new FlatMesh("DoorSelection", root, HudStyle.Accent);
            bulkMesh = new FlatMesh("BulkStorage", root, BulkCol);
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
            const float yG = 0.05f;
            ground.Quad(x0, z0 - 4.04f, x1, z0 - 3.96f, yG);
            ground.Quad(x1 + 3.96f, z0, x1 + 4.04f, z1, yG);
            for (float x = x0; x <= x1 + 0.01f; x += 10) ground.Quad(x - 0.03f, z0 - 4.6f, x + 0.03f, z0 - 3.4f, yG);
            for (float z = z0; z <= z1 + 0.01f; z += 10) ground.Quad(x1 + 3.4f, z - 0.03f, x1 + 4.6f, z + 0.03f, yG);
            ground.Apply();

            DrawYard(s, yG);

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
            // 화물 자리: 대기 구역 · 출고장은 문 색, 평치는 보라
            bulkMesh.Clear();
            if (s.doors.Count > 0) bulkMesh.Outline(Plan.BulkRect(s), 0.14f, yM);
            foreach (var z in Plan.Zones(s))
            {
                var m = z.kind == 1 ? bulkMesh : z.kind == 0 ? dockIn : dockOut;
                foreach (var sl in z.slots)
                {
                    bool fx = Mathf.Abs(sl.face.x) > 0.5f;
                    float hx = fx ? 0.65f : 0.55f, hz = fx ? 0.55f : 0.65f;
                    m.Outline(Rect.MinMaxRect(sl.pos.x - hx, sl.pos.y - hz, sl.pos.x + hx, sl.pos.y + hz), 0.05f, yM);
                }
                if (z.kind == 1 && z.slots.Count > 0)
                    bulkMesh.Dashed(z.slots[0].aisle, z.slots[z.slots.Count - 1].aisle, 0.06f, yM, 0.8f, 0.6f);
            }
            bulkMesh.Apply();
            Diamond(dockIn, Plan.InPoint + new Vector2(1.5f, 0), 1.2f, yG + 0.01f);
            Diamond(dockOut, Plan.OutPoint + new Vector2(1.5f, 0), 1.2f, yG + 0.01f);
            dockIn.Apply(); dockOut.Apply(); selMesh.Apply();
            flowSig = float.NaN;
        }

        // 진입로 · 트럭 길 · 문 앞 트럭 마당(주차장): 아스팔트 + 차선 + 주차 칸
        void DrawYard(SiteDef s, float yP)
        {
            const float yA = 0.025f;
            asphalt.Clear(); paint.Clear(); yellow.Clear();
            var sides = new HashSet<int>();
            foreach (var d in s.doors) sides.Add(d.side);
            // 트럭 마당
            foreach (int side in sides)
            {
                var r = Plan.Apron(s, side);
                asphalt.Quad(r.xMin, r.yMin, r.xMax, r.yMax, yA);
                // 마당 가장자리 실선 (건물 쪽 제외)
                var n = Plan.Outward(side); var tg = new Vector2(-n.y, n.x);
                var c = r.center; float halfLen = Mathf.Abs(Vector2.Dot(r.size, tg)) / 2, depth = Plan.ApronDepth;
                var wallMid = c - n * depth / 2;
                var far = wallMid + n * (depth - 0.4f);
                paint.Band(far - tg * halfLen, far + tg * halfLen, 0.15f, yP, 1f, 0f);
                paint.Band(wallMid + tg * (halfLen - 0.3f), far + tg * (halfLen - 0.3f), 0.15f, yP, 1f, 0f);
                paint.Band(wallMid - tg * (halfLen - 0.3f), far - tg * (halfLen - 0.3f), 0.15f, yP, 1f, 0f);
                // 통로 가운데 점선 (들어오는 차로와 나가는 차로 사이)
                var mid = wallMid + n * (Plan.AisleDist + Plan.OutLaneOff / 2);
                yellow.Dashed(mid - tg * (halfLen - 1f), mid + tg * (halfLen - 1f), 0.15f, yP, 3f, 3f);
            }
            // 문마다 트럭 주차 칸 (흰 테두리 + 바퀴 멈춤턱)
            foreach (var d in s.doors)
            {
                var p = Plan.DoorPoint(s, d); var n = Plan.Outward(d.side); var tg = new Vector2(-n.y, n.x);
                float hw = Plan.DoorW / 2 + 0.6f;
                var e = p + n * Plan.BayLen;
                paint.Band(e + tg * hw, e - tg * hw, 0.15f, yP, 1f, 0f);
                paint.Band(p + n * 1.4f + tg * 1.1f, p + n * 1.4f + tg * 0.3f, 0.35f, yP, 1f, 0f);
                paint.Band(p + n * 1.4f - tg * 1.1f, p + n * 1.4f - tg * 0.3f, 0.35f, yP, 1f, 0f);
            }
            // 진입로: 부지 끝 → 건물 서쪽 통로. 두 차로 + 가운데 노란 겹선
            float roadEnd = s.x - Plan.AisleDist - Plan.OutLaneOff - 2f;
            var g = Plan.Gate;
            float rx0 = g.x - 2f;
            asphalt.Quad(rx0, g.y - Plan.RoadHalf, roadEnd, g.y + Plan.RoadHalf, yA);
            paint.Quad(rx0, g.y - Plan.RoadHalf + 0.25f, roadEnd, g.y - Plan.RoadHalf + 0.4f, yP);
            paint.Quad(rx0, g.y + Plan.RoadHalf - 0.4f, roadEnd, g.y + Plan.RoadHalf - 0.25f, yP);
            yellow.Quad(rx0, g.y - 0.25f, roadEnd, g.y - 0.12f, yP);
            yellow.Quad(rx0, g.y + 0.12f, roadEnd, g.y + 0.25f, yP);
            // 트럭 길 (진입로에서 마당까지, 마당이 서쪽 벽이 아닐 때 건물을 돌아가는 길)
            foreach (var d in s.doors)
                for (int k = 0; k < 2; k++)
                {
                    var pts = Plan.Route(s, d, k == 0);
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        var a = pts[i]; var b = pts[i + 1];
                        var dir = (b - a).normalized;
                        asphalt.Band(a - dir * 2.5f, b + dir * 2.5f, 5f, yA, 1f, 0f);
                    }
                }
            asphalt.Apply(); paint.Apply(); yellow.Apply();
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

        // 부지 바깥: 입고 차로 → 트럭 길 → 문 앞 → 문 (출고는 문 → 문 앞 → 출고 차로)
        static void Outside(FlatMesh m, SiteDef s, DoorDef d, bool inbound)
        {
            var door = Plan.DoorPoint(s, d);
            var pts = Plan.Route(s, d, inbound);
            if (inbound) pts.Add(door); else pts.Insert(0, door);
            // 여러 문의 길이 겹치는 진입로에서 무늬가 맞도록, 입고는 시작점 · 출고는 끝점 기준으로 맞춤
            Path(m, 0.075f, !inbound, pts.ToArray());
        }

        // 건물 안: 문 → 안쪽으로 몇 m → 직각으로 꺾어 설비 면 (또는 반대로)
        static void Inside(FlatMesh m, SiteDef s, DoorDef d, Vector2 port, int portSide, bool inbound)
        {
            float y = s.h + 0.015f;
            var p0 = Plan.DoorInside(s, d);
            var p1 = p0 - Plan.Outward(d.side) * (Plan.StageDepth + 1.5f);
            var pa = port + Plan.Outward(portSide) * 1.5f;              // 설비 면 앞
            var corner = portSide < 2 ? new Vector2(p1.x, pa.y) : new Vector2(pa.x, p1.y);
            if (inbound) Path(m, y, false, p0, p1, corner, pa, port);
            else Path(m, y, false, port, pa, corner, p1, p0);
        }

        static void Path(FlatMesh m, float y, bool alignEnd, params Vector2[] pts)
        {
            float u = 0;
            if (alignEnd)
            {
                // Band 와 같은 방식으로 전체 길이를 먼저 셈
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    var a = pts[i]; var b = pts[i + 1];
                    if ((b - a).sqrMagnitude < 0.0001f) continue;
                    var d = (b - a).normalized;
                    var a2 = i > 0 ? a - d * (FlowW / 2) : a;
                    u -= (b - a2).magnitude / Tile;
                }
            }
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
