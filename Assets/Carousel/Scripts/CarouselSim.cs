// 수평형 + 수직형 캐러셀 피킹 시뮬레이션 (web/carousel-sim-core.js 와 같은 로직, 같은 mulberry32 난수)
// 좌표: x 오른쪽, y 안쪽 (작업자 쪽 y < 0), z 위. 거리 m, 시간 s. Unity 변환은 CarouselView.U()
using System;
using System.Collections.Generic;

namespace Carousel
{
    [Serializable]
    public class CarouselConfig
    {
        // 수평형 (가정: 캐리어 폭 0.52 m, 피치 0.62 m, 6단 x 2빈, 최고 0.45 m/s)
        public int hCount = 2, hCarriers = 26;
        public double hPitch = 0.62, hR = 0.40, hW = 0.52, hD = 0.46;
        public int hShelves = 6, hBins = 2;
        public double hShelf0 = 0.30, hShelfH = 0.33, hTopZ = 2.45;
        public double[] hx = { 0, 2.3 };
        public double hV = 0.45, hAcc = 0.35;
        public double hPickMin = 3.0, hPickMax = 5.0, walkT = 1.8;
        public int hSlots = 8; public double hOrderRate = 280;
        // 수직형 (가정: 트레이 폭 2.45 m, 깊이 0.45 m, 피치 0.66 m, 트레이 20개, 최고 0.20 m/s)
        public int vCarriers = 20;
        public double vPitch = 0.66, vR = 0.46, vW = 2.45, vD = 0.45, vH = 0.40;
        public int vBins = 6;
        public double vx = 6.6, vFrontY = 0.33, vZb = 0.80, vWinZ = 1.05;
        public double vV = 0.20, vAcc = 0.25;
        public double vPickMin = 4.0, vPickMax = 6.5;
        public int vSlots = 4; public double vOrderRate = 170;
        public int linesMax = 3;
        public int seed = 22;   // 30초 영상 기준 시드
    }

    public class Line { public int order, unit, carrier, shelf, bin; public bool done; }
    public class Order { public int id, op, left, slot = -1; public double tArrive, tDone = -1; public List<Line> lines = new List<Line>(); }

    public class Unit
    {
        public int id, n; public bool vert;
        public double pitch, vmax, acc;
        public int step, moveK; public double moveT0, moveT;
        public List<Line> pending = new List<Line>();
        public bool locked; public double busyTime, travel; public int moves;
    }

    public class Operator
    {
        public int id, at, state, walkFrom; public double tEnd, pickT0, pickT;
        public List<int> faces = new List<int>();
        public Line line; public int[] slots; public List<Order> queue = new List<Order>();
        public double rate, nextT, busyTime, leadSum; public int picks, done;
    }

    public class CarouselSim
    {
        public readonly CarouselConfig cfg;
        public double t;
        public readonly double hPer, hL, hY0, vPer, vL, vZt, vBackY;
        public readonly List<Unit> units = new List<Unit>();
        public readonly List<Operator> ops = new List<Operator>();
        public readonly List<Order> orders = new List<Order>();
        int rngState;

        public CarouselSim(CarouselConfig c)
        {
            cfg = c; rngState = c.seed;
            hPer = c.hCarriers * c.hPitch;
            hL = (hPer - 2 * Math.PI * c.hR) / 2;
            hY0 = c.hR + c.hD;
            vPer = c.vCarriers * c.vPitch;
            vL = (vPer - 2 * Math.PI * c.vR) / 2;
            vZt = c.vZb + vL;
            vBackY = c.vFrontY + 2 * c.vR;
            for (int k = 0; k <= c.hCount; k++)
            {
                bool vert = k == c.hCount;
                units.Add(new Unit
                {
                    id = k, vert = vert, n = vert ? c.vCarriers : c.hCarriers, pitch = vert ? c.vPitch : c.hPitch,
                    vmax = vert ? c.vV : c.hV, acc = vert ? c.vAcc : c.hAcc,
                });
            }
            var o0 = new Operator { id = 0, at = 0, walkFrom = 0, slots = Fill(c.hSlots, -1), rate = c.hOrderRate };
            var o1 = new Operator { id = 1, at = c.hCount, walkFrom = c.hCount, slots = Fill(c.vSlots, -1), rate = c.vOrderRate };
            o1.faces.Add(c.hCount);
            ops.Add(o0); ops.Add(o1);
            for (int k = 0; k < c.hCount; k++) o0.faces.Add(k);
            foreach (var o in ops) o.nextT = Expo(3600 / o.rate);
            foreach (var u in units) u.step = (int)Math.Floor(Rand() * u.n);
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
        double Expo(double mean) => -Math.Log(1 - Rand()) * mean;
        static double Mod(double a, double n) => ((a % n) + n) % n;
        static int Mod(int a, int n) => ((a % n) + n) % n;

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

        // ------------------------------------------------------------ 주문
        void NewOrder(Operator op)
        {
            var c = cfg;
            int nl = 1 + (int)Math.Floor(Rand() * c.linesMax);
            var o = new Order { id = orders.Count, op = op.id, tArrive = t, left = nl };
            for (int i = 0; i < nl; i++)
            {
                int unit, carrier, shelf, bin;
                if (op.id == 0)
                {
                    unit = (int)Math.Floor(Rand() * c.hCount);
                    carrier = (int)Math.Floor(Rand() * c.hCarriers);
                    shelf = (int)Math.Floor(Rand() * c.hShelves);
                    bin = (int)Math.Floor(Rand() * c.hBins);
                }
                else
                {
                    unit = c.hCount;
                    carrier = (int)Math.Floor(Rand() * c.vCarriers);
                    shelf = 0;
                    bin = (int)Math.Floor(Rand() * c.vBins);
                }
                o.lines.Add(new Line { order = o.id, unit = unit, carrier = carrier, shelf = shelf, bin = bin });
            }
            orders.Add(o);
            op.queue.Add(o);
        }

        void AssignSlots(Operator op)
        {
            while (op.queue.Count > 0)
            {
                int s = Array.IndexOf(op.slots, -1);
                if (s < 0) break;
                var o = op.queue[0]; op.queue.RemoveAt(0);
                o.slot = s; op.slots[s] = o.id;
                foreach (var ln in o.lines) units[ln.unit].pending.Add(ln);
            }
        }

        // ------------------------------------------------------------ 캐러셀
        public int FrontCarrier(Unit u) => Mod(-u.step, u.n);
        public double StepPos(Unit u)
        {
            if (u.moveK == 0) return u.step;
            double d = Math.Abs(u.moveK) * u.pitch;
            double p = ProfilePos(t - u.moveT0, d, u.vmax, u.acc) / u.pitch;
            return u.step + Math.Sign(u.moveK) * p;
        }
        public double CarrierS(Unit u, int i) => Mod((i + StepPos(u)) * u.pitch, u.n * u.pitch);
        int Delta(Unit u, int i)
        {
            int k = Mod(-i - u.step, u.n);
            if (k > u.n / 2.0) k -= u.n;
            return k;
        }
        public Line ReadyLine(Unit u)
        {
            if (u.moveK != 0) return null;
            int f = FrontCarrier(u);
            foreach (var ln in u.pending) if (ln.carrier == f) return ln;
            return null;
        }

        void StepUnit(Unit u, double dt)
        {
            if (u.moveK != 0)
            {
                u.busyTime += dt;
                if (t - u.moveT0 >= u.moveT - 1e-9) { u.step = Mod(u.step + u.moveK, u.n); u.moveK = 0; }
                return;
            }
            if (u.locked || u.pending.Count == 0 || ReadyLine(u) != null) return;
            Line best = null; int bd = 1000000000;
            foreach (var ln in u.pending)
            {
                int d = Math.Abs(Delta(u, ln.carrier));
                if (d < bd) { bd = d; best = ln; }
            }
            int k = Delta(u, best.carrier);
            u.moveK = k; u.moveT0 = t; u.moveT = ProfileTime(Math.Abs(k) * u.pitch, u.vmax, u.acc);
            u.travel += Math.Abs(k) * u.pitch; u.moves++;
        }

        // ------------------------------------------------------------ 작업자
        void StepOp(Operator op, double dt)
        {
            var c = cfg;
            if (op.state == 2) op.busyTime += dt;
            if (op.state != 0 && t < op.tEnd - 1e-9) return;
            if (op.state == 2)
            {
                var ln = op.line; var u = units[ln.unit]; var o = orders[ln.order];
                ln.done = true; u.pending.Remove(ln); u.locked = false;
                op.picks++; op.line = null;
                if (--o.left == 0)
                {
                    o.tDone = t; op.done++; op.leadSum += o.tDone - o.tArrive;
                    op.slots[o.slot] = -1;
                    AssignSlots(op);
                }
            }
            op.state = 0;
            var here = units[op.at];
            var rl = ReadyLine(here);
            if (rl != null)
            {
                op.state = 2; op.line = rl; here.locked = true;
                op.pickT0 = t;
                double lo = op.id == 0 ? c.hPickMin : c.vPickMin, hi = op.id == 0 ? c.hPickMax : c.vPickMax;
                op.pickT = lo + Rand() * (hi - lo); op.tEnd = t + op.pickT;
                return;
            }
            foreach (int k in op.faces)
            {
                if (k == op.at) continue;
                if (ReadyLine(units[k]) != null)
                {
                    op.state = 1; op.walkFrom = op.at; op.at = k; op.pickT0 = t; op.tEnd = t + c.walkT;
                    return;
                }
            }
        }

        public void Step(double dt)
        {
            t += dt;
            foreach (var op in ops)
            {
                while (t >= op.nextT) { NewOrder(op); op.nextT += Expo(3600 / op.rate); }
                AssignSlots(op);
            }
            foreach (var u in units) StepUnit(u, dt);
            foreach (var op in ops) StepOp(op, dt);
        }

        // 작업자 진행률 (이동·피킹 0..1)
        public double OpProgress(Operator op) => op.state == 0 ? 0 : Math.Min(1, (t - op.pickT0) / (op.tEnd - op.pickT0));

        // ------------------------------------------------------------ 형상 (JS / Blender 와 같은 식)
        // 수평형 궤도 위 점 → (x, y, 바깥 법선 각도 rad)
        public void HPath(int k, double s, out double x, out double y, out double ang)
        {
            var c = cfg; double r = c.hR, L = hL, x0 = c.hx[k], y0 = hY0;
            s = Mod(s, hPer);
            double q = Math.PI * r / 2, a;
            if (s < q) { a = -Math.PI / 2 + s / r; x = x0 + r * Math.Cos(a); y = y0 + r * Math.Sin(a); ang = a; return; }
            s -= q;
            if (s < L) { x = x0 + r; y = y0 + s; ang = 0; return; }
            s -= L;
            if (s < 2 * q) { a = s / r; x = x0 + r * Math.Cos(a); y = y0 + L + r * Math.Sin(a); ang = a; return; }
            s -= 2 * q;
            if (s < L) { x = x0 - r; y = y0 + L - s; ang = Math.PI; return; }
            s -= L;
            a = Math.PI + s / r; x = x0 + r * Math.Cos(a); y = y0 + r * Math.Sin(a); ang = a;
        }
        public void HCarrierPose(int k, int i, out double x, out double y, out double ang)
        {
            HPath(k, CarrierS(units[k], i), out x, out y, out ang);
            double h = cfg.hD / 2; x += Math.Cos(ang) * h; y += Math.Sin(ang) * h;
        }
        // 수직형 궤도 위 점 → (y, z) 트레이 중심
        public void VPath(double u, out double y, out double z)
        {
            var c = cfg; double r = c.vR, yF = c.vFrontY, yB = vBackY, zb = c.vZb, zt = vZt;
            u = Mod(u, vPer);
            double a0 = zt - c.vWinZ;
            if (u < a0) { y = yF; z = c.vWinZ + u; return; }
            u -= a0;
            double hc = Math.PI * r;
            if (u < hc) { double a = u / r; y = yF + r - r * Math.Cos(a); z = zt + r * Math.Sin(a); return; }
            u -= hc;
            if (u < vL) { y = yB; z = zt - u; return; }
            u -= vL;
            if (u < hc) { double a = u / r; y = yB - r + r * Math.Cos(a); z = zb - r * Math.Sin(a); return; }
            u -= hc;
            y = yF; z = zb + u;
        }
        public void VCarrierPose(int i, out double y, out double z) => VPath(CarrierS(units[cfg.hCount], i), out y, out z);

        // ------------------------------------------------------------ KPI
        double T => Math.Max(1e-9, t);
        public double HLinesPerHour => ops[0].picks / T * 3600;
        public double VLinesPerHour => ops[1].picks / T * 3600;
        public double HOpUtil => ops[0].busyTime / T;
        public double VOpUtil => ops[1].busyTime / T;
        public double HLead => ops[0].done > 0 ? ops[0].leadSum / ops[0].done : 0;
        public double VLead => ops[1].done > 0 ? ops[1].leadSum / ops[1].done : 0;
        public double HCarouselUtil { get { double s = 0; for (int k = 0; k < cfg.hCount; k++) s += units[k].busyTime; return s / (cfg.hCount * T); } }
        public double VCarouselUtil => units[cfg.hCount].busyTime / T;
    }
}
