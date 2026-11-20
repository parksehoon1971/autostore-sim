/*
 * Exotec Skypod 방식 "바닥 주행 + 랙 등반" 로봇 창고 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/Skypod/Scripts/SkypodSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 *
 * 구조: 랙 줄(y 짝수)과 통로(y 홀수)가 번갈아 있고, 로봇은 바닥을 달리다(랙 아래로도 지나감)
 * 목표 통로 칸에서 양옆 랙 기둥을 잡고 수직으로 올라가 토트를 옆으로 꺼내거나 넣습니다.
 * 꺼낸 토트를 앞쪽 피킹 스테이션까지 실어 오고, 작업자가 피킹하면 빈 자리로 다시 넣습니다.
 *
 * 바닥 격자: x = -1..nx (x=-1 왼쪽 진입 통로, x=nx 오른쪽 복귀 통로), y = -2..2K
 *   y = -2  스테이션 줄 (대기 칸 + 피킹 칸)   y = -1  앞쪽 주행로 (-x 방향)
 *   y 짝수  랙 줄: 맨 앞·맨 뒤 줄은 로봇 주차, 가운데 줄은 랙 아래 되돌이 길 (-x)
 *   y 홀수  통로 (등반 칸, +x 방향 일방통행)
 * 순환: 앞쪽 주행로(-x) → 왼쪽 진입로(+y) → 통로(+x) → 오른쪽 복귀로(-y) → 앞쪽 주행로
 *       통로 → (옆으로) 랙 아래 되돌이 길(-x) → 왼쪽 진입로 (스테이션을 거치지 않고 다른 통로로)
 * 좌표: wx = (x+0.5)*cellX, wy = rowY(y) (랙 0번 줄 앞면 = 0), z 위. 시간 s, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 16, aisles: 4, levels: 20,        // 통로 길이(칸), 통로 수, 랙 단 수
    cellX: 0.72,                          // 랙 칸 폭 = 기둥 간격 (토트 650 mm + 여유, 가정)
    aisleW: 0.80, rackD: 0.47, flue: 0.04, // 통로 폭, 한쪽 랙 깊이 (토트 450 mm + 여유), 등지는 랙 사이 틈 (가정)
    level0Z: 1.25, levelH: 0.42,          // 맨 아래 단 높이 (로봇이 아래로 지나감), 단 간격 (가정)
    toteW: 0.65, toteD: 0.45, toteH: 0.32, // 토트 650 x 450, 높이 320 mm (공개 자료 3종 중 가운데)
    bayZ: 0.40,                           // 로봇 몸체 안 토트 받침 높이 (가정)
    fill: 0.85,
    robots: 20,
    stations: [1, 6, 11],                 // 스테이션 시작 x (피킹 1칸 + 대기 spurLen-1칸, y = -2 줄)
    spurLen: 5,
    frontY: -0.80, spurY: -1.75,          // 앞쪽 주행로 / 스테이션 줄 중심 y
    orderRate: 800,                       // 토트 요청 / 시간
    popularShare: 0.2, popularDemand: 0.8,
    vmax: 4.0, acc: 3.0,                  // 바닥 4 m/s (공개 자료), 가속도 (가정)
    turn: 0.35,                           // 방향 바꿀 때 바퀴 조향 정지 시간 (가정)
    climbV: 3.0, climbAcc: 2.0,           // 등반 속도 (가정. Exotec: "3차원으로 최대 4 m/s")
    engage: 0.4,                          // 랙 기둥 맞물림 / 풀림 (가정)
    armT: 1.1,                            // 텔레스코픽 암 한 번 펴기 또는 접기 (가정)
    pickMin: 4, pickMax: 8,               // 작업자 피킹 시간 / 토트 (스테이션당 최대 600 토트/h 공개 수치에 맞춤)
    seed: 3,
  };

  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function profileTime(d, v, a) {
    if (d <= 0) return 0;
    return d >= v * v / a ? d / v + v / a : 2 * Math.sqrt(d / a);
  }
  function profilePos(t, d, v, a) {
    const T = profileTime(d, v, a);
    if (t >= T) return d;
    if (t <= 0) return 0;
    let vp = v, ta = v / a;
    if (d < v * v / a) { vp = Math.sqrt(d * a); ta = vp / a; }
    if (t < ta) return 0.5 * a * t * t;
    const dA = 0.5 * a * ta * ta, tc = T - 2 * ta;
    if (t < ta + tc) return dA + vp * (t - ta);
    const td = T - t;
    return d - 0.5 * a * td * td;
  }

  const DIRS = [[1, 0], [-1, 0], [0, 1], [0, -1]];
  const VOID = 0, LANE = 1, AISLE = 2, PARK = 3, SPUR = 4, PICK = 5, UNDER = 6;

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      const K = c.aisles;
      this.Y1 = 2 * K;                         // 맨 뒤 랙 줄 y
      this.W = c.nx + 2; this.H = this.Y1 + 3; // x: -1..nx, y: -2..2K
      const N = this.W * this.H;
      this.N = N;
      this.type = new Uint8Array(N);
      this.stOf = new Int32Array(N).fill(-1);  // 스테이션 줄 칸의 스테이션 번호
      this.occ = new Int32Array(N).fill(-1);
      this.parkRes = new Int32Array(N).fill(-1);
      // 줄 중심 y
      this.rowYs = [];
      let acc = 0;
      for (let y = 0; y <= this.Y1; y++) {
        const w = this.rowW(y);
        this.rowYs.push(acc + w / 2); acc += w;
      }
      this.depthY = acc;
      // 칸 종류
      for (let y = -2; y <= this.Y1; y++) for (let x = -1; x <= c.nx; x++) {
        let t = VOID;
        if (y === -1) t = LANE;
        else if (y >= 0 && (x === -1 || x === c.nx) && y < this.Y1) t = LANE;
        else if (y >= 0 && x >= 0 && x < c.nx) t = (y % 2 === 1) ? AISLE : (y === 0 || y === this.Y1) ? PARK : UNDER;
        this.type[this.ci(x, y)] = t;
      }
      this.stations = c.stations.map((x0, si) => {
        for (let k = 0; k < c.spurLen; k++) { const i = this.ci(x0 + k, -2); this.type[i] = k === 0 ? PICK : SPUR; this.stOf[i] = si; }
        return { id: si, x0, load: 0, busyTime: 0, picked: 0, cur: -1, pickT: 0 };
      });
      // 다음 칸 목록 (일방통행 그래프)
      this.succ = [];
      for (let i = 0; i < N; i++) this.succ.push([]);
      for (let y = -2; y <= this.Y1; y++) for (let x = -1; x <= c.nx; x++) {
        const i = this.ci(x, y), t = this.type[i], s = this.succ[i];
        if (t === VOID) continue;
        const add = (d) => { const nx = x + DIRS[d][0], ny = y + DIRS[d][1]; if (this.typeAt(nx, ny) !== VOID) s.push(this.ci(nx, ny) * 4 + d); };
        if (y === -1) {
          for (const st of this.stations) if (x === st.x0 + c.spurLen - 1) add(3);
          if (x > -1) add(1); else add(2);
        } else if (y === -2) {
          if (t === PICK) add(2); else add(1);
        } else if (x === -1) {
          if (y % 2 === 1) add(0);
          if (y < this.Y1 - 1) add(2);
        } else if (x === c.nx) {
          add(3);
        } else if (t === AISLE) {
          add(0); add(3); add(2);
        } else if (t === PARK) {
          add(y === 0 ? 2 : 3);
        } else if (t === UNDER) {
          add(1);
        }
      }
      // 토트 자리: (통로 a, 칸 x, 쪽 s(0 = -y, 1 = +y), 단 l)
      this.nSlots = K * c.nx * 2 * c.levels;
      this.slotTote = new Int32Array(this.nSlots).fill(-1);
      this.slotRes = new Uint8Array(this.nSlots);
      const cand = [];
      for (let id = 0; id < this.nSlots; id++) {
        if (this.rand() < c.fill) cand.push({ id, key: this.slotCost(id) + this.gauss() * 3.0 });
      }
      cand.sort((a, b) => a.key - b.key || a.id - b.id);
      this.toteLoc = [];   // { kind: "slot", s } | { kind: "robot", r }
      cand.forEach((s, id) => { this.slotTote[s.id] = id; this.toteLoc[id] = { kind: "slot", s: s.id }; });
      this.totes = cand.length;
      this.nPopular = Math.max(1, Math.floor(this.totes * c.popularShare));
      this.busy = new Uint8Array(this.totes);   // 요청됐거나 옮기는 중
      // 로봇: 랙 아래 주차 칸에서 시작
      this.robots = [];
      for (let i = 0; i < c.robots; i++) {
        let x, y, k;
        do { x = Math.floor(this.rand() * c.nx); y = this.rand() < 0.5 ? 0 : this.Y1; k = this.ci(x, y); }
        while (this.occ[k] !== -1);
        const r = { id: i, x, y, px: this.worldX(x), py: this.rowY(y), pz: 0, arm: 0, side: 1, carry: -1,
          acts: [], seg: null, lastDir: -1, waitT: 0, blocked: 0, busyTime: 0, blockedTime: 0, climbs: 0, climbH: 0,
          dist: 0, state: "idle", order: null };
        this.occ[k] = i; this.parkRes[k] = i; this.robots.push(r);
      }
      this.orders = []; this.pending = [];
      this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { done: 0, leadSum: 0, leadMax: 0, retrieves: 0 };
    }

    // ------------------------------------------------------------ 기본
    gauss() { let u = 0; while (!u) u = this.rand(); const v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    ci(x, y) { return (x + 1) + (y + 2) * this.W; }
    cx(i) { return i % this.W - 1; }
    cy(i) { return Math.floor(i / this.W) - 2; }
    inb(x, y) { return x >= -1 && x <= this.cfg.nx && y >= -2 && y <= this.Y1; }
    typeAt(x, y) { return this.inb(x, y) ? this.type[this.ci(x, y)] : VOID; }
    rowW(y) { const c = this.cfg; if (y % 2 === 1) return c.aisleW; return (y === 0 || y === this.Y1) ? c.rackD + c.flue / 2 : 2 * c.rackD + c.flue; }
    rowY(y) { const c = this.cfg; if (y === -1) return c.frontY; if (y === -2) return c.spurY; return this.rowYs[y]; }
    worldX(x) { return (x + 0.5) * this.cfg.cellX; }
    levelZ(l) { return this.cfg.level0Z + l * this.cfg.levelH; }   // 토트 바닥 높이
    climbZ(l) { return this.levelZ(l) - this.cfg.bayZ; }            // 그 단에서 로봇 바닥 높이
    slotAisle(s) { return Math.floor(s / (this.cfg.nx * 2 * this.cfg.levels)); }
    slotX(s) { return Math.floor(s / (2 * this.cfg.levels)) % this.cfg.nx; }
    slotSide(s) { return Math.floor(s / this.cfg.levels) % 2; }
    slotLevel(s) { return s % this.cfg.levels; }
    slotId(a, x, side, l) { return ((a * this.cfg.nx + x) * 2 + side) * this.cfg.levels + l; }
    reach() { return this.cfg.aisleW / 2 + this.cfg.rackD / 2; }
    slotPos(s) {
      const a = this.slotAisle(s), y = this.rowY(2 * a + 1) + (this.slotSide(s) ? 1 : -1) * this.reach();
      return [this.worldX(this.slotX(s)), y, this.levelZ(this.slotLevel(s))];
    }
    // 스테이션 출구에서 그 자리까지 가서 넣고 내려오는 대략 시간 (초기 배치·빈 자리 고르기)
    slotCost(s) {
      const c = this.cfg, x0 = c.stations[Math.floor(c.stations.length / 2)];
      const ay = 2 * this.slotAisle(s) + 1;
      const d = (x0 + 1) * c.cellX + (this.rowY(ay) - c.frontY) + (this.slotX(s) + 1) * c.cellX;
      return d / c.vmax + 2 * profileTime(this.climbZ(this.slotLevel(s)), c.climbV, c.climbAcc);
    }

    // ------------------------------------------------------------ 경로 (일방통행 그래프 BFS)
    // 주차 칸은 목적지일 때만, 스테이션 줄은 그 스테이션이 목적지일 때만 들어감
    canEnter(k, goal, goalSt) {
      const t = this.type[k];
      if (t === PARK) return goal(k);
      if (t === SPUR || t === PICK) return this.stOf[k] === goalSt;
      return true;
    }
    bfs(from, goal, goalSt) {
      const N = this.N, prev = new Int32Array(N).fill(-2), q = [from];
      prev[from] = -1;
      for (let h = 0; h < q.length; h++) {
        const u = q[h];
        if (u !== from && goal(u)) {
          const path = []; let v = u;
          while (v !== from) { path.push(v); v = prev[v]; }
          path.reverse();
          return path;
        }
        if (u !== from && this.type[u] === PARK) continue;
        for (const e of this.succ[u]) {
          const k = e >> 2;
          if (prev[k] !== -2 || !this.canEnter(k, goal, goalSt)) continue;
          prev[k] = u; q.push(k);
        }
      }
      return null;
    }
    dirOf(a, b) { const dx = this.cx(b) - this.cx(a), dy = this.cy(b) - this.cy(a); return dx > 0 ? 0 : dx < 0 ? 1 : dy > 0 ? 2 : 3; }
    stepDist(a, b) {
      const dx = this.cx(b) - this.cx(a);
      if (dx !== 0) return Math.abs(dx) * this.cfg.cellX;
      return Math.abs(this.rowY(this.cy(b)) - this.rowY(this.cy(a)));
    }
    targetSt(k) { return this.type[k] === SPUR || this.type[k] === PICK ? this.stOf[k] : -1; }

    // ------------------------------------------------------------ 행동 실행
    stepRobot(r, dt) {
      if (r.acts.length || r.seg) r.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!r.acts.length) { r.state = "idle"; return; }
        const a = r.acts[0];
        let used = 0;
        if (a.k === "move") used = this.actMove(r, a, dt);
        else if (a.k === "climb") used = this.actClimb(r, a, dt);
        else if (a.k === "pick") used = this.actPick(r, a, dt);
        if (a.done) r.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }

    // 주행: 가속·감속 운동 (앞 로봇이 비켜 주면 예약을 늘려 멈추지 않고 따라감)
    actMove(r, a, dt) {
      const c = this.cfg;
      if (!r.seg) {
        const here = this.ci(r.x, r.y);
        if (here === a.to) { a.done = true; return 0; }
        if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; r.blockedTime += dt; return dt; }
        const to = a.to, path = this.bfs(here, k => k === to, this.targetSt(to));
        if (!path) { r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.25; return dt; }
        const d = this.dirOf(here, path[0]), line = [];
        let prev = here;
        for (const k of path) {
          if (this.dirOf(prev, k) !== d) break;
          line.push(k); prev = k;
          if (this.type[k] === PICK || k === to) break;
        }
        if (this.occ[line[0]] !== -1) { r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.1; return dt; }
        r.blocked = 0;
        const cum = []; let s = 0; prev = here;
        for (const k of line) { s += this.stepDist(prev, k); cum.push(s); prev = k; }
        const turnT = (r.lastDir !== -1 && r.lastDir !== d) ? c.turn : 0;
        r.seg = { d, line, cum, from: here, x0: r.px, y0: r.py, n: 0, s: 0, v: 0, wait: turnT, released: 0 };
        r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move";
      }
      const g = r.seg, dt0 = dt;
      if (g.wait > 0) { g.wait -= dt; if (g.wait > 0) return dt0; dt = -g.wait; g.wait = 0; }
      // 예약 늘리기: 제동 거리 + 한 칸 앞까지
      const look = g.v * g.v / (2 * c.acc) + c.cellX;
      while (g.n < g.line.length && (g.n === 0 || g.cum[g.n - 1] - g.s < look)) {
        const k = g.line[g.n];
        if (this.occ[k] !== -1) break;
        this.occ[k] = r.id; g.n++;
      }
      const end = g.n ? g.cum[g.n - 1] : 0, rem = end - g.s;
      const vAllow = Math.sqrt(2 * c.acc * Math.max(0, rem));
      g.v = Math.min(g.v + c.acc * dt, c.vmax, vAllow);
      g.s = Math.min(end, g.s + g.v * dt);
      const [dx, dy] = DIRS[g.d];
      r.px = g.x0 + dx * g.s; r.py = g.y0 + dy * g.s;
      r.state = g.v < 0.05 && g.n < g.line.length && end - g.s < 1e-3 ? "blocked" : (r.carry >= 0 ? "carry" : "move");
      if (r.state === "blocked") r.blockedTime += dt;
      // 다음 칸 중심에 닿으면 뒤 칸 반납
      while (g.released < g.n && g.s >= g.cum[g.released] - 1e-9) {
        const pc = g.released === 0 ? g.from : g.line[g.released - 1];
        this.occ[pc] = -1; g.released++;
        const k = g.line[g.released - 1]; r.x = this.cx(k); r.y = this.cy(k);
      }
      if (g.released === g.line.length) {
        const k = g.line[g.line.length - 1];
        r.px = this.worldX(r.x); r.py = this.rowY(r.y);
        r.dist += g.cum[g.line.length - 1]; r.seg = null; r.blocked = 0;
        if (k === a.to) a.done = true;
        return dt0;
      }
      // 앞이 막혀 멈춘 채 오래 기다리면 경로를 다시 짬 (직선 구간을 버리고)
      if (r.state === "blocked") {
        r.blocked += dt;
        if (r.blocked > 3 && g.released === g.n) {
          for (let i = g.released; i < g.n; i++) this.occ[g.line[i]] = -1;
          if (g.released > 0) { r.dist += g.cum[g.released - 1]; }
          r.px = this.worldX(r.x); r.py = this.rowY(r.y); r.seg = null; r.blocked = 0;
        }
      } else r.blocked = 0;
      return dt0;
    }

    // 등반: 맞물림 → 위로 → 암 펴기 → (토트 주고받기) → 암 접기 → 아래로 → 풀림
    actClimb(r, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) {
        a.t = 0; a.h = this.climbZ(this.slotLevel(a.slot)); a.tu = profileTime(a.h, c.climbV, c.climbAcc);
        a.T = 2 * c.engage + 2 * a.tu + 2 * c.armT; a.swapped = false;
        r.side = this.slotSide(a.slot) ? 1 : -1; r.state = "climb"; r.climbs++; r.climbH += a.h;
      }
      a.t += dt;
      let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
      const e = c.engage, tu = a.tu, A = c.armT, t = a.t;
      const t1 = e, t2 = e + tu, t3 = t2 + A, t4 = t3 + A, t5 = t4 + tu;
      if (t < t1) { r.pz = 0; r.arm = 0; }
      else if (t < t2) { r.pz = profilePos(t - t1, a.h, c.climbV, c.climbAcc); r.arm = 0; }
      else if (t < t3) { r.pz = a.h; r.arm = this.smooth((t - t2) / A); }
      else if (t < t4) { r.pz = a.h; r.arm = 1 - this.smooth((t - t3) / A); }
      else if (t < t5) { r.pz = a.h - profilePos(t - t4, a.h, c.climbV, c.climbAcc); r.arm = 0; }
      else { r.pz = 0; r.arm = 0; }
      r.state = t >= t2 && t < t4 ? (a.put ? "put" : "get") : "climb";
      if (!a.swapped && t >= t3) {
        a.swapped = true;
        if (a.put) {
          const id = r.carry; r.carry = -1; this.slotTote[a.slot] = id; this.slotRes[a.slot] = 0;
          this.toteLoc[id] = { kind: "slot", s: a.slot }; this.busy[id] = 0;
        } else {
          const id = this.slotTote[a.slot]; this.slotTote[a.slot] = -1; r.carry = id;
          this.toteLoc[id] = { kind: "robot", r: r.id };
        }
      }
      if (a.t >= a.T) { r.pz = 0; r.arm = 0; a.done = true; }
      return used;
    }
    smooth(u) { u = Math.min(1, Math.max(0, u)); return u * u * (3 - 2 * u); }

    actPick(r, a, dt) {
      const c = this.cfg, st = this.stations[a.st];
      if (a.t === undefined) {
        a.t = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
        st.cur = r.id; r.state = "pick";
        const o = r.order; o.tPresent = this.t;
      }
      a.t -= dt; st.busyTime += Math.min(dt, Math.max(0, a.t + dt));
      if (a.t > 0) return dt;
      const used = dt + a.t;
      a.done = true; st.cur = -1; st.picked++; st.load--;
      const o = r.order; o.tDone = this.t + used;
      const lead = o.tDone - o.tArrive;
      this.stats.done++; this.stats.leadSum += lead; this.stats.leadMax = Math.max(this.stats.leadMax, lead);
      r.order = null;
      // 넣을 빈 자리를 고름 (인기 토트일수록 많이 비교해 가까운 자리)
      const s = this.chooseSlot(r.carry);
      this.slotRes[s] = 1;
      r.acts.push({ k: "move", to: this.ci(this.slotX(s), 2 * this.slotAisle(s) + 1) });
      r.acts.push({ k: "climb", slot: s, put: true });
      return used;
    }

    chooseSlot(tote) {
      const n = tote < this.nPopular ? 40 : 8;
      let best = -1, bc = Infinity, got = 0;
      for (let tries = 0; tries < 20000 && got < n; tries++) {
        const s = Math.floor(this.rand() * this.nSlots);
        if (this.slotTote[s] !== -1 || this.slotRes[s]) continue;
        got++;
        const cst = this.slotCost(s);
        if (cst < bc) { bc = cst; best = s; }
      }
      if (best < 0) for (let s = 0; s < this.nSlots; s++) if (this.slotTote[s] === -1 && !this.slotRes[s]) { best = s; break; }
      return best;
    }

    // ------------------------------------------------------------ 주문 / 배정
    newOrder() {
      const c = this.cfg;
      for (let k = 0; k < 10; k++) {
        const pop = this.rand() < c.popularDemand;
        const id = pop ? Math.floor(this.rand() * this.nPopular) : this.nPopular + Math.floor(this.rand() * (this.totes - this.nPopular));
        if (this.busy[id]) continue;
        this.busy[id] = 1;
        const o = { id: this.orders.length, tote: id, tArrive: this.t, tPresent: -1, tDone: -1 };
        this.orders.push(o); this.pending.push(o);
        return;
      }
    }

    free(r) { return !r.acts.length && !r.seg; }

    dispatch() {
      while (this.pending.length) {
        const o = this.pending[0], L = this.toteLoc[o.tote];
        const s = L.s, to = this.ci(this.slotX(s), 2 * this.slotAisle(s) + 1);
        // 목표 통로 칸까지 가장 가까운 빈 로봇 (BFS 단계 수)
        let best = null, bd = Infinity;
        for (const r of this.robots) {
          if (!this.free(r)) continue;
          const here = this.ci(r.x, r.y);
          const p = here === to ? [] : this.bfs(here, k => k === to, -1);
          if (!p) continue;
          if (p.length < bd) { bd = p.length; best = r; }
        }
        if (!best) return;
        this.pending.shift();
        let st = this.stations[0];
        for (const x of this.stations) if (x.load < st.load) st = x;
        st.load++;
        const r = best;
        const here = this.ci(r.x, r.y);
        if (this.parkRes[here] === r.id) this.parkRes[here] = -1;
        r.order = o; this.stats.retrieves++;
        r.acts.push({ k: "move", to });
        r.acts.push({ k: "climb", slot: s, put: false });
        r.acts.push({ k: "move", to: this.ci(st.x0, -2) });
        r.acts.push({ k: "pick", st: st.id });
      }
    }

    // 할 일이 없으면 가까운 랙 아래 주차 칸으로
    park(r) {
      const here = this.ci(r.x, r.y);
      if (this.type[here] === PARK) { this.parkRes[here] = r.id; return; }
      const path = this.bfs(here, k => this.type[k] === PARK && this.parkRes[k] === -1 && this.occ[k] === -1, -1);
      if (!path) return;
      const k = path[path.length - 1];
      this.parkRes[k] = r.id;
      r.acts.push({ k: "move", to: k });
    }

    step(dt) {
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.dispatch();
      for (const r of this.robots) if (this.free(r)) this.park(r);
      for (const r of this.robots) this.stepRobot(r, dt);
    }
    run(seconds, dt) { dt = dt || 1 / 60; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    // 토트 바닥 중심 위치 — 렌더링용
    totePose(id) {
      const L = this.toteLoc[id];
      if (L.kind === "robot") { const r = this.robots[L.r]; return [r.px, r.py + r.side * r.arm * this.reach(), r.pz + this.cfg.bayZ]; }
      return this.slotPos(L.s);
    }
    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats, n = this.robots.length;
      const climbs = this.robots.reduce((a, r) => a + r.climbs, 0);
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: this.orders.length,
        ordersDone: s.done,
        throughput_totesPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        maxLeadTime_s: +s.leadMax.toFixed(1),
        backlog: this.pending.length,
        robotUtil: +(this.robots.reduce((a, r) => a + r.busyTime, 0) / (n * T)).toFixed(3),
        robotWaitShare: +(this.robots.reduce((a, r) => a + r.blockedTime, 0) / (n * T)).toFixed(3),
        stationUtil: +(this.stations.reduce((a, st) => a + st.busyTime, 0) / (this.stations.length * T)).toFixed(3),
        avgClimb_m: climbs ? +(this.robots.reduce((a, r) => a + r.climbH, 0) / climbs).toFixed(2) : null,
        perRobotTotesPerHour: +((s.done / T * 3600) / n).toFixed(1),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos, VOID, LANE, AISLE, PARK, SPUR, PICK };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.SkypodSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
