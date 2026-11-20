// 수평형 + 수직형 캐러셀 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)을 내보냅니다.
//   node tools/export_carousel_trace.js [--warmup 300] [--seconds 30] [--fps 30] [--seed 22] [--out blender/carousel_trace.json]
// Unity(CarouselView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const { Sim } = require(path.join(__dirname, "..", "web", "carousel-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 300), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "carousel_trace.json"));
const sim = new Sim({ seed: +arg("seed", 22) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const units = sim.units.map(() => []), ops = sim.ops.map(() => []);
const events = [];
const moving = sim.units.map(() => false);

for (let f = 0; f <= frames; f++) {
  for (const u of sim.units) {
    // 칸 위치는 이어지게 펼쳐서 기록 (n 을 넘어가도 되감지 않음 → 선형 보간 가능)
    let v = sim.stepPos(u);
    const tr = units[u.id];
    if (tr.length) { const p = tr[tr.length - 1]; v += Math.round((p - v) / u.n) * u.n; }
    tr.push(r4(v));
    if (u.moveK && !moving[u.id]) events.push({ f, kind: "move", unit: u.id, k: u.moveK, T: r4(u.moveT) });
    moving[u.id] = !!u.moveK;
  }
  for (const op of sim.ops) {
    const ln = op.line;
    // [선 자리, 상태(0 대기 1 이동 2 피킹), 진행률, 캐러셀, 캐리어, 단, 칸, 주문 칸, 출발 자리]
    const u = op.state ? Math.min(1, (sim.t - op.pickT0) / (op.tEnd - op.pickT0)) : 0;
    ops[op.id].push([op.at, op.state, r4(u), ln ? ln.unit : -1, ln ? ln.carrier : -1, ln ? ln.shelf : -1, ln ? ln.bin : -1,
                     ln ? sim.orders[ln.order].slot : -1, op.walkFrom]);
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

const keys = ["hCount", "hCarriers", "hPitch", "hR", "hW", "hD", "hShelves", "hBins", "hShelf0", "hShelfH", "hTopZ", "hx", "hSlots",
  "vCarriers", "vPitch", "vR", "vW", "vD", "vH", "vBins", "vx", "vFrontY", "vZb", "vWinZ", "vSlots", "seed"];
const data = {
  cfg: Object.fromEntries(keys.map(k => [k, c[k]])),
  hL: r4(sim.hL), hY0: r4(sim.hY0), vL: r4(sim.vL), vZt: r4(sim.vZt), vBackY: r4(sim.vBackY),
  fps, frames, warmup, sub, kpi: sim.kpi(),
  units, ops, events,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
console.log(`trace -> ${out}  frames=${frames} moves=${events.length}`);
for (const e of events) console.log(`  ${(e.f / fps).toFixed(1)}s unit${e.unit} k=${e.k} T=${e.T}`);
console.log("KPI", JSON.stringify(sim.kpi()));
