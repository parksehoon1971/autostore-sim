// Exotec Skypod 방식 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_skypod_trace.js [--warmup 600] [--seconds 30] [--fps 30] [--robots 20] [--seed 3] [--out blender/skypod_trace.json]
// Unity(SkypodView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim } = require(path.join(__dirname, "..", "web", "skypod-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 600), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "skypod_trace.json"));
const sim = new Sim({ robots: +arg("robots", 20), seed: +arg("seed", 3) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const STATE = { idle: 0, move: 1, carry: 2, blocked: 3, climb: 4, get: 5, put: 6, pick: 7 };
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const robots = sim.robots.map(() => []), stations = sim.stations.map(() => []);
const initial = [];
for (let id = 0; id < sim.totes; id++) initial.push(sim.totePose(id).map(r4));
const last = initial.map(p => p.join(","));
const toteTracks = new Map();
const events = [];
const climbing = new Set();

for (let f = 0; f <= frames; f++) {
  for (const r of sim.robots) robots[r.id].push([r4(r.px), r4(r.py), r4(r.pz), r4(r.side * r.arm), r.carry, STATE[r.state] ?? 0]);
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
  // 등반 시작 프레임 기록 (카메라 동선 맞추기용)
  for (const r of sim.robots) {
    const a = r.acts[0], up = a && a.k === "climb" && a.t !== undefined;
    if (up && !climbing.has(r.id)) {
      climbing.add(r.id);
      events.push({ f, kind: a.put ? "put" : "get", robot: r.id, level: sim.slotLevel(a.slot), h: r4(a.h), x: r.x, y: r.y, T: r4(a.T) });
    } else if (!up) climbing.delete(r.id);
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const data = {
  cfg: { nx: c.nx, aisles: c.aisles, levels: c.levels, cellX: c.cellX, aisleW: c.aisleW, rackD: c.rackD, flue: c.flue,
         level0Z: c.level0Z, levelH: c.levelH, toteW: c.toteW, toteD: c.toteD, toteH: c.toteH, bayZ: c.bayZ,
         stations: c.stations, spurLen: c.spurLen, frontY: c.frontY, spurY: c.spurY, seed: c.seed, robots: c.robots },
  rowY: sim.rowYs.map(r4), depthY: r4(sim.depthY),
  fps, frames, warmup, sub, kpi: sim.kpi(),
  states: Object.keys(STATE),
  initialTotes: initial, totePopular: sim.nPopular,
  robots, stations, toteTracks: Object.fromEntries(toteTracks), events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} robots=${robots.length} totes=${sim.totes} movedTotes=${toteTracks.size} climbs=${events.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
