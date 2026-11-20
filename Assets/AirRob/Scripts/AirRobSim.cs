// Libiao AirRob 방식 "랙 면 레일 로봇 + 바닥 이송 로봇" 토트 창고 이산시간 시뮬레이션
// (web/airrob-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 통로마다 AirRob 1대가 랙 면 레일을 따라 수평·수직으로 움직이며 양방향 훅으로 토트를 당기고 밉니다.
// 랙 맨 아래는 터널이라 바닥 로봇이 지나다니고, AirRob 은 꺼낸 토트를 그 칸 아래의 바닥 로봇 위로 밀어 줍니다.
// 좌표: wx = (x+0.5)*cellX, wy = RowY(y) (첫 터널 줄 앞면 = 0), z 위 (Unity 에서는 (x, z, y))
using System;
using System.Collections.Generic;

namespace AirRob
{
    [Serializable]
    public class AirRobConfig
    {
        public int nx = 16, aisles = 4, levels = 13;            // 통로 길이(칸), 통로 수, 랙 단 수
        public double cellX = 0.90;                             // 칸 폭 = 기둥 간격, 칸마다 토트 2열 (가정)
        public double colOff = 0.215;                           // 칸 중심 → 토트 열 중심 (토트 사이 10 mm: 공개)
        public double aisleW = 0.85, rackD = 0.66, flue = 0.06; // 통로 850 mm (공개), 랙 깊이, 등지는 랙 틈 (가정)
        public double parkW = 0.90;                             // 맨 뒤 주차 줄 폭
        public double level0Z = 0.85, levelH = 0.40;            // 맨 아래 보관 단 높이 (아래는 터널), 단 간격 (가정)
        public double toteW = 0.41, toteD = 0.53, toteH = 0.30; // 토트 530 x 410 mm (공개), 높이 300 mm (가정)
        public double deckZ = 0.33;                             // 바닥 로봇 상판 = 주고받기 높이 (가정)
        public double fill = 0.85;
        public int bots = 16;                                   // 주차 줄 칸 수(nx) 이하
        public int[] stations = { 1, 6 };
        public int spurLen = 4;
        public double frontY = -0.70, spurY = -1.70;
        public double orderRate = 360;                          // 토트 요청 / 시간
        public double popularShare = 0.2, popularDemand = 0.8;
        public double vmax = 2.0, acc = 1.5, turn = 0.4;        // 바닥 로봇 (가정)
        public double airVx = 2.0, airAx = 2.0;                 // AirRob 수평 2 m/s, 2 m/s² (공개)
        public double airVz = 1.0, airAz = 0.8;                 // AirRob 수직 1 m/s, 0.8 m/s² (공개)
        public double hookT = 4.5;                              // 당기기 / 밀기 한 번 4.5 s (공개)
        public int maxPerTunnel = 2;                            // 터널 하나에 동시에 배정하는 작업 수
        public double abortWait = 2.0;
        public double pickMin = 3, pickMax = 6;
        public int seed = 3;
    }

    public enum LocKind { Slot, Bot, Air }
    public struct ToteLoc { public LocKind kind; public int s, r, a; }

    public class Job
    {
        public int id, slot, phase, cell, side;
        public bool isGet, handed;
        public Bot bot; public double t0;
    }

    public class Act
    {
        public string k;                 // 바닥 로봇: move | hand | pick, AirRob: go | hook
        public int to = -1, st = -1;
        public bool park, done, started, swapped, abort;
        public bool pull, toSlot, fromSlot;
        public Job job;
        public double t, x, z, x0, z0, dx, dz, T;
    }

    public class Seg
    {
        public int d, from, n, released;
        public List<int> line = new List<int>();
        public List<double> cum = new List<double>();
        public double x0, y0, s, v, wait;
    }

    public class Order { public int id, tote; public double tArrive, tPresent = -1, tDone = -1; }

    public class Station { public int id, x0, load, picked, cur = -1; public double busyTime; }

    public class Bot
    {
        public int id, x, y, carry = -1, lastDir = -1;
        public double px, py, waitT, blocked, busyTime, blockedTime, dist;
        public List<Act> acts = new List<Act>(); public Seg seg;
        public string state = "idle"; public Order order;
    }

    public class Air
    {
        public int id, side = 1, carry = -1, ops, aborts;
        public double px, pz, hook, waitT, busyTime, liftH;
        public List<Act> acts = new List<Act>();
        public List<Job> jobs = new List<Job>();
        public Job job;
        public string state = "idle";
    }

    public class AirRobSim
    {
        public const int VOID = 0, LANE = 1, TUNNEL = 2, PARK = 3, SPUR = 4, PICK = 5;
        static readonly int[,] DIRS = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };
        const double PULL_AT = 0.45, PUSH_AT = 0.55;

        public readonly AirRobConfig c;
        public double t;
        public readonly int R, W, H, N;
        public readonly byte[] type;
        public readonly int[] stOf, occ, parkRes;
        readonly List<int>[] succ;
        public readonly double[] rowYs;
        public readonly double depthY, rackDepthY;
        public readonly List<Station> stations = new List<Station>();
        public readonly int nSlots;
        public readonly int[] slotTote;
        public ToteLoc[] toteLoc;
        public int[] toteHome;
        public int totes, nPopular;
        public byte[] busy;
        public readonly List<Air> airs = new List<Air>();
        public readonly List<Bot> robots = new List<Bot>();
        public readonly List<Order> orders = new List<Order>();
        readonly List<Order> pending = new List<Order>();
        double nextOrderT;
        public int statDone, statJobs; public double leadSum, leadMax;
        int rngState;

        public AirRobSim(AirRobConfig cfg)
        {
            c = cfg; rngState = c.seed;
            t = 0;
            int K = c.aisles;
            R = 3 * K; W = c.nx + 2; H = R + 3; N = W * H;
            type = new byte[N]; stOf = Fill(N, -1); occ = Fill(N, -1); parkRes = Fill(N, -1);
            rowYs = new double[R + 1];
            double acc = 0;
            for (int y = 0; y <= R; y++)
            {
                if (y > 0 && y < R && y % 3 == 0) acc += c.flue;
                if (y == R) acc += 0.10;
                double w = RowW(y); rowYs[y] = acc + w / 2; acc += w;
            }
            depthY = acc;
            rackDepthY = rowYs[R - 1] + c.rackD / 2;
            for (int y = -2; y <= R; y++)
                for (int x = -1; x <= c.nx; x++)
                {
                    int tt = VOID;
                    if (y == -1) tt = LANE;
                    else if (y >= 0 && y < R && (x == -1 || x == c.nx)) tt = LANE;
                    else if (y >= 0 && x >= 0 && x < c.nx) tt = y == R ? PARK : (y % 3 == 1 ? VOID : TUNNEL);
                    type[Ci(x, y)] = (byte)tt;
                }
            for (int si = 0; si < c.stations.Length; si++)
            {
                int x0 = c.stations[si];
                for (int k = 0; k < c.spurLen; k++) { int i = Ci(x0 + k, -2); type[i] = (byte)(k == 0 ? PICK : SPUR); stOf[i] = si; }
                stations.Add(new Station { id = si, x0 = x0 });
            }
            succ = new List<int>[N];
            for (int i = 0; i < N; i++) succ[i] = new List<int>();
            for (int y = -2; y <= R; y++)
                for (int x = -1; x <= c.nx; x++)
                {
                    int i = Ci(x, y), tt = type[i];
                    if (tt == VOID) continue;
                    var s = succ[i];
                    int xx = x, yy = y;
                    Action<int> add = d => { int nx = xx + DIRS[d, 0], ny = yy + DIRS[d, 1]; if (TypeAt(nx, ny) != VOID) s.Add(Ci(nx, ny) * 4 + d); };
                    if (y == -1)
                    {
                        foreach (var st in stations) if (x == st.x0 + c.spurLen - 1) add(3);
                        if (x > -1) add(1); else add(2);
                    }
                    else if (y == -2) { if (tt == PICK) add(2); else add(1); }
                    else if (x == -1) { if (y % 3 != 1) add(0); if (y < R - 1) add(2); }
                    else if (x == c.nx) add(3);
                    else if (tt == TUNNEL) { add(0); if (y == R - 1) add(2); }
                    else if (tt == PARK) add(3);
                }
            // 토트 자리: (통로 a, 칸 x, 열 col, 쪽 s(0 = -y, 1 = +y), 단 l)
            nSlots = K * c.nx * 2 * 2 * c.levels;
            slotTote = Fill(nSlots, -1);
            var cand = new List<(int id, double key)>();
            for (int id = 0; id < nSlots; id++)
                if (Rand() < c.fill) cand.Add((id, SlotCost(id) + Gauss() * 3.0));
            cand.Sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : a.id.CompareTo(b.id));
            toteLoc = new ToteLoc[cand.Count]; toteHome = new int[cand.Count];
            for (int id = 0; id < cand.Count; id++)
            {
                slotTote[cand[id].id] = id; toteLoc[id] = new ToteLoc { kind = LocKind.Slot, s = cand[id].id }; toteHome[id] = cand[id].id;
            }
            totes = cand.Count;
            nPopular = Math.Max(1, (int)Math.Floor(totes * c.popularShare));
            busy = new byte[totes];
            for (int a = 0; a < K; a++)
                airs.Add(new Air { id = a, px = WorldX(c.nx / 2), pz = c.deckZ });
            for (int i = 0; i < Math.Min(c.bots, c.nx); i++)   // 주차 줄 칸 수까지
            {
                int x, k;
                do { x = (int)Math.Floor(Rand() * c.nx); k = Ci(x, R); } while (occ[k] != -1);
                var r = new Bot { id = i, x = x, y = R, px = WorldX(x), py = RowY(R) };
                occ[k] = i; parkRes[k] = i; robots.Add(r);
            }
            nextOrderT = Expo(3600 / c.orderRate);
        }

        static int[] Fill(int n, int v) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        // ------------------------------------------------------------ 기본 (mulberry32: JS 와 같은 수열)
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
            if (t >= T) return d;
            if (t <= 0) return 0;
            double vp = v, ta = v / a;
            if (d < v * v / a) { vp = Math.Sqrt(d * a); ta = vp / a; }
            if (t < ta) return 0.5 * a * t * t;
            double dA = 0.5 * a * ta * ta, tc = T - 2 * ta;
            if (t < ta + tc) return dA + vp * (t - ta);
            double td = T - t;
            return d - 0.5 * a * td * td;
        }

        public int Ci(int x, int y) => (x + 1) + (y + 2) * W;
        public int Cx(int i) => i % W - 1;
        public int Cy(int i) => i / W - 2;
        public bool Inb(int x, int y) => x >= -1 && x <= c.nx && y >= -2 && y <= R;
        public int TypeAt(int x, int y) => Inb(x, y) ? type[Ci(x, y)] : VOID;
        double RowW(int y) { if (y == R) return c.parkW; return y % 3 == 1 ? c.aisleW : c.rackD; }
        public double RowY(int y) { if (y == -1) return c.frontY; if (y == -2) return c.spurY; return rowYs[y]; }
        public double WorldX(int x) => (x + 0.5) * c.cellX;
        public double LevelZ(int l) => c.level0Z + l * c.levelH;
        public double AisleY(int a) => rowYs[3 * a + 1];
        public int TunnelRow(int a, int side) => 3 * a + (side > 0 ? 2 : 0);
        public double Reach => c.aisleW / 2 + c.rackD / 2;
        public int SlotAisle(int s) => s / (c.nx * 4 * c.levels);
        public int SlotX(int s) => (s / (4 * c.levels)) % c.nx;
        public int SlotCol(int s) => (s / (2 * c.levels)) % 2;
        public int SlotSide(int s) => (s / c.levels) % 2 == 1 ? 1 : -1;
        public int SlotLevel(int s) => s % c.levels;
        public double SlotWX(int s) => WorldX(SlotX(s)) + (SlotCol(s) == 1 ? 1 : -1) * c.colOff;
        public (double x, double y, double z) SlotPos(int s) =>
            (SlotWX(s), AisleY(SlotAisle(s)) + SlotSide(s) * Reach, LevelZ(SlotLevel(s)));
        double SlotCost(int s)
        {
            int x0 = c.stations[0];
            int ty = TunnelRow(SlotAisle(s), SlotSide(s));
            double d = (x0 + 1) * c.cellX + (RowY(ty) - c.frontY) + (SlotX(s) + 1) * c.cellX;
            return d / c.vmax + 2 * ProfileTime(LevelZ(SlotLevel(s)) - c.deckZ, c.airVz, c.airAz);
        }
        int HandoverCell(int s) => Ci(SlotX(s), TunnelRow(SlotAisle(s), SlotSide(s)));

        // ------------------------------------------------------------ 경로 (일방통행 그래프 BFS)
        bool CanEnter(int k, Func<int, bool> goal, int goalSt)
        {
            int tt = type[k];
            if (tt == PARK) return goal(k);
            if (tt == SPUR || tt == PICK) return stOf[k] == goalSt;
            return true;
        }

        int[] prevBuf; readonly List<int> qBuf = new List<int>();
        List<int> Bfs(int from, Func<int, bool> goal, int goalSt)
        {
            if (prevBuf == null) prevBuf = new int[N];
            var prev = prevBuf; for (int i = 0; i < N; i++) prev[i] = -2;
            var q = qBuf; q.Clear(); q.Add(from);
            prev[from] = -1;
            for (int h = 0; h < q.Count; h++)
            {
                int u = q[h];
                if (u != from && goal(u))
                {
                    var path = new List<int>(); int v = u;
                    while (v != from) { path.Add(v); v = prev[v]; }
                    path.Reverse();
                    return path;
                }
                if (u != from && type[u] == PARK) continue;
                foreach (int e in succ[u])
                {
                    int k = e >> 2;
                    if (prev[k] != -2 || !CanEnter(k, goal, goalSt)) continue;
                    prev[k] = u; q.Add(k);
                }
            }
            return null;
        }
        int DirOf(int a, int b) { int dx = Cx(b) - Cx(a), dy = Cy(b) - Cy(a); return dx > 0 ? 0 : dx < 0 ? 1 : dy > 0 ? 2 : 3; }
        double StepDist(int a, int b)
        {
            int dx = Cx(b) - Cx(a);
            if (dx != 0) return Math.Abs(dx) * c.cellX;
            return Math.Abs(RowY(Cy(b)) - RowY(Cy(a)));
        }
        int TargetSt(int k) => type[k] == SPUR || type[k] == PICK ? stOf[k] : -1;

        // ------------------------------------------------------------ 바닥 로봇
        void StepRobot(Bot r, double dt)
        {
            if (r.acts.Count > 0 || r.seg != null) r.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (r.acts.Count == 0) { if (r.seg == null) r.state = "idle"; return; }
                var a = r.acts[0];
                double used = 0;
                if (a.k == "move") used = ActMove(r, a, dt);
                else if (a.k == "hand") used = ActHand(r, a, dt);
                else if (a.k == "pick") used = ActPick(r, a, dt);
                if (a.done) r.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }

        double ActMove(Bot r, Act a, double dt)
        {
            if (r.seg == null)
            {
                int here = Ci(r.x, r.y);
                if (here == a.to) { a.done = true; return 0; }
                if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; r.blockedTime += dt; return dt; }
                int to = a.to;
                var path = Bfs(here, k => k == to, TargetSt(to));
                if (path == null) { r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.25; return dt; }
                int d = DirOf(here, path[0]);
                var ng = new Seg { d = d, from = here, x0 = r.px, y0 = r.py };
                int prev = here;
                foreach (int k in path)
                {
                    if (DirOf(prev, k) != d) break;
                    ng.line.Add(k); prev = k;
                    if (type[k] == PICK || k == to) break;
                }
                if (occ[ng.line[0]] != -1) { r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.1; return dt; }
                r.blocked = 0;
                double sum = 0; prev = here;
                foreach (int k in ng.line) { sum += StepDist(prev, k); ng.cum.Add(sum); prev = k; }
                ng.wait = (r.lastDir != -1 && r.lastDir != d) ? c.turn : 0;
                r.seg = ng;
                r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move";
            }
            var g = r.seg; double dt0 = dt;
            if (g.wait > 0) { g.wait -= dt; if (g.wait > 0) return dt0; dt = -g.wait; g.wait = 0; }
            double look = g.v * g.v / (2 * c.acc) + c.cellX;
            while (g.n < g.line.Count && (g.n == 0 || g.cum[g.n - 1] - g.s < look))
            {
                int k = g.line[g.n];
                if (occ[k] != -1) break;
                occ[k] = r.id; g.n++;
            }
            double end = g.n > 0 ? g.cum[g.n - 1] : 0, rem = end - g.s;
            double vAllow = Math.Sqrt(2 * c.acc * Math.Max(0, rem));
            g.v = Math.Min(Math.Min(g.v + c.acc * dt, c.vmax), vAllow);
            g.s = Math.Min(end, g.s + g.v * dt);
            r.px = g.x0 + DIRS[g.d, 0] * g.s; r.py = g.y0 + DIRS[g.d, 1] * g.s;
            r.state = g.v < 0.05 && g.n < g.line.Count && end - g.s < 1e-3 ? "blocked" : (r.carry >= 0 ? "carry" : "move");
            if (r.state == "blocked") r.blockedTime += dt;
            while (g.released < g.n && g.s >= g.cum[g.released] - 1e-9)
            {
                int pc = g.released == 0 ? g.from : g.line[g.released - 1];
                occ[pc] = -1; g.released++;
                int k = g.line[g.released - 1]; r.x = Cx(k); r.y = Cy(k);
            }
            if (g.released == g.line.Count)
            {
                int k = g.line[g.line.Count - 1];
                r.px = WorldX(r.x); r.py = RowY(r.y);
                r.dist += g.cum[g.line.Count - 1]; r.seg = null; r.blocked = 0;
                if (k == a.to) a.done = true;
                return dt0;
            }
            if (r.state == "blocked")
            {
                r.blocked += dt;
                if (r.blocked > 3 && g.released == g.n)
                {
                    for (int i = g.released; i < g.n; i++) occ[g.line[i]] = -1;
                    if (g.released > 0) r.dist += g.cum[g.released - 1];
                    r.px = WorldX(r.x); r.py = RowY(r.y); r.seg = null; r.blocked = 0;
                }
            }
            else r.blocked = 0;
            return dt0;
        }

        double ActHand(Bot r, Act a, double dt)
        {
            if (a.job.handed) { a.done = true; return 0; }
            r.state = "wait";
            return dt;
        }

        double ActPick(Bot r, Act a, double dt)
        {
            var st = stations[a.st];
            if (!a.started)
            {
                a.started = true;
                a.t = c.pickMin + Rand() * (c.pickMax - c.pickMin);
                st.cur = r.id; r.state = "pick";
                r.order.tPresent = t;
            }
            a.t -= dt; st.busyTime += Math.Min(dt, Math.Max(0, a.t + dt));
            if (a.t > 0) return dt;
            double used = dt + a.t;
            a.done = true; st.cur = -1; st.picked++; st.load--;
            var o = r.order; o.tDone = t + used;
            double lead = o.tDone - o.tArrive;
            statDone++; leadSum += lead; leadMax = Math.Max(leadMax, lead);
            r.order = null;
            int s = toteHome[r.carry];
            var job = NewJob(false, s, r);
            r.acts.Add(new Act { k = "move", to = HandoverCell(s) });
            r.acts.Add(new Act { k = "hand", job = job });
            return used;
        }

        Job NewJob(bool isGet, int s, Bot r)
        {
            var job = new Job { id = statJobs++, isGet = isGet, slot = s, bot = r, cell = SlotX(s), side = SlotSide(s), t0 = t };
            airs[SlotAisle(s)].jobs.Add(job);
            return job;
        }

        // ------------------------------------------------------------ AirRob
        bool BotArrived(Job job)
        {
            var r = job.bot;
            if (r.acts.Count == 0) return false;
            var a = r.acts[0];
            return a.k == "hand" && a.job == job && r.seg == null;
        }

        bool AheadWaiting(Air A, Job J, bool arrivedOnly)
        {
            int ty = TunnelRow(A.id, J.side); var b = J.bot;
            foreach (var K in A.jobs)
                if (K != J && K.phase == 0 && !K.handed && K.bot.y == ty && K.bot.x > b.x && (!arrivedOnly || BotArrived(K))) return true;
            return false;
        }
        bool InTunnel(Job J) { var b = J.bot; return b.y == TunnelRow(SlotAisle(J.slot), J.side) && b.x >= 0 && b.x < c.nx; }
        int TunnelJobs(int a, int side) { int n = 0; foreach (var j in airs[a].jobs) if (j.side == side) n++; return n; }

        Job Find(Air A, Func<Job, bool> f) { foreach (var j in A.jobs) if (f(j)) return j; return null; }

        void PlanAir(Air A, double dt)
        {
            var J = A.job;
            if (A.carry >= 0 && J != null && J.isGet)
            {
                double hx = WorldX(J.cell);
                if (Math.Abs(A.px - hx) > 1e-6 || Math.Abs(A.pz - c.deckZ) > 1e-6) { A.acts.Add(new Act { k = "go", x = hx, z = c.deckZ }); return; }
                if (BotArrived(J)) { A.waitT = 0; A.acts.Add(new Act { k = "hook", pull = false, toSlot = false, job = J }); return; }
                A.waitT += dt; A.state = "wait";
                if (A.waitT > c.abortWait && J.bot.state == "blocked" && AheadWaiting(A, J, true))
                {
                    var p = SlotPos(J.slot);
                    A.acts.Add(new Act { k = "go", x = p.x, z = p.z });
                    A.acts.Add(new Act { k = "hook", pull = false, toSlot = true, job = J, abort = true });
                    A.aborts++; A.waitT = 0;
                }
                return;
            }
            A.waitT = 0;
            var K = Find(A, j => !j.isGet && j.phase == 0 && BotArrived(j));
            if (K != null)
            {
                var p = SlotPos(K.slot);
                K.phase = 1; A.job = K;
                A.acts.Add(new Act { k = "go", x = WorldX(K.cell), z = c.deckZ });
                A.acts.Add(new Act { k = "hook", pull = true, fromSlot = false, job = K });
                A.acts.Add(new Act { k = "go", x = p.x, z = p.z });
                A.acts.Add(new Act { k = "hook", pull = false, toSlot = true, job = K });
                return;
            }
            K = Find(A, j => j.isGet && j.phase == 0 && BotArrived(j)) ?? Find(A, j => j.isGet && j.phase == 0 && InTunnel(j) && !AheadWaiting(A, j, false));
            if (K != null)
            {
                var p = SlotPos(K.slot);
                K.phase = 1; A.job = K;
                A.acts.Add(new Act { k = "go", x = p.x, z = p.z });
                A.acts.Add(new Act { k = "hook", pull = true, fromSlot = true, job = K });
                return;
            }
            K = Find(A, j => !j.isGet && j.phase == 0);
            if (K != null)
            {
                double hx = WorldX(K.cell);
                if (Math.Abs(A.px - hx) > 1e-6 || Math.Abs(A.pz - c.deckZ) > 1e-6) { A.acts.Add(new Act { k = "go", x = hx, z = c.deckZ }); return; }
                A.state = "wait"; return;
            }
            A.state = "idle";
        }

        void StepAir(Air A, double dt)
        {
            if (A.acts.Count == 0) PlanAir(A, dt);
            if (A.acts.Count > 0) A.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (A.acts.Count == 0) return;
                var a = A.acts[0];
                double used = a.k == "go" ? AirGo(A, a, dt) : AirHook(A, a, dt);
                if (a.done) A.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }

        double AirGo(Air A, Act a, double dt)
        {
            if (!a.started)
            {
                a.started = true; a.t = 0; a.x0 = A.px; a.z0 = A.pz;
                a.dx = Math.Abs(a.x - a.x0); a.dz = Math.Abs(a.z - a.z0);
                a.T = Math.Max(ProfileTime(a.dx, c.airVx, c.airAx), ProfileTime(a.dz, c.airVz, c.airAz));
                if (a.dz > 0.01) A.liftH += a.dz;
            }
            a.t += dt;
            double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
            A.px = a.x0 + Math.Sign(a.x - a.x0) * ProfilePos(a.t, a.dx, c.airVx, c.airAx);
            A.pz = a.z0 + Math.Sign(a.z - a.z0) * ProfilePos(a.t, a.dz, c.airVz, c.airAz);
            A.state = a.dz > 0.01 ? "lift" : "move";
            if (a.t >= a.T) { A.px = a.x; A.pz = a.z; a.done = true; }
            return used;
        }

        static double Smooth(double u) { u = Math.Min(1, Math.Max(0, u)); return u * u * (3 - 2 * u); }

        double AirHook(Air A, Act a, double dt)
        {
            var J = a.job;
            if (!a.started) { a.started = true; a.t = 0; a.swapped = false; A.side = J.side; A.ops++; }
            a.t += dt;
            double used = dt;
            if (a.t >= c.hookT) { used = dt - (a.t - c.hookT); a.t = c.hookT; }
            double u = a.t / c.hookT; int s = J.side;
            double at = a.pull ? PULL_AT : PUSH_AT;
            A.hook = u < at ? s * Smooth(u / at) : s * (1 - Smooth((u - at) / (1 - at)));
            A.state = a.pull ? "pull" : "push";
            if (!a.swapped && u >= at)
            {
                a.swapped = true;
                if (a.pull)
                {
                    int id;
                    if (a.fromSlot) { id = slotTote[J.slot]; slotTote[J.slot] = -1; }
                    else { var r = J.bot; id = r.carry; r.carry = -1; }
                    A.carry = id; toteLoc[id] = new ToteLoc { kind = LocKind.Air, a = A.id };
                }
                else
                {
                    int id = A.carry; A.carry = -1;
                    if (a.toSlot)
                    {
                        slotTote[J.slot] = id; toteLoc[id] = new ToteLoc { kind = LocKind.Slot, s = J.slot };
                        if (!a.abort) busy[id] = 0;
                    }
                    else
                    {
                        var r = J.bot; r.carry = id; toteLoc[id] = new ToteLoc { kind = LocKind.Bot, r = r.id };
                        J.handed = true;
                    }
                }
            }
            if (a.t >= c.hookT)
            {
                A.hook = 0; a.done = true;
                if (a.pull && !a.fromSlot) J.handed = true;
                bool finished = (!a.pull && !a.toSlot) || (!a.pull && a.toSlot && !a.abort);
                if (a.abort) { J.phase = 0; A.job = null; }
                if (finished) { A.jobs.Remove(J); A.job = null; }
            }
            return used;
        }

        // ------------------------------------------------------------ 주문 / 배정
        void NewOrder()
        {
            for (int k = 0; k < 10; k++)
            {
                bool pop = Rand() < c.popularDemand;
                int id = pop ? (int)Math.Floor(Rand() * nPopular) : nPopular + (int)Math.Floor(Rand() * (totes - nPopular));
                if (busy[id] != 0) continue;
                busy[id] = 1;
                var o = new Order { id = orders.Count, tote = id, tArrive = t };
                orders.Add(o); pending.Add(o);
                return;
            }
        }

        bool Free(Bot r) => r.order == null && (r.acts.Count == 0 || (r.acts.Count == 1 && r.acts[0].park));

        void Dispatch()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var o = pending[i]; int s = toteLoc[o.tote].s, to = HandoverCell(s);
                if (TunnelJobs(SlotAisle(s), SlotSide(s)) >= c.maxPerTunnel) continue;
                Bot best = null; int bd = int.MaxValue;
                foreach (var r in robots)
                {
                    if (!Free(r)) continue;
                    int here = Ci(r.x, r.y);
                    int len;
                    if (here == to) len = 0;
                    else { var p = Bfs(here, k => k == to, -1); if (p == null) continue; len = p.Count; }
                    if (len < bd) { bd = len; best = r; }
                }
                if (best == null) return;
                pending.RemoveAt(i); i--;
                var st = stations[0];
                foreach (var x in stations) if (x.load < st.load) st = x;
                st.load++;
                var rb = best;
                for (int k = 0; k < N; k++) if (parkRes[k] == rb.id) parkRes[k] = -1;
                rb.acts.Clear();
                rb.order = o;
                var job = NewJob(true, s, rb);
                rb.acts.Add(new Act { k = "move", to = to });
                rb.acts.Add(new Act { k = "hand", job = job });
                rb.acts.Add(new Act { k = "move", to = Ci(st.x0, -2) });
                rb.acts.Add(new Act { k = "pick", st = st.id });
            }
        }

        void Park(Bot r)
        {
            if (r.acts.Count > 0 || r.seg != null) return;
            int here = Ci(r.x, r.y);
            if (type[here] == PARK) { parkRes[here] = r.id; return; }
            var path = Bfs(here, k => type[k] == PARK && parkRes[k] == -1 && occ[k] == -1, -1);
            if (path == null) return;
            int kk = path[path.Count - 1];
            parkRes[kk] = r.id;
            r.acts.Add(new Act { k = "move", to = kk, park = true });
        }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600 / c.orderRate); }
            Dispatch();
            foreach (var r in robots) if (r.order == null && r.acts.Count == 0) Park(r);
            foreach (var r in robots) StepRobot(r, dt);
            foreach (var A in airs) StepAir(A, dt);
        }

        // 토트 바닥 중심 위치 — 렌더링용
        public (double x, double y, double z) TotePose(int id)
        {
            var L = toteLoc[id];
            if (L.kind == LocKind.Bot) { var r = robots[L.r]; return (r.px, r.py, c.deckZ); }
            if (L.kind == LocKind.Air) { var A = airs[L.a]; return (A.px, AisleY(A.id) + A.hook * Reach, A.pz); }
            return SlotPos(L.s);
        }

        public double ThroughputPerHour => t > 0 ? statDone / t * 3600 : 0;
        public double AvgLead => statDone > 0 ? leadSum / statDone : 0;
        public int Backlog => pending.Count;
        public double BotUtil { get { double s = 0; foreach (var r in robots) s += r.busyTime; return t > 0 ? s / (robots.Count * t) : 0; } }
        public double AirUtil { get { double s = 0; foreach (var A in airs) s += A.busyTime; return t > 0 ? s / (airs.Count * t) : 0; } }
        public double StationUtil { get { double s = 0; foreach (var st in stations) s += st.busyTime; return t > 0 ? s / (stations.Count * t) : 0; } }
    }
}
