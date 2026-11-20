"""
Libiao AirRob 방식 "랙 면 레일 로봇 + 바닥 이송 로봇" 토트 창고 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  node tools/export_airrob_trace.js --warmup 1342      # blender/airrob_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/airrob_blender.py -- --out blender/out/airrob --trace blender/airrob_trace.json --still 1,200,330,700,880
  # 2) 30초 영상 (GPU 권장. GPU 없는 PC 는 --engine eevee)
  blender -b -P blender/airrob_blender.py -- --out blender/out/airrob --trace blender/airrob_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_airrob_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더
  --no-export        FBX/GLB 내보내기 생략

공개 수치 (Libiao 매뉴얼·보도자료): AirRob 폭 764 mm, 깊이 820 mm, 승강 범위 2.5~9 m, 수평 2 m/s (2 m/s²),
수직 1 m/s (0.8 m/s²), 토트 당기기·밀기 각 4.5 s, 토트 530 x 410 mm (30 kg 이하), 통로 폭 850 mm, 토트 사이 10 mm.
마스트·캐리지·훅의 형상, 바닥 로봇 치수, 색은 가정입니다 (docs/airrob-research.md). Libiao 제품 도면이 아닙니다.

z-fighting 방지 규칙: 겹치는 상자끼리 같은 평면에 면이 오지 않게 (붙는 장식은 최소 3 mm 띄움, 보는 기둥보다 얇게).
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector

def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else argv[1:]
    a = {"out": "./out", "trace": None, "still": None, "anim": False, "frames": None, "res": "1920x1080",
         "samples": 96, "gpu": False, "no-export": False, "step": 1, "engine": "cycles"}
    i = 0
    while i < len(argv):
        k = argv[i].lstrip("-")
        if k in ("anim", "gpu", "no-export"):
            a[k] = True; i += 1
        elif k in a:
            a[k] = argv[i + 1]; i += 2
        else:
            i += 1
    a["out"] = os.path.abspath(a["out"])
    return a

# 시뮬레이션 설정 (trace.json 의 cfg 로 덮어씀)
C = dict(nx=16, aisles=4, levels=13, cellX=0.90, colOff=0.215, aisleW=0.85, rackD=0.66, flue=0.06, parkW=0.90,
         level0Z=0.85, levelH=0.40, toteW=0.41, toteD=0.53, toteH=0.30, deckZ=0.33, stations=[1, 6], spurLen=4,
         frontY=-0.70, spurY=-1.70)
ROW_Y = []
DEPTH = 9.86; RACK_DEPTH = 8.86
UPR_X, UPR_Y = 0.07, 0.05            # 랙 기둥 단면 (x 폭, y 깊이)
AIR_W, AIR_D = 0.764, 0.82           # AirRob 폭 (x, 통로 방향) · 깊이 (y): 공개 수치
BOT_L, BOT_W = 0.70, 0.46            # 바닥 로봇 (가정)

def wx(i): return (i + 0.5) * C["cellX"]
def row_y(j): return C["frontY"] if j == -1 else C["spurY"] if j == -2 else ROW_Y[j]
def level_z(l): return C["level0Z"] + l * C["levelH"]
def GW(): return C["nx"] * C["cellX"]
def top_z(): return level_z(C["levels"] - 1) + C["toteH"] + 0.15
def mast_h(): return top_z() + 0.25
def aisle_y(a): return ROW_Y[3 * a + 1]
def tunnel_row(a, s): return 3 * a + (2 if s > 0 else 0)
def reach(): return C["aisleW"] / 2 + C["rackD"] / 2

# ---------------------------------------------------------------- 유틸
def link_coll(ob, coll):
    (coll or bpy.context.scene.collection).objects.link(ob)

def bevel(ob, w=0.004, seg=2):
    bm = bmesh.new(); bm.from_mesh(ob.data)
    edges = [e for e in bm.edges if e.calc_face_angle(0) > 0.5]
    if edges:
        bmesh.ops.bevel(bm, geom=edges, offset=w, segments=seg, affect="EDGES", profile=0.5, clamp_overlap=True)
    bm.to_mesh(ob.data); bm.free()
    return ob

def new_coll(name, parent=None):
    c = bpy.data.collections.new(name)
    (parent or bpy.context.scene.collection).children.link(c)
    return c

class Parts:
    """많은 상자를 bmesh 하나로 직접 모음 (오브젝트를 만들지 않아 빠름). 재질별 인덱스."""
    def __init__(self):
        self.bm = bmesh.new(); self.mats = []
    def _mi(self, m):
        if m not in self.mats: self.mats.append(m)
        return self.mats.index(m)
    def box(self, size, loc, m):
        mi = self._mi(m)
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        for v in r["verts"]:
            v.co = Vector((v.co.x * size[0] + loc[0], v.co.y * size[1] + loc[1], v.co.z * size[2] + loc[2]))
        for f in {f for v in r["verts"] for f in v.link_faces}: f.material_index = mi
    def boxz(self, size, x, y, z0, z1, m):
        """z0..z1 범위 상자"""
        self.box((size[0], size[1], z1 - z0), (x, y, (z0 + z1) / 2), m)
    def cyl(self, r, depth, loc, m, axis="Z", seg=16):
        mi = self._mi(m)
        out = bmesh.ops.create_cone(self.bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=depth)
        for v in out["verts"]:
            x, y, z = v.co
            if axis == "X": x, y, z = z, y, x
            elif axis == "Y": x, y, z = x, z, y
            v.co = Vector((x + loc[0], y + loc[1], z + loc[2]))
        for f in {f for v in out["verts"] for f in v.link_faces}: f.material_index = mi; f.smooth = True
    def build(self, name, coll, bev=0.0):
        me = bpy.data.meshes.new(name); self.bm.to_mesh(me); self.bm.free()
        for m in self.mats: me.materials.append(m)
        ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
        if bev: bevel(ob, bev, 1)
        return ob

# ---------------------------------------------------------------- 재질 (절차적 PBR)
def setin(node, name, value):
    if name in node.inputs:
        node.inputs[name].default_value = value

def principled(name, base, metal=0.0, rough=0.5, **kw):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    b = nt.nodes.get("Principled BSDF")
    setin(b, "Base Color", (*base, 1)); setin(b, "Metallic", metal); setin(b, "Roughness", rough)
    for k, v in kw.items():
        setin(b, k.replace("_", " "), v)
    m.diffuse_color = (*base, 1)
    return m, nt, b

def add_noise_bump(nt, b, scale=200.0, strength=0.05, detail=6.0, rough_var=None, coord="Object"):
    tc = nt.nodes.new("ShaderNodeTexCoord")
    nz = nt.nodes.new("ShaderNodeTexNoise"); setin(nz, "Scale", scale); setin(nz, "Detail", detail)
    nt.links.new(tc.outputs[coord], nz.inputs["Vector"])
    bp = nt.nodes.new("ShaderNodeBump"); setin(bp, "Strength", strength)
    nt.links.new(nz.outputs["Fac"], bp.inputs["Height"])
    nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
    if rough_var:
        mr = nt.nodes.new("ShaderNodeMapRange")
        setin(mr, "To Min", rough_var[0]); setin(mr, "To Max", rough_var[1])
        nt.links.new(nz.outputs["Fac"], mr.inputs["Value"])
        nt.links.new(mr.outputs["Result"], b.inputs["Roughness"])
    return nz, bp, tc

def build_materials():
    M = {}
    # 랙: 파란 도장 기둥 + 주황 보 (중국 경량 랙에서 흔한 배색, 가정) + 아연도금 토트 받침
    m, nt, b = principled("RackBlue", (0.04, 0.16, 0.42), metal=0.2, rough=0.38, Coat_Weight=0.25)
    add_noise_bump(nt, b, scale=60, strength=0.01); M["upright"] = m
    m, nt, b = principled("BeamOrange", (0.85, 0.30, 0.03), metal=0.1, rough=0.4, Coat_Weight=0.25)
    add_noise_bump(nt, b, scale=60, strength=0.01); M["beam"] = m
    m, nt, b = principled("Galvanized", (0.62, 0.64, 0.66), metal=1.0, rough=0.38)
    add_noise_bump(nt, b, scale=35, strength=0.02, rough_var=(0.28, 0.5)); M["galv"] = m
    M["rail"] = principled("RailSteel", (0.55, 0.57, 0.59), metal=1.0, rough=0.32)[0]
    # 토트: 회청색 PP (미세 요철)
    m, nt, b = principled("TotePP", (0.20, 0.27, 0.36), rough=0.48)
    add_noise_bump(nt, b, scale=400, strength=0.03, rough_var=(0.4, 0.55)); M["tote"] = m
    M["toteDark"] = principled("ToteRim", (0.10, 0.12, 0.15), rough=0.5)[0]
    M["label"] = principled("ToteLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["barcode"] = principled("Barcode", (0.02, 0.02, 0.02), rough=0.6)[0]
    # AirRob: 알루미늄 압출 마스트 + 흰 덮개 + 파란 띠 (색은 가정)
    m, nt, b = principled("AirWhite", (0.88, 0.89, 0.90), rough=0.3, Coat_Weight=0.5, Coat_Roughness=0.12)
    add_noise_bump(nt, b, scale=200, strength=0.01); M["airWhite"] = m
    M["extrusion"] = principled("Extrusion", (0.72, 0.73, 0.75), metal=1.0, rough=0.35)[0]
    M["groove"] = principled("ExtrusionGroove", (0.10, 0.10, 0.11), metal=0.6, rough=0.5)[0]
    M["accent"] = principled("AccentBlue", (0.02, 0.28, 0.70), rough=0.3, Coat_Weight=0.4)[0]
    M["chassis"] = principled("ChassisGrey", (0.10, 0.105, 0.11), metal=0.3, rough=0.45)[0]
    M["botGrey"] = principled("BotGrey", (0.32, 0.34, 0.36), metal=0.1, rough=0.4, Coat_Weight=0.3)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["poly"] = principled("PolyWheel", (0.85, 0.55, 0.10), rough=0.5)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["led"] = principled("LEDCyan", (0.2, 0.8, 1.0), rough=0.3, Emission_Color=(0.2, 0.8, 1.0, 1), Emission_Strength=8.0)[0]
    M["ledAmber"] = principled("LEDAmber", (1.0, 0.55, 0.05), rough=0.3, Emission_Color=(1.0, 0.55, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    # 스테이션·건물
    M["alu"] = principled("Aluminium", (0.78, 0.79, 0.80), metal=1.0, rough=0.28)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["white"] = principled("WhitePanel", (0.88, 0.89, 0.90), rough=0.35, Coat_Weight=0.2)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.35, 0.6, 1.0, 1), Emission_Strength=2.5)[0]
    M["mat"] = principled("FloorMat", (0.06, 0.06, 0.065), rough=0.9)[0]
    M["mesh"] = principled("FenceMesh", (0.12, 0.12, 0.13), metal=0.5, rough=0.5)[0]
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardWhite", (0.78, 0.74, 0.68)), ("ProdBlue", (0.08, 0.25, 0.62)),
                    ("ProdRed", (0.70, 0.08, 0.06)), ("ProdGreen", (0.12, 0.45, 0.22)), ("ProdYellow", (0.90, 0.72, 0.10))):
        m, nt, b = principled(nm, col, rough=0.6)
        add_noise_bump(nt, b, scale=90, strength=0.05); M[nm] = m
    m, nt, b = principled("EpoxyFloor", (0.58, 0.60, 0.60), rough=0.22, Coat_Weight=0.5, Coat_Roughness=0.08)
    nz, bp, tc = add_noise_bump(nt, b, scale=3.0, strength=0.01, detail=8.0, rough_var=(0.12, 0.3))
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.52, 0.55, 0.55, 1); ramp.color_ramp.elements[1].color = (0.64, 0.66, 0.66, 1)
    nt.links.new(nz.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["floor"] = m
    M["wall"] = principled("WallWhite", (0.80, 0.81, 0.82), rough=0.6)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["steel"] = principled("SteelBeam", (0.70, 0.71, 0.72), metal=0.6, rough=0.4)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=10.0)[0]
    M["greenline"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.45)[0]
    return M

# ---------------------------------------------------------------- 에셋: AirRob
def make_airrob(M, coll, name="AirRob"):
    """AirRob 근사: 바닥 레일 위 구동 대차 + 알루미늄 쌍기둥 마스트 + 위 레일 가이드 헤드 (몸체 = 원점 = 바닥, 통로 중심).
    자식 'Carriage' (z 로 승강, 원점 = 토트 바닥 높이) → 'HookMid' (y 로 절반) → 'Hook' (y 로 끝까지, 양방향 훅).
    폭 764 (x) x 깊이 820 (y) mm 는 공개 수치, 나머지 형상은 가정."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    W, D, Hm = AIR_W, AIR_D, mast_h()
    cx = 0.30                                   # 기둥 중심 x
    P = Parts()
    # 구동 대차 (바닥 레일을 감싸는 바퀴 2쌍) + 제어함 (LBAirBox)
    P.boxz((W - 0.02, 0.34, 0), 0, 0, 0.055, 0.21, M["chassis"])
    P.boxz((W - 0.06, 0.30, 0), 0, 0, 0.21, 0.235, M["accent"])
    P.boxz((0.30, 0.20, 0), 0.0, -0.27, 0.07, 0.31, M["airWhite"])           # 제어함 (캐패시터·제어 보드)
    P.boxz((0.10, 0.004, 0), -0.06, -0.372, 0.24, 0.27, M["black"])          # 상태 창
    P.boxz((0.05, 0.004, 0), 0.08, -0.372, 0.25, 0.262, M["ledGreen"])
    P.boxz((0.30, 0.20, 0), 0.0, 0.27, 0.07, 0.27, M["chassis"])             # 반대편 배터리(슈퍼캐패시터) 함
    for sx in (-1, 1):
        P.box((0.004, 0.16, 0.05), (sx * (W / 2 - 0.006), 0, 0.15), M["lens"])   # 앞뒤 안전 센서
        P.box((0.004, 0.24, 0.012), (sx * (W / 2 - 0.006), 0, 0.19), M["ledAmber"])
    # 마스트: 기둥 2개 (압출 알루미늄 + 홈 띠) + 위 가이드 헤드
    for sx in (-1, 1):
        P.boxz((0.08, 0.10, 0), sx * cx, 0, 0.235, Hm - 0.10, M["extrusion"])
        for sy in (-1, 1):
            P.boxz((0.012, 0.004, 0), sx * cx, sy * 0.0535, 0.30, Hm - 0.16, M["groove"])   # 기둥 앞뒤 면에서 3.5 mm 띄움
        # 대각 보강 (아래)
        P.box((0.04, 0.04, 0.40), (sx * (cx - 0.13), 0.06, 0.42), M["extrusion"])
    P.boxz((W - 0.04, 0.22, 0), 0, 0, Hm - 0.10, Hm - 0.02, M["airWhite"])            # 위 헤드
    P.boxz((W - 0.10, 0.004, 0), 0, -0.113, Hm - 0.075, Hm - 0.05, M["accent"])
    P.boxz((W - 0.10, 0.004, 0), 0, 0.113, Hm - 0.075, Hm - 0.05, M["accent"])
    body = P.build(name + "_Body", coll, bev=0.004); body.parent = root
    # 바퀴: 바닥 레일 위 주행 바퀴 + 레일 옆 가이드 롤러, 위 레일 가이드 롤러
    G = Parts()
    for sx in (-1, 1):
        x = sx * 0.27
        G.cyl(0.05, 0.05, (x, 0, 0.09), M["poly"], axis="Y", seg=24)
        G.cyl(0.022, 0.054, (x, 0, 0.09), M["hub"], axis="Y", seg=12)
        for sy in (-1, 1):
            G.cyl(0.018, 0.04, (x, sy * 0.045, 0.035), M["rubber"], axis="Z", seg=12)       # 바닥 레일 옆
            G.cyl(0.02, 0.04, (x, sy * 0.045, Hm + 0.02), M["rubber"], axis="Z", seg=12)    # 위 레일 옆
    wheels = G.build(name + "_Wheels", coll); wheels.parent = root
    # 캐리지 (원점 = 토트 바닥). 기둥을 감싸는 베어링 블록 + 토트 받침 레일 + 위 훅 구동부
    car = bpy.data.objects.new("Carriage", None); link_coll(car, coll); car.parent = root
    car.location = (0, 0, C["deckZ"])
    Q = Parts()
    for sx in (-1, 1):
        Q.boxz((0.14, 0.16, 0), sx * cx, 0, -0.07, 0.37, M["airWhite"])                    # 베어링 블록 (기둥보다 굵게)
        Q.boxz((0.004, 0.10, 0), sx * (cx + 0.072), 0, 0.20, 0.23, M["led"])              # 상태 LED (블록 면에서 2 mm 밖)
        Q.boxz((0.025, 0.66, 0), sx * 0.17, 0, -0.032, -0.004, M["rail"])                 # 토트 받침 레일 (토트 바닥보다 4 mm 아래)
    Q.boxz((0.50, 0.70, 0), 0, 0, -0.07, -0.034, M["chassis"])                            # 받침판
    Q.boxz((0.50, 0.30, 0), 0, 0, 0.345, 0.385, M["airWhite"])                            # 위 훅 구동부 (토트 위)
    Q.boxz((0.46, 0.004, 0), 0, -0.153, 0.355, 0.37, M["accent"])
    Q.boxz((0.46, 0.004, 0), 0, 0.153, 0.355, 0.37, M["accent"])
    Q.boxz((0.10, 0.20, 0), 0, 0, 0.385, 0.42, M["chassis"])                              # 모터
    cb = Q.build("Carriage_Body", coll, bev=0.003); cb.parent = car
    # 훅: 가운데 중간 단 (절반 이동) + 끝 단 (끝까지 이동, 양 끝에 손잡이에 거는 손가락)
    mid = bpy.data.objects.new("HookMid", None); link_coll(mid, coll); mid.parent = car
    Hq = Parts()
    Hq.boxz((0.10, 0.50, 0), 0, 0, 0.322, 0.340, M["extrusion"])
    hm = Hq.build("HookMid_Body", coll); hm.parent = mid
    hook = bpy.data.objects.new("Hook", None); link_coll(hook, coll); hook.parent = car
    Hk = Parts()
    Hk.boxz((0.07, 0.56, 0), 0, 0, 0.305, 0.320, M["groove"])
    for sy in (-1, 1):
        Hk.boxz((0.09, 0.016, 0), 0, sy * (C["toteD"] / 2 + 0.012), 0.235, 0.320, M["accent"])   # 손가락 (토트 앞뒤 손잡이)
    hk = Hk.build("Hook_Body", coll); hk.parent = hook
    return root

# ---------------------------------------------------------------- 에셋: 바닥 이송 로봇
def make_bot(M, coll, name="FloorBot"):
    """랙 아래 터널을 달리는 낮은 이송 로봇 (가정 치수 0.70 x 0.46 x 0.33 m). 원점 = 바닥 중심, 길이 = +X."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, dz = BOT_L, BOT_W, C["deckZ"]
    P = Parts()
    P.boxz((L, W, 0), 0, 0, 0.035, dz - 0.05, M["botGrey"])                     # 몸체
    P.boxz((L - 0.06, W - 0.06, 0), 0, 0, dz - 0.05, dz - 0.012, M["chassis"])   # 상판 받침
    for k in range(5):                                                          # 상판 롤러 (토트 바닥보다 2 mm 아래)
        P.cyl(0.018, W - 0.10, (-0.24 + k * 0.12, 0, dz - 0.020), M["hub"], axis="Y", seg=12)
    for sx in (-1, 1):
        P.boxz((0.03, W + 0.01, 0), sx * (L / 2 + 0.008), 0, 0.05, 0.16, M["black"])   # 범퍼
        P.boxz((0.004, W - 0.08, 0), sx * (L / 2 + 0.025), 0, 0.10, 0.115, M["led"])  # 범퍼 LED 띠 (범퍼 면에서 2 mm 밖)
        P.boxz((0.004, 0.12, 0), sx * (L / 2 + 0.025), 0, 0.125, 0.15, M["lens"])     # 라이다 창
    for sy in (-1, 1):
        P.boxz((L - 0.16, 0.004, 0), 0, sy * (W / 2 + 0.002), 0.20, 0.23, M["accent"])
    body = P.build(name + "_Body", coll, bev=0.006); body.parent = root
    G = Parts()
    for sx in (-1, 1):
        for sy in (-1, 1):
            G.cyl(0.045, 0.04, (sx * (L / 2 - 0.12), sy * (W / 2 - 0.05), 0.045), M["rubber"], axis="Y", seg=20)
            G.cyl(0.022, 0.042, (sx * (L / 2 - 0.12), sy * (W / 2 - 0.05), 0.045), M["hub"], axis="Y", seg=12)
    wh = G.build(name + "_Wheels", coll); wh.parent = root
    return root

# ---------------------------------------------------------------- 에셋: 토트 (원점 = 바닥 중심. 긴 변 530 mm 가 y)
def tote_shell(name, M, coll, H):
    W, D = C["toteW"], C["toteD"]
    w0, d0, w1, d1, t = W - 0.02, D - 0.02, W, D, 0.005
    bm = bmesh.new()
    def rect(w, d, z): return [bm.verts.new((sx * w / 2, sy * d / 2, z)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    ob_, ot_ = rect(w0, d0, 0.0), rect(w1, d1, H)
    ib_, it_ = rect(w0 - 2 * t, d0 - 2 * t, t), rect(w1 - 2 * t, d1 - 2 * t, H)
    bm.faces.new(list(reversed(ob_)))
    for k in range(4):
        n = (k + 1) % 4
        bm.faces.new((ob_[k], ob_[n], ot_[n], ot_[k]))
        bm.faces.new((ot_[k], ot_[n], it_[n], it_[k]))
        bm.faces.new((ib_[n], ib_[k], it_[k], it_[n]))
    bm.faces.new(ib_)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(M["tote"])
    ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
    P = Parts()
    for sy in (-1, 1):                                       # 앞뒤 (y) 면: 손잡이 홈 (훅이 거는 곳) + 라벨
        P.box((w1 + 0.014, 0.010, 0.02), (0, sy * (d1 / 2 + 0.004), H - 0.01), M["toteDark"])
        P.box((0.16, 0.004, 0.035), (0, sy * (d1 / 2 + 0.003), H - 0.06), M["toteDark"])
        P.box((0.10, 0.002, 0.050), (-0.10, sy * (d1 / 2 + 0.004), H - 0.13), M["label"])
        P.box((0.07, 0.001, 0.016), (-0.10, sy * (d1 / 2 + 0.0055), H - 0.14), M["barcode"])
    for sx in (-1, 1):                                       # 옆 (x) 면: 테두리 + 세로 리브 (토트 사이 10 mm 안쪽)
        P.box((0.010, d1 + 0.014, 0.02), (sx * (w1 / 2 + 0.003), 0, H - 0.01), M["toteDark"])
        for k in (-1, 0, 1):
            P.box((0.004, 0.010, H * 0.7), (sx * (w0 / 2 + 0.004), k * 0.14, H * 0.48), M["tote"])
    rim = P.build(name + "_rim", coll)
    return [ob, rim]

def join(objs, name):
    bm = bmesh.new(); mats = []
    for o in objs:
        me = o.data.copy(); me.transform(o.matrix_basis)
        idx = {}
        for i, m in enumerate(me.materials):
            if m not in mats: mats.append(m)
            idx[i] = mats.index(m)
        for p in me.polygons: p.material_index = idx.get(p.material_index, 0)
        bm.from_mesh(me); bpy.data.meshes.remove(me)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for m in mats: me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    colls = objs[0].users_collection
    (colls[0] if colls else bpy.context.scene.collection).objects.link(ob)
    for o in objs:
        d = o.data; bpy.data.objects.remove(o, do_unlink=True)
        if d.users == 0: bpy.data.meshes.remove(d)
    return ob

def make_tote_meshes(M, coll):
    """토트 2종: 0 = 빈 통, 1 = 상품이 든 통"""
    H = C["toteH"]; rnd = random.Random(11)
    out = []
    for vi, fl in enumerate([[], ["Cardboard", "ProdBlue", "CardboardWhite"]]):
        parts = tote_shell(f"Tote_v{vi}", M, coll, H)
        y = -C["toteD"] / 2 + 0.03
        for nm in fl:
            sy = rnd.uniform(0.11, 0.16); sx = rnd.uniform(0.2, C["toteW"] - 0.07); sz = rnd.uniform(0.08, H * 0.75)
            if y + sy > C["toteD"] / 2 - 0.03: break
            P = Parts(); P.box((sx, sy, sz), (rnd.uniform(-0.02, 0.02), y + sy / 2, 0.006 + sz / 2), M[nm])
            parts.append(P.build("item", coll, bev=0.004)); y += sy + 0.01
        out.append(join(parts, f"AirRobTote_v{vi}"))
    return out

# ---------------------------------------------------------------- 랙 (통로 양쪽 단일 깊이 랙, 맨 아래는 바닥 로봇 터널)
def make_racks(M, coll):
    nx, cX, nl = C["nx"], C["cellX"], C["levels"]
    zt = top_z(); rd = C["rackD"]
    U = Parts(); B = Parts(); R = Parts()
    for a in range(C["aisles"]):
        for s in (-1, 1):
            yc = row_y(tunnel_row(a, s))
            ya = yc - s * rd / 2                 # 통로 쪽 면
            yb = yc + s * rd / 2                 # 뒤쪽 면
            for yf, sg in ((ya, s), (yb, -s)):
                yu = yf + sg * UPR_Y / 2         # 기둥 중심 (면 안쪽)
                for k in range(nx + 1):
                    x = k * cX
                    U.boxz((UPR_X, UPR_Y, 0), x, yu, 0.0, zt, M["upright"])
                    U.boxz((0.13, 0.10, 0), x, yu, 0.0, 0.008, M["galv"])            # 베이스판 (기둥보다 넓게)
                    if k < nx:
                        for l in range(nl):                                           # 단마다 보 (기둥보다 얇게: 면이 겹치지 않음)
                            z = level_z(l)
                            B.boxz((cX - UPR_X, UPR_Y - 0.012, 0), x + cX / 2, yu, z - 0.058, z - 0.018, M["beam"])
                        B.boxz((cX - UPR_X, UPR_Y - 0.012, 0), x + cX / 2, yu, zt - 0.06, zt - 0.01, M["beam"])
            # 토트 받침 레일 (y 방향, 토트 열마다 2개). 윗면 = 단 높이 - 3 mm (토트 바닥과 떨어뜨림)
            y0, y1 = ya + s * UPR_Y, yb - s * UPR_Y
            for k in range(nx):
                for col in (-1, 1):
                    xc = wx(k) + col * C["colOff"]
                    for l in range(nl):
                        z = level_z(l)
                        for e in (-1, 1):
                            R.box((0.024, abs(y1 - y0) - 0.004, 0.026), (xc + e * 0.175, (y0 + y1) / 2, z - 0.016), M["galv"])
    # 등지는 랙 사이 가로 연결대 (높이 1.5 m 마다)
    for a in range(C["aisles"] - 1):
        y = (row_y(3 * a + 2) + row_y(3 * a + 3)) / 2
        for k in range(0, nx + 1, 2):
            for z in (1.5, 3.0, 4.5):
                U.box((0.03, C["flue"] + 2 * UPR_Y + 0.02, 0.03), (k * cX + 0.05, y, z), M["galv"])
    U.build("RackUprights", coll)
    B.build("RackBeams", coll)
    R.build("RackRails", coll)

# ---------------------------------------------------------------- AirRob 레일 (바닥 레일 + 위 레일과 받침)
def make_air_rails(M, coll):
    P = Parts()
    x0, x1 = -0.15, GW() + 0.15
    Hm = mast_h(); zt = top_z(); rd = C["rackD"]
    for a in range(C["aisles"]):
        ya = aisle_y(a)
        P.boxz((x1 - x0, 0.14, 0), (x0 + x1) / 2, ya, 0.0, 0.006, M["galv"])          # 바닥 고정판
        P.boxz((x1 - x0, 0.05, 0), (x0 + x1) / 2, ya, 0.006, 0.044, M["rail"])        # 바닥 레일
        P.boxz((x1 - x0, 0.05, 0), (x0 + x1) / 2, ya, Hm + 0.004, Hm + 0.05, M["rail"])   # 위 레일
        # 위 레일 받침: 양쪽 랙 맨 위에서 통로를 가로지르는 보 (1.8 m 마다)
        for k in range(0, C["nx"] + 1, 2):
            x = k * C["cellX"] + 0.06
            P.boxz((0.05, C["aisleW"] + 2 * UPR_Y + 0.04, 0), x, ya, Hm + 0.05, Hm + 0.09, M["upright"])
            for s in (-1, 1):
                P.boxz((0.05, 0.05, 0), x, ya + s * (C["aisleW"] / 2 + UPR_Y / 2 + 0.005), zt + 0.002, Hm + 0.05, M["upright"])
    P.build("AirRobRails", coll)

# ---------------------------------------------------------------- 바닥 표시 + 안전 펜스
def make_floor_marks(M, coll):
    P = Parts()
    x0, x1 = -1.0, GW() + 1.0
    zt = 0.006                                      # 바닥 위 6 mm 두께 (멀리서도 바닥과 깜빡이지 않게)
    for y in (C["frontY"] - 0.40, -2.25):
        P.boxz((x1 - x0, 0.06, 0), (x0 + x1) / 2, y, 0.0, zt, M["yellow"])
    for x in (-1.0, GW() + 1.0):
        P.boxz((0.06, RACK_DEPTH + 1.2, 0), x, (RACK_DEPTH - 1.2) / 2, 0.0, zt, M["yellow"])
    for k in range(1, C["nx"], 3):                  # 앞쪽 주행로 방향 표시 (-x)
        x = wx(k)
        P.boxz((0.30, 0.05, 0), x, C["frontY"], 0.0, zt, M["white"])
        P.boxz((0.10, 0.12, 0), x - 0.17, C["frontY"], 0.0, zt, M["white"])
    for a in range(C["aisles"]):                    # 터널 입구 표시
        for s in (-1, 1):
            y = row_y(tunnel_row(a, s))
            P.boxz((0.25, 0.05, 0), -0.45, y, 0.0, zt, M["white"])
    def fence(xa, ya, xb, yb):
        L = math.hypot(xb - xa, yb - ya); n = max(1, int(L / 1.5))
        for i in range(n + 1):
            px, py = xa + (xb - xa) * i / n, ya + (yb - ya) * i / n
            P.box((0.06, 0.06, 2.4), (px, py, 1.2), M["yellow"])
        alongx = abs(xb - xa) > abs(yb - ya)
        cx, cy = (xa + xb) / 2, (ya + yb) / 2
        for z in (0.15, 2.35):
            P.box((L if alongx else 0.03, 0.03 if alongx else L, 0.03), (cx, cy, z), M["yellow"])
        P.box((L if alongx else 0.008, 0.008 if alongx else L, 2.15), (cx, cy, 1.25), M["mesh"])
    gy1 = DEPTH + 0.6
    fence(-1.8, 0.2, -1.8, gy1); fence(GW() + 1.8, 0.2, GW() + 1.8, gy1); fence(-1.8, gy1, GW() + 1.8, gy1)
    P.build("FloorMarks", coll)

# ---------------------------------------------------------------- 작업 스테이션 (y = -2 줄, 피킹 칸 = 시작 x)
def make_stations(M, coll):
    lights = []
    P = Parts()
    for si, x0 in enumerate(C["stations"]):
        xc = wx(x0); yc = C["spurY"]; yo = yc - 0.50
        hh = 2.2
        for sx in (-1, 1):
            P.box((0.08, 0.9, hh), (xc + sx * 0.46, yc, hh / 2), M["white"])
        P.box((1.00, 0.9, 0.10), (xc, yc, hh), M["white"])
        P.box((1.00, 0.04, 0.6), (xc, yc + 0.43, hh - 0.30), M["white"])
        P.box((1.2, 0.36, 0.04), (xc, yo - 0.10, 0.80), M["white"])                         # 카운터
        P.box((1.2, 0.03, 0.40), (xc, yo + 0.07, 0.60), M["white"])                         # 카운터 앞판 (로봇 위로 열림)
        P.box((0.20, 0.004, 0.035), (xc + 0.38, yo - 0.285, 0.822), M["ledGreen"])
        P.box((0.50, 0.03, 0.32), (xc, yo + 0.05, 1.50), M["black"])
        P.box((0.46, 0.004, 0.28), (xc, yo + 0.032, 1.50), M["screen"])
        P.box((0.04, 0.04, 0.52), (xc, yo + 0.08, 1.08), M["alu"])
        P.box((0.9, 0.12, 0.04), (xc, yo + 0.1, hh - 0.08), M["lightpanel"])
        P.boxz((1.6, 1.1, 0), xc, yo - 0.80, 0.0, 0.012, M["mat"])
        ys = yo - 1.65
        for lv, zz in enumerate((0.35, 0.85, 1.35)):
            P.box((1.6, 0.5, 0.025), (xc, ys, zz), M["alu"])
            for k in range(3):
                x = xc + (k - 1) * 0.5
                P.box((0.40, 0.32, 0.22), (x, ys, zz + 0.125), M["Cardboard"] if (k + lv) % 3 else M["CardboardWhite"])
                P.box((0.05, 0.004, 0.02), (x, ys + 0.255, zz + 0.012), M["ledGreen" if (k + lv + si) % 4 == 0 else "black"])
        for xx in (xc - 0.8, xc + 0.8):
            for yy in (ys - 0.24, ys + 0.24):
                P.box((0.035, 0.035, 1.6), (xx, yy, 0.8), M["alu"])
        P.cyl(0.03, 0.06, (xc + 0.46, yc - 0.40, hh + 0.10), M["ledRed"], seg=16)
        P.cyl(0.03, 0.06, (xc + 0.46, yc - 0.40, hh + 0.16), M["ledGreen"], seg=16)
        for k in range(1, C["spurLen"]):
            P.boxz((0.5, 0.04, 0), wx(x0 + k), yc - 0.32, 0.0, 0.006, M["white"])
        ld = bpy.data.lights.new(f"StationSpot_{si}", "SPOT"); ld.energy = 0.0; ld.spot_size = math.radians(40)
        ld.spot_blend = 0.6; ld.color = (1.0, 0.97, 0.9); ld.shadow_soft_size = 0.05
        lo = bpy.data.objects.new(f"StationSpot_{si}", ld); lo.location = (xc, yc - 0.1, hh - 0.08); link_coll(lo, coll)
        lights.append(lo)
    P.build("Stations", coll)
    return lights

# ---------------------------------------------------------------- 건물
def make_building(M, coll):
    cxw, cyw = GW() / 2, DEPTH / 2
    S = 50.0; Hh = 10.0
    P = Parts()
    P.box((S, S, 0.02), (cxw, cyw, -0.01), M["floor"])
    P.boxz((S * 0.8, 0.12, 0), cxw, -6.0, 0.0, 0.006, M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 14, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 18, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 20, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 20, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh + 0.1), M["ceiling"])
    for k in range(-18, 20, 4):
        P.box((0.3, S, 0.6), (cxw + k, cyw, Hh - 0.3), M["steel"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 13.7, 1.2), M["greenline"])
    for k in range(-16, 20, 8):
        P.box((0.4, 0.4, Hh), (cxw + k, cyw + 13.6, Hh / 2), M["steel"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            L.box((1.8, 0.45, 0.05), (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.65), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.8; ld.size_y = 0.45
            ld.energy = 450; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.70); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity AirRobCamera.cs 와 같은 값.
# seed 3, warmup 1342 s 궤적 기준: 통로 0 의 AirRob 이 3~9 s 에 x 2.9 → 14.2 m 를 달리며 5.65 m 맨 위 단까지 오르고,
# 9.5~14 s 에 앞쪽 랙(-y)의 토트를 당겨 오고, 14~20.5 s 에 내려와 20.6~25 s 에 랙 아래 터널의 바닥 로봇 15 위로 밀어 줌.
# 로봇은 26.5 s 에 터널을 나와 28.5 s 에 앞쪽 주행로로 꺾어 스테이션으로 감.
CAM_KEYS = [
    (0.000, (20.5, -8.5, 7.0), (7.0, 4.0, 2.2), 24),       # 전경: 앞 오른쪽 높이
    (0.100, (17.2, 1.10, 3.2), (6.0, 1.085, 2.6), 50),     # 통로 0 끝 (망원): AirRob 이 달려오며 올라감 (3 s)
    (0.200, (16.9, 1.15, 4.8), (9.5, 1.0, 4.3), 45),       # 6 s
    (0.300, (16.6, 0.90, 6.5), (14.0, 0.90, 5.8), 36),     # 9 s: 맨 위 단 도착
    (0.350, (15.9, 0.05, 6.7), (14.17, 0.70, 5.80), 38),   # 10.5 s: 랙 끝 바깥에서 토트 옆면을 봄. 훅이 걸어 당김
    (0.433, (15.9, 0.30, 6.5), (14.17, 0.95, 5.80), 38),   # 13 s: 토트가 통로 쪽 캐리지로 들어옴
    (0.467, (16.2, 1.45, 5.4), (14.1, 0.95, 5.1), 36),     # 14 s: 내려가기 시작
    (0.600, (16.4, 1.30, 2.6), (14.0, 0.95, 2.0), 34),     # 18 s
    (0.680, (16.2, -0.6, 1.1), (14.0, 0.60, 0.75), 30),    # 20.4 s: 모서리를 돌아 터널 앞
    (0.750, (15.6, -2.05, 1.00), (13.95, 0.33, 0.50), 28), # 22.5 s: 랙 아래 바닥 로봇 위로 토트를 밀어 줌
    (0.833, (16.0, -2.10, 1.05), (14.0, 0.33, 0.50), 30),  # 25 s
    (0.900, (17.4, -2.6, 1.4), (14.85, -0.10, 0.45), 28),  # 27 s: 로봇이 터널을 나와 앞쪽으로
    (1.000, (16.5, -7.0, 3.6), (8.0, -0.8, 1.0), 24),      # 30 s: 스테이션 쪽 전경
]

def setup_camera(scene_coll, frames):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36; cam_d.clip_start = 0.05
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 5.6
    for frac, loc, look, lens in CAM_KEYS:
        f = 1 + round(frac * (frames - 1))
        cam.location = loc; cam.keyframe_insert("location", frame=f)
        tgt.location = look; tgt.keyframe_insert("location", frame=f)
        cam_d.lens = lens; cam_d.keyframe_insert("lens", frame=f)
    return cam

def setup_render(a, frames, fps):
    sc = bpy.context.scene
    eevee = str(a["engine"]).lower().startswith("eevee")
    if eevee:
        for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
            try: sc.render.engine = eng; break
            except Exception: continue
        try: sc.eevee.taa_render_samples = int(a["samples"])
        except Exception: pass
    else:
        sc.render.engine = "CYCLES"
        sc.cycles.samples = int(a["samples"])
        sc.cycles.use_adaptive_sampling = True
        try:
            sc.cycles.use_denoising = True
            sc.cycles.denoiser = "OPENIMAGEDENOISE"
        except Exception:
            pass
        sc.cycles.max_bounces = 8
        sc.cycles.caustics_reflective = False; sc.cycles.caustics_refractive = False
        sc.cycles.blur_glossy = 1.0
    w, h = [int(v) for v in a["res"].lower().split("x")]
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.resolution_percentage = 100
    sc.render.fps = fps
    sc.frame_start, sc.frame_end = 1, frames
    sc.render.use_motion_blur = True
    sc.render.motion_blur_shutter = 0.5
    try:
        sc.view_settings.view_transform = "AgX"
        sc.view_settings.look = "AgX - Medium High Contrast"
        sc.view_settings.exposure = -0.3
    except Exception:
        try: sc.view_settings.view_transform = "Filmic"
        except Exception: pass
    if a["gpu"] and not eevee:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for dev in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
            try:
                prefs.compute_device_type = dev
                prefs.get_devices()
                if any(d.type == dev for d in prefs.devices):
                    for d in prefs.devices: d.use = d.type == dev
                    sc.cycles.device = "GPU"; print("[AirRob] GPU:", dev); break
            except Exception:
                continue
    elif not eevee:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.78, 0.80, 0.83, 1); bg.inputs[1].default_value = 0.35

# ---------------------------------------------------------------- 애니메이션 (궤적)
def iter_fcurves(idblock):
    ad = idblock.animation_data; act = ad.action
    slot = getattr(ad, "action_slot", None)
    if slot is not None and hasattr(act, "layers"):
        for layer in act.layers:
            for strip in layer.strips:
                cb = strip.channelbag(slot) if hasattr(strip, "channelbag") else None
                if cb is not None: return list(cb.fcurves)
    return list(act.fcurves)

def bake_channel(ob, path, index, frames, values, interp="LINEAR", data=None):
    if not frames: return
    tgt = data if data is not None else ob
    prop = getattr(tgt, path)
    if index >= 0:
        prop[index] = values[0]; tgt.keyframe_insert(path, index=index, frame=frames[0])
    else:
        setattr(tgt, path, values[0]); tgt.keyframe_insert(path, frame=frames[0])
    idx = max(0, index)
    fc = next(f for f in iter_fcurves(tgt) if f.data_path == path and f.array_index == idx)
    n = len(frames)
    if n > 1:
        fc.keyframe_points.add(n - 1)
    co = []
    for f, v in zip(frames, values): co += [f, v]
    fc.keyframe_points.foreach_set("co", co)
    ii = {"LINEAR": 1, "CONSTANT": 0}[interp]
    fc.keyframe_points.foreach_set("interpolation", [ii] * n)
    fc.update()

def compress(vals, eps=1e-5):
    fs, vs = [], []
    n = len(vals)
    for i, v in enumerate(vals):
        if i == 0 or i == n - 1 or abs(v - vals[i - 1]) > eps or abs(vals[i + 1] - v) > eps:
            fs.append(i + 1); vs.append(v)
    return fs, vs

def child(root, name):
    return next(c for c in root.children_recursive if c.name.split(".")[0] == name)

def apply_trace(airs, bots, tote_meshes, spots, coll):
    tr = TRACE
    nf = tr["frames"] + 1
    R = reach()
    for ai, track in enumerate(tr["airs"]):
        root = airs[ai]
        car, mid, hook = child(root, "Carriage"), child(root, "HookMid"), child(root, "Hook")
        root.location = (track[0][0], aisle_y(ai), 0)
        fs, vs = compress([k[0] for k in track]); bake_channel(root, "location", 0, fs, vs)
        fs, vs = compress([k[1] for k in track]); bake_channel(car, "location", 2, fs, vs)
        fs, vs = compress([k[2] * R for k in track]); bake_channel(hook, "location", 1, fs, vs)
        fs, vs = compress([k[2] * R / 2 for k in track]); bake_channel(mid, "location", 1, fs, vs)
    for bi, track in enumerate(tr["bots"]):
        root = bots[bi]
        for ax in range(2):
            fs, vs = compress([k[ax] for k in track]); bake_channel(root, "location", ax, fs, vs)
        # 진행 방향으로 돌림 (y 로 달릴 때 90°)
        yaw = []; cur = 0.0
        for f in range(len(track)):
            a, b = track[max(0, f - 1)], track[min(len(track) - 1, f + 1)]
            dx, dy = b[0] - a[0], b[1] - a[1]
            if abs(dx) + abs(dy) > 1e-4: cur = 0.0 if abs(dx) >= abs(dy) else math.pi / 2
            yaw.append(cur)
        fs, vs = compress(yaw); bake_channel(root, "rotation_euler", 2, fs, vs, "CONSTANT")
    rnd = random.Random(5)
    tracks = tr["toteTracks"]
    for tid, p in enumerate(tr["initialTotes"]):
        v = 0 if rnd.random() < 0.2 else 1
        ob = bpy.data.objects.new(f"Tote_{tid}", tote_meshes[v].data); coll.objects.link(ob)
        ob.location = (p[0], p[1], p[2])
        ob.rotation_euler = (0, 0, math.pi if rnd.random() < 0.5 else 0)
        keys = tracks.get(str(tid))
        if keys:
            fs = [k[0] + 1 for k in keys]
            for ax in range(3):
                bake_channel(ob, "location", ax, fs, [k[1 + ax] for k in keys])
    for si, cur in enumerate(tr["stations"]):
        lo = spots[si]
        fs, es = [], []
        for f, s in enumerate(cur):
            if f == 0 or (s >= 0) != (cur[f - 1] >= 0):
                fs.append(f + 1); es.append(40.0 if s >= 0 else 0.0)
        bake_channel(lo, "energy", -1, fs, es, "CONSTANT", data=lo.data)
        lo.rotation_euler = (0, 0, 0)
    print(f"[AirRob] totes={len(tr['initialTotes'])} moving={len(tracks)} hookOps={len(tr['events'])}")
    return nf, tr["fps"]

# ---------------------------------------------------------------- 내보내기
def select_tree(root):
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for c in root.children_recursive: c.select_set(True)
    bpy.context.view_layer.objects.active = root

def export_fbx(root, path, types=("EMPTY", "MESH")):
    select_tree(root)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, apply_unit_scale=True,
                             apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, object_types=set(types),
                             use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False)

def export_glb(root, path):
    select_tree(root)
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_yup=True, export_apply=True)

# ---------------------------------------------------------------- 메인
TRACE = None
def main():
    global TRACE, ROW_Y, DEPTH, RACK_DEPTH
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/airrob_trace.json 이 필요합니다 (node tools/export_airrob_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    ROW_Y = TRACE["rowY"]; DEPTH = TRACE["depthY"]; RACK_DEPTH = TRACE["rackDepthY"]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_air = make_airrob(M, assets, "AirRobUnit")
    a_bot = make_bot(M, assets, "AirRobFloorBot")
    tote_meshes = make_tote_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_air, os.path.join(out, "airrob_unit.fbx"))
        export_glb(a_air, os.path.join(out, "airrob_unit.glb"))
        export_fbx(a_bot, os.path.join(out, "airrob_floorbot.fbx"))
        for vi, ob in enumerate(tote_meshes):
            export_fbx(ob, os.path.join(out, f"airrob_tote_v{vi}.fbx"), ("MESH",))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("AirRobScene")
    make_building(M, scene)
    sc_ = new_coll("Structure", scene)
    make_racks(M, sc_)
    make_air_rails(M, sc_)
    make_floor_marks(M, sc_)
    spots = make_stations(M, scene)
    rc = new_coll("Machines", scene)
    airs = [make_airrob(M, rc, f"AirRob_{i}") for i in range(len(TRACE["airs"]))]
    bots = [make_bot(M, rc, f"FloorBot_{i}") for i in range(len(TRACE["bots"]))]
    tc = new_coll("Totes", scene)
    frames, fps = apply_trace(airs, bots, tote_meshes, spots, tc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[AirRob] airs={len(airs)} bots={len(bots)} aisles={C['aisles']} levels={C['levels']} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "airrob_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"airrob_still_{f:04d}.png")
            bpy.ops.render.render(write_still=True)
    if a["anim"]:
        if a["frames"]:
            s, e = [int(v) for v in a["frames"].split("-")]; sc.frame_start, sc.frame_end = s, e
        fdir = os.path.join(out, "frames"); os.makedirs(fdir, exist_ok=True)
        sc.render.image_settings.file_format = "PNG"
        sc.render.filepath = os.path.join(fdir, "f_")
        sc.render.use_overwrite = False; sc.render.use_placeholder = True
        step = int(a["step"]); sc.frame_step = step
        bpy.ops.render.render(animation=True)
        ff = shutil.which("ffmpeg")
        if ff:
            mp4 = os.path.join(out, "airrob_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[AirRob] video ->", mp4)
        else:
            print("[AirRob] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[AirRob] done ->", out)

if __name__ == "__main__":
    main()
