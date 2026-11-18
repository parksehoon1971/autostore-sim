// 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_trace.js [--warmup 900] [--seconds 30] [--fps 30] [--robots 8] [--seed 3] [--out blender/trace.json]
const path = require("path");
const fs = require("fs");
const { Sim } = require(path.join(__dirname, "..", "web", "sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 900), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const out = arg("out", path.join(__dirname, "..", "blender", "trace.json"));
const sim = new Sim({ robots: +arg("robots", 8), seed: +arg("seed", 3), orderRate: +arg("rate", 300) });
const c = sim.cfg;

// 워밍업: 운영 중인 상태에서 촬영 시작
const sub = 0.01;
for (let t = 0; t < warmup; t += sub) sim.step(sub);

function binPose(id) {
  const L = sim.binLoc[id];
  if (L.kind === "stack") { const k = sim.stack(L.x, L.y).indexOf(id); return [sim.worldX(L.x), sim.worldY(L.y), k * c.binH + 0.005]; }
  if (L.kind === "robot") { const r = sim.robots[L.r]; return [r.px, r.py, sim.restZ - r.g]; }
  const p = sim.ports[L.p];
  const i = p.queue.indexOf(id);
  // 포트: 맨 앞 빈은 제시 위치, 나머지(대기/완료)는 포트 하우징 안쪽 아래
  const z = i === 0 ? c.portZ : c.portZ - 0.45;
  return [sim.worldX(p.x), sim.worldY(-1), z];
}

const frames = Math.round(seconds * fps);
const robots = sim.robots.map(() => []);
const binTracks = new Map();                // id -> [[f, x, y, z], ...] (변화 시점만)
const initial = [];
for (let id = 0; id < sim.bins; id++) initial.push(binPose(id).map(v => +v.toFixed(4)));
const last = initial.map(p => p.join(","));
const portState = sim.ports.map(() => []);

for (let f = 0; f <= frames; f++) {
  for (const r of sim.robots) robots[r.id].push([+r.px.toFixed(4), +r.py.toFixed(4), +r.g.toFixed(4), r.carry, r.state]);
  for (let id = 0; id < sim.bins; id++) {
    const p = binPose(id).map(v => +v.toFixed(4)); const key = p.join(",");
    if (key !== last[id]) {
      if (!binTracks.has(id)) binTracks.set(id, [[f - 1, ...initial[id]]]);
      binTracks.get(id).push([f, ...p]); last[id] = key;
    }
  }
  sim.ports.forEach((p, i) => portState[i].push(p.queue.length ? 1 : 0));
  const steps = Math.round(1 / fps / sub);
  for (let s = 0; s < steps; s++) sim.step(sub);
}

const data = {
  cfg: { nx: c.nx, ny: c.ny, nz: c.nz, cellX: c.cellX, cellY: c.cellY, binH: c.binH, ports: c.ports, portZ: c.portZ,
         topZ: sim.topZ, restZ: sim.restZ },
  fps, frames, warmup, kpi: sim.kpi(),
  initialBins: initial,
  robots, binTracks: Object.fromEntries(binTracks), portActive: portState,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} robots=${robots.length} bins=${sim.bins} movedBins=${binTracks.size}`);
console.log("KPI", JSON.stringify(sim.kpi()));
