// AutoStore 큐브 스토리지 이산시간 시뮬레이션 (web/sim-core.js 와 같은 로직의 C# 포팅)
// 좌표: 셀 (x, y). y = -1 은 포트 셀. 월드 좌표: wx = (x+0.5)*cellX, wy = (y+0.5)*cellY (Unity에서는 wy → Z)
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoStore
{
    [Serializable]
    public class SimConfig
    {
        public int nx = 12, ny = 8, nz = 16;
        public float cellX = 0.70f, cellY = 0.50f, binH = 0.33f;
        public float fill = 0.80f;
        public int robots = 8;
        public int[] ports = { 2, 5, 8 };
        public float portZ = 0.95f;
        public int portCap = 4;
        public float orderRate = 300f;          // 빈 요청 / 시간
        public float popularShare = 0.2f, popularDemand = 0.8f;
        public float vmax = 3.1f, acc = 0.8f;   // AutoStore R5 공개 사양
        public float turn = 1.0f;               // 방향 전환(바퀴 세트 교체)
        public float liftV = 1.6f, grip = 1.0f;
        public float pickMin = 6f, pickMax = 12f;
        public int seed = 3;
    }

    public enum BinKind { Stack, Robot, Port }
    public struct BinLoc { public BinKind kind; public int x, y, r, p; }

    public class Order { public int id, bin; public double tArrive, tPresent = -1, tDone = -1; }

    public class Port
    {
        public int id, x;
        public List<int> queue = new List<int>();
        public double pickLeft = -1;
        public int outBin = -1; public bool outClaimed;
        public int reserved;
        public double busyTime; public int picked;
    }

    public class Act
    {
        public string k;           // move | lift | lower | wait
        public int x, y;           // move 목표
        public string where;       // lift: stack|port, lower: stack|port
        public double t, T = -1, td, dist; public bool swapped, done;
    }

    public class Segment
    {
        public int d, x0, y0, released; public List<(int x, int y)> cells; public double dist, step, t, T;
    }

    public interface ITask { string Kind { get; } void Next(Robot r); }

    public class Robot
    {
        public int id, x, y, carry = -1, lastDir = -1, digs, deliveries, returns;
        public double px, py, g, blocked, waitT, busyTime, distance;
        public ITask task; public List<Act> acts = new List<Act>(); public Segment seg;
        public string state = "idle";
    }

    public class AutoStoreSim
    {
        public readonly SimConfig c;
        public double t;
        public readonly double topZ, restZ;
        readonly int W, H;
        readonly HashSet<int> portSet;
        public readonly int[] occ;
        public readonly List<List<int>> stacks = new List<List<int>>();
        readonly int[] incoming;
        public BinLoc[] binLoc;
        public readonly HashSet<int> requested = new HashSet<int>();
        public int bins, nPopular, version;
        public readonly List<Port> ports = new List<Port>();
        public readonly List<Robot> robots = new List<Robot>();
        public readonly List<Order> orders = new List<Order>();
        readonly List<Order> pending = new List<Order>();
        readonly Dictionary<int, Order> active = new Dictionary<int, Order>();
        double nextOrderT;
        public int statDone, statDigs, statFetches; public double leadSum, leadMax;
        readonly System.Random rng;

        static readonly int[,] DIRS = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };

        public AutoStoreSim(SimConfig cfg)
        {
            c = cfg; rng = new System.Random(c.seed);
            topZ = c.nz * c.binH + 0.10; restZ = topZ + 0.05;
            W = c.nx; H = c.ny + 1;
            portSet = new HashSet<int>(c.ports);
            occ = Enumerable.Repeat(-1, W * H).ToArray();
            incoming = new int[c.nx * c.ny];
            // 빈 배치: 인기 빈(작은 ID)이 위쪽에 오도록
            var slots = new List<(int x, int y, int k, double key)>();
            for (int y = 0; y < c.ny; y++)
                for (int x = 0; x < c.nx; x++)
                {
                    stacks.Add(new List<int>());
                    int h = (int)Math.Round(c.nz * Math.Min(1, Math.Max(0, c.fill + Gauss() * 0.12)));
                    if (h >= c.nz) h = c.nz - 1;
                    for (int k = 0; k < h; k++) slots.Add((x, y, k, k + Gauss() * c.nz * 0.25));
                }
            var byKey = slots.Select((s, i) => (s, i)).OrderByDescending(p => p.s.key).ToList();
            var ids = new int[slots.Count];
            for (int id = 0; id < byKey.Count; id++) ids[byKey[id].i] = id;
            binLoc = new BinLoc[slots.Count];
            var order = slots.Select((s, i) => (s, i)).OrderBy(p => p.s.k).ToList();
            foreach (var (s, i) in order)
            {
                int id = ids[i];
                Stack(s.x, s.y).Add(id);
                binLoc[id] = new BinLoc { kind = BinKind.Stack, x = s.x, y = s.y };
            }
            bins = slots.Count; nPopular = Math.Max(1, (int)(bins * c.popularShare));
            for (int i = 0; i < c.ports.Length; i++) ports.Add(new Port { id = i, x = c.ports[i] });
            for (int i = 0; i < c.robots; i++)
            {
                int x, y;
                do { x = rng.Next(c.nx); y = rng.Next(c.ny); } while (occ[Ci(x, y)] != -1);
                var r = new Robot { id = i, x = x, y = y };
                SetPos(r); occ[Ci(x, y)] = i; robots.Add(r);
            }
            nextOrderT = Expo(3600.0 / c.orderRate);
        }

        double Rand() => rng.NextDouble();
        double Gauss() { double u = 0; while (u == 0) u = Rand(); double v = Rand(); return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); }
        double Expo(double mean) => -Math.Log(1 - Rand()) * mean;
        public int Ci(int x, int y) => x + (y + 1) * W;
        public bool Valid(int x, int y) => x >= 0 && x < c.nx && ((y >= 0 && y < c.ny) || (y == -1 && portSet.Contains(x)));
        public List<int> Stack(int x, int y) => stacks[x + y * c.nx];
        public double WorldX(int x) => (x + 0.5) * c.cellX;
        public double WorldY(int y) => (y + 0.5) * c.cellY;
        void SetPos(Robot r) { r.px = WorldX(r.x); r.py = WorldY(r.y); }

        // ---------------- 속도 프로파일
        public static double ProfileTime(double d, double v, double a)
        {
            if (d <= 0) return 0;
            return d >= v * v / a ? d / v + v / a : 2 * Math.Sqrt(d / a);
        }
        public static double ProfilePos(double t, double d, double v, double a)
        {
            double T = ProfileTime(d, v, a);
            if (t >= T) return d; if (t <= 0) return 0;
            double vp = v, ta = v / a;
            if (d < v * v / a) { vp = Math.Sqrt(d * a); ta = vp / a; }
            if (t < ta) return 0.5 * a * t * t;
            double dA = 0.5 * a * ta * ta, tc = T - 2 * ta;
            if (t < ta + tc) return dA + vp * (t - ta);
            double td = T - t; return d - 0.5 * a * td * td;
        }

        // ---------------- 경로 계획 (방향 전환 비용 포함 다익스트라)
        List<(int x, int y, int d)> Plan(Robot r, int tx, int ty, bool avoid)
        {
            int N = W * H * 4;
            var cost = Enumerable.Repeat(double.PositiveInfinity, N).ToArray();
            var prev = Enumerable.Repeat(-1, N).ToArray();
            var done = new bool[N];
            int sc = Ci(r.x, r.y);
            double[] stepT = { c.cellX / c.vmax + 0.05, c.cellX / c.vmax + 0.05, c.cellY / c.vmax + 0.05, c.cellY / c.vmax + 0.05 };
            for (int d = 0; d < 4; d++) cost[sc * 4 + d] = (r.lastDir == -1 || (r.lastDir >> 1) == (d >> 1)) ? 0 : c.turn;
            int target = Ci(tx, ty), best = -1;
            while (true)
            {
                int u = -1; double bu = double.PositiveInfinity;
                for (int i = 0; i < N; i++) if (!done[i] && cost[i] < bu) { bu = cost[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                int cell = u >> 2, d0 = u & 3;
                if (cell == target) { best = u; break; }
                int x = cell % W, y = cell / W - 1;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + DIRS[d, 0], ny = y + DIRS[d, 1];
                    if (!Valid(nx, ny)) continue;
                    int nc = Ci(nx, ny);
                    if (avoid && occ[nc] != -1 && occ[nc] != r.id && nc != target) continue;
                    double turn = ((d0 >> 1) == (d >> 1)) ? 0 : c.turn;
                    int v = nc * 4 + d; double nc2 = bu + stepT[d] + turn;
                    if (nc2 < cost[v]) { cost[v] = nc2; prev[v] = u; }
                }
            }
            if (best < 0) return null;
            var path = new List<(int, int, int)>();
            int w = best;
            while (w >= 0 && (w >> 2) != sc) { int cell = w >> 2; path.Add((cell % W, cell / W - 1, w & 3)); w = prev[w]; }
            path.Reverse();
            return path;
        }

        // ---------------- 로봇 행동
        void StepRobot(Robot r, double dt)
        {
            if (r.task != null || r.acts.Count > 0 || r.seg != null) r.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 8)
            {
                if (r.acts.Count == 0)
                {
                    r.task?.Next(r);
                    if (r.acts.Count == 0) { if (r.task == null) r.state = "idle"; return; }
                }
                var a = r.acts[0];
                double used = a.k switch
                {
                    "move" => ActMove(r, a, dt),
                    "lift" => GripAct(r, a, dt, true),
                    "lower" => GripAct(r, a, dt, false),
                    _ => ActWait(a, dt),
                };
                if (a.done) r.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }

        double ActWait(Act a, double dt) { a.t -= dt; if (a.t <= 0) { a.done = true; return dt + a.t; } return dt; }

        double ActMove(Robot r, Act a, double dt)
        {
            if (r.seg == null)
            {
                if (r.x == a.x && r.y == a.y) { a.done = true; return 0; }
                if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; return dt; }
                var path = Plan(r, a.x, a.y, r.blocked > 1.5);
                if (path == null || path.Count == 0) path = Plan(r, a.x, a.y, false);
                if (path == null || path.Count == 0) { r.blocked += dt; return dt; }
                int d = path[0].d;
                var cells = new List<(int x, int y)>();
                foreach (var p in path)
                {
                    if (p.d != d) break;
                    int o = occ[Ci(p.x, p.y)];
                    if (o != -1 && o != r.id) break;
                    cells.Add((p.x, p.y));
                }
                if (cells.Count == 0)
                {
                    r.blocked += dt; r.state = "blocked";
                    int o = occ[Ci(path[0].x, path[0].y)];
                    if (o >= 0) Nudge(robots[o], path);
                    if (r.blocked > 6) { Sidestep(r); r.blocked = 3; }
                    r.waitT = 0.25;
                    return dt;
                }
                r.blocked = 0;
                foreach (var p in cells) occ[Ci(p.x, p.y)] = r.id;
                double step = (d >> 1) == 0 ? c.cellX : c.cellY;
                double dist = cells.Count * step;
                double turnT = (r.lastDir != -1 && (r.lastDir >> 1) != (d >> 1)) ? c.turn : 0;
                r.seg = new Segment { d = d, cells = cells, x0 = r.x, y0 = r.y, dist = dist, step = step, t = -turnT, T = ProfileTime(dist, c.vmax, c.acc) };
                r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move";
            }
            var s = r.seg;
            s.t += dt;
            double used = dt;
            if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
            double pos = s.t <= 0 ? 0 : ProfilePos(s.t, s.dist, c.vmax, c.acc);
            int dx = DIRS[s.d, 0], dy = DIRS[s.d, 1];
            r.px = WorldX(s.x0) + dx * pos; r.py = WorldY(s.y0) + dy * pos;
            int passed = (int)Math.Floor((pos + s.step * 0.5) / s.step);
            while (s.released < passed && s.released < s.cells.Count)
            {
                var pc = s.released == 0 ? (s.x0, s.y0) : s.cells[s.released - 1];
                occ[Ci(pc.Item1, pc.Item2)] = -1; s.released++;
                r.x = s.cells[s.released - 1].x; r.y = s.cells[s.released - 1].y;
            }
            if (s.t >= s.T)
            {
                while (s.released < s.cells.Count)
                {
                    var pc = s.released == 0 ? (s.x0, s.y0) : s.cells[s.released - 1];
                    occ[Ci(pc.Item1, pc.Item2)] = -1; s.released++;
                }
                var last = s.cells[s.cells.Count - 1];
                r.x = last.x; r.y = last.y; occ[Ci(r.x, r.y)] = r.id; SetPos(r);
                r.distance += s.dist; r.seg = null;
                if (r.x == a.x && r.y == a.y) a.done = true;
            }
            return used;
        }

        void Nudge(Robot o, List<(int x, int y, int d)> path)
        {
            if (o.task != null || o.acts.Count > 0 || o.seg != null) return;
            var onPath = new HashSet<int>(path.Select(p => Ci(p.x, p.y)));
            var opts = new List<(int, int)>();
            for (int i = 0; i < 4; i++)
            {
                int nx = o.x + DIRS[i, 0], ny = o.y + DIRS[i, 1];
                if (!Valid(nx, ny) || ny < 0) continue;
                int k = Ci(nx, ny);
                if (occ[k] != -1 || onPath.Contains(k)) continue;
                opts.Add((nx, ny));
            }
            if (opts.Count == 0)
                for (int i = 0; i < 4; i++)
                {
                    int nx = o.x + DIRS[i, 0], ny = o.y + DIRS[i, 1];
                    if (Valid(nx, ny) && ny >= 0 && occ[Ci(nx, ny)] == -1) opts.Add((nx, ny));
                }
            if (opts.Count > 0) { var p = opts[rng.Next(opts.Count)]; o.acts.Add(new Act { k = "move", x = p.Item1, y = p.Item2 }); }
        }

        void Sidestep(Robot r)
        {
            var opts = new List<(int, int)>();
            for (int i = 0; i < 4; i++)
            {
                int nx = r.x + DIRS[i, 0], ny = r.y + DIRS[i, 1];
                if (Valid(nx, ny) && occ[Ci(nx, ny)] == -1) opts.Add((nx, ny));
            }
            if (opts.Count > 0) { var p = opts[rng.Next(opts.Count)]; r.acts.Insert(0, new Act { k = "move", x = p.Item1, y = p.Item2 }); }
        }

        double GripDist(Robot r, string where, bool isLift)
        {
            if (where == "port") return restZ - c.portZ;
            int h = Stack(r.x, r.y).Count;
            return restZ - (isLift ? (h - 1) : h) * c.binH;
        }

        double GripAct(Robot r, Act a, double dt, bool isLift)
        {
            if (a.T < 0)
            {
                a.dist = GripDist(r, a.where, isLift);
                a.td = a.dist / c.liftV; a.T = 2 * a.td + c.grip; a.t = 0; a.swapped = false;
                r.state = isLift ? "lift" : "lower";
            }
            a.t += dt;
            double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
            if (a.t < a.td) r.g = a.dist * a.t / a.td;
            else if (a.t < a.td + c.grip) r.g = a.dist;
            else r.g = a.dist * (1 - (a.t - a.td - c.grip) / a.td);
            if (!a.swapped && a.t >= a.td + c.grip * 0.5)
            {
                a.swapped = true;
                if (isLift)
                {
                    if (a.where == "stack") { var s = Stack(r.x, r.y); r.carry = s[s.Count - 1]; s.RemoveAt(s.Count - 1); }
                    else { var p = PortAt(r.x); r.carry = p.outBin; p.outBin = -1; p.outClaimed = false; }
                    binLoc[r.carry] = new BinLoc { kind = BinKind.Robot, r = r.id };
                }
                else
                {
                    int id = r.carry; r.carry = -1;
                    if (a.where == "stack")
                    {
                        Stack(r.x, r.y).Add(id); incoming[r.x + r.y * c.nx]--;
                        binLoc[id] = new BinLoc { kind = BinKind.Stack, x = r.x, y = r.y };
                    }
                    else
                    {
                        var p = PortAt(r.x); p.queue.Add(id); p.reserved--;
                        binLoc[id] = new BinLoc { kind = BinKind.Port, p = p.id };
                    }
                }
                version++;
            }
            if (a.t >= a.T) { a.done = true; r.g = 0; }
            return used;
        }

        public Port PortAt(int x) => ports.First(p => p.x == x);

        (int x, int y)? FindDrop(int fx, int fy, (int x, int y)? exclude)
        {
            (int, int)? best = null; double bd = double.PositiveInfinity;
            for (int y = 0; y < c.ny; y++)
                for (int x = 0; x < c.nx; x++)
                {
                    if (exclude.HasValue && exclude.Value.x == x && exclude.Value.y == y) continue;
                    int k = x + y * c.nx;
                    if (stacks[k].Count + incoming[k] >= c.nz - 1) continue;
                    double pen = 0;
                    foreach (var id in stacks[k]) if (requested.Contains(id)) { pen = 3; break; }
                    if (occ[Ci(x, y)] != -1) pen += 1.5;
                    double d = Math.Abs(x - fx) * c.cellX + Math.Abs(y - fy) * c.cellY + pen + stacks[k].Count * 0.01;
                    if (d < bd) { bd = d; best = (x, y); }
                }
            if (best.HasValue) incoming[best.Value.Item1 + best.Value.Item2 * c.nx]++;
            return best;
        }

        // ---------------- 작업
        class FetchTask : ITask
        {
            readonly AutoStoreSim sim; public readonly Order order; public readonly Port port; string phase = "goto";
            public FetchTask(AutoStoreSim s, Order o, Port p) { sim = s; order = o; port = p; }
            public string Kind => "fetch";
            public void Next(Robot r)
            {
                if (phase == "goto")
                {
                    var L = sim.binLoc[order.bin];
                    if (r.x != L.x || r.y != L.y) { r.acts.Add(new Act { k = "move", x = L.x, y = L.y }); return; }
                    var s = sim.Stack(r.x, r.y);
                    int depth = s.Count - 1 - s.IndexOf(order.bin);
                    if (depth > 0)
                    {
                        var drop = sim.FindDrop(r.x, r.y, (r.x, r.y)).Value;
                        r.acts.Add(new Act { k = "lift", where = "stack" });
                        r.acts.Add(new Act { k = "move", x = drop.x, y = drop.y });
                        r.acts.Add(new Act { k = "lower", where = "stack" });
                        r.digs++; sim.statDigs++; r.state = "dig";
                        return;
                    }
                    r.acts.Add(new Act { k = "lift", where = "stack" }); phase = "deliver"; return;
                }
                if (phase == "deliver")
                {
                    r.acts.Add(new Act { k = "move", x = port.x, y = -1 });
                    r.acts.Add(new Act { k = "lower", where = "port" });
                    phase = "after"; return;
                }
                if (phase == "after")
                {
                    r.deliveries++; sim.statFetches++;
                    if (port.outBin >= 0 && !port.outClaimed) { port.outClaimed = true; r.task = new ReturnTask(sim, port); r.task.Next(r); return; }
                    r.task = null; if (r.y == -1) r.acts.Add(new Act { k = "move", x = r.x, y = 0 });
                }
            }
        }

        class ReturnTask : ITask
        {
            readonly AutoStoreSim sim; readonly Port port; string phase = "goto";
            public ReturnTask(AutoStoreSim s, Port p) { sim = s; port = p; }
            public string Kind => "return";
            public void Next(Robot r)
            {
                if (phase == "goto")
                {
                    r.acts.Add(new Act { k = "move", x = port.x, y = -1 });
                    r.acts.Add(new Act { k = "lift", where = "port" }); phase = "store"; return;
                }
                if (phase == "store")
                {
                    var d = sim.FindDrop(r.x, Math.Max(0, r.y), null).Value;
                    r.acts.Add(new Act { k = "move", x = d.x, y = d.y });
                    r.acts.Add(new Act { k = "lower", where = "stack" }); phase = "end"; return;
                }
                r.returns++; r.task = null;
            }
        }

        // ---------------- 주문 / 포트 / 배차
        void NewOrder()
        {
            for (int tries = 0; tries < 40; tries++)
            {
                bool pop = Rand() < c.popularDemand;
                int id = pop ? (int)(Rand() * nPopular) : nPopular + (int)(Rand() * (bins - nPopular));
                if (requested.Contains(id) || binLoc[id].kind != BinKind.Stack) continue;
                requested.Add(id);
                var o = new Order { id = orders.Count, bin = id, tArrive = t };
                orders.Add(o); pending.Add(o); return;
            }
        }

        void StepPorts(double dt)
        {
            foreach (var p in ports)
            {
                if (p.queue.Count == 0) continue;
                p.busyTime += dt;
                int id = p.queue[0];
                if (p.pickLeft < 0)
                {
                    p.pickLeft = c.pickMin + Rand() * (c.pickMax - c.pickMin);
                    if (active.TryGetValue(id, out var o)) o.tPresent = t;
                }
                p.pickLeft -= dt;
                if (p.pickLeft <= 0 && p.outBin < 0)
                {
                    p.queue.RemoveAt(0); p.outBin = id; p.outClaimed = false; p.pickLeft = -1; p.picked++;
                    if (active.TryGetValue(id, out var o))
                    {
                        o.tDone = t; requested.Remove(id); statDone++;
                        double lead = o.tPresent - o.tArrive; leadSum += lead; leadMax = Math.Max(leadMax, lead);
                        active.Remove(id);
                    }
                }
            }
        }

        void Dispatch()
        {
            var idle = robots.Where(r => r.task == null && r.acts.Count == 0 && r.seg == null).ToList();
            double Dist(Robot r, int x, int y) => Math.Abs(r.x - x) * c.cellX + Math.Abs(r.y - y) * c.cellY;
            foreach (var p in ports)
            {
                if (p.outBin >= 0 && !p.outClaimed && idle.Count > 0)
                {
                    var r = idle.OrderBy(q => Dist(q, p.x, -1)).First(); idle.Remove(r);
                    p.outClaimed = true; r.task = new ReturnTask(this, p);
                }
            }
            while (pending.Count > 0 && idle.Count > 0)
            {
                var o = pending[0]; var L = binLoc[o.bin];
                Port port = null; double best = double.PositiveInfinity;
                foreach (var p in ports)
                {
                    int load = p.queue.Count + p.reserved;
                    if (load >= c.portCap) continue;
                    double sc = load * 100 + Math.Abs(p.x - L.x);
                    if (sc < best) { best = sc; port = p; }
                }
                if (port == null) break;
                var r = idle.OrderBy(q => Dist(q, L.x, L.y)).First(); idle.Remove(r);
                pending.RemoveAt(0); port.reserved++;
                active[o.bin] = o;
                r.task = new FetchTask(this, o, port);
            }
        }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600.0 / c.orderRate); }
            Dispatch();
            foreach (var r in robots) StepRobot(r, dt);
            StepPorts(dt);
        }

        // 빈의 월드 좌표 (x, 높이, y) — Unity: (x, y, z)
        public (double x, double h, double y) BinPose(int id)
        {
            var L = binLoc[id];
            if (L.kind == BinKind.Stack) { int k = Stack(L.x, L.y).IndexOf(id); return (WorldX(L.x), k * c.binH + 0.005, WorldY(L.y)); }
            if (L.kind == BinKind.Robot) { var r = robots[L.r]; return (r.px, restZ - r.g, r.py); }
            var p = ports[L.p]; int i = p.queue.IndexOf(id);
            return (WorldX(p.x), i == 0 ? c.portZ : c.portZ - 0.45, WorldY(-1));
        }

        public int Backlog => pending.Count;
        public double ThroughputPerHour => t > 0 ? statDone / t * 3600 : 0;
        public double AvgLead => statDone > 0 ? leadSum / statDone : 0;
        public double RobotUtil => t > 0 ? robots.Sum(r => r.busyTime) / (robots.Count * t) : 0;
        public double PortUtil => t > 0 ? ports.Sum(p => p.busyTime) / (ports.Count * t) : 0;
        public double DigsPerFetch => statFetches > 0 ? (double)statDigs / statFetches : 0;
    }
}
