/*
 * Rapyuta ASRS 방식 다층 셀 스토리지 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/Rapyuta/Scripts/RapyutaSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 *
 * 구조: 층(f) × 셀(x, y). 층마다 바닥 패널 위에 빈이 한 개씩 놓이고(셀당 1개, 위로 쌓지 않음),
 * 높이 80 mm의 얇은 로봇이 빈 아래를 지나다닙니다. 빈을 들어 올린 로봇은 빈이 없는 셀로만 다닐 수 있어서
 * 안쪽 빈을 꺼내려면 앞을 막은 빈을 먼저 옆으로 옮깁니다. 층 사이는 리프트로 오가고,
 * 피킹층(stationFloor) 앞쪽 스테이션 슬롯에 빈을 내려놓으면 작업자가 피킹합니다.
 *
 * 셀 종류: 0 없음, 1 보관, 2 통로, 3 리프트, 4 스테이션 슬롯
 * 좌표: 셀 중심 wx = (x+0.5)*cellX, wy = (y+0.5)*cellY, 층 바닥 z = baseZ + f*floorH. 시간 s, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 16, ny: 13, nf: 7,            // 셀 수 (x, y), 층 수
    cellX: 0.70, cellY: 0.50,         // 셀 피치 (빈 650 x 440 mm 기준 가정)
    floorH: 0.63, baseZ: 0.12,        // 층 높이 = 기둥 630 mm (공식), 바닥 블록 높이 (가정)
    binH: 0.40, binFoot: 0.10,        // 빈 높이 400 mm (공식 3종 중 중간), 로봇이 지나갈 다리 높이 (가정)
    robotH: 0.08, stroke: 0.035,      // 로봇 높이 80 mm (공식), 들어 올리는 높이 (가정)
    fill: 0.78,
    reserveFree: 12,                   // 층마다 남겨 둘 빈 셀 (앞 빈을 옮겨 놓을 자리)
    robots: 20,
    aisleStep: 6,                      // y % aisleStep == 0 인 행은 통로 (빈 없음). 사이 5행은 빈이 빽빽한 블록
    bufferRows: 2,                     // 피킹층 앞쪽 빈 없는 완충 행 수
    lifts: [[-1, 3], [-1, 6], [-1, 9], [16, 4], [16, 8]], // 리프트 셀 (그리드 좌우 바깥), 접근 셀은 가장자리 통로 열
    stationFloor: 1,                   // 피킹층
    stations: [[3, 4], [9, 4]],        // [시작 x, 슬롯 수] — y = -1 행, 작업자 2명 x 슬롯 4 = 8개
    orderRate: 600,                    // 빈 요청 / 시간
    popularShare: 0.2, popularDemand: 0.8,
    vmax: 2.5, acc: 1.2,               // 로봇 2.5 m/s (공식), 가속도 (가정)
    turn: 0.25,                        // 메카넘 휠: 방향 바꿀 때 정지 없이 0.25 s 손실로 근사
    liftV: 1.6, liftAcc: 1.6, liftSettle: 0.4, // 리프트 (가정)
    grip: 0.9,                         // 빈 들기/내리기 시간 (가정)
    pickMin: 5, pickMax: 10,           // 작업자 피킹 시간 / 빈
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
  const VOID = 0, STORE = 1, AISLE = 2, LIFT = 3, SLOT = 4;

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      this.W = c.nx + 2; this.H = c.ny + 1;     // x: -1..nx, y: -1..ny-1
      this.FN = this.W * this.H;
      const N = this.FN * c.nf;
      this.type = new Uint8Array(N);
      this.binAt = new Int32Array(N).fill(-1);
      this.occ = new Int32Array(N).fill(-1);
      this.incoming = new Uint8Array(N);
      this.prot = new Int32Array(N);            // 다른 로봇이 막지 않도록 보호한 셀 (꺼내는 경로)
      for (let f = 0; f < c.nf; f++) {
        for (let y = 0; y < c.ny; y++) for (let x = 0; x < c.nx; x++)
          this.type[this.ci(f, x, y)] = (y % c.aisleStep === 0 || x <= 1 || x >= c.nx - 2 ||
            (f === c.stationFloor && y <= c.bufferRows)) ? AISLE : STORE;   // 피킹층 앞쪽은 빈 없는 완충 구역
      }
      // 리프트
      this.lifts = c.lifts.map(([x, y], i) => {
        for (let f = 0; f < c.nf; f++) this.type[this.ci(f, x, y)] = LIFT;
        const ax = x < 0 ? 0 : c.nx - 1;
        return { id: i, x, y, ax, ay: y, z: this.deckZ(0), f: 0, from: 0, to: 0, t: 0, T: 0, moving: false,
                 owner: -1, queue: [], trips: 0 };
      });
      // 스테이션 (피킹층 앞 y = -1)
      this.slots = []; this.stations = [];
      c.stations.forEach(([x0, n], si) => {
        const st = { id: si, slots: [], busyTime: 0, picked: 0, cur: -1, pickLeft: -1 };
        for (let k = 0; k < n; k++) {
          const s = { id: this.slots.length, st: si, f: c.stationFloor, x: x0 + k, y: -1, bin: -1, reserved: false,
                      ready: false, claimed: false, tArrive: 0 };
          this.type[this.ci(s.f, s.x, s.y)] = SLOT;
          this.slots.push(s); st.slots.push(s);
        }
        this.stations.push(st);
      });
      // 빈 초기 배치 (인기 빈 = 작은 ID 가 낮은 층·통로 가까이에 오도록)
      const cells = [];
      for (let f = 0; f < c.nf; f++) for (let y = 0; y < c.ny; y++) for (let x = 0; x < c.nx; x++) {
        if (this.type[this.ci(f, x, y)] !== STORE) continue;
        if (this.rand() < c.fill) cells.push({ f, x, y, key: this.depth(f, x, y) * 2 + f * 0.8 + this.gauss() * 1.5 });
      }
      cells.sort((a, b) => a.key - b.key);
      this.binLoc = [];
      cells.forEach((s, id) => { this.binAt[this.ci(s.f, s.x, s.y)] = id; this.binLoc[id] = { kind: "cell", f: s.f, x: s.x, y: s.y }; });
      this.bins = cells.length;
      this.nPopular = Math.max(1, Math.floor(this.bins * c.popularShare));
      this.locked = new Set();                   // 로봇이 옮기기로 한 빈
      this.requested = new Set();
      // 로봇: 층마다 고르게, 빈 아래에 주차된 상태로 시작
      this.robots = [];
      for (let i = 0; i < c.robots; i++) {
        const f = i % c.nf;
        let x, y, k;
        do { x = 2 + Math.floor(this.rand() * (c.nx - 4)); y = Math.floor(this.rand() * c.ny); k = this.ci(f, x, y); }
        while (this.occ[k] !== -1 || this.type[k] !== STORE);
        const r = { id: i, f, x, y, px: 0, py: 0, pz: 0, lift: 0, carry: -1, task: null, acts: [], seg: null, lastDir: -1,
          blocked: 0, waitT: 0, busyTime: 0, blockedTime: 0, digs: 0, deliveries: 0, returns: 0, rides: 0, dist: 0,
          state: "idle", onLift: -1, parked: false };
        this.setPos(r); this.occ[k] = i; this.robots.push(r);
      }
      this.orders = []; this.pending = []; this.active = new Map();
      this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { done: 0, leadSum: 0, leadMax: 0, digs: 0, fetches: 0, reliefs: 0 };
    }

    // ------------------------------------------------------------ 기본
    gauss() { let u = 0; while (!u) u = this.rand(); const v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    ci(f, x, y) { return f * this.FN + (x + 1) + (y + 1) * this.W; }
    inb(x, y) { return x >= -1 && x <= this.cfg.nx && y >= -1 && y < this.cfg.ny; }
    typeAt(f, x, y) { return this.inb(x, y) ? this.type[this.ci(f, x, y)] : VOID; }
    worldX(x) { return (x + 0.5) * this.cfg.cellX; }
    worldY(y) { return (y + 0.5) * this.cfg.cellY; }
    deckZ(f) { return this.cfg.baseZ + f * this.cfg.floorH; }
    setPos(r) { r.px = this.worldX(r.x); r.py = this.worldY(r.y); r.pz = this.deckZ(r.f); }
    // 가장 가까운 통로까지 빈 몇 개를 지나야 하는지 (블록 안 깊이, 0 = 통로 옆)
    depth(f, x, y) { const s = this.cfg.aisleStep, m = y % s; return Math.min(m, s - m) - 1; }

    // ------------------------------------------------------------ 경로 계획 (한 층, 방향 전환 비용 포함 다익스트라)
    // 빈을 든 로봇의 일방통행 규칙: 통로 행은 번갈아 +x / -x, 좌우 2열 척추 통로는 바깥 열·안쪽 열이 반대 방향
    rowDir(y) { return (Math.floor(y / this.cfg.aisleStep) % 2 === 0) ? 0 : 1; }          // DIRS 인덱스
    colDir(x) { const n = this.cfg.nx; return (x === 0 || x === n - 2) ? 3 : 2; }
    isSpine(x) { const n = this.cfg.nx; return x === 0 || x === 1 || x === n - 2 || x === n - 1; }
    isRow(x, y) { return y >= 0 && y % this.cfg.aisleStep === 0 && !this.isSpine(x) && x >= 0 && x < this.cfg.nx; }
    oneWayOk(fx, fy, x, y, d) {
      if ((d >> 1) === 0) { if ((this.isRow(fx, fy) || this.isRow(x, y)) && fy === y && y % this.cfg.aisleStep === 0) return d === this.rowDir(y); return true; }
      if (this.isSpine(x) && fx === x && y >= 0 && fy >= 0) return d === this.colDir(x);
      return true;
    }

    passable(f, x, y, loaded, target, fx, fy, d) {
      if (!this.inb(x, y)) return false;
      const k = this.ci(f, x, y), ty = this.type[k];
      if (ty === VOID) return false;
      if (k === target) return true;
      if (ty === LIFT || ty === SLOT) return false;
      if (loaded && this.binAt[k] !== -1) return false;
      if (loaded && d !== undefined && !this.oneWayOk(fx, fy, x, y, d)) return false;
      return true;
    }

    plan(r, tx, ty, avoid) {
      const c = this.cfg, FN = this.FN, f = r.f, base = f * FN, N = FN * 4;
      const loaded = r.carry >= 0;
      const cost = new Float64Array(N).fill(Infinity), prev = new Int32Array(N).fill(-1), done = new Uint8Array(N);
      const sc = this.ci(f, r.x, r.y) - base, target = this.ci(f, tx, ty), tl = target - base;
      const stepT = [c.cellX / c.vmax + 0.04, c.cellX / c.vmax + 0.04, c.cellY / c.vmax + 0.04, c.cellY / c.vmax + 0.04];
      for (let d = 0; d < 4; d++) cost[sc * 4 + d] = (r.lastDir === -1 || r.lastDir === d) ? 0 : c.turn;
      let best = -1;
      for (;;) {
        let u = -1, bu = Infinity;
        for (let i = 0; i < N; i++) if (!done[i] && cost[i] < bu) { bu = cost[i]; u = i; }
        if (u < 0) break;
        done[u] = 1;
        const cell = u >> 2, d0 = u & 3;
        if (cell === tl) { best = u; break; }
        const x = cell % this.W - 1, y = Math.floor(cell / this.W) - 1;
        for (let d = 0; d < 4; d++) {
          const nx = x + DIRS[d][0], ny = y + DIRS[d][1];
          if (!this.passable(f, nx, ny, loaded, target, x, y, d)) continue;
          const nk = this.ci(f, nx, ny);
          if (avoid && this.occ[nk] !== -1 && this.occ[nk] !== r.id && nk !== target) continue;
          const v = (nk - base) * 4 + d, nc = bu + stepT[d] + (d0 === d ? 0 : c.turn);
          if (nc < cost[v]) { cost[v] = nc; prev[v] = u; }
        }
      }
      if (best < 0) return null;
      const path = []; let u = best;
      while (u >= 0 && (u >> 2) !== sc) { path.push(u); u = prev[u]; }
      path.reverse();
      return path.map(v => { const cell = v >> 2; return { x: cell % this.W - 1, y: Math.floor(cell / this.W) - 1, d: v & 3 }; });
    }

    // 빈 없이 다닐 수 있는 셀만으로 (f, x0, y0) 에서 닿는 셀까지의 단계 수 (BFS)
    reach(f, x0, y0, rid) {
      const dist = new Int32Array(this.FN).fill(-1), q = [this.ci(f, x0, y0) - f * this.FN];
      dist[q[0]] = 0;
      for (let h = 0; h < q.length; h++) {
        const cell = q[h], x = cell % this.W - 1, y = Math.floor(cell / this.W) - 1;
        for (const [dx, dy] of DIRS) {
          const nx = x + dx, ny = y + dy;
          if (!this.inb(nx, ny)) continue;
          const k = this.ci(f, nx, ny), ty = this.type[k];
          if (ty !== STORE && ty !== AISLE) continue;
          if (this.binAt[k] !== -1) continue;
          if (rid !== undefined && this.occ[k] !== -1 && this.occ[k] !== rid) continue;   // 로봇이 서 있는 셀도 막힘으로 봄
          const kl = k - f * this.FN;
          if (dist[kl] >= 0) continue;
          dist[kl] = dist[cell] + 1; q.push(kl);
        }
      }
      return dist;
    }

    // 목표 빈에서 통로까지 꺼내는 경로: 0-1 BFS (빈이 있는 셀 = 비용 1). 다른 로봇이 쓰는 빈은 지나지 않음
    clearPath(f, x0, y0, target) {
      const FN = this.FN, base = f * FN, dist = new Int32Array(FN).fill(1 << 20), prev = new Int32Array(FN).fill(-1);
      const s = this.ci(f, x0, y0) - base; dist[s] = 0;
      const dq = [s]; let head = 0, goal = -1;
      const done = new Uint8Array(FN);
      while (head < dq.length) {
        // 간단한 0-1 BFS: 비용 순으로 꺼내기 위해 매번 최소를 찾음 (셀 수가 작음)
        let bi = -1, bd = 1 << 21;
        for (let i = head; i < dq.length; i++) if (!done[dq[i]] && dist[dq[i]] < bd) { bd = dist[dq[i]]; bi = i; }
        if (bi < 0) break;
        const cell = dq[bi]; dq[bi] = dq[head]; dq[head] = cell; head++;
        if (done[cell]) continue; done[cell] = 1;
        const x = cell % this.W - 1, y = Math.floor(cell / this.W) - 1, k = cell + base;
        if (this.type[k] === AISLE) { goal = cell; break; }
        for (const [dx, dy] of DIRS) {
          const nx = x + dx, ny = y + dy;
          if (!this.inb(nx, ny)) continue;
          const nk = this.ci(f, nx, ny), ty = this.type[nk];
          if (ty !== STORE && ty !== AISLE) continue;
          const b = this.binAt[nk];
          if (b !== -1 && (this.locked.has(b) || this.requested.has(b))) continue;
          const w = b !== -1 ? 1 : 0, nl = nk - base;
          if (dist[cell] + w < dist[nl]) { dist[nl] = dist[cell] + w; prev[nl] = cell; dq.push(nl); }
        }
      }
      if (goal < 0) return null;
      const cells = []; let u = goal;
      while (u >= 0) { cells.push(u + base); u = prev[u]; }
      return cells;   // 통로 쪽 → 목표 쪽 순서
    }

    // ------------------------------------------------------------ 행동 실행
    stepRobot(r, dt) {
      if (r.task || r.acts.length || r.seg) r.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!r.acts.length) { if (r.task) r.task.next(r); if (!r.acts.length) { if (!r.task) r.state = "idle"; return; } }
        const a = r.acts[0];
        const used = this["act_" + a.k](r, a, dt);
        if (a.done) { const i = r.acts.indexOf(a); if (i >= 0) r.acts.splice(i, 1); }
        dt -= used;
        if (used === 0 && !a.done && r.acts[0] === a) return;
      }
    }

    act_wait(r, a, dt) { a.t -= dt; if (a.t <= 0) { a.done = true; return dt + a.t; } return dt; }

    act_move(r, a, dt) {
      const c = this.cfg;
      if (!r.seg) {
        if (r.x === a.x && r.y === a.y) { a.done = true; return 0; }
        if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; r.blockedTime += dt; return dt; }
        let path = this.plan(r, a.x, a.y, r.blocked > 1.2);
        if (!path || !path.length) path = this.plan(r, a.x, a.y, false);
        if (!path || !path.length) {
          r.blocked += dt; r.blockedTime += dt; r.state = "blocked"; r.waitT = 0.5;
          if (this.relieve(r, a)) return dt;
          if (a.spot && r.blocked > 1) { this.incoming[this.ci(r.f, a.x, a.y)] = 0; a.done = true; r.blocked = 0; r.waitT = 0; return dt; }
          // 빈을 놓으러 가는데 길이 막힘(그 사이 다른 빈이 놓임) → 이 층에서 닿는 다른 빈 셀로 바꿈
          if (a.drop && r.carry >= 0 && r.blocked > 1) {
            const nd = this.findDrop(r, false, this.ci(r.f, a.x, a.y));
            if (nd) { this.incoming[this.ci(r.f, a.x, a.y)] = 0; a.x = nd.x; a.y = nd.y; r.blocked = 0; }
            else if (r.task && r.task.rehome) {   // 이 층에는 닿는 자리가 없음 → 다른 층으로
              this.incoming[this.ci(r.f, a.x, a.y)] = 0; this.clearActs(r); r.blocked = 0; r.waitT = 0;
              r.task.rehome(r); a.done = true; return dt;
            }
          }
          return dt;
        }
        const d = path[0].d, cells = [];
        for (const p of path) { if (p.d !== d) break; const o = this.occ[this.ci(r.f, p.x, p.y)]; if (o !== -1 && o !== r.id) break; cells.push(p); }
        if (!cells.length) {
          r.blocked += dt; r.blockedTime += dt; r.state = "blocked";
          // 빈을 놓으러 가다 다른 로봇과 막혀 서로 못 지나가면 놓을 자리를 바꿔 길을 비켜 줌
          if (a.spot && r.blocked > 4) { this.incoming[this.ci(r.f, a.x, a.y)] = 0; a.done = true; r.blocked = 0; return dt; }
          if (a.drop && r.carry >= 0 && r.blocked > 2.5) {
            const old = this.ci(r.f, a.x, a.y);
            const nd = this.findDrop(r, false, old);
            if (nd) { this.incoming[old] = 0; a.x = nd.x; a.y = nd.y; r.blocked = 0; r.waitT = 0.2; return dt; }
            if (r.task && r.task.rehome) {
              this.incoming[old] = 0; this.clearActs(r); r.blocked = 0; r.waitT = 0;
              r.task.rehome(r); a.done = true; return dt;
            }
          }
          const o = this.occ[this.ci(r.f, path[0].x, path[0].y)];
          if (o >= 0) this.nudge(this.robots[o], r, path);
          if (this.relieve(r, a)) return dt;
          if (r.blocked > 5) { this.sidestep(r); r.blocked = 2; }
          r.waitT = 0.2;
          return dt;
        }
        r.blocked = 0;
        for (const p of cells) this.occ[this.ci(r.f, p.x, p.y)] = r.id;
        const step = (d >> 1) === 0 ? c.cellX : c.cellY;
        const dist = cells.length * step;
        const turnT = (r.lastDir !== -1 && r.lastDir !== d) ? c.turn : 0;
        r.seg = { d, cells, x0: r.x, y0: r.y, dist, step, t: -turnT, T: profileTime(dist, c.vmax, c.acc), released: 0 };
        r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move"; r.parked = false;
      }
      const s = r.seg;
      s.t += dt;
      let used = dt;
      if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
      const pos = s.t <= 0 ? 0 : profilePos(s.t, s.dist, c.vmax, c.acc);
      const [dx, dy] = DIRS[s.d];
      r.px = this.worldX(s.x0) + dx * pos;
      r.py = this.worldY(s.y0) + dy * pos;
      const passed = Math.floor((pos + s.step * 0.5) / s.step);
      while (s.released < Math.min(passed, s.cells.length)) {
        const pc = s.released === 0 ? { x: s.x0, y: s.y0 } : s.cells[s.released - 1];
        this.occ[this.ci(r.f, pc.x, pc.y)] = -1; s.released++;
        r.x = s.cells[s.released - 1].x; r.y = s.cells[s.released - 1].y;
      }
      if (s.t >= s.T) {
        while (s.released < s.cells.length) {
          const pc = s.released === 0 ? { x: s.x0, y: s.y0 } : s.cells[s.released - 1];
          this.occ[this.ci(r.f, pc.x, pc.y)] = -1; s.released++;
        }
        const last = s.cells[s.cells.length - 1];
        r.x = last.x; r.y = last.y; this.occ[this.ci(r.f, r.x, r.y)] = r.id; this.setPos(r);
        r.dist += s.dist; r.seg = null;
        if (r.x === a.x && r.y === a.y) a.done = true;
      }
      return used;
    }

    // 행동 목록을 비움. 기다리던 리프트 차례/사용권도 반납
    clearActs(r) {
      for (const a of r.acts) if (a.k === "ride") {
        const L = this.lifts[a.lift], i = L.queue.indexOf(r.id);
        if (i >= 0) L.queue.splice(i, 1);
        if (L.owner === r.id) L.owner = -1;
      }
      r.acts.length = 0;
    }

    // 교착 해소: 빈을 든 로봇이 보관 셀 안에서 오래 막히면 빈을 그 자리에 내려놓고 작업을 다시 계획
    // (빈이 없으면 로봇은 다른 빈 아래로 지나갈 수 있으므로 대부분 풀림)
    relieve(r, a) {
      if (r.carry < 0 || r.blocked < 8 || !r.task || !r.task.relief) return false;
      const k = this.ci(r.f, r.x, r.y);
      if (this.type[k] !== STORE || this.binAt[k] !== -1) return false;
      if (a.drop) this.incoming[this.ci(r.f, a.x, a.y)] = 0;
      this.clearActs(r); r.blocked = 0; r.waitT = 0;
      r.acts.push({ k: "lower" });
      r.task.relief(r);
      this.stats.reliefs++;
      return true;
    }

    // 길을 막은 유휴 로봇을 옆 셀(빈 아래도 가능)로 비킴
    nudge(o, r, path) {
      if (o.task || o.acts.length || o.seg) return;
      const onPath = new Set(path.map(p => this.ci(o.f, p.x, p.y)));
      const opts = [], any = [];
      for (let d = 0; d < 4; d++) {
        const nx = o.x + DIRS[d][0], ny = o.y + DIRS[d][1];
        if (!this.passable(o.f, nx, ny, o.carry >= 0, -1, o.x, o.y, d)) continue;
        const k = this.ci(o.f, nx, ny);
        if (this.occ[k] !== -1) continue;
        any.push({ x: nx, y: ny });
        if (!onPath.has(k)) opts.push({ x: nx, y: ny });
      }
      const pick = opts.length ? opts : any;
      if (pick.length) { const p = pick[Math.floor(this.rand() * pick.length)]; o.acts.push({ k: "move", x: p.x, y: p.y }); }
    }

    sidestep(r) {
      const opts = [];
      for (let d = 0; d < 4; d++) {
        const nx = r.x + DIRS[d][0], ny = r.y + DIRS[d][1];
        if (this.passable(r.f, nx, ny, r.carry >= 0, -1, r.x, r.y, d) && this.occ[this.ci(r.f, nx, ny)] === -1) opts.push({ x: nx, y: ny });
      }
      if (opts.length) { const p = opts[Math.floor(this.rand() * opts.length)]; r.acts.unshift({ k: "move", x: p.x, y: p.y }); }
    }

    // 빈 들기/내리기: 로봇 상판이 stroke 만큼 오르내림. 중간에 빈 소유가 바뀜
    act_lift(r, a, dt) { return this.gripAct(r, a, dt, true); }
    act_lower(r, a, dt) { return this.gripAct(r, a, dt, false); }
    gripAct(r, a, dt, isLift) {
      const c = this.cfg;
      if (a.t === undefined) { a.t = 0; a.swapped = false; r.state = isLift ? "lift" : "lower"; }
      a.t += dt;
      let used = dt;
      if (a.t >= c.grip) { used = dt - (a.t - c.grip); a.t = c.grip; }
      const u = a.t / c.grip;
      r.lift = c.stroke * (isLift ? Math.min(1, u * 1.6) : Math.max(0, 1 - u * 1.6));
      const k = this.ci(r.f, r.x, r.y);
      if (!a.swapped && u >= 0.5) {
        a.swapped = true;
        if (isLift) {
          const id = this.binAt[k]; this.binAt[k] = -1; r.carry = id; this.binLoc[id] = { kind: "robot", r: r.id };
          const s = this.slotAt(r.f, r.x, r.y); if (s) { s.bin = -1; s.ready = false; s.claimed = false; }
        } else {
          const id = r.carry; r.carry = -1; this.binAt[k] = id; this.incoming[k] = 0;
          this.binLoc[id] = { kind: "cell", f: r.f, x: r.x, y: r.y };
          this.locked.delete(id);
          const s = this.slotAt(r.f, r.x, r.y); if (s) { s.bin = id; s.tArrive = this.t; s.reserved = false; }
        }
      }
      if (a.t >= c.grip) { a.done = true; r.lift = isLift ? c.stroke : 0; }
      return used;
    }

    slotAt(f, x, y) { if (y !== -1 || f !== this.cfg.stationFloor) return null; return this.slots.find(s => s.x === x) || null; }

    // ------------------------------------------------------------ 리프트
    // ride: 리프트 사용권을 받을 때까지 제자리 대기 → 접근 셀 → 리프트 셀 → 승강 → 접근 셀 → 반납
    act_ride(r, a, dt) {
      const L = this.lifts[a.lift];
      if (a.st === undefined) { a.st = 0; if (!L.queue.includes(r.id) && L.owner !== r.id) L.queue.push(r.id); }
      if (a.st === 0) {
        if (L.owner !== r.id) {
          // 차례를 기다리는 동안 통로를 막지 않음: 빈 로봇은 가까운 빈 아래로 비켜서 기다림
          // 빈을 든 로봇은 닿을 수 있는 빈 셀로 들어가서 기다림
          if (this.type[this.ci(r.f, r.x, r.y)] === AISLE && (!a.moved || (a.wt = (a.wt || 0) + dt) > 3)) {
            a.moved = true; a.wt = 0;
            const p = r.carry < 0 ? this.nearStore(r) : this.findDrop(r, false, -2);
            if (p) { if (r.carry >= 0) a.waitK = this.ci(r.f, p.x, p.y); r.acts.unshift({ k: "move", x: p.x, y: p.y, spot: true }); return 0; }
          }
          r.state = "liftwait"; r.blockedTime += dt; return dt;
        }
        if (a.waitK !== undefined) { this.incoming[a.waitK] = 0; a.waitK = undefined; }
        a.st = 1;
      }
      if (a.st === 1) { a.st = 2; r.acts.unshift({ k: "move", x: L.ax, y: L.ay }); return 0; }
      if (a.st === 2) {   // 접근 셀 도착. 플랫폼이 이 층에 올 때까지 대기
        if (L.moving || L.f !== r.f) { if (!L.moving) this.liftGo(L, r.f); r.state = "liftwait"; return dt; }
        a.st = 3; r.acts.unshift({ k: "move", x: L.x, y: L.y }); return 0;
      }
      if (a.st === 3) {   // 리프트 셀 안. 승강 시작
        r.onLift = L.id; r.state = "ride"; r.rides++;
        this.occ[this.ci(r.f, L.x, L.y)] = -1;
        this.liftGo(L, a.to); a.st = 4;
      }
      if (a.st === 4) {
        r.pz = L.z; r.px = this.worldX(L.x); r.py = this.worldY(L.y);
        if (L.moving) return dt;
        r.f = a.to; r.onLift = -1; this.occ[this.ci(r.f, L.x, L.y)] = r.id; this.setPos(r);
        a.st = 5; r.acts.unshift({ k: "move", x: L.ax, y: L.ay }); return 0;
      }
      if (a.st === 5) { L.owner = -1; a.done = true; return 0; }
      return dt;
    }

    liftGo(L, f) {
      if (L.f === f && !L.moving) return;
      L.from = L.z; L.to = f; L.t = 0;
      L.T = profileTime(Math.abs(this.deckZ(f) - L.z), this.cfg.liftV, this.cfg.liftAcc) + this.cfg.liftSettle;
      L.moving = true; L.trips++;
    }

    stepLifts(dt) {
      const c = this.cfg;
      for (const L of this.lifts) {
        if (L.owner < 0 && L.queue.length) { L.owner = L.queue.shift(); L.grantT = 0; }
        // 사용권을 받은 로봇이 대기열 로봇들에 막혀 접근 셀에 못 오면 차례를 뒤로 미룸 (교착 방지)
        if (L.owner >= 0 && !L.moving) {
          L.grantT = (L.grantT || 0) + dt;
          const o = this.robots[L.owner], a = o.acts.find(q => q.k === "ride" && q.lift === L.id);
          if (L.grantT > 12 && a && (a.st === 2 || a.st === 1) && !o.seg && L.queue.length) {
            const m = o.acts[0];
            if (m && m.k === "move" && m.x === L.ax && m.y === L.ay) o.acts.shift();
            a.st = 0; a.moved = false; L.queue.push(o.id); L.owner = L.queue.shift(); L.grantT = 0;
          }
        }
        if (!L.moving) continue;
        L.t += dt;
        const D = this.deckZ(L.to) - L.from, T = L.T - c.liftSettle;
        L.z = L.from + Math.sign(D) * profilePos(Math.min(L.t, T), Math.abs(D), c.liftV, c.liftAcc);
        if (L.t >= L.T) { L.moving = false; L.f = L.to; L.z = this.deckZ(L.f); }
      }
    }

    // 층 이동에 쓸 리프트: 접근 거리 + 대기열 길이
    pickLift(r) {
      let best = null, bs = Infinity;
      for (const L of this.lifts) {
        const s = Math.abs(r.x - L.ax) * this.cfg.cellX + Math.abs(r.y - L.ay) * this.cfg.cellY + (L.queue.length + (L.owner >= 0 ? 1 : 0)) * 6;
        if (s < bs) { bs = s; best = L; }
      }
      return best.id;
    }
    ride(r, f) { r.acts.push({ k: "ride", lift: this.pickLift(r), to: f }); }

    // ------------------------------------------------------------ 빈 놓을 자리
    // 빈을 든 로봇이 지금 닿을 수 있는 빈 셀 중 가까운 곳. 통로 바로 옆은 되도록 비워 둠
    findDrop(r, avoidDepth0, notK) {
      const f = r.f, dist = this.reach(f, r.x, r.y, notK !== undefined ? r.id : undefined);
      let best = null, bd = Infinity;
      for (let y = 0; y < this.cfg.ny; y++) for (let x = 0; x < this.cfg.nx; x++) {
        const k = this.ci(f, x, y);
        if (this.type[k] !== STORE || this.binAt[k] !== -1 || this.incoming[k] || this.prot[k] || k === notK) continue;
        const d = dist[k - f * this.FN]; if (d < 0) continue;
        const o = this.occ[k];
        const s = d + (o !== -1 && o !== r.id ? 3 : 0) + (avoidDepth0 && this.depth(f, x, y) === 0 ? 2 : 0);
        if (s < bd) { bd = s; best = { f, x, y }; }
      }
      if (best) this.incoming[this.ci(best.f, best.x, best.y)] = 1;
      return best;
    }

    // 피킹 끝난 빈을 돌려놓을 자리: 모든 층. 인기 빈은 낮은 층·통로 옆, 나머지는 높은 층·안쪽
    findHome(r, id, skipF) {
      const c = this.cfg, pop = id < this.nPopular;
      let best = null, bs = Infinity;
      for (let f = 0; f < c.nf; f++) {
        if (f === skipF) continue;
        // 층마다 빈 셀 몇 개는 디깅용으로 남김. 피킹층은 통행이 많아 더 넉넉히 비워 둠
        if (this.emptyCells(f) <= c.reserveFree * (f === c.stationFloor ? 2 : 1)) continue;
        let sx = r.x, sy = r.y;
        if (f !== r.f) { const L = this.lifts[this.pickLift(r)]; sx = L.ax; sy = L.ay; }
        const dist = this.reach(f, sx, sy);
        const lift = f === r.f ? 0 : 7 + Math.abs(this.deckZ(f) - this.deckZ(r.f)) / c.liftV;
        for (let y = 0; y < c.ny; y++) for (let x = 0; x < c.nx; x++) {
          const k = this.ci(f, x, y);
          if (this.type[k] !== STORE || this.binAt[k] !== -1 || this.incoming[k] || this.prot[k]) continue;
          const d = dist[k - f * this.FN]; if (d < 0) continue;
          const dep = this.depth(f, x, y);
          const s = lift + d * 0.4 + (pop ? dep * 3 + f * 1.5 : (1 - dep) * 2 + (c.nf - 1 - f) * 0.8);
          if (s < bs) { bs = s; best = { f, x, y }; }
        }
      }
      if (best) this.incoming[this.ci(best.f, best.x, best.y)] = 1;
      return best;
    }

    nearStore(r) {
      const c = this.cfg; let best = null, bd = Infinity;
      for (let y = 0; y < c.ny; y++) for (let x = 2; x < c.nx - 2; x++) {
        const k = this.ci(r.f, x, y);
        if (this.type[k] !== STORE || this.occ[k] !== -1) continue;
        const d = Math.abs(x - r.x) + Math.abs(y - r.y);
        if (d < bd) { bd = d; best = { x, y }; }
      }
      return best;
    }

    emptyCells(f) {
      let n = 0;
      for (let y = 0; y < this.cfg.ny; y++) for (let x = 0; x < this.cfg.nx; x++) {
        const k = this.ci(f, x, y);
        if (this.type[k] === STORE && this.binAt[k] === -1 && !this.incoming[k]) n++;
      }
      return n;
    }

    park(r) {
      // 유휴 로봇은 가까운 빈 아래(통로 아님)에 주차
      const c = this.cfg; let best = null, bd = Infinity;
      for (let y = 0; y < c.ny; y++) for (let x = 2; x < c.nx - 2; x++) {
        const k = this.ci(r.f, x, y);
        if (this.type[k] !== STORE || this.occ[k] !== -1 || this.binAt[k] === -1) continue;
        const d = Math.abs(x - r.x) + Math.abs(y - r.y) + this.rand() * 0.5;
        if (d < bd) { bd = d; best = { x, y }; }
      }
      if (best) { r.acts.push({ k: "move", x: best.x, y: best.y }); r.parked = true; }
    }

    // ------------------------------------------------------------ 작업
    // 슬롯으로 빈을 가져옴. 슬롯에 아직 앞 빈이 있으면 빈을 든 채 보관 셀 안에서 기다렸다가 출발
    fetchTask(order, slot) {
      const sim = this; let phase = "goto", path = null; const st = slot.st;
      const unprotect = () => { if (path) for (const k of path) sim.prot[k]--; path = null; };
      return {
        kind: "fetch", order, slot,
        next(r) {
          const c = sim.cfg;
          if (phase === "goto") {
            const L = sim.binLoc[order.bin];
            if (r.f !== L.f) { sim.ride(r, L.f); return; }
            unprotect();
            const cells = sim.clearPath(L.f, L.x, L.y, order.bin);
            if (!cells) { r.acts.push({ k: "wait", t: 1 }); return; }
            path = cells; for (const k of path) sim.prot[k]++;
            // 통로 쪽에서 가장 바깥에 있는 막는 빈부터 옮김
            let blk = -1;
            for (const k of cells) { const b = sim.binAt[k]; if (b !== -1 && b !== order.bin) { blk = k; break; } }
            if (blk >= 0) {
              const b = sim.binAt[blk], bx = (blk % sim.FN) % sim.W - 1, by = Math.floor((blk % sim.FN) / sim.W) - 1;
              sim.locked.add(b);
              r.acts.push({ k: "move", x: bx, y: by }, { k: "lift" }, { k: "dig", bin: b });
              r.digs++; sim.stats.digs++;
              return;
            }
            if (r.x !== L.x || r.y !== L.y) { r.acts.push({ k: "move", x: L.x, y: L.y }); return; }
            r.acts.push({ k: "lift" }); phase = "deliver"; return;
          }
          if (phase === "deliver") {
            if (!this.go) {
              if (slot.bin >= 0 || sim.occ[sim.ci(slot.f, slot.x, slot.y)] !== -1) { r.state = "hold"; r.acts.push({ k: "wait", t: 0.5 }); return; }
              this.go = true;
            }
            if (r.f !== c.stationFloor) { sim.ride(r, c.stationFloor); return; }
            unprotect();
            r.acts.push({ k: "move", x: slot.x, y: 0 }, { k: "move", x: slot.x, y: slot.y }, { k: "lower" });
            phase = "after"; return;
          }
          if (phase === "after") {
            unprotect();
            r.deliveries++; sim.stats.fetches++;
            r.task = null;
            // 이 스테이션에 피킹 끝난 빈이 있으면 바로 회수 (교환)
            const s = sim.slots.find(q => q.ready && !q.claimed && q.st === slot.st) || sim.slots.find(q => q.ready && !q.claimed);
            if (s) { s.claimed = true; r.task = sim.returnTask(s); r.task.next(r); return; }
            r.acts.push({ k: "move", x: slot.x, y: 0 });
            sim.park(r);
          }
        },
        relief(r) { unprotect(); phase = "goto"; },
      };
    }

    // 꺼내는 경로를 막은 빈을 들었음 → 가까운 빈 셀에 내려놓음
    act_dig(r, a, dt) {
      const d = this.findDrop(r, true);
      if (!d) {
        r.state = "blocked"; r.blockedTime += dt; a.wt = (a.wt || 0) + dt;
        // 놓을 자리가 없으면 제자리에 내려놓고 잠시 뒤 다시 계획
        if (a.wt > 3) { a.done = true; r.acts.splice(1, 0, { k: "lower" }, { k: "wait", t: 2 }); return 0; }
        return dt;
      }
      a.done = true;
      r.acts.splice(1, 0, { k: "move", x: d.x, y: d.y, drop: true }, { k: "lower" });
      return 0;
    }

    returnTask(slot) {
      const sim = this; let phase = "goto", skipF = -1;
      return {
        kind: "return", slot,
        rehome(r) { this.dest = null; skipF = r.f; phase = "home"; },
        relief(r) { this.dest = null; phase = "end"; },
        next(r) {
          const c = sim.cfg;
          if (phase === "goto") {
            if (r.f !== slot.f) { sim.ride(r, slot.f); return; }
            r.acts.push({ k: "move", x: slot.x, y: 0 }, { k: "move", x: slot.x, y: slot.y }, { k: "lift" });
            phase = "home"; return;
          }
          if (phase === "home") {
            if (!this.dest) {
              this.dest = sim.findHome(r, r.carry, skipF);
              if (!this.dest) { r.acts.push({ k: "wait", t: 1 }); return; }
            }
            if (r.f !== this.dest.f) { sim.ride(r, this.dest.f); return; }
            r.acts.push({ k: "move", x: this.dest.x, y: this.dest.y, drop: true }, { k: "lower" });
            phase = "end"; return;
          }
          if (phase === "end") { r.returns++; r.task = null; sim.park(r); }
        },
      };
    }

    // ------------------------------------------------------------ 주문 / 스테이션 / 배차
    newOrder() {
      const c = this.cfg;
      if (this.pending.length > 40) return;
      for (let tries = 0; tries < 40; tries++) {
        const pop = this.rand() < c.popularDemand;
        const id = pop ? Math.floor(this.rand() * this.nPopular) : this.nPopular + Math.floor(this.rand() * (this.bins - this.nPopular));
        const B = this.binLoc[id];
        if (this.requested.has(id) || this.locked.has(id) || B.kind !== "cell" || this.type[this.ci(B.f, B.x, B.y)] !== STORE) continue;
        this.requested.add(id);
        const o = { id: this.orders.length, bin: id, tArrive: this.t, tPresent: -1, tDone: -1 };
        this.orders.push(o); this.pending.push(o); return;
      }
    }

    stepStations(dt) {
      const c = this.cfg;
      for (const st of this.stations) {
        if (st.cur < 0) {
          // 도착 순서대로 다음 빈
          let best = null;
          for (const s of st.slots) if (s.bin >= 0 && !s.ready && (!best || s.tArrive < best.tArrive)) best = s;
          if (!best) continue;
          st.cur = best.id; st.pickLeft = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
          const o = this.active.get(best.bin); if (o) o.tPresent = this.t;
        }
        st.busyTime += dt;
        st.pickLeft -= dt;
        if (st.pickLeft <= 0) {
          const s = this.slots[st.cur]; st.cur = -1; s.ready = true; s.claimed = false; st.picked++;
          const o = this.active.get(s.bin);
          if (o) {
            o.tDone = this.t; this.requested.delete(s.bin); this.active.delete(s.bin); this.stats.done++;
            const lead = o.tPresent - o.tArrive; this.stats.leadSum += lead; this.stats.leadMax = Math.max(this.stats.leadMax, lead);
          }
        }
      }
    }

    dispatch() {
      const c = this.cfg;
      const idle = this.robots.filter(r => !r.task && !r.seg && (r.acts.length === 0 || r.parked) && r.onLift < 0);
      const cost = (r, f, x, y) => Math.abs(r.x - x) * c.cellX + Math.abs(r.y - y) * c.cellY + (r.f === f ? 0 : 6 + Math.abs(r.f - f) * 0.5);
      const take = (r) => { idle.splice(idle.indexOf(r), 1); r.acts = r.acts.filter(a => a.k !== "move" || !r.parked); r.parked = false; };
      // 1) 피킹 끝난 빈 회수
      for (const s of this.slots) {
        if (!s.ready || s.claimed || !idle.length) continue;
        idle.sort((a, b) => cost(a, s.f, s.x, s.y) - cost(b, s.f, s.x, s.y));
        const r = idle[0]; take(r); s.claimed = true; r.task = this.returnTask(s);
      }
      // 2) 주문 → 빈 인출. 예약 안 된 슬롯 (앞 빈을 피킹 중이어도 됨 → 미리 가져와 대기)
      while (this.pending.length && idle.length) {
        let slot = null, bs = Infinity;
        for (const s of this.slots) {
          if (s.reserved) continue;
          const sc = this.stationLoad(s.st) * 10 + (s.bin < 0 ? 0 : s.ready ? 1 : 2) * 3 + s.id * 0.01;
          if (sc < bs) { bs = sc; slot = s; }
        }
        if (!slot) break;
        const o = this.pending.shift(), L = this.binLoc[o.bin];
        idle.sort((a, b) => cost(a, L.f, L.x, L.y) - cost(b, L.f, L.x, L.y));
        const r = idle[0]; take(r);
        slot.reserved = true; this.active.set(o.bin, o);
        r.task = this.fetchTask(o, slot);
      }
    }
    stationLoad(si) { return this.stations[si].slots.reduce((n, s) => n + (s.reserved ? 1 : 0) + (s.bin >= 0 && !s.ready ? 1 : 0), 0); }

    step(dt) {
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.dispatch();
      this.stepLifts(dt);
      for (const r of this.robots) this.stepRobot(r, dt);
      this.stepStations(dt);
    }

    run(seconds, dt) { dt = dt || 0.05; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    // 빈 위치 (바닥 다리 밑면 기준) — 렌더링용
    binPose(id) {
      const L = this.binLoc[id];
      if (L.kind === "robot") { const r = this.robots[L.r]; return [r.px, r.py, r.pz + r.lift]; }
      return [this.worldX(L.x), this.worldY(L.y), this.deckZ(L.f)];
    }

    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats, n = this.robots.length;
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: this.orders.length,
        ordersDone: s.done,
        throughput_binsPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        backlog: this.pending.length,
        digsPerFetch: s.fetches ? +(s.digs / s.fetches).toFixed(2) : null,
        robotUtil: +(this.robots.reduce((a, r) => a + r.busyTime, 0) / (n * T)).toFixed(3),
        robotWaitShare: +(this.robots.reduce((a, r) => a + r.blockedTime, 0) / (n * T)).toFixed(3),
        stationUtil: +(this.stations.reduce((a, st) => a + st.busyTime, 0) / (this.stations.length * T)).toFixed(3),
        liftTripsPerHour: +(this.lifts.reduce((a, L) => a + L.trips, 0) / T * 3600).toFixed(0),
        reliefs: s.reliefs,
        perRobotBinsPerHour: +((s.fetches / T * 3600) / n).toFixed(1),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos, VOID, STORE, AISLE, LIFT, SLOT };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.RapyutaSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
