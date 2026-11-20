// 반자동 제함기 → 컨베이어(충전·상부 접지) → 반자동 봉함기 라인의 동작 정의.
// blender/caseline_kinematics.py 를 그대로 옮긴 것 (UnityEngine 의존 없음: mono 로 따로 컴파일해 비교 가능).
// 좌표는 Blender 기준 (x = 흐름 방향, y = 안쪽, z = 위). Unity 로는 CaseLineView.U(x, y, z) = (x, z, y).
using System;
using System.Collections.Generic;

namespace CaseLine
{
    public struct D3
    {
        public double x, y, z;
        public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
        public static D3 Cross(D3 a, D3 b) => new D3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }

    public struct Slab
    {
        public string name; public D3 center, ex, ey, ez, size;
    }

    public struct TapePart
    {
        public string name; public bool on; public D3 center, size;
    }

    public static class K
    {
        // ------------------------------------------------------------ 치수
        public const double TABLE = 0.75;
        public const double L = 0.40, W = 0.30, H = 0.30;
        public const double F = W / 2;
        public const double T = 0.004;
        public const double REST_Z = TABLE + T;
        public const double PERIOD = 5.0;
        public const double PHASE = 0.0;
        public const double LIFE = 32.6;
        public const double SPLAY = -0.30;
        public static readonly D3 PACK = new D3(0.185, 0.135, 0.12);
        public const double X_FILL = 1.90;
        public const double X_END = 9.50;
        public const double V = 0.35;
        public const double ACC = 0.8;
        public const double ACC_PUSH = 1.0;
        public const double X_HEAD = 4.30;
        public const double MAG_Y = 0.55;
        public const double LIFT = 0.20;
        public const double ARM_UP = 0.40;
        public const double X_BAR = 2.78;
        public const double RAIL_LEN = 0.22;
        public const double RAIL_END = 3.95;
        public const double RAIL_Z0 = 0.10, RAIL_Z1 = 0.014, RAIL_R = 0.008;
        public const double X_KICK = 2.70;
        public const double KICK_H = 0.36, KICK_R = 0.38;
        public const double KICK_START = X_KICK - 0.26;
        public const double KICK_LEN = 0.10;
        public static readonly double KICK_PARK = -60.0 * Math.PI / 180.0;
        public const double KICK_Z = REST_Z + H + KICK_H;
        public const double PLOW_X0 = 3.25, PLOW_X1 = 3.80;
        public const double PLOW_Y0 = 0.20, PLOW_Y1 = 0.045, PLOW_Z0 = 0.16, PLOW_Z1 = 0.014;
        public const double CHUTE_SY = 1.05, CHUTE_SZ = TABLE + 0.95;
        public const double CHUTE_EY = 0.30, CHUTE_EZ = TABLE + 0.62;
        public static readonly double CHUTE_A = Math.Atan2(CHUTE_SZ - CHUTE_EZ, CHUTE_SY - CHUTE_EY);
        public const double ROLLER_R = 0.025;
        public const double PUSH_REST = -0.25;
        public const double PUSH_END = 0.24;
        public const double G = 9.81;
        public const double HALF_PI = Math.PI / 2;
        public const double TAPE_W = 0.048, TAPE_T = 0.0006, TAB = 0.06;

        public const double T_OPEN0 = 0.9, T_OPEN1 = 1.5;
        public const double T_DOWN0 = 1.5, T_DOWN1 = 1.9;
        public const double T_PUSH = 2.45;
        public const double FILL_ARRIVE = T_PUSH + X_FILL / V + V / ACC_PUSH;
        public const double PACK_T0 = FILL_ARRIVE - 0.55, PACK_DT = 0.25, PACK_SLIDE = 0.7;
        public const double FILL_DONE = PACK_T0 + 7 * PACK_DT + PACK_SLIDE + 0.45;

        static double Rad(double deg) => deg * Math.PI / 180.0;
        public static double Clamp01(double u) => u < 0 ? 0 : u > 1 ? 1 : u;
        public static double Smooth(double u) { u = Clamp01(u); return u * u * (3 - 2 * u); }
        public static double Lerp(double a, double b, double u) => a + (b - a) * u;

        public static double Trap(double t, double d, double v = V, double a = ACC)
        {
            if (t <= 0) return 0;
            double ta = v / a, tt = d / v + ta;
            if (t < ta) return 0.5 * a * t * t;
            if (t < tt - ta) return 0.5 * a * ta * ta + v * (t - ta);
            if (t < tt) return d - 0.5 * a * (tt - t) * (tt - t);
            return d;
        }

        // ------------------------------------------------------------ 박스 원점 경로
        // 흡착 암은 블랭크 앞면(L2)을 잡음. 펼치는 동안 L2 는 x 가 그대로라 박스 원점 x = W cos(theta)
        public static double Theta(double tau) => Rad(Lerp(2.0, 90.0, Smooth((tau - T_OPEN0) / (T_OPEN1 - T_OPEN0))));

        public static double PushX(double tau)
        {
            double d = X_FILL, ta = V / ACC_PUSH, tt = d / V + ta, t = tau - T_PUSH;
            if (t <= 0) return 0;
            if (t < ta) return 0.5 * ACC_PUSH * t * t;
            if (t < tt - ta) return 0.5 * ACC_PUSH * ta * ta + V * (t - ta);
            if (t < tt) return d - 0.5 * ACC_PUSH * (tt - t) * (tt - t);
            return d;
        }

        public static double BoxX(double tau)
        {
            if (tau < T_PUSH) return W * Math.Cos(Theta(tau));
            if (tau < FILL_DONE) return PushX(tau);
            return X_FILL + Trap(tau - FILL_DONE, X_END - X_FILL);
        }

        public static double BoxY(double tau)
        {
            if (tau < 0.4) return MAG_Y;
            return MAG_Y * (1 - Smooth((tau - 0.4) / 0.9));
        }

        public static double BoxZ(double tau)
        {
            if (tau < 0.4) return REST_Z + LIFT * Smooth(tau / 0.4);
            if (tau < T_DOWN0) return REST_Z + LIFT;
            return REST_Z + LIFT * (1 - Smooth((tau - T_DOWN0) / (T_DOWN1 - T_DOWN0)));
        }

        // ------------------------------------------------------------ 위 날개 접촉
        static double RailZ(double x) => RAIL_Z1 + (RAIL_Z0 - RAIL_Z1) * (1 - Smooth((x - X_BAR) / RAIL_LEN));

        public static double LeadContact(double front)
        {
            double best = -10.0;
            double x0 = Math.Max(X_BAR, front - F), x1 = Math.Min(RAIL_END, front + F);
            const int n = 40;
            if (x1 < x0) return best;
            for (int i = 0; i <= n; i++)
            {
                double xr = x0 + (x1 - x0) * i / n;
                double s = front - xr, z = RailZ(xr) - RAIL_R;
                if (s * s + z * z <= F * F) best = Math.Max(best, Math.Atan2(s, z));
            }
            return best;
        }

        public static double PlowContact(double X)
        {
            double best = -10.0;
            double x0 = Math.Max(PLOW_X0, X - L / 2), x1 = Math.Min(PLOW_X1 + 0.15, X + L / 2);
            const int n = 30;
            if (x1 < x0) return best;
            for (int i = 0; i <= n; i++)
            {
                double xp = x0 + (x1 - x0) * i / n;
                double u = Clamp01((xp - PLOW_X0) / (PLOW_X1 - PLOW_X0));
                double s = W / 2 - Lerp(PLOW_Y0, PLOW_Y1, u), z = Lerp(PLOW_Z0, PLOW_Z1, u) - RAIL_R;
                if (s * s + z * z <= F * F) best = Math.Max(best, Math.Atan2(s, z));
            }
            return best;
        }

        static double KickU(double X) => Clamp01((X - L / 2 - KICK_START) / KICK_LEN);
        static double TrailAngle(double X, double b) => b + (HALF_PI - b) * Smooth(KickU(X));

        // 킥 중 팔 끝이 날개 위에 닿는 팔 각. 해가 없으면 NaN
        public static double KickArmFor(double X, double b)
        {
            double rear = X - L / 2, phi = TrailAngle(X, b);
            double s = Math.Sin(phi), c = Math.Cos(phi);
            double ax = rear - X_KICK, az = -KICK_H;
            double bb = ax * s + az * c, cc = ax * ax + az * az - KICK_R * KICK_R, D = bb * bb - cc;
            if (D < 0) return double.NaN;
            double r = double.NaN;
            foreach (double rr in new[] { -bb - Math.Sqrt(D), -bb + Math.Sqrt(D) })
                if (rr >= 0.0 && rr <= F + 1e-9 && (double.IsNaN(r) || rr > r)) r = rr;
            if (double.IsNaN(r)) return double.NaN;
            double qx = rear + r * s, qz = r * c;
            return Math.Atan2(qx - X_KICK, qz - KICK_H);
        }

        // (아래 단변, 아래 장변, 위 앞 단변, 위 뒤 단변, 위 장변)
        public static void FlapAngles(double tau, out double bMinor, out double bMajor, out double lead, out double trail, out double major)
        {
            double X = BoxX(tau);
            bMinor = HALF_PI * Smooth((tau - 1.95) / 0.35);
            bMajor = tau >= T_PUSH ? HALF_PI * Smooth((X - 0.02) / 0.28) : 0.0;
            double b = SPLAY * Smooth((tau - 1.0) / 0.6);
            double front = X + L / 2;
            lead = Math.Max(Math.Max(b, LeadContact(front)), b + (HALF_PI - b) * Smooth((front - X_BAR - 0.30) / 0.15));
            lead = Math.Min(lead, HALF_PI);
            trail = TrailAngle(X, b);
            major = Math.Min(HALF_PI, Math.Max(Math.Max(b, PlowContact(X)), b + (HALF_PI - b) * Smooth((X - 3.70) / 0.15)));
        }

        // ------------------------------------------------------------ 박스 판 (벽 4 + 날개 8)
        public static readonly string[] PanelNames = { "L1", "W1", "L2", "W2" };

        public static List<Slab> BoxSlabs(double tau)
        {
            double th = Theta(tau), c = Math.Cos(th), s = Math.Sin(th);
            double dx = -c, dy = -s;
            var pts = new double[4, 2];
            pts[0, 0] = -L / 2; pts[0, 1] = W / 2;
            pts[1, 0] = L / 2; pts[1, 1] = W / 2;
            pts[2, 0] = pts[1, 0] + W * dx; pts[2, 1] = pts[1, 1] + W * dy;
            pts[3, 0] = pts[0, 0] + W * dx; pts[3, 1] = pts[0, 1] + W * dy;
            double[] lens = { L, W, L, W };
            FlapAngles(tau, out double bmi, out double bma, out double lead, out double trail, out double tma);
            double[] bot = { bma, bmi, bma, bmi };
            double[] top = { tma, lead, tma, trail };
            var walls = new List<Slab>(); var bots = new List<Slab>(); var tops = new List<Slab>();
            var Z = new D3(0, 0, 1); var mZ = new D3(0, 0, -1);
            for (int i = 0; i < 4; i++)
            {
                double ax = pts[i, 0], ay = pts[i, 1], bx = pts[(i + 1) % 4, 0], by = pts[(i + 1) % 4, 1], ln = lens[i];
                var e = new D3((bx - ax) / ln, (by - ay) / ln, 0);
                var n = new D3(e.y, -e.x, 0);
                var outN = n * -1;
                bool isMajor = i % 2 == 0;
                var mid = new D3(ax + e.x * (ln + T) / 2, ay + e.y * (ln + T) / 2, 0);
                walls.Add(new Slab { name = "Wall_" + PanelNames[i], center = mid + outN * (T / 2) + new D3(0, 0, H / 2), ex = e, ey = outN, ez = Z, size = new D3(ln + T, T, H) });
                var hm = new D3(ax + e.x * ln / 2, ay + e.y * ln / 2, 0);
                double flen = ln - (isMajor ? 0.004 : 0.012);
                for (int kk = 0; kk < 2; kk++)
                {
                    bool isBot = kk == 0;
                    double phi = isBot ? bot[i] : top[i];
                    double cp = Math.Cos(phi), sp = Math.Sin(phi);
                    D3 f, m; double hz;
                    if (isBot) { f = mZ * cp + n * sp; m = n * cp + Z * sp; hz = 0; }
                    else { f = Z * cp + n * sp; m = n * cp + mZ * sp; hz = H; }
                    double side = isMajor ? -1 : 1;
                    var cen = new D3(hm.x, hm.y, hz) + f * (F / 2) + m * (side * T / 2);
                    var Y = D3.Cross(f, e);
                    var sl = new Slab { name = (isBot ? "Bot_" : "Top_") + PanelNames[i], center = cen, ex = e, ey = Y, ez = f, size = new D3(flen, T, F) };
                    (isBot ? bots : tops).Add(sl);
                }
            }
            walls.AddRange(bots); walls.AddRange(tops);
            return walls;
        }

        // ------------------------------------------------------------ 테이프
        public static readonly string[] TapeNames = { "Tape_Top", "Tape_Bot", "Tab_FrontTop", "Tab_FrontBot", "Tab_RearTop", "Tab_RearBot" };

        public static TapePart[] TapeParts(double tau)
        {
            double X = BoxX(tau);
            double a = Math.Max(-L / 2, Math.Min(L / 2, X_HEAD - X));
            bool on = X + L / 2 > X_HEAD;
            double ln = on ? L / 2 + T - a : 0;
            double ztop = H + T + TAPE_T / 2, zbot = -T - TAPE_T / 2, xf = L / 2 + T + TAPE_T / 2;
            bool rear = X - L / 2 > X_HEAD + 0.005;
            double cx = (a + L / 2 + T) / 2;
            return new[]
            {
                new TapePart { name = "Tape_Top", on = on, center = new D3(cx, 0, ztop), size = new D3(Math.Max(ln, 1e-4), TAPE_W, TAPE_T) },
                new TapePart { name = "Tape_Bot", on = on, center = new D3(cx, 0, zbot), size = new D3(Math.Max(ln, 1e-4), TAPE_W, TAPE_T) },
                new TapePart { name = "Tab_FrontTop", on = on, center = new D3(xf, 0, H + T - TAB / 2), size = new D3(TAPE_T, TAPE_W, TAB) },
                new TapePart { name = "Tab_FrontBot", on = on, center = new D3(xf, 0, -T + TAB / 2), size = new D3(TAPE_T, TAPE_W, TAB) },
                new TapePart { name = "Tab_RearTop", on = rear, center = new D3(-xf, 0, H + T - TAB / 2), size = new D3(TAPE_T, TAPE_W, TAB) },
                new TapePart { name = "Tab_RearBot", on = rear, center = new D3(-xf, 0, -T + TAB / 2), size = new D3(TAPE_T, TAPE_W, TAB) },
            };
        }

        // ------------------------------------------------------------ 상품 8개 (슈트 등가속 → 포물선 낙하)
        static readonly int[,] PackOrder = { { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 } };
        static readonly double CHUTE_D = Math.Sqrt((CHUTE_SY - CHUTE_EY) * (CHUTE_SY - CHUTE_EY) + (CHUTE_SZ - CHUTE_EZ) * (CHUTE_SZ - CHUTE_EZ));
        static readonly double CHUTE_ACC = 2 * CHUTE_D / (PACK_SLIDE * PACK_SLIDE);
        static readonly double CHUTE_VE = CHUTE_ACC * PACK_SLIDE;

        public static bool PackPose(double tau, int j, out D3 pos, out double ang)
        {
            int sx = PackOrder[j % 4, 0], sy = PackOrder[j % 4, 1], layer = j / 4;
            double ox = sx * 0.098, oy = sy * 0.072;
            double ts = PACK_T0 + PACK_DT * j;
            double X = BoxX(tau);
            var S = new D3(X_FILL + ox, CHUTE_SY, CHUTE_SZ); var E = new D3(X_FILL + ox, CHUTE_EY, CHUTE_EZ);
            var Gp = new D3(X + ox, oy, REST_Z + PACK.z / 2 + layer * PACK.z);
            if (tau < ts) { pos = S; ang = CHUTE_A; return false; }
            double t = tau - ts;
            if (t < PACK_SLIDE)
            {
                double u = 0.5 * CHUTE_ACC * t * t / CHUTE_D;
                pos = new D3(Lerp(S.x, E.x, u), Lerp(S.y, E.y, u), Lerp(S.z, E.z, u)); ang = CHUTE_A; return true;
            }
            double vz = CHUTE_VE * Math.Sin(CHUTE_A), dz = E.z - Gp.z;
            double tf = (-vz + Math.Sqrt(vz * vz + 2 * G * dz)) / G;
            t -= PACK_SLIDE;
            if (t < tf)
            {
                double u = t / tf;
                pos = new D3(Lerp(E.x, Gp.x, u), Lerp(E.y, Gp.y, u), E.z - vz * t - 0.5 * G * t * t); ang = CHUTE_A * (1 - Smooth(u)); return true;
            }
            pos = Gp; ang = 0; return true;
        }

        // ------------------------------------------------------------ 기계 가동부
        public static void Cycle(double t, out long k, out double tau)
        {
            k = (long)Math.Floor((t - PHASE) / PERIOD);
            tau = t - PHASE - k * PERIOD;
        }

        // 흡착면 = 블랭크 앞면(L2) 바깥 면 y
        public static double GripY(double tau) => BoxY(tau) + W / 2 - W * Math.Sin(Theta(tau)) - T;
        public static readonly double ARM_Y_MAG = MAG_Y + W / 2 - W * Math.Sin(Rad(2.0)) - T;

        // 흡착 암 기준점 = 흡착컵 앞면 중심 (z 는 박스 원점 높이 규약, 컵은 그 위 H/2)
        public static D3 PickArm(double t)
        {
            Cycle(t, out _, out double tau);
            double y0 = GripY(2.3);
            if (tau < 2.3) return new D3(0, GripY(tau), BoxZ(tau));
            if (tau < 2.5) return new D3(0, y0 - 0.06 * Smooth((tau - 2.3) / 0.2), REST_Z);
            if (tau < 3.0) return new D3(0, y0 - 0.06, REST_Z + ARM_UP * Smooth((tau - 2.5) / 0.5));
            if (tau < 4.1) return new D3(0, Lerp(y0 - 0.06, ARM_Y_MAG - 0.05, Smooth((tau - 3.0) / 1.1)), REST_Z + ARM_UP);
            if (tau < 4.55) return new D3(0, ARM_Y_MAG - 0.05, REST_Z + ARM_UP * (1 - Smooth((tau - 4.1) / 0.45)));
            return new D3(0, Lerp(ARM_Y_MAG - 0.05, ARM_Y_MAG, Smooth((tau - 4.55) / 0.45)), REST_Z);
        }

        public static double PusherX(double t)
        {
            Cycle(t, out _, out double tau);
            double contact = -L / 2 - 0.012;
            if (tau < 2.15) return PUSH_REST;
            if (tau < T_PUSH) return Lerp(PUSH_REST, contact, Smooth((tau - 2.15) / (T_PUSH - 2.15)));
            double p = Math.Min(BoxX(tau) + contact, PUSH_END);
            if (tau < 3.95) return p;
            return Lerp(PUSH_END, PUSH_REST, Smooth((tau - 3.95) / 0.6));
        }

        // 뒤 날개 킥커 팔 (y축 회전 rad, 0 = 위, - = 뒤로)
        public static double KickerAngle(double t)
        {
            long k0 = (long)Math.Floor((t - PHASE) / PERIOD);
            for (long k = k0 - 7; k <= k0; k++)
            {
                double tau = t - PHASE - k * PERIOD;
                if (tau < 0 || tau > LIFE) continue;
                double X = BoxX(tau), rear = X - L / 2;
                if (rear < KICK_START - 0.12 || rear > KICK_START + KICK_LEN + 0.14) continue;
                double aFirst = KickArmFor(KICK_START + L / 2, SPLAY);
                double aLast = KickArmFor(KICK_START + KICK_LEN + L / 2, SPLAY);
                if (rear < KICK_START) return Lerp(KICK_PARK, aFirst, Smooth((rear - (KICK_START - 0.12)) / 0.12));
                if (rear <= KICK_START + KICK_LEN)
                {
                    double a = KickArmFor(X, SPLAY);
                    return double.IsNaN(a) ? aLast : a;
                }
                return Lerp(aLast, KICK_PARK, Smooth((rear - KICK_START - KICK_LEN) / 0.14));
            }
            return KICK_PARK;
        }

        public static double RollerAngle(double t) => V / ROLLER_R * t;

        public static List<long> BoxesAlive(double t)
        {
            var r = new List<long>();
            long k0 = (long)Math.Floor((t - PHASE) / PERIOD);
            for (long k = k0 - 7; k <= k0 + 1; k++)
            {
                double tau = t - PHASE - k * PERIOD;
                if (tau >= -PERIOD && tau <= LIFE) r.Add(k);
            }
            return r;
        }

        public static bool BoxPose(double tau, out D3 root)
        {
            if (tau < -PERIOD || tau > LIFE) { root = new D3(0, 0, 0); return false; }
            double tc = Math.Max(tau, 0.0);
            root = new D3(BoxX(tc), BoxY(tc), BoxZ(tc));
            return true;
        }
    }
}
