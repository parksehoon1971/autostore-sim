// 다층 셔틀랙 (멀티 레벨 토트 셔틀 AS/RS) 이산시간 시뮬레이션
// (web/shuttle-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 통로의 각 단에 셔틀 1대, 랙 앞 두 칸은 단마다 출고(-y)/입고(+y) 버퍼 컨베이어, 통로 앞에 출고/입고 리프트,
// 1층에 통로마다 U자 컨베이어 (출고 리프트 → 피킹 스테이션 → 입고 리프트). 특정 제조사 제품이 아닌 일반 구성입니다.
// 좌표: x = 통로 방향 (랙 앞면 = 0), y = 깊이, z 위 (Unity 에서는 (x, z, y))
using System;
using System.Collections.Generic;

namespace ShuttleRack
{
    [Serializable]
    public class ShuttleConfig
    {
        public int aisles = 3, nx = 26, levels = 14;            // 통로 수, 통로 길이(칸), 단 수
        public int bufBays = 2;                                 // 앞쪽 버퍼 칸 수
        public double bayW = 0.70, aisleW = 0.72, rackD = 0.46, flue = 0.06;
        public double level0Z = 0.55, levelH = 0.42;            // 맨 아래 단 토트 바닥 높이, 단 간격
        public double toteW = 0.60, toteD = 0.40, toteH = 0.28; // 토트 600 x 400 x 280 mm
        public double liftX = -0.75, xferOff = 0.62;            // 리프트 x, 리프트 ↔ 컨베이어 끝 거리
        public double convZ = 0.75, stationX = -4.6;            // 1층 컨베이어 높이, 스테이션 피킹 위치 x
        public double fill = 0.85;
        public double orderRate = 1800;                         // 토트 요청 / 시간 (전체)
        public double popularShare = 0.2, popularDemand = 0.8;
        public int maxOut = 10;                                 // 통로마다 랙 밖에 나와 있을 수 있는 토트 수
        public double vmax = 4.0, acc = 2.0;                    // 셔틀 주행 4 m/s, 2 m/s² (공개 자료)
        public double armT = 0.9;                               // 텔레스코픽 암 한 번 펴기 또는 접기
        public double liftV = 5.0, liftAcc = 5.0, xferT = 1.0;  // 리프트
        public double convV = 1.0, gap = 0.66;                  // 컨베이어
        public double pickMin = 3, pickMax = 5;                 // 작업자 피킹 시간 / 토트
        public int seed = 3;
    }

    public class ConvItem { public int id; public double s, readyT = -1; }

    // 축적형 컨베이어: 꺾인 선 경로 위의 토트들이 앞 토트와 gap 을 두고 전진
    public class Conv
    {
        public readonly double[][] pts; public readonly double[] cum; public readonly double L, v, gap;
        public readonly List<ConvItem> items = new List<ConvItem>();
        public Conv(double[][] pts, double v, double gap)
        {
            this.pts = pts; this.v = v; this.gap = gap;
            cum = new double[pts.Length];
            for (int i = 1; i < pts.Length; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                cum[i] = cum[i - 1] + Math.Abs(b[0] - a[0]) + Math.Abs(b[1] - a[1]) + Math.Abs(b[2] - a[2]);
            }
            L = cum[pts.Length - 1];
        }
        public double[] Pos(double s)
        {
            int i = 1;
            while (i < pts.Length - 1 && s > cum[i]) i++;
            double seg = cum[i] - cum[i - 1], u = seg > 0 ? Math.Min(1, Math.Max(0, (s - cum[i - 1]) / seg)) : 1;
            var a = pts[i - 1]; var b = pts[i];
            return new[] { a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u };
        }
        public bool CanAccept() => items.Count == 0 || items[items.Count - 1].s >= gap - 1e-9;
        public bool HeadReady() => items.Count > 0 && items[0].s >= L - 1e-6;
        public int Count => items.Count;
        public void Push(int id) => items.Add(new ConvItem { id = id, s = 0, readyT = -1 });
        public int Pop() { int id = items[0].id; items.RemoveAt(0); return id; }
        public void Step(double dt, ShuttleSim sim)
        {
            double lim = L;
            foreach (var it in items)
            {
                it.s = Math.Max(it.s, Math.Min(lim, it.s + v * dt));
                if (it.readyT < 0 && it.s >= L - 1e-6) it.readyT = sim.t;
                lim = it.s - gap;
                sim.totePos[it.id] = Pos(it.s);
            }
        }
    }

    public class Act
    {
        public string k, op;             // k: go | arm | lz | xfer
        public double x, z; public int side, slot = -1, l = -1;
        public bool started, done, swapped;
        public double t, x0, z0, d, T;
        public double[] from, to;
    }

    public class Order { public int id, tote; public double tArrive, tRelease = -1, tPresent = -1, tDone = -1; }

    public class Shuttle
    {
        public int id, a, l, carry = -1, side = 1, task = -1, jobs;
        public double x, arm, busyTime, waitTime, dist;
        public List<Act> acts = new List<Act>(); public List<Order> queue = new List<Order>();
        public string state = "idle";
    }

    public class Lift
    {
        public int id, a, side, carry = -1, trips;
        public double z, busyTime;
        public List<Act> acts = new List<Act>();
        public string state = "idle";
    }

    public class Station { public int id, a, cur = -1, picked; public double t, busyTime; }

    public class ShuttleSim
    {
        public const int ST_SLOT = 0, ST_SHUTTLE = 1, ST_OUTBUF = 2, ST_OUTLIFT = 3, ST_OUTCONV = 4, ST_PICK = 5,
            ST_RETCONV = 6, ST_INLIFT = 7, ST_INBUF = 8, ST_SHUTTLE_IN = 9;

        public readonly ShuttleConfig c;
        public double t;
        public readonly int nb, nSlots;
        public readonly double pitch, depthY, R;
        public readonly Conv[][] outBuf, inBuf;
        public readonly Conv[] outConv, retConv;
        public readonly int[] slotTote; public readonly byte[] slotRes;
        public readonly int totes, nPopular;
        public readonly List<double[]> totePos = new List<double[]>();
        public readonly byte[] toteStage, busy; public readonly int[] toteSlot, toteTarget;
        readonly Order[] toteOrder;
        public readonly List<Shuttle> shuttles = new List<Shuttle>();
        public readonly List<Lift> lifts = new List<Lift>();
        public readonly List<Station> stations = new List<Station>();
        readonly int[][] incoming; readonly int[] inflight;
        public readonly List<Order> orders = new List<Order>();
        readonly List<Order> pending = new List<Order>();
        double nextOrderT;
        public int statDone; public double leadSum, leadMax;
        int rngState;

        public ShuttleSim(ShuttleConfig cfg)
        {
            c = cfg; rngState = c.seed; t = 0;
            int A = c.aisles, Lv = c.levels;
            nb = c.nx - c.bufBays;
            pitch = c.aisleW + 2 * c.rackD + c.flue;
            depthY = A * (c.aisleW + 2 * c.rackD) + (A - 1) * c.flue;
            R = c.aisleW / 2 + c.rackD / 2;
            outBuf = new Conv[A][]; inBuf = new Conv[A][]; outConv = new Conv[A]; retConv = new Conv[A];
            double xr = BufX, xe = c.liftX + c.xferOff, xg = c.liftX - c.xferOff, xs = c.stationX;
            for (int a = 0; a < A; a++)
            {
                double ya = AisleY(a), yo = ya - R, yi = ya + R;
                outBuf[a] = new Conv[Lv]; inBuf[a] = new Conv[Lv];
                for (int l = 0; l < Lv; l++)
                {
                    double z = LevelZ(l);
                    outBuf[a][l] = new Conv(new[] { new[] { xr, yo, z }, new[] { xe, yo, z } }, c.convV, c.gap);
                    inBuf[a][l] = new Conv(new[] { new[] { xe, yi, z }, new[] { xr, yi, z } }, c.convV, c.gap);
                }
                outConv[a] = new Conv(new[] { new[] { xg, yo, c.convZ }, new[] { xs, yo, c.convZ }, new[] { xs, ya, c.convZ } }, c.convV, c.gap);
                retConv[a] = new Conv(new[] { new[] { xs, ya, c.convZ }, new[] { xs, yi, c.convZ }, new[] { xg, yi, c.convZ } }, c.convV, c.gap);
            }
            nSlots = A * nb * 2 * Lv;
            slotTote = new int[nSlots]; for (int i = 0; i < nSlots; i++) slotTote[i] = -1;
            slotRes = new byte[nSlots];
            var cand = new List<(int id, double key)>();
            for (int id = 0; id < nSlots; id++)
                if (Rand() < c.fill) cand.Add((id, SlotCost(id) + Gauss() * 1.5));
            cand.Sort((p, q) => p.key < q.key ? -1 : p.key > q.key ? 1 : p.id.CompareTo(q.id));
            totes = cand.Count;
            toteStage = new byte[totes]; toteSlot = new int[totes]; toteTarget = new int[totes];
            for (int i = 0; i < totes; i++) { toteSlot[i] = -1; toteTarget[i] = -1; }
            for (int id = 0; id < totes; id++) { slotTote[cand[id].id] = id; toteSlot[id] = cand[id].id; totePos.Add(SlotPos(cand[id].id)); }
            nPopular = Math.Max(1, (int)Math.Floor(totes * c.popularShare));
            busy = new byte[totes];
            toteOrder = new Order[totes];
            for (int a = 0; a < A; a++)
                for (int l = 0; l < Lv; l++)
                    shuttles.Add(new Shuttle { id = shuttles.Count, a = a, l = l, x = xr });
            for (int a = 0; a < A; a++)
                foreach (int side in new[] { -1, 1 })
                    lifts.Add(new Lift { id = lifts.Count, a = a, side = side, z = c.convZ });
            incoming = new int[A][]; for (int a = 0; a < A; a++) incoming[a] = new int[Lv];
            inflight = new int[A];
            for (int a = 0; a < A; a++) stations.Add(new Station { id = a, a = a });
            nextOrderT = Expo(3600 / c.orderRate);
        }

        // ------------------------------------------------------------ 기본
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
        static double Smooth(double u) { u = Math.Min(1, Math.Max(0, u)); return u * u * (3 - 2 * u); }

        public double Wx(int b) => (b + 0.5) * c.bayW;
        public double BufX => Wx(c.bufBays - 1);
        public double AisleY(int a) => c.rackD + c.aisleW / 2 + a * pitch;
        public double LevelZ(int l) => c.level0Z + l * c.levelH;
        public int SlotAisle(int s) => s / (nb * 2 * c.levels);
        public int SlotBay(int s) => c.bufBays + (s / (2 * c.levels)) % nb;
        public int SlotSide(int s) => ((s / c.levels) % 2) != 0 ? 1 : -1;
        public int SlotLevel(int s) => s % c.levels;
        public double[] SlotPos(int s) => new[] { Wx(SlotBay(s)), AisleY(SlotAisle(s)) + SlotSide(s) * R, LevelZ(SlotLevel(s)) };
        double SlotCost(int s) =>
            ProfileTime(Math.Abs(LevelZ(SlotLevel(s)) - c.convZ), c.liftV, c.liftAcc) + ProfileTime(Wx(SlotBay(s)) - BufX, c.vmax, c.acc);
        public Shuttle ShuttleOf(int a, int l) => shuttles[a * c.levels + l];
        public double LiftY(Lift lf) => AisleY(lf.a) + lf.side * R;

        // ------------------------------------------------------------ 주문
        void NewOrder()
        {
            for (int k = 0; k < 10; k++)
            {
                bool pop = Rand() < c.popularDemand;
                int id = pop ? (int)Math.Floor(Rand() * nPopular) : nPopular + (int)Math.Floor(Rand() * (totes - nPopular));
                if (busy[id] != 0) continue;
                busy[id] = 1;
                var o = new Order { id = orders.Count, tote = id, tArrive = t };
                orders.Add(o); pending.Add(o); toteOrder[id] = o;
                return;
            }
        }
        void Release()
        {
            int w = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var o = pending[i]; int s = toteSlot[o.tote], a = SlotAisle(s);
                if (inflight[a] < c.maxOut) { inflight[a]++; o.tRelease = t; ShuttleOf(a, SlotLevel(s)).queue.Add(o); }
                else pending[w++] = o;
            }
            pending.RemoveRange(w, pending.Count - w);
        }

        // ------------------------------------------------------------ 셔틀
        void PlanShuttle(Shuttle sh)
        {
            var ib = inBuf[sh.a][sh.l];
            if (ib.HeadReady())
            {
                int s = toteTarget[ib.items[0].id];
                sh.acts.Add(new Act { k = "go", x = BufX }); sh.acts.Add(new Act { k = "arm", side = 1, op = "takeBuf" });
                sh.acts.Add(new Act { k = "go", x = Wx(SlotBay(s)) }); sh.acts.Add(new Act { k = "arm", side = SlotSide(s), op = "putSlot", slot = s });
                sh.jobs++;
            }
            else if (sh.queue.Count > 0)
            {
                var o = sh.queue[0]; sh.queue.RemoveAt(0); int s = toteSlot[o.tote];
                sh.acts.Add(new Act { k = "go", x = Wx(SlotBay(s)) }); sh.acts.Add(new Act { k = "arm", side = SlotSide(s), op = "takeSlot", slot = s });
                sh.acts.Add(new Act { k = "go", x = BufX }); sh.acts.Add(new Act { k = "arm", side = -1, op = "putBuf" });
                sh.task = o.tote; sh.jobs++;
            }
        }
        void StepShuttle(Shuttle sh, double dt)
        {
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (sh.acts.Count == 0) PlanShuttle(sh);
                if (sh.acts.Count == 0) { sh.state = "idle"; return; }
                var a = sh.acts[0];
                double used = a.k == "go" ? ShGo(sh, a, dt) : ShArm(sh, a, dt);
                sh.busyTime += used;
                if (a.done) sh.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }
        void CarryPose(Shuttle sh)
        {
            if (sh.carry >= 0) totePos[sh.carry] = new[] { sh.x, AisleY(sh.a) + sh.side * sh.arm * R, LevelZ(sh.l) };
        }
        double ShGo(Shuttle sh, Act a, double dt)
        {
            if (!a.started) { a.started = true; a.t = 0; a.x0 = sh.x; a.d = Math.Abs(a.x - sh.x); a.T = ProfileTime(a.d, c.vmax, c.acc); sh.dist += a.d; }
            a.t += dt;
            double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
            sh.x = a.x0 + (a.x >= a.x0 ? 1 : -1) * ProfilePos(a.t, a.d, c.vmax, c.acc);
            if (a.done) sh.x = a.x;
            sh.state = sh.carry >= 0 ? "carry" : "move";
            CarryPose(sh);
            return used;
        }
        double ShArm(Shuttle sh, Act a, double dt)
        {
            if (!a.started)
            {
                if (a.op == "putBuf" && !outBuf[sh.a][sh.l].CanAccept()) { sh.state = "wait"; sh.waitTime += dt; return dt; }
                a.started = true; a.t = 0; a.swapped = false; sh.side = a.side;
            }
            a.t += dt;
            double used = dt, T = 2 * c.armT;
            if (a.t >= T) { used = dt - (a.t - T); a.t = T; a.done = true; }
            sh.arm = a.t < c.armT ? Smooth(a.t / c.armT) : 1 - Smooth((a.t - c.armT) / c.armT);
            sh.state = a.op == "takeSlot" || a.op == "takeBuf" ? "get" : "put";
            if (!a.swapped && a.t >= c.armT)
            {
                a.swapped = true; sh.arm = 1;
                if (a.op == "takeSlot")
                {
                    int id = slotTote[a.slot]; slotTote[a.slot] = -1; toteSlot[id] = -1;
                    sh.carry = id; toteStage[id] = ST_SHUTTLE;
                }
                else if (a.op == "takeBuf")
                {
                    int id = inBuf[sh.a][sh.l].Pop(); sh.carry = id; toteStage[id] = ST_SHUTTLE_IN;
                }
                else if (a.op == "putSlot")
                {
                    int id = sh.carry; sh.carry = -1;
                    slotTote[a.slot] = id; slotRes[a.slot] = 0; toteSlot[id] = a.slot; toteTarget[id] = -1;
                    totePos[id] = SlotPos(a.slot); toteStage[id] = ST_SLOT; busy[id] = 0; inflight[sh.a]--;
                }
                else
                {
                    int id = sh.carry; sh.carry = -1;
                    totePos[id] = new[] { sh.x, AisleY(sh.a) + sh.side * R, LevelZ(sh.l) };
                    outBuf[sh.a][sh.l].Push(id); toteStage[id] = ST_OUTBUF; sh.task = -1;
                }
            }
            if (a.done) sh.arm = 0;
            CarryPose(sh);
            return used;
        }

        // ------------------------------------------------------------ 리프트
        void PlanLift(Lift lf)
        {
            int Lv = c.levels;
            if (lf.side < 0)
            {
                int best = -1; double bt = double.PositiveInfinity;
                for (int l = 0; l < Lv; l++)
                {
                    var ob = outBuf[lf.a][l];
                    if (ob.HeadReady() && ob.items[0].readyT < bt) { bt = ob.items[0].readyT; best = l; }
                }
                if (best < 0) return;
                lf.acts.Add(new Act { k = "lz", z = LevelZ(best) }); lf.acts.Add(new Act { k = "xfer", op = "takeBuf", l = best });
                lf.acts.Add(new Act { k = "lz", z = c.convZ }); lf.acts.Add(new Act { k = "xfer", op = "putConv" });
                lf.trips++;
            }
            else
            {
                var rc = retConv[lf.a];
                if (!rc.HeadReady()) return;
                int id = rc.items[0].id, s = ChooseSlot(lf.a, id);
                if (s < 0) return;
                int l = SlotLevel(s);
                slotRes[s] = 1; toteTarget[id] = s; incoming[lf.a][l]++;
                lf.acts.Add(new Act { k = "lz", z = c.convZ }); lf.acts.Add(new Act { k = "xfer", op = "takeRet" });
                lf.acts.Add(new Act { k = "lz", z = LevelZ(l) }); lf.acts.Add(new Act { k = "xfer", op = "putBuf", l = l });
                lf.trips++;
            }
        }
        int ChooseSlot(int a, int tote)
        {
            int per = nb * 2 * c.levels, n = tote < nPopular ? 30 : 8;
            int best = -1, got = 0; double bc = double.PositiveInfinity;
            for (int tries = 0; tries < 4000 && got < n; tries++)
            {
                int s = a * per + (int)Math.Floor(Rand() * per);
                if (slotTote[s] != -1 || slotRes[s] != 0) continue;
                int l = SlotLevel(s);
                if (inBuf[a][l].Count + incoming[a][l] >= 2) continue;
                got++;
                var sh = ShuttleOf(a, l);
                double cst = SlotCost(s) + 4 * (sh.queue.Count + inBuf[a][l].Count + incoming[a][l]) + (sh.acts.Count > 0 ? 3 : 0);
                if (cst < bc) { bc = cst; best = s; }
            }
            return best;
        }
        void StepLift(Lift lf, double dt)
        {
            int guard = 0;
            while (dt > 1e-9 && guard++ < 10)
            {
                if (lf.acts.Count == 0) PlanLift(lf);
                if (lf.acts.Count == 0) { lf.state = "idle"; return; }
                var a = lf.acts[0];
                double used = a.k == "lz" ? LiftMove(lf, a, dt) : LiftXfer(lf, a, dt);
                lf.busyTime += used;
                if (a.done) lf.acts.RemoveAt(0);
                dt -= used;
                if (used == 0 && !a.done) return;
            }
        }
        double LiftMove(Lift lf, Act a, double dt)
        {
            if (!a.started) { a.started = true; a.t = 0; a.z0 = lf.z; a.d = Math.Abs(a.z - lf.z); a.T = ProfileTime(a.d, c.liftV, c.liftAcc); }
            a.t += dt;
            double used = dt;
            if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
            lf.z = a.done ? a.z : a.z0 + (a.z >= a.z0 ? 1 : -1) * ProfilePos(a.t, a.d, c.liftV, c.liftAcc);
            lf.state = "move";
            if (lf.carry >= 0) totePos[lf.carry] = new[] { c.liftX, LiftY(lf), lf.z };
            return used;
        }
        double LiftXfer(Lift lf, Act a, double dt)
        {
            var car = new[] { c.liftX, LiftY(lf), lf.z };
            if (!a.started)
            {
                if (a.op == "putConv" && !outConv[lf.a].CanAccept()) { lf.state = "wait"; return dt; }
                if (a.op == "putBuf" && !inBuf[lf.a][a.l].CanAccept()) { lf.state = "wait"; return dt; }
                a.started = true; a.t = 0;
                if (a.op == "takeBuf")
                {
                    var ob = outBuf[lf.a][a.l]; a.from = ob.Pos(ob.L); a.to = car;
                    lf.carry = ob.Pop(); toteStage[lf.carry] = ST_OUTLIFT;
                }
                else if (a.op == "takeRet")
                {
                    var rc = retConv[lf.a]; a.from = rc.Pos(rc.L); a.to = car;
                    lf.carry = rc.Pop(); toteStage[lf.carry] = ST_INLIFT;
                }
                else if (a.op == "putConv") { a.from = car; a.to = outConv[lf.a].Pos(0); }
                else { a.from = car; a.to = inBuf[lf.a][a.l].Pos(0); }
            }
            a.t += dt;
            double used = dt;
            if (a.t >= c.xferT) { used = dt - (a.t - c.xferT); a.t = c.xferT; a.done = true; }
            double u = Smooth(a.t / c.xferT); int id = lf.carry;
            totePos[id] = new[] { a.from[0] + (a.to[0] - a.from[0]) * u, a.from[1] + (a.to[1] - a.from[1]) * u, a.from[2] + (a.to[2] - a.from[2]) * u };
            lf.state = "xfer";
            if (a.done)
            {
                if (a.op == "putConv") { outConv[lf.a].Push(id); toteStage[id] = ST_OUTCONV; lf.carry = -1; }
                else if (a.op == "putBuf") { inBuf[lf.a][a.l].Push(id); toteStage[id] = ST_INBUF; incoming[lf.a][a.l]--; lf.carry = -1; }
            }
            return used;
        }

        // ------------------------------------------------------------ 스테이션
        void StepStation(Station st, double dt)
        {
            var oc = outConv[st.a]; var rc = retConv[st.a];
            if (st.cur < 0 && oc.HeadReady())
            {
                st.cur = oc.items[0].id; st.t = c.pickMin + Rand() * (c.pickMax - c.pickMin);
                toteStage[st.cur] = ST_PICK;
                var o = toteOrder[st.cur]; if (o != null) o.tPresent = t;
            }
            if (st.cur < 0) return;
            if (st.t > 0) { st.busyTime += Math.Min(dt, st.t); st.t -= dt; }
            if (st.t <= 0 && rc.CanAccept())
            {
                int id = oc.Pop(); rc.Push(id); toteStage[id] = ST_RETCONV;
                st.cur = -1; st.picked++;
                var o = toteOrder[id];
                if (o != null)
                {
                    o.tDone = t; toteOrder[id] = null;
                    double lead = o.tDone - o.tArrive;
                    statDone++; leadSum += lead; leadMax = Math.Max(leadMax, lead);
                }
            }
        }

        public void Step(double dt)
        {
            t += dt;
            while (t >= nextOrderT) { NewOrder(); nextOrderT += Expo(3600 / c.orderRate); }
            Release();
            int A = c.aisles, Lv = c.levels;
            for (int a = 0; a < A; a++)
            {
                for (int l = 0; l < Lv; l++) { outBuf[a][l].Step(dt, this); inBuf[a][l].Step(dt, this); }
                outConv[a].Step(dt, this); retConv[a].Step(dt, this);
            }
            foreach (var sh in shuttles) StepShuttle(sh, dt);
            foreach (var lf in lifts) StepLift(lf, dt);
            foreach (var st in stations) StepStation(st, dt);
        }

        public double[] TotePose(int id) => totePos[id];

        public double ThroughputPerHour => t > 0 ? statDone / t * 3600 : 0;
        public double AvgLead => statDone > 0 ? leadSum / statDone : 0;
        public int Backlog => pending.Count;
        public double ShuttleUtil { get { double s = 0; foreach (var x in shuttles) s += x.busyTime; return t > 0 ? s / (shuttles.Count * t) : 0; } }
        public double LiftUtil { get { double s = 0; foreach (var x in lifts) s += x.busyTime; return t > 0 ? s / (lifts.Count * t) : 0; } }
        public double StationUtil { get { double s = 0; foreach (var x in stations) s += x.busyTime; return t > 0 ? s / (stations.Count * t) : 0; } }
    }
}
