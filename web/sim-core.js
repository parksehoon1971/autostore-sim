/*
 * AutoStore 큐브 스토리지 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(unity/Assets/AutoStore/Scripts/AutoStoreSim.cs)과 같은 로직입니다.
 *
 * 좌표: 셀 (x, y). y = -1 행은 포트 셀(그리드 앞 가장자리)이며 PORTS에 있는 x만 존재.
 * 시간: 초. 거리: 미터.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 12, ny: 8, nz: 16,          // 그리드 셀 수, 스택 최대 빈 수
    cellX: 0.70, cellY: 0.50,       // 셀 피치 (근사)
    binH: 0.33,                     // 빈 높이 (330 mm 빈)
    fill: 0.80,                     // 초기 채움률
    robots: 8,
    ports: [2, 5, 8],               // 포트가 붙는 x 인덱스
    portZ: 0.95,                    // 포트 빈 제시 높이
    portCap: 4,                     // 포트당 배정 가능 빈 수 (도착 대기 + 처리 중)
    orderRate: 300,                 // 주문(빈 요청)/시간
    popularShare: 0.2,              // 인기 빈 비율 (ABC)
    popularDemand: 0.8,             // 인기 빈이 차지하는 수요 비율
    vmax: 3.1, acc: 0.8,            // 로봇 최고속도 m/s, 가속도 m/s² (R5 공개 사양)
    turn: 1.0,                      // 방향 전환(바퀴 세트 교체) 시간 s
    liftV: 1.6,                     // 그리퍼 승강 속도 m/s
    grip: 1.0,                      // 집기/놓기 시간 s
    pickMin: 6, pickMax: 12,        // 포트 피킹 시간 s
    seed: 1,
  };

  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  // 사다리꼴 속도 프로파일: 거리 d를 이동하는 데 걸리는 시간과 t초 시점 위치
  function profileTime(d, v, a) {
    if (d <= 0) return 0;
    const dAcc = v * v / a;          // 가속+감속 거리 합
    return d >= dAcc ? d / v + v / a : 2 * Math.sqrt(d / a);
  }
  function profilePos(t, d, v, a) {
    const T = profileTime(d, v, a);
    if (t >= T) return d;
    if (t <= 0) return 0;
    let vp = v, ta = v / a;
    if (d < v * v / a) { vp = Math.sqrt(d * a); ta = vp / a; }
    if (t < ta) return 0.5 * a * t * t;
    const dA = 0.5 * a * ta * ta;
    const tc = T - 2 * ta;
    if (t < ta + tc) return dA + vp * (t - ta);
    const td = T - t;
    return d - 0.5 * a * td * td;
  }

  const DIRS = [[1, 0], [-1, 0], [0, 1], [0, -1]];

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      this.topZ = c.nz * c.binH + 0.10;           // 레일 상면
      this.restZ = this.topZ + 0.05;              // 로봇 안에 든 빈 바닥 높이
      this.W = c.nx; this.H = c.ny + 1;           // 셀 배열 (y+1)
      this.portSet = new Set(c.ports);
      this.occ = new Int32Array(this.W * this.H).fill(-1);
      this.stacks = [];                            // [x + y*nx] -> bin id 배열(아래->위)
      this.incoming = new Int32Array(c.nx * c.ny);
      this.binLoc = [];                            // id -> {kind:'stack'|'robot'|'port', x,y | r | p}
      this.requested = new Set();
      this.bins = 0;
      this.version = 0;                            // 빈 배치가 바뀔 때마다 증가 (렌더링용)
      // --- 빈 초기 배치
      const slots = [];
      for (let y = 0; y < c.ny; y++) for (let x = 0; x < c.nx; x++) {
        const s = []; this.stacks.push(s);
        let h = Math.round(c.nz * Math.min(1, Math.max(0, c.fill + this.gauss() * 0.12)));
        if (h >= c.nz) h = c.nz - 1;               // 맨 위 한 칸은 비워 둠 (작업 공간)
        for (let k = 0; k < h; k++) slots.push([x, y, k]);
      }
      // ID 배정: 운영이 안정된 상태처럼 인기 빈(작은 ID)이 위쪽에 오도록 (높이 + 잡음 순 정렬)
      const keyed = slots.map(([x, y, k]) => ({ x, y, k, key: k + this.gauss() * c.nz * 0.25 }));
      keyed.sort((a, b) => b.key - a.key);
      keyed.forEach((s, id) => { s.id = id; });
      keyed.sort((a, b) => a.k - b.k);
      keyed.forEach(s => { this.stacks[s.x + s.y * c.nx].push(s.id); this.binLoc[s.id] = { kind: "stack", x: s.x, y: s.y }; });
      this.bins = slots.length;
      this.nPopular = Math.max(1, Math.floor(this.bins * c.popularShare));
      // --- 포트
      this.ports = c.ports.map((x, i) => ({ id: i, x, y: -1, queue: [], pickLeft: -1, out: -1, outClaimed: false,
        reserved: 0, busyTime: 0, picked: 0 }));
      // --- 로봇
      this.robots = [];
      for (let i = 0; i < c.robots; i++) {
        let x, y;
        do { x = Math.floor(this.rand() * c.nx); y = Math.floor(this.rand() * c.ny); } while (this.occ[this.ci(x, y)] !== -1);
        const r = { id: i, x, y, px: 0, py: 0, g: 0, carry: -1, task: null, acts: [], seg: null, lastDir: -1,
          blocked: 0, waitT: 0, busyTime: 0, digs: 0, deliveries: 0, returns: 0, dist: 0, state: "idle" };
        this.setPos(r); this.occ[this.ci(x, y)] = i; this.robots.push(r);
      }
      // --- 주문
      this.orders = []; this.pending = []; this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { done: 0, leadSum: 0, waitSum: 0, digs: 0, fetches: 0, leadMax: 0 };
    }

    gauss() { let u = 0, v = 0; while (!u) u = this.rand(); v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    ci(x, y) { return x + (y + 1) * this.W; }
    valid(x, y) { return x >= 0 && x < this.cfg.nx && ((y >= 0 && y < this.cfg.ny) || (y === -1 && this.portSet.has(x))); }
    stack(x, y) { return this.stacks[x + y * this.cfg.nx]; }
    worldX(x) { return (x + 0.5) * this.cfg.cellX; }
    worldY(y) { return (y + 0.5) * this.cfg.cellY; }
    setPos(r) { r.px = this.worldX(r.x); r.py = this.worldY(r.y); }

    // ------------------------------------------------------------ 경로 계획 (방향 전환 비용 포함 다익스트라)
    plan(r, tx, ty, avoid) {
      const c = this.cfg, N = this.W * this.H * 4;
      const cost = new Float64Array(N).fill(Infinity), prev = new Int32Array(N).fill(-1), done = new Uint8Array(N);
      const sc = this.ci(r.x, r.y);
      const stepT = [c.cellX / c.vmax + 0.05, c.cellX / c.vmax + 0.05, c.cellY / c.vmax + 0.05, c.cellY / c.vmax + 0.05];
      for (let d = 0; d < 4; d++) cost[sc * 4 + d] = (r.lastDir === -1 || (r.lastDir >> 1) === (d >> 1)) ? 0 : c.turn;
      const target = this.ci(tx, ty);
      let best = -1;
      for (;;) {
        let u = -1, bu = Infinity;
        for (let i = 0; i < N; i++) if (!done[i] && cost[i] < bu) { bu = cost[i]; u = i; }
        if (u < 0) break;
        done[u] = 1;
        const cell = u >> 2, d0 = u & 3;
        if (cell === target) { best = u; break; }
        const x = cell % this.W, y = Math.floor(cell / this.W) - 1;
        for (let d = 0; d < 4; d++) {
          const nx = x + DIRS[d][0], ny = y + DIRS[d][1];
          if (!this.valid(nx, ny)) continue;
          const nc = this.ci(nx, ny);
          if (avoid && this.occ[nc] !== -1 && this.occ[nc] !== r.id && nc !== target) continue;
          const turn = ((d0 >> 1) === (d >> 1)) ? 0 : c.turn;
          const v = nc * 4 + d, nc2 = bu + stepT[d] + turn;
          if (nc2 < cost[v]) { cost[v] = nc2; prev[v] = u; }
        }
      }
      if (best < 0) return null;
      const path = []; let u = best;
      while (u >= 0 && (u >> 2) !== sc) { path.push(u); u = prev[u]; }
      path.reverse();
      return path.map(v => { const cell = v >> 2; return { x: cell % this.W, y: Math.floor(cell / this.W) - 1, d: v & 3 }; });
    }

    // ------------------------------------------------------------ 행동 실행
    // 행동: {k:'move',x,y} | {k:'lift',src:'stack'|'port'} | {k:'lower',dst:'stack'|'port'} | {k:'wait',t}
    stepRobot(r, dt) {
      if (r.task || r.acts.length || r.seg) r.busyTime += dt;
      let guard = 0;
      while (dt > 1e-9 && guard++ < 8) {
        if (!r.acts.length) { if (r.task) r.task.next(r); if (!r.acts.length) { r.state = r.task ? r.state : "idle"; return; } }
        const a = r.acts[0];
        const used = this["act_" + a.k](r, a, dt);
        if (a.done) r.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }

    act_wait(r, a, dt) { a.t -= dt; if (a.t <= 0) { a.done = true; return dt + a.t; } return dt; }

    act_move(r, a, dt) {
      const c = this.cfg;
      if (!r.seg) {
        if (r.x === a.x && r.y === a.y) { a.done = true; return 0; }
        if (r.waitT > 0) { r.waitT -= dt; r.blocked += dt; return dt; }
        let path = this.plan(r, a.x, a.y, r.blocked > 1.5);
        if (!path || !path.length) path = this.plan(r, a.x, a.y, false);
        if (!path || !path.length) { r.blocked += dt; return dt; }
        // 첫 직선 구간 + 예약 가능한 만큼
        const d = path[0].d; const cells = [];
        for (const p of path) { if (p.d !== d) break; const k = this.ci(p.x, p.y); const o = this.occ[k]; if (o !== -1 && o !== r.id) break; cells.push(p); }
        if (!cells.length) {
          r.blocked += dt; r.state = "blocked";
          const o = this.occ[this.ci(path[0].x, path[0].y)];
          if (o >= 0) this.nudge(this.robots[o], r, path);
          if (r.blocked > 6) { this.sidestep(r); r.blocked = 3; }
          r.waitT = 0.25;
          return dt;
        }
        r.blocked = 0;
        for (const p of cells) this.occ[this.ci(p.x, p.y)] = r.id;
        const step = (d >> 1) === 0 ? c.cellX : c.cellY;
        const dist = cells.length * step;
        const turnT = (r.lastDir !== -1 && (r.lastDir >> 1) !== (d >> 1)) ? c.turn : 0;
        r.seg = { d, cells, x0: r.x, y0: r.y, dist, step, t: -turnT, T: profileTime(dist, c.vmax, c.acc), released: 0 };
        r.lastDir = d; r.state = r.carry >= 0 ? "carry" : "move";
      }
      const s = r.seg;
      s.t += dt;
      let used = dt;
      if (s.t >= s.T) { used = dt - (s.t - s.T); s.t = s.T; }
      const pos = s.t <= 0 ? 0 : profilePos(s.t, s.dist, c.vmax, c.acc);
      const [dx, dy] = DIRS[s.d];
      r.px = this.worldX(s.x0) + dx * pos * (s.d >> 1 ? 0 : 1);
      r.py = this.worldY(s.y0) + dy * pos * (s.d >> 1 ? 1 : 0);
      // 지나온 셀 해제 (현재 위치 셀 이전까지)
      const passed = Math.floor((pos + s.step * 0.5) / s.step);  // 현재 점유 중심 셀 인덱스(0=출발)
      while (s.released < passed) {
        const prevCell = s.released === 0 ? { x: s.x0, y: s.y0 } : s.cells[s.released - 1];
        this.occ[this.ci(prevCell.x, prevCell.y)] = -1; s.released++;
        r.x = s.cells[s.released - 1].x; r.y = s.cells[s.released - 1].y;
      }
      if (s.t >= s.T) {
        while (s.released < s.cells.length) {
          const prevCell = s.released === 0 ? { x: s.x0, y: s.y0 } : s.cells[s.released - 1];
          this.occ[this.ci(prevCell.x, prevCell.y)] = -1; s.released++;
        }
        const last = s.cells[s.cells.length - 1];
        r.x = last.x; r.y = last.y; this.occ[this.ci(r.x, r.y)] = r.id; this.setPos(r);
        r.dist += s.dist; r.seg = null;
        if (r.x === a.x && r.y === a.y) a.done = true;
      }
      return used;
    }

    // 막고 있는 유휴 로봇을 비켜나게 함
    nudge(o, r, path) {
      if (o.task || o.acts.length || o.seg) return;
      const onPath = new Set(path.map(p => this.ci(p.x, p.y)));
      const opts = [];
      for (const [dx, dy] of DIRS) {
        const nx = o.x + dx, ny = o.y + dy;
        if (!this.valid(nx, ny) || ny < 0) continue;
        const k = this.ci(nx, ny);
        if (this.occ[k] !== -1 || onPath.has(k)) continue;
        opts.push({ x: nx, y: ny });
      }
      if (!opts.length) for (const [dx, dy] of DIRS) {
        const nx = o.x + dx, ny = o.y + dy;
        if (this.valid(nx, ny) && ny >= 0 && this.occ[this.ci(nx, ny)] === -1) opts.push({ x: nx, y: ny });
      }
      if (opts.length) { const p = opts[Math.floor(this.rand() * opts.length)]; o.acts.push({ k: "move", x: p.x, y: p.y }); }
    }

    // 교착 시 임의 이웃 셀로 비켜섬 (현재 행동 앞에 끼워 넣음)
    sidestep(r) {
      const opts = [];
      for (const [dx, dy] of DIRS) {
        const nx = r.x + dx, ny = r.y + dy;
        if (this.valid(nx, ny) && this.occ[this.ci(nx, ny)] === -1) opts.push({ x: nx, y: ny });
      }
      if (opts.length) { const p = opts[Math.floor(this.rand() * opts.length)]; r.acts.unshift({ k: "move", x: p.x, y: p.y }); }
    }

    // 그리퍼 승강: 내려가서(dist/v) 집기/놓기(grip) 올라옴(dist/v). 중간에 빈 이동.
    gripDist(r, kind, isLift) {
      const c = this.cfg;
      if (kind === "port") return this.restZ - c.portZ;
      const h = this.stack(r.x, r.y).length;
      return this.restZ - (isLift ? (h - 1) : h) * c.binH;
    }
    act_lift(r, a, dt) { return this.gripAct(r, a, dt, true); }
    act_lower(r, a, dt) { return this.gripAct(r, a, dt, false); }
    gripAct(r, a, dt, isLift) {
      const c = this.cfg;
      if (a.T === undefined) {
        a.dist = this.gripDist(r, isLift ? a.src : a.dst, isLift);
        a.td = a.dist / c.liftV; a.T = 2 * a.td + c.grip; a.t = 0; a.swapped = false;
        r.state = isLift ? "lift" : "lower";
      }
      a.t += dt;
      let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; }
      if (a.t < a.td) r.g = a.dist * a.t / a.td;
      else if (a.t < a.td + c.grip) r.g = a.dist;
      else r.g = a.dist * (1 - (a.t - a.td - c.grip) / a.td);
      if (!a.swapped && a.t >= a.td + c.grip * 0.5) {
        a.swapped = true;
        if (isLift) {
          if (a.src === "stack") { const id = this.stack(r.x, r.y).pop(); r.carry = id; }
          else { const p = this.portAt(r.x); r.carry = p.out; p.out = -1; p.outClaimed = false; }
          this.binLoc[r.carry] = { kind: "robot", r: r.id };
        } else {
          const id = r.carry; r.carry = -1;
          if (a.dst === "stack") {
            this.stack(r.x, r.y).push(id); this.incoming[r.x + r.y * c.nx]--;
            this.binLoc[id] = { kind: "stack", x: r.x, y: r.y };
          } else {
            const p = this.portAt(r.x); p.queue.push(id); p.reserved--; this.binLoc[id] = { kind: "port", p: p.id };
          }
        }
        this.version++;
      }
      if (a.t >= a.T) { a.done = true; r.g = 0; }
      return used;
    }

    portAt(x) { return this.ports.find(p => p.x === x); }

    // 빈을 놓을 가장 가까운 스택 (공간 있음, 제외 셀 제외)
    findDrop(fromX, fromY, exclude) {
      const c = this.cfg; let best = null, bd = Infinity;
      for (let y = 0; y < c.ny; y++) for (let x = 0; x < c.nx; x++) {
        if (exclude && exclude.x === x && exclude.y === y) continue;
        const k = x + y * c.nx;
        if (this.stacks[k].length + this.incoming[k] >= c.nz - 1) continue;
        // 요청된 빈이 있는 스택은 피함 (불필요한 디깅 방지)
        let pen = 0; for (const id of this.stacks[k]) if (this.requested.has(id)) { pen = 3; break; }
        const o = this.occ[this.ci(x, y)]; if (o !== -1) pen += 1.5;
        const d = Math.abs(x - fromX) * c.cellX + Math.abs(y - fromY) * c.cellY + pen + this.stacks[k].length * 0.01;
        if (d < bd) { bd = d; best = { x, y }; }
      }
      if (best) this.incoming[best.x + best.y * c.nx]++;
      return best;
    }

    // ------------------------------------------------------------ 작업 (상태 기계)
    fetchTask(order, port) {
      const sim = this; let phase = "goto";
      return {
        kind: "fetch", order, port, target: order.bin,
        next(r) {
          const c = sim.cfg;
          if (phase === "goto") {
            const L = sim.binLoc[order.bin];
            if (r.x !== L.x || r.y !== L.y) { r.acts.push({ k: "move", x: L.x, y: L.y }); return; }
            const s = sim.stack(r.x, r.y);
            const depth = s.length - 1 - s.indexOf(order.bin);
            if (depth > 0) {
              // 디깅: 위의 빈을 들어 인접 스택에 옮김
              const home = { x: r.x, y: r.y };
              const drop = sim.findDrop(r.x, r.y, home);
              r.acts.push({ k: "lift", src: "stack" }, { k: "move", x: drop.x, y: drop.y }, { k: "lower", dst: "stack" });
              r.digs++; sim.stats.digs++; r.state = "dig";
              return;
            }
            r.acts.push({ k: "lift", src: "stack" }); phase = "deliver"; return;
          }
          if (phase === "deliver") {
            r.acts.push({ k: "move", x: port.x, y: -1 }, { k: "lower", dst: "port" });
            phase = "after"; order.tDelivered = null; return;
          }
          if (phase === "after") {
            r.deliveries++; sim.stats.fetches++;
            // 교환: 포트에 처리 끝난 빈이 있으면 바로 가져감
            if (port.out >= 0 && !port.outClaimed) { port.outClaimed = true; r.task = sim.returnTask(port); r.task.next(r); return; }
            r.task = null; sim.leavePort(r); return;
          }
        },
      };
    }

    returnTask(port) {
      const sim = this; let phase = "goto";
      return {
        kind: "return", port,
        next(r) {
          if (phase === "goto") { r.acts.push({ k: "move", x: port.x, y: -1 }, { k: "lift", src: "port" }); phase = "store"; return; }
          if (phase === "store") {
            const d = sim.findDrop(r.x, Math.max(0, r.y), null);
            r.acts.push({ k: "move", x: d.x, y: d.y }, { k: "lower", dst: "stack" }); phase = "end"; return;
          }
          if (phase === "end") { r.returns++; r.task = null; }
        },
      };
    }

    leavePort(r) { if (r.y === -1) r.acts.push({ k: "move", x: r.x, y: 0 }); }

    // ------------------------------------------------------------ 주문 / 포트 / 배차
    newOrder() {
      const c = this.cfg;
      for (let tries = 0; tries < 40; tries++) {
        const pop = this.rand() < c.popularDemand;
        const id = pop ? Math.floor(this.rand() * this.nPopular) : this.nPopular + Math.floor(this.rand() * (this.bins - this.nPopular));
        if (this.requested.has(id) || this.binLoc[id].kind !== "stack") continue;
        this.requested.add(id);
        const o = { id: this.orders.length, bin: id, tArrive: this.t, tPresent: -1, tDone: -1 };
        this.orders.push(o); this.pending.push(o); return;
      }
    }

    stepPorts(dt) {
      const c = this.cfg;
      for (const p of this.ports) {
        if (p.queue.length) {
          p.busyTime += dt;
          const id = p.queue[0];
          if (p.pickLeft < 0) {
            p.pickLeft = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
            const o = this.orderByBin(id); if (o) o.tPresent = this.t;
          }
          p.pickLeft -= dt;
          if (p.pickLeft <= 0 && p.out < 0) {
            p.queue.shift(); p.out = id; p.outClaimed = false; p.pickLeft = -1; p.picked++;
            const o = this.orderByBin(id);
            if (o) {
              o.tDone = this.t; this.requested.delete(id); this.stats.done++;
              const lead = o.tPresent - o.tArrive; this.stats.leadSum += lead; this.stats.leadMax = Math.max(this.stats.leadMax, lead);
              this.activeOrders.delete(id);
            }
          }
        }
      }
    }
    orderByBin(id) { return this.activeOrders.get(id); }

    dispatch() {
      const c = this.cfg;
      const idle = this.robots.filter(r => !r.task && !r.acts.length && !r.seg);
      const dist = (r, x, y) => Math.abs(r.x - x) * c.cellX + Math.abs(r.y - y) * c.cellY;
      // 1) 포트에서 처리 끝난 빈 회수
      for (const p of this.ports) {
        if (p.out >= 0 && !p.outClaimed && idle.length) {
          idle.sort((a, b) => dist(a, p.x, -1) - dist(b, p.x, -1));
          const r = idle.shift(); p.outClaimed = true; r.task = this.returnTask(p);
        }
      }
      // 2) 주문 → 빈 인출
      while (this.pending.length && idle.length) {
        const o = this.pending[0];
        const L = this.binLoc[o.bin];
        let port = null, best = Infinity;
        for (const p of this.ports) {
          const load = p.queue.length + p.reserved;
          if (load >= c.portCap) continue;
          const sc = load * 100 + Math.abs(p.x - L.x);
          if (sc < best) { best = sc; port = p; }
        }
        if (!port) break;
        idle.sort((a, b) => dist(a, L.x, L.y) - dist(b, L.x, L.y));
        const r = idle.shift();
        this.pending.shift(); port.reserved++;
        this.activeOrders.set(o.bin, o);
        r.task = this.fetchTask(o, port);
      }
    }

    step(dt) {
      if (!this.activeOrders) this.activeOrders = new Map();
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.dispatch();
      for (const r of this.robots) this.stepRobot(r, dt);
      this.stepPorts(dt);
    }

    run(seconds, dt) { dt = dt || 0.05; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats;
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: this.orders.length,
        ordersDone: s.done,
        throughput_binsPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        maxLeadTime_s: +s.leadMax.toFixed(1),
        backlog: this.pending.length,
        digsPerFetch: s.fetches ? +(s.digs / s.fetches).toFixed(2) : null,
        robotUtil: +(this.robots.reduce((a, r) => a + r.busyTime, 0) / (this.robots.length * T)).toFixed(3),
        portUtil: +(this.ports.reduce((a, p) => a + p.busyTime, 0) / (this.ports.length * T)).toFixed(3),
        perRobotBinsPerHour: +((s.fetches / T * 3600) / this.robots.length).toFixed(1),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.AutoStoreSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
