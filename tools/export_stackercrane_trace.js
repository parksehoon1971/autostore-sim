// 스태커크레인 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_stackercrane_trace.js [--warmup 624] [--seconds 30] [--fps 30] [--seed 6] [--out blender/stackercrane_trace.json]
// Unity(StackerCraneView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim, STATES } = require(path.join(__dirname, "..", "web", "stackercrane-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 600), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "stackercrane_trace.json"));
const sim = new Sim({ seed: +arg("seed", 6) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);
sim.events.length = 0;
const t0 = sim.t;

const r4 = v => +v.toFixed(4);
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const cranes = sim.cranes.map(() => []);
const nP = sim.pal.length;
const initial = [];
for (let id = 0; id < nP; id++) initial.push(sim.palletPose(id).map(r4));
const last = initial.map(p => p.join(","));
const tracks = new Map();

for (let f = 0; f <= frames; f++) {
  for (const cr of sim.cranes) cranes[cr.id].push([r4(cr.x), r4(cr.z), r4(cr.fork), cr.carry, STATES.indexOf(cr.state)]);
  for (let id = 0; id < nP; id++) {
    const p = sim.palletPose(id).map(r4), key = p.join(",");
    if (key !== last[id]) {
      if (!tracks.has(id)) tracks.set(id, []);
      const tk = tracks.get(id), prev = last[id].split(",").map(Number);
      if (f > 0 && (!tk.length || tk[tk.length - 1][0] !== f - 1)) tk.push([f - 1, ...prev]);
      tk.push([f, ...p]); last[id] = key;
    }
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const events = sim.events.filter(e => e.t - t0 <= seconds).map(e => ({ ...e, f: Math.round((e.t - t0) * fps), t: r4(e.t - t0), x: r4(e.x), z: r4(e.z) }));
const cfgKeys = ["aisles", "nx", "levels", "slotW", "bay", "rackD", "aisleW", "flue", "level0Z", "levelH", "palletL", "palletW", "palletH",
  "pdX", "convN", "convL", "seed"];
const data = {
  cfg: Object.fromEntries(cfgKeys.map(k => [k, c[k]])),
  depthY: r4(sim.depthY), reach: r4(sim.reach), aisleY: sim.cranes.map(cr => r4(sim.aisleY(cr.aisle))),
  laneY: sim.lanes.map((L, i) => r4(sim.laneY(i))),
  fps, frames, warmup, sub, kpi: sim.kpi(), states: STATES,
  pallets: sim.pal.map(p => [r4(p.load), p.variant]),
  initialPallets: initial, cranes, palletTracks: Object.fromEntries(tracks), events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} cranes=${cranes.length} pallets=${nP} moved=${tracks.size} events=${events.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
for (const e of events) console.log(JSON.stringify(e));
