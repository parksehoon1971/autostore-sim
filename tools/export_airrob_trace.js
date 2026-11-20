// Libiao AirRob 방식 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_airrob_trace.js [--warmup 600] [--seconds 30] [--fps 30] [--bots 16] [--seed 3] [--out blender/airrob_trace.json]
// Unity(AirRobView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim } = require(path.join(__dirname, "..", "web", "airrob-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 600), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "airrob_trace.json"));
const sim = new Sim({ bots: +arg("bots", 16), seed: +arg("seed", 3) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const BOT = { idle: 0, move: 1, carry: 2, blocked: 3, wait: 4, pick: 5 };
const AIR = { idle: 0, move: 1, lift: 2, pull: 3, push: 4, wait: 5 };
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const bots = sim.robots.map(() => []), airs = sim.airs.map(() => []), stations = sim.stations.map(() => []);
const initial = [];
for (let id = 0; id < sim.totes; id++) initial.push(sim.totePose(id).map(r4));
const last = initial.map(p => p.join(","));
const toteTracks = new Map();
const events = [];
const hooking = new Set();

for (let f = 0; f <= frames; f++) {
  for (const r of sim.robots) bots[r.id].push([r4(r.px), r4(r.py), r.carry, BOT[r.state] ?? 0]);
  for (const A of sim.airs) airs[A.id].push([r4(A.px), r4(A.pz), r4(A.hook), A.carry, AIR[A.state] ?? 0]);
  sim.stations.forEach((S, i) => stations[i].push(S.cur));
  for (let id = 0; id < sim.totes; id++) {
    const p = sim.totePose(id).map(r4), key = p.join(",");
    if (key !== last[id]) {
      if (!toteTracks.has(id)) toteTracks.set(id, []);
      const tk = toteTracks.get(id), prev = last[id].split(",").map(Number);
      if (f > 0 && (!tk.length || tk[tk.length - 1][0] !== f - 1)) tk.push([f - 1, ...prev]);
      tk.push([f, ...p]); last[id] = key;
    }
  }
  // 훅 동작 시작 프레임 기록 (카메라 동선 맞추기용)
  for (const A of sim.airs) {
    const a = A.acts[0], on = a && a.k === "hook" && a.t !== undefined;
    if (on && !hooking.has(A.id)) {
      hooking.add(A.id);
      events.push({ f, air: A.id, mode: a.mode, where: a.src || a.dest, job: a.job.kind, bot: a.job.bot.id, x: r4(A.px), z: r4(A.pz),
        level: a.src === "slot" || a.dest === "slot" ? sim.slotLevel(a.job.slot) : -1, side: a.job.side });
    } else if (!on) hooking.delete(A.id);
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const data = {
  cfg: { nx: c.nx, aisles: c.aisles, levels: c.levels, cellX: c.cellX, colOff: c.colOff, aisleW: c.aisleW, rackD: c.rackD, flue: c.flue,
         parkW: c.parkW, level0Z: c.level0Z, levelH: c.levelH, toteW: c.toteW, toteD: c.toteD, toteH: c.toteH, deckZ: c.deckZ,
         stations: c.stations, spurLen: c.spurLen, frontY: c.frontY, spurY: c.spurY, seed: c.seed, bots: c.bots },
  rowY: sim.rowYs.map(r4), depthY: r4(sim.depthY), rackDepthY: r4(sim.rackDepthY),
  fps, frames, warmup, sub, kpi: sim.kpi(),
  botStates: Object.keys(BOT), airStates: Object.keys(AIR),
  initialTotes: initial, totePopular: sim.nPopular,
  bots, airs, stations, toteTracks: Object.fromEntries(toteTracks), events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} bots=${bots.length} airs=${airs.length} totes=${sim.totes} movedTotes=${toteTracks.size} hookOps=${events.length}`);
console.log("KPI", JSON.stringify(sim.kpi()));
