/*
 * HaiPick Climb 방식 랙 등반 로봇 창고 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/HaiPick/Scripts/HaiPickSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 *
 * 구조: 바닥에서 x 방향으로 뻗은 랙 줄(R)과 통로(A)가 y 방향으로 번갈아 놓입니다 (lanes = "AARRARRARRA").
 * 랙 맨 아래 단은 바닥에서 띄워 로봇(토트를 실은 채로도)이 랙 밑을 y 방향으로 지나갈 수 있습니다.
 * 랙의 통로 쪽 면에는 칸마다 세로 가이드 레일이 있고, 로봇은 통로 칸에 서서 팔 두 개로 레일을 잡고
 * 원하는 단까지 올라가 토트를 꺼내(포크) 몸 위에 싣고 내려옵니다. 그다음 바닥을 달려 앞쪽 작업대로 가서
 * 작업대 레일을 조금 올라 작업자 허리 높이로 토트를 내밀고, 피킹이 끝나면 가까운 빈 칸에 토트를 다시 넣습니다.
 *
 * 셀: (x, j). x = 0..nx-1 (칸), j = 0..nL-1 (레인), j = -1 은 작업대 줄 (작업대 칸만 존재)
 * 이동: 통로 레인은 ±x, ±y 모두 가능. 랙 레인(랙 밑)은 기둥 때문에 ±y 로만 지나감.
 *       작업대 줄: 대기열 끝 칸으로만 들어가고(−y), −x 로만 앞으로 가며, 피킹 칸에서만 나감(+y)
 * 좌표: x = (i+0.5)*cellX, y = 레인 중심 (앞쪽 통로 앞 가장자리 = 0), z-up. 시간 s, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 22, cellX: 0.55,               // 랙 칸 수, 칸 피치 (토트 폭 400 mm + 기둥 + 레일, 가정)
    lanes: "AARRARRARRA",              // y 방향 레인: A 통로, R 랙 줄 (등을 맞댄 2줄씩). 맨 앞 통로는 작업대 앞 공간 (랙 없음)
    aisleW: 0.90, rackD: 0.62,         // 통로 폭 900 mm (공식 최소값), 랙 깊이 (가정)
    rackBase: 0.78, levelH: 0.42, levels: 26, beamH: 0.08, // 맨 아래 단 높이 (로봇+토트가 밑으로 지나감), 단 간격 → 높이 약 11.7 m (공식 최대 12 m)
    toteW: 0.40, toteD: 0.60, toteH: 0.30, // 표준 600 x 400 x 300 mm 토트 (가정)
    robotL: 0.48, robotW: 0.75, deckH: 0.32, // 로봇 x 폭, y 길이, 토트를 싣는 상판 높이 (가정)
    fill: 0.85,
    robots: 18,
    stations: [[2, 4], [9, 4], [16, 4]], // [피킹 칸 x, 칸 수] — 앞쪽 j = -1 줄. 피킹 칸 + 대기 3칸
    stationDepth: 0.90, presentZ: 0.53, // 작업대에서 로봇이 올라가는 높이 → 토트 바닥 0.85 m (가정)
    orderRate: 650,                    // 토트 요청 / 시간 (작업대 3곳 처리 능력의 약 90%)
    popularShare: 0.2, popularDemand: 0.7,
    vmax: 4.0, acc: 2.0,               // 주행 4 m/s (공식), 가속도 (가정)
    turn: 0.3,                         // x↔y 방향 바꿀 때 바퀴 전환 시간 (가정)
    climbV: 1.0, climbAcc: 1.0,        // 등반 1 m/s (공식), 가속도 (가정)
    attach: 0.6,                       // 등반 팔을 레일에 걸기/풀기 (가정)
    xfer: 2.4,                         // 포크로 토트 꺼내기/넣기 (가정)
    pickMin: 5, pickMax: 10,           // 작업자 피킹 시간 / 토트
    stationCap: 6,                     // 작업대 하나에 동시에 배정하는 토트 수 (꺼내는 중 + 대기 + 피킹)
    seed: 10,
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

  const DX = [1, -1, 0, 0], DY = [0, 0, 1, -1];
  // 로봇 상태 (궤적 코드와 같은 순서)
  const STATES = ["idle", "move", "carry", "blocked", "attach", "climb", "xfer", "present", "detach", "turn"];

  // 작은 이진 힙 (비용, 넣은 순서) — C# 버전과 같은 순서로 꺼내도록 직접 구현
  class Heap {
    constructor() { this.k = []; this.s = []; this.v = []; this.n = 0; }
    clear() { this.n = 0; }
    less(i, j) { return this.k[i] < this.k[j] || (this.k[i] === this.k[j] && this.s[i] < this.s[j]); }
    swap(i, j) {
      let t = this.k[i]; this.k[i] = this.k[j]; this.k[j] = t;
      t = this.s[i]; this.s[i] = this.s[j]; this.s[j] = t;
      t = this.v[i]; this.v[i] = this.v[j]; this.v[j] = t;
    }
    push(key, seq, val) {
      let i = this.n++;
      this.k[i] = key; this.s[i] = seq; this.v[i] = val;
      while (i > 0) { const p = (i - 1) >> 1; if (!this.less(i, p)) break; this.swap(i, p); i = p; }
    }
    pop() {   // 값 반환, 키는 this.lastKey
      const val = this.v[0]; this.lastKey = this.k[0];
      this.n--;
      if (this.n > 0) {
        this.k[0] = this.k[this.n]; this.s[0] = this.s[this.n]; this.v[0] = this.v[this.n];
        let i = 0;
        for (;;) {
          const l = 2 * i + 1, r = l + 1; let m = i;
          if (l < this.n && this.less(l, m)) m = l;
          if (r < this.n && this.less(r, m)) m = r;
          if (m === i) break;
          this.swap(i, m); i = m;
        }
      }
      return val;
    }
  }

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      this.nL = c.lanes.length;
      this.NX = c.nx;
      this.NC = (this.nL + 1) * c.nx;          // 셀 수 (j = -1 줄 포함)
      // 레인 중심 y
      this.laneY = []; this.laneW = [];
      let y = 0;
      for (let j = 0; j < this.nL; j++) {
        const w = c.lanes[j] === "A" ? c.aisleW : c.rackD;
        this.laneW.push(w); this.laneY.push(y + w / 2); y += w;
      }
      this.depthY = y;
      this.stationY = -c.stationDepth / 2;
      // 셀 종류: 0 없음, 1 통로, 2 랙 밑, 3 작업대 대기, 4 작업대 피킹
      this.kind = new Uint8Array(this.NC);
      this.stAt = new Int32Array(this.NC).fill(-1);
      for (let j = 0; j < this.nL; j++) for (let x = 0; x < c.nx; x++) this.kind[this.ci(x, j)] = c.lanes[j] === "A" ? 1 : 2;
      this.stations = c.stations.map(([x0, n], si) => {
        for (let k = 0; k < n; k++) { const q = this.ci(x0 + k, -1); this.kind[q] = k === 0 ? 4 : 3; this.stAt[q] = si; }
        return { id: si, x0, n, assigned: 0, tokens: 0, busyTime: 0, picked: 0, cur: -1 };
      });
      this.occ = new Int32Array(this.NC).fill(-1);
      this.parkRes = new Int32Array(this.NC).fill(-1);
      // 랙 슬롯: 랙 레인 j, 칸 x, 단 lv
      this.rackLanes = [];
      for (let j = 0; j < this.nL; j++) if (c.lanes[j] === "R") this.rackLanes.push(j);
      this.NS = this.rackLanes.length * c.nx * c.levels;
      this.slotTote = new Int32Array(this.NS).fill(-1);
      this.slotRes = new Uint8Array(this.NS);
      // 토트 초기 배치 (인기 토트 = 작은 ID 가 낮은 단·작업대 가까이에 오도록)
      const cells = [];
      for (let s = 0; s < this.NS; s++) {
        if (this.rand() < c.fill) {
          const lv = s % c.levels, x = Math.floor(s / c.levels) % c.nx, rj = this.rackLanes[Math.floor(s / (c.levels * c.nx))];
          cells.push({ s, key: lv * 0.42 + this.laneY[rj] * 0.05 + this.gauss() * 1.2 });
        }
      }
      cells.sort((a, b) => a.key - b.key || a.s - b.s);
      this.toteLoc = [];
      cells.forEach((e, id) => { this.slotTote[e.s] = id; this.toteLoc.push({ kind: 0, slot: e.s, r: -1, u: 0 }); });   // kind 0 랙, 1 로봇, 2 옮기는 중
      this.totes = cells.length;
      this.nPopular = Math.max(1, Math.floor(this.totes * c.popularShare));
      this.toteBusy = new Uint8Array(this.totes);
      // 로봇: 랙 밑 셀에 흩어 세워 둠
      this.robots = [];
      for (let i = 0; i < c.robots; i++) {
        let x, j, k;
        do { x = Math.floor(this.rand() * c.nx); j = this.rackLanes[Math.floor(this.rand() * this.rackLanes.length)]; k = this.ci(x, j); }
        while (this.occ[k] !== -1);
        const r = { id: i, x, j, px: 0, py: 0, pz: 0, arm: 0, fork: 0, side: 1, carry: -1, task: null, acts: [], seg: null,
          lastDir: -1, path: null, pi: 0, goal: -1, wait: 0, stuck: 0, waitT: 0, state: 0,
          busyTime: 0, blockedTime: 0, climbs: 0, dist: 0, deliveries: 0 };
        this.setPos(r); this.occ[k] = i; this.robots.push(r);
      }
      this.pending = [];
      this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { arrived: 0, done: 0, leadSum: 0, leadMax: 0, climbs: 0, climbM: 0, replans: 0 };
      this.heap = new Heap();
      this.goalCount = new Int32Array(this.NC);
      this.cost = new Float64Array(this.NC * 4); this.prev = new Int32Array(this.NC * 4); this.done = new Uint8Array(this.NC * 4);
    }

    // ------------------------------------------------------------ 기본
    gauss() { let u = 0; while (!u) u = this.rand(); const v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    ci(x, j) { return (j + 1) * this.NX + x; }
    cx(k) { return k % this.NX; }
    cj(k) { return Math.floor(k / this.NX) - 1; }
    worldX(x) { return (x + 0.5) * this.cfg.cellX; }
    worldY(j) { return j < 0 ? this.stationY : this.laneY[j]; }
    setPos(r) { r.px = this.worldX(r.x); r.py = this.worldY(r.j); }
    slotLane(s) { return this.rackLanes[Math.floor(s / (this.cfg.levels * this.cfg.nx))]; }
    slotX(s) { return Math.floor(s / this.cfg.levels) % this.cfg.nx; }
    slotLv(s) { return s % this.cfg.levels; }
    slotId(ri, x, lv) { return (ri * this.cfg.nx + x) * this.cfg.levels + lv; }
    slotZ(s) { return this.cfg.rackBase + this.slotLv(s) * this.cfg.levelH; }     // 토트 바닥 높이
    // 랙 레인 j 를 마주보는 통로 레인
    faceLane(j) { return j > 0 && this.cfg.lanes[j - 1] === "A" ? j - 1 : j + 1; }
    accessCell(s) { return this.ci(this.slotX(s), this.faceLane(this.slotLane(s))); }

    // ------------------------------------------------------------ 이동 규칙
    canMove(k, d) {
      const x = this.cx(k), j = this.cj(k), nx = x + DX[d], nj = j + DY[d];
      if (nx < 0 || nx >= this.NX || nj < -1 || nj >= this.nL) return -1;
      const nk = this.ci(nx, nj), kd = this.kind[k], nkd = this.kind[nk];
      if (nkd === 0) return -1;
      if (d < 2) {                                         // x 이동
        if (kd === 1 && nkd === 1) return nk;
        if ((kd === 3 || kd === 4) && (nkd === 3 || nkd === 4) && d === 1 && this.stAt[k] === this.stAt[nk]) return nk;
        return -1;
      }
      if (j >= 0 && nj >= 0) return nk;                    // 레인 사이 y 이동 (통로 ↔ 랙 밑)
      if (j === 0 && nj === -1) {                          // 대기열 끝 칸으로만 진입
        const st = this.stations[this.stAt[nk]];
        return st && nx === st.x0 + st.n - 1 ? nk : -1;
      }
      if (j === -1 && nj === 0) return kd === 4 ? nk : -1; // 피킹 칸에서만 나감
      return -1;
    }
    stepLen(k, d) { return d < 2 ? this.cfg.cellX : Math.abs(this.worldY(this.cj(k) + DY[d]) - this.worldY(this.cj(k))); }

    // 방향 전환 비용 포함 다익스트라. avoid: 0 = 서 있는 로봇만 피함, 1 = 모든 로봇 피함
    plan(r, goal, avoid) {
      const c = this.cfg, N = this.NC * 4, cost = this.cost, prev = this.prev, done = this.done, H = this.heap;
      cost.fill(Infinity); prev.fill(-1); done.fill(0); H.clear();
      const sk = this.ci(r.x, r.j);
      let seq = 0;
      for (let d = 0; d < 4; d++) {
        const v = sk * 4 + d; cost[v] = (r.lastDir === -1 || r.lastDir === d) ? 0 : c.turn; H.push(cost[v], seq++, v);
      }
      let best = -1;
      while (H.n > 0) {
        const u = H.pop(); const bu = H.lastKey;
        if (done[u] || bu > cost[u]) continue;
        done[u] = 1;
        const k = u >> 2, d0 = u & 3;
        if (k === goal) { best = u; break; }
        for (let d = 0; d < 4; d++) {
          const nk = this.canMove(k, d);
          if (nk < 0) continue;
          const o = this.occ[nk];
          if (o !== -1 && o !== r.id && nk !== goal) {
            if (avoid === 1) continue;
            const ro = this.robots[o];
            if (!ro.seg && (ro.acts.length === 0 || ro.acts[0].k !== "go")) continue;   // 서 있는 로봇 (주차·등반·작업대)
          }
          const v = nk * 4 + d, nc = bu + this.stepLen(k, d) / c.vmax + 0.03 + (d0 === d ? 0 : c.turn);
          if (nc < cost[v]) { cost[v] = nc; prev[v] = u; H.push(nc, seq++, v); }
        }
      }
      if (best < 0) return null;
      const path = []; let u = best;
      while (u >= 0 && (u >> 2) !== sk) { path.push(u); u = prev[u]; }
      path.reverse();
      return path;   // 원소 = 셀*4 + 방향
    }

    // ------------------------------------------------------------ 로봇 행동
    stepRobot(r, dt) {
      if (r.task || r.acts.length || r.seg) r.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 12) {
        if (!r.acts.length) {
          if (r.task) this.nextTask(r);
          if (!r.acts.length) { r.state = 0; return; }
        }
        const a = r.acts[0];
        let used = 0;
        switch (a.k) {
          case "go": used = this.actGo(r, a, dt); break;
          case "arm": used = this.actArm(r, a, dt); break;
          case "climb": used = this.actClimb(r, a, dt); break;
          case "take": used = this.actXfer(r, a, dt, true); break;
          case "put": used = this.actXfer(r, a, dt, false); break;
          case "present": used = this.actPresent(r, a, dt); break;
        }
        if (a.done) r.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }

    actGo(r, a, dt) {
      const c = this.cfg;
      if (!r.seg) {
        const here = this.ci(r.x, r.j);
        if (here === a.goal) { a.done = true; r.path = null; return 0; }
        if (r.waitT > 0) { r.waitT -= dt; r.stuck += dt; r.blockedTime += dt; r.state = 3; return dt; }
        if (!r.path || r.goal !== a.goal || r.pi >= r.path.length) {
          r.path = this.plan(r, a.goal, r.wait > 1.5 ? 1 : 0);
          if (!r.path) r.path = this.plan(r, a.goal, 0);
          r.pi = 0; r.goal = a.goal;
          if (r.wait > 1.5) { r.wait = 0; this.stats.replans++; }
          if (!r.path) { r.wait += dt; r.blockedTime += dt; r.state = 3; r.waitT = 0.3; return dt; }
        }
        // 같은 방향으로 이어지는 빈 셀을 할 수 있는 만큼 예약
        const d = r.path[r.pi] & 3, cells = [];
        for (let i = r.pi; i < r.path.length; i++) {
          const v = r.path[i]; if ((v & 3) !== d) break;
          const k = v >> 2, o = this.occ[k];
          if (o !== -1 && o !== r.id) break;
          cells.push(k);
        }
        if (!cells.length) {
          r.wait += dt; r.blockedTime += dt; r.state = 3;
          // 목표 칸에서 다른 로봇이 등반·토트 이동 중이면 그대로 기다림 (곧 비켜 줌)
          const nk0 = r.path[r.pi] >> 2, ob = this.robots[this.occ[nk0]];
          if (nk0 === a.goal && ob && ob.acts.length && ob.acts[0].k !== "go" && r.stuck < 20) { r.wait = 0; r.stuck += dt; return dt; }
          r.stuck += dt;
          // 서로 길을 막음 → 막은 로봇의 경로가 아닌 옆 셀로 잠시 비킴
          if (r.stuck > 2 && !a.side && this.sidestep(r, this.occ[r.path[r.pi] >> 2])) { r.stuck = 0; r.wait = 0; r.path = null; return dt; }
          if (r.wait > 1.5 && r.waitT <= 0) { r.path = null; r.waitT = 0.1 + this.rand() * 0.6; }   // 다른 길로 다시 계획
          return dt;
        }
        r.wait = 0; r.stuck = 0;
        for (const k of cells) this.occ[k] = r.id;
        let dist = 0;
        { let k = here; for (const nk of cells) { dist += this.stepLen(k, d); k = nk; } }
        const turnT = (r.lastDir !== -1 && r.lastDir !== d) ? c.turn : 0;
        r.seg = { d, cells, from: here, x0: r.px, y0: r.py, dist, t: -turnT, T: profileTime(dist, c.vmax, c.acc), rel: 0, marks: [] };
        { let k = here, acc = 0; for (const nk of cells) { acc += this.stepLen(k, d); r.seg.marks.push(acc); k = nk; } }
        r.pi += cells.length;
        r.lastDir = d;
      }
      const s = r.seg;
      s.t += dt;
      let used = dt;
      if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
      r.state = s.t < 0 ? 9 : (r.carry >= 0 ? 2 : 1);
      const pos = s.t <= 0 ? 0 : profilePos(s.t, s.dist, c.vmax, c.acc);
      r.px = s.x0 + DX[s.d] * pos; r.py = s.y0 + DY[s.d] * pos;
      // 다음 셀 중심을 지나면 이전 셀을 비움
      while (s.rel < s.cells.length - 1 && pos >= s.marks[s.rel]) {
        const prevK = s.rel === 0 ? s.from : s.cells[s.rel - 1];
        this.occ[prevK] = -1; s.rel++;
        const k = s.cells[s.rel - 1]; r.x = this.cx(k); r.j = this.cj(k);
      }
      if (s.t >= s.T) {
        while (s.rel < s.cells.length) { const prevK = s.rel === 0 ? s.from : s.cells[s.rel - 1]; this.occ[prevK] = -1; s.rel++; }
        const last = s.cells[s.cells.length - 1];
        r.x = this.cx(last); r.j = this.cj(last); this.occ[last] = r.id; this.setPos(r);
        r.dist += s.dist; r.seg = null;
        if (last === a.goal) { a.done = true; r.path = null; if (a.side) r.waitT = 0.6 + this.rand() * 0.6; }
      }
      return used;
    }

    // 막힌 로봇이 옆의 빈 셀로 잠시 비킴 (랙 밑 셀 우선)
    sidestep(r, blocker) {
      const here = this.ci(r.x, r.j), opts = [], good = [];
      const bp = blocker >= 0 ? this.robots[blocker] : null;
      for (let d = 0; d < 4; d++) {
        const nk = this.canMove(here, d);
        if (nk < 0 || this.occ[nk] !== -1 || this.kind[nk] > 2) continue;
        opts.push(nk);
        let onPath = false;
        if (bp && bp.path) for (let i = bp.pi; i < bp.path.length; i++) if ((bp.path[i] >> 2) === nk) { onPath = true; break; }
        if (bp && bp.acts.length && bp.acts[0].k === "go" && bp.acts[0].goal === nk) onPath = true;
        if (!onPath) good.push(nk);
      }
      const pick = good.length ? good : opts;
      if (!pick.length) return false;
      const k = pick[Math.floor(this.rand() * pick.length)];
      r.acts.unshift({ k: "go", goal: k, side: true });
      return true;
    }

    // 등반 팔 펴기 (a.on = 1) / 접기 (0)
    actArm(r, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) {
        a.t = 0;
        // 어느 쪽 레일을 잡는지 (렌더링용): 랙 슬롯 쪽, 작업대에서는 작업자 쪽(−y)
        if (a.on) r.side = a.slot >= 0 ? (this.laneY[this.slotLane(a.slot)] > r.py ? 1 : -1) : -1;
      }
      r.state = a.on ? 4 : 8;
      a.t += dt; let used = dt;
      if (a.t >= c.attach) { used = dt - (a.t - c.attach); a.t = c.attach; a.done = true; }
      const u = a.t / c.attach;
      r.arm = a.on ? u : 1 - u;
      return used;
    }

    actClimb(r, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) {
        a.t = 0; a.z0 = r.pz; a.d = Math.abs(a.z - r.pz); a.T = profileTime(a.d, c.climbV, c.climbAcc);
        if (a.z > r.pz) { this.stats.climbs++; this.stats.climbM += a.d; r.climbs++; }
      }
      r.state = 5;
      a.t += dt; let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
      const p = profilePos(a.t, a.d, c.climbV, c.climbAcc);
      r.pz = a.z0 + (a.z >= a.z0 ? p : -p);
      if (a.done) r.pz = a.z;
      return used;
    }

    // 포크로 토트를 슬롯 ↔ 로봇 상판으로 옮김. tote u: 0 = 슬롯, 1 = 로봇 위
    actXfer(r, a, dt, take) {
      const c = this.cfg;
      if (a.t === undefined) {
        a.t = 0;
        const lane = this.slotLane(a.slot);
        r.side = this.laneY[lane] > r.py ? 1 : -1;
        if (take) {
          const id = this.slotTote[a.slot]; this.slotTote[a.slot] = -1; r.carry = id;
          this.toteLoc[id] = { kind: 2, slot: a.slot, r: r.id, u: 0 };
        } else {
          this.toteLoc[r.carry] = { kind: 2, slot: a.slot, r: r.id, u: 1 };
        }
      }
      r.state = 6;
      a.t += dt; let used = dt;
      if (a.t >= c.xfer) { used = dt - (a.t - c.xfer); a.t = c.xfer; a.done = true; }
      const u = a.t / c.xfer;
      // 포크: 0→1 (뻗기) → 0 (당기기). 토트는 뒤 절반 동안 이동
      r.fork = take ? (u < 0.4 ? u / 0.4 : Math.max(0, 1 - (u - 0.4) / 0.6)) : (u < 0.6 ? u / 0.6 : Math.max(0, 1 - (u - 0.6) / 0.4));
      const L = this.toteLoc[r.carry];
      L.u = take ? (u < 0.4 ? 0 : Math.min(1, (u - 0.4) / 0.6)) : (u < 0.6 ? 1 - u / 0.6 : 0);
      if (a.done) {
        r.fork = 0;
        if (take) this.toteLoc[r.carry] = { kind: 1, slot: -1, r: r.id, u: 1 };
        else {
          const id = r.carry; this.slotTote[a.slot] = id; this.slotRes[a.slot] = 0;
          this.toteLoc[id] = { kind: 0, slot: a.slot, r: -1, u: 0 }; r.carry = -1;
        }
      }
      return used;
    }

    actPresent(r, a, dt) {
      const c = this.cfg, st = this.stations[a.st];
      if (a.t === undefined) {
        a.t = 0; a.T = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
        st.cur = r.id;
        const o = r.task.order; const lead = this.t - o.t;
        this.stats.done++; this.stats.leadSum += lead; if (lead > this.stats.leadMax) this.stats.leadMax = lead;
      }
      r.state = 7;
      a.t += dt; st.busyTime += dt; let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; st.cur = -1; st.picked++; }
      return used;
    }

    // ------------------------------------------------------------ 작업 순서
    // 단계: 0 꺼내기 → 1 작업대 → 2 되돌려 놓기 → 3 끝
    nextTask(r) {
      const T = r.task, c = this.cfg;
      if (T.phase === 0) {
        const s = T.slot;
        r.acts.push({ k: "go", goal: this.accessCell(s) }, { k: "arm", on: 1, slot: s },
          { k: "climb", z: this.slotZ(s) - c.deckH }, { k: "take", slot: s },
          { k: "climb", z: 0 }, { k: "arm", on: 0 });
        T.phase = 1;
      } else if (T.phase === 1) {
        const st = this.stations[T.st];
        // 작업대 칸(피킹 + 대기)이 모두 찼으면 랙 밑에서 기다림 (앞 통로를 막지 않게)
        if (st.tokens >= st.n) {
          if (this.kind[this.ci(r.x, r.j)] !== 2) this.park(r);
          r.state = 3; r.blockedTime += 0; return;
        }
        st.tokens++;
        this.releasePark(r);
        r.acts.push({ k: "go", goal: this.ci(st.x0, -1) }, { k: "arm", on: 1, slot: -1 }, { k: "climb", z: c.presentZ },
          { k: "present", st: T.st }, { k: "climb", z: 0 }, { k: "arm", on: 0 });
        T.phase = 2;
      } else if (T.phase === 2) {
        this.stations[T.st].assigned--; this.stations[T.st].tokens--;
        // 작업대에서 나온 뒤 되돌려 놓을 빈 칸을 고름 (가까움 + 낮음)
        const s = this.chooseReturn(r);
        this.slotRes[s] = 1;
        const st = this.stations[T.st];
        r.acts.push({ k: "go", goal: this.ci(st.x0, 0) }, { k: "go", goal: this.accessCell(s) }, { k: "arm", on: 1, slot: s },
          { k: "climb", z: this.slotZ(s) - c.deckH }, { k: "put", slot: s }, { k: "climb", z: 0 }, { k: "arm", on: 0 });
        T.phase = 3;
      } else {
        this.toteBusy[T.tote] = 0;
        r.task = null; r.deliveries++;
        this.park(r);
      }
    }

    chooseReturn(r) {
      const c = this.cfg; let best = -1, bs = Infinity;
      this.goalCount.fill(0);
      for (const o of this.robots) for (const a of o.acts) if (a.k === "go") { this.goalCount[a.goal]++; break; }
      const pop = r.carry >= 0 && r.carry < this.nPopular;
      for (let s = 0; s < this.NS; s++) {
        if (this.slotTote[s] !== -1 || this.slotRes[s]) continue;
        const k = this.accessCell(s);
        const dx = Math.abs(this.worldX(this.cx(k)) - r.px), dy = Math.abs(this.worldY(this.cj(k)) - r.py);
        const h = this.slotZ(s) - c.deckH;
        // 다른 로봇이 가려는 통로 칸 근처는 피함 (한 통로에 몰리지 않게)
        const sc = (dx + dy) / c.vmax + h / c.climbV * (pop ? 2 : 0.6) + this.goalCount[k] * 6 + (this.occ[k] !== -1 ? 3 : 0);
        if (sc < bs) { bs = sc; best = s; }
      }
      return best;
    }
    slotY(s) { return this.laneY[this.slotLane(s)]; }

    // 할 일이 없으면 가장 가까운 랙 밑 빈 셀로 비켜 섬
    park(r) {
      const here = this.ci(r.x, r.j);
      if (this.kind[here] === 2) return;
      let best = -1, bd = Infinity;
      for (let k = 0; k < this.NC; k++) {
        if (this.kind[k] !== 2 || this.occ[k] !== -1 || this.parkRes[k] !== -1) continue;
        const d = Math.abs(this.cx(k) - r.x) * this.cfg.cellX + Math.abs(this.worldY(this.cj(k)) - r.py);
        if (d < bd) { bd = d; best = k; }
      }
      if (best < 0) return;
      this.parkRes[best] = r.id;
      r.acts.push({ k: "go", goal: best, park: true });
    }

    releasePark(r) { for (let q = 0; q < this.NC; q++) if (this.parkRes[q] === r.id) this.parkRes[q] = -1; }

    // ------------------------------------------------------------ 주문
    newOrder() {
      const c = this.cfg;
      for (let tries = 0; tries < 12; tries++) {
        const id = this.rand() < c.popularDemand ? Math.floor(this.rand() * this.nPopular)
          : this.nPopular + Math.floor(this.rand() * (this.totes - this.nPopular));
        if (this.toteBusy[id]) continue;
        this.toteBusy[id] = 1;
        this.pending.push({ tote: id, t: this.t });
        this.stats.arrived++;
        return;
      }
    }

    dispatch() {
      const c = this.cfg;
      while (this.pending.length) {
        let st = null;
        for (const s of this.stations) if (s.assigned < c.stationCap && (!st || s.assigned < st.assigned)) st = s;
        if (!st) return;
        const o = this.pending[0], L = this.toteLoc[o.tote];
        const k = this.accessCell(L.slot);
        const gx = this.worldX(this.cx(k)), gy = this.worldY(this.cj(k));
        let best = null, bd = Infinity;
        for (const r of this.robots) {
          if (r.task || r.seg) continue;
          if (r.acts.length && !(r.acts.length === 1 && r.acts[0].park)) continue;
          const d = Math.abs(r.px - gx) + Math.abs(r.py - gy);
          if (d < bd) { bd = d; best = r; }
        }
        if (!best) return;
        this.pending.shift();
        if (best.acts.length) { const pk = best.acts[0].goal; if (this.parkRes[pk] === best.id) this.parkRes[pk] = -1; best.acts.length = 0; best.path = null; }
        this.releasePark(best);
        st.assigned++;
        best.task = { order: o, tote: o.tote, slot: L.slot, st: st.id, phase: 0 };
      }
    }

    step(dt) {
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.dispatch();
      for (const r of this.robots) {
        this.stepRobot(r, dt);
        if (!r.task && !r.acts.length && !r.seg) { const k = this.ci(r.x, r.j); if (this.parkRes[k] === r.id) this.parkRes[k] = -1; }
      }
    }

    run(seconds, dt) { dt = dt || 1 / 60; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    // 토트 위치 (바닥 밑면 중심) — 렌더링용
    slotPos(s) { const c = this.cfg; return [this.worldX(this.slotX(s)), this.slotY(s), this.slotZ(s)]; }
    totePose(id) {
      const L = this.toteLoc[id], c = this.cfg;
      if (L.kind === 0) return this.slotPos(L.slot);
      const r = this.robots[L.r], top = [r.px, r.py, r.pz + c.deckH];
      if (L.kind === 1) return top;
      const sp = this.slotPos(L.slot), u = L.u;
      return [top[0], sp[1] + (top[1] - sp[1]) * u, sp[2] + (top[2] - sp[2]) * u];
    }

    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats, n = this.robots.length;
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: s.arrived,
        ordersDone: s.done,
        throughput_totesPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        maxLeadTime_s: +s.leadMax.toFixed(1),
        backlog: this.pending.length,
        robotUtil: +(this.robots.reduce((a, r) => a + r.busyTime, 0) / (n * T)).toFixed(3),
        robotWaitShare: +(this.robots.reduce((a, r) => a + r.blockedTime, 0) / (n * T)).toFixed(3),
        stationUtil: +(this.stations.reduce((a, st) => a + st.busyTime, 0) / (this.stations.length * T)).toFixed(3),
        climbsPerHour: +(s.climbs / T * 3600).toFixed(0),
        avgClimb_m: s.climbs ? +(s.climbM / s.climbs).toFixed(2) : null,
        replans: s.replans,
        perRobotTotesPerHour: +((s.done / T * 3600) / n).toFixed(1),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos, STATES };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.HaiPickSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
