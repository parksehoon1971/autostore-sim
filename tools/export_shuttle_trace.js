// 셔틀랙 시뮬레이션을 돌려 Blender 애니메이션용 궤적(JSON)과 30초 카메라 동선을 내보냅니다.
//   node tools/export_shuttle_trace.js [--warmup 1200] [--seconds 30] [--fps 30] [--seed 3] [--out blender/shuttle_trace.json]
// 카메라는 "주인공 토트" 하나를 따라갑니다: 셔틀이 꺼냄 → 출고 버퍼 → 리프트로 내려옴 → 컨베이어 → 스테이션 피킹.
// 같은 카메라 경로를 Assets/ShuttleRack/Resources/ShuttleCamera.json 에도 써서 Unity 가 그대로 씁니다.
// Unity(ShuttleView)는 같은 설정·같은 1/60 s 스텝으로 시뮬레이션을 직접 돌리므로 같은 장면이 나옵니다.
const path = require("path");
const fs = require("fs");
const S = require(path.join(__dirname, "..", "web", "shuttle-sim-core.js"));

const arg = (k, d) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const warmup = +arg("warmup", 1200), seconds = +arg("seconds", 30), fps = +arg("fps", 30);
const sub = 1 / 60;
const out = arg("out", path.join(__dirname, "..", "blender", "shuttle_trace.json"));
const camOut = arg("cam-out", path.join(__dirname, "..", "Assets", "ShuttleRack", "Resources", "ShuttleCamera.json"));
const sim = new S.Sim({ seed: +arg("seed", 3) });
const c = sim.cfg;

const nWarm = Math.round(warmup / sub);
for (let i = 0; i < nWarm; i++) sim.step(sub);

const r4 = v => +v.toFixed(4);
const STATE = { idle: 0, move: 1, carry: 2, wait: 3, get: 4, put: 5, xfer: 6 };
const frames = Math.round(seconds * fps), per = Math.round(1 / fps / sub);
const shuttles = sim.shuttles.map(() => []), lifts = sim.lifts.map(() => []), stations = sim.stations.map(() => []);
const initial = [];
for (let id = 0; id < sim.totes; id++) initial.push(sim.totePose(id).map(r4));
const last = initial.map(p => p.join(","));
const toteTracks = new Map();
const full = new Map();        // 움직인 토트의 프레임별 위치 (카메라 계산용)
const stageAt = new Map();     // 토트 → [[frame, stage], ...]
const taskAt = new Map();      // 토트 → 셔틀이 꺼내러 출발한 프레임
const lastStage = Array.from(sim.toteStage);

for (let f = 0; f <= frames; f++) {
  for (const sh of sim.shuttles) shuttles[sh.id].push([r4(sh.x), r4(sh.side * sh.arm), sh.carry, STATE[sh.state] ?? 0]);
  for (const lf of sim.lifts) lifts[lf.id].push([r4(lf.z), lf.carry, STATE[lf.state] ?? 0]);
  sim.stations.forEach((st, i) => stations[i].push(st.cur));
  for (const sh of sim.shuttles) if (sh.task >= 0 && sh.carry < 0 && !taskAt.has(sh.task)) taskAt.set(sh.task, f);
  for (let id = 0; id < sim.totes; id++) {
    const p = sim.totePose(id).map(r4), key = p.join(",");
    if (sim.toteStage[id] !== lastStage[id]) {
      if (!stageAt.has(id)) stageAt.set(id, []);
      stageAt.get(id).push([f, sim.toteStage[id]]); lastStage[id] = sim.toteStage[id];
    }
    if (key !== last[id]) {
      if (!toteTracks.has(id)) toteTracks.set(id, []);
      const tk = toteTracks.get(id), prev = last[id].split(",").map(Number);
      if (f > 0 && (!tk.length || tk[tk.length - 1][0] !== f - 1)) tk.push([f - 1, ...prev]);
      tk.push([f, ...p]); last[id] = key;
    }
    if (toteTracks.has(id) || full.has(id)) {
      if (!full.has(id)) full.set(id, new Array(f).fill(initial[id]));
      full.get(id).push(p);
    }
  }
  for (let s = 0; s < per; s++) sim.step(sub);
}

// ------------------------------------------------------------------ 주인공 토트 고르기
const first = (id, st) => { const a = stageAt.get(id) || []; const e = a.find(x => x[1] === st); return e ? e[0] : -1; };
let hero = -1, heroScore = -Infinity, H = null;
for (const [id, f0] of taskAt) {
  const s = sim.toteSlot[id] >= 0 ? sim.toteSlot[id] : -1;
  const tTake = first(id, S.ST_SHUTTLE), tBuf = first(id, S.ST_OUTBUF), tLift = first(id, S.ST_OUTLIFT),
    tConv = first(id, S.ST_OUTCONV), tPick = first(id, S.ST_PICK), tRet = first(id, S.ST_RETCONV);
  if ([tTake, tBuf, tLift, tConv, tPick, tRet].some(v => v < 0)) continue;
  const p0 = full.get(id)[tTake];
  const a = Math.round((p0[1] - c.rackD - c.aisleW / 2) / sim.pitch), l = Math.round((p0[2] - c.level0Z) / c.levelH);
  const bay = Math.round(p0[0] / c.bayW - 0.5);
  if (l < 4) continue;
  if (f0 < 1.5 * fps || f0 > 9 * fps || tRet > 27 * fps) continue;
  const score = -Math.abs(f0 - 4.5 * fps) / fps - 2 * a + Math.min(l, 10) * 0.6 + Math.min(bay, 16) * 0.25 - (tPick - tConv) / fps * 0.15;
  if (score > heroScore) { heroScore = score; hero = id; H = { f0, tTake, tBuf, tLift, tConv, tPick, tRet, a, l, bay }; }
}
if (hero < 0) { for (const [id, f0] of [...taskAt].slice(0, 15)) console.log(id, f0, JSON.stringify(stageAt.get(id))); }
if (hero < 0) throw new Error("주인공 토트를 찾지 못했습니다. --seed 또는 --warmup 을 바꿔 보세요.");
console.log("hero tote", hero, "score", heroScore.toFixed(2), JSON.stringify(H), "sec:", ["f0", "tTake", "tBuf", "tLift", "tConv", "tPick", "tRet"].map(k => k + "=" + (H[k] / fps).toFixed(1)).join(" "));

// ------------------------------------------------------------------ 카메라 동선
const HP = full.get(hero);
// 따라가는 점: 임계 감쇠 스프링으로 부드럽게 (카메라가 덜컹이지 않게)
function smoothTrack(P, tau) {
  const outp = [P[0].slice()], v = [0, 0, 0], dt = 1 / fps, w = 2 / tau;
  for (let f = 1; f < P.length; f++) {
    const x = outp[f - 1].slice();
    for (let k = 0; k < 3; k++) {
      const e = x[k] - P[f][k];
      const acc = -w * w * e - 2 * w * v[k];
      v[k] += acc * dt; x[k] += v[k] * dt;
    }
    outp.push(x);
  }
  return outp;
}
const HS = smoothTrack(HP, 0.35);
const ya = sim.aisleY(H.a), R = sim.R, xl = c.liftX, xs = c.stationX, zc = c.convZ;
const GW = c.nx * c.bayW, GD = sim.depthY, zTop = sim.levelZ(c.levels - 1) + c.toteH + 0.2;
const lerp = (a, b, u) => a.map((v, k) => v + (b[k] - v) * u);
const sm = u => { u = Math.min(1, Math.max(0, u)); return u * u * (3 - 2 * u); };
const T = f => f / fps;
const tA = T(H.f0), tBuf = T(H.tBuf), tLift = T(H.tLift), tConv = T(H.tConv), tPick = T(H.tPick), tRet = T(H.tRet);
const tc = (f) => HS[Math.min(HS.length - 1, Math.max(0, f))];

// 각 샷: (프레임) → [위치, 바라보는 점, 렌즈]
const shots = [
  { // 0: 전경 — 앞 왼쪽 높은 곳에서 통로 0 입구 쪽으로 내려오며 다가감
    t0: 0, fn: f => {
      const u = sm(T(f) / Math.max(1, tA));
      return [lerp([-7.5, -7.0, 6.2], [-4.2, ya - 0.6, 4.2], u), lerp([GW * 0.45, GD * 0.5, 2.6], [4.0, ya, tc(f)[2]], u), 24 + 8 * u];
    } },
  { // 1: 통로 입구 (두 리프트 사이)에서 통로 안쪽을 봄 — 셔틀이 달려와 토트를 출고 버퍼에 내려놓음
    t0: tA - 0.4, blend: 2.0, fn: f => {
      const p = tc(f), u = sm((T(f) - tA) / Math.max(1, tLift - tA));
      return [[-2.2 - 0.3 * u, ya, p[2] + 0.35], [p[0] + 1.5, ya + (p[1] - ya) * 0.4, p[2] + 0.12], 50];
    } },
  { // 2: 리프트 — 토트가 리프트 캐리지에 실려 내려옴. 통로 중심선에서 뒤로 물러나며 따라 내려감
    t0: tLift - 0.2, blend: 1.4, fn: f => {
      const p = tc(f), u = sm((T(f) - tLift) / Math.max(0.5, tConv - tLift));
      return [[-2.2 - 0.4 * u, ya + 0.05, Math.max(1.55, p[2] + 0.55)], [p[0], p[1], p[2] + 0.1], 30];
    } },
  { // 3: 1층 컨베이어 — 앞쪽 옆에서 나란히 따라감
    t0: tConv + 0.4, blend: 1.4, fn: f => {
      const p = tc(f);
      return [[p[0] + 0.9, ya - R - 1.7, 1.75], [p[0] - 0.4, p[1], p[2] + 0.1], 28];
    } },
  { // 4: 스테이션 — 작업자 어깨 너머로 피킹
    t0: tPick - 0.6, blend: 1.4, fn: f => {
      const u = sm((T(f) - tPick) / Math.max(1, tRet - tPick + 1.5));
      return [lerp([xs - 1.7, ya - 1.5, 1.85], [xs - 1.9, ya - 1.1, 2.05], u), [xs + 0.1, ya, zc + 0.15], 32];
    } },
  { // 5: 빠지며 전경 — 스테이션 위로 올라가 리프트 줄과 랙 전체
    t0: tRet + 0.2, blend: 1.8, fn: f => {
      const u = sm((T(f) - tRet - 0.2) / Math.max(1, seconds - tRet - 0.2));
      return [lerp([xs - 2.8, ya - 2.6, 2.6], [-9.0, -6.5, 6.8], u), lerp([xs + 1.0, ya + 0.3, 1.0], [GW * 0.4, GD * 0.5, 2.8], u), 28 - 4 * u];
    } },
];
const cam = [];
for (let f = 0; f <= frames; f++) {
  const t = T(f);
  let i = 0; while (i < shots.length - 1 && t >= shots[i + 1].t0) i++;
  let [p, l, lens] = shots[i].fn(f);
  if (i > 0 && t < shots[i].t0 + shots[i].blend) {
    const w = sm((t - shots[i].t0) / shots[i].blend), [p0, l0, n0] = shots[i - 1].fn(f);
    p = lerp(p0, p, w); l = lerp(l0, l, w); lens = n0 + (lens - n0) * w;
  }
  cam.push([...p.map(r4), ...l.map(r4), r4(lens)]);
}

const data = {
  cfg: { aisles: c.aisles, nx: c.nx, levels: c.levels, bufBays: c.bufBays, bayW: c.bayW, aisleW: c.aisleW, rackD: c.rackD, flue: c.flue,
         level0Z: c.level0Z, levelH: c.levelH, toteW: c.toteW, toteD: c.toteD, toteH: c.toteH, liftX: c.liftX, xferOff: c.xferOff,
         convZ: c.convZ, stationX: c.stationX, seed: c.seed },
  pitch: r4(sim.pitch), depthY: r4(sim.depthY), reach: r4(sim.R),
  fps, frames, warmup, sub, kpi: sim.kpi(),
  states: Object.keys(STATE),
  initialTotes: initial, totePopular: sim.nPopular,
  shuttles, lifts, stations, toteTracks: Object.fromEntries(toteTracks),
  hero: { tote: hero, ...H }, camera: cam,
};
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(data));
fs.mkdirSync(path.dirname(camOut), { recursive: true });
// Unity 자막 시점 (초): 셔틀 출발, 출고 버퍼, 리프트, 컨베이어, 피킹 시작, 피킹 끝
fs.writeFileSync(camOut, JSON.stringify({ fps, warmup, seed: c.seed, hero,
  times: [tA, tBuf, tLift, tConv, tPick, tRet].map(v => +v.toFixed(3)), data: cam.flat() }));
console.log(`trace -> ${out}  frames=${frames} shuttles=${shuttles.length} lifts=${lifts.length} totes=${sim.totes} movedTotes=${toteTracks.size}`);
console.log(`camera -> ${camOut}`);
console.log("KPI", JSON.stringify(sim.kpi()));
