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
ACC_PUSH = 1.0          # 푸셔가 밀어 컨베이어 속도까지 올리는 가속
X_HEAD = 4.30           # 봉함기 테이프 헤드 (상·하) 위치
MAG_Y = 0.55            # 매거진에서 박스 원점 y
LIFT = 0.20             # 집어 올리는 높이
ARM_UP = 0.40           # 흡착 암 복귀 높이 (세운 박스 날개 위로)
X_BAR = 2.78            # 앞 날개 접는 중앙 레일 시작 (높이 RAIL_Z0 → RAIL_Z1)
RAIL_LEN = 0.22         # 레일이 내려오는 구간
RAIL_END = 3.95
RAIL_Z0, RAIL_Z1, RAIL_R = 0.10, 0.014, 0.008   # 박스 윗면 기준 레일 중심 높이, 봉 반지름
X_KICK = 2.70           # 뒤 날개 킥커 회전축 (박스 윗면 위 KICK_H)
KICK_H, KICK_R = 0.36, 0.38
KICK_START = X_KICK - 0.26      # 킥 시작 시 박스 뒤끝 x
KICK_LEN = 0.10                 # 킥 동안 박스 이동 거리
KICK_PARK = math.radians(-60)   # 대기 각 (위쪽 뒤로)
KICK_Z = REST_Z + H + KICK_H
PLOW_X0, PLOW_X1 = 3.25, 3.80   # 장변 날개 플로 레일 (|y| 0.20 → 0.045, 윗면 위 0.16 → 0.014)
PLOW_Y0, PLOW_Y1, PLOW_Z0, PLOW_Z1 = 0.20, 0.045, 0.16, 0.014
CHUTE_S = (1.05, TABLE + 0.95)   # 슈트 시작 (y, z)  — 상품 공급 터널 안
CHUTE_E = (0.30, TABLE + 0.62)   # 슈트 끝 (박스 위)
CHUTE_A = math.atan2(CHUTE_S[1] - CHUTE_E[1], CHUTE_S[0] - CHUTE_E[0])
ROLLER_R = 0.025
PUSH_REST = -0.25       # 푸셔 판 대기 위치 (x)
PUSH_END = 0.24         # 푸셔 스트로크 끝 (박스 중심 x = 0.452 에서 롤러에 넘김)
G = 9.81

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
# 흡착 암은 블랭크 앞면(L2, 제함기 쪽)을 잡음. 펼치는 동안 L2 는 x 가 그대로라 박스 원점 x = W cos(theta)
T_OPEN0, T_OPEN1 = 0.9, 1.5     # 몸통 펼침 (적재 더미에서 충분히 떨어진 뒤)
T_DOWN0, T_DOWN1 = 1.5, 1.9     # 베드로 내림
T_PUSH = 2.45                   # 푸셔 출발
FILL_ARRIVE = T_PUSH + X_FILL / V + V / ACC_PUSH
PACK_T0, PACK_DT, PACK_SLIDE = FILL_ARRIVE - 0.55, 0.25, 0.7
FILL_DONE = PACK_T0 + 7 * PACK_DT + PACK_SLIDE + 0.45

def theta(tau):
    """몸통 펼침 각 (0 = 납작, 90도 = 직사각형)"""
    return math.radians(lerp(2.0, 90.0, smooth((tau - T_OPEN0) / (T_OPEN1 - T_OPEN0))))

def push_x(tau):
    """푸셔가 밀기 시작한 뒤 박스 중심 x (가속 후 컨베이어 속도로 이어짐 → 충전 위치에서 정지)"""
    d = X_FILL
    ta = V / ACC_PUSH; tt = d / V + ta
    t = tau - T_PUSH
    if t <= 0: return 0.0
    if t < ta: return 0.5 * ACC_PUSH * t * t
    if t < tt - ta: return 0.5 * ACC_PUSH * ta * ta + V * (t - ta)
    if t < tt: return d - 0.5 * ACC_PUSH * (tt - t) ** 2
    return d

def box_x(tau):
    if tau < T_PUSH: return W * math.cos(theta(tau))
    if tau < FILL_DONE: return push_x(tau)
    return X_FILL + trap(tau - FILL_DONE, X_END - X_FILL)

def box_y(tau):
    if tau < 0.4: return MAG_Y
    return MAG_Y * (1 - smooth((tau - 0.4) / 0.9))

def box_z(tau):
    if tau < 0.4: return REST_Z + LIFT * smooth(tau / 0.4)
    if tau < T_DOWN0: return REST_Z + LIFT
    return REST_Z + LIFT * (1 - smooth((tau - T_DOWN0) / (T_DOWN1 - T_DOWN0)))

# ---------------------------------------------------------------- 위 날개 접촉
def _rail_z(x):
    return RAIL_Z1 + (RAIL_Z0 - RAIL_Z1) * (1 - smooth((x - X_BAR) / RAIL_LEN))

def lead_contact(front):
    """앞 단변 날개 (뒤쪽으로 접힘): 중앙 레일 아래에 있으려면 필요한 최소 각"""
    best = -10.0
    x0 = max(X_BAR, front - F); x1 = min(RAIL_END, front + F)
    n = 40
    if x1 < x0: return best
    for i in range(n + 1):
        xr = x0 + (x1 - x0) * i / n
        s = front - xr; z = _rail_z(xr) - RAIL_R
        if s * s + z * z <= F * F:
            best = max(best, math.atan2(s, z))
    return best

def plow_contact(X):
    """장변 날개 (안쪽으로 접힘): 플로 레일 중 박스 길이 안에서 가장 앞선 점 기준"""
    best = -10.0
    x0 = max(PLOW_X0, X - L / 2); x1 = min(PLOW_X1 + 0.15, X + L / 2)
    n = 30
    if x1 < x0: return best
    for i in range(n + 1):
        xp = x0 + (x1 - x0) * i / n
        u = clamp01((xp - PLOW_X0) / (PLOW_X1 - PLOW_X0))
        s = W / 2 - lerp(PLOW_Y0, PLOW_Y1, u); z = lerp(PLOW_Z0, PLOW_Z1, u) - RAIL_R
        if s * s + z * z <= F * F:
            best = max(best, math.atan2(s, z))
    return best

def kick_u(X):
    return clamp01((X - L / 2 - KICK_START) / KICK_LEN)

def trail_angle(X, base):
    """뒤 단변 날개: 킥커 팔 끝이 날개를 눌러 앞으로 접음, 접힌 뒤엔 그대로 (중앙 레일이 이어 받음)"""
    u = kick_u(X)
    return base + (HALF_PI - base) * smooth(u)

def kick_arm_for(X, base):
    """킥 중일 때 팔 끝이 날개 위에 닿는 팔 각 (y 축 회전, 0 = 위, + = 앞으로). 닿는 해가 없으면 None"""
    rear = X - L / 2
    phi = trail_angle(X, base)
    s, c = math.sin(phi), math.cos(phi)
    ax, az = rear - X_KICK, -KICK_H
    b = ax * s + az * c; cc = ax * ax + az * az - KICK_R * KICK_R; D = b * b - cc
    if D < 0: return None
    rs = [r for r in (-b - math.sqrt(D), -b + math.sqrt(D)) if 0.0 <= r <= F + 1e-9]
    if not rs: return None
    r = max(rs)
    qx, qz = rear + r * s, r * c
    return math.atan2(qx - X_KICK, qz - KICK_H)

def flap_angles(tau):
    """(아래 단변, 아래 장변, 위 앞 단변(W1, +x), 위 뒤 단변(W2), 위 장변) 접힘 각. 0 = 펼침, pi/2 = 안쪽으로 접힘"""
    X = box_x(tau)
    b_minor = HALF_PI * smooth((tau - 1.95) / 0.35)
    b_major = HALF_PI * smooth((X - 0.02) / 0.28) if tau >= T_PUSH else 0.0
    base = SPLAY * smooth((tau - 1.0) / 0.6)
    front = X + L / 2
    lead = max(base, lead_contact(front), base + (HALF_PI - base) * smooth((front - X_BAR - 0.30) / 0.15))
    lead = min(lead, HALF_PI)
    trail = trail_angle(X, base)
    major = min(HALF_PI, max(base, plow_contact(X), base + (HALF_PI - base) * smooth((X - 3.70) / 0.15)))
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
CHUTE_D = math.hypot(CHUTE_S[0] - CHUTE_E[0], CHUTE_S[1] - CHUTE_E[1])
CHUTE_ACC = 2 * CHUTE_D / (PACK_SLIDE * PACK_SLIDE)      # 슈트 위 등가속 (정지 → 끝 속도)
CHUTE_VE = CHUTE_ACC * PACK_SLIDE

def pack_pose(tau, j):
    """(보임, 월드 중심, x축 기준 기울기 rad). 슈트에서 등가속으로 미끄러진 뒤 그 속도로 포물선 낙하 → 박스와 함께 이동"""
    sx, sy = PACK_ORDER[j % 4]; layer = j // 4
    ox, oy = sx * 0.098, sy * 0.072
    ts = PACK_T0 + PACK_DT * j
    X = box_x(tau)
    S = (X_FILL + ox, CHUTE_S[0], CHUTE_S[1]); E = (X_FILL + ox, CHUTE_E[0], CHUTE_E[1])
    G_ = (X + ox, oy, REST_Z + PACK[2] / 2 + layer * PACK[2])
    if tau < ts: return False, S, CHUTE_A
    t = tau - ts
    if t < PACK_SLIDE:
        u = 0.5 * CHUTE_ACC * t * t / CHUTE_D
        return True, (lerp(S[0], E[0], u), lerp(S[1], E[1], u), lerp(S[2], E[2], u)), CHUTE_A
    vz = CHUTE_VE * math.sin(CHUTE_A); dz = E[2] - G_[2]
    tf = (-vz + math.sqrt(vz * vz + 2 * G * dz)) / G           # 낙하 시간
    t -= PACK_SLIDE
    if t < tf:
        u = t / tf
        z = E[2] - vz * t - 0.5 * G * t * t
        return True, (lerp(E[0], G_[0], u), lerp(E[1], G_[1], u), z), CHUTE_A * (1 - smooth(u))
    return True, G_, 0.0

# ---------------------------------------------------------------- 기계 가동부
def cycle(t):
    """현재 제함 사이클 (집기·푸셔용): (k, tau)"""
    k = math.floor((t - PHASE) / PERIOD)
    return k, t - PHASE - k * PERIOD

def grip_y(tau):
    """흡착면 = 블랭크 앞면(L2) 바깥 면 y"""
    return box_y(tau) + W / 2 - W * math.sin(theta(tau)) - T

ARM_Y_MAG = MAG_Y + W / 2 - W * math.sin(math.radians(2.0)) - T   # 매거진 맨 앞 블랭크 앞면

def pick_arm(t):
    """흡착 암 기준점 = 흡착컵 앞면 중심 (x, y, z: z 는 박스 원점 높이 규약, 컵은 그 위 H/2)"""
    _, tau = cycle(t)
    y0 = grip_y(2.3)
    if tau < 2.3: return (0.0, grip_y(tau), box_z(tau))
    if tau < 2.5: return (0.0, y0 - 0.06 * smooth((tau - 2.3) / 0.2), REST_Z)
    if tau < 3.0: return (0.0, y0 - 0.06, REST_Z + ARM_UP * smooth((tau - 2.5) / 0.5))
    if tau < 4.1: return (0.0, lerp(y0 - 0.06, ARM_Y_MAG - 0.05, smooth((tau - 3.0) / 1.1)), REST_Z + ARM_UP)
    if tau < 4.55: return (0.0, ARM_Y_MAG - 0.05, REST_Z + ARM_UP * (1 - smooth((tau - 4.1) / 0.45)))
    return (0.0, lerp(ARM_Y_MAG - 0.05, ARM_Y_MAG, smooth((tau - 4.55) / 0.45)), REST_Z)

def pusher_x(t):
    _, tau = cycle(t)
    contact = -L / 2 - 0.012
    if tau < 2.15: return PUSH_REST
    if tau < T_PUSH: return lerp(PUSH_REST, contact, smooth((tau - 2.15) / (T_PUSH - 2.15)))
    p = min(box_x(tau) + contact, PUSH_END)
    if tau < 3.95: return p
    return lerp(PUSH_END, PUSH_REST, smooth((tau - 3.95) / 0.6))

def kicker_angle(t):
    """뒤 날개 킥커 팔 (y축 회전 rad, 0 = 위, - = 뒤로). 대기 → 날개 끝에 닿기 → 날개를 따라 누름 → 복귀"""
    k0 = math.floor((t - PHASE) / PERIOD)
    for k in range(k0 - 7, k0 + 1):
        tau = t - PHASE - k * PERIOD
        if tau < 0 or tau > LIFE: continue
        X = box_x(tau)
        rear = X - L / 2
        if rear < KICK_START - 0.12 or rear > KICK_START + KICK_LEN + 0.14: continue
        base = SPLAY
        a_first = kick_arm_for(KICK_START + L / 2, base)
        a_last = kick_arm_for(KICK_START + KICK_LEN + L / 2, base)
        if rear < KICK_START:                     # 날개 끝으로 내려옴
            return lerp(KICK_PARK, a_first, smooth((rear - (KICK_START - 0.12)) / 0.12))
        if rear <= KICK_START + KICK_LEN:
            a = kick_arm_for(X, base)
            return a if a is not None else a_last
        return lerp(a_last, KICK_PARK, smooth((rear - KICK_START - KICK_LEN) / 0.14))
    return KICK_PARK

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
