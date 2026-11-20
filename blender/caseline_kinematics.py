"""
반자동 제함기 → 컨베이어(충전·상부 접지) → 반자동 봉함기(상하 테이핑) 라인의 동작 정의.
Blender (caseline_blender.py) 와 Unity (Assets/CaseLine/Scripts/CaseLineKinematics.cs) 가 같은 식을 씁니다.
한쪽을 고치면 다른 쪽도 같이 고치고  python3 tools/caseline_dump.py  ↔  Unity 결과를 비교하세요.

좌표: Blender 기준 (x = 흐름 방향, y = 안쪽(작업자 반대편), z = 위). 단위 m, s.
박스 k 는 t_k = PHASE + k * PERIOD 에 매거진에서 집혀 나오며, 박스별 국소 시간 tau = t - t_k.
"""
import math

# ---------------------------------------------------------------- 치수
TABLE = 0.75            # 제함기 베드 / 컨베이어 / 봉함기 롤러 상면 높이
L, W, H = 0.40, 0.30, 0.30   # 박스 안치수 (RSC, 길이 x 폭 x 높이)
F = W / 2               # 날개(플랩) 길이 = 폭의 절반 (RSC)
T = 0.004               # 골판지 두께
REST_Z = TABLE + T      # 박스 원점(벽 아래 모서리 높이)이 베드/롤러 위에 있을 때
PERIOD = 5.0            # 박스 간격 (s) — 제함기 최대 700 box/h(5.1 s) 에 맞춤
PHASE = 0.0
LIFE = 32.6             # 박스가 커튼 너머(X_END)로 사라지는 국소 시간
SPLAY = -0.30           # 상부 날개가 바깥으로 벌어진 각 (rad)

PACK = (0.185, 0.135, 0.12)  # 상품 단상자 (2 x 2 x 2단 = 8개)
X_FILL = 1.90           # 충전 정지 위치 (박스 중심)
X_END = 9.50            # 출구 컨베이어 끝 (벽 커튼 뒤)
V = 0.35                # 컨베이어 / 봉함기 벨트 속도 (m/s, 21 m/min)
ACC = 0.8               # 가감속 (m/s^2)
X_HEAD = 4.30           # 봉함기 테이프 헤드 (상·하) 위치
MAG_Y = 0.55            # 매거진에서 박스 원점 y (L1 면 = MAG_Y + W/2)
LIFT = 0.20             # 집어 올리는 높이
X_BAR = 2.78            # 앞 날개 접는 고정 바
X_KICK = 2.62           # 뒤 날개 킥커 회전축
KICK_Z = REST_Z + H + 0.18
CHUTE_S = (1.05, TABLE + 0.95)   # 슈트 시작 (y, z)  — 상품 공급 터널 안
CHUTE_E = (0.30, TABLE + 0.62)   # 슈트 끝 (박스 위)
CHUTE_A = math.atan2(CHUTE_S[1] - CHUTE_E[1], CHUTE_S[0] - CHUTE_E[0])
ROLLER_R = 0.025
PUSH_REST = -0.32       # 푸셔 판 대기 위치 (x)

HALF_PI = math.pi / 2

def clamp01(u): return 0.0 if u < 0 else 1.0 if u > 1 else u
def smooth(u):
    u = clamp01(u); return u * u * (3 - 2 * u)
def lerp(a, b, u): return a + (b - a) * u

def trap(t, d, v=V, a=ACC):
    """사다리꼴 속도 이동: 거리 d 를 최고속 v, 가속 a 로 갈 때 t 초 후 이동 거리"""
    if t <= 0: return 0.0
    ta = v / a; tt = d / v + ta
    if t < ta: return 0.5 * a * t * t
    if t < tt - ta: return 0.5 * a * ta * ta + v * (t - ta)
    if t < tt: return d - 0.5 * a * (tt - t) ** 2
    return d

# ---------------------------------------------------------------- 박스 원점 경로
FILL_DONE = 10.4        # 8개 투입이 끝나고 출발하는 시각

def box_x(tau):
    if tau < 2.2: return 0.0
    if tau < 2.9: return 0.55 * smooth((tau - 2.2) / 0.7)
    if tau < FILL_DONE: return 0.55 + trap(tau - 3.0, X_FILL - 0.55)
    return X_FILL + trap(tau - FILL_DONE, X_END - X_FILL)

def box_y(tau):
    if tau < 0.4: return MAG_Y
    return MAG_Y * (1 - smooth((tau - 0.4) / 0.9))

def box_z(tau):
    if tau < 0.4: return REST_Z + LIFT * smooth(tau / 0.4)
    if tau < 1.3: return REST_Z + LIFT
    return REST_Z + LIFT * (1 - smooth((tau - 1.3) / 0.4))

def theta(tau):
    """몸통 펼침 각 (0 = 납작, 90도 = 직사각형)"""
    if tau < 0.4: return math.radians(lerp(2.0, 10.0, smooth(tau / 0.4)))
    return math.radians(lerp(10.0, 90.0, smooth((tau - 0.4) / 0.9)))

def flap_angles(tau):
    """(아래 단변, 아래 장변, 위 앞 단변(W1, +x), 위 뒤 단변(W2), 위 장변) 접힘 각. 0 = 펼침, pi/2 = 안쪽으로 접힘"""
    X = box_x(tau)
    b_minor = HALF_PI * smooth((tau - 1.7) / 0.5)
    b_major = HALF_PI * smooth((tau - 2.2) / 0.45)
    base = SPLAY * smooth((tau - 0.6) / 0.7)
    k = HALF_PI - SPLAY
    lead = base + k * smooth((X - 2.70) / 0.25)
    trail = base + k * smooth((X - 2.98) / 0.20)
    major = base + k * smooth((X - 3.25) / 0.45)
    return b_minor, b_major, lead, trail, major

# ---------------------------------------------------------------- 박스 판 (벽 4 + 날개 8)
# 각 판: (이름, 중심(박스 원점 기준), X축, Y축, Z축, 크기(sx, sy, sz)) — 축은 오른손 직교, 판은 원점 중심 상자
def _v(a, b, c): return (a, b, c)
def _add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def _mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def _cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

PANEL_NAMES = ["L1", "W1", "L2", "W2"]     # L1: 뒤(+y) 장변, W1: 앞(+x) 단변, L2: 앞(-y) 장변, W2: 뒤(-x) 단변
SLAB_NAMES = ([f"Wall_{p}" for p in PANEL_NAMES] + [f"Bot_{p}" for p in PANEL_NAMES] + [f"Top_{p}" for p in PANEL_NAMES])

def box_slabs(tau):
    th = theta(tau)
    c, s = math.cos(th), math.sin(th)
    d = (-c, -s)
    P0 = (-L / 2, W / 2); P1 = (L / 2, W / 2)
    P2 = (P1[0] + W * d[0], P1[1] + W * d[1]); P3 = (P0[0] + W * d[0], P0[1] + W * d[1])
    pts = [P0, P1, P2, P3]
    lens = [L, W, L, W]
    bmi, bma, lead, trail, tma = flap_angles(tau)
    bot = [bma, bmi, bma, bmi]
    top = [tma, lead, tma, trail]
    out = []
    walls, bots, tops = [], [], []
    for i in range(4):
        a = pts[i]; b = pts[(i + 1) % 4]; ln = lens[i]
        e = ((b[0] - a[0]) / ln, (b[1] - a[1]) / ln, 0.0)
        n = (e[1], -e[0], 0.0)                     # 안쪽 법선 (시계 방향 순서)
        out_n = _mul(n, -1)
        major = i % 2 == 0
        # 벽: 끝(b 쪽)으로 T 만큼 늘려 모서리 틈을 메움, 두께는 바깥쪽
        mid = (a[0] + e[0] * (ln + T) / 2, a[1] + e[1] * (ln + T) / 2, 0.0)
        cen = _add(_add(mid, _mul(out_n, T / 2)), (0, 0, H / 2))
        walls.append((f"Wall_{PANEL_NAMES[i]}", cen, e, out_n, (0, 0, 1), (ln + T, T, H)))
        hinge_mid = (a[0] + e[0] * ln / 2, a[1] + e[1] * ln / 2)
        flen = ln - (0.004 if major else 0.012)     # 날개는 홈(슬롯) 만큼 짧음
        for kind, phi in (("Bot", bot[i]), ("Top", top[i])):
            cp, sp = math.cos(phi), math.sin(phi)
            if kind == "Bot":
                f = _add(_mul((0, 0, -1), cp), _mul(n, sp))
                m = _add(_mul(n, cp), _mul((0, 0, 1), sp))
                hz = 0.0
            else:
                f = _add(_mul((0, 0, 1), cp), _mul(n, sp))
                m = _add(_mul(n, cp), _mul((0, 0, -1), sp))
                hz = H
            side = -1 if major else 1                 # 장변 날개는 바깥, 단변 날개는 안쪽
            cen = _add(_add((hinge_mid[0], hinge_mid[1], hz), _mul(f, F / 2)), _mul(m, side * T / 2))
            Y = _cross(f, e)
            (bots if kind == "Bot" else tops).append((f"{kind}_{PANEL_NAMES[i]}", cen, e, Y, f, (flen, T, F)))
    return walls + bots + tops

# ---------------------------------------------------------------- 테이프 (상·하 본선 + 앞/뒤 탭)
TAPE_W, TAPE_T, TAB = 0.048, 0.0006, 0.06
TAPE_NAMES = ["Tape_Top", "Tape_Bot", "Tab_FrontTop", "Tab_FrontBot", "Tab_RearTop", "Tab_RearBot"]

def tape_parts(tau):
    """[(이름, 보임, 중심, 크기)] 박스 원점 기준, 축 정렬"""
    X = box_x(tau)
    a = max(-L / 2, min(L / 2, X_HEAD - X))
    on = X + L / 2 > X_HEAD
    ln = L / 2 + T - a if on else 0.0
    ztop = H + T + TAPE_T / 2; zbot = -T - TAPE_T / 2
    xf = L / 2 + T + TAPE_T / 2
    rear = X - L / 2 > X_HEAD + 0.005
    cx = (a + L / 2 + T) / 2
    return [
        ("Tape_Top", on, (cx, 0, ztop), (max(ln, 1e-4), TAPE_W, TAPE_T)),
        ("Tape_Bot", on, (cx, 0, zbot), (max(ln, 1e-4), TAPE_W, TAPE_T)),
        ("Tab_FrontTop", on, (xf, 0, H + T - TAB / 2), (TAPE_T, TAPE_W, TAB)),
        ("Tab_FrontBot", on, (xf, 0, -T + TAB / 2), (TAPE_T, TAPE_W, TAB)),
        ("Tab_RearTop", rear, (-xf, 0, H + T - TAB / 2), (TAPE_T, TAPE_W, TAB)),
        ("Tab_RearBot", rear, (-xf, 0, -T + TAB / 2), (TAPE_T, TAPE_W, TAB)),
    ]

# ---------------------------------------------------------------- 상품 8개
PACK_ORDER = [(-1, -1), (1, -1), (-1, 1), (1, 1)]

def pack_pose(tau, j):
    """(보임, 월드 중심, x축 기준 기울기 rad). 슈트 → 낙하 → 박스와 함께 이동"""
    sx, sy = PACK_ORDER[j % 4]; layer = j // 4
    ox, oy = sx * 0.098, sy * 0.072
    ts = 7.40 + 0.30 * j
    X = box_x(tau)
    S = (X_FILL + ox, CHUTE_S[0], CHUTE_S[1]); E = (X_FILL + ox, CHUTE_E[0], CHUTE_E[1])
    G = (X + ox, oy, REST_Z + PACK[2] / 2 + layer * PACK[2])
    if tau < ts: return False, S, CHUTE_A
    u = (tau - ts) / 0.5
    if u < 1:
        u = u * u
        return True, (lerp(S[0], E[0], u), lerp(S[1], E[1], u), lerp(S[2], E[2], u)), CHUTE_A
    u = (tau - ts - 0.5) / 0.3
    if u < 1:
        return True, (lerp(E[0], G[0], u), lerp(E[1], G[1], u), lerp(E[2], G[2], u * u)), CHUTE_A * (1 - smooth(u))
    return True, G, 0.0

# ---------------------------------------------------------------- 기계 가동부
def cycle(t):
    """현재 제함 사이클 (집기·푸셔용): (k, tau)"""
    k = math.floor((t - PHASE) / PERIOD)
    return k, t - PHASE - k * PERIOD

def pick_arm(t):
    """흡착 암 가상 원점 (박스 원점과 같은 규약). 흡착판은 L1 바깥 면 (+y)"""
    _, tau = cycle(t)
    if tau < 1.75: return (0.0, box_y(tau), box_z(tau))
    if tau < 2.05: return (0.0, 0.06 * smooth((tau - 1.75) / 0.3), REST_Z)
    if tau < 4.3: return (0.0, lerp(0.06, MAG_Y + 0.06, smooth((tau - 2.05) / 2.25)), REST_Z)
    return (0.0, lerp(MAG_Y + 0.06, MAG_Y, smooth((tau - 4.3) / 0.7)), REST_Z)

def pusher_x(t):
    _, tau = cycle(t)
    if tau < 1.95: return PUSH_REST
    if tau < 2.2: return lerp(PUSH_REST, -L / 2 - 0.012, smooth((tau - 1.95) / 0.25))
    if tau < 2.9: return box_x(tau) - L / 2 - 0.012
    return lerp(box_x(2.9) - L / 2 - 0.012, PUSH_REST, smooth((tau - 2.9) / 0.7))

def kicker_angle(t):
    """뒤 날개 킥커 팔 (y축 회전 rad, 0 = 위로 세움). 영역 안 박스 기준"""
    best = 0.0
    k0 = math.floor((t - PHASE) / PERIOD)
    for k in range(k0 - 7, k0 + 1):
        tau = t - PHASE - k * PERIOD
        if tau < 0 or tau > LIFE: continue
        X = box_x(tau)
        if 2.8 < X < 3.5:
            a = math.radians(115) * smooth((X - 2.94) / 0.2) * (1 - smooth((X - 3.22) / 0.14))
            best = max(best, a)
    return best

def roller_angle(t):
    return V / ROLLER_R * t

def boxes_alive(t):
    """화면에 있을 수 있는 박스 k 목록 (매거진 대기 박스 포함: tau in [-PERIOD, LIFE])"""
    k0 = math.floor((t - PHASE) / PERIOD)
    return [k for k in range(k0 - 7, k0 + 2) if -PERIOD <= t - PHASE - k * PERIOD <= LIFE]

def box_pose(tau):
    """(보임, 원점 위치). 매거진 대기 중(tau<0)이면 납작한 상태로 매거진 맨 앞"""
    if tau < -PERIOD or tau > LIFE: return False, (0, 0, 0)
    tc = max(tau, 0.0)
    return True, (box_x(tc), box_y(tc), box_z(tc))
