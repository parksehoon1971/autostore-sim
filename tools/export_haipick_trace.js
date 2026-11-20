// HaiPick Climb 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_haipick_trace.js [--warmup 600] [--seconds 30] [--fps 30] [--robots 18] [--seed 10] [--out blender/haipick_trace.json]
// Unity(HaiPickView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim, STATES } = require(path.join(__dirname, "..", "web", "haipick-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 600), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "haipick_trace.json"));
const sim = new Sim({ robots: +arg("robots", 18), seed: +arg("seed", 10) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const robots = sim.robots.map(() => []), stations = sim.stations.map(() => []);
const initial = [];
for (let id = 0; id < sim.totes; id++) initial.push(sim.totePose(id).map(r4));
const last = initial.map(p => p.join(","));
const toteTracks = new Map();
const events = [];
const climbing = new Map();

for (let f = 0; f <= frames; f++) {
  for (const r of sim.robots) robots[r.id].push([r4(r.px), r4(r.py), r4(r.pz), r4(r.arm), r4(r.fork), r.side, r.carry, r.state]);
  sim.stations.forEach((S, i) => stations[i].push(S.cur));
  for (let id = 0; id < sim.totes; id++) {
    const p = sim.totePose(id).map(r4), key = p.join(",");
    if (key !== last[id]) {
      // 멈춰 있다가 다시 움직이면 직전 프레임 위치를 먼저 넣어 보간이 미끄러지지 않게 함
      if (!toteTracks.has(id)) toteTracks.set(id, []);
      const tk = toteTracks.get(id), prev = last[id].split(",").map(Number);
      if (f > 0 && (!tk.length || tk[tk.length - 1][0] !== f - 1)) tk.push([f - 1, ...prev]);
      tk.push([f, ...p]); last[id] = key;
    }
  }
  // 등반 시작/최고점 기록 (카메라 동선 맞추기용)
  for (const r of sim.robots) {
    const up = r.state === 5;
    if (up && !climbing.has(r.id)) climbing.set(r.id, { f, robot: r.id, x: r.x, j: r.j, z0: r.pz, zmax: r.pz });
    else if (up) { const e = climbing.get(r.id); e.zmax = Math.max(e.zmax, r.pz); }
    else if (!up && climbing.has(r.id)) { const e = climbing.get(r.id); e.fEnd = f; e.z1 = r.pz; e.zmax = r4(e.zmax); events.push(e); climbing.delete(r.id); }
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const data = {
  cfg: { nx: c.nx, cellX: c.cellX, lanes: c.lanes, aisleW: c.aisleW, rackD: c.rackD, rackBase: c.rackBase, levelH: c.levelH,
         levels: c.levels, beamH: c.beamH, toteW: c.toteW, toteD: c.toteD, toteH: c.toteH, robotL: c.robotL, robotW: c.robotW,
         deckH: c.deckH, stations: c.stations, stationDepth: c.stationDepth, presentZ: c.presentZ, seed: c.seed, robots: c.robots },
  laneY: sim.laneY.map(r4), laneW: sim.laneW, stationY: sim.stationY,
  fps, frames, warmup, sub, kpi: sim.kpi(), states: STATES,
  initialTotes: initial, totePopular: sim.nPopular,
  robots, stations, toteTracks: Object.fromEntries(toteTracks), events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} robots=${robots.length} totes=${sim.totes} movedTotes=${toteTracks.size} climbs=${events.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
