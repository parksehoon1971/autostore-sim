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
        public const double X_HEAD = 4.30;
        public const double MAG_Y = 0.55;
        public const double LIFT = 0.20;
        public const double X_BAR = 2.78;
        public const double X_KICK = 2.62;
        public const double KICK_Z = REST_Z + H + 0.18;
        public const double CHUTE_SY = 1.05, CHUTE_SZ = TABLE + 0.95;
        public const double CHUTE_EY = 0.30, CHUTE_EZ = TABLE + 0.62;
        public static readonly double CHUTE_A = Math.Atan2(CHUTE_SZ - CHUTE_EZ, CHUTE_SY - CHUTE_EY);
        public const double ROLLER_R = 0.025;
        public const double PUSH_REST = -0.32;
        public const double HALF_PI = Math.PI / 2;
        public const double FILL_DONE = 10.4;
        public const double TAPE_W = 0.048, TAPE_T = 0.0006, TAB = 0.06;

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
        public static double BoxX(double tau)
        {
            if (tau < 2.2) return 0;
            if (tau < 2.9) return 0.55 * Smooth((tau - 2.2) / 0.7);
            if (tau < FILL_DONE) return 0.55 + Trap(tau - 3.0, X_FILL - 0.55);
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
            if (tau < 1.3) return REST_Z + LIFT;
            return REST_Z + LIFT * (1 - Smooth((tau - 1.3) / 0.4));
        }

        public static double Theta(double tau)
        {
            if (tau < 0.4) return Rad(Lerp(2.0, 10.0, Smooth(tau / 0.4)));
            return Rad(Lerp(10.0, 90.0, Smooth((tau - 0.4) / 0.9)));
        }

        // (아래 단변, 아래 장변, 위 앞 단변, 위 뒤 단변, 위 장변)
        public static void FlapAngles(double tau, out double bMinor, out double bMajor, out double lead, out double trail, out double major)
        {
            double X = BoxX(tau);
            bMinor = HALF_PI * Smooth((tau - 1.7) / 0.5);
            bMajor = HALF_PI * Smooth((tau - 2.2) / 0.45);
            double b = SPLAY * Smooth((tau - 0.6) / 0.7);
            double k = HALF_PI - SPLAY;
            lead = b + k * Smooth((X - 2.70) / 0.25);
            trail = b + k * Smooth((X - 2.98) / 0.20);
            major = b + k * Smooth((X - 3.25) / 0.45);
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

        // ------------------------------------------------------------ 상품 8개
        static readonly int[,] PackOrder = { { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 } };

        public static bool PackPose(double tau, int j, out D3 pos, out double ang)
        {
            int sx = PackOrder[j % 4, 0], sy = PackOrder[j % 4, 1], layer = j / 4;
            double ox = sx * 0.098, oy = sy * 0.072;
            double ts = 7.40 + 0.30 * j;
            double X = BoxX(tau);
            var S = new D3(X_FILL + ox, CHUTE_SY, CHUTE_SZ); var E = new D3(X_FILL + ox, CHUTE_EY, CHUTE_EZ);
            var G = new D3(X + ox, oy, REST_Z + PACK.z / 2 + layer * PACK.z);
            if (tau < ts) { pos = S; ang = CHUTE_A; return false; }
            double u = (tau - ts) / 0.5;
            if (u < 1)
            {
                u = u * u;
                pos = new D3(Lerp(S.x, E.x, u), Lerp(S.y, E.y, u), Lerp(S.z, E.z, u)); ang = CHUTE_A; return true;
            }
            u = (tau - ts - 0.5) / 0.3;
            if (u < 1)
            {
                pos = new D3(Lerp(E.x, G.x, u), Lerp(E.y, G.y, u), Lerp(E.z, G.z, u * u)); ang = CHUTE_A * (1 - Smooth(u)); return true;
            }
            pos = G; ang = 0; return true;
        }

        // ------------------------------------------------------------ 기계 가동부
        public static void Cycle(double t, out long k, out double tau)
        {
            k = (long)Math.Floor((t - PHASE) / PERIOD);
            tau = t - PHASE - k * PERIOD;
        }

        public static D3 PickArm(double t)
        {
            Cycle(t, out _, out double tau);
            if (tau < 1.75) return new D3(0, BoxY(tau), BoxZ(tau));
            if (tau < 2.05) return new D3(0, 0.06 * Smooth((tau - 1.75) / 0.3), REST_Z);
            if (tau < 4.3) return new D3(0, Lerp(0.06, MAG_Y + 0.06, Smooth((tau - 2.05) / 2.25)), REST_Z);
            return new D3(0, Lerp(MAG_Y + 0.06, MAG_Y, Smooth((tau - 4.3) / 0.7)), REST_Z);
        }

        public static double PusherX(double t)
        {
            Cycle(t, out _, out double tau);
            if (tau < 1.95) return PUSH_REST;
            if (tau < 2.2) return Lerp(PUSH_REST, -L / 2 - 0.012, Smooth((tau - 1.95) / 0.25));
            if (tau < 2.9) return BoxX(tau) - L / 2 - 0.012;
            return Lerp(BoxX(2.9) - L / 2 - 0.012, PUSH_REST, Smooth((tau - 2.9) / 0.7));
        }

        public static double KickerAngle(double t)
        {
            double best = 0;
            long k0 = (long)Math.Floor((t - PHASE) / PERIOD);
            for (long k = k0 - 7; k <= k0; k++)
            {
                double tau = t - PHASE - k * PERIOD;
                if (tau < 0 || tau > LIFE) continue;
                double X = BoxX(tau);
                if (X > 2.8 && X < 3.5)
                {
                    double a = Rad(115) * Smooth((X - 2.94) / 0.2) * (1 - Smooth((X - 3.22) / 0.14));
                    best = Math.Max(best, a);
                }
            }
            return best;
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
