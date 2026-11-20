// 배치 게임의 입출고: 화주 주문을 실은 윙바디 트럭이 입고 / 출고 지점에서 출발해 출입문 앞 트럭 자리에 후진 주차하고,
// 윙을 열고 기다립니다. 사용자가 센터 안에 하역 수단(지게차·작업자)을 두면 자동으로 상하차를 시작합니다.
//  - 입고: 트럭 적재함 → 문 안쪽 대기 구역. 출고: 출고 대기 구역 → 트럭.
//  - 마감 전에 끝내면 화주가 보상, 마감을 넘기면 패널티를 물고 트럭은 남은 화물과 함께 떠납니다.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace EquipFX
{
    [Serializable]
    public class MoverSave { public int kind; public float x, z; }

    // 하역 수단
    public static class MoverInfo
    {
        public const int Forklift = 0, Worker = 1;
        public static readonly int[] All = { Forklift, Worker };
        public static string Name(int k) => k == Forklift ? "지게차" : "작업자";
        public static string Desc(int k) => k == Forklift ? "파렛트·드럼·토트·카톤 모두 하역. 2.8 m/s" : "토트·카톤만 손으로 하역 (파렛트 불가). 1.4 m/s";
        public static bool Handles(int k, CargoKind c) => k == Forklift || c == CargoKind.Tote || c == CargoKind.Carton;
        public static float Speed(int k) => k == Forklift ? 2.8f : 1.4f;
    }

    // ------------------------------------------------------------------ 윙바디 트럭
    public class Truck
    {
        public const float L = 12f, BoxW = 2.45f, BoxH = 2.5f;
        public const int Rows = 5, Cols = 2;
        public readonly Transform root;
        readonly Transform wingL, wingR;
        public readonly float bed;
        public readonly Transform[] slot = new Transform[Rows * Cols];    // 칸마다 실린 화물 (없으면 null)
        public readonly bool[] reserved = new bool[Rows * Cols];
        float wing;
        // 주행
        readonly List<(Vector3 p, bool rev)> path = new List<(Vector3, bool)>();
        int seg;

        public Truck(Transform parent, float bedHeight, Color cab)
        {
            bed = bedHeight;
            root = new GameObject("WingTruck").transform; root.SetParent(parent, false);
            var white = Mat("TruckBox", new Color(0.92f, 0.93f, 0.94f), 0.5f, 0.2f);
            var dark = Mat("TruckDark", new Color(0.12f, 0.13f, 0.14f), 0.3f);
            var cabM = Mat("TruckCab" + cab.GetHashCode(), cab, 0.6f, 0.3f);
            var glass = Mat("TruckGlass", new Color(0.10f, 0.16f, 0.22f), 0.9f);
            float rear = -L / 2, boxL = 9.4f, z0 = rear + 0.1f, z1 = z0 + boxL;
            // 차대 · 바퀴
            Box(root, new Vector3(0, bed - 0.45f, 0), new Vector3(2.2f, 0.3f, L - 0.4f), dark);
            foreach (var wz in new[] { rear + 1.6f, rear + 2.9f, L / 2 - 1.5f })
                foreach (var wx in new[] { -1.05f, 1.05f })
                {
                    var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Object.Destroy(w.GetComponent<Collider>());
                    w.transform.SetParent(root, false);
                    w.transform.localPosition = new Vector3(wx, 0.5f, wz);
                    w.transform.localRotation = Quaternion.Euler(0, 0, 90);
                    w.transform.localScale = new Vector3(1.0f, 0.16f, 1.0f);
                    w.GetComponent<MeshRenderer>().sharedMaterial = dark;
                }
            // 적재함: 바닥 + 앞벽 (뒤는 열림, 좌우·지붕은 윙)
            Box(root, new Vector3(0, bed - 0.05f, (z0 + z1) / 2), new Vector3(BoxW, 0.1f, boxL), white);
            Box(root, new Vector3(0, bed + BoxH / 2, z1 - 0.05f), new Vector3(BoxW, BoxH, 0.1f), white);
            // 윙 (지붕 가운데를 축으로 위로 열림)
            wingL = Wing(-1, bed, (z0 + z1) / 2, boxL, white);
            wingR = Wing(+1, bed, (z0 + z1) / 2, boxL, white);
            // 캡
            float cz = z1 + 0.15f + 1.1f;
            Box(root, new Vector3(0, 0.9f + 1.3f, cz), new Vector3(2.4f, 2.6f, 2.2f), cabM);
            Box(root, new Vector3(0, 2.6f, cz + 1.11f), new Vector3(2.2f, 1.0f, 0.02f), glass);
            Box(root, new Vector3(0, 0.75f, cz + 1.12f), new Vector3(2.3f, 0.3f, 0.06f), dark);
        }

        Transform Wing(int side, float bed, float zc, float len, Material m)
        {
            var pivot = new GameObject(side < 0 ? "WingL" : "WingR").transform; pivot.SetParent(root, false);
            pivot.localPosition = new Vector3(0, bed + BoxH, zc);
            Box(pivot, new Vector3(side * BoxW / 4, 0.03f, 0), new Vector3(BoxW / 2, 0.06f, len), m);
            Box(pivot, new Vector3(side * (BoxW / 2 - 0.03f), -BoxH / 2 + 0.05f, 0), new Vector3(0.06f, BoxH - 0.1f, len), m);
            return pivot;
        }

        // 칸 i 의 적재 위치 (트럭 기준). i 가 작을수록 뒤쪽 (문 가까이)
        public Vector3 SlotLocal(int i)
        {
            int r = i / Cols, c = i % Cols;
            return new Vector3((c - 0.5f) * 1.2f, bed, -L / 2 + 0.9f + r * 1.75f);
        }

        public Vector3 SlotWorld(int i) => root.TransformPoint(SlotLocal(i));

        public int Loaded { get { int n = 0; foreach (var t in slot) if (t != null) n++; return n; } }

        public void Put(int i, Transform unit)
        {
            slot[i] = unit;
            unit.SetParent(root, false);
            unit.localPosition = SlotLocal(i); unit.localRotation = Quaternion.identity; unit.localScale = Vector3.one;
        }

        public bool SetWing(float target, float dt)
        {
            wing = Mathf.MoveTowards(wing, target, dt / 2.5f);
            float a = Mathf.SmoothStep(0, 1, wing) * 72f;
            wingL.localRotation = Quaternion.Euler(0, 0, -a);
            wingR.localRotation = Quaternion.Euler(0, 0, a);
            return Mathf.Approximately(wing, target);
        }

        public void SetPath(params (Vector3 p, bool rev)[] pts) { path.Clear(); path.AddRange(pts); seg = 0; }

        // 경로를 따라 이동. 끝까지 가면 true
        public bool Drive(float dt)
        {
            var pos = root.position;
            while (seg < path.Count && dt > 0)
            {
                var (tgt, rev) = path[seg];
                float sp = rev ? 2.2f : 7f;
                var d = tgt - pos; d.y = 0;
                float dist = d.magnitude;
                if (dist < 0.001f) { seg++; continue; }
                var face = rev ? -d / dist : d / dist;
                root.rotation = Quaternion.RotateTowards(root.rotation, Quaternion.LookRotation(face), 140f * dt);
                float step = sp * dt;
                if (step >= dist) { pos = tgt; seg++; dt -= dist / sp; }
                else { pos += d / dist * step; dt = 0; }
            }
            root.position = pos;
            return seg >= path.Count;
        }

        public void Destroy() { if (root != null) Object.Destroy(root.gameObject); }

        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => mats.Clear();

        internal static Material Mat(string name, Color c, float smooth, float metal = 0f)
        {
            if (mats.TryGetValue(name, out var m) && m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metal);
            mats[name] = m;
            return m;
        }

        internal static Transform Box(Transform parent, Vector3 pos, Vector3 size, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localScale = size;
            g.GetComponent<MeshRenderer>().sharedMaterial = m;
            return g.transform;
        }
    }

    // ------------------------------------------------------------------ 하역 수단 (지게차 · 작업자)
    public class Mover
    {
        public int kind, id;
        public Transform t, hold;
        public Bay bay;               // 맡은 트럭 자리 (하차 · 상차 중)
        public Bay pickFor;           // 평치에서 출고장으로 옮겨 주는 출고 트럭 자리
        public int slotIdx = -1;      // 맡은 트럭 칸
        public Transform load;
        public string State = "대기";
        public IEnumerator<object> job;                              // 지금 하는 일 (프레임마다 한 걸음)
        public StoreZone holding;                                    // 들어가 있는 구역 (한 번에 한 대만)
        public readonly List<StoreSlot> claims = new List<StoreSlot>();   // 잡아 둔 화물 자리

        public static Mover Create(Transform parent, int kind, Vector3 pos, int id)
        {
            var m = new Mover { kind = kind, id = id };
            m.t = new GameObject(MoverInfo.Name(kind) + " " + id).transform; m.t.SetParent(parent, false);
            m.t.position = pos;
            if (kind == MoverInfo.Forklift)
            {
                var yel = Truck.Mat("Forklift", new Color(0.98f, 0.72f, 0.10f), 0.5f);
                var dark = Truck.Mat("TruckDark", new Color(0.12f, 0.13f, 0.14f), 0.3f);
                Truck.Box(m.t, new Vector3(0, 0.65f, -0.1f), new Vector3(1.1f, 0.9f, 1.8f), yel);
                Truck.Box(m.t, new Vector3(0, 0.75f, -0.95f), new Vector3(1.1f, 0.7f, 0.3f), dark);   // 균형추
                Truck.Box(m.t, new Vector3(0, 2.15f, -0.2f), new Vector3(1.05f, 0.06f, 1.2f), dark);  // 머리 보호대
                foreach (var x in new[] { -0.48f, 0.48f })
                {
                    Truck.Box(m.t, new Vector3(x, 1.4f, -0.75f), new Vector3(0.06f, 1.5f, 0.06f), dark);
                    Truck.Box(m.t, new Vector3(x * 0.75f, 1.2f, 0.9f), new Vector3(0.08f, 2.3f, 0.08f), dark);   // 마스트
                    Truck.Box(m.t, new Vector3(x * 0.6f, 0.1f, 1.5f), new Vector3(0.12f, 0.05f, 1.1f), dark);    // 포크
                }
                m.hold = new GameObject("Fork").transform; m.hold.SetParent(m.t, false); m.hold.localPosition = new Vector3(0, 0.12f, 1.5f);
            }
            else
            {
                var vest = Truck.Mat("Vest", new Color(1f, 0.45f, 0.10f), 0.3f);
                var skin = Truck.Mat("Skin", new Color(0.86f, 0.70f, 0.58f), 0.3f);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(m.t, false); body.transform.localPosition = new Vector3(0, 0.85f, 0); body.transform.localScale = new Vector3(0.45f, 0.85f, 0.35f);
                body.GetComponent<MeshRenderer>().sharedMaterial = vest;
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(m.t, false); head.transform.localPosition = new Vector3(0, 1.85f, 0); head.transform.localScale = Vector3.one * 0.26f;
                head.GetComponent<MeshRenderer>().sharedMaterial = skin;
                m.hold = new GameObject("Hands").transform; m.hold.SetParent(m.t, false); m.hold.localPosition = new Vector3(0, 0.75f, 0.45f);
            }
            return m;
        }

        // rev: 뒤로 이동 (앞을 그대로 보며 물러남)
        public bool MoveTo(Vector3 p, float dt, bool rev = false)
        {
            var d = p - t.position; float dist = d.magnitude;
            float step = MoverInfo.Speed(kind) * (rev ? 0.6f : 1f) * dt;
            var flat = new Vector3(d.x, 0, d.z);
            if (flat.sqrMagnitude > 0.0004f) t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(rev ? -flat : flat), 360f * dt);
            if (step >= dist) { t.position = p; return true; }
            t.position += d / dist * step;
            return false;
        }

        public void Carry(Transform unit)
        {
            load = unit;
            unit.SetParent(hold, false);
            unit.localPosition = Vector3.zero; unit.localRotation = Quaternion.identity;
            unit.localScale = kind == MoverInfo.Worker ? Vector3.one * 0.45f : Vector3.one;
        }
    }

    // ------------------------------------------------------------------ 화주 주문 · 트럭 자리 (출입문 하나)
    public class Bay
    {
        public int door;              // Site.doors 순번
        public bool inbound;
        public Truck truck;
        public int state;             // 0 빈 자리 1 도착 중 2 윙 여는 중 3 상하차 4 윙 닫는 중 5 출발
        public float timer;
        public int units, done;       // 주문 물량, 처리한 수
        public float due;             // 마감 (교통 시계 s)
        public bool late, noticed;
        public int Assigned;          // 트럭에서 일하는 하역 수단 수 (한 자리에 한 대)
        public int picking;           // 평치에서 출고장으로 옮기는 중인 수 (출고)
    }

    // ------------------------------------------------------------------ 센터 안 화물 자리 (입고 대기·검수 / 평치 / 출고장)
    public class StoreSlot
    {
        public Vector2 pos, aisle, face;
        public Transform unit;
        public bool reserved, inspected;
    }

    public class StoreZone
    {
        public int kind, door = -1;       // kind 0 입고 대기(검수) 1 평치 통로 2 출고장
        public Vector2 entryA, entryB, axis;
        public readonly List<StoreSlot> slots = new List<StoreSlot>();
        public Mover holder;
        public int Units { get { int n = 0; foreach (var x in slots) if (x.unit != null) n++; return n; } }
        public StoreSlot Free() { foreach (var x in slots) if (x.unit == null && !x.reserved) return x; return null; }
    }

    public class Traffic
    {
        public const float Window = 240f;          // 도착부터 마감까지 (s)
        public const int RewardPerUnit = 2, OnTimeBonus = 10, Penalty = 30, PenaltyPerUnit = 3;   // 만원
        public readonly List<Bay> bays = new List<Bay>();
        public readonly List<Mover> movers = new List<Mover>();
        public float clock;
        public int balance, inDone, outDone, onTime, missed;
        public event Action<string> Notice;
        readonly Transform root;
        readonly System.Random rng = new System.Random(7);
        static readonly Color[] cabColors = { new Color(0.15f, 0.35f, 0.75f), new Color(0.80f, 0.15f, 0.15f), new Color(0.95f, 0.95f, 0.95f), new Color(0.10f, 0.55f, 0.35f) };
        string sig;

        public Traffic(Transform parent)
        {
            root = new GameObject("Traffic").transform; root.SetParent(parent, false);
        }

        public readonly List<StoreZone> zones = new List<StoreZone>();
        public const int StartStock = 16;           // 처음 평치에 있는 재고
        float dtNow, hNow;
        SiteDef siteNow;
        bool qcNoticed, stockNoticed;

        // 건물·문·화물이 바뀌면 트럭과 화물을 모두 치우고 새로 시작 (하역 수단은 그대로). 평치에는 처음 재고를 둠
        void Reset(SiteDef s, CargoKind cargo)
        {
            foreach (var b in bays) b.truck?.Destroy();
            bays.Clear();
            foreach (var m in movers) Release(m);
            foreach (var z in zones) foreach (var sl in z.slots) if (sl.unit != null) Object.Destroy(sl.unit.gameObject);
            zones.Clear();
            for (int i = 0; i < s.doors.Count; i++)
                bays.Add(new Bay { door = i, inbound = s.doors[i].kind == 0, timer = 3f + i * 4f + (float)rng.NextDouble() * 4f });
            foreach (var g in Plan.Zones(s))
            {
                var z = new StoreZone { kind = g.kind, door = g.door, entryA = g.entryA, entryB = g.entryB, axis = g.axis };
                foreach (var sg in g.slots) z.slots.Add(new StoreSlot { pos = sg.pos, aisle = sg.aisle, face = sg.face });
                zones.Add(z);
            }
            int n = 0;
            foreach (var z in zones)
                if (z.kind == 1)
                    foreach (var sl in z.slots)
                    {
                        if (n >= StartStock) break;
                        var u = new GameObject("Cargo").transform; CargoModels.Build(u, cargo, n);
                        Place(sl, u, s.h); sl.inspected = true; MarkInspected(u); n++;
                    }
            qcNoticed = stockNoticed = false;
        }

        public int Stock { get { int n = 0; foreach (var z in zones) if (z.kind == 1) n += z.Units; return n; } }
        public int StockSlots { get { int n = 0; foreach (var z in zones) if (z.kind == 1) n += z.slots.Count; return n; } }
        public int AwaitingQc { get { int n = 0; foreach (var z in zones) if (z.kind == 0) foreach (var x in z.slots) if (x.unit != null && !x.inspected) n++; return n; } }
        public int InboundStaged { get { int n = 0; foreach (var z in zones) if (z.kind == 0) n += z.Units; return n; } }
        public int OutboundStaged { get { int n = 0; foreach (var z in zones) if (z.kind == 2) n += z.Units; return n; } }

        public Mover AddMover(int kind, Vector3 pos)
        {
            int id = 1; foreach (var m in movers) if (m.kind == kind) id = Mathf.Max(id, m.id + 1);
            var mv = Mover.Create(root, kind, pos, id);
            movers.Add(mv);
            return mv;
        }

        public void RemoveMover(Mover m)
        {
            Release(m);
            movers.Remove(m);
            Object.Destroy(m.t.gameObject);
        }

        // 하던 일을 그만둠: 든 화물은 사라지고, 잡아 둔 자리와 구역을 놓음
        void Release(Mover m)
        {
            m.job = null;
            if (m.load != null) { Object.Destroy(m.load.gameObject); m.load = null; }
            if (m.bay != null)
            {
                m.bay.Assigned--;
                if (m.slotIdx >= 0 && m.bay.truck != null) m.bay.truck.reserved[m.slotIdx] = false;
            }
            if (m.pickFor != null) { m.pickFor.picking--; m.pickFor = null; }
            foreach (var c in m.claims) c.reserved = false;
            m.claims.Clear();
            Leave(m);
            m.bay = null; m.slotIdx = -1; m.State = "대기";
        }

        public void Tick(float dt, SiteDef s, CargoKind cargo)
        {
            string ns = (int)cargo + "|" + s.x + "," + s.z + "," + s.w + "," + s.d + "," + s.h + "|" + s.doors.Count;
            foreach (var d in s.doors) ns += ";" + d.kind + d.side + d.pos.ToString("0.0");
            if (ns != sig) { sig = ns; Reset(s, cargo); }
            if (dt <= 0) return;
            clock += dt; dtNow = dt; hNow = s.h; siteNow = s;
            foreach (var b in bays) TickBay(b, dt, s, cargo);
            foreach (var m in movers) TickMover(m, dt, s, cargo);
            if (!qcNoticed && AwaitingQc > 0 && !AnyWorker())
            {
                qcNoticed = true;
                Notice?.Invoke("입고 화물을 검수할 작업자가 없습니다. 라이브러리 '하역 수단'에서 작업자를 센터 안에 놓으세요.");
            }
        }

        // ---------------------------------------------------------------- 트럭
        void TickBay(Bay b, float dt, SiteDef s, CargoKind cargo)
        {
            var door = s.doors[b.door];
            var dp = Plan.DoorPoint(s, door); var n = Plan.Outward(door.side);
            Vector3 V(Vector2 p) => new Vector3(p.x, 0, p.y);
            var D = dp + n * (Truck.L / 2 + 0.25f);
            switch (b.state)
            {
                case 0:
                    b.timer -= dt;
                    if (b.timer > 0) break;
                    b.truck = new Truck(root, s.h, cabColors[rng.Next(cabColors.Length)]);
                    var arrive = Plan.Route(s, door, true);
                    b.truck.root.position = V(arrive[0]);
                    b.truck.root.rotation = Quaternion.LookRotation(V(arrive.Count > 1 ? arrive[1] - arrive[0] : Vector2.right));
                    b.units = 4 + rng.Next(Truck.Rows * Truck.Cols - 3);
                    b.done = 0; b.late = false; b.noticed = false;
                    if (b.inbound)
                        for (int i = 0; i < b.units; i++)
                        {
                            var u = new GameObject("Cargo").transform;
                            CargoModels.Build(u, cargo, i);
                            b.truck.Put(i, u);
                        }
                    var go = new List<(Vector3, bool)>();
                    for (int i = 1; i < arrive.Count; i++) go.Add((V(arrive[i]), false));
                    go.Add((V(D), true));
                    b.truck.SetPath(go.ToArray());
                    b.state = 1;
                    break;
                case 1:
                    if (b.truck.Drive(dt)) { b.state = 2; b.due = clock + Window; }
                    break;
                case 2:
                    if (b.truck.SetWing(1f, dt)) b.state = 3;
                    break;
                case 3:
                    bool finished = b.inbound ? b.truck.Loaded == 0 && b.Assigned == 0 : b.done >= b.units && b.Assigned == 0;
                    if (finished)
                    {
                        int reward = b.units * RewardPerUnit + OnTimeBonus;
                        balance += reward; onTime++;
                        Notice?.Invoke($"{Plan.DoorName(s, b.door)}: 화주 주문 {b.units}개 처리 완료  +{reward}만원");
                        b.state = 4;
                    }
                    else if (clock > b.due)
                    {
                        int left = b.units - b.done;
                        int pen = Penalty + left * PenaltyPerUnit;
                        balance -= pen; missed++; b.late = true;
                        Notice?.Invoke($"{Plan.DoorName(s, b.door)}: 마감 초과, {left}개 미처리  −{pen}만원");
                        foreach (var m in movers) if (m.bay == b) Release(m);
                        b.state = 4;
                    }
                    else if (!b.noticed && b.Assigned == 0 && !AnyMover(cargo))
                    {
                        b.noticed = true;
                        Notice?.Invoke(cargo == CargoKind.Pallet || cargo == CargoKind.Drum
                            ? "트럭이 윙을 열고 기다립니다. 왼쪽 라이브러리 '하역 수단'에서 지게차를 센터 안에 놓으면 하역을 시작합니다."
                            : "트럭이 윙을 열고 기다립니다. 지게차나 작업자를 센터 안에 놓으면 하역을 시작합니다.");
                    }
                    else if (!b.inbound && !stockNoticed && Stock == 0 && OutboundStaged == 0 && b.picking == 0)
                    {
                        stockNoticed = true;
                        Notice?.Invoke("출고 지시가 나왔지만 평치 재고가 없습니다. 입고 화물을 검수해 평치에 적치해야 출고할 수 있습니다.");
                    }
                    break;
                case 4:
                    if (b.truck.SetWing(0f, dt))
                    {
                        var leave = Plan.Route(s, door, false);
                        var back = new List<(Vector3, bool)>();
                        foreach (var q in leave) back.Add((V(q), false));
                        b.truck.SetPath(back.ToArray());
                        b.state = 5;
                    }
                    break;
                case 5:
                    if (b.truck.Drive(dt))
                    {
                        b.truck.Destroy(); b.truck = null;
                        b.state = 0; b.timer = 15f + (float)rng.NextDouble() * 25f;
                    }
                    break;
            }
        }

        bool AnyMover(CargoKind c) { foreach (var m in movers) if (MoverInfo.Handles(m.kind, c)) return true; return false; }
        bool AnyWorker() { foreach (var m in movers) if (m.kind == MoverInfo.Worker) return true; return false; }

        public const float InspectTime = 2.5f;     // 화물 하나 검수 (s)
        public int inspected, stored;              // 검수한 수, 평치에 적치한 수

        StoreZone StageOf(int door) { foreach (var z in zones) if (z.door == door) return z; return null; }

        static int LoadedTruckSlot(Bay b)
        {
            for (int i = 0; i < b.truck.slot.Length; i++) if (b.truck.slot[i] != null && !b.truck.reserved[i]) return i;   // 뒤쪽부터
            return -1;
        }

        static int FreeTruckSlot(Bay b)
        {
            for (int i = b.truck.slot.Length - 1; i >= 0; i--) if (b.truck.slot[i] == null && !b.truck.reserved[i]) return i;   // 앞쪽부터
            return -1;
        }

        static void Claim(Mover m, StoreSlot sl) { sl.reserved = true; m.claims.Add(sl); }
        static void Unclaim(Mover m, StoreSlot sl) { sl.reserved = false; m.claims.Remove(sl); }

        void ClaimTruck(Mover m, Bay b, int ti)
        {
            m.bay = b; b.Assigned++;
            m.slotIdx = ti; b.truck.reserved[ti] = true;
        }

        // ---------------------------------------------------------------- 하역 수단: 할 일 고르기
        void TickMover(Mover m, float dt, SiteDef s, CargoKind cargo)
        {
            if (m.job != null)
            {
                if (!m.job.MoveNext()) m.job = null;
                return;
            }
            m.job = PickJob(m, s, cargo);
            if (m.job == null)
                m.State = m.kind == MoverInfo.Worker && !MoverInfo.Handles(m.kind, cargo) ? "대기 (검수할 화물 없음)" : "대기";
        }

        // 우선순위: 검수(작업자) → 상차 → 하차 → 출고 피킹(평치 → 출고장) → 적치(대기 구역 → 평치)
        IEnumerator<object> PickJob(Mover m, SiteDef s, CargoKind cargo)
        {
            if (m.kind == MoverInfo.Worker)
                foreach (var z in zones)
                {
                    if (z.kind != 0) continue;
                    foreach (var sl in z.slots)
                        if (sl.unit != null && !sl.inspected && !sl.reserved) { Claim(m, sl); return InspectJob(m, z, sl).GetEnumerator(); }
                }
            if (!MoverInfo.Handles(m.kind, cargo)) return null;

            foreach (var b in bays)
            {
                if (b.inbound || b.state != 3 || b.truck == null || b.Assigned > 0 || b.done >= b.units) continue;
                var z = StageOf(b.door); if (z == null) continue;
                var sl = z.slots.Find(x => x.unit != null && !x.reserved);
                int ti = FreeTruckSlot(b);
                if (sl == null || ti < 0) continue;
                Claim(m, sl); ClaimTruck(m, b, ti);
                return LoadJob(m, s, b, z, sl).GetEnumerator();
            }
            foreach (var b in bays)
            {
                if (!b.inbound || b.state != 3 || b.truck == null || b.Assigned > 0) continue;
                var z = StageOf(b.door); if (z == null) continue;
                var sl = z.Free();
                int ti = LoadedTruckSlot(b);
                if (sl == null || ti < 0) continue;
                Claim(m, sl); ClaimTruck(m, b, ti);
                return UnloadJob(m, s, b, z, sl).GetEnumerator();
            }
            foreach (var b in bays)
            {
                if (b.inbound || b.state != 3) continue;
                var z = StageOf(b.door); if (z == null) continue;
                int need = b.units - b.done - z.Units - b.picking - b.Assigned;
                if (need <= 0) continue;
                var dst = z.Free(); if (dst == null) continue;
                StoreZone fromZ = null; StoreSlot from = null;
                foreach (var bz in zones)
                {
                    if (bz.kind != 1) continue;
                    from = bz.slots.Find(x => x.unit != null && x.inspected && !x.reserved);
                    if (from != null) { fromZ = bz; break; }
                }
                if (from == null) break;
                Claim(m, from); Claim(m, dst);
                b.picking++; m.pickFor = b;
                return MoveJob(m, fromZ, from, z, dst).GetEnumerator();
            }
            foreach (var z in zones)
            {
                if (z.kind != 0) continue;
                var sl = z.slots.Find(x => x.unit != null && x.inspected && !x.reserved);
                if (sl == null) continue;
                StoreZone toZ = null; StoreSlot to = null;
                foreach (var bz in zones)
                {
                    if (bz.kind != 1) continue;
                    to = bz.Free();
                    if (to != null) { toZ = bz; break; }
                }
                if (to == null) return null;   // 평치가 가득 참
                Claim(m, sl); Claim(m, to);
                return MoveJob(m, z, sl, toZ, to).GetEnumerator();
            }
            return null;
        }

        // ---------------------------------------------------------------- 하역 수단: 움직임 조각
        Vector3 V3(Vector2 p) => new Vector3(p.x, hNow, p.y);

        IEnumerable<object> Go(Mover m, Vector2 p, bool rev = false)
        {
            var tgt = V3(p);
            while (!m.MoveTo(tgt, dtNow, rev)) yield return null;
        }

        IEnumerable<object> Go3(Mover m, Vector3 p, bool rev = false)
        {
            while (!m.MoveTo(p, dtNow, rev)) yield return null;
        }

        IEnumerable<object> Wait(float sec)
        {
            for (float t0 = 0; t0 < sec; t0 += dtNow) yield return null;
        }

        // 대기 구역 · 평치 통로에는 한 대씩만 들어감 (화물 · 장비끼리 겹치지 않게)
        IEnumerable<object> Enter(Mover m, StoreZone z)
        {
            string was = m.State;
            while (z.holder != null && z.holder != m) { m.State = "통로가 비기를 기다림"; yield return null; }
            z.holder = m; m.holding = z; m.State = was;
        }

        void Leave(Mover m)
        {
            if (m.holding != null && m.holding.holder == m) m.holding.holder = null;
            m.holding = null;
        }

        // 건물 안 이동: 대기 구역과 평치 사이 통로를 따라 (직각으로)
        IEnumerable<object> Travel(Mover m, Vector2 to)
        {
            var from = new Vector2(m.t.position.x, m.t.position.z);
            var pts = new List<Vector2>();
            if (siteNow != null && siteNow.doors.Count > 0)
            {
                Plan.WallFrame(siteNow, Plan.DockSide(siteNow), out var o, out var al, out var iw);
                float cd = Plan.CrossDepth;
                float af = Vector2.Dot(from - o, al), df = Vector2.Dot(from - o, iw), at = Vector2.Dot(to - o, al);
                if (Mathf.Abs(df - cd) > 0.3f) pts.Add(o + al * af + iw * cd);
                if (Mathf.Abs(at - af) > 0.3f) pts.Add(o + al * at + iw * cd);
            }
            pts.Add(to);
            foreach (var p in pts)
                foreach (var st in Go(m, p)) yield return st;
        }

        // 화물 자리 앞: 통로에서 옆으로 돌아 포크(손) 끝이 자리 가운데에 오도록
        static Vector2 Reach(Mover m, StoreSlot sl) => sl.pos - sl.face * (m.kind == MoverInfo.Forklift ? 1.5f : 0.45f);

        IEnumerable<object> IntoSlot(Mover m, StoreSlot sl)
        {
            foreach (var st in Go(m, sl.aisle)) yield return st;
            foreach (var st in Go(m, Reach(m, sl))) yield return st;
        }

        IEnumerable<object> OutOfSlot(Mover m, StoreSlot sl)
        {
            foreach (var st in Go(m, sl.aisle, true)) yield return st;
        }

        void Place(StoreSlot sl, Transform u, float h)
        {
            u.SetParent(root, false); u.localScale = Vector3.one;
            u.position = new Vector3(sl.pos.x, h, sl.pos.y);
            u.rotation = Quaternion.LookRotation(new Vector3(sl.face.x, 0, sl.face.y));
            sl.unit = u;
        }

        static void MarkInspected(Transform u)
        {
            float top = 0.3f;
            foreach (var r in u.GetComponentsInChildren<Renderer>()) top = Mathf.Max(top, r.bounds.max.y - u.position.y);
            var g = Truck.Box(u, new Vector3(0, top + 0.03f, 0), new Vector3(0.45f, 0.06f, 0.45f), Truck.Mat("QcOk", new Color(0.15f, 0.75f, 0.35f), 0.4f));
            g.name = "QC";
        }

        // ---------------------------------------------------------------- 하역 수단: 일
        // 검수: 입고 대기 구역의 화물 옆에서 잠시 확인 → 초록 표시
        IEnumerable<object> InspectJob(Mover m, StoreZone z, StoreSlot sl)
        {
            m.State = "검수하러 가는 중";
            foreach (var st in Travel(m, z.entryB)) yield return st;
            foreach (var st in Enter(m, z)) yield return st;
            foreach (var st in IntoSlot(m, sl)) yield return st;
            m.State = "검수 중";
            foreach (var st in Wait(InspectTime)) yield return st;
            if (sl.unit != null && !sl.inspected) { sl.inspected = true; MarkInspected(sl.unit); inspected++; }
            Unclaim(m, sl);
            foreach (var st in OutOfSlot(m, sl)) yield return st;
            foreach (var st in Go(m, z.entryB)) yield return st;
            Leave(m);
        }

        // 하차: 대기 구역 통로 → 문 → 트럭 칸 → (후진) 문 → 대기 구역 빈자리
        IEnumerable<object> UnloadJob(Mover m, SiteDef s, Bay b, StoreZone z, StoreSlot sl)
        {
            m.State = $"{Plan.DoorName(s, b.door)} 하차";
            foreach (var st in Travel(m, z.entryB)) yield return st;
            foreach (var st in Enter(m, z)) yield return st;
            foreach (var st in Go(m, z.entryA)) yield return st;
            if (b.truck == null) { Release(m); yield break; }
            foreach (var st in Go3(m, Approach(b, m))) yield return st;
            foreach (var st in Wait(1.2f)) yield return st;
            if (b.truck == null) { Release(m); yield break; }
            var u = b.truck.slot[m.slotIdx];
            b.truck.slot[m.slotIdx] = null; b.truck.reserved[m.slotIdx] = false; m.slotIdx = -1;
            if (u != null) m.Carry(u);
            foreach (var st in Go(m, z.entryA, true)) yield return st;
            m.bay = null; b.Assigned--;
            foreach (var st in IntoSlot(m, sl)) yield return st;
            foreach (var st in Wait(0.8f)) yield return st;
            if (m.load != null) { var lu = m.load; m.load = null; Place(sl, lu, hNow); sl.inspected = false; b.done++; inDone++; }
            Unclaim(m, sl);
            foreach (var st in OutOfSlot(m, sl)) yield return st;
            foreach (var st in Go(m, z.entryB)) yield return st;
            Leave(m);
        }

        // 상차: 출고장 화물 → 문 → 트럭 빈 칸 → (후진) 문 → 통로로 나옴
        IEnumerable<object> LoadJob(Mover m, SiteDef s, Bay b, StoreZone z, StoreSlot sl)
        {
            m.State = $"{Plan.DoorName(s, b.door)} 상차";
            foreach (var st in Travel(m, z.entryB)) yield return st;
            foreach (var st in Enter(m, z)) yield return st;
            foreach (var st in IntoSlot(m, sl)) yield return st;
            foreach (var st in Wait(1.0f)) yield return st;
            if (sl.unit != null) { m.Carry(sl.unit); sl.unit = null; sl.inspected = false; }
            Unclaim(m, sl);
            foreach (var st in OutOfSlot(m, sl)) yield return st;
            foreach (var st in Go(m, z.entryA)) yield return st;
            if (b.truck == null) { Release(m); yield break; }
            foreach (var st in Go3(m, Approach(b, m))) yield return st;
            foreach (var st in Wait(0.8f)) yield return st;
            if (b.truck == null) { Release(m); yield break; }
            if (m.load != null) { b.truck.Put(m.slotIdx, m.load); m.load = null; b.done++; outDone++; }
            b.truck.reserved[m.slotIdx] = false; m.slotIdx = -1;
            foreach (var st in Go(m, z.entryA, true)) yield return st;
            m.bay = null; b.Assigned--;
            foreach (var st in Go(m, z.entryB)) yield return st;
            Leave(m);
        }

        // 옮기기: 적치(입고 대기 구역 → 평치) 또는 출고 피킹(평치 → 출고장)
        IEnumerable<object> MoveJob(Mover m, StoreZone za, StoreSlot sa, StoreZone zb, StoreSlot sb)
        {
            m.State = zb.kind == 1 ? "평치 적치" : "출고 피킹 (평치 → 출고장)";
            foreach (var st in Travel(m, za.entryB)) yield return st;
            foreach (var st in Enter(m, za)) yield return st;
            foreach (var st in IntoSlot(m, sa)) yield return st;
            foreach (var st in Wait(1.0f)) yield return st;
            bool ok = sa.inspected;
            if (sa.unit != null) { m.Carry(sa.unit); sa.unit = null; sa.inspected = false; }
            Unclaim(m, sa);
            foreach (var st in OutOfSlot(m, sa)) yield return st;
            foreach (var st in Go(m, za.entryB)) yield return st;
            Leave(m);
            foreach (var st in Travel(m, zb.entryB)) yield return st;
            foreach (var st in Enter(m, zb)) yield return st;
            foreach (var st in IntoSlot(m, sb)) yield return st;
            foreach (var st in Wait(0.8f)) yield return st;
            if (m.load != null) { var lu = m.load; m.load = null; Place(sb, lu, hNow); sb.inspected = ok; if (zb.kind == 1) stored++; }
            Unclaim(m, sb);
            if (m.pickFor != null) { m.pickFor.picking--; m.pickFor = null; }
            foreach (var st in OutOfSlot(m, sb)) yield return st;
            foreach (var st in Go(m, zb.entryB)) yield return st;
            Leave(m);
        }

        // 트럭 칸 앞: 포크(손) 끝이 칸 가운데에 오도록 조금 앞에서 멈춤
        static Vector3 Approach(Bay bay, Mover m)
        {
            var p = bay.truck.SlotWorld(m.slotIdx);
            var toDoor = -bay.truck.root.forward;   // 트럭은 문을 등지고 있음 → 뒤쪽 = 문 방향
            float reach = m.kind == MoverInfo.Forklift ? 1.5f : 0.45f;
            var q = p + toDoor * reach; q.y = bay.truck.bed;
            return q;
        }

        // HUD 이름표용: 주차한 트럭의 주문 상태
        public IEnumerable<(Vector3 pos, string text, bool late)> Tags(SiteDef s)
        {
            foreach (var b in bays)
            {
                if (b.truck == null || b.state < 2 || b.state > 3) continue;
                float left = Mathf.Max(0, b.due - clock);
                string t = $"{(b.inbound ? "입고" : "출고")} {b.done}/{b.units}  ·  마감 {(int)(left / 60)}:{(int)(left % 60):00}";
                yield return (b.truck.root.position + Vector3.up * 4.6f, t, left < 40f);
            }
        }

        public int TrucksWaiting { get { int n = 0; foreach (var b in bays) if (b.state == 2 || b.state == 3) n++; return n; } }
    }
}
