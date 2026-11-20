/*
 * 다층 셔틀랙 (멀티 레벨 토트 셔틀 AS/RS) 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/ShuttleRack/Scripts/ShuttleSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 * 특정 제조사 제품이 아니라 일반적인 구성입니다 (docs/shuttle-research.md).
 *
 * 구조: 통로마다 양쪽에 단일 깊이 랙. 통로의 각 단에 셔틀 1대가 레일을 따라 x 방향으로 달리며
 * 텔레스코픽 암으로 양옆 랙의 토트를 꺼내거나 넣습니다. 랙 앞쪽 두 칸(bay 0~1)은 각 단의 버퍼 컨베이어:
 *   왼쪽(-y) = 출고 버퍼 (셔틀 → 출고 리프트), 오른쪽(+y) = 입고 버퍼 (입고 리프트 → 셔틀).
 * 랙 앞에 통로마다 수직 리프트 2대 (출고/입고). 리프트는 1층 컨베이어 높이(convZ)와 각 단 사이를 오갑니다.
 * 1층에는 통로마다 U자 컨베이어: 출고 리프트 → 피킹 스테이션 → 입고 리프트.
 * 좌표: x = 통로 방향 (랙 앞면 = 0, 앞쪽이 -x), y = 깊이 (맨 앞 랙 앞면 = 0), z 위. 시간 s, 거리 m.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    aisles: 3, nx: 26, levels: 14,          // 통로 수, 통로 길이(칸), 단 수
    bufBays: 2,                             // 앞쪽 버퍼 칸 수 (보관 안 함)
    bayW: 0.70, aisleW: 0.72, rackD: 0.46, flue: 0.06, // 칸 폭, 통로 폭, 한쪽 랙 깊이, 등지는 랙 사이 틈
    level0Z: 0.55, levelH: 0.42,            // 맨 아래 단 토트 바닥 높이, 단 간격
    toteW: 0.60, toteD: 0.40, toteH: 0.28,  // 토트 600 x 400 x 280 mm (유로 규격)
    liftX: -0.75, xferOff: 0.62,            // 리프트 x, 리프트 ↔ 컨베이어 끝 거리
    convZ: 0.75, stationX: -4.6,            // 1층 컨베이어 토트 바닥 높이, 스테이션 피킹 위치 x
    fill: 0.85,
    orderRate: 1800,                        // 토트 요청 / 시간 (전체, 통로당 600)
    popularShare: 0.2, popularDemand: 0.8,
    maxOut: 10,                             // 통로마다 랙 밖에 나와 있을 수 있는 토트 수
    vmax: 4.0, acc: 2.0,                    // 셔틀 주행 4 m/s, 2 m/s² (공개 자료)
    armT: 0.9,                              // 텔레스코픽 암 한 번 펴기 또는 접기
    liftV: 5.0, liftAcc: 5.0, xferT: 1.0,   // 리프트 수직 속도·가속도, 리프트 ↔ 컨베이어 옮겨 싣기
    convV: 1.0, gap: 0.66,                  // 컨베이어 속도, 토트 앞뒤 간격
    pickMin: 3, pickMax: 5,                 // 작업자 피킹 시간 / 토트 (표시등 피킹, 스테이션당 약 900 토트/h)
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
  function smooth(u) { u = Math.min(1, Math.max(0, u)); return u * u * (3 - 2 * u); }

  // 토트 단계 (카메라·통계용)
  const ST_SLOT = 0, ST_SHUTTLE = 1, ST_OUTBUF = 2, ST_OUTLIFT = 3, ST_OUTCONV = 4, ST_PICK = 5,
    ST_RETCONV = 6, ST_INLIFT = 7, ST_INBUF = 8, ST_SHUTTLE_IN = 9;

  // 축적형 컨베이어: 꺾인 선 경로 위의 토트들이 앞 토트와 gap 을 두고 전진
  class Conv {
    constructor(pts, v, gap) {
      this.pts = pts; this.v = v; this.gap = gap;
      this.cum = [0];
      for (let i = 1; i < pts.length; i++) {
        const a = pts[i - 1], b = pts[i];
        this.cum.push(this.cum[i - 1] + Math.abs(b[0] - a[0]) + Math.abs(b[1] - a[1]) + Math.abs(b[2] - a[2]));
      }
      this.L = this.cum[this.cum.length - 1];
      this.items = [];   // 앞(끝에 가까운) 토트부터: { id, s, readyT }
    }
    pos(s) {
      const p = this.pts, c = this.cum;
      let i = 1;
      while (i < p.length - 1 && s > c[i]) i++;
      const seg = c[i] - c[i - 1], u = seg > 0 ? Math.min(1, Math.max(0, (s - c[i - 1]) / seg)) : 1;
      const a = p[i - 1], b = p[i];
      return [a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u];
    }
    canAccept() { return this.items.length === 0 || this.items[this.items.length - 1].s >= this.gap - 1e-9; }
    headReady() { return this.items.length > 0 && this.items[0].s >= this.L - 1e-6; }
    count() { return this.items.length; }
    push(id) { this.items.push({ id, s: 0, readyT: -1 }); }
    pop() { return this.items.shift().id; }
    step(dt, sim) {
      let lim = this.L;
      for (const it of this.items) {
        it.s = Math.max(it.s, Math.min(lim, it.s + this.v * dt));
        if (it.readyT < 0 && it.s >= this.L - 1e-6) it.readyT = sim.t;
        lim = it.s - this.gap;
        sim.totePos[it.id] = this.pos(it.s);
      }
    }
  }

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      const A = c.aisles, L = c.levels;
      this.nb = c.nx - c.bufBays;                       // 보관 칸 수 / 통로·쪽·단
      this.pitch = c.aisleW + 2 * c.rackD + c.flue;
      this.depthY = A * (c.aisleW + 2 * c.rackD) + (A - 1) * c.flue;
      this.R = c.aisleW / 2 + c.rackD / 2;              // 통로 중심 ↔ 토트 중심 (암 최대 길이)
      // 컨베이어
      this.outBuf = []; this.inBuf = []; this.outConv = []; this.retConv = [];
      const xr = this.bufX(), xe = c.liftX + c.xferOff, xg = c.liftX - c.xferOff, xs = c.stationX;
      for (let a = 0; a < A; a++) {
        const ya = this.aisleY(a), yo = ya - this.R, yi = ya + this.R;
        const ob = [], ib = [];
        for (let l = 0; l < L; l++) {
          const z = this.levelZ(l);
          ob.push(new Conv([[xr, yo, z], [xe, yo, z]], c.convV, c.gap));
          ib.push(new Conv([[xe, yi, z], [xr, yi, z]], c.convV, c.gap));
        }
        this.outBuf.push(ob); this.inBuf.push(ib);
        this.outConv.push(new Conv([[xg, yo, c.convZ], [xs, yo, c.convZ], [xs, ya, c.convZ]], c.convV, c.gap));
        this.retConv.push(new Conv([[xs, ya, c.convZ], [xs, yi, c.convZ], [xg, yi, c.convZ]], c.convV, c.gap));
      }
      // 토트 자리 (통로 a, 보관 칸 b, 쪽 sd(0 = -y, 1 = +y), 단 l)
      this.nSlots = A * this.nb * 2 * L;
      this.slotTote = new Int32Array(this.nSlots).fill(-1);
      this.slotRes = new Uint8Array(this.nSlots);
      const cand = [];
      for (let id = 0; id < this.nSlots; id++) {
        if (this.rand() < c.fill) cand.push({ id, key: this.slotCost(id) + this.gauss() * 1.5 });
      }
      cand.sort((p, q) => p.key - q.key || p.id - q.id);
      // 인기 토트를 통로마다 고르게 (정렬 순서대로 통로를 돌아가며 가장 가까운 자리)
      this.totes = cand.length;
      this.totePos = []; this.toteStage = new Uint8Array(this.totes); this.toteSlot = new Int32Array(this.totes).fill(-1);
      this.toteTarget = new Int32Array(this.totes).fill(-1);
      cand.forEach((s, id) => { this.slotTote[s.id] = id; this.toteSlot[id] = s.id; this.totePos[id] = this.slotPos(s.id); });
      this.nPopular = Math.max(1, Math.floor(this.totes * c.popularShare));
      this.busy = new Uint8Array(this.totes);
      this.toteOrder = new Array(this.totes).fill(null);
      // 셔틀 (통로 × 단), 리프트 (통로 × 출고/입고), 스테이션 (통로마다)
      this.shuttles = [];
      for (let a = 0; a < A; a++) for (let l = 0; l < L; l++)
        this.shuttles.push({ id: this.shuttles.length, a, l, x: xr, arm: 0, side: 1, carry: -1, acts: [], queue: [],
          state: "idle", busyTime: 0, waitTime: 0, dist: 0, jobs: 0 });
      this.lifts = [];
      for (let a = 0; a < A; a++) for (const side of [-1, 1])
        this.lifts.push({ id: this.lifts.length, a, side, z: c.convZ, carry: -1, acts: [], state: "idle", busyTime: 0, trips: 0 });
      this.incoming = []; for (let a = 0; a < A; a++) this.incoming.push(new Int32Array(L));
      this.inflight = new Int32Array(A);
      this.stations = [];
      for (let a = 0; a < A; a++) this.stations.push({ id: a, a, cur: -1, t: 0, busyTime: 0, picked: 0 });
      this.orders = []; this.pending = [];
      this.nextOrderT = this.expo(3600 / c.orderRate);
      this.stats = { done: 0, leadSum: 0, leadMax: 0 };
    }

    // ------------------------------------------------------------ 기본
    gauss() { let u = 0; while (!u) u = this.rand(); const v = this.rand(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v); }
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    wx(b) { return (b + 0.5) * this.cfg.bayW; }
    bufX() { return this.wx(this.cfg.bufBays - 1); }
    aisleY(a) { return this.cfg.rackD + this.cfg.aisleW / 2 + a * this.pitch; }
    levelZ(l) { return this.cfg.level0Z + l * this.cfg.levelH; }
    slotAisle(s) { return Math.floor(s / (this.nb * 2 * this.cfg.levels)); }
    slotBay(s) { return this.cfg.bufBays + Math.floor(s / (2 * this.cfg.levels)) % this.nb; }
    slotSide(s) { return (Math.floor(s / this.cfg.levels) % 2) ? 1 : -1; }
    slotLevel(s) { return s % this.cfg.levels; }
    slotPos(s) { return [this.wx(this.slotBay(s)), this.aisleY(this.slotAisle(s)) + this.slotSide(s) * this.R, this.levelZ(this.slotLevel(s))]; }
    slotCost(s) {
      const c = this.cfg;
      return profileTime(Math.abs(this.levelZ(this.slotLevel(s)) - c.convZ), c.liftV, c.liftAcc) +
        profileTime(this.wx(this.slotBay(s)) - this.bufX(), c.vmax, c.acc);
    }
    shuttleOf(a, l) { return this.shuttles[a * this.cfg.levels + l]; }

    // ------------------------------------------------------------ 주문
    newOrder() {
      const c = this.cfg;
      for (let k = 0; k < 10; k++) {
        const pop = this.rand() < c.popularDemand;
        const id = pop ? Math.floor(this.rand() * this.nPopular) : this.nPopular + Math.floor(this.rand() * (this.totes - this.nPopular));
        if (this.busy[id]) continue;
        this.busy[id] = 1;
        const o = { id: this.orders.length, tote: id, tArrive: this.t, tRelease: -1, tPresent: -1, tDone: -1 };
        this.orders.push(o); this.pending.push(o); this.toteOrder[id] = o;
        return;
      }
    }
    release() {
      let w = 0;
      for (let i = 0; i < this.pending.length; i++) {
        const o = this.pending[i], s = this.toteSlot[o.tote], a = this.slotAisle(s);
        if (this.inflight[a] < this.cfg.maxOut) {
          this.inflight[a]++; o.tRelease = this.t;
          this.shuttleOf(a, this.slotLevel(s)).queue.push(o);
        } else this.pending[w++] = o;
      }
      this.pending.length = w;
    }

    // ------------------------------------------------------------ 셔틀
    planShuttle(sh) {
      const ib = this.inBuf[sh.a][sh.l];
      if (ib.headReady()) {
        const s = this.toteTarget[ib.items[0].id];
        sh.acts.push({ k: "go", x: this.bufX() }, { k: "arm", side: 1, op: "takeBuf" },
          { k: "go", x: this.wx(this.slotBay(s)) }, { k: "arm", side: this.slotSide(s), op: "putSlot", slot: s });
        sh.jobs++;
      } else if (sh.queue.length) {
        const o = sh.queue.shift(), s = this.toteSlot[o.tote];
        sh.acts.push({ k: "go", x: this.wx(this.slotBay(s)) }, { k: "arm", side: this.slotSide(s), op: "takeSlot", slot: s },
          { k: "go", x: this.bufX() }, { k: "arm", side: -1, op: "putBuf" });
        sh.task = o.tote; sh.jobs++;
      }
    }
    stepShuttle(sh, dt) {
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!sh.acts.length) this.planShuttle(sh);
        if (!sh.acts.length) { sh.state = "idle"; return; }
        const a = sh.acts[0];
        const used = a.k === "go" ? this.shGo(sh, a, dt) : this.shArm(sh, a, dt);
        sh.busyTime += used;
        if (a.done) sh.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }
    carryPose(sh) {
      if (sh.carry >= 0) this.totePos[sh.carry] = [sh.x, this.aisleY(sh.a) + sh.side * sh.arm * this.R, this.levelZ(sh.l)];
    }
    shGo(sh, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) { a.t = 0; a.x0 = sh.x; a.d = Math.abs(a.x - sh.x); a.T = profileTime(a.d, c.vmax, c.acc); sh.dist += a.d; }
      a.t += dt;
      let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
      sh.x = a.x0 + (a.x >= a.x0 ? 1 : -1) * profilePos(a.t, a.d, c.vmax, c.acc);
      if (a.done) sh.x = a.x;
      sh.state = sh.carry >= 0 ? "carry" : "move";
      this.carryPose(sh);
      return used;
    }
    shArm(sh, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) {
        if (a.op === "putBuf" && !this.outBuf[sh.a][sh.l].canAccept()) { sh.state = "wait"; sh.waitTime += dt; return dt; }
        a.t = 0; a.swapped = false; sh.side = a.side;
      }
      a.t += dt;
      let used = dt;
      const T = 2 * c.armT;
      if (a.t >= T) { used = dt - (a.t - T); a.t = T; a.done = true; }
      sh.arm = a.t < c.armT ? smooth(a.t / c.armT) : 1 - smooth((a.t - c.armT) / c.armT);
      sh.state = a.op === "takeSlot" || a.op === "takeBuf" ? "get" : "put";
      if (!a.swapped && a.t >= c.armT) {
        a.swapped = true; sh.arm = 1;
        if (a.op === "takeSlot") {
          const id = this.slotTote[a.slot]; this.slotTote[a.slot] = -1; this.toteSlot[id] = -1;
          sh.carry = id; this.toteStage[id] = ST_SHUTTLE;
        } else if (a.op === "takeBuf") {
          const id = this.inBuf[sh.a][sh.l].pop(); sh.carry = id; this.toteStage[id] = ST_SHUTTLE_IN;
        } else if (a.op === "putSlot") {
          const id = sh.carry; sh.carry = -1;
          this.slotTote[a.slot] = id; this.slotRes[a.slot] = 0; this.toteSlot[id] = a.slot; this.toteTarget[id] = -1;
          this.totePos[id] = this.slotPos(a.slot); this.toteStage[id] = ST_SLOT; this.busy[id] = 0; this.inflight[sh.a]--;
        } else {
          const id = sh.carry; sh.carry = -1;
          this.carryPoseOf(sh, id);
          this.outBuf[sh.a][sh.l].push(id); this.toteStage[id] = ST_OUTBUF; sh.task = -1;
        }
      }
      if (a.done) sh.arm = 0;
      this.carryPose(sh);
      return used;
    }
    carryPoseOf(sh, id) { this.totePos[id] = [sh.x, this.aisleY(sh.a) + sh.side * this.R, this.levelZ(sh.l)]; }

    // ------------------------------------------------------------ 리프트
    liftY(lf) { return this.aisleY(lf.a) + lf.side * this.R; }
    planLift(lf) {
      const c = this.cfg, L = c.levels;
      if (lf.side < 0) {
        let best = -1, bt = Infinity;
        for (let l = 0; l < L; l++) {
          const ob = this.outBuf[lf.a][l];
          if (ob.headReady() && ob.items[0].readyT < bt) { bt = ob.items[0].readyT; best = l; }
        }
        if (best < 0) return;
        lf.acts.push({ k: "lz", z: this.levelZ(best) }, { k: "xfer", op: "takeBuf", l: best },
          { k: "lz", z: c.convZ }, { k: "xfer", op: "putConv" });
        lf.trips++;
      } else {
        const rc = this.retConv[lf.a];
        if (!rc.headReady()) return;
        const id = rc.items[0].id, s = this.chooseSlot(lf.a, id);
        if (s < 0) return;
        const l = this.slotLevel(s);
        this.slotRes[s] = 1; this.toteTarget[id] = s; this.incoming[lf.a][l]++;
        lf.acts.push({ k: "lz", z: c.convZ }, { k: "xfer", op: "takeRet" }, { k: "lz", z: this.levelZ(l) }, { k: "xfer", op: "putBuf", l });
        lf.trips++;
      }
    }
    // 빈 자리 고르기: 리프트·셔틀 시간 + 그 단 셔틀 일감 (인기 토트일수록 많이 비교)
    chooseSlot(a, tote) {
      const c = this.cfg, per = this.nb * 2 * c.levels, n = tote < this.nPopular ? 30 : 8;
      let best = -1, bc = Infinity, got = 0;
      for (let tries = 0; tries < 4000 && got < n; tries++) {
        const s = a * per + Math.floor(this.rand() * per);
        if (this.slotTote[s] !== -1 || this.slotRes[s]) continue;
        const l = this.slotLevel(s);
        if (this.inBuf[a][l].count() + this.incoming[a][l] >= 2) continue;
        got++;
        const sh = this.shuttleOf(a, l);
        const cst = this.slotCost(s) + 4 * (sh.queue.length + this.inBuf[a][l].count() + this.incoming[a][l]) + (sh.acts.length ? 3 : 0);
        if (cst < bc) { bc = cst; best = s; }
      }
      return best;
    }
    stepLift(lf, dt) {
      let guard = 0;
      while (dt > 1e-9 && guard++ < 10) {
        if (!lf.acts.length) this.planLift(lf);
        if (!lf.acts.length) { lf.state = "idle"; return; }
        const a = lf.acts[0];
        const used = a.k === "lz" ? this.liftMove(lf, a, dt) : this.liftXfer(lf, a, dt);
        lf.busyTime += used;
        if (a.done) lf.acts.shift();
        dt -= used;
        if (used === 0 && !a.done) return;
      }
    }
    liftMove(lf, a, dt) {
      const c = this.cfg;
      if (a.t === undefined) { a.t = 0; a.z0 = lf.z; a.d = Math.abs(a.z - lf.z); a.T = profileTime(a.d, c.liftV, c.liftAcc); }
      a.t += dt;
      let used = dt;
      if (a.t >= a.T) { used = dt - (a.t - a.T); a.t = a.T; a.done = true; }
      lf.z = a.done ? a.z : a.z0 + (a.z >= a.z0 ? 1 : -1) * profilePos(a.t, a.d, c.liftV, c.liftAcc);
      lf.state = "move";
      if (lf.carry >= 0) this.totePos[lf.carry] = [c.liftX, this.liftY(lf), lf.z];
      return used;
    }
    liftXfer(lf, a, dt) {
      const c = this.cfg, y = this.liftY(lf), car = [c.liftX, y, lf.z];
      if (a.t === undefined) {
        if (a.op === "putConv" && !this.outConv[lf.a].canAccept()) { lf.state = "wait"; return dt; }
        if (a.op === "putBuf" && !this.inBuf[lf.a][a.l].canAccept()) { lf.state = "wait"; return dt; }
        a.t = 0;
        if (a.op === "takeBuf") {
          const ob = this.outBuf[lf.a][a.l]; a.from = ob.pos(ob.L); a.to = car;
          lf.carry = ob.pop(); this.toteStage[lf.carry] = ST_OUTLIFT;
        } else if (a.op === "takeRet") {
          const rc = this.retConv[lf.a]; a.from = rc.pos(rc.L); a.to = car;
          lf.carry = rc.pop(); this.toteStage[lf.carry] = ST_INLIFT;
        } else if (a.op === "putConv") { a.from = car; a.to = this.outConv[lf.a].pos(0); }
        else { a.from = car; a.to = this.inBuf[lf.a][a.l].pos(0); }
      }
      a.t += dt;
      let used = dt;
      if (a.t >= c.xferT) { used = dt - (a.t - c.xferT); a.t = c.xferT; a.done = true; }
      const u = smooth(a.t / c.xferT), id = lf.carry;
      this.totePos[id] = [a.from[0] + (a.to[0] - a.from[0]) * u, a.from[1] + (a.to[1] - a.from[1]) * u, a.from[2] + (a.to[2] - a.from[2]) * u];
      lf.state = "xfer";
      if (a.done) {
        if (a.op === "putConv") { this.outConv[lf.a].push(id); this.toteStage[id] = ST_OUTCONV; lf.carry = -1; }
        else if (a.op === "putBuf") { this.inBuf[lf.a][a.l].push(id); this.toteStage[id] = ST_INBUF; this.incoming[lf.a][a.l]--; lf.carry = -1; }
      }
      return used;
    }

    // ------------------------------------------------------------ 스테이션
    stepStation(st, dt) {
      const c = this.cfg, oc = this.outConv[st.a], rc = this.retConv[st.a];
      if (st.cur < 0 && oc.headReady()) {
        st.cur = oc.items[0].id; st.t = c.pickMin + this.rand() * (c.pickMax - c.pickMin);
        this.toteStage[st.cur] = ST_PICK;
        const o = this.toteOrder[st.cur]; if (o) o.tPresent = this.t;
      }
      if (st.cur < 0) return;
      if (st.t > 0) { st.busyTime += Math.min(dt, st.t); st.t -= dt; }
      if (st.t <= 0 && rc.canAccept()) {
        const id = oc.pop(); rc.push(id); this.toteStage[id] = ST_RETCONV;
        st.cur = -1; st.picked++;
        const o = this.toteOrder[id];
        if (o) {
          o.tDone = this.t; this.toteOrder[id] = null;
          const lead = o.tDone - o.tArrive;
          this.stats.done++; this.stats.leadSum += lead; this.stats.leadMax = Math.max(this.stats.leadMax, lead);
        }
      }
    }

    step(dt) {
      this.t += dt;
      while (this.t >= this.nextOrderT) { this.newOrder(); this.nextOrderT += this.expo(3600 / this.cfg.orderRate); }
      this.release();
      const A = this.cfg.aisles, L = this.cfg.levels;
      for (let a = 0; a < A; a++) {
        for (let l = 0; l < L; l++) { this.outBuf[a][l].step(dt, this); this.inBuf[a][l].step(dt, this); }
        this.outConv[a].step(dt, this); this.retConv[a].step(dt, this);
      }
      for (const sh of this.shuttles) this.stepShuttle(sh, dt);
      for (const lf of this.lifts) this.stepLift(lf, dt);
      for (const st of this.stations) this.stepStation(st, dt);
    }
    run(seconds, dt) { dt = dt || 1 / 60; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    totePose(id) { return this.totePos[id]; }
    kpi() {
      const T = Math.max(this.t, 1e-9), s = this.stats;
      const sum = (arr, f) => arr.reduce((x, e) => x + f(e), 0);
      return {
        simTime_s: +T.toFixed(1),
        ordersArrived: this.orders.length,
        ordersDone: s.done,
        throughput_totesPerHour: +(s.done / T * 3600).toFixed(1),
        avgLeadTime_s: s.done ? +(s.leadSum / s.done).toFixed(1) : null,
        maxLeadTime_s: +s.leadMax.toFixed(1),
        backlog: this.pending.length,
        shuttleUtil: +(sum(this.shuttles, x => x.busyTime) / (this.shuttles.length * T)).toFixed(3),
        shuttleWaitShare: +(sum(this.shuttles, x => x.waitTime) / (this.shuttles.length * T)).toFixed(3),
        outLiftUtil: +(sum(this.lifts.filter(x => x.side < 0), x => x.busyTime) / (this.cfg.aisles * T)).toFixed(3),
        inLiftUtil: +(sum(this.lifts.filter(x => x.side > 0), x => x.busyTime) / (this.cfg.aisles * T)).toFixed(3),
        stationUtil: +(sum(this.stations, x => x.busyTime) / (this.stations.length * T)).toFixed(3),
      };
    }
  }

  const api = { Sim, Conv, DEFAULTS, profileTime, profilePos,
    ST_SLOT, ST_SHUTTLE, ST_OUTBUF, ST_OUTLIFT, ST_OUTCONV, ST_PICK, ST_RETCONV, ST_INLIFT, ST_INBUF, ST_SHUTTLE_IN };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.ShuttleSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
