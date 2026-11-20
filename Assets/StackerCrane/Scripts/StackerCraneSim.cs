// 팔레트용 스태커크레인 (단위 하중 AS/RS) 이산시간 시뮬레이션
// (web/stackercrane-sim-core.js 와 같은 로직의 C# 포팅. 같은 mulberry32 난수. 한쪽을 고치면 다른 쪽도 고치세요)
// 통로마다 크레인 1대가 바닥 레일과 랙 상부 가이드 레일 사이를 주행하며, 통로 앞 끝의 입고 컨베이어(쪽 0, -y)에서
// 팔레트를 받아 랙에 넣고, 출고 요청 팔레트를 꺼내 출고 컨베이어(쪽 1, +y)에 내려놓습니다. 특정 제조사 제품이 아닌 일반형.
// 좌표: x 통로 방향 (랙 앞 끝 = 0), y 통로를 가로지름, z 위 (Unity 에서는 (x, z, y)). 크레인 z = 포크 윗면 높이.
using System;
using System.Collections.Generic;

namespace StackerCrane
{
    [Serializable]
    public class StackerCraneConfig
    {
        public int aisles = 2, nx = 22, levels = 10;           // 통로 수, 통로 한쪽 칸 수, 단 수
        public double slotW = 1.25; public int bay = 2;        // 칸 폭, 기둥 사이 칸 수
        public double rackD = 1.10, aisleW = 1.50, flue = 0.30; // 랙 깊이, 통로 폭, 등지는 랙 사이 틈
        public double level0Z = 0.90, levelH = 1.75;           // 맨 아래 단 빔 윗면 (= 컨베이어 높이), 단 간격
        public double palletL = 1.20, palletW = 1.00, palletH = 0.144;
        public double loadMin = 0.95, loadMax = 1.40;          // 적재물 높이
        public double fill = 0.70;
        public double pdX = -1.80;                             // P&D (입출고 컨베이어 끝 칸) x
        public int convN = 6; public double convL = 1.25;      // 컨베이어 칸 수, 칸 길이
        public double convV = 0.30, convA = 0.30;              // 체인 컨베이어 (가정)
        public double vx = 4.0, ax = 1.0;                      // 주행 240 m/min, 1.0 m/s²
        public double vz = 1.2, az = 1.0;                      // 승강 72 m/min, 1.0 m/s²
        public double vf = 1.0, af = 1.2;                      // 텔레스코픽 포크 (가정)
        public double lift = 0.12, vl = 0.15, al = 0.5;        // 포크 들기/내리기 (가정)
        public double settle = 0.6;                            // 위치 보정 · 마스트 흔들림 정착 (가정)
        public double forkGap = 0.03;
        public double inRate = 45, outRate = 45;               // 통로당 입고 / 출고 요청 (팔레트/시간)
        public int pool = 24;                                  // 창고 밖 대기 팔레트
        public int seed = 6;
    }

    public class Pallet { public int id, kind, refi = -1, variant; public double x, y, z = -5, load, reqT = -1; }

    public class Lane { public int aisle; public bool outb; public int[] seg, res; public int queue; }

    public class ConvMove { public int lane, p, from, to; public double x0, x1, t, T; }

    public class CraneAct
    {
        public string k;                 // move | fork | wait
        public double x, z;
        public bool get, released, started;
        public int side, lane = -1, slot = -1, p = -1;
        public double z0, t, T, Tx, Tz, Tf, Tl, zs, x0, zz0;
    }

    public class SimEvent { public double t, x, z; public int crane, level; public string kind, where; }

    public class Crane
    {
        public int id, aisle, carry = -1, requests, cycles, dual, stores, retrieves;
        public double x, z, fork, busyTime, travelDist, hoistDist, support = -1;
        public string state = "idle";
        public List<CraneAct> acts = new List<CraneAct>();
        public List<double> reqTimes = new List<double>();
    }

    public class StackerCraneSim
    {
        public const int HIDDEN = 0, SLOT = 1, CONV = 2, CRANE = 3;
        public static readonly string[] STATES = { "idle", "travel", "settle", "forkOut", "lift", "lower", "forkIn", "wait" };

        public readonly StackerCraneConfig c;
        public double t;
        public readonly double pitch, depthY, reach;
        public readonly int nSlots;
        public readonly int[] slotP; public readonly byte[] slotRes;
        public readonly List<Pallet> pal = new List<Pallet>();
        public readonly List<Lane> lanes = new List<Lane>();
        public readonly List<ConvMove> moving = new List<ConvMove>();
        public readonly List<Crane> cranes = new List<Crane>();
        public readonly List<SimEvent> events = new List<SimEvent>();
        readonly double[] nextIn, nextOut;
        public int statStored, statRetrieved, statInArrived, statOutArrived, statExited;
        public double statOutLeadSum;
        uint seedState;

        public StackerCraneSim(StackerCraneConfig cfg)
        {
            c = cfg;
            seedState = (uint)c.seed;
            pitch = c.aisleW + 2 * c.rackD + c.flue;
            depthY = c.aisles * (c.aisleW + 2 * c.rackD) + (c.aisles - 1) * c.flue;
            reach = c.aisleW / 2 + c.rackD / 2;
            nSlots = c.aisles * 2 * c.nx * c.levels;
            slotP = new int[nSlots]; for (int i = 0; i < nSlots; i++) slotP[i] = -1;
            slotRes = new byte[nSlots];
            for (int s = 0; s < nSlots; s++)
                if (Rand() < c.fill) { int id = NewPallet(); pal[id].kind = SLOT; pal[id].refi = s; slotP[s] = id; }
            for (int k = 0; k < c.pool; k++) NewPallet();
            for (int a = 0; a < c.aisles; a++)
                for (int d = 0; d < 2; d++)
                {
                    var L = new Lane { aisle = a, outb = d == 1, seg = new int[c.convN], res = new int[c.convN] };
                    for (int i = 0; i < c.convN; i++) { L.seg[i] = -1; L.res[i] = -1; }
                    lanes.Add(L);
                }
            for (int a = 0; a < c.aisles; a++)
                cranes.Add(new Crane { id = a, aisle = a, x = c.pdX, z = ForkZ(c.level0Z, false) });
            nextIn = new double[c.aisles]; nextOut = new double[c.aisles];
            for (int a = 0; a < c.aisles; a++) { nextIn[a] = Expo(3600 / c.inRate); nextOut[a] = Expo(3600 / c.outRate); }
        }

        // mulberry32 (JS 와 같은 수열)
        public double Rand()
        {
            unchecked
            {
                seedState += 0x6D2B79F5;
                uint t = seedState;
                t = (uint)((int)(t ^ (t >> 15)) * (int)(1 | t));
                t = (t + (uint)((int)(t ^ (t >> 7)) * (int)(61 | t))) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }

        int NewPallet()
        {
            int id = pal.Count;
            var p = new Pallet { id = id, kind = HIDDEN };
            p.load = c.loadMin + Rand() * (c.loadMax - c.loadMin);
            p.variant = (int)Math.Floor(Rand() * 4);
            pal.Add(p);
            return id;
        }

        // ------------------------------------------------------------ 프로파일 (사다리꼴 속도)
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

        // ------------------------------------------------------------ 기하
        double Expo(double mean) => -Math.Log(1 - Rand()) * mean;
        public double AisleY(int a) => c.rackD + c.aisleW / 2 + a * pitch;
        public double SlotX(int i) => (i + 0.5) * c.slotW;
        public double LevelZ(int l) => c.level0Z + l * c.levelH;
        public int SlotId(int a, int side, int i, int l) => ((a * 2 + side) * c.nx + i) * c.levels + l;
        public int SlotAisle(int s) => s / (2 * c.nx * c.levels);
        public int SlotSide(int s) => (s / (c.nx * c.levels)) % 2;
        public int SlotI(int s) => (s / c.levels) % c.nx;
        public int SlotL(int s) => s % c.levels;
        public double[] SlotPos(int s)
        {
            int a = SlotAisle(s), sd = SlotSide(s);
            return new[] { SlotX(SlotI(s)), AisleY(a) + (sd == 1 ? 1 : -1) * reach, LevelZ(SlotL(s)) };
        }
        public double LaneY(int li) { var L = lanes[li]; return AisleY(L.aisle) + (L.outb ? 1 : -1) * reach; }
        public double SegX(int k) => c.pdX - k * c.convL;
        public double ForkZ(double z0, bool loaded) => z0 + c.palletH - 0.022 - c.forkGap + (loaded ? c.lift : 0);
        double TravelTime(double x0, double z0, double x1, double z1) =>
            Math.Max(ProfileTime(Math.Abs(x1 - x0), c.vx, c.ax), ProfileTime(Math.Abs(z1 - z0), c.vz, c.az));

        // ------------------------------------------------------------ 컨베이어
        void StepConveyors(double dt)
        {
            for (int k = moving.Count - 1; k >= 0; k--)
            {
                var m = moving[k]; var p = pal[m.p];
                m.t += dt;
                double s = ProfilePos(m.t, c.convL, c.convV, c.convA) / c.convL;
                p.x = m.x0 + (m.x1 - m.x0) * s;
                if (m.t >= m.T)
                {
                    var L = lanes[m.lane];
                    if (m.from >= 0 && m.from < c.convN) L.seg[m.from] = -1;
                    if (m.to >= c.convN)
                    {
                        p.kind = HIDDEN; p.refi = -1; p.z = -5;
                        if (p.reqT >= 0) { statOutLeadSum += t - p.reqT; statExited++; p.reqT = -1; }
                    }
                    else { L.seg[m.to] = m.p; L.res[m.to] = -1; p.refi = m.lane * c.convN + m.to; p.x = SegX(m.to); }
                    moving.RemoveAt(k);
                }
            }
            for (int li = 0; li < lanes.Count; li++)
            {
                var L = lanes[li];
                if (!L.outb)
                {
                    if (L.queue > 0 && L.seg[c.convN - 1] < 0 && L.res[c.convN - 1] < 0)
                    {
                        int pid = TakeHidden();
                        if (pid >= 0)
                        {
                            L.queue--;
                            var p = pal[pid];
                            p.kind = CONV; p.y = LaneY(li); p.z = c.level0Z; p.x = SegX(c.convN);
                            StartMove(li, pid, c.convN, c.convN - 1);
                        }
                    }
                    for (int k = 1; k < c.convN; k++)
                    {
                        int pid = L.seg[k];
                        if (pid >= 0 && !IsMoving(pid) && L.seg[k - 1] < 0 && L.res[k - 1] < 0) StartMove(li, pid, k, k - 1);
                    }
                }
                else
                {
                    for (int k = c.convN - 1; k >= 0; k--)
                    {
                        int pid = L.seg[k];
                        if (pid < 0 || IsMoving(pid)) continue;
                        if (k == c.convN - 1) StartMove(li, pid, k, c.convN);
                        else if (L.seg[k + 1] < 0 && L.res[k + 1] < 0) StartMove(li, pid, k, k + 1);
                    }
                }
            }
        }
        bool IsMoving(int pid) { foreach (var m in moving) if (m.p == pid) return true; return false; }
        void StartMove(int li, int pid, int from, int to)
        {
            var L = lanes[li];
            if (to < c.convN) L.res[to] = pid;
            moving.Add(new ConvMove { lane = li, p = pid, from = from, to = to, x0 = SegX(from), x1 = SegX(to), t = 0, T = ProfileTime(c.convL, c.convV, c.convA) });
        }
        int TakeHidden() { foreach (var p in pal) if (p.kind == HIDDEN) return p.id; return -1; }

        // ------------------------------------------------------------ 크레인 작업 계획
        int PickEmpty(int a)
        {
            int best = -1; double bt = 1e9;
            double zIn = ForkZ(c.level0Z, true);
            for (int k = 0; k < 40 && (best < 0 || k < 8); k++)
            {
                int sd = (int)Math.Floor(Rand() * 2), i = (int)Math.Floor(Rand() * c.nx), l = (int)Math.Floor(Rand() * c.levels);
                int s = SlotId(a, sd, i, l);
                if (slotP[s] >= 0 || slotRes[s] != 0) continue;
                var P = SlotPos(s); double tt = TravelTime(c.pdX, zIn, P[0], ForkZ(P[2], true));
                if (tt < bt) { bt = tt; best = s; }
            }
            return best;
        }
        int PickFull(int a)
        {
            for (int k = 0; k < 200; k++)
            {
                int sd = (int)Math.Floor(Rand() * 2), i = (int)Math.Floor(Rand() * c.nx), l = (int)Math.Floor(Rand() * c.levels);
                int s = SlotId(a, sd, i, l);
                if (slotP[s] >= 0 && slotRes[s] == 0) return s;
            }
            return -1;
        }

        bool Plan(Crane cr)
        {
            int a = cr.aisle, outLi = 2 * a + 1; var inL = lanes[2 * a];
            int inP = inL.seg[0];
            bool inReady = inP >= 0 && !IsMoving(inP);
            int sIn = -1, sOut = -1;
            if (inReady) sIn = PickEmpty(a);
            if (cr.requests > 0) sOut = PickFull(a);
            var acts = new List<CraneAct>();
            Action<double, double> Go = (x, z) => acts.Add(new CraneAct { k = "move", x = x, z = z });
            if (sIn >= 0)
            {
                slotRes[sIn] = 1;
                Go(c.pdX, ForkZ(c.level0Z, false));
                acts.Add(new CraneAct { k = "fork", get = true, side = -1, lane = 2 * a, slot = -1, z0 = c.level0Z });
                var P = SlotPos(sIn);
                Go(P[0], ForkZ(P[2], true));
                acts.Add(new CraneAct { k = "fork", get = false, side = SlotSide(sIn) == 1 ? 1 : -1, lane = -1, slot = sIn, z0 = P[2] });
                cr.stores++;
            }
            if (sOut >= 0)
            {
                slotRes[sOut] = 1; cr.requests--;
                pal[slotP[sOut]].reqT = cr.reqTimes[0]; cr.reqTimes.RemoveAt(0);
                var P = SlotPos(sOut);
                Go(P[0], ForkZ(P[2], false));
                acts.Add(new CraneAct { k = "fork", get = true, side = SlotSide(sOut) == 1 ? 1 : -1, lane = -1, slot = sOut, z0 = P[2] });
                Go(c.pdX, ForkZ(c.level0Z, true));
                acts.Add(new CraneAct { k = "wait", lane = outLi });
                acts.Add(new CraneAct { k = "fork", get = false, side = 1, lane = outLi, slot = -1, z0 = c.level0Z });
                cr.retrieves++;
            }
            if (acts.Count == 0) return false;
            cr.cycles++; if (sIn >= 0 && sOut >= 0) cr.dual++;
            cr.acts = acts;
            return true;
        }

        // ------------------------------------------------------------ 크레인 동작
        void StepCrane(Crane cr, double dt)
        {
            if (cr.acts.Count == 0 && !Plan(cr)) { cr.state = "idle"; return; }
            cr.busyTime += dt;
            var A = cr.acts[0];
            if (!A.started) BeginAct(cr, A);
            A.t += dt;
            if (A.k == "move")
            {
                double tx = Math.Min(A.t, A.Tx), tz = Math.Min(A.t, A.Tz);
                cr.x = A.x0 + Math.Sign(A.x - A.x0) * ProfilePos(tx, Math.Abs(A.x - A.x0), c.vx, c.ax);
                cr.z = A.zz0 + Math.Sign(A.z - A.zz0) * ProfilePos(tz, Math.Abs(A.z - A.zz0), c.vz, c.az);
                cr.state = A.t < Math.Max(A.Tx, A.Tz) ? "travel" : "settle";
                if (A.t >= A.T) { cr.x = A.x; cr.z = A.z; cr.acts.RemoveAt(0); }
            }
            else if (A.k == "wait")
            {
                cr.state = "wait";
                var L = lanes[A.lane];
                if (L.seg[0] < 0 && L.res[0] < 0) { L.res[0] = cr.carry; cr.acts.RemoveAt(0); }
            }
            else if (A.k == "fork")
            {
                double R = reach, Tf = A.Tf, Tl = A.Tl;
                if (A.t < Tf)
                {
                    cr.state = "forkOut"; cr.fork = A.side * ProfilePos(A.t, R, c.vf, c.af) / R;
                }
                else if (A.t < Tf + Tl)
                {
                    cr.fork = A.side; cr.state = A.get ? "lift" : "lower";
                    if (A.get && cr.carry < 0) { cr.carry = A.p; cr.support = A.z0; pal[A.p].kind = CRANE; }
                    double d = ProfilePos(A.t - Tf, c.lift, c.vl, c.al);
                    cr.z = A.zs + (A.get ? d : -d);
                }
                else
                {
                    if (!A.released) Release(cr, A);
                    cr.state = "forkIn";
                    cr.z = A.zs + (A.get ? c.lift : -c.lift);
                    cr.fork = A.side * (1 - ProfilePos(A.t - Tf - Tl, R, c.vf, c.af) / R);
                }
                if (A.t >= A.T) { cr.fork = 0; cr.acts.RemoveAt(0); }
            }
        }

        void BeginAct(Crane cr, CraneAct A)
        {
            A.started = true; A.t = 0;
            if (A.k == "move")
            {
                A.x0 = cr.x; A.zz0 = cr.z;
                A.Tx = ProfileTime(Math.Abs(A.x - A.x0), c.vx, c.ax); A.Tz = ProfileTime(Math.Abs(A.z - A.zz0), c.vz, c.az);
                A.T = Math.Max(A.Tx, A.Tz) + c.settle;
                cr.travelDist += Math.Abs(A.x - A.x0); cr.hoistDist += Math.Abs(A.z - A.zz0);
            }
            else if (A.k == "fork")
            {
                A.Tf = ProfileTime(reach, c.vf, c.af); A.Tl = ProfileTime(c.lift, c.vl, c.al);
                A.T = 2 * A.Tf + A.Tl; A.zs = cr.z; A.released = false;
                A.p = A.get ? (A.slot >= 0 ? slotP[A.slot] : lanes[A.lane].seg[0]) : cr.carry;
                if (!A.get) cr.support = A.z0;
                events.Add(new SimEvent { t = t, crane = cr.id, kind = A.get ? "get" : "put", where = A.slot >= 0 ? "rack" : "conv",
                    level = A.slot >= 0 ? SlotL(A.slot) : -1, x = cr.x, z = cr.z });
            }
        }

        void Release(Crane cr, CraneAct A)
        {
            A.released = true;
            var p = pal[A.p];
            if (A.get)
            {
                cr.support = -1;
                if (A.slot >= 0) { slotP[A.slot] = -1; slotRes[A.slot] = 0; }
                else lanes[A.lane].seg[0] = -1;
            }
            else
            {
                cr.carry = -1; cr.support = -1;
                if (A.slot >= 0)
                {
                    slotP[A.slot] = A.p; slotRes[A.slot] = 0; p.kind = SLOT; p.refi = A.slot; statStored++;
                    var P = SlotPos(A.slot); p.x = P[0]; p.y = P[1]; p.z = P[2];
                }
                else
                {
                    var L = lanes[A.lane];
                    L.seg[0] = A.p; L.res[0] = -1; p.kind = CONV; p.refi = A.lane * c.convN; statRetrieved++;
                    p.x = SegX(0); p.y = LaneY(A.lane); p.z = c.level0Z;
                }
            }
        }

        // ------------------------------------------------------------ 한 스텝
        public void Step(double dt)
        {
            for (int a = 0; a < c.aisles; a++)
            {
                while (nextIn[a] <= t) { lanes[2 * a].queue++; statInArrived++; nextIn[a] += Expo(3600 / c.inRate); }
                while (nextOut[a] <= t)
                {
                    var cr = cranes[a]; cr.requests++; cr.reqTimes.Add(nextOut[a]);
                    statOutArrived++; nextOut[a] += Expo(3600 / c.outRate);
                }
            }
            StepConveyors(dt);
            foreach (var cr in cranes) StepCrane(cr, dt);
            t += dt;
        }

        // 팔레트 바닥 가운데 위치 (x, y, z). 숨은 팔레트는 z = -5
        public void PalletPose(int id, out double x, out double y, out double z)
        {
            var p = pal[id];
            if (p.kind == CRANE)
                foreach (var cr in cranes)
                    if (cr.carry == id)
                    {
                        z = cr.z - (c.palletH - 0.022);
                        if (cr.support >= 0 && z < cr.support) z = cr.support;
                        x = cr.x; y = AisleY(cr.aisle) + cr.fork * reach; return;
                    }
            if (p.kind == SLOT) { var P = SlotPos(p.refi); x = P[0]; y = P[1]; z = P[2]; return; }
            if (p.kind == HIDDEN) { x = SegX(c.convN + 1); y = 0; z = -5; return; }
            x = p.x; y = p.y; z = p.z;
        }

        // ------------------------------------------------------------ 지표
        public double MovesPerHour => (statStored + statRetrieved) / Math.Max(1e-9, t) * 3600;
        public double CraneUtil { get { double s = 0; foreach (var cr in cranes) s += cr.busyTime; return s / (cranes.Count * Math.Max(1e-9, t)); } }
        public double AvgCycle { get { double s = 0; int n = 0; foreach (var cr in cranes) { s += cr.busyTime; n += cr.cycles; } return n > 0 ? s / n : 0; } }
        public double DualShare { get { int d = 0, n = 0; foreach (var cr in cranes) { d += cr.dual; n += cr.cycles; } return n > 0 ? (double)d / n : 0; } }
        public double Occupancy { get { int n = 0; foreach (var v in slotP) if (v >= 0) n++; return (double)n / nSlots; } }
    }
}
