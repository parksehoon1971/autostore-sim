#!/usr/bin/env node
/*
 * Libiao 3D 소터 시뮬레이션 → Blender 궤적(JSON)
 *   node tools/export_libiao_trace.js [--seconds 30] [--fps 30] [--warmup 600] [--robots 20] [--seed 3] [--out blender/libiao_trace.json]
 * 형식
 *   robots[r][f] = [x, y, heading, tilt]
 *   parcels[id] = {type, tint, born, died, keys:[[f, x, y, z, rx, ry, rz], ...]}  (정지 구간은 키 생략)
 */
const fs = require("fs");
const path = require("path");
const { Sim, PARCEL_TYPES } = require("../web/libiao-sim-core.js");

const args = { seconds: 30, fps: 30, warmup: 600, out: path.join(__dirname, "..", "blender", "libiao_trace.json") };
const opts = {};
const av = process.argv.slice(2);
for (let i = 0; i < av.length; i += 2) {
  const k = av[i].replace(/^--/, ""), v = av[i + 1];
  if (k in args) args[k] = k === "out" ? v : Number(v); else opts[k] = Number(v);
}
const sim = new Sim(opts);
const sub = 1 / (args.fps * 2);
for (let t = 0; t < args.warmup; t += sub) sim.step(sub);
const t0 = sim.delivered;
const frames = Math.round(args.seconds * args.fps);
const robots = sim.robots.map(() => []);
const parcels = {};
const r3 = v => Math.round(v * 1000) / 1000;
const last = {};
const perFrame = Math.round(1 / args.fps / sub);
for (let f = 0; f <= frames; f++) {
  if (f > 0) for (let k = 0; k < perFrame; k++) sim.step(sub);
  sim.robots.forEach((r, i) => robots[i].push([r3(r.x), r3(r.y), r3(r.h), r3(r.tilt)]));
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
  fps: args.fps, frames, config: c, parcelTypes: PARCEL_TYPES,
  stations: sim.stations.map(s => s.x),
  chutes: sim.chutes.map(ch => [ch.i, ch.j]),
  robots, parcels,
  stats: { delivered30s: sim.delivered - t0, throughputPerHour: Math.round(sim.throughputPerHour), blockedShare: r3(sim.blockedShare) },
};
fs.writeFileSync(args.out, JSON.stringify(out));
console.log(`trace -> ${args.out}  frames=${frames} robots=${robots.length} parcels=${Object.keys(parcels).length} delivered=${out.stats.delivered30s} thr=${out.stats.throughputPerHour}/h`);
