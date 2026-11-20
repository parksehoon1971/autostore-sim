/*
 * 수평형 + 수직형 캐러셀 (회전 선반) 피킹 이산시간 시뮬레이션 코어 (브라우저 + Node 공용)
 * Unity C# 버전(Assets/Carousel/Scripts/CarouselSim.cs)과 같은 로직입니다. 같은 mulberry32 난수를 씁니다.
 *
 * 장면: 왼쪽에 수평형 캐러셀 2대가 나란한 피킹 포드 (작업자 1명), 오른쪽에 수직형 캐러셀 1대 (작업자 1명).
 *  - 수평형: 타원 궤도를 따라 선반 캐리어가 돌고, 앞쪽 끝(피킹면)으로 목표 캐리어를 가져옴.
 *    작업자가 한 대에서 피킹하는 동안 다른 한 대는 다음 캐리어로 미리 돌아감 (포드 운영).
 *  - 수직형: 캐비닛 안에서 트레이(캐리어)가 위아래로 순환하고, 목표 트레이를 작업창 높이로 가져옴.
 *  - 둘 다 가까운 쪽으로 돎 (양방향 최단 경로). 남은 주문 줄 중 가장 가까운 캐리어부터.
 *  - 피킹 라이트가 선반 단(수평형) 또는 트레이 칸(수직형)을 가리키고, 작업자가 꺼내 뒤쪽 주문 토트(풋 라이트)에 넣음.
 *
 * 좌표: x 오른쪽, y 안쪽 (작업자 쪽이 y < 0), z 위. 거리 m, 시간 s.
 * 수평형 k: 궤도 중심선 = 캐리어 안쪽 면. 앞쪽 끝 꼭짓점 (hx[k], hy0 - hr) 에서 s = 0, +x 쪽으로 출발해 반시계 방향.
 * 수직형: 앞쪽 기둥 작업창 높이에서 u = 0, 위로 올라가 뒤로 넘어가고 아래로 내려와 다시 앞으로 (y-z 평면).
 */
(function (root) {
  "use strict";

  const DEFAULTS = {
    // 수평형 (가정: 캐리어 폭 0.52 m, 피치 0.62 m, 6단 x 2빈, 최고 0.45 m/s)
    hCount: 2, hCarriers: 26, hPitch: 0.62, hR: 0.40, hW: 0.52, hD: 0.46,
    hShelves: 6, hBins: 2, hShelf0: 0.30, hShelfH: 0.33, hTopZ: 2.45,
    hx: [0, 2.3],
    hV: 0.45, hAcc: 0.35,
    hPickMin: 3.0, hPickMax: 5.0, walkT: 1.8,
    hSlots: 8, hOrderRate: 280,               // 주문 / 시간 (포드 능력의 약 90%)
    // 수직형 (가정: 트레이 폭 2.45 m, 깊이 0.45 m, 피치 0.66 m, 트레이 20개, 최고 0.20 m/s)
    vCarriers: 20, vPitch: 0.66, vR: 0.46, vW: 2.45, vD: 0.45, vH: 0.40, vBins: 6,
    vx: 6.6, vFrontY: 0.33, vZb: 0.80, vWinZ: 1.05,
    vV: 0.20, vAcc: 0.25,
    vPickMin: 4.0, vPickMax: 6.5,
    vSlots: 4, vOrderRate: 170,
    linesMax: 3,                               // 주문당 줄 1~3
    seed: 22,                                 // 30초 영상 기준 시드 (두 장비가 다 움직이는 구간이 긴 것)
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
  const mod = (a, n) => ((a % n) + n) % n;

  class Sim {
    constructor(opts) {
      const c = this.cfg = Object.assign({}, DEFAULTS, opts || {});
      this.rand = mulberry32(c.seed);
      this.t = 0;
      // 궤도 길이
      this.hPer = c.hCarriers * c.hPitch;
      this.hL = (this.hPer - 2 * Math.PI * c.hR) / 2;      // 직선 구간 길이
      this.hY0 = c.hR + c.hD;                               // 앞쪽 반원 중심 y (캐리어 바깥면이 y = 0)
      this.vPer = c.vCarriers * c.vPitch;
      this.vL = (this.vPer - 2 * Math.PI * c.vR) / 2;      // 수직 직선 구간 길이
      this.vZt = c.vZb + this.vL;                           // 위쪽 반원 중심 z
      this.vBackY = c.vFrontY + 2 * c.vR;
      // 캐러셀 (0..hCount-1 수평형, hCount 수직형)
      this.units = [];
      for (let k = 0; k <= c.hCount; k++) {
        const vert = k === c.hCount;
        this.units.push({
          id: k, vert, n: vert ? c.vCarriers : c.hCarriers, pitch: vert ? c.vPitch : c.hPitch,
          vmax: vert ? c.vV : c.hV, acc: vert ? c.vAcc : c.hAcc,
          step: 0,                      // 앞(피킹 위치)에 온 캐리어 = mod(-step, n)
          moveK: 0, moveT0: 0, moveT: 0, // 움직이는 중: 캐리어 moveK 칸 (부호 = 방향)
          pending: [], locked: false, busyTime: 0, travel: 0, moves: 0,
        });
      }
      // 작업자: 0 = 수평형 포드 (캐러셀 0, 1), 1 = 수직형
      this.ops = [
        { id: 0, faces: [], at: 0, state: 0, tEnd: 0, line: null, pickT0: 0, pickT: 0, walkFrom: 0, slots: new Array(c.hSlots).fill(-1), queue: [], rate: c.hOrderRate, nextT: 0, busyTime: 0, picks: 0, done: 0, leadSum: 0 },
        { id: 1, faces: [c.hCount], at: c.hCount, state: 0, tEnd: 0, line: null, pickT0: 0, pickT: 0, walkFrom: c.hCount, slots: new Array(c.vSlots).fill(-1), queue: [], rate: c.vOrderRate, nextT: 0, busyTime: 0, picks: 0, done: 0, leadSum: 0 },
      ];
      for (let k = 0; k < c.hCount; k++) this.ops[0].faces.push(k);
      this.orders = [];
      for (const o of this.ops) o.nextT = this.expo(3600 / o.rate);
      // 처음 위치를 조금 섞음
      for (const u of this.units) u.step = Math.floor(this.rand() * u.n);
    }

    expo(mean) { return -Math.log(1 - this.rand()) * mean; }

    // ---------------------------------------------------------------- 주문
    newOrder(op) {
      const c = this.cfg;
      const nl = 1 + Math.floor(this.rand() * c.linesMax);
      const o = { id: this.orders.length, op: op.id, tArrive: this.t, tDone: -1, left: nl, slot: -1, lines: [] };
      for (let i = 0; i < nl; i++) {
        let unit, carrier, shelf, bin;
        if (op.id === 0) {
          unit = Math.floor(this.rand() * c.hCount);
          carrier = Math.floor(this.rand() * c.hCarriers);
          shelf = Math.floor(this.rand() * c.hShelves);
          bin = Math.floor(this.rand() * c.hBins);
        } else {
          unit = c.hCount;
          carrier = Math.floor(this.rand() * c.vCarriers);
          shelf = 0;
          bin = Math.floor(this.rand() * c.vBins);
        }
        o.lines.push({ order: o.id, unit, carrier, shelf, bin, done: false });
      }
      this.orders.push(o);
      op.queue.push(o);
    }

    assignSlots(op) {
      while (op.queue.length) {
        const s = op.slots.indexOf(-1);
        if (s < 0) break;
        const o = op.queue.shift();
        o.slot = s; op.slots[s] = o.id;
        for (const ln of o.lines) this.units[ln.unit].pending.push(ln);
      }
    }

    // ---------------------------------------------------------------- 캐러셀
    frontCarrier(u) { return mod(-u.step, u.n); }
    // 움직이는 중이면 소수 칸 위치 (step 단위)
    stepPos(u) {
      if (!u.moveK) return u.step;
      const d = Math.abs(u.moveK) * u.pitch;
      const p = profilePos(this.t - u.moveT0, d, u.vmax, u.acc) / u.pitch;
      return u.step + Math.sign(u.moveK) * p;
    }
    offset(u) { return mod(this.stepPos(u) * u.pitch, u.n * u.pitch); }
    // 캐리어 i 의 궤도 위치 (m)
    carrierS(u, i) { return mod((i + this.stepPos(u)) * u.pitch, u.n * u.pitch); }
    // 캐리어를 앞으로 가져오는 최단 칸 수 (부호 = 방향)
    delta(u, i) {
      let k = mod(-i - u.step, u.n);
      if (k > u.n / 2) k -= u.n;
      return k;
    }
    readyLine(u) {
      if (u.moveK) return null;
      const f = this.frontCarrier(u);
      for (const ln of u.pending) if (ln.carrier === f) return ln;
      return null;
    }

    stepUnit(u, dt) {
      if (u.moveK) {
        u.busyTime += dt;
        if (this.t - u.moveT0 >= u.moveT - 1e-9) {
          u.step = mod(u.step + u.moveK, u.n); u.moveK = 0;
        }
        return;
      }
      if (u.locked || !u.pending.length || this.readyLine(u)) return;
      let best = null, bd = 1e9;
      for (const ln of u.pending) {
        const d = Math.abs(this.delta(u, ln.carrier));
        if (d < bd) { bd = d; best = ln; }
      }
      const k = this.delta(u, best.carrier);
      u.moveK = k; u.moveT0 = this.t; u.moveT = profileTime(Math.abs(k) * u.pitch, u.vmax, u.acc);
      u.travel += Math.abs(k) * u.pitch; u.moves++;
    }

    // ---------------------------------------------------------------- 작업자
    stepOp(op, dt) {
      const c = this.cfg;
      if (op.state === 2) op.busyTime += dt;
      if (op.state !== 0 && this.t < op.tEnd - 1e-9) return;
      if (op.state === 2) {
        const ln = op.line, u = this.units[ln.unit], o = this.orders[ln.order];
        ln.done = true; u.pending.splice(u.pending.indexOf(ln), 1); u.locked = false;
        op.picks++; op.line = null;
        if (--o.left === 0) {
          o.tDone = this.t; op.done++; op.leadSum += o.tDone - o.tArrive;
          op.slots[o.slot] = -1;
          this.assignSlots(op);
        }
      }
      op.state = 0;
      // 지금 선 자리의 캐러셀이 준비되면 피킹, 아니면 준비된 다른 캐러셀로 걸어감
      const here = this.units[op.at];
      let ln = this.readyLine(here);
      if (ln) {
        op.state = 2; op.line = ln; here.locked = true;
        op.pickT0 = this.t;
        const lo = op.id === 0 ? c.hPickMin : c.vPickMin, hi = op.id === 0 ? c.hPickMax : c.vPickMax;
        op.pickT = lo + this.rand() * (hi - lo); op.tEnd = this.t + op.pickT;
        return;
      }
      for (const k of op.faces) {
        if (k === op.at) continue;
        if (this.readyLine(this.units[k])) {
          op.state = 1; op.walkFrom = op.at; op.at = k; op.pickT0 = this.t; op.tEnd = this.t + c.walkT;
          return;
        }
      }
    }

    step(dt) {
      this.t += dt;
      for (const op of this.ops) {
        while (this.t >= op.nextT) { this.newOrder(op); op.nextT += this.expo(3600 / op.rate); }
        this.assignSlots(op);
      }
      for (const u of this.units) this.stepUnit(u, dt);
      for (const op of this.ops) this.stepOp(op, dt);
    }

    // ---------------------------------------------------------------- 형상 (Blender / Unity 와 같은 식)
    // 수평형 궤도 위 점 (s, m) → [x, y, 바깥 법선 각도(rad, 0 = +x)]
    hPath(k, s) {
      const c = this.cfg, r = c.hR, L = this.hL, x0 = c.hx[k], y0 = this.hY0;
      s = mod(s, this.hPer);
      const q = Math.PI * r / 2;
      if (s < q) { const a = -Math.PI / 2 + s / r; return [x0 + r * Math.cos(a), y0 + r * Math.sin(a), a]; }
      s -= q;
      if (s < L) return [x0 + r, y0 + s, 0];
      s -= L;
      if (s < 2 * q) { const a = s / r; return [x0 + r * Math.cos(a), y0 + L + r * Math.sin(a), a]; }
      s -= 2 * q;
      if (s < L) return [x0 - r, y0 + L - s, Math.PI];
      s -= L;
      const a = Math.PI + s / r; return [x0 + r * Math.cos(a), y0 + r * Math.sin(a), a];
    }
    // 수평형 캐리어 중심 [x, y, 각도]
    hCarrierPose(k, i) {
      const p = this.hPath(k, this.carrierS(this.units[k], i)), h = this.cfg.hD / 2;
      return [p[0] + Math.cos(p[2]) * h, p[1] + Math.sin(p[2]) * h, p[2]];
    }
    // 수직형 궤도 위 점 (u, m) → [y, z] (트레이 중심, 트레이는 늘 수평)
    vPath(u) {
      const c = this.cfg, r = c.vR, yF = c.vFrontY, yB = this.vBackY, zb = c.vZb, zt = this.vZt;
      u = mod(u, this.vPer);
      const a0 = zt - c.vWinZ;                    // 작업창에서 위쪽 반원까지
      if (u < a0) return [yF, c.vWinZ + u];
      u -= a0;
      const hc = Math.PI * r;
      if (u < hc) { const a = u / r; return [yF + r - r * Math.cos(a), zt + r * Math.sin(a)]; }
      u -= hc;
      if (u < this.vL) return [yB, zt - u];
      u -= this.vL;
      if (u < hc) { const a = u / r; return [yB - r + r * Math.cos(a), zb - r * Math.sin(a)]; }
      u -= hc;
      return [yF, zb + u];
    }
    vCarrierPose(i) { return this.vPath(this.carrierS(this.units[this.cfg.hCount], i)); }

    kpi() {
      const T = Math.max(1e-9, this.t), c = this.cfg;
      const hu = this.units.slice(0, c.hCount), vu = this.units[c.hCount], o0 = this.ops[0], o1 = this.ops[1];
      const r1 = v => +v.toFixed(1), r3 = v => +v.toFixed(3);
      return {
        simTime_s: r1(T),
        hPod_linesPerHour: r1(o0.picks / T * 3600), hPod_ordersPerHour: r1(o0.done / T * 3600),
        hPod_avgLead_s: o0.done ? r1(o0.leadSum / o0.done) : null, hPod_operatorUtil: r3(o0.busyTime / T),
        hPod_carouselUtil: r3(hu.reduce((a, u) => a + u.busyTime, 0) / (hu.length * T)),
        hPod_avgMove_m: r3(hu.reduce((a, u) => a + u.travel, 0) / Math.max(1, hu.reduce((a, u) => a + u.moves, 0))),
        hPod_queue: o0.queue.length,
        v_linesPerHour: r1(o1.picks / T * 3600), v_ordersPerHour: r1(o1.done / T * 3600),
        v_avgLead_s: o1.done ? r1(o1.leadSum / o1.done) : null, v_operatorUtil: r3(o1.busyTime / T),
        v_carouselUtil: r3(vu.busyTime / T), v_avgMove_m: r3(vu.travel / Math.max(1, vu.moves)), v_queue: o1.queue.length,
      };
    }
  }

  const api = { Sim, DEFAULTS, profileTime, profilePos };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.CarouselSim = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
