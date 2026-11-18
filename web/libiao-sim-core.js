/*
 * Libiao(立镖) 방식 3D 소터 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/Libiao/Scripts/LibiaoSim.cs)과 같은 로직입니다. 한쪽을 고치면 다른 쪽도 고치세요.
 *
 * 구조
 *  - 강철 플랫폼(데크) 위 셀 격자 nx x ny. aisleStep(3)마다 있는 행/열이 로봇 통로, 나머지 셀은 슈트(구멍).
 *    → 2x2 슈트 블록이 통로로 둘러싸인 형태. 슈트 아래 아래층에 자루(백)가 걸려 있음.
 *  - 통로는 일방통행(외곽은 반시계 순환, 안쪽은 번갈아). 충돌 회피는 셀 예약.
 *  - 투입 스테이션: 데크 앞/뒤의 U자 분기. 앞: (s,0)->(s,-1)->(s,-2)[적재]->(s+1,-2)->(s+1,-1)->(s+1,0)
 *    뒤: (s,ny-1)->(s,ny)->(s,ny+1)[적재]->(s-1,ny+1)->(s-1,ny)->(s-1,ny-1). 작업자는 그 바깥 적재대 앞에 섬.
 *  - 로봇: 스테이션에서 작업자가 소포를 올림 → 목적지 슈트 옆 통로 셀로 이동 → 트레이를 옆으로 기울여 투하
 *    → 소포가 슈트로 떨어져 아래층 자루에 쌓임 → 가까운 스테이션으로 복귀.
 * 좌표: 셀 (i, j) 중심 = ((i+0.5)*cell, (j+0.5)*cell). z 위. 시간 초, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    nx: 19, ny: 13,            // 데크 셀 수
    cell: 0.70,                // 셀 피치 m (근사)
    aisleStep: 3,              // 통로 간격 (3 → 슈트 2x2 블록)
    deckZ: 2.40,               // 데크 상면 높이 m (근사)
    stations: [2, 8, 14],      // 앞쪽(y<0) 투입 스테이션 x 인덱스 (s, s+1 사용)
    stationsBack: [16, 10, 4], // 뒤쪽(y>=ny) 투입 스테이션 x 인덱스 (s, s-1 사용)
    robots: 30,
    vmax: 3.0, acc: 1.5,       // 로봇 최고속도 m/s, 가감속 m/s² (근사)
    rotSpeed: Math.PI,         // 제자리 회전 rad/s (90° = 0.5 s)
    loadTime: 2.0,             // 작업자 적재 + 스캔 s
    feedTime: 1.2,             // 다음 소포가 컨베이어에서 적재대로 오는 시간 s
    dropTime: 0.9,             // 트레이 기울임-복귀 s
    tiltMax: 0.62,             // 트레이 최대 기울기 rad (~35°)
    bagCap: 28,                // 자루 용량(개)
    bagBottom: 0.55,           // 자루 바닥 높이 m
    deadlock: 5.0,             // 이 시간 이상 막히면 우회 경로 탐색 s
    seed: 3,
  };

  // 소포 종류: [가로, 세로, 높이, 이름]
  const PARCEL_TYPES = [
    [0.30, 0.22, 0.14, "box_s"],
    [0.38, 0.28, 0.20, "box_m"],
    [0.24, 0.18, 0.10, "box_xs"],
    [0.34, 0.26, 0.06, "bag_grey"],
    [0.30, 0.24, 0.05, "bag_white"],
  ];
  const PARCEL_WEIGHTS = [0.28, 0.18, 0.16, 0.22, 0.16];

  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  // 사다리꼴 속도 프로파일 (정지→정지)
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
    const dA = 0.5 * a * ta * ta;
    if (t < T - ta) return dA + vp * (t - ta);
    const td = T - t;
    return d - 0.5 * a * td * td;
  }
  function wrapPi(a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }
  function smooth(u) { u = Math.min(1, Math.max(0, u)); return u * u * (3 - 2 * u); }

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      this.j0 = -2;                                  // 가장 앞 행 (스테이션)
      this.W = c.nx; this.H = c.ny + 2 - this.j0;    // 셀 배열 크기 (앞뒤 스테이션 2행씩)
      this.occ = new Int32Array(this.W * this.H).fill(-1);
      // --- 통로 방향
      const nAx = Math.floor((c.nx - 1) / c.aisleStep), nAy = Math.floor((c.ny - 1) / c.aisleStep);
      this.rowDir = {}; this.colDir = {};
      for (let k = 0; k <= nAy; k++) {
        const j = k * c.aisleStep;
        this.rowDir[j] = k === 0 ? 1 : k === nAy ? -1 : (k % 2 === 1 ? -1 : 1);
      }
      for (let k = 0; k <= nAx; k++) {
        const i = k * c.aisleStep;
        this.colDir[i] = k === 0 ? -1 : k === nAx ? 1 : (k % 2 === 1 ? 1 : -1);
      }
      // --- 스테이션 분기 간선
      this.extra = new Map();                        // key -> [keys]
      this.stations = [];
      const add = (a, b) => { const k = this.key(a[0], a[1]); if (!this.extra.has(k)) this.extra.set(k, []); this.extra.get(k).push(this.key(b[0], b[1])); };
      for (const s of c.stations) {
        add([s, 0], [s, -1]); add([s, -1], [s, -2]); add([s, -2], [s + 1, -2]); add([s + 1, -2], [s + 1, -1]); add([s + 1, -1], [s + 1, 0]);
        this.stations.push({ id: this.stations.length, x: s, side: -1, j: -2, load: this.key(s, -2), parcel: -1, readyAt: 0, served: 0 });
      }
      const e = c.ny - 1;
      for (const s of c.stationsBack) {
        add([s, e], [s, e + 1]); add([s, e + 1], [s, e + 2]); add([s, e + 2], [s - 1, e + 2]); add([s - 1, e + 2], [s - 1, e + 1]); add([s - 1, e + 1], [s - 1, e]);
        this.stations.push({ id: this.stations.length, x: s, side: 1, j: e + 2, load: this.key(s, e + 2), parcel: -1, readyAt: 0, served: 0 });
      }
      // --- 슈트 / 자루
      this.chutes = [];
      this.chuteAt = new Int32Array(this.W * this.H).fill(-1);
      for (let j = 0; j < c.ny; j++) for (let i = 0; i < c.nx; i++) {
        if (this.isAisle(i, j)) continue;
        const drops = [];
        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
          const a = i + dx, b = j + dy;
          if (this.inDeck(a, b) && this.isAisle(a, b) && !(this.rowDir[b] !== undefined && this.colDir[a] !== undefined)) drops.push(this.key(a, b));
        }
        const id = this.chutes.length;
        this.chuteAt[this.idx(i, j)] = id;
        this.chutes.push({ id, i, j, drops, count: 0, parcels: [], swaps: 0, received: 0 });
      }
      // --- 소포
      this.parcels = [];                             // {id,type,state,...}
      this.alive = [];                               // 살아 있는 소포 id
      this.version = 0;
      // 초기 자루 채움
      for (const ch of this.chutes) {
        const n = Math.floor(this.rand() * c.bagCap * 0.7);
        for (let k = 0; k < n; k++) this.toBag(this.newParcel(), ch, true);
      }
      for (const st of this.stations) this.feedStation(st, true);
      // --- 로봇: 통로 셀에 배치
      this.robots = [];
      const free = [];
      for (let j = 0; j < c.ny; j++) for (let i = 0; i < c.nx; i++) if (this.isAisle(i, j)) free.push([i, j]);
      for (let r = 0; r < c.robots; r++) {
        const p = free.splice(Math.floor(this.rand() * free.length), 1)[0];
        const k = this.key(p[0], p[1]);
        const nb = this.next(k);
        const n0 = this.unkey(nb[0]);
        const rb = {
          id: r, cell: k, x: this.cx(p[0]), y: this.cy(p[1]), h: Math.atan2(n0[1] - p[1], n0[0] - p[0]),
          mode: "stop", path: [], task: "toStation", target: -1, parcel: -1, tiltSign: 0, tilt: 0,
          wait: 0, held: [k], busy: 0, travel: 0, delivered: 0,
          m: null,
        };
        this.occ[this.idx(p[0], p[1])] = r;
        this.robots.push(rb);
      }
      for (const rb of this.robots) this.planToStation(rb);
      this.delivered = 0; this.deliveredT = []; this.inducted = 0;
      this.blockedT = 0;
    }

    // ------------------------------------------------ 격자
    key(i, j) { return (j - this.j0) * this.W + i; }
    unkey(k) { return [k % this.W, Math.floor(k / this.W) + this.j0]; }
    idx(i, j) { return (j - this.j0) * this.W + i; }
    cx(i) { return (i + 0.5) * this.cfg.cell; }
    cy(j) { return (j + 0.5) * this.cfg.cell; }
    inDeck(i, j) { return i >= 0 && j >= 0 && i < this.cfg.nx && j < this.cfg.ny; }
    isAisle(i, j) { return this.inDeck(i, j) && (this.rowDir[j] !== undefined || this.colDir[i] !== undefined); }
    isStationCell(i, j) { return (j < 0 || j >= this.cfg.ny) && j >= this.j0 && j < this.j0 + this.H && this.extra.has(this.key(i, j)); }
    next(k) {
      const [i, j] = this.unkey(k);
      const out = [];
      if (this.inDeck(i, j)) {
        if (this.rowDir[j] !== undefined) { const a = i + this.rowDir[j]; if (this.inDeck(a, j)) out.push(this.key(a, j)); }
        if (this.colDir[i] !== undefined) { const b = j + this.colDir[i]; if (this.inDeck(i, b)) out.push(this.key(i, b)); }
      }
      const e = this.extra.get(k);
      if (e) for (const n of e) out.push(n);
      return out;
    }
    // BFS 최단 경로 (start 제외, goal 포함). blocked: 지나가지 않을 셀 key
    route(start, goals, blocked) {
      const N = this.W * this.H;
      const prev = new Int32Array(N).fill(-2);
      prev[start] = -1;
      const q = [start]; let head = 0, hit = -1;
      const goalSet = new Set(goals);
      while (head < q.length) {
        const u = q[head++];
        if (u !== start && goalSet.has(u)) { hit = u; break; }
        for (const v of this.next(u)) {
          if (prev[v] !== -2 || v === blocked) continue;
          prev[v] = u; q.push(v);
        }
      }
      if (hit < 0) return null;
      const path = [];
      for (let v = hit; v !== start; v = prev[v]) path.push(v);
      return path.reverse();
    }

    // ------------------------------------------------ 소포
    newParcel() {
      let u = this.rand(), type = 0;
      for (let k = 0; k < PARCEL_WEIGHTS.length; k++) { if (u < PARCEL_WEIGHTS[k]) { type = k; break; } u -= PARCEL_WEIGHTS[k]; type = k; }
      const p = {
        id: this.parcels.length, type, state: "new", yaw: (this.rand() - 0.5) * 0.5, tint: this.rand(),
        x: 0, y: 0, z: 0, rx: 0, ry: 0, rz: 0, t0: 0, from: null, to: null, born: this.t, chute: -1, alive: true,
      };
      this.parcels.push(p); this.alive.push(p.id);
      return p;
    }
    size(p) { return PARCEL_TYPES[p.type]; }
    bagSlotPose(ch, n, p) {
      // 자루 안 쌓기: 2x2 칸, 층마다 높이 누적 (근사)
      const c = this.cfg, sz = this.size(p);
      const k = n % 4, layer = Math.floor(n / 4);
      const ox = (k % 2 ? 1 : -1) * 0.07 + Math.sin(n * 7.3) * 0.02;
      const oy = (k < 2 ? 1 : -1) * 0.06 + Math.cos(n * 5.1) * 0.02;
      return {
        x: this.cx(ch.i) + ox, y: this.cy(ch.j) + oy, z: c.bagBottom + layer * 0.11 + sz[2] / 2,
        rx: Math.sin(n * 3.7) * 0.12, ry: Math.cos(n * 2.9) * 0.12, rz: (n % 2) * Math.PI / 2 + Math.sin(n * 1.31) * 0.15,
      };
    }
    toBag(p, ch, instant) {
      const pose = this.bagSlotPose(ch, ch.count, p);
      ch.count++; ch.parcels.push(p.id); p.chute = ch.id;
      if (instant) { p.state = "bag"; Object.assign(p, pose); }
      return pose;
    }
    swapBag(ch) {
      for (const id of ch.parcels) { const p = this.parcels[id]; p.state = "gone"; p.alive = false; }
      this.alive = this.alive.filter(id => this.parcels[id].alive);
      ch.parcels = []; ch.count = 0; ch.swaps++; this.version++;
    }
    feedStation(st, instant) {
      const p = this.newParcel();
      const c = this.cfg;
      p.state = "feed"; p.t0 = this.t; p.station = st.id;
      st.parcel = p.id; st.readyAt = instant ? this.t : this.t + c.feedTime;
      this.version++;
    }
    // 작업자 적재대 위 소포 위치 (적재 셀 바깥 한 칸)
    tablePose(st) { return { x: this.cx(st.x), y: this.cy(st.j + st.side) - st.side * 0.05, z: this.cfg.deckZ + 0.20 }; }

    // ------------------------------------------------ 계획
    planToStation(rb) {
      // 각 스테이션까지 경로 길이 + 대기 로봇 수로 선택
      let best = null, bestCost = 1e9;
      for (const st of this.stations) {
        const path = st.load === rb.cell ? [] : this.route(rb.cell, [st.load], -1);
        if (!path) continue;
        let queued = 0;
        for (const o of this.robots) if (o !== rb && o.task === "toStation" && o.target === st.id) queued++;
        const cost = path.length + queued * 4;
        if (cost < bestCost) { bestCost = cost; best = { st, path }; }
      }
      rb.task = "toStation"; rb.target = best.st.id; rb.path = best.path; rb.wait = 0;
    }
    planToChute(rb) {
      const c = this.cfg;
      let ch;
      for (let tries = 0; tries < 20; tries++) {
        ch = this.chutes[Math.floor(this.rand() * this.chutes.length)];
        if (ch.count < c.bagCap) break;
      }
      let best = null;
      for (const d of ch.drops) {
        const path = this.route(rb.cell, [d], -1);
        if (path && (!best || path.length < best.length)) best = path;
      }
      rb.task = "toChute"; rb.target = ch.id; rb.path = best; rb.wait = 0;
    }

    // ------------------------------------------------ 진행
    step(dt) {
      this.t += dt;
      for (const rb of this.robots) this.updateRobot(rb, dt);
      // 소포 애니메이션 상태 갱신 (떨어짐 → 자루 도착)
      for (const id of this.alive) {
        const p = this.parcels[id];
        if (p.state === "fall" && this.t >= p.t1) {
          p.state = "bag"; Object.assign(p, p.to);
          this.delivered++; this.deliveredT.push(this.t);
          const ch = this.chutes[p.chute]; ch.received++;
          if (ch.count >= this.cfg.bagCap && ch.parcels.every(q => this.parcels[q].state === "bag")) this.swapBag(ch);
        }
      }
      while (this.deliveredT.length && this.deliveredT[0] < this.t - 600) this.deliveredT.shift();
    }

    release(rb, keepLast) {
      const keep = keepLast ? rb.held[rb.held.length - 1] : -1;
      for (const k of rb.held) if (k !== keep && this.occ[k] === rb.id) this.occ[k] = -1;
      rb.held = keepLast ? [keep] : [];
    }

    updateRobot(rb, dt) {
      const c = this.cfg;
      if (rb.mode === "act") {
        const m = rb.m;
        if (m.kind === "drop") {
          const u = (this.t - m.t0) / c.dropTime;
          rb.tilt = rb.tiltSign * c.tiltMax * (u < 0.35 ? smooth(u / 0.35) : u < 0.6 ? 1 : 1 - smooth((u - 0.6) / 0.4));
          if (rb.parcel >= 0 && u >= 0.3) this.releaseParcel(rb);
        }
        if (this.t >= m.t1) {
          rb.mode = "stop"; rb.tilt = 0;
          if (m.kind === "load") {
            const st = this.stations[m.st];
            const p = this.parcels[st.parcel];
            p.state = "robot"; p.robot = rb.id; rb.parcel = p.id; st.served++; this.inducted++;
            this.feedStation(st, false);
            this.planToChute(rb);
          } else {
            if (rb.parcel >= 0) this.releaseParcel(rb);
            rb.delivered++;
            this.planToStation(rb);
          }
        }
        return;
      }
      if (rb.mode === "rot") {
        const m = rb.m, u = Math.min(1, (this.t - m.t0) / (m.t1 - m.t0));
        rb.h = m.h0 + (m.h1 - m.h0) * smooth(u);
        if (this.t >= m.t1) { rb.h = wrapPi(m.h1); rb.mode = "stop"; }
        return;
      }
      if (rb.mode === "move") {
        const m = rb.m, s = profilePos(this.t - m.t0, m.d, c.vmax, c.acc);
        rb.x = m.x0 + m.dx * s; rb.y = m.y0 + m.dy * s;
        // 지나간 셀 해제 (로봇 중심이 다음 셀 중심에 도달하면 이전 셀은 비어 있음)
        const passed = Math.floor(s / c.cell + 1e-6);
        while (m.released < passed) {
          const k = rb.held[0];
          if (this.occ[k] === rb.id) this.occ[k] = -1;
          rb.held.shift(); m.released++;
          rb.cell = rb.held[0]; rb.path.shift(); rb.travel += c.cell;
        }
        if (this.t >= m.t0 + m.T) {
          const [i, j] = this.unkey(rb.cell);
          rb.x = this.cx(i); rb.y = this.cy(j); rb.mode = "stop";
        }
        return;
      }
      // ---- 정지 상태
      if (!rb.path || rb.path.length === 0) {
        if (rb.task === "toStation") {
          const st = this.stations[rb.target];
          if (rb.cell === st.load && this.t >= st.readyAt) {
            rb.mode = "act"; rb.m = { kind: "load", t0: this.t, t1: this.t + c.loadTime, st: st.id };
            const p = this.parcels[st.parcel]; p.state = "pick"; p.t0 = this.t; p.robot = rb.id;
          } else if (rb.cell !== st.load) this.planToStation(rb);
        } else if (rb.task === "toChute") {
          const ch = this.chutes[rb.target];
          const [i, j] = this.unkey(rb.cell);
          // 진행 방향 기준 왼쪽(+)/오른쪽(-) 판정
          const vx = this.cx(ch.i) - this.cx(i), vy = this.cy(ch.j) - this.cy(j);
          const cross = Math.cos(rb.h) * vy - Math.sin(rb.h) * vx;
          rb.tiltSign = cross > 0 ? 1 : -1;
          rb.mode = "act"; rb.m = { kind: "drop", t0: this.t, t1: this.t + c.dropTime, ch: ch.id };
        }
        return;
      }
      const [i, j] = this.unkey(rb.cell);
      const [ni, nj] = this.unkey(rb.path[0]);
      const dx = Math.sign(ni - i), dy = Math.sign(nj - j);
      const want = Math.atan2(dy, dx);
      const dh = wrapPi(want - rb.h);
      if (Math.abs(dh) > 0.01) {
        rb.mode = "rot"; rb.m = { t0: this.t, t1: this.t + Math.abs(dh) / c.rotSpeed, h0: rb.h, h1: rb.h + dh };
        return;
      }
      // 같은 방향 직선 구간 예약
      let n = 0, pi = i, pj = j;
      for (const k of rb.path) {
        const [a, b] = this.unkey(k);
        if (a - pi !== dx || b - pj !== dy) break;
        if (this.occ[k] !== -1 && this.occ[k] !== rb.id) break;
        this.occ[k] = rb.id; rb.held.push(k); n++; pi = a; pj = b;
      }
      if (n === 0) {
        rb.wait += dt; this.blockedT += dt;
        if (rb.wait > c.deadlock) {
          // 막힌 셀을 피하는 우회 경로
          const goal = rb.path[rb.path.length - 1];
          const alt = this.route(rb.cell, [goal], rb.path[0]);
          if (alt) rb.path = alt;
          rb.wait = 0;
        }
        return;
      }
      rb.wait = 0;
      const d = n * c.cell;
      rb.mode = "move";
      rb.m = { t0: this.t, d, T: profileTime(d, c.vmax, c.acc), x0: rb.x, y0: rb.y, dx, dy, released: 0 };
    }

    releaseParcel(rb) {
      const p = this.parcels[rb.parcel];
      const ch = this.chutes[rb.target];
      const start = this.parcelPose(p.id);
      p.from = start;
      p.to = this.toBag(p, ch, false);
      p.state = "fall"; p.t0 = this.t;
      const h = Math.max(0.2, this.cfg.deckZ - p.to.z);
      p.slide = 0.22; p.t1 = this.t + p.slide + Math.sqrt(2 * h / 9.81);
      rb.parcel = -1; this.version++;
    }

    // ------------------------------------------------ 자세 (렌더링용)
    trayZ() { return this.cfg.deckZ + 0.30; }
    parcelPose(id) {
      const p = this.parcels[id], c = this.cfg, sz = this.size(p);
      if (p.state === "bag") return { x: p.x, y: p.y, z: p.z, rx: p.rx, ry: p.ry, rz: p.rz };
      if (p.state === "feed" || p.state === "pick") {
        const st = this.stations[p.station], tp = this.tablePose(st);
        if (p.state === "feed") {
          const u = smooth((this.t - p.t0) / c.feedTime);
          return { x: tp.x, y: tp.y + st.side * 1.6 * (1 - u), z: tp.z + sz[2] / 2, rx: 0, ry: 0, rz: Math.PI / 2 + p.yaw };
        }
        // 작업자가 들어 로봇 트레이로 (적재 시간 후반 45%)
        const rb = this.robots[p.robot];
        const u = smooth(((this.t - p.t0) / c.loadTime - 0.4) / 0.45);
        const tz = this.trayZ() + sz[2] / 2;
        return {
          x: tp.x + (rb.x - tp.x) * u, y: tp.y + (rb.y - tp.y) * u,
          z: tp.z + sz[2] / 2 + (tz - tp.z - sz[2] / 2) * u + Math.sin(u * Math.PI) * 0.25,
          rx: 0, ry: 0, rz: Math.PI / 2 + p.yaw + (rb.h - Math.PI / 2) * u,
        };
      }
      if (p.state === "robot") {
        const rb = this.robots[p.robot];
        // 트레이 기울기: 진행방향 축 회전. 옆으로 미끄러짐
        const lat = rb.tilt * 0.12;
        const nx = -Math.sin(rb.h), ny = Math.cos(rb.h);
        return {
          x: rb.x + nx * lat, y: rb.y + ny * lat, z: this.trayZ() + sz[2] / 2 - Math.abs(rb.tilt) * 0.05,
          rx: -rb.tilt, ry: 0, rz: rb.h + p.yaw,   // 오일러 XYZ: 로컬 X(=진행방향) 축 기울기
        };
      }
      if (p.state === "fall") {
        const a = p.from, b = p.to, tt = this.t - p.t0;
        const ch = this.chutes[p.chute];
        const mx = this.cx(ch.i), my = this.cy(ch.j);
        if (tt < p.slide) {
          // 트레이에서 슈트 구멍 중심 쪽으로 미끄러지며 떨어지기 시작
          const u = tt / p.slide;
          return { x: a.x + (mx - a.x) * u * u, y: a.y + (my - a.y) * u * u, z: a.z - 0.25 * u * u,
                   rx: a.rx * (1 + u), ry: a.ry * (1 + u), rz: a.rz };
        }
        const tf = tt - p.slide, T = p.t1 - p.t0 - p.slide;
        const u = Math.min(1, tf / T);
        const z0 = a.z - 0.25;
        const z = Math.max(b.z, z0 - 0.5 * 9.81 * tf * tf - 0.8 * tf);
        return {
          x: mx + (b.x - mx) * u, y: my + (b.y - my) * u, z,
          rx: a.rx * 2 + (b.rx - a.rx * 2) * u, ry: a.ry * 2 + (b.ry - a.ry * 2) * u, rz: a.rz + (b.rz - a.rz) * u,
        };
      }
      return { x: 0, y: 0, z: -10, rx: 0, ry: 0, rz: 0 };
    }

    // ------------------------------------------------ 통계
    get throughputPerHour() {
      const win = Math.min(600, this.t);
      return win > 0 ? this.deliveredT.length * 3600 / win : 0;
    }
    // 로봇이 앞 셀이 비기를 기다린 시간 비율
    get blockedShare() { return this.t > 0 ? this.blockedT / (this.t * this.robots.length) : 0; }
  }

  Sim.DEFAULTS = DEFAULTS;
  Sim.PARCEL_TYPES = PARCEL_TYPES;
  const api = { Sim, DEFAULTS, PARCEL_TYPES };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.LibiaoSim = api;
})(typeof window !== "undefined" ? window : globalThis);
