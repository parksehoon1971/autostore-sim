// Libiao(立镖) 방식 3D 소터 이산시간 시뮬레이션 (web/libiao-sim-core.js 와 같은 로직의 C# 포팅. 한쪽을 고치면 다른 쪽도 고치세요)
// 좌표: 셀 (i, j) 중심 = ((i+0.5)*cell, (j+0.5)*cell), z 위. Unity 에서는 (x, z, y) 로 바꿔 씁니다.
using System;
using System.Collections.Generic;

namespace Libiao
{
    [Serializable]
    public class SorterConfig
    {
        public int nx = 19, ny = 13;          // 데크 셀 수
        public float cell = 0.70f;            // 셀 피치 m (근사)
        public int aisleStep = 3;             // 통로 간격 (3 → 슈트 2x2 블록)
        public float deckZ = 2.40f;           // 데크 상면 높이 m (근사)
        public int[] stations = { 2, 8, 14 };       // 앞쪽 투입 스테이션 (s, s+1)
        public int[] stationsBack = { 16, 10, 4 };  // 뒤쪽 투입 스테이션 (s, s-1)
        public int robots = 30;
        public float vmax = 3.0f, acc = 1.5f; // m/s, m/s² (근사)
        public float rotSpeed = (float)Math.PI; // 제자리 회전 rad/s
        public float loadTime = 2.0f;         // 작업자 적재 + 스캔
        public float feedTime = 1.2f;         // 다음 소포가 적재대로 오는 시간
        public float dropTime = 0.9f;         // 트레이 기울임-복귀
        public float tiltMax = 0.62f;         // 트레이 최대 기울기 rad
        public int bagCap = 28;               // 자루 용량
        public float bagBottom = 0.55f;       // 자루 바닥 높이
        public float deadlock = 5.0f;         // 우회 경로 탐색까지 대기 s
        public int seed = 3;
    }

    public struct Pose { public double x, y, z, rx, ry, rz; }

    public class Station { public int id, x, side, j, load, parcel = -1, served; public double readyAt; }

    public class Chute { public int id, i, j, count, swaps, received; public List<int> drops = new List<int>(); public List<int> parcels = new List<int>(); }

    public enum PState { New, Feed, Pick, Robot, Fall, Bag, Gone }

    public class Parcel
    {
        public int id, type, chute = -1, robot = -1, station = -1;
        public PState state;
        public double yaw, tint, t0, t1, slide, born;
        public double x, y, z, rx, ry, rz;
        public Pose from, to;
        public bool alive = true;
    }

    public enum RMode { Stop, Rot, Move, Act }
    public enum RTask { ToStation, ToChute }

    public class SorterRobot
    {
        public int id, cell, target = -1, parcel = -1, delivered;
        public double x, y, h, tilt, tiltSign, wait, travel;
        public RMode mode = RMode.Stop;
        public RTask task = RTask.ToStation;
        public List<int> path = new List<int>();
        public List<int> held = new List<int>();
        // 동작 파라미터
        public string mKind; public double mT0, mT1, mH0, mH1, mD, mT, mX0, mY0; public int mDx, mDy, mReleased, mSt;
    }

    public class LibiaoSim
    {
        // 소포 종류: 가로, 세로, 높이 (m)
        public static readonly double[,] ParcelSize = { { 0.30, 0.22, 0.14 }, { 0.38, 0.28, 0.20 }, { 0.24, 0.18, 0.10 }, { 0.34, 0.26, 0.06 }, { 0.30, 0.24, 0.05 } };
        public static readonly string[] ParcelName = { "box_s", "box_m", "box_xs", "bag_grey", "bag_white" };
        static readonly double[] ParcelWeight = { 0.28, 0.18, 0.16, 0.22, 0.16 };

        public readonly SorterConfig c;
        public double t;
        public readonly int j0 = -2, W, H;
        public readonly int[] occ;
        readonly Dictionary<int, int> rowDir = new Dictionary<int, int>(), colDir = new Dictionary<int, int>();
        readonly Dictionary<int, List<int>> extra = new Dictionary<int, List<int>>();
        public readonly List<Station> stations = new List<Station>();
        public readonly List<Chute> chutes = new List<Chute>();
        public readonly List<Parcel> parcels = new List<Parcel>();
        public List<int> alive = new List<int>();
        public readonly List<SorterRobot> robots = new List<SorterRobot>();
        public int version, delivered, inducted;
        public double blockedT;
        readonly Queue<double> deliveredT = new Queue<double>();
        int rngState;

        // mulberry32 (JS 와 같은 난수열)
        double Rand()
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
            double dA = 0.5 * a * ta * ta;
            if (t < T - ta) return dA + vp * (t - ta);
            double td = T - t;
            return d - 0.5 * a * td * td;
        }
        static double WrapPi(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }
        static double Smooth(double u) { u = Math.Min(1, Math.Max(0, u)); return u * u * (3 - 2 * u); }

        public LibiaoSim(SorterConfig cfg)
        {
            c = cfg;
            rngState = c.seed;
            W = c.nx; H = c.ny + 2 - j0;
            occ = new int[W * H];
            for (int k = 0; k < occ.Length; k++) occ[k] = -1;
            // 통로 방향
            int nAx = (c.nx - 1) / c.aisleStep, nAy = (c.ny - 1) / c.aisleStep;
            for (int k = 0; k <= nAy; k++) rowDir[k * c.aisleStep] = k == 0 ? 1 : k == nAy ? -1 : (k % 2 == 1 ? -1 : 1);
            for (int k = 0; k <= nAx; k++) colDir[k * c.aisleStep] = k == 0 ? -1 : k == nAx ? 1 : (k % 2 == 1 ? 1 : -1);
            // 스테이션 분기
            foreach (int s in c.stations)
            {
                Add(s, 0, s, -1); Add(s, -1, s, -2); Add(s, -2, s + 1, -2); Add(s + 1, -2, s + 1, -1); Add(s + 1, -1, s + 1, 0);
                stations.Add(new Station { id = stations.Count, x = s, side = -1, j = -2, load = Key(s, -2) });
            }
            int e = c.ny - 1;
            foreach (int s in c.stationsBack)
            {
                Add(s, e, s, e + 1); Add(s, e + 1, s, e + 2); Add(s, e + 2, s - 1, e + 2); Add(s - 1, e + 2, s - 1, e + 1); Add(s - 1, e + 1, s - 1, e);
                stations.Add(new Station { id = stations.Count, x = s, side = 1, j = e + 2, load = Key(s, e + 2) });
            }
            // 슈트
            int[,] d4 = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };
            for (int j = 0; j < c.ny; j++)
                for (int i = 0; i < c.nx; i++)
                {
                    if (IsAisle(i, j)) continue;
                    var ch = new Chute { id = chutes.Count, i = i, j = j };
                    for (int q = 0; q < 4; q++)
                    {
                        int a = i + d4[q, 0], b = j + d4[q, 1];
                        if (InDeck(a, b) && IsAisle(a, b) && !(rowDir.ContainsKey(b) && colDir.ContainsKey(a))) ch.drops.Add(Key(a, b));
                    }
                    chutes.Add(ch);
                }
            // 초기 자루 채움
            foreach (var ch in chutes)
            {
                int n = (int)Math.Floor(Rand() * c.bagCap * 0.7);
                for (int k = 0; k < n; k++) ToBag(NewParcel(), ch, true);
            }
            foreach (var st in stations) FeedStation(st, true);
            // 로봇
            var free = new List<int[]>();
            for (int j = 0; j < c.ny; j++) for (int i = 0; i < c.nx; i++) if (IsAisle(i, j)) free.Add(new[] { i, j });
            for (int r = 0; r < c.robots; r++)
            {
                int pick = (int)Math.Floor(Rand() * free.Count);
                var p = free[pick]; free.RemoveAt(pick);
                int k = Key(p[0], p[1]);
                var n0 = Unkey(Next(k)[0]);
                var rb = new SorterRobot { id = r, cell = k, x = Cx(p[0]), y = Cy(p[1]), h = Math.Atan2(n0[1] - p[1], n0[0] - p[0]) };
                rb.held.Add(k);
                occ[k] = r;
                robots.Add(rb);
            }
            foreach (var rb in robots) PlanToStation(rb);
        }

        void Add(int a0, int a1, int b0, int b1)
        {
            int k = Key(a0, a1);
            if (!extra.TryGetValue(k, out var l)) { l = new List<int>(); extra[k] = l; }
            l.Add(Key(b0, b1));
        }

        // ------------------------------------------------ 격자
        public int Key(int i, int j) => (j - j0) * W + i;
        public int[] Unkey(int k) => new[] { k % W, k / W + j0 };
        public double Cx(int i) => (i + 0.5) * c.cell;
        public double Cy(int j) => (j + 0.5) * c.cell;
        public bool InDeck(int i, int j) => i >= 0 && j >= 0 && i < c.nx && j < c.ny;
        public bool IsAisle(int i, int j) => InDeck(i, j) && (rowDir.ContainsKey(j) || colDir.ContainsKey(i));
        public bool IsStationCell(int i, int j) => (j < 0 || j >= c.ny) && j >= j0 && j < j0 + H && i >= 0 && i < W && extra.ContainsKey(Key(i, j));
        public bool IsRow(int j) => rowDir.ContainsKey(j);
        public bool IsCol(int i) => colDir.ContainsKey(i);

        List<int> Next(int k)
        {
            var ij = Unkey(k); int i = ij[0], j = ij[1];
            var o = new List<int>(3);
            if (InDeck(i, j))
            {
                if (rowDir.TryGetValue(j, out int dr)) { int a = i + dr; if (InDeck(a, j)) o.Add(Key(a, j)); }
                if (colDir.TryGetValue(i, out int dc)) { int b = j + dc; if (InDeck(i, b)) o.Add(Key(i, b)); }
            }
            if (extra.TryGetValue(k, out var ex)) o.AddRange(ex);
            return o;
        }

        // BFS 최단 경로 (start 제외, goal 포함)
        List<int> Route(int start, List<int> goals, int blocked)
        {
            int N = W * H;
            var prev = new int[N];
            for (int k = 0; k < N; k++) prev[k] = -2;
            prev[start] = -1;
            var q = new List<int> { start }; int head = 0, hit = -1;
            var goalSet = new HashSet<int>(goals);
            while (head < q.Count)
            {
                int u = q[head++];
                if (u != start && goalSet.Contains(u)) { hit = u; break; }
                foreach (int v in Next(u))
                {
                    if (prev[v] != -2 || v == blocked) continue;
                    prev[v] = u; q.Add(v);
                }
            }
            if (hit < 0) return null;
            var path = new List<int>();
            for (int v = hit; v != start; v = prev[v]) path.Add(v);
            path.Reverse();
            return path;
        }

        // ------------------------------------------------ 소포
        Parcel NewParcel()
        {
            double u = Rand(); int type = 0;
            for (int k = 0; k < ParcelWeight.Length; k++) { if (u < ParcelWeight[k]) { type = k; break; } u -= ParcelWeight[k]; type = k; }
            var p = new Parcel { id = parcels.Count, type = type, state = PState.New, yaw = (Rand() - 0.5) * 0.5, tint = Rand(), born = t };
            parcels.Add(p); alive.Add(p.id);
            return p;
        }
        public double SizeX(Parcel p) => ParcelSize[p.type, 0];
        public double SizeY(Parcel p) => ParcelSize[p.type, 1];
        public double SizeZ(Parcel p) => ParcelSize[p.type, 2];

        Pose BagSlotPose(Chute ch, int n, Parcel p)
        {
            int k = n % 4, layer = n / 4;
            double ox = (k % 2 != 0 ? 1 : -1) * 0.07 + Math.Sin(n * 7.3) * 0.02;
            double oy = (k < 2 ? 1 : -1) * 0.06 + Math.Cos(n * 5.1) * 0.02;
            return new Pose
            {
                x = Cx(ch.i) + ox, y = Cy(ch.j) + oy, z = c.bagBottom + layer * 0.11 + SizeZ(p) / 2,
                rx = Math.Sin(n * 3.7) * 0.12, ry = Math.Cos(n * 2.9) * 0.12, rz = (n % 2) * Math.PI / 2 + Math.Sin(n * 1.31) * 0.15,
            };
        }
        Pose ToBag(Parcel p, Chute ch, bool instant)
        {
            var pose = BagSlotPose(ch, ch.count, p);
            ch.count++; ch.parcels.Add(p.id); p.chute = ch.id;
            if (instant) { p.state = PState.Bag; SetPose(p, pose); }
            return pose;
        }
        static void SetPose(Parcel p, Pose q) { p.x = q.x; p.y = q.y; p.z = q.z; p.rx = q.rx; p.ry = q.ry; p.rz = q.rz; }
        void SwapBag(Chute ch)
        {
            foreach (int id in ch.parcels) { var p = parcels[id]; p.state = PState.Gone; p.alive = false; }
            alive = alive.FindAll(id => parcels[id].alive);
            ch.parcels.Clear(); ch.count = 0; ch.swaps++; version++;
        }
        void FeedStation(Station st, bool instant)
        {
            var p = NewParcel();
            p.state = PState.Feed; p.t0 = t; p.station = st.id;
            st.parcel = p.id; st.readyAt = instant ? t : t + c.feedTime;
            version++;
        }
        // 작업자 적재대 위 소포 위치
        public Pose TablePose(Station st) => new Pose { x = Cx(st.x), y = Cy(st.j + st.side) - st.side * 0.05, z = c.deckZ + 0.20 };

        // ------------------------------------------------ 계획
        void PlanToStation(SorterRobot rb)
        {
            Station best = null; List<int> bestPath = null; double bestCost = 1e9;
            foreach (var st in stations)
            {
                var path = st.load == rb.cell ? new List<int>() : Route(rb.cell, new List<int> { st.load }, -1);
                if (path == null) continue;
                int queued = 0;
                foreach (var o in robots) if (o != rb && o.task == RTask.ToStation && o.target == st.id) queued++;
                double cost = path.Count + queued * 4;
                if (cost < bestCost) { bestCost = cost; best = st; bestPath = path; }
            }
            rb.task = RTask.ToStation; rb.target = best.id; rb.path = bestPath; rb.wait = 0;
        }
        void PlanToChute(SorterRobot rb)
        {
            Chute ch = null;
            for (int tries = 0; tries < 20; tries++)
            {
                ch = chutes[(int)Math.Floor(Rand() * chutes.Count)];
                if (ch.count < c.bagCap) break;
            }
            List<int> best = null;
            foreach (int d in ch.drops)
            {
                var path = Route(rb.cell, new List<int> { d }, -1);
                if (path != null && (best == null || path.Count < best.Count)) best = path;
            }
            rb.task = RTask.ToChute; rb.target = ch.id; rb.path = best ?? new List<int>(); rb.wait = 0;
        }

        // ------------------------------------------------ 진행
        public void Step(double dt)
        {
            t += dt;
            foreach (var rb in robots) UpdateRobot(rb, dt);
            var snapshot = alive.ToArray();
            foreach (int id in snapshot)
            {
                var p = parcels[id];
                if (p.state == PState.Fall && t >= p.t1)
                {
                    p.state = PState.Bag; SetPose(p, p.to);
                    delivered++; deliveredT.Enqueue(t);
                    var ch = chutes[p.chute]; ch.received++;
                    if (ch.count >= c.bagCap && ch.parcels.TrueForAll(q => parcels[q].state == PState.Bag)) SwapBag(ch);
                }
            }
            while (deliveredT.Count > 0 && deliveredT.Peek() < t - 600) deliveredT.Dequeue();
        }

        void UpdateRobot(SorterRobot rb, double dt)
        {
            if (rb.mode == RMode.Act)
            {
                if (rb.mKind == "drop")
                {
                    double u = (t - rb.mT0) / c.dropTime;
                    rb.tilt = rb.tiltSign * c.tiltMax * (u < 0.35 ? Smooth(u / 0.35) : u < 0.6 ? 1 : 1 - Smooth((u - 0.6) / 0.4));
                    if (rb.parcel >= 0 && u >= 0.3) ReleaseParcel(rb);
                }
                if (t >= rb.mT1)
                {
                    rb.mode = RMode.Stop; rb.tilt = 0;
                    if (rb.mKind == "load")
                    {
                        var st = stations[rb.mSt];
                        var p = parcels[st.parcel];
                        p.state = PState.Robot; p.robot = rb.id; rb.parcel = p.id; st.served++; inducted++;
                        FeedStation(st, false);
                        PlanToChute(rb);
                    }
                    else
                    {
                        if (rb.parcel >= 0) ReleaseParcel(rb);
                        rb.delivered++;
                        PlanToStation(rb);
                    }
                }
                return;
            }
            if (rb.mode == RMode.Rot)
            {
                double u = Math.Min(1, (t - rb.mT0) / (rb.mT1 - rb.mT0));
                rb.h = rb.mH0 + (rb.mH1 - rb.mH0) * Smooth(u);
                if (t >= rb.mT1) { rb.h = WrapPi(rb.mH1); rb.mode = RMode.Stop; }
                return;
            }
            if (rb.mode == RMode.Move)
            {
                double s = ProfilePos(t - rb.mT0, rb.mD, c.vmax, c.acc);
                rb.x = rb.mX0 + rb.mDx * s; rb.y = rb.mY0 + rb.mDy * s;
                int passed = (int)Math.Floor(s / c.cell + 1e-6);
                while (rb.mReleased < passed)
                {
                    int k = rb.held[0];
                    if (occ[k] == rb.id) occ[k] = -1;
                    rb.held.RemoveAt(0); rb.mReleased++;
                    rb.cell = rb.held[0]; rb.path.RemoveAt(0); rb.travel += c.cell;
                }
                if (t >= rb.mT0 + rb.mT)
                {
                    var ij = Unkey(rb.cell);
                    rb.x = Cx(ij[0]); rb.y = Cy(ij[1]); rb.mode = RMode.Stop;
                }
                return;
            }
            // ---- 정지 상태
            if (rb.path == null || rb.path.Count == 0)
            {
                if (rb.task == RTask.ToStation)
                {
                    var st = stations[rb.target];
                    if (rb.cell == st.load && t >= st.readyAt)
                    {
                        rb.mode = RMode.Act; rb.mKind = "load"; rb.mT0 = t; rb.mT1 = t + c.loadTime; rb.mSt = st.id;
                        var p = parcels[st.parcel]; p.state = PState.Pick; p.t0 = t; p.robot = rb.id;
                    }
                    else if (rb.cell != st.load) PlanToStation(rb);
                }
                else
                {
                    var ch = chutes[rb.target];
                    var ij = Unkey(rb.cell);
                    double vx = Cx(ch.i) - Cx(ij[0]), vy = Cy(ch.j) - Cy(ij[1]);
                    double cross = Math.Cos(rb.h) * vy - Math.Sin(rb.h) * vx;
                    rb.tiltSign = cross > 0 ? 1 : -1;
                    rb.mode = RMode.Act; rb.mKind = "drop"; rb.mT0 = t; rb.mT1 = t + c.dropTime;
                }
                return;
            }
            var cur = Unkey(rb.cell); var nx = Unkey(rb.path[0]);
            int dx = Math.Sign(nx[0] - cur[0]), dy = Math.Sign(nx[1] - cur[1]);
            double want = Math.Atan2(dy, dx);
            double dh = WrapPi(want - rb.h);
            if (Math.Abs(dh) > 0.01)
            {
                rb.mode = RMode.Rot; rb.mT0 = t; rb.mT1 = t + Math.Abs(dh) / c.rotSpeed; rb.mH0 = rb.h; rb.mH1 = rb.h + dh;
                return;
            }
            int n = 0, pi = cur[0], pj = cur[1];
            foreach (int k in rb.path)
            {
                var ab = Unkey(k);
                if (ab[0] - pi != dx || ab[1] - pj != dy) break;
                if (occ[k] != -1 && occ[k] != rb.id) break;
                occ[k] = rb.id; rb.held.Add(k); n++; pi = ab[0]; pj = ab[1];
            }
            if (n == 0)
            {
                rb.wait += dt; blockedT += dt;
                if (rb.wait > c.deadlock)
                {
                    int goal = rb.path[rb.path.Count - 1];
                    var alt = Route(rb.cell, new List<int> { goal }, rb.path[0]);
                    if (alt != null) rb.path = alt;
                    rb.wait = 0;
                }
                return;
            }
            rb.wait = 0;
            double d = n * c.cell;
            rb.mode = RMode.Move;
            rb.mT0 = t; rb.mD = d; rb.mT = ProfileTime(d, c.vmax, c.acc); rb.mX0 = rb.x; rb.mY0 = rb.y; rb.mDx = dx; rb.mDy = dy; rb.mReleased = 0;
        }

        void ReleaseParcel(SorterRobot rb)
        {
            var p = parcels[rb.parcel];
            var ch = chutes[rb.target];
            p.from = ParcelPose(p.id);
            p.to = ToBag(p, ch, false);
            p.state = PState.Fall; p.t0 = t;
            double h = Math.Max(0.2, c.deckZ - p.to.z);
            p.slide = 0.22; p.t1 = t + p.slide + Math.Sqrt(2 * h / 9.81);
            rb.parcel = -1; version++;
        }

        // ------------------------------------------------ 자세 (렌더링용, 시뮬레이션 좌표 + 오일러 XYZ)
        public double TrayZ => c.deckZ + 0.30;
        public Pose ParcelPose(int id)
        {
            var p = parcels[id];
            double sz = SizeZ(p);
            switch (p.state)
            {
                case PState.Bag:
                    return new Pose { x = p.x, y = p.y, z = p.z, rx = p.rx, ry = p.ry, rz = p.rz };
                case PState.Feed:
                {
                    var tp = TablePose(stations[p.station]);
                    double u = Smooth((t - p.t0) / c.feedTime);
                    return new Pose { x = tp.x, y = tp.y + stations[p.station].side * 1.6 * (1 - u), z = tp.z + sz / 2, rz = Math.PI / 2 + p.yaw };
                }
                case PState.Pick:
                {
                    var tp = TablePose(stations[p.station]);
                    var rb = robots[p.robot];
                    double u = Smooth(((t - p.t0) / c.loadTime - 0.4) / 0.45);
                    double tz = TrayZ + sz / 2;
                    return new Pose
                    {
                        x = tp.x + (rb.x - tp.x) * u, y = tp.y + (rb.y - tp.y) * u,
                        z = tp.z + sz / 2 + (tz - tp.z - sz / 2) * u + Math.Sin(u * Math.PI) * 0.25,
                        rz = Math.PI / 2 + p.yaw + (rb.h - Math.PI / 2) * u,
                    };
                }
                case PState.Robot:
                {
                    var rb = robots[p.robot];
                    double lat = rb.tilt * 0.12;
                    double nx = -Math.Sin(rb.h), ny = Math.Cos(rb.h);
                    return new Pose { x = rb.x + nx * lat, y = rb.y + ny * lat, z = TrayZ + sz / 2 - Math.Abs(rb.tilt) * 0.05, rx = -rb.tilt, ry = 0, rz = rb.h + p.yaw };
                }
                case PState.Fall:
                {
                    var a = p.from; var b = p.to; double tt = t - p.t0;
                    var ch = chutes[p.chute];
                    double mx = Cx(ch.i), my = Cy(ch.j);
                    if (tt < p.slide)
                    {
                        double u = tt / p.slide;
                        return new Pose { x = a.x + (mx - a.x) * u * u, y = a.y + (my - a.y) * u * u, z = a.z - 0.25 * u * u, rx = a.rx * (1 + u), ry = a.ry * (1 + u), rz = a.rz };
                    }
                    double tf = tt - p.slide, T = p.t1 - p.t0 - p.slide;
                    double uu = Math.Min(1, tf / T);
                    double z0 = a.z - 0.25;
                    double z = Math.Max(b.z, z0 - 0.5 * 9.81 * tf * tf - 0.8 * tf);
                    return new Pose
                    {
                        x = mx + (b.x - mx) * uu, y = my + (b.y - my) * uu, z = z,
                        rx = a.rx * 2 + (b.rx - a.rx * 2) * uu, ry = a.ry * 2 + (b.ry - a.ry * 2) * uu, rz = a.rz + (b.rz - a.rz) * uu,
                    };
                }
            }
            return new Pose { z = -10 };
        }

        // ------------------------------------------------ 통계
        public double ThroughputPerHour { get { double win = Math.Min(600, t); return win > 0 ? deliveredT.Count * 3600 / win : 0; } }
        public double BlockedShare => t > 0 ? blockedT / (t * robots.Count) : 0;
    }
}
