/*
 * Libiao AirRob 방식 "랙 면 레일 로봇 + 바닥 이송 로봇" 토트 창고 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/AirRob/Scripts/AirRobSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 *
 * 구조: 통로마다 AirRob 1대가 랙 면에 붙은 위·아래 레일을 따라 수평(x)으로 달리고, 가벼운 마스트의 캐리지가
 * 수직(z)으로 오르내리며 양방향 훅으로 통로 양쪽 랙의 토트를 당겨 오거나 밀어 넣습니다.
 * 랙의 맨 아래 단은 비워 두어 바닥 이송 로봇(floor-bot)이 랙 아래 터널을 달립니다.
 * AirRob 은 꺼낸 토트를 바로 그 칸 아래까지 내려 터널 안에서 기다리는 바닥 로봇 위로 밀어 줍니다 (랙 아래 어디서나 주고받기).
 * 바닥 로봇은 토트를 앞쪽 작업 스테이션으로 싣고 가고, 피킹이 끝나면 같은 칸 아래로 돌아와 AirRob 에 돌려줍니다.
 *
 * 바닥 격자: x = -1..nx (x=-1 왼쪽 진입로, x=nx 오른쪽 복귀로), y = -2..R
 *   y = -2  스테이션 줄 (대기 칸 + 피킹 칸)   y = -1  앞쪽 주행로 (-x 방향)
 *   y = 3a, 3a+2  통로 a 의 -y / +y 쪽 랙 아래 터널 (+x 방향 일방통행)   y = 3a+1  통로 a (AirRob 레일, 바닥 로봇 못 들어감)
 *   y = R = 3K  맨 뒤 주차 줄 (마지막 터널과 옆으로 드나듦)
 * 순환: 앞쪽 주행로(-x) → 왼쪽 진입로(+y) → 터널(+x) → 오른쪽 복귀로(-y) → 앞쪽 주행로
 * 좌표: wx = (x+0.5)*cellX, wy = rowY(y) (첫 터널 줄 앞면 = 0), z 위. 시간 s, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 16, aisles: 4, levels: 13,        // 통로 길이(칸), 통로 수, 랙 단 수 (맨 아래 터널 위로)
    cellX: 0.90,                          // 칸 폭 = 기둥 간격. 칸마다 토트 2열 (가정)
    colOff: 0.215,                        // 칸 중심에서 토트 열 중심까지 (토트 사이 10 mm: 공개 수치)
    aisleW: 0.85, rackD: 0.66, flue: 0.06, // 통로 폭 850 mm (공개), 한쪽 랙 깊이 (토트 530 + 여유, 가정), 등지는 랙 사이 틈
    parkW: 0.90,                          // 맨 뒤 주차 줄 폭
    level0Z: 0.85, levelH: 0.40,          // 맨 아래 보관 단 높이 (아래가 터널), 단 간격 (가정)
    toteW: 0.41, toteD: 0.53, toteH: 0.30, // 토트 530 x 410 mm (공개), 높이 300 mm (가정). 긴 변이 통로 방향(y)과 수직
    deckZ: 0.33,                          // 바닥 로봇 상판 높이 = 주고받기 높이 (가정)
    fill: 0.85,
    bots: 16,                             // 주차 줄 칸 수(nx) 이하
    stations: [1, 6],                     // 스테이션 시작 x (피킹 1칸 + 대기 spurLen-1칸, y = -2 줄)
    spurLen: 4,
    frontY: -0.70, spurY: -1.70,          // 앞쪽 주행로 / 스테이션 줄 중심 y
    orderRate: 360,                       // 토트 요청 / 시간
    popularShare: 0.2, popularDemand: 0.8,
    vmax: 2.0, acc: 1.5, turn: 0.4,       // 바닥 로봇 (가정)
    airVx: 2.0, airAx: 2.0,               // AirRob 수평 2 m/s, 2 m/s² (공개 매뉴얼 값)
    airVz: 1.0, airAz: 0.8,               // AirRob 수직 1 m/s, 0.8 m/s² (공개 매뉴얼 값)
    hookT: 4.5,                           // 토트 당기기 / 밀기 한 번 4.5 s (공개 매뉴얼 값)
    maxPerTunnel: 2,                      // 터널 하나에 동시에 배정하는 출고 + 반납 작업 수
    abortWait: 2.0,                       // 다른 로봇이 막고 있으면 이만큼 기다린 뒤 토트를 되돌려 놓음
    pickMin: 3, pickMax: 6,               // 작업자 피킹 시간 / 토트 (스테이션당 600 토트/h 공개 수치)
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
  const VOID = 0, LANE = 1, TUNNEL = 2, PARK = 3, SPUR = 4, PICK = 5;
  const PULL_AT = 0.45, PUSH_AT = 0.55;   // 훅 동작 중 토트를 넘겨받는 / 놓는 시점 (비율)

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      const K = c.aisles;
      this.R = 3 * K;                          // 주차 줄 y
      this.W = c.nx + 2; this.H = this.R + 3;  // x: -1..nx, y: -2..R
      const N = this.W * this.H;
      this.N = N;
      this.type = new Uint8Array(N);
      this.stOf = new Int32Array(N).fill(-1);
      this.occ = new Int32Array(N).fill(-1);
      this.parkRes = new Int32Array(N).fill(-1);
      // 줄 중심 y (등지는 터널 사이 flue, 주차 줄 앞 여유)
      this.rowYs = [];
      let acc = 0;
      for (let y = 0; y <= this.R; y++) {
        if (y > 0 && y < this.R && y % 3 === 0) acc += c.flue;
        if (y === this.R) acc += 0.10;
        const w = this.rowW(y);
        this.rowYs.push(acc + w / 2); acc += w;
      }
      this.depthY = acc;
      this.rackDepthY = this.rowYs[this.R - 1] + c.rackD / 2;   // 마지막 랙 뒷면
      for (let y = -2; y <= this.R; y++) for (let x = -1; x <= c.nx; x++) {
        let t = VOID;
        if (y === -1) t = LANE;
        else if (y >= 0 && y < this.R && (x === -1 || x === c.nx)) t = LANE;
        else if (y >= 0 && x >= 0 && x < c.nx) t = y === this.R ? PARK : (y % 3 === 1 ? VOID : TUNNEL);
        this.type[this.ci(x, y)] = t;
      }
      this.stations = c.stations.map((x0, si) => {
        for (let k = 0; k < c.spurLen; k++) { const i = this.ci(x0 + k, -2); this.type[i] = k === 0 ? PICK : SPUR; this.stOf[i] = si; }
        return { id: si, x0, load: 0, busyTime: 0, picked: 0, cur: -1 };
      });
      // 다음 칸 목록 (일방통행 그래프)
      this.succ = [];
      for (let i = 0; i < N; i++) this.succ.push([]);
      for (let y = -2; y <= this.R; y++) for (let x = -1; x <= c.nx; x++) {
        const i = this.ci(x, y), t = this.type[i], s = this.succ[i];
        if (t === VOID) continue;
        const add = (d) => { const nx = x + DIRS[d][0], ny = y + DIRS[d][1]; if (this.typeAt(nx, ny) !== VOID) s.push(this.ci(nx, ny) * 4 + d); };
        if (y === -1) {
          for (const st of this.stations) if (x === st.x0 + c.spurLen - 1) add(3);
          if (x > -1) add(1); else add(2);
        } else if (y === -2) {
          if (t === PICK) add(2); else add(1);
        } else if (x === -1) {
          if (y % 3 !== 1) add(0);
          if (y < this.R - 1) add(2);
        } else if (x === c.nx) {
          add(3);
        } else if (t === TUNNEL) {
          add(0);
          if (y === this.R - 1) add(2);
        } else if (t === PARK) {
          add(3);
        }
      }
      // 토트 자리: (통로 a, 칸 x, 열 col, 쪽 s(0 = -y, 1 = +y), 단 l)
      this.nSlots = K * c.nx * 2 * 2 * c.levels;
      this.slotTote = new Int32Array(this.nSlots).fill(-1);
      const cand = [];
      for (let id = 0; id < this.nSlots; id++) {
        if (this.rand() < c.fill) cand.push({ id, key: this.slotCost(id) + this.gauss() * 3.0 });
      }
      cand.sort((a, b) => a.key - b.key || a.id - b.id);
      this.toteLoc = [];   // { kind: "slot", s } | { kind: "bot", r } | { kind: "air", a }
      this.toteHome = [];  // 토트가 돌아갈 자리 (꺼낸 자리로 되돌림)
      cand.forEach((s, id) => { this.slotTote[s.id] = id; this.toteLoc[id] = { kind: "slot", s: s.id }; this.toteHome[id] = s.id; });
      this.totes = cand.length;
      this.nPopular = Math.max(1, Math.floor(this.totes * c.popularShare));
      this.busy = new Uint8Array(this.totes);
      // AirRob: 통로마다 1대
      this.airs = [];
      for (let a = 0; a < K; a++) {
        this.airs.push({ id: a, px: this.worldX(Math.floor(c.nx / 2)), pz: c.deckZ, hook: 0, side: 1, carry: -1,
          acts: [], jobs: [], job: null, waitT: 0, state: "idle", busyTime: 0, ops: 0, aborts: 0, liftH: 0 });
      }
      // 바닥 로봇: 주차 줄에서 시작
      this.robots = [];
      for (let i = 0; i < Math.min(c.bots, c.nx); i++) {   // 주차 줄 칸 수까지
        let x, k;
        do { x = Math.floor(this.rand() * c.nx); k = this.ci(x, this.R); } while (this.occ[k] !== -1);
        const r = { id: i, x, y: this.R, px: this.worldX(x), py: this.rowY(this.R), carry: -1,
          acts: [], seg: null, lastDir: -1, waitT: 0, blocked: 0, busyTime: 0, blockedTime: 0, dist: 0,
          state: "idle", order: null };
        this.occ[k] = i; this.parkRes[k] = i; this.robots.push(r);
      }
      this.orders = []; this.pending = [];
      this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { done: 0, leadSum: 0, leadMax: 0, jobs: 0 };
    }

    // ------------------------------------------------------------ 기본
    gauss() { let u = 0; while (!u) u = this.rand(); const v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    ci(x, y) { return (x + 1) + (y + 2) * this.W; }
    cx(i) { return i % this.W - 1; }
    cy(i) { return Math.floor(i / this.W) - 2; }
    inb(x, y) { return x >= -1 && x <= this.cfg.nx && y >= -2 && y <= this.R; }
    typeAt(x, y) { return this.inb(x, y) ? this.type[this.ci(x, y)] : VOID; }
    rowW(y) { const c = this.cfg; if (y === this.R) return c.parkW; return y % 3 === 1 ? c.aisleW : c.rackD; }
    rowY(y) { const c = this.cfg; if (y === -1) return c.frontY; if (y === -2) return c.spurY; return this.rowYs[y]; }
    worldX(x) { return (x + 0.5) * this.cfg.cellX; }
    levelZ(l) { return this.cfg.level0Z + l * this.cfg.levelH; }   // 토트 바닥 높이
    aisleY(a) { return this.rowYs[3 * a + 1]; }
    tunnelRow(a, side) { return 3 * a + (side > 0 ? 2 : 0); }
    reach() { return this.cfg.aisleW / 2 + this.cfg.rackD / 2; }
    slotAisle(s) { return Math.floor(s / (this.cfg.nx * 4 * this.cfg.levels)); }
    slotX(s) { return Math.floor(s / (4 * this.cfg.levels)) % this.cfg.nx; }
    slotCol(s) { return Math.floor(s / (2 * this.cfg.levels)) % 2; }
    slotSide(s) { return Math.floor(s / this.cfg.levels) % 2 ? 1 : -1; }
    slotLevel(s) { return s % this.cfg.levels; }
    slotWX(s) { return this.worldX(this.slotX(s)) + (this.slotCol(s) ? 1 : -1) * this.cfg.colOff; }
    slotPos(s) {
      return [this.slotWX(s), this.aisleY(this.slotAisle(s)) + this.slotSide(s) * this.reach(), this.levelZ(this.slotLevel(s))];
    }
    // 바닥 로봇이 그 칸 아래까지 가는 거리 + AirRob 오르내림 (초기 배치용 비용)
    slotCost(s) {
      const c = this.cfg, x0 = c.stations[0];
      const ty = this.tunnelRow(this.slotAisle(s), this.slotSide(s));
      const d = (x0 + 1) * c.cellX + (this.rowY(ty) - c.frontY) + (this.slotX(s) + 1) * c.cellX;
      return d / c.vmax + 2 * profileTime(this.levelZ(this.slotLevel(s)) - c.deckZ, c.airVz, c.airAz);
    }
    handoverCell(s) { return this.ci(this.slotX(s), this.tunnelRow(this.slotAisle(s), this.slotSide(s))); }

    // ------------------------------------------------------------ 경로 (일방통행 그래프 BFS)
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

    // ------------------------------------------------------------ 바닥 로봇
    stepRobot(r, dt) {
      if (r.acts.length || r.seg) r.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!r.acts.length) { if (!r.seg) r.state = "idle"; return; }
        const a = r.acts[0];
        let used = 0;
        if (a.k === "move") used = this.actMove(r, a, dt);
        else if (a.k === "hand") used = this.actHand(r, a, dt);
        else if (a.k === "pick") used = this.actPick(r, a, dt);
        if (a.done) r.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }

    // 주행: 가속·감속 운동 (앞 로봇이 비켜 주면 예약을 늘려 멈추지 않고 따라감) — skypod-sim-core.js 와 같은 방식
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

    // 랙 아래에서 AirRob 과 토트 주고받기를 기다림
    actHand(r, a, dt) {
      if (a.job.handed) { a.done = true; return 0; }
      r.state = "wait";
      return dt;
    }

    actPick(r, a, dt) {
      const c = this.cfg, st = this.stations[a.st];
      if (a.t === undefined) {
        a.t = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
        st.cur = r.id; r.state = "pick";
        r.order.tPresent = this.t;
      }
      a.t -= dt; st.busyTime += Math.min(dt, Math.max(0, a.t + dt));
      if (a.t > 0) return dt;
      const used = dt + a.t;
      a.done = true; st.cur = -1; st.picked++; st.load--;
      const o = r.order; o.tDone = this.t + used;
      const lead = o.tDone - o.tArrive;
      this.stats.done++; this.stats.leadSum += lead; this.stats.leadMax = Math.max(this.stats.leadMax, lead);
      r.order = null;
      // 꺼낸 자리로 되돌림: 같은 칸 아래로 가서 AirRob 에 넘김
      const s = this.toteHome[r.carry];
      const job = this.newJob("put", s, r);
      r.acts.push({ k: "move", to: this.handoverCell(s) });
      r.acts.push({ k: "hand", job });
      return used;
    }

    newJob(kind, s, r) {
      const job = { id: this.stats.jobs++, kind, slot: s, bot: r, phase: 0, handed: false, cell: this.slotX(s), side: this.slotSide(s), t0: this.t };
      this.airs[this.slotAisle(s)].jobs.push(job);
      return job;
    }

    // ------------------------------------------------------------ AirRob
    botArrived(job) {
      const r = job.bot, a = r.acts[0];
      return !!a && a.k === "hand" && a.job === job && !r.seg;
    }

    // J 의 바닥 로봇보다 앞(+x)에 같은 터널에서 이 AirRob 을 기다리는(아직 못 받은) 다른 로봇이 있는가.
    // 터널은 +x 일방통행이라 그런 로봇이 있으면 J 의 로봇이 막힐 수 있음
    aheadWaiting(A, J, arrivedOnly) {
      const ty = this.tunnelRow(A.id, J.side), b = J.bot;
      return A.jobs.some(K => K !== J && K.phase === 0 && !K.handed && K.bot.y === ty && K.bot.x > b.x &&
        (!arrivedOnly || this.botArrived(K)));
    }
    inTunnel(J) { const b = J.bot; return b.y === this.tunnelRow(this.slotAisle(J.slot), J.side) && b.x >= 0 && b.x < this.cfg.nx; }
    tunnelJobs(a, side) { return this.airs[a].jobs.reduce((n, j) => n + (j.side === side ? 1 : 0), 0); }

    // 할 일 고르기: 도착한 반납 로봇 → 도착한 출고 로봇 → 출고 토트 미리 꺼내기 → 반납 로봇 쪽으로 미리 이동
    planAir(A, dt) {
      const c = this.cfg;
      const J = A.job;
      if (A.carry >= 0 && J && J.kind === "get") {
        const hx = this.worldX(J.cell);
        if (Math.abs(A.px - hx) > 1e-6 || Math.abs(A.pz - c.deckZ) > 1e-6) { A.acts.push({ k: "go", x: hx, z: c.deckZ }); return; }
        if (this.botArrived(J)) { A.waitT = 0; A.acts.push({ k: "hook", mode: "push", dest: "bot", job: J }); return; }
        A.waitT += dt; A.state = "wait";
        // 이 토트를 받을 로봇이 다른 로봇 뒤에 막혀 있으면 토트를 제자리에 되돌려 놓고 먼저 온 로봇부터 처리
        if (A.waitT > c.abortWait && J.bot.state === "blocked" && this.aheadWaiting(A, J, true)) {
          const p = this.slotPos(J.slot);
          A.acts.push({ k: "go", x: p[0], z: p[2] });
          A.acts.push({ k: "hook", mode: "push", dest: "slot", job: J, abort: true });
          A.aborts++; A.waitT = 0;
        }
        return;
      }
      A.waitT = 0;
      let K = A.jobs.find(j => j.kind === "put" && j.phase === 0 && this.botArrived(j));
      if (K) {
        const p = this.slotPos(K.slot);
        K.phase = 1; A.job = K;
        A.acts.push({ k: "go", x: this.worldX(K.cell), z: c.deckZ });
        A.acts.push({ k: "hook", mode: "pull", src: "bot", job: K });
        A.acts.push({ k: "go", x: p[0], z: p[2] });
        A.acts.push({ k: "hook", mode: "push", dest: "slot", job: K });
        return;
      }
      K = A.jobs.find(j => j.kind === "get" && j.phase === 0 && this.botArrived(j)) || A.jobs.find(j => j.kind === "get" && j.phase === 0 && this.inTunnel(j) && !this.aheadWaiting(A, j, false));
      if (K) {
        const p = this.slotPos(K.slot);
        K.phase = 1; A.job = K;
        A.acts.push({ k: "go", x: p[0], z: p[2] });
        A.acts.push({ k: "hook", mode: "pull", src: "slot", job: K });
        return;
      }
      K = A.jobs.find(j => j.kind === "put" && j.phase === 0);
      if (K) {
        const hx = this.worldX(K.cell);
        if (Math.abs(A.px - hx) > 1e-6 || Math.abs(A.pz - c.deckZ) > 1e-6) { A.acts.push({ k: "go", x: hx, z: c.deckZ }); return; }
        A.state = "wait"; return;
      }
      A.state = "idle";
    }

    stepAir(A, dt) {
      if (!A.acts.length) this.planAir(A, dt);
      if (A.acts.length) A.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!A.acts.length) return;
        const a = A.acts[0];
        const used = a.k === "go" ? this.airGo(A, a, dt) : this.airHook(A, a, dt);
        if (a.done) A.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }

    // 수평·수직 동시 이동 (축마다 따로 가속·감속)
    airGo(A, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) {
        a.t = 0; a.x0 = A.px; a.z0 = A.pz;
        a.dx = Math.abs(a.x - a.x0); a.dz = Math.abs(a.z - a.z0);
        a.T = Math.max(profileTime(a.dx, c.airVx, c.airAx), profileTime(a.dz, c.airVz, c.airAz));
        if (a.dz > 0.01) A.liftH += a.dz;
      }
      a.t += dt;
      let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
      A.px = a.x0 + Math.sign(a.x - a.x0) * profilePos(a.t, a.dx, c.airVx, c.airAx);
      A.pz = a.z0 + Math.sign(a.z - a.z0) * profilePos(a.t, a.dz, c.airVz, c.airAz);
      A.state = a.dz > 0.01 ? "lift" : "move";
      if (a.t >= a.T) { A.px = a.x; A.pz = a.z; a.done = true; }
      return used;
    }

    smooth(u) { u = Math.min(1, Math.max(0, u)); return u * u * (3 - 2 * u); }

    // 양방향 훅: 당기기 = 빈 훅을 펴서 걸고 토트와 함께 접음, 밀기 = 토트와 함께 펴서 놓고 빈 훅을 접음
    airHook(A, a, dt) {
      const c = this.cfg, J = a.job;
      if (a.t === undefined) { a.t = 0; a.swapped = false; A.side = J.side; A.ops++; }
      a.t += dt;
      let used = dt;
      if (a.t >= c.hookT) { used = dt - (a.t - c.hookT); a.t = c.hookT; }
      const u = a.t / c.hookT, s = J.side;
      const at = a.mode === "pull" ? PULL_AT : PUSH_AT;
      A.hook = u < at ? s * this.smooth(u / at) : s * (1 - this.smooth((u - at) / (1 - at)));
      A.state = a.mode;
      if (!a.swapped && u >= at) {
        a.swapped = true;
        if (a.mode === "pull") {
          let id;
          if (a.src === "slot") { id = this.slotTote[J.slot]; this.slotTote[J.slot] = -1; }
          else { const r = J.bot; id = r.carry; r.carry = -1; }
          A.carry = id; this.toteLoc[id] = { kind: "air", a: A.id };
        } else {
          const id = A.carry; A.carry = -1;
          if (a.dest === "slot") {
            this.slotTote[J.slot] = id; this.toteLoc[id] = { kind: "slot", s: J.slot };
            if (!a.abort) this.busy[id] = 0;
          } else {
            const r = J.bot; r.carry = id; this.toteLoc[id] = { kind: "bot", r: r.id };
            J.handed = true;
          }
        }
      }
      if (a.t >= c.hookT) {
        A.hook = 0; a.done = true;
        if (a.mode === "pull" && a.src === "bot") J.handed = true;          // 훅을 다 접은 뒤 바닥 로봇 출발
        const finished = (a.mode === "push" && a.dest === "bot") || (a.mode === "push" && a.dest === "slot" && !a.abort);
        if (a.abort) { J.phase = 0; A.job = null; }
        if (finished) { A.jobs.splice(A.jobs.indexOf(J), 1); A.job = null; }
      }
      return used;
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

    // 배정 가능한 로봇: 할 일이 없거나 주차하러 가는 중
    free(r) { return !r.order && (!r.acts.length || (r.acts.length === 1 && r.acts[0].park)); }

    dispatch() {
      const c = this.cfg;
      for (let i = 0; i < this.pending.length; i++) {
        const o = this.pending[i], s = this.toteLoc[o.tote].s, to = this.handoverCell(s);
        // 한 터널에 로봇이 몰리면 진입로가 막히므로 터널마다 동시 작업 수 제한
        if (this.tunnelJobs(this.slotAisle(s), this.slotSide(s)) >= c.maxPerTunnel) continue;
        let best = null, bd = Infinity;
        for (const r of this.robots) {
          if (!this.free(r)) continue;
          const here = this.ci(r.x, r.y);
          const p = here === to ? [] : this.bfs(here, k => k === to, -1);
          if (!p) continue;
          if (p.length < bd) { bd = p.length; best = r; }
        }
        if (!best) return;
        this.pending.splice(i, 1); i--;
        let st = this.stations[0];
        for (const x of this.stations) if (x.load < st.load) st = x;
        st.load++;
        const r = best;
        for (let k = 0; k < this.N; k++) if (this.parkRes[k] === r.id) this.parkRes[k] = -1;
        r.acts.length = 0;   // 주차하러 가던 중이면 지금 구간을 마저 달린 뒤 새 목적지로
        r.order = o;
        const job = this.newJob("get", s, r);
        r.acts.push({ k: "move", to });
        r.acts.push({ k: "hand", job });
        r.acts.push({ k: "move", to: this.ci(st.x0, -2) });
        r.acts.push({ k: "pick", st: st.id });
      }
    }

    // 할 일이 없으면 맨 뒤 주차 줄로
    park(r) {
      if (r.acts.length || r.seg) return;
      const here = this.ci(r.x, r.y);
      if (this.type[here] === PARK) { this.parkRes[here] = r.id; return; }
      const path = this.bfs(here, k => this.type[k] === PARK && this.parkRes[k] === -1 && this.occ[k] === -1, -1);
      if (!path) return;
      const k = path[path.length - 1];
      this.parkRes[k] = r.id;
      r.acts.push({ k: "move", to: k, park: true });
    }

    step(dt) {
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.dispatch();
      for (const r of this.robots) if (!r.order && !r.acts.length) this.park(r);
      for (const r of this.robots) this.stepRobot(r, dt);
      for (const A of this.airs) this.stepAir(A, dt);
    }
    run(seconds, dt) { dt = dt || 1 / 60; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    // 토트 바닥 중심 위치 — 렌더링용
    totePose(id) {
      const L = this.toteLoc[id];
      if (L.kind === "bot") { const r = this.robots[L.r]; return [r.px, r.py, this.cfg.deckZ]; }
      if (L.kind === "air") { const A = this.airs[L.a]; return [A.px, this.aisleY(A.id) + A.hook * this.reach(), A.pz]; }
      return this.slotPos(L.s);
    }
    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats, n = this.robots.length, na = this.airs.length;
      const ops = this.airs.reduce((a, A) => a + A.ops, 0);
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: this.orders.length,
        ordersDone: s.done,
        throughput_totesPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        maxLeadTime_s: +s.leadMax.toFixed(1),
        backlog: this.pending.length,
        botUtil: +(this.robots.reduce((a, r) => a + r.busyTime, 0) / (n * T)).toFixed(3),
        botWaitShare: +(this.robots.reduce((a, r) => a + r.blockedTime, 0) / (n * T)).toFixed(3),
        airUtil: +(this.airs.reduce((a, A) => a + A.busyTime, 0) / (na * T)).toFixed(3),
        airHookOpsPerHour: +(ops / na / T * 3600).toFixed(1),
        airAborts: this.airs.reduce((a, A) => a + A.aborts, 0),
        stationUtil: +(this.stations.reduce((a, st) => a + st.busyTime, 0) / (this.stations.length * T)).toFixed(3),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos, VOID, LANE, TUNNEL, PARK, SPUR, PICK };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.AirRobSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
