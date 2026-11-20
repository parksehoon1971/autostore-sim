#!/usr/bin/env node
/*
 * Libiao T-Sort 핑거형 소터 시뮬레이션 → Blender 궤적(JSON)
 *   node tools/export_tsorter_trace.js [--seconds 30] [--fps 30] [--warmup 600] [--robots 30] [--seed 3] [--out blender/tsorter_trace.json]
 * Unity(TSorterView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
 * 형식
 *   robots[r][f] = [x, y, heading, tilt]
 *   parcels[id] = {type, tint, born, died, keys:[[f, x, y, z, rx, ry, rz], ...]}  (정지 구간은 키 생략)
 *   drops[] = {f, robot, chute, finger, side, x, y}   (투하 순간, 카메라 동선 맞추기용)
 */
const fs = require("fs");
const path = require("path");
const { Sim, PARCEL_TYPES } = require("../web/tsorter-sim-core.js");

const args = { seconds: 30, fps: 30, warmup: 600, out: path.join(__dirname, "..", "blender", "tsorter_trace.json") };
const opts = {};
const av = process.argv.slice(2);
for (let i = 0; i < av.length; i += 2) {
  const k = av[i].replace(/^--/, ""), v = av[i + 1];
  if (k in args) args[k] = k === "out" ? v : Number(v); else opts[k] = Number(v);
}
const sim = new Sim(opts);
const sub = 1 / 60;
const nWarm = Math.round(args.warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);
const d0 = sim.delivered;
const frames = Math.round(args.seconds * args.fps);
const perFrame = Math.round(1 / args.fps / sub);
const robots = sim.robots.map(() => []);
const parcels = {};
const drops = [];
const r3 = v => Math.round(v * 1000) / 1000;
const last = {};
const dropping = new Set();
for (let f = 0; f <= frames; f++) {
  if (f > 0) for (let k = 0; k < perFrame; k++) sim.step(sub);
  sim.robots.forEach((r, i) => {
    robots[i].push([r3(r.x), r3(r.y), r3(r.h), r3(r.tilt)]);
    const d = r.mode === "act" && r.m.kind === "drop";
    if (d && !dropping.has(i)) {
      const ch = sim.chutes[r.target];
      drops.push({ f, robot: i, chute: ch.id, finger: ch.finger, side: ch.side, x: r3(r.x), y: r3(r.y) });
      dropping.add(i);
    } else if (!d) dropping.delete(i);
  });
  const aliveNow = new Set(sim.alive);
  for (const id of sim.alive) {
    const p = sim.parcels[id];
    if (!parcels[id]) parcels[id] = { type: p.type, tint: r3(p.tint), born: f, died: -1, keys: [] };
    const q = sim.parcelPose(id);
    const k = [f, r3(q.x), r3(q.y), r3(q.z), r3(q.rx), r3(q.ry), r3(q.rz)];
    const s = k.slice(1).join(",");
    if (last[id] !== s) {
      const P = parcels[id];
      // 정지 후 다시 움직일 때 직전 프레임 키를 넣어 보간이 앞당겨지지 않게
      if (P.keys.length && P.keys[P.keys.length - 1][0] < f - 1) P.keys.push([f - 1, ...P.keys[P.keys.length - 1].slice(1)]);
      P.keys.push(k); last[id] = s;
    }
  }
  for (const id in parcels) if (parcels[id].died < 0 && !aliveNow.has(Number(id))) parcels[id].died = f;
}
const c = sim.cfg;
const out = {
  fps: args.fps, frames, warmup: args.warmup, config: c, parcelTypes: PARCEL_TYPES,
  ny: sim.ny, tipX: sim.tipX,
  stations: sim.stations.map(s => s.j),
  chutes: sim.chutes.map(ch => [ch.i, ch.j, ch.side]),
  robots, parcels, drops,
  stats: { delivered30s: sim.delivered - d0, kpi: sim.kpi() },
};
fs.mkdirSync(path.dirname(args.out), { recursive: true });
fs.writeFileSync(args.out, JSON.stringify(out));
console.log(`trace -> ${args.out}  frames=${frames} robots=${robots.length} parcels=${Object.keys(parcels).length} delivered30s=${out.stats.delivered30s} drops=${drops.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
