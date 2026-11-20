// HaiPick Climb 방식 랙 등반 로봇 창고 이산시간 시뮬레이션
// (web/haipick-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 구조: x 방향 랙 줄(R)과 통로(A)가 y 방향으로 번갈아 놓임. 로봇은 통로 칸에서 랙 앞면 레일을 잡고 올라가
// 토트를 꺼내고, 바닥(랙 밑 포함)을 달려 앞쪽 작업대에서 토트를 내민 뒤 가까운 빈 칸에 되돌려 놓음.
// 좌표: x = (i+0.5)*cellX, y = 레인 중심, z-up (Unity 에서는 (x, z, y))
using System;
using System.Collections.Generic;

namespace HaiPick
{
    [Serializable]
    public class HaiPickConfig
    {
        public int nx = 22; public double cellX = 0.55;   // 랙 칸 수, 칸 피치 (가정)
        public string lanes = "AARRARRARRA";              // A 통로, R 랙 줄. 맨 앞 통로는 작업대 앞 공간
        public double aisleW = 0.90, rackD = 0.62;        // 통로 900 mm (공식 최소), 랙 깊이 (가정)
        public double rackBase = 0.78, levelH = 0.42; public int levels = 26; public double beamH = 0.08; // 높이 약 11.7 m (공식 최대 12 m)
        public double toteW = 0.40, toteD = 0.60, toteH = 0.30;  // 600 x 400 x 300 mm 토트 (가정)
        public double robotL = 0.48, robotW = 0.75, deckH = 0.32; // 로봇 x 폭, y 길이, 상판 높이 (가정)
        public double fill = 0.85;
        public int robots = 18;
        public int[] stationX0 = { 2, 9, 16 };            // 작업대 피킹 칸 x (j = -1 줄)
        public int[] stationN = { 4, 4, 4 };              // 피킹 칸 + 대기 3칸
        public double stationDepth = 0.90, presentZ = 0.53; // 작업대에서 올라가는 높이 → 토트 바닥 0.85 m (가정)
        public double orderRate = 650;                    // 토트 요청 / 시간
        public double popularShare = 0.2, popularDemand = 0.7;
        public double vmax = 4.0, acc = 2.0;              // 주행 4 m/s (공식), 가속도 (가정)
        public double turn = 0.3;                         // x↔y 전환 (가정)
        public double climbV = 1.0, climbAcc = 1.0;       // 등반 1 m/s (공식), 가속도 (가정)
        public double attach = 0.6;                       // 팔 걸기/풀기 (가정)
        public double xfer = 2.4;                         // 토트 꺼내기/넣기 (가정)
        public double pickMin = 5, pickMax = 10;          // 피킹 시간 / 토트
        public int stationCap = 6;                        // 작업대 하나에 동시에 배정하는 토트 수
        public int seed = 10;
    }

    public enum ActK { Go, Arm, Climb, Take, Put, Present }

    public class Act
    {
        public ActK k; public int goal = -1, slot = -1, st = -1; public bool on, side, park, done, started;
        public double z, t, z0, d, T;
    }

    public class Seg { public int d, from, rel; public List<int> cells = new List<int>(); public List<double> marks = new List<double>(); public double x0, y0, dist, t, T; }

    public class ToteLoc { public int kind, slot = -1, r = -1; public double u; }   // kind 0 랙, 1 로봇, 2 옮기는 중
    public class Order { public int tote; public double t; }
    public class HTask { public Order order; public int tote, slot, st, phase; }

    public class Station { public int id, x0, n, assigned, tokens, picked, cur = -1; public double busyTime; }

    public class Robot
    {
        public int id, x, j, side = 1, carry = -1, lastDir = -1, pi, goal = -1, state, climbs, deliveries;
        public double px, py, pz, arm, fork, wait, stuck, waitT, busyTime, blockedTime, dist;
        public HTask task; public List<Act> acts = new List<Act>(); public Seg seg; public List<int> path;
    }

    // 작은 이진 힙 (비용, 넣은 순서) — JS 버전과 같은 순서로 꺼냄
    class Heap
    {
        double[] k = new double[1024]; int[] s = new int[1024], v = new int[1024];
        public int n; public double lastKey;
        public void Clear() { n = 0; }
        bool Less(int i, int j) => k[i] < k[j] || (k[i] == k[j] && s[i] < s[j]);
        void Swap(int i, int j)
        {
            double t = k[i]; k[i] = k[j]; k[j] = t;
            int u = s[i]; s[i] = s[j]; s[j] = u;
            u = v[i]; v[i] = v[j]; v[j] = u;
        }
        public void Push(double key, int seq, int val)
        {
            if (n == k.Length) { Array.Resize(ref k, n * 2); Array.Resize(ref s, n * 2); Array.Resize(ref v, n * 2); }
            int i = n++;
            k[i] = key; s[i] = seq; v[i] = val;
            while (i > 0) { int p = (i - 1) >> 1; if (!Less(i, p)) break; Swap(i, p); i = p; }
        }
        public int Pop()
        {
            int val = v[0]; lastKey = k[0];
            n--;
            if (n > 0)
            {
                k[0] = k[n]; s[0] = s[n]; v[0] = v[n];
                int i = 0;
                for (; ; )
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < n && Less(l, m)) m = l;
                    if (r < n && Less(r, m)) m = r;
                    if (m == i) break;
                    Swap(i, m); i = m;
                }
            }
            return val;
        }
    }

    public class HaiPickSim
    {
        public static readonly string[] States = { "idle", "move", "carry", "blocked", "attach", "climb", "xfer", "present", "detach", "turn" };
        static readonly int[] DX = { 1, -1, 0, 0 }, DY = { 0, 0, 1, -1 };

        public readonly HaiPickConfig cfg;
        public double t;
        public int nL, NX, NC, NS, totes, nPopular;
        public double[] laneY, laneW; public double depthY, stationY;
        public byte[] kind; public int[] stAt, occ, parkRes, slotTote, goalCount; public byte[] slotRes, toteBusy;
        public List<int> rackLanes = new List<int>();
        public List<Station> stations = new List<Station>();
        public List<Robot> robots = new List<Robot>();
        public List<ToteLoc> toteLoc = new List<ToteLoc>();
        public List<Order> pending = new List<Order>();
        public double nextOrderT;
        public int statArrived, statDone, statClimbs, statReplans; public double statLeadSum, statLeadMax, statClimbM;
        int rngState;
        readonly Heap heap = new Heap();
        double[] cost; int[] prev; byte[] done;

        public HaiPickSim(HaiPickConfig c)
        {
            cfg = c; rngState = c.seed;
            nL = c.lanes.Length; NX = c.nx; NC = (nL + 1) * c.nx;
            laneY = new double[nL]; laneW = new double[nL];
            double y = 0;
            for (int j = 0; j < nL; j++) { double w = c.lanes[j] == 'A' ? c.aisleW : c.rackD; laneW[j] = w; laneY[j] = y + w / 2; y += w; }
            depthY = y; stationY = -c.stationDepth / 2;
            kind = new byte[NC]; stAt = Fill(NC, -1);
            for (int j = 0; j < nL; j++) for (int x = 0; x < c.nx; x++) kind[Ci(x, j)] = (byte)(c.lanes[j] == 'A' ? 1 : 2);
            for (int si = 0; si < c.stationX0.Length; si++)
            {
                int x0 = c.stationX0[si], n = c.stationN[si];
                for (int k = 0; k < n; k++) { int q = Ci(x0 + k, -1); kind[q] = (byte)(k == 0 ? 4 : 3); stAt[q] = si; }
                stations.Add(new Station { id = si, x0 = x0, n = n });
            }
            occ = Fill(NC, -1); parkRes = Fill(NC, -1);
            for (int j = 0; j < nL; j++) if (c.lanes[j] == 'R') rackLanes.Add(j);
            NS = rackLanes.Count * c.nx * c.levels;
            slotTote = Fill(NS, -1); slotRes = new byte[NS];
            var cells = new List<(int s, double key)>();
            for (int s = 0; s < NS; s++)
            {
                if (Rand() < c.fill)
                {
                    int lv = s % c.levels, rj = rackLanes[s / (c.levels * c.nx)];
                    double key = lv * 0.42 + laneY[rj] * 0.05; key += Gauss() * 1.2;
                    cells.Add((s, key));
                }
            }
            cells.Sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : a.s.CompareTo(b.s));
            for (int id = 0; id < cells.Count; id++) { slotTote[cells[id].s] = id; toteLoc.Add(new ToteLoc { kind = 0, slot = cells[id].s }); }
            totes = cells.Count;
            nPopular = Math.Max(1, (int)Math.Floor(totes * c.popularShare));
            toteBusy = new byte[totes];
            for (int i = 0; i < c.robots; i++)
            {
                int x, j, k;
                do { x = (int)Math.Floor(Rand() * c.nx); j = rackLanes[(int)Math.Floor(Rand() * rackLanes.Count)]; k = Ci(x, j); }
                while (occ[k] != -1);
                var r = new Robot { id = i, x = x, j = j };
                SetPos(r); occ[k] = i; robots.Add(r);
            }
            nextOrderT = Expo(3600 / c.orderRate);
            goalCount = new int[NC];
            cost = new double[NC * 4]; prev = new int[NC * 4]; done = new byte[NC * 4];
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

        // ------------------------------------------------------------ 기본
        public int Ci(int x, int j) => (j + 1) * NX + x;
        public int Cx(int k) => k % NX;
        public int Cj(int k) => k / NX - 1;
        public double WorldX(int x) => (x + 0.5) * cfg.cellX;
        public double WorldY(int j) => j < 0 ? stationY : laneY[j];
        void SetPos(Robot r) { r.px = WorldX(r.x); r.py = WorldY(r.j); }
        public int SlotLane(int s) => rackLanes[s / (cfg.levels * cfg.nx)];
        public int SlotX(int s) => (s / cfg.levels) % cfg.nx;
        public int SlotLv(int s) => s % cfg.levels;
        public double SlotZ(int s) => cfg.rackBase + SlotLv(s) * cfg.levelH;
        public double SlotY(int s) => laneY[SlotLane(s)];
        public int FaceLane(int j) => j > 0 && cfg.lanes[j - 1] == 'A' ? j - 1 : j + 1;
        public int AccessCell(int s) => Ci(SlotX(s), FaceLane(SlotLane(s)));

        // ------------------------------------------------------------ 이동 규칙
        int CanMove(int k, int d)
        {
            int x = Cx(k), j = Cj(k), nx = x + DX[d], nj = j + DY[d];
            if (nx < 0 || nx >= NX || nj < -1 || nj >= nL) return -1;
            int nk = Ci(nx, nj), kd = kind[k], nkd = kind[nk];
            if (nkd == 0) return -1;
            if (d < 2)
            {
                if (kd == 1 && nkd == 1) return nk;
                if ((kd == 3 || kd == 4) && (nkd == 3 || nkd == 4) && d == 1 && stAt[k] == stAt[nk]) return nk;
                return -1;
            }
            if (j >= 0 && nj >= 0) return nk;
            if (j == 0 && nj == -1)
            {
                if (stAt[nk] < 0) return -1;
                var st = stations[stAt[nk]];
                return nx == st.x0 + st.n - 1 ? nk : -1;
            }
            if (j == -1 && nj == 0) return kd == 4 ? nk : -1;
            return -1;
        }
        double StepLen(int k, int d) => d < 2 ? cfg.cellX : Math.Abs(WorldY(Cj(k) + DY[d]) - WorldY(Cj(k)));

        List<int> Plan(Robot r, int goal, int avoid)
        {
            var c = cfg; var H = heap;
            for (int i = 0; i < cost.Length; i++) { cost[i] = double.PositiveInfinity; prev[i] = -1; done[i] = 0; }
            H.Clear();
            int sk = Ci(r.x, r.j), seq = 0;
            for (int d = 0; d < 4; d++) { int v = sk * 4 + d; cost[v] = (r.lastDir == -1 || r.lastDir == d) ? 0 : c.turn; H.Push(cost[v], seq++, v); }
            int best = -1;
            while (H.n > 0)
            {
                int u = H.Pop(); double bu = H.lastKey;
                if (done[u] != 0 || bu > cost[u]) continue;
                done[u] = 1;
                int k = u >> 2, d0 = u & 3;
                if (k == goal) { best = u; break; }
                for (int d = 0; d < 4; d++)
                {
                    int nk = CanMove(k, d);
                    if (nk < 0) continue;
                    int o = occ[nk];
                    if (o != -1 && o != r.id && nk != goal)
                    {
                        if (avoid == 1) continue;
                        var ro = robots[o];
                        if (ro.seg == null && (ro.acts.Count == 0 || ro.acts[0].k != ActK.Go)) continue;
                    }
                    int v = nk * 4 + d; double nc = bu + StepLen(k, d) / c.vmax + 0.03 + (d0 == d ? 0 : c.turn);
                    if (nc < cost[v]) { cost[v] = nc; prev[v] = u; H.Push(nc, seq++, v); }
                }
            }
            if (best < 0) return null;
            var path = new List<int>(); int w = best;
            while (w >= 0 && (w >> 2) != sk) { path.Add(w); w = prev[w]; }
            path.Reverse();
            return path;
        }

        // ------------------------------------------------------------ 로봇 행동
        void StepRobot(Robot r, double dt)
        {
            if (r.task != null || r.acts.Count > 0 || r.seg != null) r.busyTime += dt;
            int guard = 0;
            while (dt > 1e-9 && guard++ < 12)
            {
                if (r.acts.Count == 0)
                {
                    if (r.task != null) NextTask(r);
                    if (r.acts.Count == 0) { r.state = 0; return; }
                }
                var a = r.acts[0];
                double used = 0;
                switch (a.k)
                {
                    case ActK.Go: used = ActGo(r, a, dt); break;
                    case ActK.Arm: used = ActArm(r, a, dt); break;
                    case ActK.Climb: used = ActClimb(r, a, dt); break;
                    case ActK.Take: used = ActXfer(r, a, dt, true); break;
                    case ActK.Put: used = ActXfer(r, a, dt, false); break;
                    case ActK.Present: used = ActPresent(r, a, dt); break;
                }
                if (a.done) r.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }

        double ActGo(Robot r, Act a, double dt)
        {
            var c = cfg;
            if (r.seg == null)
            {
                int here = Ci(r.x, r.j);
                if (here == a.goal) { a.done = true; r.path = null; return 0; }
                if (r.waitT > 0) { r.waitT -= dt; r.stuck += dt; r.blockedTime += dt; r.state = 3; return dt; }
                if (r.path == null || r.goal != a.goal || r.pi >= r.path.Count)
                {
                    r.path = Plan(r, a.goal, r.wait > 1.5 ? 1 : 0);
                    if (r.path == null) r.path = Plan(r, a.goal, 0);
                    r.pi = 0; r.goal = a.goal;
                    if (r.wait > 1.5) { r.wait = 0; statReplans++; }
                    if (r.path == null) { r.wait += dt; r.blockedTime += dt; r.state = 3; r.waitT = 0.3; return dt; }
                }
                int dir = r.path[r.pi] & 3; var cells = new List<int>();
                for (int i = r.pi; i < r.path.Count; i++)
                {
                    int v = r.path[i]; if ((v & 3) != dir) break;
                    int k = v >> 2, o = occ[k];
                    if (o != -1 && o != r.id) break;
                    cells.Add(k);
                }
                if (cells.Count == 0)
                {
                    r.wait += dt; r.blockedTime += dt; r.state = 3;
                    int nk0 = r.path[r.pi] >> 2, oi = occ[nk0];
                    var ob = oi >= 0 ? robots[oi] : null;
                    if (nk0 == a.goal && ob != null && ob.acts.Count > 0 && ob.acts[0].k != ActK.Go && r.stuck < 20) { r.wait = 0; r.stuck += dt; return dt; }
                    r.stuck += dt;
                    if (r.stuck > 2 && !a.side && Sidestep(r, occ[r.path[r.pi] >> 2])) { r.stuck = 0; r.wait = 0; r.path = null; return dt; }
                    if (r.wait > 1.5 && r.waitT <= 0) { r.path = null; r.waitT = 0.1 + Rand() * 0.6; }
                    return dt;
                }
                r.wait = 0; r.stuck = 0;
                foreach (int k in cells) occ[k] = r.id;
                double dist = 0;
                { int k = here; foreach (int nk in cells) { dist += StepLen(k, dir); k = nk; } }
                double turnT = (r.lastDir != -1 && r.lastDir != dir) ? c.turn : 0;
                r.seg = new Seg { d = dir, cells = cells, from = here, x0 = r.px, y0 = r.py, dist = dist, t = -turnT, T = ProfileTime(dist, c.vmax, c.acc) };
                { int k = here; double acc = 0; foreach (int nk in cells) { acc += StepLen(k, dir); r.seg.marks.Add(acc); k = nk; } }
                r.pi += cells.Count;
                r.lastDir = dir;
            }
            var s = r.seg;
            s.t += dt;
            double used = dt;
            if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
            r.state = s.t < 0 ? 9 : (r.carry >= 0 ? 2 : 1);
            double pos = s.t <= 0 ? 0 : ProfilePos(s.t, s.dist, c.vmax, c.acc);
            r.px = s.x0 + DX[s.d] * pos; r.py = s.y0 + DY[s.d] * pos;
            while (s.rel < s.cells.Count - 1 && pos >= s.marks[s.rel])
            {
                int prevK = s.rel == 0 ? s.from : s.cells[s.rel - 1];
                occ[prevK] = -1; s.rel++;
                int k = s.cells[s.rel - 1]; r.x = Cx(k); r.j = Cj(k);
            }
            if (s.t >= s.T)
            {
                while (s.rel < s.cells.Count) { int prevK = s.rel == 0 ? s.from : s.cells[s.rel - 1]; occ[prevK] = -1; s.rel++; }
                int last = s.cells[s.cells.Count - 1];
                r.x = Cx(last); r.j = Cj(last); occ[last] = r.id; SetPos(r);
                r.dist += s.dist; r.seg = null;
                if (last == a.goal) { a.done = true; r.path = null; if (a.side) r.waitT = 0.6 + Rand() * 0.6; }
            }
            return used;
        }

        bool Sidestep(Robot r, int blocker)
        {
            int here = Ci(r.x, r.j); var opts = new List<int>(); var good = new List<int>();
            var bp = blocker >= 0 ? robots[blocker] : null;
            for (int d = 0; d < 4; d++)
            {
                int nk = CanMove(here, d);
                if (nk < 0 || occ[nk] != -1 || kind[nk] > 2) continue;
                opts.Add(nk);
                bool onPath = false;
                if (bp != null && bp.path != null) for (int i = bp.pi; i < bp.path.Count; i++) if ((bp.path[i] >> 2) == nk) { onPath = true; break; }
                if (bp != null && bp.acts.Count > 0 && bp.acts[0].k == ActK.Go && bp.acts[0].goal == nk) onPath = true;
                if (!onPath) good.Add(nk);
            }
            var pick = good.Count > 0 ? good : opts;
            if (pick.Count == 0) return false;
            int kk = pick[(int)Math.Floor(Rand() * pick.Count)];
            r.acts.Insert(0, new Act { k = ActK.Go, goal = kk, side = true });
            return true;
        }

        double ActArm(Robot r, Act a, double dt)
        {
            var c = cfg;
            if (!a.started)
            {
                a.started = true; a.t = 0;
                // 어느 쪽 레일을 잡는지 (렌더링용): 랙 슬롯 쪽, 작업대에서는 작업자 쪽(−y)
                if (a.on) r.side = a.slot >= 0 ? (laneY[SlotLane(a.slot)] > r.py ? 1 : -1) : -1;
            }
            r.state = a.on ? 4 : 8;
            a.t += dt; double used = dt;
            if (a.t >= c.attach) { used = dt - (a.t - c.attach); a.t = c.attach; a.done = true; }
            double u = a.t / c.attach;
            r.arm = a.on ? u : 1 - u;
            return used;
        }

        double ActClimb(Robot r, Act a, double dt)
        {
            var c = cfg;
            if (!a.started)
            {
                a.started = true; a.t = 0; a.z0 = r.pz; a.d = Math.Abs(a.z - r.pz); a.T = ProfileTime(a.d, c.climbV, c.climbAcc);
                if (a.z > r.pz) { statClimbs++; statClimbM += a.d; r.climbs++; }
            }
            r.state = 5;
            a.t += dt; double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
            double p = ProfilePos(a.t, a.d, c.climbV, c.climbAcc);
            r.pz = a.z0 + (a.z >= a.z0 ? p : -p);
            if (a.done) r.pz = a.z;
            return used;
        }

        double ActXfer(Robot r, Act a, double dt, bool take)
        {
            var c = cfg;
            if (!a.started)
            {
                a.started = true; a.t = 0;
                int lane = SlotLane(a.slot);
                r.side = laneY[lane] > r.py ? 1 : -1;
                if (take)
                {
                    int id = slotTote[a.slot]; slotTote[a.slot] = -1; r.carry = id;
                    toteLoc[id] = new ToteLoc { kind = 2, slot = a.slot, r = r.id, u = 0 };
                }
                else toteLoc[r.carry] = new ToteLoc { kind = 2, slot = a.slot, r = r.id, u = 1 };
            }
            r.state = 6;
            a.t += dt; double used = dt;
            if (a.t >= c.xfer) { used = dt - (a.t - c.xfer); a.t = c.xfer; a.done = true; }
            double u = a.t / c.xfer;
            r.fork = take ? (u < 0.4 ? u / 0.4 : Math.Max(0, 1 - (u - 0.4) / 0.6)) : (u < 0.6 ? u / 0.6 : Math.Max(0, 1 - (u - 0.6) / 0.4));
            var L = toteLoc[r.carry];
            L.u = take ? (u < 0.4 ? 0 : Math.Min(1, (u - 0.4) / 0.6)) : (u < 0.6 ? 1 - u / 0.6 : 0);
            if (a.done)
            {
                r.fork = 0;
                if (take) toteLoc[r.carry] = new ToteLoc { kind = 1, slot = -1, r = r.id, u = 1 };
                else
                {
                    int id = r.carry; slotTote[a.slot] = id; slotRes[a.slot] = 0;
                    toteLoc[id] = new ToteLoc { kind = 0, slot = a.slot }; r.carry = -1;
                }
            }
            return used;
        }

        double ActPresent(Robot r, Act a, double dt)
        {
            var c = cfg; var st = stations[a.st];
            if (!a.started)
            {
                a.started = true; a.t = 0; a.T = c.pickMin + Rand() * (c.pickMax - c.pickMin);
                st.cur = r.id;
                double lead = t - r.task.order.t;
                statDone++; statLeadSum += lead; if (lead > statLeadMax) statLeadMax = lead;
            }
            r.state = 7;
            a.t += dt; st.busyTime += dt; double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; st.cur = -1; st.picked++; }
            return used;
        }

        // ------------------------------------------------------------ 작업 순서 (0 꺼내기 → 1 작업대 → 2 되돌려 놓기 → 3 끝)
        void NextTask(Robot r)
        {
            var T = r.task; var c = cfg;
            if (T.phase == 0)
            {
                int s = T.slot;
                r.acts.Add(new Act { k = ActK.Go, goal = AccessCell(s) }); r.acts.Add(new Act { k = ActK.Arm, on = true, slot = s });
                r.acts.Add(new Act { k = ActK.Climb, z = SlotZ(s) - c.deckH }); r.acts.Add(new Act { k = ActK.Take, slot = s });
                r.acts.Add(new Act { k = ActK.Climb, z = 0 }); r.acts.Add(new Act { k = ActK.Arm, on = false });
                T.phase = 1;
            }
            else if (T.phase == 1)
            {
                var st = stations[T.st];
                if (st.tokens >= st.n)
                {
                    if (kind[Ci(r.x, r.j)] != 2) Park(r);
                    r.state = 3; return;
                }
                st.tokens++;
                ReleasePark(r);
                r.acts.Add(new Act { k = ActK.Go, goal = Ci(st.x0, -1) }); r.acts.Add(new Act { k = ActK.Arm, on = true });
                r.acts.Add(new Act { k = ActK.Climb, z = c.presentZ }); r.acts.Add(new Act { k = ActK.Present, st = T.st });
                r.acts.Add(new Act { k = ActK.Climb, z = 0 }); r.acts.Add(new Act { k = ActK.Arm, on = false });
                T.phase = 2;
            }
            else if (T.phase == 2)
            {
                stations[T.st].assigned--; stations[T.st].tokens--;
                int s = ChooseReturn(r);
                slotRes[s] = 1;
                var st = stations[T.st];
                r.acts.Add(new Act { k = ActK.Go, goal = Ci(st.x0, 0) }); r.acts.Add(new Act { k = ActK.Go, goal = AccessCell(s) });
                r.acts.Add(new Act { k = ActK.Arm, on = true, slot = s }); r.acts.Add(new Act { k = ActK.Climb, z = SlotZ(s) - c.deckH });
                r.acts.Add(new Act { k = ActK.Put, slot = s }); r.acts.Add(new Act { k = ActK.Climb, z = 0 }); r.acts.Add(new Act { k = ActK.Arm, on = false });
                T.phase = 3;
            }
            else
            {
                toteBusy[T.tote] = 0;
                r.task = null; r.deliveries++;
                Park(r);
            }
        }

        int ChooseReturn(Robot r)
        {
            var c = cfg; int best = -1; double bs = double.PositiveInfinity;
            Array.Clear(goalCount, 0, goalCount.Length);
            foreach (var o in robots) foreach (var a in o.acts) if (a.k == ActK.Go) { goalCount[a.goal]++; break; }
            bool pop = r.carry >= 0 && r.carry < nPopular;
            for (int s = 0; s < NS; s++)
            {
                if (slotTote[s] != -1 || slotRes[s] != 0) continue;
                int k = AccessCell(s);
                double dx = Math.Abs(WorldX(Cx(k)) - r.px), dy = Math.Abs(WorldY(Cj(k)) - r.py);
                double h = SlotZ(s) - c.deckH;
                double sc = (dx + dy) / c.vmax + h / c.climbV * (pop ? 2 : 0.6) + goalCount[k] * 6 + (occ[k] != -1 ? 3 : 0);
                if (sc < bs) { bs = sc; best = s; }
            }
            return best;
        }

        void Park(Robot r)
        {
            int here = Ci(r.x, r.j);
            if (kind[here] == 2) return;
            int best = -1; double bd = double.PositiveInfinity;
            for (int k = 0; k < NC; k++)
            {
                if (kind[k] != 2 || occ[k] != -1 || parkRes[k] != -1) continue;
                double d = Math.Abs(Cx(k) - r.x) * cfg.cellX + Math.Abs(WorldY(Cj(k)) - r.py);
                if (d < bd) { bd = d; best = k; }
            }
            if (best < 0) return;
            parkRes[best] = r.id;
            r.acts.Add(new Act { k = ActK.Go, goal = best, park = true });
        }

        void ReleasePark(Robot r) { for (int q = 0; q < NC; q++) if (parkRes[q] == r.id) parkRes[q] = -1; }

        // ------------------------------------------------------------ 주문
        void NewOrder()
        {
            var c = cfg;
            for (int tries = 0; tries < 12; tries++)
            {
                int id = Rand() < c.popularDemand ? (int)Math.Floor(Rand() * nPopular) : nPopular + (int)Math.Floor(Rand() * (totes - nPopular));
                if (toteBusy[id] != 0) continue;
                toteBusy[id] = 1;
                pending.Add(new Order { tote = id, t = t });
                statArrived++;
                return;
            }
        }

        void Dispatch()
        {
            var c = cfg;
            while (pending.Count > 0)
            {
                Station st = null;
                foreach (var s in stations) if (s.assigned < c.stationCap && (st == null || s.assigned < st.assigned)) st = s;
                if (st == null) return;
                var o = pending[0]; var L = toteLoc[o.tote];
                int k = AccessCell(L.slot);
                double gx = WorldX(Cx(k)), gy = WorldY(Cj(k));
                Robot best = null; double bd = double.PositiveInfinity;
                foreach (var r in robots)
                {
                    if (r.task != null || r.seg != null) continue;
                    if (r.acts.Count > 0 && !(r.acts.Count == 1 && r.acts[0].park)) continue;
                    double d = Math.Abs(r.px - gx) + Math.Abs(r.py - gy);
                    if (d < bd) { bd = d; best = r; }
                }
                if (best == null) return;
                pending.RemoveAt(0);
                if (best.acts.Count > 0) { int pk = best.acts[0].goal; if (parkRes[pk] == best.id) parkRes[pk] = -1; best.acts.Clear(); best.path = null; }
                ReleasePark(best);
                st.assigned++;
                best.task = new HTask { order = o, tote = o.tote, slot = L.slot, st = st.id, phase = 0 };
            }
        }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600 / cfg.orderRate); }
            Dispatch();
            foreach (var r in robots)
            {
                StepRobot(r, dt);
                if (r.task == null && r.acts.Count == 0 && r.seg == null) { int k = Ci(r.x, r.j); if (parkRes[k] == r.id) parkRes[k] = -1; }
            }
        }

        // 토트 위치 (바닥 밑면 중심) — 렌더링용
        public (double x, double y, double z) SlotPos(int s) => (WorldX(SlotX(s)), SlotY(s), SlotZ(s));
        public (double x, double y, double z) TotePose(int id)
        {
            var L = toteLoc[id];
            if (L.kind == 0) return SlotPos(L.slot);
            var r = robots[L.r]; double tx = r.px, ty = r.py, tz = r.pz + cfg.deckH;
            if (L.kind == 1) return (tx, ty, tz);
            var sp = SlotPos(L.slot); double u = L.u;
            return (tx, sp.y + (ty - sp.y) * u, sp.z + (tz - sp.z) * u);
        }

        // ------------------------------------------------------------ KPI
        public double ThroughputPerHour => statDone / Math.Max(t, 1e-9) * 3600;
        public double AvgLead => statDone > 0 ? statLeadSum / statDone : 0;
        public double RobotUtil { get { double s = 0; foreach (var r in robots) s += r.busyTime; return s / (robots.Count * Math.Max(t, 1e-9)); } }
        public double StationUtil { get { double s = 0; foreach (var x in stations) s += x.busyTime; return s / (stations.Count * Math.Max(t, 1e-9)); } }
        public double AvgClimb => statClimbs > 0 ? statClimbM / statClimbs : 0;
        public int Backlog => pending.Count;
    }
}
