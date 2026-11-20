// Rapyuta ASRS 방식 다층 셀 스토리지 이산시간 시뮬레이션
// (web/rapyuta-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 구조: 층(f) × 셀(x, y). 층마다 셀당 빈 1개, 높이 80 mm 로봇이 빈 아래를 지나다님.
// 빈을 든 로봇은 빈 없는 셀로만 다니므로 안쪽 빈은 앞 빈을 먼저 옮긴 뒤 꺼냄. 층 사이는 리프트.
// 좌표: 셀 중심 wx = (x+0.5)*cellX, wy = (y+0.5)*cellY, 층 바닥 z = baseZ + f*floorH (Unity 에서는 (x, z, y))
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rapyuta
{
    [Serializable]
    public class RapyutaConfig
    {
        public int nx = 16, ny = 13, nf = 7;             // 셀 수 (x, y), 층 수
        public double cellX = 0.70, cellY = 0.50;        // 셀 피치 (빈 650 x 440 mm 기준 가정)
        public double floorH = 0.63, baseZ = 0.12;       // 층 높이 = 기둥 630 mm (공식), 바닥 블록 (가정)
        public double binH = 0.40, binFoot = 0.10;       // 빈 높이 400 mm (공식), 로봇이 지나갈 다리 높이 (가정)
        public double robotH = 0.08, stroke = 0.035;     // 로봇 높이 80 mm (공식), 들어 올리는 높이 (가정)
        public double fill = 0.78;
        public int reserveFree = 12;                     // 층마다 남겨 둘 빈 셀
        public int robots = 20;
        public int aisleStep = 6;                        // y % aisleStep == 0 인 행은 통로
        public int bufferRows = 2;                       // 피킹층 앞쪽 빈 없는 완충 행
        public int[] liftX = { -1, -1, -1, 16, 16 };     // 리프트 셀 (그리드 좌우 바깥)
        public int[] liftY = { 3, 6, 9, 4, 8 };
        public int stationFloor = 1;                     // 피킹층
        public int[] stationX0 = { 3, 9 };               // 스테이션 슬롯 시작 x (y = -1 행)
        public int[] stationN = { 4, 4 };                // 슬롯 수
        public double orderRate = 600;                   // 빈 요청 / 시간
        public double popularShare = 0.2, popularDemand = 0.8;
        public double vmax = 2.5, acc = 1.2;             // 로봇 2.5 m/s (공식), 가속도 (가정)
        public double turn = 0.25;                       // 메카넘 휠 방향 전환 손실
        public double liftV = 1.6, liftAcc = 1.6, liftSettle = 0.4;
        public double grip = 0.9;                        // 빈 들기/내리기
        public double pickMin = 5, pickMax = 10;         // 피킹 시간
        public int seed = 3;
    }

    public enum BinKind { Cell, Robot }
    public struct BinLoc { public BinKind kind; public int f, x, y, r; }

    public class Act
    {
        public string k;                 // move | lift | lower | wait | ride | dig
        public int x, y, lift, to, bin;
        public bool drop, spot, done, swapped, moved, started;
        public double t, wt;             // wait: 남은 시간 / grip: 경과 시간
        public int st = -1, waitK = -1;
    }

    public class Seg { public int d, x0, y0, released; public List<(int x, int y, int d)> cells; public double dist, step, t, T; }

    public class Lift
    {
        public int id, x, y, ax, ay, f, to, owner = -1, trips;
        public double z, from, t, T, grantT; public bool moving;
        public List<int> queue = new List<int>();
    }

    public class Slot { public int id, st, f, x, y = -1, bin = -1; public bool reserved, ready, claimed; public double tArrive; }
    public class Station { public int id, cur = -1, picked; public List<Slot> slots = new List<Slot>(); public double busyTime, pickLeft = -1; }
    public class Order { public int id, bin; public double tArrive, tPresent = -1, tDone = -1; }

    public abstract class RTask
    {
        public abstract string Kind { get; }
        public abstract void Next(Robot r);
        public abstract void Relief(Robot r);
        public virtual bool CanRehome => false;
        public virtual void Rehome(Robot r) { }
    }

    public class Robot
    {
        public int id, f, x, y, carry = -1, lastDir = -1, onLift = -1, digs, deliveries, returns, rides;
        public double px, py, pz, lift, blocked, waitT, busyTime, blockedTime, dist;
        public RTask task; public List<Act> acts = new List<Act>(); public Seg seg;
        public string state = "idle"; public bool parked;
    }

    public class RapyutaSim
    {
        public const int VOID = 0, STORE = 1, AISLE = 2, LIFT = 3, SLOT = 4;
        static readonly int[,] DIRS = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };

        public readonly RapyutaConfig c;
        public double t;
        public readonly int W, H, FN;
        public readonly byte[] type;
        public readonly int[] binAt, occ, prot;
        public readonly byte[] incoming;
        public readonly List<Lift> lifts = new List<Lift>();
        public readonly List<Slot> slots = new List<Slot>();
        public readonly List<Station> stations = new List<Station>();
        public BinLoc[] binLoc;
        public int bins, nPopular;
        public readonly HashSet<int> locked = new HashSet<int>(), requested = new HashSet<int>();
        public readonly List<Robot> robots = new List<Robot>();
        public readonly List<Order> orders = new List<Order>();
        readonly List<Order> pending = new List<Order>();
        readonly Dictionary<int, Order> active = new Dictionary<int, Order>();
        double nextOrderT;
        public int statDone, statDigs, statFetches, statReliefs; public double leadSum, leadMax;
        int rngState;

        public RapyutaSim(RapyutaConfig cfg)
        {
            c = cfg; rngState = c.seed;
            W = c.nx + 2; H = c.ny + 1; FN = W * H;
            int N = FN * c.nf;
            type = new byte[N]; binAt = Fill(N, -1); occ = Fill(N, -1); incoming = new byte[N]; prot = new int[N];
            for (int f = 0; f < c.nf; f++)
                for (int y = 0; y < c.ny; y++)
                    for (int x = 0; x < c.nx; x++)
                        type[Ci(f, x, y)] = (byte)((y % c.aisleStep == 0 || x <= 1 || x >= c.nx - 2 ||
                            (f == c.stationFloor && y <= c.bufferRows)) ? AISLE : STORE);
            for (int i = 0; i < c.liftX.Length; i++)
            {
                int x = c.liftX[i], y = c.liftY[i];
                for (int f = 0; f < c.nf; f++) type[Ci(f, x, y)] = LIFT;
                lifts.Add(new Lift { id = i, x = x, y = y, ax = x < 0 ? 0 : c.nx - 1, ay = y, z = DeckZ(0) });
            }
            for (int si = 0; si < c.stationX0.Length; si++)
            {
                var st = new Station { id = si };
                for (int k = 0; k < c.stationN[si]; k++)
                {
                    var s = new Slot { id = slots.Count, st = si, f = c.stationFloor, x = c.stationX0[si] + k };
                    type[Ci(s.f, s.x, s.y)] = SLOT;
                    slots.Add(s); st.slots.Add(s);
                }
                stations.Add(st);
            }
            // 빈 초기 배치 (인기 빈 = 작은 ID 가 낮은 층·통로 가까이)
            var cells = new List<(int f, int x, int y, double key)>();
            for (int f = 0; f < c.nf; f++)
                for (int y = 0; y < c.ny; y++)
                    for (int x = 0; x < c.nx; x++)
                    {
                        if (type[Ci(f, x, y)] != STORE) continue;
                        if (Rand() < c.fill) { double key = Depth(f, x, y) * 2 + f * 0.8; key += Gauss() * 1.5; cells.Add((f, x, y, key)); }
                    }
            cells = cells.OrderBy(q => q.key).ToList();   // 안정 정렬 (JS sort 와 같음)
            binLoc = new BinLoc[cells.Count];
            for (int id = 0; id < cells.Count; id++)
            {
                var s = cells[id];
                binAt[Ci(s.f, s.x, s.y)] = id; binLoc[id] = new BinLoc { kind = BinKind.Cell, f = s.f, x = s.x, y = s.y };
            }
            bins = cells.Count; nPopular = Math.Max(1, (int)Math.Floor(bins * c.popularShare));
            for (int i = 0; i < c.robots; i++)
            {
                int f = i % c.nf, x, y, k;
                do { x = 2 + (int)Math.Floor(Rand() * (c.nx - 4)); y = (int)Math.Floor(Rand() * c.ny); k = Ci(f, x, y); }
                while (occ[k] != -1 || type[k] != STORE);
                var r = new Robot { id = i, f = f, x = x, y = y };
                SetPos(r); occ[k] = i; robots.Add(r);
            }
            nextOrderT = Expo(3600 / c.orderRate);
        }

        static int[] Fill(int n, int v) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        // mulberry32 (JS 와 같은 난수열)
        public double Rand()
        {
            unchecked
            {
                rngState = rngState + 0x6D2B79F5;
                int a = rngState;
                int q = (a ^ (int)((uint)a >> 15)) * (1 | a);
                q = (q + (q ^ (int)((uint)q >> 7)) * (61 | q)) ^ q;
                return (uint)(q ^ (int)((uint)q >> 14)) / 4294967296.0;
            }
        }
        double Gauss() { double u = 0; while (u == 0) u = Rand(); double v = Rand(); return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); }
        double Expo(double mean) => -Math.Log(1 - Rand()) * mean;

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

        // ---------------- 기본
        public int Ci(int f, int x, int y) => f * FN + (x + 1) + (y + 1) * W;
        public bool Inb(int x, int y) => x >= -1 && x <= c.nx && y >= -1 && y < c.ny;
        public int TypeAt(int f, int x, int y) => Inb(x, y) ? type[Ci(f, x, y)] : VOID;
        public double WorldX(int x) => (x + 0.5) * c.cellX;
        public double WorldY(int y) => (y + 0.5) * c.cellY;
        public double DeckZ(int f) => c.baseZ + f * c.floorH;
        void SetPos(Robot r) { r.px = WorldX(r.x); r.py = WorldY(r.y); r.pz = DeckZ(r.f); }
        public int Depth(int f, int x, int y) { int s = c.aisleStep, m = y % s; return Math.Min(m, s - m) - 1; }
        int CX(int cell) => cell % W - 1;
        int CY(int cell) => cell / W - 1;

        // ---------------- 일방통행 (빈을 든 로봇)
        int RowDir(int y) => ((y / c.aisleStep) % 2 == 0) ? 0 : 1;
        int ColDir(int x) => (x == 0 || x == c.nx - 2) ? 3 : 2;
        bool IsSpine(int x) => x == 0 || x == 1 || x == c.nx - 2 || x == c.nx - 1;
        bool IsRow(int x, int y) => y >= 0 && y % c.aisleStep == 0 && !IsSpine(x) && x >= 0 && x < c.nx;
        bool OneWayOk(int fx, int fy, int x, int y, int d)
        {
            if ((d >> 1) == 0)
            {
                if ((IsRow(fx, fy) || IsRow(x, y)) && fy == y && y % c.aisleStep == 0) return d == RowDir(y);
                return true;
            }
            if (IsSpine(x) && fx == x && y >= 0 && fy >= 0) return d == ColDir(x);
            return true;
        }

        bool Passable(int f, int x, int y, bool loaded, int target, int fx, int fy, int d)
        {
            if (!Inb(x, y)) return false;
            int k = Ci(f, x, y), ty = type[k];
            if (ty == VOID) return false;
            if (k == target) return true;
            if (ty == LIFT || ty == SLOT) return false;
            if (loaded && binAt[k] != -1) return false;
            if (loaded && d >= 0 && !OneWayOk(fx, fy, x, y, d)) return false;
            return true;
        }

        List<(int x, int y, int d)> Plan(Robot r, int tx, int ty, bool avoid)
        {
            int f = r.f, bas = f * FN, N = FN * 4;
            bool loaded = r.carry >= 0;
            var cost = new double[N]; for (int i = 0; i < N; i++) cost[i] = double.PositiveInfinity;
            var prev = Fill(N, -1); var done = new bool[N];
            int sc = Ci(f, r.x, r.y) - bas, target = Ci(f, tx, ty), tl = target - bas;
            double sx = c.cellX / c.vmax + 0.04, sy = c.cellY / c.vmax + 0.04;
            double[] stepT = { sx, sx, sy, sy };
            for (int d = 0; d < 4; d++) cost[sc * 4 + d] = (r.lastDir == -1 || r.lastDir == d) ? 0 : c.turn;
            int best = -1;
            while (true)
            {
                int u = -1; double bu = double.PositiveInfinity;
                for (int i = 0; i < N; i++) if (!done[i] && cost[i] < bu) { bu = cost[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                int cell = u >> 2, d0 = u & 3;
                if (cell == tl) { best = u; break; }
                int x = CX(cell), y = CY(cell);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + DIRS[d, 0], ny = y + DIRS[d, 1];
                    if (!Passable(f, nx, ny, loaded, target, x, y, d)) continue;
                    int nk = Ci(f, nx, ny);
                    if (avoid && occ[nk] != -1 && occ[nk] != r.id && nk != target) continue;
                    int v = (nk - bas) * 4 + d; double nc = bu + stepT[d] + (d0 == d ? 0 : c.turn);
                    if (nc < cost[v]) { cost[v] = nc; prev[v] = u; }
                }
            }
            if (best < 0) return null;
            var path = new List<(int x, int y, int d)>();
            int w = best;
            while (w >= 0 && (w >> 2) != sc) { int cell = w >> 2; path.Add((CX(cell), CY(cell), w & 3)); w = prev[w]; }
            path.Reverse();
            return path;
        }

        // 빈 없는 셀만으로 닿는 셀까지의 단계 수 (BFS). rid >= 0 이면 다른 로봇이 선 셀도 막힘
        int[] Reach(int f, int x0, int y0, int rid = -2)
        {
            var dist = Fill(FN, -1); var q = new List<int> { Ci(f, x0, y0) - f * FN };
            dist[q[0]] = 0;
            for (int h = 0; h < q.Count; h++)
            {
                int cell = q[h], x = CX(cell), y = CY(cell);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + DIRS[d, 0], ny = y + DIRS[d, 1];
                    if (!Inb(nx, ny)) continue;
                    int k = Ci(f, nx, ny), ty = type[k];
                    if (ty != STORE && ty != AISLE) continue;
                    if (binAt[k] != -1) continue;
                    if (rid != -2 && occ[k] != -1 && occ[k] != rid) continue;
                    int kl = k - f * FN;
                    if (dist[kl] >= 0) continue;
                    dist[kl] = dist[cell] + 1; q.Add(kl);
                }
            }
            return dist;
        }

        // 목표 빈 → 통로 꺼내는 경로 (빈 있는 셀 비용 1). 통로 쪽 → 목표 쪽 순서
        List<int> ClearPath(int f, int x0, int y0)
        {
            int bas = f * FN;
            var dist = Fill(FN, 1 << 20); var prev = Fill(FN, -1); var done = new bool[FN];
            int s = Ci(f, x0, y0) - bas; dist[s] = 0;
            var dq = new List<int> { s }; int head = 0, goal = -1;
            while (head < dq.Count)
            {
                int bi = -1, bd = 1 << 21;
                for (int i = head; i < dq.Count; i++) if (!done[dq[i]] && dist[dq[i]] < bd) { bd = dist[dq[i]]; bi = i; }
                if (bi < 0) break;
                int cell = dq[bi]; dq[bi] = dq[head]; dq[head] = cell; head++;
                if (done[cell]) continue; done[cell] = true;
                int x = CX(cell), y = CY(cell), k = cell + bas;
                if (type[k] == AISLE) { goal = cell; break; }
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + DIRS[d, 0], ny = y + DIRS[d, 1];
                    if (!Inb(nx, ny)) continue;
                    int nk = Ci(f, nx, ny), ty = type[nk];
                    if (ty != STORE && ty != AISLE) continue;
                    int b = binAt[nk];
                    if (b != -1 && (locked.Contains(b) || requested.Contains(b))) continue;
                    int wgt = b != -1 ? 1 : 0, nl = nk - bas;
                    if (dist[cell] + wgt < dist[nl]) { dist[nl] = dist[cell] + wgt; prev[nl] = cell; dq.Add(nl); }
                }
            }
            if (goal < 0) return null;
            var cells = new List<int>(); int u = goal;
            while (u >= 0) { cells.Add(u + bas); u = prev[u]; }
            return cells;
        }

        // ---------------- 행동 실행
        void StepRobot(Robot r, double dt)
        {
            if (r.task != null || r.acts.Count > 0 || r.seg != null) r.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (r.acts.Count == 0)
                {
                    r.task?.Next(r);
                    if (r.acts.Count == 0) { if (r.task == null) r.state = "idle"; return; }
                }
                var a = r.acts[0];
                double used;
                switch (a.k)
                {
                    case "move": used = ActMove(r, a, dt); break;
                    case "lift": used = GripAct(r, a, dt, true); break;
                    case "lower": used = GripAct(r, a, dt, false); break;
                    case "ride": used = ActRide(r, a, dt); break;
                    case "dig": used = ActDig(r, a, dt); break;
                    default: used = ActWait(a, dt); break;
                }
                if (a.done) { int i = r.acts.IndexOf(a); if (i >= 0) r.acts.RemoveAt(i); }
                dt -= used;
                if (used == 0 && !a.done && r.acts.Count > 0 && r.acts[0] == a) return;
            }
        }

        double ActWait(Act a, double dt) { a.t -= dt; if (a.t <= 0) { a.done = true; return dt + a.t; } return dt; }

        bool Retarget(Robot r, Act a, int notK)
        {
            var nd = FindDrop(r, false, notK);
            if (nd.HasValue) { incoming[Ci(r.f, a.x, a.y)] = 0; a.x = nd.Value.x; a.y = nd.Value.y; r.blocked = 0; return true; }
            return false;
        }

        double ActMove(Robot r, Act a, double dt)
        {
            if (r.seg == null)
            {
                if (r.x == a.x && r.y == a.y) { a.done = true; return 0; }
                if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; r.blockedTime += dt; return dt; }
                var path = Plan(r, a.x, a.y, r.blocked > 1.2);
                if (path == null || path.Count == 0) path = Plan(r, a.x, a.y, false);
                if (path == null || path.Count == 0)
                {
                    r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.5;
                    if (Relieve(r, a)) return dt;
                    if (a.spot && r.blocked > 1) { incoming[Ci(r.f, a.x, a.y)] = 0; a.done = true; r.blocked = 0; r.waitT = 0; return dt; }
                    if (a.drop && r.carry >= 0 && r.blocked > 1)
                    {
                        if (!Retarget(r, a, Ci(r.f, a.x, a.y)) && r.task != null && r.task.CanRehome)
                        {
                            incoming[Ci(r.f, a.x, a.y)] = 0; ClearActs(r); r.blocked = 0; r.waitT = 0;
                            r.task.Rehome(r); a.done = true; return dt;
                        }
                    }
                    return dt;
                }
                int d = path[0].d;
                var cells = new List<(int x, int y, int d)>();
                foreach (var p in path) { if (p.d != d) break; int o = occ[Ci(r.f, p.x, p.y)]; if (o != -1 && o != r.id) break; cells.Add(p); }
                if (cells.Count == 0)
                {
                    r.blocked += dt; r.blockedTime += dt; r.state = "blocked";
                    if (a.spot && r.blocked > 4) { incoming[Ci(r.f, a.x, a.y)] = 0; a.done = true; r.blocked = 0; return dt; }
                    if (a.drop && r.carry >= 0 && r.blocked > 2.5)
                    {
                        int old = Ci(r.f, a.x, a.y);
                        var nd = FindDrop(r, false, old);
                        if (nd.HasValue) { incoming[old] = 0; a.x = nd.Value.x; a.y = nd.Value.y; r.blocked = 0; r.waitT = 0.2; return dt; }
                        if (r.task != null && r.task.CanRehome)
                        {
                            incoming[old] = 0; ClearActs(r); r.blocked = 0; r.waitT = 0;
                            r.task.Rehome(r); a.done = true; return dt;
                        }
                    }
                    int ob = occ[Ci(r.f, path[0].x, path[0].y)];
                    if (ob >= 0) Nudge(robots[ob], path);
                    if (Relieve(r, a)) return dt;
                    if (r.blocked > 5) { Sidestep(r); r.blocked = 2; }
                    r.waitT = 0.2;
                    return dt;
                }
                r.blocked = 0;
                foreach (var p in cells) occ[Ci(r.f, p.x, p.y)] = r.id;
                double step = (d >> 1) == 0 ? c.cellX : c.cellY;
                double dist = cells.Count * step;
                double turnT = (r.lastDir != -1 && r.lastDir != d) ? c.turn : 0;
                r.seg = new Seg { d = d, cells = cells, x0 = r.x, y0 = r.y, dist = dist, step = step, t = -turnT, T = ProfileTime(dist, c.vmax, c.acc) };
                r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move"; r.parked = false;
            }
            var s = r.seg;
            s.t += dt;
            double used = dt;
            if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
            double pos = s.t <= 0 ? 0 : ProfilePos(s.t, s.dist, c.vmax, c.acc);
            r.px = WorldX(s.x0) + DIRS[s.d, 0] * pos;
            r.py = WorldY(s.y0) + DIRS[s.d, 1] * pos;
            int passed = (int)Math.Floor((pos + s.step * 0.5) / s.step);
            while (s.released < Math.Min(passed, s.cells.Count))
            {
                int px = s.released == 0 ? s.x0 : s.cells[s.released - 1].x, py = s.released == 0 ? s.y0 : s.cells[s.released - 1].y;
                occ[Ci(r.f, px, py)] = -1; s.released++;
                r.x = s.cells[s.released - 1].x; r.y = s.cells[s.released - 1].y;
            }
            if (s.t >= s.T)
            {
                while (s.released < s.cells.Count)
                {
                    int px = s.released == 0 ? s.x0 : s.cells[s.released - 1].x, py = s.released == 0 ? s.y0 : s.cells[s.released - 1].y;
                    occ[Ci(r.f, px, py)] = -1; s.released++;
                }
                var last = s.cells[s.cells.Count - 1];
                r.x = last.x; r.y = last.y; occ[Ci(r.f, r.x, r.y)] = r.id; SetPos(r);
                r.dist += s.dist; r.seg = null;
                if (r.x == a.x && r.y == a.y) a.done = true;
            }
            return used;
        }

        // 행동 목록을 비움. 기다리던 리프트 차례/사용권도 반납
        void ClearActs(Robot r)
        {
            foreach (var a in r.acts)
                if (a.k == "ride")
                {
                    var L = lifts[a.lift]; L.queue.Remove(r.id);
                    if (L.owner == r.id) L.owner = -1;
                }
            r.acts.Clear();
        }

        // 교착 해소: 빈을 든 로봇이 보관 셀 안에서 오래 막히면 빈을 그 자리에 내려놓고 다시 계획
        bool Relieve(Robot r, Act a)
        {
            if (r.carry < 0 || r.blocked < 8 || r.task == null) return false;
            int k = Ci(r.f, r.x, r.y);
            if (type[k] != STORE || binAt[k] != -1) return false;
            if (a.drop) incoming[Ci(r.f, a.x, a.y)] = 0;
            ClearActs(r); r.blocked = 0; r.waitT = 0;
            r.acts.Add(new Act { k = "lower" });
            r.task.Relief(r);
            statReliefs++;
            return true;
        }

        void Nudge(Robot o, List<(int x, int y, int d)> path)
        {
            if (o.task != null || o.acts.Count > 0 || o.seg != null) return;
            var onPath = new HashSet<int>(path.Select(p => Ci(o.f, p.x, p.y)));
            var opts = new List<(int x, int y)>(); var any = new List<(int x, int y)>();
            for (int d = 0; d < 4; d++)
            {
                int nx = o.x + DIRS[d, 0], ny = o.y + DIRS[d, 1];
                if (!Passable(o.f, nx, ny, o.carry >= 0, -1, o.x, o.y, d)) continue;
                int k = Ci(o.f, nx, ny);
                if (occ[k] != -1) continue;
                any.Add((nx, ny));
                if (!onPath.Contains(k)) opts.Add((nx, ny));
            }
            var pick = opts.Count > 0 ? opts : any;
            if (pick.Count > 0) { var p = pick[(int)Math.Floor(Rand() * pick.Count)]; o.acts.Add(new Act { k = "move", x = p.x, y = p.y }); }
        }

        void Sidestep(Robot r)
        {
            var opts = new List<(int x, int y)>();
            for (int d = 0; d < 4; d++)
            {
                int nx = r.x + DIRS[d, 0], ny = r.y + DIRS[d, 1];
                if (Passable(r.f, nx, ny, r.carry >= 0, -1, r.x, r.y, d) && occ[Ci(r.f, nx, ny)] == -1) opts.Add((nx, ny));
            }
            if (opts.Count > 0) { var p = opts[(int)Math.Floor(Rand() * opts.Count)]; r.acts.Insert(0, new Act { k = "move", x = p.x, y = p.y }); }
        }

        double GripAct(Robot r, Act a, double dt, bool isLift)
        {
            if (!a.started) { a.started = true; a.t = 0; a.swapped = false; r.state = isLift ? "lift" : "lower"; }
            a.t += dt;
            double used = dt;
            if (a.t >= c.grip) { used = dt - (a.t - c.grip); a.t = c.grip; }
            double u = a.t / c.grip;
            r.lift = c.stroke * (isLift ? Math.Min(1, u * 1.6) : Math.Max(0, 1 - u * 1.6));
            int k = Ci(r.f, r.x, r.y);
            if (!a.swapped && u >= 0.5)
            {
                a.swapped = true;
                var s = SlotAt(r.f, r.x, r.y);
                if (isLift)
                {
                    int id = binAt[k]; binAt[k] = -1; r.carry = id; binLoc[id] = new BinLoc { kind = BinKind.Robot, r = r.id };
                    if (s != null) { s.bin = -1; s.ready = false; s.claimed = false; }
                }
                else
                {
                    int id = r.carry; r.carry = -1; binAt[k] = id; incoming[k] = 0;
                    binLoc[id] = new BinLoc { kind = BinKind.Cell, f = r.f, x = r.x, y = r.y };
                    locked.Remove(id);
                    if (s != null) { s.bin = id; s.tArrive = t; s.reserved = false; }
                }
            }
            if (a.t >= c.grip) { a.done = true; r.lift = isLift ? c.stroke : 0; }
            return used;
        }

        public Slot SlotAt(int f, int x, int y)
        {
            if (y != -1 || f != c.stationFloor) return null;
            foreach (var s in slots) if (s.x == x) return s;
            return null;
        }

        // ---------------- 리프트
        double ActRide(Robot r, Act a, double dt)
        {
            var L = lifts[a.lift];
            if (a.st < 0) { a.st = 0; if (!L.queue.Contains(r.id) && L.owner != r.id) L.queue.Add(r.id); }
            if (a.st == 0)
            {
                if (L.owner != r.id)
                {
                    // 차례를 기다리는 동안 통로를 막지 않음 (빈 로봇은 빈 아래로, 빈을 든 로봇은 빈 셀로)
                    if (type[Ci(r.f, r.x, r.y)] == AISLE && (!a.moved || (a.wt += dt) > 3))
                    {
                        a.moved = true; a.wt = 0;
                        var p = r.carry < 0 ? NearStore(r) : FindDrop(r, false, -2);
                        if (p.HasValue)
                        {
                            if (r.carry >= 0) a.waitK = Ci(r.f, p.Value.x, p.Value.y);
                            r.acts.Insert(0, new Act { k = "move", x = p.Value.x, y = p.Value.y, spot = true }); return 0;
                        }
                    }
                    r.state = "liftwait"; r.blockedTime += dt; return dt;
                }
                if (a.waitK >= 0) { incoming[a.waitK] = 0; a.waitK = -1; }
                a.st = 1;
            }
            if (a.st == 1) { a.st = 2; r.acts.Insert(0, new Act { k = "move", x = L.ax, y = L.ay }); return 0; }
            if (a.st == 2)
            {
                if (L.moving || L.f != r.f) { if (!L.moving) LiftGo(L, r.f); r.state = "liftwait"; return dt; }
                a.st = 3; r.acts.Insert(0, new Act { k = "move", x = L.x, y = L.y }); return 0;
            }
            if (a.st == 3)
            {
                r.onLift = L.id; r.state = "ride"; r.rides++;
                occ[Ci(r.f, L.x, L.y)] = -1;
                LiftGo(L, a.to); a.st = 4;
            }
            if (a.st == 4)
            {
                r.pz = L.z; r.px = WorldX(L.x); r.py = WorldY(L.y);
                if (L.moving) return dt;
                r.f = a.to; r.onLift = -1; occ[Ci(r.f, L.x, L.y)] = r.id; SetPos(r);
                a.st = 5; r.acts.Insert(0, new Act { k = "move", x = L.ax, y = L.ay }); return 0;
            }
            if (a.st == 5) { L.owner = -1; a.done = true; return 0; }
            return dt;
        }

        void LiftGo(Lift L, int f)
        {
            if (L.f == f && !L.moving) return;
            L.from = L.z; L.to = f; L.t = 0;
            L.T = ProfileTime(Math.Abs(DeckZ(f) - L.z), c.liftV, c.liftAcc) + c.liftSettle;
            L.moving = true; L.trips++;
        }

        void StepLifts(double dt)
        {
            foreach (var L in lifts)
            {
                if (L.owner < 0 && L.queue.Count > 0) { L.owner = L.queue[0]; L.queue.RemoveAt(0); L.grantT = 0; }
                // 사용권을 받은 로봇이 대기열 로봇들에 막혀 못 오면 차례를 뒤로 미룸
                if (L.owner >= 0 && !L.moving)
                {
                    L.grantT += dt;
                    var o = robots[L.owner]; var a = o.acts.Find(q => q.k == "ride" && q.lift == L.id);
                    if (L.grantT > 12 && a != null && (a.st == 2 || a.st == 1) && o.seg == null && L.queue.Count > 0)
                    {
                        var m = o.acts[0];
                        if (m.k == "move" && m.x == L.ax && m.y == L.ay) o.acts.RemoveAt(0);
                        a.st = 0; a.moved = false; L.queue.Add(o.id); L.owner = L.queue[0]; L.queue.RemoveAt(0); L.grantT = 0;
                    }
                }
                if (!L.moving) continue;
                L.t += dt;
                double D = DeckZ(L.to) - L.from, T = L.T - c.liftSettle;
                L.z = L.from + Math.Sign(D) * ProfilePos(Math.Min(L.t, T), Math.Abs(D), c.liftV, c.liftAcc);
                if (L.t >= L.T) { L.moving = false; L.f = L.to; L.z = DeckZ(L.f); }
            }
        }

        int PickLift(Robot r)
        {
            Lift best = null; double bs = double.PositiveInfinity;
            foreach (var L in lifts)
            {
                double s = Math.Abs(r.x - L.ax) * c.cellX + Math.Abs(r.y - L.ay) * c.cellY + (L.queue.Count + (L.owner >= 0 ? 1 : 0)) * 6;
                if (s < bs) { bs = s; best = L; }
            }
            return best.id;
        }
        void Ride(Robot r, int f) => r.acts.Add(new Act { k = "ride", lift = PickLift(r), to = f });

        // ---------------- 빈 놓을 자리
        // notK: 제외할 셀 (-1 = 없음). notK != -1 이면 다른 로봇이 선 셀도 막힘으로 봄
        (int f, int x, int y)? FindDrop(Robot r, bool avoidDepth0, int notK = -1)
        {
            int f = r.f; var dist = Reach(f, r.x, r.y, notK != -1 ? r.id : -2);
            (int f, int x, int y)? best = null; double bd = double.PositiveInfinity;
            for (int y = 0; y < c.ny; y++)
                for (int x = 0; x < c.nx; x++)
                {
                    int k = Ci(f, x, y);
                    if (type[k] != STORE || binAt[k] != -1 || incoming[k] != 0 || prot[k] != 0 || k == notK) continue;
                    int dd = dist[k - f * FN]; if (dd < 0) continue;
                    int o = occ[k];
                    double s = dd + (o != -1 && o != r.id ? 3 : 0) + (avoidDepth0 && Depth(f, x, y) == 0 ? 2 : 0);
                    if (s < bd) { bd = s; best = (f, x, y); }
                }
            if (best.HasValue) incoming[Ci(best.Value.f, best.Value.x, best.Value.y)] = 1;
            return best;
        }

        // 피킹 끝난 빈을 돌려놓을 자리. 인기 빈은 낮은 층·통로 옆, 나머지는 높은 층·안쪽
        (int f, int x, int y)? FindHome(Robot r, int id, int skipF)
        {
            bool pop = id < nPopular;
            (int f, int x, int y)? best = null; double bs = double.PositiveInfinity;
            for (int f = 0; f < c.nf; f++)
            {
                if (f == skipF) continue;
                if (EmptyCells(f) <= c.reserveFree * (f == c.stationFloor ? 2 : 1)) continue;
                int sx = r.x, sy = r.y;
                if (f != r.f) { var L = lifts[PickLift(r)]; sx = L.ax; sy = L.ay; }
                var dist = Reach(f, sx, sy);
                double lift = f == r.f ? 0 : 7 + Math.Abs(DeckZ(f) - DeckZ(r.f)) / c.liftV;
                for (int y = 0; y < c.ny; y++)
                    for (int x = 0; x < c.nx; x++)
                    {
                        int k = Ci(f, x, y);
                        if (type[k] != STORE || binAt[k] != -1 || incoming[k] != 0 || prot[k] != 0) continue;
                        int dd = dist[k - f * FN]; if (dd < 0) continue;
                        int dep = Depth(f, x, y);
                        double s = lift + dd * 0.4 + (pop ? dep * 3 + f * 1.5 : (1 - dep) * 2 + (c.nf - 1 - f) * 0.8);
                        if (s < bs) { bs = s; best = (f, x, y); }
                    }
            }
            if (best.HasValue) incoming[Ci(best.Value.f, best.Value.x, best.Value.y)] = 1;
            return best;
        }

        (int f, int x, int y)? NearStore(Robot r)
        {
            (int f, int x, int y)? best = null; double bd = double.PositiveInfinity;
            for (int y = 0; y < c.ny; y++)
                for (int x = 2; x < c.nx - 2; x++)
                {
                    int k = Ci(r.f, x, y);
                    if (type[k] != STORE || occ[k] != -1) continue;
                    double d = Math.Abs(x - r.x) + Math.Abs(y - r.y);
                    if (d < bd) { bd = d; best = (r.f, x, y); }
                }
            return best;
        }

        public int EmptyCells(int f)
        {
            int n = 0;
            for (int y = 0; y < c.ny; y++)
                for (int x = 0; x < c.nx; x++)
                {
                    int k = Ci(f, x, y);
                    if (type[k] == STORE && binAt[k] == -1 && incoming[k] == 0) n++;
                }
            return n;
        }

        void Park(Robot r)
        {
            (int x, int y)? best = null; double bd = double.PositiveInfinity;
            for (int y = 0; y < c.ny; y++)
                for (int x = 2; x < c.nx - 2; x++)
                {
                    int k = Ci(r.f, x, y);
                    if (type[k] != STORE || occ[k] != -1 || binAt[k] == -1) continue;
                    double d = Math.Abs(x - r.x) + Math.Abs(y - r.y) + Rand() * 0.5;
                    if (d < bd) { bd = d; best = (x, y); }
                }
            if (best.HasValue) { r.acts.Add(new Act { k = "move", x = best.Value.x, y = best.Value.y }); r.parked = true; }
        }

        // ---------------- 작업
        class FetchTask : RTask
        {
            readonly RapyutaSim sim; readonly Order order; readonly Slot slot;
            string phase = "goto"; List<int> path; bool go;
            public FetchTask(RapyutaSim s, Order o, Slot sl) { sim = s; order = o; slot = sl; }
            public override string Kind => "fetch";
            void Unprotect() { if (path != null) foreach (int k in path) sim.prot[k]--; path = null; }
            public override void Relief(Robot r) { Unprotect(); phase = "goto"; }
            public override void Next(Robot r)
            {
                var c = sim.c;
                if (phase == "goto")
                {
                    var L = sim.binLoc[order.bin];
                    if (r.f != L.f) { sim.Ride(r, L.f); return; }
                    Unprotect();
                    var cells = sim.ClearPath(L.f, L.x, L.y);
                    if (cells == null) { r.acts.Add(new Act { k = "wait", t = 1 }); return; }
                    path = cells; foreach (int k in path) sim.prot[k]++;
                    // 통로 쪽에서 가장 바깥의 막는 빈부터 옮김
                    int blk = -1;
                    foreach (int k in cells) { int b = sim.binAt[k]; if (b != -1 && b != order.bin) { blk = k; break; } }
                    if (blk >= 0)
                    {
                        int b = sim.binAt[blk], bx = sim.CX(blk % sim.FN), by = sim.CY(blk % sim.FN);
                        sim.locked.Add(b);
                        r.acts.Add(new Act { k = "move", x = bx, y = by }); r.acts.Add(new Act { k = "lift" }); r.acts.Add(new Act { k = "dig", bin = b });
                        r.digs++; sim.statDigs++;
                        return;
                    }
                    if (r.x != L.x || r.y != L.y) { r.acts.Add(new Act { k = "move", x = L.x, y = L.y }); return; }
                    r.acts.Add(new Act { k = "lift" }); phase = "deliver"; return;
                }
                if (phase == "deliver")
                {
                    if (!go)
                    {
                        if (slot.bin >= 0 || sim.occ[sim.Ci(slot.f, slot.x, slot.y)] != -1) { r.state = "hold"; r.acts.Add(new Act { k = "wait", t = 0.5 }); return; }
                        go = true;
                    }
                    if (r.f != c.stationFloor) { sim.Ride(r, c.stationFloor); return; }
                    Unprotect();
                    r.acts.Add(new Act { k = "move", x = slot.x, y = 0 }); r.acts.Add(new Act { k = "move", x = slot.x, y = slot.y }); r.acts.Add(new Act { k = "lower" });
                    phase = "after"; return;
                }
                if (phase == "after")
                {
                    Unprotect();
                    r.deliveries++; sim.statFetches++;
                    r.task = null;
                    // 이 스테이션에 피킹 끝난 빈이 있으면 바로 회수 (교환)
                    var s = sim.slots.Find(q => q.ready && !q.claimed && q.st == slot.st) ?? sim.slots.Find(q => q.ready && !q.claimed);
                    if (s != null) { s.claimed = true; r.task = new ReturnTask(sim, s); r.task.Next(r); return; }
                    r.acts.Add(new Act { k = "move", x = slot.x, y = 0 });
                    sim.Park(r);
                }
            }
        }

        // 꺼내는 경로를 막은 빈을 들었음 → 가까운 빈 셀에 내려놓음
        double ActDig(Robot r, Act a, double dt)
        {
            var d = FindDrop(r, true);
            if (!d.HasValue)
            {
                r.state = "blocked"; r.blockedTime += dt; a.wt += dt;
                if (a.wt > 3) { a.done = true; r.acts.Insert(1, new Act { k = "lower" }); r.acts.Insert(2, new Act { k = "wait", t = 2 }); return 0; }
                return dt;
            }
            a.done = true;
            r.acts.Insert(1, new Act { k = "move", x = d.Value.x, y = d.Value.y, drop = true });
            r.acts.Insert(2, new Act { k = "lower" });
            return 0;
        }

        class ReturnTask : RTask
        {
            readonly RapyutaSim sim; readonly Slot slot;
            string phase = "goto"; int skipF = -1; (int f, int x, int y)? dest;
            public ReturnTask(RapyutaSim s, Slot sl) { sim = s; slot = sl; }
            public override string Kind => "return";
            public override bool CanRehome => true;
            public override void Rehome(Robot r) { dest = null; skipF = r.f; phase = "home"; }
            public override void Relief(Robot r) { dest = null; phase = "end"; }
            public override void Next(Robot r)
            {
                if (phase == "goto")
                {
                    if (r.f != slot.f) { sim.Ride(r, slot.f); return; }
                    r.acts.Add(new Act { k = "move", x = slot.x, y = 0 }); r.acts.Add(new Act { k = "move", x = slot.x, y = slot.y }); r.acts.Add(new Act { k = "lift" });
                    phase = "home"; return;
                }
                if (phase == "home")
                {
                    if (!dest.HasValue)
                    {
                        dest = sim.FindHome(r, r.carry, skipF);
                        if (!dest.HasValue) { r.acts.Add(new Act { k = "wait", t = 1 }); return; }
                    }
                    if (r.f != dest.Value.f) { sim.Ride(r, dest.Value.f); return; }
                    r.acts.Add(new Act { k = "move", x = dest.Value.x, y = dest.Value.y, drop = true }); r.acts.Add(new Act { k = "lower" });
                    phase = "end"; return;
                }
                if (phase == "end") { r.returns++; r.task = null; sim.Park(r); }
            }
        }

        // ---------------- 주문 / 스테이션 / 배차
        void NewOrder()
        {
            if (pending.Count > 40) return;
            for (int tries = 0; tries < 40; tries++)
            {
                bool pop = Rand() < c.popularDemand;
                int id = pop ? (int)Math.Floor(Rand() * nPopular) : nPopular + (int)Math.Floor(Rand() * (bins - nPopular));
                var B = binLoc[id];
                if (requested.Contains(id) || locked.Contains(id) || B.kind != BinKind.Cell || type[Ci(B.f, B.x, B.y)] != STORE) continue;
                requested.Add(id);
                var o = new Order { id = orders.Count, bin = id, tArrive = t };
                orders.Add(o); pending.Add(o); return;
            }
        }

        void StepStations(double dt)
        {
            foreach (var st in stations)
            {
                if (st.cur < 0)
                {
                    Slot best = null;
                    foreach (var s in st.slots) if (s.bin >= 0 && !s.ready && (best == null || s.tArrive < best.tArrive)) best = s;
                    if (best == null) continue;
                    st.cur = best.id; st.pickLeft = c.pickMin + Rand() * (c.pickMax - c.pickMin);
                    if (active.TryGetValue(best.bin, out var o0)) o0.tPresent = t;
                }
                st.busyTime += dt;
                st.pickLeft -= dt;
                if (st.pickLeft <= 0)
                {
                    var s = slots[st.cur]; st.cur = -1; s.ready = true; s.claimed = false; st.picked++;
                    if (active.TryGetValue(s.bin, out var o))
                    {
                        o.tDone = t; requested.Remove(s.bin); active.Remove(s.bin); statDone++;
                        double lead = o.tPresent - o.tArrive; leadSum += lead; leadMax = Math.Max(leadMax, lead);
                    }
                }
            }
        }

        double Cost(Robot r, int f, int x, int y) => Math.Abs(r.x - x) * c.cellX + Math.Abs(r.y - y) * c.cellY + (r.f == f ? 0 : 6 + Math.Abs(r.f - f) * 0.5);

        void Dispatch()
        {
            var idle = robots.Where(r => r.task == null && r.seg == null && (r.acts.Count == 0 || r.parked) && r.onLift < 0).ToList();
            Action<Robot> Take = r => { idle.Remove(r); if (r.parked) r.acts.RemoveAll(a => a.k == "move"); r.parked = false; };
            foreach (var s in slots)
            {
                if (!s.ready || s.claimed || idle.Count == 0) continue;
                idle = idle.OrderBy(r => Cost(r, s.f, s.x, s.y)).ToList();
                var r0 = idle[0]; Take(r0); s.claimed = true; r0.task = new ReturnTask(this, s);
            }
            while (pending.Count > 0 && idle.Count > 0)
            {
                Slot slot = null; double bs = double.PositiveInfinity;
                foreach (var s in slots)
                {
                    if (s.reserved) continue;
                    double sc = StationLoad(s.st) * 10 + (s.bin < 0 ? 0 : s.ready ? 1 : 2) * 3 + s.id * 0.01;
                    if (sc < bs) { bs = sc; slot = s; }
                }
                if (slot == null) break;
                var o = pending[0]; pending.RemoveAt(0); var L = binLoc[o.bin];
                idle = idle.OrderBy(r => Cost(r, L.f, L.x, L.y)).ToList();
                var r1 = idle[0]; Take(r1);
                slot.reserved = true; active[o.bin] = o;
                r1.task = new FetchTask(this, o, slot);
            }
        }

        int StationLoad(int si) { int n = 0; foreach (var s in stations[si].slots) n += (s.reserved ? 1 : 0) + (s.bin >= 0 && !s.ready ? 1 : 0); return n; }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600 / c.orderRate); }
            Dispatch();
            StepLifts(dt);
            foreach (var r in robots) StepRobot(r, dt);
            StepStations(dt);
        }

        // ---------------- 렌더링/HUD 용
        // 빈 위치 (다리 밑면 기준)
        public (double x, double y, double z) BinPose(int id)
        {
            var L = binLoc[id];
            if (L.kind == BinKind.Robot) { var r = robots[L.r]; return (r.px, r.py, r.pz + r.lift); }
            return (WorldX(L.x), WorldY(L.y), DeckZ(L.f));
        }
        public double ThroughputPerHour => t > 0 ? statDone / t * 3600 : 0;
        public double DigsPerFetch => statFetches > 0 ? (double)statDigs / statFetches : 0;
        public int Backlog => pending.Count;
        public double RobotUtil { get { double s = 0; foreach (var r in robots) s += r.busyTime; return t > 0 ? s / (robots.Count * t) : 0; } }
        public double StationUtil { get { double s = 0; foreach (var st in stations) s += st.busyTime; return t > 0 ? s / (stations.Count * t) : 0; } }
    }
}
