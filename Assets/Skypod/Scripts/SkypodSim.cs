// Exotec Skypod 방식 "바닥 주행 + 랙 등반" 로봇 창고 이산시간 시뮬레이션
// (web/skypod-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 랙 줄(y 짝수)과 통로(y 홀수)가 번갈아 있고, 로봇은 바닥을 달리다 목표 통로 칸에서 양옆 랙 기둥을 잡고 올라가
// 토트를 옆으로 꺼내거나 넣습니다. 꺼낸 토트는 앞쪽 피킹 스테이션으로 가져가고, 피킹 후 빈 자리에 다시 넣습니다.
// 좌표: wx = (x+0.5)*cellX, wy = RowY(y) (랙 0번 줄 앞면 = 0), z 위 (Unity 에서는 (x, z, y))
using System;
using System.Collections.Generic;

namespace Skypod
{
    [Serializable]
    public class SkypodConfig
    {
        public int nx = 16, aisles = 4, levels = 20;           // 통로 길이(칸), 통로 수, 랙 단 수
        public double cellX = 0.72;                            // 랙 칸 폭 = 기둥 간격 (가정)
        public double aisleW = 0.80, rackD = 0.47, flue = 0.04; // 통로 폭, 한쪽 랙 깊이, 등지는 랙 사이 틈 (가정)
        public double level0Z = 1.25, levelH = 0.42;           // 맨 아래 단 높이, 단 간격 (가정)
        public double toteW = 0.65, toteD = 0.45, toteH = 0.32; // 토트 650 x 450 x 320 mm
        public double bayZ = 0.40;                             // 로봇 몸체 안 토트 받침 높이 (가정)
        public double fill = 0.85;
        public int robots = 20;
        public int[] stations = { 1, 6, 11 };                  // 스테이션 시작 x (피킹 1칸 + 대기 spurLen-1칸)
        public int spurLen = 5;
        public double frontY = -0.80, spurY = -1.75;           // 앞쪽 주행로 / 스테이션 줄 중심 y
        public double orderRate = 800;                         // 토트 요청 / 시간
        public double popularShare = 0.2, popularDemand = 0.8;
        public double vmax = 4.0, acc = 3.0;                   // 바닥 4 m/s (공개 자료), 가속도 (가정)
        public double turn = 0.35;                             // 방향 바꿀 때 조향 정지 (가정)
        public double climbV = 3.0, climbAcc = 2.0;            // 등반 (가정)
        public double engage = 0.4;                            // 랙 기둥 맞물림 / 풀림 (가정)
        public double armT = 1.1;                              // 암 한 번 펴기 또는 접기 (가정)
        public double pickMin = 4, pickMax = 8;                // 작업자 피킹 시간 / 토트
        public int seed = 3;
    }

    public struct ToteLoc { public bool onRobot; public int s, r; }

    public class Act
    {
        public string k;                 // move | climb | pick
        public int to = -1, slot = -1, st = -1;
        public bool put, done, started, swapped;
        public double t, h, tu, T;
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

    public class Robot
    {
        public int id, x, y, carry = -1, lastDir = -1, climbs;
        public double px, py, pz, arm, side = 1, waitT, blocked, busyTime, blockedTime, climbH, dist;
        public List<Act> acts = new List<Act>(); public Seg seg;
        public string state = "idle"; public Order order;
    }

    public class SkypodSim
    {
        public const int VOID = 0, LANE = 1, AISLE = 2, PARK = 3, SPUR = 4, PICK = 5, UNDER = 6;
        static readonly int[,] DIRS = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };

        public readonly SkypodConfig c;
        public double t;
        public readonly int Y1, W, H, N;
        public readonly byte[] type;
        public readonly int[] stOf, occ, parkRes;
        readonly List<int>[] succ;
        public readonly double[] rowYs;
        public readonly double depthY;
        public readonly List<Station> stations = new List<Station>();
        public readonly int nSlots;
        public readonly int[] slotTote;
        public readonly byte[] slotRes;
        public ToteLoc[] toteLoc;
        public int totes, nPopular;
        public byte[] busy;
        public readonly List<Robot> robots = new List<Robot>();
        public readonly List<Order> orders = new List<Order>();
        readonly List<Order> pending = new List<Order>();
        double nextOrderT;
        public int statDone, statRetrieves; public double leadSum, leadMax;
        int rngState;

        public SkypodSim(SkypodConfig cfg)
        {
            c = cfg; rngState = c.seed;
            t = 0;
            int K = c.aisles;
            Y1 = 2 * K; W = c.nx + 2; H = Y1 + 3; N = W * H;
            type = new byte[N]; stOf = Fill(N, -1); occ = Fill(N, -1); parkRes = Fill(N, -1);
            rowYs = new double[Y1 + 1];
            double acc = 0;
            for (int y = 0; y <= Y1; y++) { double w = RowW(y); rowYs[y] = acc + w / 2; acc += w; }
            depthY = acc;
            for (int y = -2; y <= Y1; y++)
                for (int x = -1; x <= c.nx; x++)
                {
                    int tt = VOID;
                    if (y == -1) tt = LANE;
                    else if (y >= 0 && (x == -1 || x == c.nx) && y < Y1) tt = LANE;
                    else if (y >= 0 && x >= 0 && x < c.nx) tt = (y % 2 == 1) ? AISLE : (y == 0 || y == Y1) ? PARK : UNDER;
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
            for (int y = -2; y <= Y1; y++)
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
                    else if (x == -1) { if (y % 2 == 1) add(0); if (y < Y1 - 1) add(2); }
                    else if (x == c.nx) add(3);
                    else if (tt == AISLE) { add(0); add(3); add(2); }
                    else if (tt == PARK) add(y == 0 ? 2 : 3);
                    else if (tt == UNDER) add(1);
                }
            // 토트 자리: (통로 a, 칸 x, 쪽 s(0 = -y, 1 = +y), 단 l)
            nSlots = K * c.nx * 2 * c.levels;
            slotTote = Fill(nSlots, -1); slotRes = new byte[nSlots];
            var cand = new List<(int id, double key)>();
            for (int id = 0; id < nSlots; id++)
                if (Rand() < c.fill) cand.Add((id, SlotCost(id) + Gauss() * 3.0));
            cand.Sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : a.id.CompareTo(b.id));
            toteLoc = new ToteLoc[cand.Count];
            for (int id = 0; id < cand.Count; id++) { slotTote[cand[id].id] = id; toteLoc[id] = new ToteLoc { s = cand[id].id }; }
            totes = cand.Count;
            nPopular = Math.Max(1, (int)Math.Floor(totes * c.popularShare));
            busy = new byte[totes];
            // 로봇: 맨 앞·맨 뒤 랙 아래 주차 칸에서 시작
            for (int i = 0; i < c.robots; i++)
            {
                int x, y, k;
                do { x = (int)Math.Floor(Rand() * c.nx); y = Rand() < 0.5 ? 0 : Y1; k = Ci(x, y); }
                while (occ[k] != -1);
                var r = new Robot { id = i, x = x, y = y, px = WorldX(x), py = RowY(y) };
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
        public bool Inb(int x, int y) => x >= -1 && x <= c.nx && y >= -2 && y <= Y1;
        public int TypeAt(int x, int y) => Inb(x, y) ? type[Ci(x, y)] : VOID;
        double RowW(int y) { if (y % 2 == 1) return c.aisleW; return (y == 0 || y == Y1) ? c.rackD + c.flue / 2 : 2 * c.rackD + c.flue; }
        public double RowY(int y) { if (y == -1) return c.frontY; if (y == -2) return c.spurY; return rowYs[y]; }
        public double WorldX(int x) => (x + 0.5) * c.cellX;
        public double LevelZ(int l) => c.level0Z + l * c.levelH;
        public double ClimbZ(int l) => LevelZ(l) - c.bayZ;
        public int SlotAisle(int s) => s / (c.nx * 2 * c.levels);
        public int SlotX(int s) => (s / (2 * c.levels)) % c.nx;
        public int SlotSide(int s) => (s / c.levels) % 2;
        public int SlotLevel(int s) => s % c.levels;
        public double Reach => c.aisleW / 2 + c.rackD / 2;
        public (double x, double y, double z) SlotPos(int s)
        {
            double y = RowY(2 * SlotAisle(s) + 1) + (SlotSide(s) == 1 ? 1 : -1) * Reach;
            return (WorldX(SlotX(s)), y, LevelZ(SlotLevel(s)));
        }
        double SlotCost(int s)
        {
            int x0 = c.stations[c.stations.Length / 2];
            int ay = 2 * SlotAisle(s) + 1;
            double d = (x0 + 1) * c.cellX + (RowY(ay) - c.frontY) + (SlotX(s) + 1) * c.cellX;
            return d / c.vmax + 2 * ProfileTime(ClimbZ(SlotLevel(s)), c.climbV, c.climbAcc);
        }

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

        // ------------------------------------------------------------ 행동 실행
        void StepRobot(Robot r, double dt)
        {
            if (r.acts.Count > 0 || r.seg != null) r.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (r.acts.Count == 0) { r.state = "idle"; return; }
                var a = r.acts[0];
                double used = 0;
                if (a.k == "move") used = ActMove(r, a, dt);
                else if (a.k == "climb") used = ActClimb(r, a, dt);
                else if (a.k == "pick") used = ActPick(r, a, dt);
                if (a.done) r.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }

        double ActMove(Robot r, Act a, double dt)
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

        // 등반: 맞물림 → 위로 → 암 펴기 → (토트 주고받기) → 암 접기 → 아래로 → 풀림
        double ActClimb(Robot r, Act a, double dt)
        {
            if (!a.started)
            {
                a.started = true; a.t = 0; a.h = ClimbZ(SlotLevel(a.slot)); a.tu = ProfileTime(a.h, c.climbV, c.climbAcc);
                a.T = 2 * c.engage + 2 * a.tu + 2 * c.armT; a.swapped = false;
                r.side = SlotSide(a.slot) == 1 ? 1 : -1; r.state = "climb"; r.climbs++; r.climbH += a.h;
            }
            a.t += dt;
            double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
            double e = c.engage, tu = a.tu, A = c.armT, tt = a.t;
            double t1 = e, t2 = e + tu, t3 = t2 + A, t4 = t3 + A, t5 = t4 + tu;
            if (tt < t1) { r.pz = 0; r.arm = 0; }
            else if (tt < t2) { r.pz = ProfilePos(tt - t1, a.h, c.climbV, c.climbAcc); r.arm = 0; }
            else if (tt < t3) { r.pz = a.h; r.arm = Smooth((tt - t2) / A); }
            else if (tt < t4) { r.pz = a.h; r.arm = 1 - Smooth((tt - t3) / A); }
            else if (tt < t5) { r.pz = a.h - ProfilePos(tt - t4, a.h, c.climbV, c.climbAcc); r.arm = 0; }
            else { r.pz = 0; r.arm = 0; }
            r.state = tt >= t2 && tt < t4 ? (a.put ? "put" : "get") : "climb";
            if (!a.swapped && tt >= t3)
            {
                a.swapped = true;
                if (a.put)
                {
                    int id = r.carry; r.carry = -1; slotTote[a.slot] = id; slotRes[a.slot] = 0;
                    toteLoc[id] = new ToteLoc { s = a.slot }; busy[id] = 0;
                }
                else
                {
                    int id = slotTote[a.slot]; slotTote[a.slot] = -1; r.carry = id;
                    toteLoc[id] = new ToteLoc { onRobot = true, r = r.id };
                }
            }
            if (a.t >= a.T) { r.pz = 0; r.arm = 0; a.done = true; }
            return used;
        }
        static double Smooth(double u) { u = Math.Min(1, Math.Max(0, u)); return u * u * (3 - 2 * u); }

        double ActPick(Robot r, Act a, double dt)
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
            int s = ChooseSlot(r.carry);
            slotRes[s] = 1;
            r.acts.Add(new Act { k = "move", to = Ci(SlotX(s), 2 * SlotAisle(s) + 1) });
            r.acts.Add(new Act { k = "climb", slot = s, put = true });
            return used;
        }

        int ChooseSlot(int tote)
        {
            int n = tote < nPopular ? 40 : 8;
            int best = -1, got = 0; double bc = double.PositiveInfinity;
            for (int tries = 0; tries < 20000 && got < n; tries++)
            {
                int s = (int)Math.Floor(Rand() * nSlots);
                if (slotTote[s] != -1 || slotRes[s] != 0) continue;
                got++;
                double cst = SlotCost(s);
                if (cst < bc) { bc = cst; best = s; }
            }
            if (best < 0) for (int s = 0; s < nSlots; s++) if (slotTote[s] == -1 && slotRes[s] == 0) { best = s; break; }
            return best;
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

        static bool Free(Robot r) => r.acts.Count == 0 && r.seg == null;

        void Dispatch()
        {
            while (pending.Count > 0)
            {
                var o = pending[0];
                int s = toteLoc[o.tote].s, to = Ci(SlotX(s), 2 * SlotAisle(s) + 1);
                Robot best = null; int bd = int.MaxValue;
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
                pending.RemoveAt(0);
                var st = stations[0];
                foreach (var x in stations) if (x.load < st.load) st = x;
                st.load++;
                var rb = best;
                int h0 = Ci(rb.x, rb.y);
                if (parkRes[h0] == rb.id) parkRes[h0] = -1;
                rb.order = o; statRetrieves++;
                rb.acts.Add(new Act { k = "move", to = to });
                rb.acts.Add(new Act { k = "climb", slot = s, put = false });
                rb.acts.Add(new Act { k = "move", to = Ci(st.x0, -2) });
                rb.acts.Add(new Act { k = "pick", st = st.id });
            }
        }

        void Park(Robot r)
        {
            int here = Ci(r.x, r.y);
            if (type[here] == PARK) { parkRes[here] = r.id; return; }
            var path = Bfs(here, k => type[k] == PARK && parkRes[k] == -1 && occ[k] == -1, -1);
            if (path == null) return;
            int kk = path[path.Count - 1];
            parkRes[kk] = r.id;
            r.acts.Add(new Act { k = "move", to = kk });
        }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600 / c.orderRate); }
            Dispatch();
            foreach (var r in robots) if (Free(r)) Park(r);
            foreach (var r in robots) StepRobot(r, dt);
        }

        // 토트 바닥 중심 위치 — 렌더링용
        public (double x, double y, double z) TotePose(int id)
        {
            var L = toteLoc[id];
            if (L.onRobot) { var r = robots[L.r]; return (r.px, r.py + r.side * r.arm * Reach, r.pz + c.bayZ); }
            return SlotPos(L.s);
        }

        public double ThroughputPerHour => t > 0 ? statDone / t * 3600 : 0;
        public double AvgLead => statDone > 0 ? leadSum / statDone : 0;
        public int Backlog => pending.Count;
        public double RobotUtil { get { double s = 0; foreach (var r in robots) s += r.busyTime; return t > 0 ? s / (robots.Count * t) : 0; } }
        public double RobotWait { get { double s = 0; foreach (var r in robots) s += r.blockedTime; return t > 0 ? s / (robots.Count * t) : 0; } }
        public double StationUtil { get { double s = 0; foreach (var st in stations) s += st.busyTime; return t > 0 ? s / (stations.Count * t) : 0; } }
        public double AvgClimb { get { double h = 0; int n = 0; foreach (var r in robots) { h += r.climbH; n += r.climbs; } return n > 0 ? h / n : 0; } }
    }
}
