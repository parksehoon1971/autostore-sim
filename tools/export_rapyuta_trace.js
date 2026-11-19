// Rapyuta ASRS 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_rapyuta_trace.js [--warmup 600] [--seconds 30] [--fps 30] [--robots 20] [--seed 3] [--out blender/rapyuta_trace.json]
// Unity(RapyutaView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim } = require(path.join(__dirname, "..", "web", "rapyuta-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 600), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "rapyuta_trace.json"));
const sim = new Sim({ robots: +arg("robots", 20), seed: +arg("seed", 3) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const STATE = { idle: 0, move: 1, carry: 2, lift: 3, lower: 4, blocked: 5, liftwait: 6, ride: 7, hold: 8 };
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const robots = sim.robots.map(() => []), lifts = sim.lifts.map(() => []), stations = sim.stations.map(() => []);
const initial = [];
for (let id = 0; id < sim.bins; id++) initial.push(sim.binPose(id).map(r4));
const last = initial.map(p => p.join(","));
const binTracks = new Map();
const events = [];
const digging = new Set();

for (let f = 0; f <= frames; f++) {
  for (const r of sim.robots) robots[r.id].push([r4(r.px), r4(r.py), r4(r.pz), r4(r.lift), r.carry, STATE[r.state] ?? 0, r.f]);
  sim.lifts.forEach((L, i) => lifts[i].push(r4(L.z)));
  sim.stations.forEach((S, i) => stations[i].push(S.cur));
  for (let id = 0; id < sim.bins; id++) {
    const p = sim.binPose(id).map(r4), key = p.join(",");
    if (key !== last[id]) {
      // 멈춰 있다가 다시 움직이면 직전 프레임 위치를 먼저 넣어 보간이 미끄러지지 않게 함
      if (!binTracks.has(id)) binTracks.set(id, []);
      const tk = binTracks.get(id), prev = last[id].split(",").map(Number);
      if (f > 0 && (!tk.length || tk[tk.length - 1][0] !== f - 1)) tk.push([f - 1, ...prev]);
      tk.push([f, ...p]); last[id] = key;
    }
  }
  // 앞쪽 빈을 치우는 중(들고 있는 빈이 locked)인 로봇의 시작 프레임 기록
  for (const r of sim.robots) {
    const d = r.carry >= 0 && sim.locked.has(r.carry);
    if (d && !digging.has(r.id)) { digging.add(r.id); events.push({ f, kind: "dig", robot: r.id, bin: r.carry, floor: r.f, x: r.x, y: r.y }); }
    else if (!d) digging.delete(r.id);
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const data = {
  cfg: { nx: c.nx, ny: c.ny, nf: c.nf, cellX: c.cellX, cellY: c.cellY, floorH: c.floorH, baseZ: c.baseZ, binH: c.binH,
         binFoot: c.binFoot, robotH: c.robotH, aisleStep: c.aisleStep, bufferRows: c.bufferRows, lifts: c.lifts,
         stationFloor: c.stationFloor, stations: c.stations, seed: c.seed, robots: c.robots },
  fps, frames, warmup, sub, kpi: sim.kpi(),
  states: Object.keys(STATE),
  initialBins: initial, binPopular: sim.nPopular,
  robots, lifts, stations, binTracks: Object.fromEntries(binTracks), events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} robots=${robots.length} bins=${sim.bins} movedBins=${binTracks.size} digEvents=${events.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
