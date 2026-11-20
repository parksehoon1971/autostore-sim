/*
 * Libiao(立镖) T-Sort 핑거형 플랫폼 소터 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/TSorter/Scripts/TSorterSim.cs)과 같은 로직입니다. 한쪽을 고치면 다른 쪽도 고치세요.
 *
 * 구조 (공식 사이트 T-Sort 솔루션 그림: 투입 스테이션 / 메인 데크 / 핑거형 플랫폼 / 슈트)
 *  - 셀 피치 cell. x 는 핑거 방향, y 는 핑거가 늘어선 방향. 데크 높이 deckZ 의 강철 플랫폼.
 *  - 메인 데크: x 0..mainX-1, y 0..ny-1 (ny = 4 * fingers). 모든 셀이 통로.
 *      행 방향: 홀수 행 +x, 짝수 행 -x.  열 방향: 0, 2 열 +y, 마지막 열(mainX-1) -y.
 *  - 핑거 k (빗살): 메인 데크 오른쪽으로 fingerLen 셀 뻗은 2차선 데크.
 *      나가는 차선 y = 4k+1 (+x), 돌아오는 차선 y = 4k+2 (-x), 끝에서 U턴.
 *      핑거 사이 두 행(4k+3, 4k+4)은 데크가 없는 틈이고, 그 아래 바닥에 슈트 통이 두 줄 놓임.
 *      나가는 차선 아래쪽(-y) 틈 = 행 4k, 돌아오는 차선 위쪽(+y) 틈 = 행 4k+3.
 *  - 슈트: 핑거 차선 셀마다 그 옆 틈에 하나. 로봇이 차선 셀에서 트레이를 틈 쪽으로 기울여 소포를 떨어뜨림.
 *  - 투입 스테이션: 메인 데크 왼쪽(x=-1) U자 분기. (0,s)->(-1,s)[대기]->(-1,s+1)[적재]->(0,s+1).
 *    작업자는 그 바깥(x=-2) 투입대 앞에 서서 스캔한 소포를 로봇에 올림.
 *  - 충돌 회피: 셀 예약. 5초 이상 막히면 우회 경로.
 * 좌표: 셀 (i, j) 중심 = ((i+0.5)*cell, (j+0.5)*cell). z 위. 시간 초, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    fingers: 6,                // 핑거 수
    fingerLen: 14,             // 핑거 길이 (셀)
    mainX: 5,                  // 메인 데크 폭 (셀)
    cell: 0.60,                // 셀 피치 m (로봇 480 mm + 여유, 가정)
    deckZ: 1.10,               // 데크 상면 높이 m (가정)
    stations: [2, 8, 14, 20],  // 투입 스테이션 시작 행 (s, s+1 사용)
    robots: 30,
    vmax: 3.0, acc: 1.5,       // 로봇 최고속도 m/s, 가감속 m/s² (가정)
    rotSpeed: Math.PI,         // 제자리 회전 rad/s (90° = 0.5 s)
    loadTime: 2.0,             // 작업자 적재 + 스캔 s
    feedTime: 1.2,             // 다음 소포가 투입대로 오는 시간 s
    dropTime: 0.9,             // 트레이 기울임-복귀 s
    tiltMax: 0.62,             // 트레이 최대 기울기 rad (~35°)
    binCap: 20,                // 슈트 통 용량(개)
    binBottom: 0.16,           // 통 바닥 높이 m
    deadlock: 5.0,             // 이 시간 이상 막히면 우회 경로 탐색 s
    seed: 3,
  };

  // 소포 종류: [가로, 세로, 높이, 이름]
  const PARCEL_TYPES = [
    [0.30, 0.22, 0.14, "box_s"],
    [0.36, 0.26, 0.18, "box_m"],
    [0.24, 0.18, 0.10, "box_xs"],
    [0.34, 0.26, 0.06, "bag_grey"],
    [0.30, 0.24, 0.05, "bag_white"],
  ];
  const PARCEL_WEIGHTS = [0.28, 0.16, 0.18, 0.22, 0.16];

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
      this.ny = 4 * c.fingers;
      this.tipX = c.mainX + c.fingerLen - 1;
      this.i0 = -1;                                  // 가장 왼쪽 열 (스테이션)
      this.W = c.mainX + c.fingerLen + 1; this.H = this.ny;
      this.occ = new Int32Array(this.W * this.H).fill(-1);
      // --- 스테이션 분기 간선
      this.extra = new Map();                        // key -> [keys]
      const add = (a, b) => { const k = this.key(a[0], a[1]); if (!this.extra.has(k)) this.extra.set(k, []); this.extra.get(k).push(this.key(b[0], b[1])); };
      this.stations = [];
      for (const s of c.stations) {
        add([0, s], [-1, s]); add([-1, s], [-1, s + 1]); add([-1, s + 1], [0, s + 1]);
        this.stations.push({ id: this.stations.length, j: s + 1, load: this.key(-1, s + 1), parcel: -1, readyAt: 0, served: 0 });
      }
      // 핑거 끝 U턴
      for (let k = 0; k < c.fingers; k++) add([this.tipX, 4 * k + 1], [this.tipX, 4 * k + 2]);
      // --- 슈트 / 통
      this.chutes = [];
      for (let k = 0; k < c.fingers; k++) for (const side of [0, 1]) for (let i = c.mainX; i <= this.tipX; i++) {
        const lane = 4 * k + 1 + side, j = side === 0 ? 4 * k : 4 * k + 3;
        this.chutes.push({ id: this.chutes.length, i, j, finger: k, side, drop: this.key(i, lane), count: 0, parcels: [], swaps: 0, received: 0 });
      }
      // --- 소포
      this.parcels = [];
      this.alive = [];
      this.version = 0;
      for (const ch of this.chutes) {
        const n = Math.floor(this.rand() * c.binCap * 0.7);
        for (let k = 0; k < n; k++) this.toBin(this.newParcel(), ch, true);
      }
      for (const st of this.stations) this.feedStation(st, true);
      // --- 로봇: 메인 데크·핑거 통로 셀에 배치
      this.robots = [];
      const free = [];
      for (let j = 0; j < this.ny; j++) for (let i = 0; i <= this.tipX; i++) if (this.isLane(i, j)) free.push([i, j]);
      for (let r = 0; r < c.robots; r++) {
        const p = free.splice(Math.floor(this.rand() * free.length), 1)[0];
        const k = this.key(p[0], p[1]);
        const n0 = this.unkey(this.next(k)[0]);
        const rb = {
          id: r, cell: k, x: this.cx(p[0]), y: this.cy(p[1]), h: Math.atan2(n0[1] - p[1], n0[0] - p[0]),
          mode: "stop", path: [], task: "toStation", target: -1, parcel: -1, tiltSign: 0, tilt: 0,
          wait: 0, held: [k], travel: 0, delivered: 0, m: null,
        };
        this.occ[k] = r;
        this.robots.push(rb);
      }
      for (const rb of this.robots) this.planToStation(rb);
      this.delivered = 0; this.deliveredT = []; this.inducted = 0;
      this.blockedT = 0;
    }

    // ------------------------------------------------ 격자
    key(i, j) { return j * this.W + (i - this.i0); }
    unkey(k) { return [(k % this.W) + this.i0, Math.floor(k / this.W)]; }
    cx(i) { return (i + 0.5) * this.cfg.cell; }
    cy(j) { return (j + 0.5) * this.cfg.cell; }
    inMain(i, j) { return i >= 0 && i < this.cfg.mainX && j >= 0 && j < this.ny; }
    inFinger(i, j) { const m = j & 3; return i >= this.cfg.mainX && i <= this.tipX && j >= 0 && j < this.ny && (m === 1 || m === 2); }
    isLane(i, j) { return this.inMain(i, j) || this.inFinger(i, j); }
    rowDir(j) { return (j & 1) ? 1 : -1; }
    colDir(i) { const c = this.cfg; return i === 0 || i === 2 ? 1 : i === c.mainX - 1 ? -1 : 0; }
    next(k) {
      const [i, j] = this.unkey(k);
      const out = [];
      if (this.isLane(i, j)) {
        const a = i + this.rowDir(j);
        if (this.isLane(a, j)) out.push(this.key(a, j));
        if (this.inMain(i, j)) { const d = this.colDir(i); if (d !== 0 && this.inMain(i, j + d)) out.push(this.key(i, j + d)); }
      }
      const e = this.extra.get(k);
      if (e) for (const n of e) out.push(n);
      return out;
    }
    // BFS 최단 경로 (start 제외, goal 포함). blocked: 지나가지 않을 셀 key
    route(start, goal, blocked) {
      const N = this.W * this.H;
      const prev = new Int32Array(N).fill(-2);
      prev[start] = -1;
      const q = [start]; let head = 0, hit = -1;
      while (head < q.length) {
        const u = q[head++];
        if (u !== start && u === goal) { hit = u; break; }
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
        x: 0, y: 0, z: 0, rx: 0, ry: 0, rz: 0, t0: 0, t1: 0, slide: 0, from: null, to: null, chute: -1, alive: true, robot: -1, station: -1,
      };
      this.parcels.push(p); this.alive.push(p.id);
      return p;
    }
    size(p) { return PARCEL_TYPES[p.type]; }
    binSlotPose(ch, n, p) {
      // 통 안 쌓기: 2x2 칸, 층마다 높이 누적 (근사)
      const c = this.cfg, sz = this.size(p);
      const k = n % 4, layer = Math.floor(n / 4);
      const ox = (k % 2 ? 1 : -1) * 0.07 + Math.sin(n * 7.3) * 0.02;
      const oy = (k < 2 ? 1 : -1) * 0.06 + Math.cos(n * 5.1) * 0.02;
      return {
        x: this.cx(ch.i) + ox, y: this.cy(ch.j) + oy, z: c.binBottom + layer * 0.10 + sz[2] / 2,
        rx: Math.sin(n * 3.7) * 0.12, ry: Math.cos(n * 2.9) * 0.12, rz: (n % 2) * Math.PI / 2 + Math.sin(n * 1.31) * 0.15,
      };
    }
    toBin(p, ch, instant) {
      const pose = this.binSlotPose(ch, ch.count, p);
      ch.count++; ch.parcels.push(p.id); p.chute = ch.id;
      if (instant) { p.state = "bin"; Object.assign(p, pose); }
      return pose;
    }
    swapBin(ch) {
      for (const id of ch.parcels) { const p = this.parcels[id]; p.state = "gone"; p.alive = false; }
      this.alive = this.alive.filter(id => this.parcels[id].alive);
      ch.parcels = []; ch.count = 0; ch.swaps++; this.version++;
    }
    feedStation(st, instant) {
      const p = this.newParcel();
      p.state = "feed"; p.t0 = this.t; p.station = st.id;
      st.parcel = p.id; st.readyAt = instant ? this.t : this.t + this.cfg.feedTime;
      this.version++;
    }
    // 투입대 위 소포 위치 (적재 셀 바깥 한 칸)
    tablePose(st) { return { x: this.cx(-2) + 0.08, y: this.cy(st.j), z: this.cfg.deckZ + 0.12 }; }

    // ------------------------------------------------ 계획
    planToStation(rb) {
      let best = null, bestCost = 1e9;
      for (const st of this.stations) {
        const path = st.load === rb.cell ? [] : this.route(rb.cell, st.load, -1);
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
        if (ch.count < c.binCap) break;
      }
      rb.task = "toChute"; rb.target = ch.id; rb.path = this.route(rb.cell, ch.drop, -1); rb.wait = 0;
    }

    // ------------------------------------------------ 진행
    step(dt) {
      this.t += dt;
      for (const rb of this.robots) this.updateRobot(rb, dt);
      // 소포 낙하 → 통 도착
      for (const id of this.alive) {
        const p = this.parcels[id];
        if (p.state === "fall" && this.t >= p.t1) {
          p.state = "bin"; Object.assign(p, p.to);
          this.delivered++; this.deliveredT.push(this.t);
          const ch = this.chutes[p.chute]; ch.received++;
          if (ch.count >= this.cfg.binCap && ch.parcels.every(q => this.parcels[q].state === "bin")) this.swapBin(ch);
        }
      }
      while (this.deliveredT.length && this.deliveredT[0] < this.t - 600) this.deliveredT.shift();
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
          if (rb.cell !== ch.drop) { rb.path = this.route(rb.cell, ch.drop, -1); return; }
          const [i, j] = this.unkey(rb.cell);
          const vx = this.cx(ch.i) - this.cx(i), vy = this.cy(ch.j) - this.cy(j);
          const cross = Math.cos(rb.h) * vy - Math.sin(rb.h) * vx;
          rb.tiltSign = cross > 0 ? 1 : -1;
          rb.mode = "act"; rb.m = { kind: "drop", t0: this.t, t1: this.t + c.dropTime, st: -1 };
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
          const goal = rb.path[rb.path.length - 1];
          const alt = this.route(rb.cell, goal, rb.path[0]);
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
      p.from = this.parcelPose(p.id);
      p.to = this.toBin(p, ch, false);
      p.state = "fall"; p.t0 = this.t;
      const h = Math.max(0.2, this.cfg.deckZ - p.to.z);
      p.slide = 0.22; p.t1 = this.t + p.slide + Math.sqrt(2 * h / 9.81);
      rb.parcel = -1; this.version++;
    }

    // ------------------------------------------------ 자세 (렌더링용)
    trayZ() { return this.cfg.deckZ + 0.23; }
    parcelPose(id) {
      const p = this.parcels[id], c = this.cfg, sz = this.size(p);
      if (p.state === "bin") return { x: p.x, y: p.y, z: p.z, rx: p.rx, ry: p.ry, rz: p.rz };
      if (p.state === "feed" || p.state === "pick") {
        const st = this.stations[p.station], tp = this.tablePose(st);
        if (p.state === "feed") {
          const u = smooth((this.t - p.t0) / c.feedTime);
          return { x: tp.x - 1.6 * (1 - u), y: tp.y, z: tp.z + sz[2] / 2, rx: 0, ry: 0, rz: p.yaw };
        }
        // 작업자가 들어 로봇 트레이로 (적재 시간 후반 45%)
        const rb = this.robots[p.robot];
        const u = smooth(((this.t - p.t0) / c.loadTime - 0.4) / 0.45);
        const tz = this.trayZ() + sz[2] / 2;
        return {
          x: tp.x + (rb.x - tp.x) * u, y: tp.y + (rb.y - tp.y) * u,
          z: tp.z + sz[2] / 2 + (tz - tp.z - sz[2] / 2) * u + Math.sin(u * Math.PI) * 0.25,
          rx: 0, ry: 0, rz: p.yaw + rb.h * u,
        };
      }
      if (p.state === "robot") {
        const rb = this.robots[p.robot];
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
          // 트레이에서 틈(슈트) 쪽으로 미끄러지며 떨어지기 시작
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
    get blockedShare() { return this.t > 0 ? this.blockedT / (this.t * this.robots.length) : 0; }
    kpi() {
      return { t: +this.t.toFixed(1), delivered: this.delivered, inducted: this.inducted,
               perHour: Math.round(this.throughputPerHour), blocked: +this.blockedShare.toFixed(3),
               swaps: this.chutes.reduce((s, ch) => s + ch.swaps, 0) };
    }
  }

  Sim.DEFAULTS = DEFAULTS;
  Sim.PARCEL_TYPES = PARCEL_TYPES;
  const api = { Sim, DEFAULTS, PARCEL_TYPES };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TSorterSim = api;
})(typeof window !== "undefined" ? window : globalThis);
