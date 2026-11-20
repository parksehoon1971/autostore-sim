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
        public Bay bay;               // 맡은 트럭 자리
        public int step, slotIdx = -1;
        public Vector3 target;
        public Transform load;
        public float wait;
        public string State = "대기";

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

        public bool MoveTo(Vector3 p, float dt)
        {
            var d = p - t.position; float dist = d.magnitude;
            float step = MoverInfo.Speed(kind) * dt;
            var flat = new Vector3(d.x, 0, d.z);
            if (flat.sqrMagnitude > 0.0004f) t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(flat), 360f * dt);
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
        public readonly List<Transform> stage = new List<Transform>();   // 입고 대기 구역에 내려놓은 화물
        public int Assigned;          // 맡은 하역 수단 수
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

        // 건물·문·화물이 바뀌면 트럭과 대기 화물을 모두 치우고 새로 시작 (하역 수단은 그대로)
        void Reset(SiteDef s)
        {
            foreach (var b in bays) { b.truck?.Destroy(); foreach (var u in b.stage) if (u != null) Object.Destroy(u.gameObject); }
            bays.Clear();
            foreach (var m in movers) Release(m);
            for (int i = 0; i < s.doors.Count; i++)
                bays.Add(new Bay { door = i, inbound = s.doors[i].kind == 0, timer = 3f + i * 4f + (float)rng.NextDouble() * 4f });
        }

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

        void Release(Mover m)
        {
            if (m.load != null) { Object.Destroy(m.load.gameObject); m.load = null; }
            if (m.bay != null)
            {
                m.bay.Assigned--;
                if (m.slotIdx >= 0 && m.bay.truck != null) m.bay.truck.reserved[m.slotIdx] = false;
            }
            m.bay = null; m.slotIdx = -1; m.step = 0; m.State = "대기";
        }

        public void Tick(float dt, SiteDef s, CargoKind cargo)
        {
            string ns = (int)cargo + "|" + s.x + "," + s.z + "," + s.w + "," + s.d + "," + s.h + "|" + s.doors.Count;
            foreach (var d in s.doors) ns += ";" + d.kind + d.side + d.pos.ToString("0.0");
            if (ns != sig) { sig = ns; Reset(s); }
            if (dt <= 0) return;
            clock += dt;
            foreach (var b in bays) TickBay(b, dt, s, cargo);
            foreach (var m in movers) TickMover(m, dt, s, cargo);
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

        // 하역 수단이 할 일이 남은 자리
        bool NeedsWork(Bay b)
        {
            if (b.state != 3 || b.truck == null || b.Assigned >= 3) return false;
            if (b.inbound) { for (int i = 0; i < b.truck.slot.Length; i++) if (b.truck.slot[i] != null && !b.truck.reserved[i]) return true; return false; }
            int pending = 0; foreach (var r in b.truck.reserved) if (r) pending++;
            return b.done + pending < b.units;
        }

        // ---------------------------------------------------------------- 하역 수단
        void TickMover(Mover m, float dt, SiteDef s, CargoKind cargo)
        {
            float h = s.h;
            if (m.wait > 0) { m.wait -= dt; return; }
            if (m.bay == null)
            {
                if (!MoverInfo.Handles(m.kind, cargo)) { m.State = cargo == CargoKind.Pallet || cargo == CargoKind.Drum ? "파렛트는 지게차만" : "대기"; return; }
                Bay best = null; float bd = float.MaxValue;
                foreach (var b in bays)
                {
                    if (!NeedsWork(b)) continue;
                    var p = Plan.DoorInside(s, s.doors[b.door]);
                    float d = (new Vector2(m.t.position.x, m.t.position.z) - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = b; }
                }
                if (best == null) { m.State = "대기"; return; }
                m.bay = best; best.Assigned++; m.step = 1;
            }
            var bay = m.bay; var door = s.doors[bay.door];
            var din = Plan.DoorInside(s, door); var dIn3 = new Vector3(din.x, h, din.y);
            var stage = Plan.StageRect(s, door);
            if (bay.truck == null || bay.state != 3) { FinishInbound(m, s); return; }

            if (bay.inbound)
            {
                switch (m.step)
                {
                    case 1:   // 트럭 칸 고르기 (뒤쪽부터)
                        m.slotIdx = -1;
                        for (int i = 0; i < bay.truck.slot.Length; i++)
                            if (bay.truck.slot[i] != null && !bay.truck.reserved[i]) { m.slotIdx = i; break; }
                        if (m.slotIdx < 0) { Release(m); return; }
                        bay.truck.reserved[m.slotIdx] = true;
                        m.State = $"{Plan.DoorName(s, bay.door)} 하차"; m.step = 2;
                        break;
                    case 2: if (m.MoveTo(dIn3, dt)) m.step = 3; break;
                    case 3: if (m.MoveTo(Approach(bay, m), dt)) { m.step = 4; m.wait = 1.2f; } break;
                    case 4:
                        var u = bay.truck.slot[m.slotIdx];
                        bay.truck.slot[m.slotIdx] = null; bay.truck.reserved[m.slotIdx] = false; m.slotIdx = -1;
                        if (u != null) m.Carry(u);
                        m.step = 5;
                        break;
                    case 5: if (m.MoveTo(dIn3, dt)) m.step = 6; break;
                    case 6: if (m.MoveTo(StageSpot(stage, bay.stage.Count, h, door), dt)) { m.step = 7; m.wait = 0.8f; } break;
                    case 7: DropInStage(m, bay, s, door); m.step = 1; if (!NeedsWork(bay)) { Release(m); } break;
                }
            }
            else
            {
                switch (m.step)
                {
                    case 1:   // 빈 칸 고르기 (앞쪽부터)
                        m.slotIdx = -1;
                        for (int i = bay.truck.slot.Length - 1; i >= 0; i--)
                            if (bay.truck.slot[i] == null && !bay.truck.reserved[i]) { m.slotIdx = i; break; }
                        if (m.slotIdx < 0) { Release(m); return; }
                        bay.truck.reserved[m.slotIdx] = true;
                        m.State = $"{Plan.DoorName(s, bay.door)} 상차"; m.step = 2;
                        break;
                    case 2: if (m.MoveTo(StageSpot(stage, 0, h, door), dt)) { m.step = 3; m.wait = 1.2f; } break;
                    case 3:
                        var u = new GameObject("Cargo").transform; CargoModels.Build(u, cargo, m.id);
                        u.SetParent(root, false); m.Carry(u);
                        m.step = 4;
                        break;
                    case 4: if (m.MoveTo(dIn3, dt)) m.step = 5; break;
                    case 5: if (m.MoveTo(Approach(bay, m), dt)) { m.step = 6; m.wait = 0.8f; } break;
                    case 6:
                        if (m.load != null) { bay.truck.Put(m.slotIdx, m.load); m.load = null; }
                        bay.truck.reserved[m.slotIdx] = false; m.slotIdx = -1;
                        bay.done++; outDone++;
                        m.step = 7;
                        break;
                    case 7: if (m.MoveTo(dIn3, dt)) { m.step = 1; if (!NeedsWork(bay)) Release(m); } break;
                }
            }
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

        static Vector3 StageSpot(Rect stage, int i, float h, DoorDef door)
        {
            int k = i % 6, c = k % 2, r = k / 2;
            float fx = 0.3f + c * 0.4f, fz = 0.25f + r * 0.25f;
            if (door.side >= 2) { var t = fx; fx = fz; fz = t; }
            return new Vector3(Mathf.Lerp(stage.xMin, stage.xMax, fx), h, Mathf.Lerp(stage.yMin, stage.yMax, fz));
        }

        void DropInStage(Mover m, Bay bay, SiteDef s, DoorDef door)
        {
            if (m.load == null) return;
            var u = m.load; m.load = null;
            u.SetParent(root, false);
            u.localScale = Vector3.one;
            var p = StageSpot(Plan.StageRect(s, door), bay.stage.Count, s.h, door);
            u.position = new Vector3(p.x, s.h, p.z);
            u.rotation = Quaternion.LookRotation(new Vector3(-Plan.Outward(door.side).x, 0, -Plan.Outward(door.side).y));
            bay.stage.Add(u);
            if (bay.stage.Count > 6) { if (bay.stage[0] != null) Object.Destroy(bay.stage[0].gameObject); bay.stage.RemoveAt(0); }   // 보관 설비로 넘어감
            bay.done++; inDone++;
        }

        void FinishInbound(Mover m, SiteDef s)
        {
            // 트럭이 떠났거나 마감: 들고 있던 화물은 대기 구역에 내려놓고 쉼
            if (m.load != null && m.bay != null && m.bay.inbound) DropInStage(m, m.bay, s, s.doors[m.bay.door]);
            Release(m);
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
