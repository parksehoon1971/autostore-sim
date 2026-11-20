/*
 * 팔레트용 스태커크레인 (단위 하중 AS/RS) 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/StackerCrane/Scripts/StackerCraneSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 * 특정 제조사 제품이 아닌 일반형 모델입니다 (docs/stackercrane-research.md).
 *
 * 구조: 통로마다 스태커크레인 1대가 바닥 레일(아래)과 랙 상부 가이드 레일(위) 사이를 주행합니다.
 * 통로 양옆은 1열 깊이 팔레트 랙. 통로 앞 끝(x < 0)에 입고 컨베이어(쪽 0, -y)와 출고 컨베이어(쪽 1, +y)가 있고,
 * 크레인은 그 끝 칸(P&D 위치)에서 텔레스코픽 포크로 팔레트를 받거나 내려놓습니다.
 * 크레인 동작: 주행(x)과 승강(z)을 동시에 → 위치 보정(정착) → 포크 펴기 → 들기/내리기 → 포크 접기.
 * 입고와 출고 요청이 둘 다 있으면 복합 사이클(입고 → 보관 칸 → 출고 칸 → 출고 컨베이어)로 처리합니다.
 *
 * 좌표: x 통로 방향 (랙 앞 끝 = 0), y 통로를 가로지름 (첫 랙 바깥면 = 0), z 위. 시간 s, 거리 m.
 * 크레인 z = 포크 윗면 높이. 팔레트 위치 = 팔레트 바닥 가운데.
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    aisles: 2, nx: 22, levels: 10,          // 통로 수, 통로 한쪽 칸 수, 단 수
    slotW: 1.25, bay: 2,                    // 칸 폭 (팔레트 1000 mm + 여유), 기둥 사이 칸 수
    rackD: 1.10, aisleW: 1.50, flue: 0.30,  // 랙 깊이 (팔레트 1200 mm 가 앞뒤로 50 mm 나옴), 통로 폭, 등지는 랙 사이 틈
    level0Z: 0.90, levelH: 1.75,            // 맨 아래 단 빔 윗면 높이 (= 컨베이어 높이), 단 간격
    palletL: 1.20, palletW: 1.00, palletH: 0.144, // EUR 팔레트 계열 1200 x 1000 x 144 mm (긴 쪽이 포크 방향)
    loadMin: 0.95, loadMax: 1.40,           // 적재물 높이 (팔레트 위)
    fill: 0.70,
    pdX: -1.80,                             // P&D (입출고 컨베이어 끝 칸) x
    convN: 6, convL: 1.25,                  // 컨베이어 칸 수, 칸 길이
    convV: 0.30, convA: 0.30,               // 체인 컨베이어 18 m/min (가정)
    vx: 4.0, ax: 1.0,                       // 주행 240 m/min, 1.0 m/s² (공개 자료 범위 160~300 m/min, 0.5~2.5 m/s²)
    vz: 1.2, az: 1.0,                       // 승강 72 m/min, 1.0 m/s² (공개 자료 54~90 m/min)
    vf: 1.0, af: 1.2,                       // 텔레스코픽 포크 60 m/min (가정)
    lift: 0.12, vl: 0.15, al: 0.5,          // 포크로 팔레트를 들거나 내리는 높이와 속도 (가정)
    settle: 0.6,                            // 도착 후 위치 보정 · 마스트 흔들림 정착 (가정)
    forkGap: 0.03,                          // 들기 전 포크 윗면과 팔레트 윗판 아래 사이 여유 (포크가 빔 위로 들어가게)
    inRate: 45, outRate: 45,                // 통로당 입고 / 출고 요청 (팔레트/시간)
    pool: 24,                               // 창고 밖 대기 팔레트 (입고로 들어오고 출고로 나감)
    seed: 6,
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

  // 팔레트 위치 종류
  const HIDDEN = 0, SLOT = 1, CONV = 2, CRANE = 3;
  const STATES = ["idle", "travel", "settle", "forkOut", "lift", "lower", "forkIn", "wait"];

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      this.pitch = c.aisleW + 2 * c.rackD + c.flue;            // 통로 중심 간격
      this.depthY = c.aisles * (c.aisleW + 2 * c.rackD) + (c.aisles - 1) * c.flue;
      this.reach = c.aisleW / 2 + c.rackD / 2;                // 통로 중심 → 팔레트 중심
      this.nSlots = c.aisles * 2 * c.nx * c.levels;
      this.slotP = new Int32Array(this.nSlots).fill(-1);       // 칸의 팔레트
      this.slotRes = new Uint8Array(this.nSlots);              // 예약 (넣으러 가는 중 / 꺼내러 가는 중)
      // 팔레트: 초기 보관 + 창고 밖 대기
      this.pal = [];
      for (let s = 0; s < this.nSlots; s++) {
        if (this.rand() < c.fill) { const id = this.newPallet(); this.pal[id].kind = SLOT; this.pal[id].ref = s; this.slotP[s] = id; }
      }
      for (let k = 0; k < c.pool; k++) this.newPallet();
      // 컨베이어: 통로마다 입고(lane 2a) / 출고(lane 2a+1). 칸 0 = P&D (크레인 쪽)
      this.lanes = [];
      for (let a = 0; a < c.aisles; a++) for (let d = 0; d < 2; d++) {
        this.lanes.push({ aisle: a, out: d === 1, seg: new Int32Array(c.convN).fill(-1), res: new Int32Array(c.convN).fill(-1), queue: 0 });
      }
      this.moving = [];                                        // 컨베이어 위에서 움직이는 팔레트
      // 크레인
      this.cranes = [];
      for (let a = 0; a < c.aisles; a++) {
        this.cranes.push({ id: a, aisle: a, x: c.pdX, z: this.forkZ(c.level0Z, false), fork: 0, carry: -1,
          acts: [], state: "idle", requests: 0, busyTime: 0, travelDist: 0, hoistDist: 0,
          cycles: 0, dual: 0, stores: 0, retrieves: 0, support: -1, reqTimes: [] });
      }
      this.nextIn = []; this.nextOut = [];
      for (let a = 0; a < c.aisles; a++) { this.nextIn.push(this.expo(3600 / c.inRate)); this.nextOut.push(this.expo(3600 / c.outRate)); }
      this.stats = { stored: 0, retrieved: 0, inArrived: 0, outArrived: 0, outLeadSum: 0, exited: 0 };
      this.events = [];
    }

    newPallet() {
      const c = this.cfg, id = this.pal.length;
      this.pal.push({ id, kind: HIDDEN, ref: -1, x: 0, y: 0, z: -5, load: c.loadMin + this.rand() * (c.loadMax - c.loadMin),
        variant: Math.floor(this.rand() * 4), reqT: -1 });
      return id;
    }

    // ------------------------------------------------------------ 기하
    expo(mean) { return -Math.log(1 - this.rand()) * mean; }
    aisleY(a) { const c = this.cfg; return c.rackD + c.aisleW / 2 + a * this.pitch; }
    slotX(i) { return (i + 0.5) * this.cfg.slotW; }
    levelZ(l) { return this.cfg.level0Z + l * this.cfg.levelH; }
    slotId(a, side, i, l) { return ((a * 2 + side) * this.cfg.nx + i) * this.cfg.levels + l; }
    slotAisle(s) { return Math.floor(s / (2 * this.cfg.nx * this.cfg.levels)); }
    slotSide(s) { return Math.floor(s / (this.cfg.nx * this.cfg.levels)) % 2; }
    slotI(s) { return Math.floor(s / this.cfg.levels) % this.cfg.nx; }
    slotL(s) { return s % this.cfg.levels; }
    slotPos(s) {
      const a = this.slotAisle(s), sd = this.slotSide(s);
      return [this.slotX(this.slotI(s)), this.aisleY(a) + (sd ? 1 : -1) * this.reach, this.levelZ(this.slotL(s))];
    }
    laneY(li) { const L = this.lanes[li]; return this.aisleY(L.aisle) + (L.out ? 1 : -1) * this.reach; }
    segX(k) { return this.cfg.pdX - k * this.cfg.convL; }
    // 받침 높이 z0 에서 포크 윗면 높이: 꺼낼 때는 팔레트 윗판 아래, 넣을 때는 들어 올린 높이
    forkZ(z0, loaded) { const c = this.cfg; return z0 + c.palletH - 0.022 - c.forkGap + (loaded ? c.lift : 0); }

    // 크레인 위치 (x, z) 에서 다른 위치까지 걸리는 시간 (주행·승강 동시)
    travelTime(x0, z0, x1, z1) {
      const c = this.cfg;
      return Math.max(profileTime(Math.abs(x1 - x0), c.vx, c.ax), profileTime(Math.abs(z1 - z0), c.vz, c.az));
    }

    // ------------------------------------------------------------ 컨베이어
    stepConveyors(dt) {
      const c = this.cfg;
      // 진행 중인 이동
      for (let k = this.moving.length - 1; k >= 0; k--) {
        const m = this.moving[k], p = this.pal[m.p];
        m.t += dt;
        const s = profilePos(m.t, c.convL, c.convV, c.convA) / c.convL;
        p.x = m.x0 + (m.x1 - m.x0) * s;
        if (m.t >= m.T) {
          const L = this.lanes[m.lane];
          if (m.from >= 0 && m.from < c.convN) L.seg[m.from] = -1;
          if (m.to >= c.convN) {                     // 출고 컨베이어 끝: 벽 뒤로 나감
            p.kind = HIDDEN; p.ref = -1; p.z = -5;
            if (p.reqT >= 0) { this.stats.outLeadSum += this.t - p.reqT; this.stats.exited++; p.reqT = -1; }
          } else {
            L.seg[m.to] = m.p; L.res[m.to] = -1; p.ref = m.lane * c.convN + m.to; p.x = this.segX(m.to);
          }
          this.moving.splice(k, 1);
        }
      }
      for (let li = 0; li < this.lanes.length; li++) {
        const L = this.lanes[li];
        if (!L.out) {
          // 입고: 벽 뒤(칸 convN)에서 들어와 칸 0 쪽으로
          if (L.queue > 0 && L.seg[c.convN - 1] < 0 && L.res[c.convN - 1] < 0) {
            const pid = this.takeHidden();
            if (pid >= 0) {
              L.queue--;
              const p = this.pal[pid];
              p.kind = CONV; p.y = this.laneY(li); p.z = c.level0Z; p.x = this.segX(c.convN);
              this.startMove(li, pid, c.convN, c.convN - 1);
            }
          }
          for (let k = 1; k < c.convN; k++) {
            const pid = L.seg[k];
            if (pid >= 0 && !this.isMoving(pid) && L.seg[k - 1] < 0 && L.res[k - 1] < 0) this.startMove(li, pid, k, k - 1);
          }
        } else {
          for (let k = c.convN - 1; k >= 0; k--) {
            const pid = L.seg[k];
            if (pid < 0 || this.isMoving(pid)) continue;
            if (k === c.convN - 1) this.startMove(li, pid, k, c.convN);
            else if (L.seg[k + 1] < 0 && L.res[k + 1] < 0) this.startMove(li, pid, k, k + 1);
          }
        }
      }
    }
    isMoving(pid) { for (const m of this.moving) if (m.p === pid) return true; return false; }
    startMove(li, pid, from, to) {
      const c = this.cfg, L = this.lanes[li];
      if (to < c.convN) L.res[to] = pid;
      this.moving.push({ lane: li, p: pid, from, to, x0: this.segX(from), x1: this.segX(to), t: 0, T: profileTime(c.convL, c.convV, c.convA) });
    }
    takeHidden() { for (const p of this.pal) if (p.kind === HIDDEN) return p.id; return -1; }

    // ------------------------------------------------------------ 크레인 작업 계획
    // 입고 칸 고르기: 빈 칸 후보 8개 중 P&D 에서 가장 빨리 가는 곳
    pickEmpty(a) {
      const c = this.cfg; let best = -1, bt = 1e9;
      const zIn = this.forkZ(c.level0Z, true);
      for (let k = 0; k < 40 && (best < 0 || k < 8); k++) {
        const s = this.slotId(a, Math.floor(this.rand() * 2), Math.floor(this.rand() * c.nx), Math.floor(this.rand() * c.levels));
        if (this.slotP[s] >= 0 || this.slotRes[s]) continue;
        const P = this.slotPos(s), tt = this.travelTime(c.pdX, zIn, P[0], this.forkZ(P[2], true));
        if (tt < bt) { bt = tt; best = s; }
      }
      return best;
    }
    // 출고 칸: 보관 팔레트 중 아무거나 (출고 요청은 품목을 고를 수 없다고 봄)
    pickFull(a) {
      const c = this.cfg;
      for (let k = 0; k < 200; k++) {
        const s = this.slotId(a, Math.floor(this.rand() * 2), Math.floor(this.rand() * c.nx), Math.floor(this.rand() * c.levels));
        if (this.slotP[s] >= 0 && !this.slotRes[s]) return s;
      }
      return -1;
    }

    plan(cr) {
      const c = this.cfg, a = cr.aisle, inL = this.lanes[2 * a], outLi = 2 * a + 1;
      const inP = inL.seg[0];
      const inReady = inP >= 0 && !this.isMoving(inP);
      let sIn = -1, sOut = -1;
      if (inReady) sIn = this.pickEmpty(a);
      if (cr.requests > 0) sOut = this.pickFull(a);
      const acts = [];
      const go = (x, z) => acts.push({ k: "move", x, z });
      if (sIn >= 0) {
        this.slotRes[sIn] = 1;
        go(c.pdX, this.forkZ(c.level0Z, false));
        acts.push({ k: "fork", get: true, side: -1, lane: 2 * a, slot: -1, z0: c.level0Z });
        const P = this.slotPos(sIn);
        go(P[0], this.forkZ(P[2], true));
        acts.push({ k: "fork", get: false, side: this.slotSide(sIn) ? 1 : -1, lane: -1, slot: sIn, z0: P[2] });
        cr.stores++;
      }
      if (sOut >= 0) {
        this.slotRes[sOut] = 1; cr.requests--;
        this.pal[this.slotP[sOut]].reqT = cr.reqTimes.shift();
        const P = this.slotPos(sOut);
        go(P[0], this.forkZ(P[2], false));
        acts.push({ k: "fork", get: true, side: this.slotSide(sOut) ? 1 : -1, lane: -1, slot: sOut, z0: P[2] });
        go(c.pdX, this.forkZ(c.level0Z, true));
        acts.push({ k: "wait", lane: outLi });
        acts.push({ k: "fork", get: false, side: 1, lane: outLi, slot: -1, z0: c.level0Z });
        cr.retrieves++;
      }
      if (!acts.length) return false;
      cr.cycles++; if (sIn >= 0 && sOut >= 0) cr.dual++;
      cr.acts = acts;
      return true;
    }

    // ------------------------------------------------------------ 크레인 동작
    stepCrane(cr, dt) {
      const c = this.cfg;
      if (!cr.acts.length && !this.plan(cr)) { cr.state = "idle"; return; }
      cr.busyTime += dt;
      const A = cr.acts[0];
      if (A.t === undefined) this.beginAct(cr, A);
      A.t += dt;
      if (A.k === "move") {
        const tx = Math.min(A.t, A.Tx), tz = Math.min(A.t, A.Tz);
        cr.x = A.x0 + Math.sign(A.x - A.x0) * profilePos(tx, Math.abs(A.x - A.x0), c.vx, c.ax);
        cr.z = A.z0 + Math.sign(A.z - A.z0) * profilePos(tz, Math.abs(A.z - A.z0), c.vz, c.az);
        cr.state = A.t < Math.max(A.Tx, A.Tz) ? "travel" : "settle";
        if (A.t >= A.T) { cr.x = A.x; cr.z = A.z; this.endAct(cr); }
      } else if (A.k === "wait") {
        cr.state = "wait";
        const L = this.lanes[A.lane];
        if (L.seg[0] < 0 && L.res[0] < 0) { L.res[0] = cr.carry; this.endAct(cr); }
      } else if (A.k === "fork") {
        const R = this.reach, Tf = A.Tf, Tl = A.Tl;
        if (A.t < Tf) {
          cr.state = "forkOut"; cr.fork = A.side * profilePos(A.t, R, c.vf, c.af) / R;
        } else if (A.t < Tf + Tl) {
          cr.fork = A.side; cr.state = A.get ? "lift" : "lower";
          if (A.get && cr.carry < 0) { cr.carry = A.p; cr.support = A.z0; this.pal[A.p].kind = CRANE; }
          const d = profilePos(A.t - Tf, c.lift, c.vl, c.al);
          cr.z = A.zs + (A.get ? d : -d);
        } else {
          if (!A.released) this.release(cr, A);
          cr.state = "forkIn";
          cr.z = A.zs + (A.get ? c.lift : -c.lift);
          cr.fork = A.side * (1 - profilePos(A.t - Tf - Tl, R, c.vf, c.af) / R);
        }
        if (A.t >= A.T) { cr.fork = 0; this.endAct(cr); }
      }
    }

    beginAct(cr, A) {
      const c = this.cfg;
      A.t = 0;
      if (A.k === "move") {
        A.x0 = cr.x; A.z0 = cr.z;
        A.Tx = profileTime(Math.abs(A.x - A.x0), c.vx, c.ax); A.Tz = profileTime(Math.abs(A.z - A.z0), c.vz, c.az);
        A.T = Math.max(A.Tx, A.Tz) + c.settle;
        cr.travelDist += Math.abs(A.x - A.x0); cr.hoistDist += Math.abs(A.z - A.z0);
      } else if (A.k === "fork") {
        A.Tf = profileTime(this.reach, c.vf, c.af); A.Tl = profileTime(c.lift, c.vl, c.al);
        A.T = 2 * A.Tf + A.Tl; A.zs = cr.z; A.released = false;
        A.p = A.get ? (A.slot >= 0 ? this.slotP[A.slot] : this.lanes[A.lane].seg[0]) : cr.carry;
        if (!A.get) cr.support = A.z0;
        this.events.push({ t: this.t, crane: cr.id, kind: A.get ? "get" : "put", where: A.slot >= 0 ? "rack" : "conv",
          level: A.slot >= 0 ? this.slotL(A.slot) : -1, x: cr.x, z: cr.z });
      }
    }

    // 포크를 접기 시작할 때: 꺼냈으면 원래 자리를 비우고, 넣었으면 팔레트를 내려놓음
    release(cr, A) {
      const c = this.cfg;
      A.released = true;
      const p = this.pal[A.p];
      if (A.get) {
        cr.support = -1;
        if (A.slot >= 0) { this.slotP[A.slot] = -1; this.slotRes[A.slot] = 0; }
        else this.lanes[A.lane].seg[0] = -1;
      } else {
        cr.carry = -1; cr.support = -1;
        if (A.slot >= 0) {
          this.slotP[A.slot] = A.p; this.slotRes[A.slot] = 0; p.kind = SLOT; p.ref = A.slot; this.stats.stored++;
          const P = this.slotPos(A.slot); p.x = P[0]; p.y = P[1]; p.z = P[2];
        } else {
          const L = this.lanes[A.lane];
          L.seg[0] = A.p; L.res[0] = -1; p.kind = CONV; p.ref = A.lane * c.convN; this.stats.retrieved++;
          p.x = this.segX(0); p.y = this.laneY(A.lane); p.z = c.level0Z;
        }
      }
    }

    endAct(cr) { cr.acts.shift(); }

    // ------------------------------------------------------------ 한 스텝
    step(dt) {
      const c = this.cfg;
      for (let a = 0; a < c.aisles; a++) {
        while (this.nextIn[a] <= this.t) { this.lanes[2 * a].queue++; this.stats.inArrived++; this.nextIn[a] += this.expo(3600 / c.inRate); }
        while (this.nextOut[a] <= this.t) {
          const cr = this.cranes[a]; cr.requests++; cr.reqTimes.push(this.nextOut[a]);
          this.stats.outArrived++; this.nextOut[a] += this.expo(3600 / c.outRate);
        }
      }
      this.stepConveyors(dt);
      for (const cr of this.cranes) this.stepCrane(cr, dt);
      this.t += dt;
    }
    run(seconds, dt) { dt = dt || 1 / 60; const n = Math.round(seconds / dt); for (let i = 0; i < n; i++) this.step(dt); return this.kpi(); }

    // 팔레트 바닥 가운데 위치 [x, y, z] (숨은 팔레트는 z = -5)
    palletPose(id) {
      const p = this.pal[id];
      if (p.kind === CRANE) {
        for (const cr of this.cranes) if (cr.carry === id) {
          const c = this.cfg;
          let z = cr.z - (c.palletH - 0.022);
          if (cr.support >= 0 && z < cr.support) z = cr.support;
          return [cr.x, this.aisleY(cr.aisle) + cr.fork * this.reach, z];
        }
      }
      if (p.kind === SLOT) return this.slotPos(p.ref);
      if (p.kind === HIDDEN) return [this.segX(this.cfg.convN + 1), 0, -5];
      return [p.x, p.y, p.z];
    }

    kpi() {
      const T = Math.max(1e-9, this.t), s = this.stats, n = this.cranes.length;
      const sum = (f) => this.cranes.reduce((a, cr) => a + f(cr), 0);
      const cyc = sum(cr => cr.cycles);
      return {
        simTime_s: +T.toFixed(1),
        stored: s.stored, retrieved: s.retrieved,
        movesPerHour: +((s.stored + s.retrieved) / T * 3600).toFixed(1),
        movesPerHourPerCrane: +((s.stored + s.retrieved) / T * 3600 / n).toFixed(1),
        dualShare: cyc ? +(sum(cr => cr.dual) / cyc).toFixed(3) : 0,
        avgCycle_s: cyc ? +(sum(cr => cr.busyTime) / cyc).toFixed(1) : null,
        craneUtil: +(sum(cr => cr.busyTime) / (n * T)).toFixed(3),
        inQueue: this.lanes.reduce((a, L) => a + L.queue, 0),
        outBacklog: sum(cr => cr.requests),
        avgOutLead_s: s.exited ? +(s.outLeadSum / s.exited).toFixed(1) : null,
        travel_km: +(sum(cr => cr.travelDist) / 1000).toFixed(2),
        occupancy: +(this.slotP.reduce((a, v) => a + (v >= 0 ? 1 : 0), 0) / this.nSlots).toFixed(3),
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos, STATES, HIDDEN, SLOT, CONV, CRANE };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.StackerCraneSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
