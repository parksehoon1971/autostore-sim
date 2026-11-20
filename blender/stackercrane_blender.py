"""
팔레트용 스태커크레인 (단위 하중 AS/RS) — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles). 특정 제조사 제품이 아닌 일반형 모델이며 로고가 없습니다.

사용 예 (저장소 루트에서)
  node tools/export_stackercrane_trace.js --warmup 624       # blender/stackercrane_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/stackercrane_blender.py -- --out blender/out/stackercrane --trace blender/stackercrane_trace.json --still 1,400
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/stackercrane_blender.py -- --out blender/out/stackercrane --trace blender/stackercrane_trace.json --anim --gpu

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_stackercrane_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더
  --no-export        FBX/GLB 내보내기 생략

치수·속도는 공개 자료 범위 안에서 잡은 가정입니다 (docs/stackercrane-research.md).
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector, Matrix

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
C = dict(aisles=2, nx=22, levels=10, slotW=1.25, bay=2, rackD=1.10, aisleW=1.50, flue=0.30, level0Z=0.90, levelH=1.75,
         palletL=1.20, palletW=1.00, palletH=0.144, pdX=-1.80, convN=6, convL=1.25)
AISLE_Y = []; LANE_Y = []; DEPTH = 6.5; REACH = 1.3

# ---- 장면 치수 (Unity StackerCraneView.cs 와 같은 값) ----
UPR_X, UPR_Y = 0.10, 0.09          # 랙 기둥 단면
BEAM_Y, BEAM_Z = 0.05, 0.12        # 빔 단면
MAST_X0, MAST_X1, MAST_Y = 0.875, 1.425, 0.25   # 마스트 (크레인 기준 +x 쪽), 반폭
BASE_X0, BASE_X1, BASE_Y, BASE_Z0, BASE_Z1 = -0.75, 2.30, 0.38, 0.30, 0.70   # 하부 주행대
WHEEL_R, WHEEL_X = 0.18, (-0.42, 1.98)
RAIL_X0, RAIL_X1 = -3.6, 30.6       # 바닥 레일 / 상부 가이드 레일 범위
FORK_X = 0.30                       # 포크 두 줄의 x 위치 (±)

def slot_x(i): return (i + 0.5) * C["slotW"]
def level_z(l): return C["level0Z"] + l * C["levelH"]
def GW(): return C["nx"] * C["slotW"]
def top_z(): return level_z(C["levels"] - 1) + 1.75
def seg_x(k): return C["pdX"] - k * C["convL"]
def wall_x(): return C["pdX"] - (C["convN"] - 0.5) * C["convL"]
def rack_rows():
    """랙 줄 (y0, y1, 통로쪽 면 y, 통로 a, 쪽): 통로마다 -y 쪽 줄과 +y 쪽 줄"""
    out = []
    for a, ya in enumerate(AISLE_Y):
        h = C["aisleW"] / 2
        out.append((ya - h - C["rackD"], ya - h, ya - h, a, 0))
        out.append((ya + h, ya + h + C["rackD"], ya + h, a, 1))
    return out

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
    def box(self, size, loc, m, rot=None):
        mi = self._mi(m)
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        R = rot or Matrix.Identity(3)
        for v in r["verts"]:
            p = R @ Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
            v.co = Vector((p.x + loc[0], p.y + loc[1], p.z + loc[2]))
        for f in {f for v in r["verts"] for f in v.link_faces}: f.material_index = mi
    def span(self, a, b, w, h, m):
        """점 a → b 를 잇는 각재 (단면 w x h). 가새·대각선용"""
        a, b = Vector(a), Vector(b); d = b - a; L = d.length
        x = d.normalized(); up = Vector((0, 0, 1)) if abs(x.z) < 0.9 else Vector((1, 0, 0))
        y = up.cross(x).normalized(); z = x.cross(y)
        R = Matrix((x, y, z)).transposed()
        self.box((L, w, h), (a + b) / 2, m, R)
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

# ---------------------------------------------------------------- 재질 (절차적 PBR, 이름은 Unity 재질 이름과 같음)
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
    # 크레인: 노란 분체도장 마스트·주행대, 진회색 승강대, 밝은 회색 제어반
    m, nt, b = principled("CraneYellow", (0.80, 0.52, 0.03), rough=0.35, Coat_Weight=0.3, Coat_Roughness=0.2)
    add_noise_bump(nt, b, scale=180, strength=0.015, rough_var=(0.3, 0.42)); M["yellow"] = m
    M["grey"] = principled("CarriageGrey", (0.26, 0.27, 0.29), metal=0.3, rough=0.42)[0]
    M["cabinet"] = principled("CabinetGrey", (0.72, 0.73, 0.72), rough=0.4)[0]
    m, nt, b = principled("ForkSteel", (0.55, 0.57, 0.59), metal=1.0, rough=0.32)
    add_noise_bump(nt, b, scale=300, strength=0.01, rough_var=(0.25, 0.4)); M["fork"] = m
    M["railSteel"] = principled("RailSteel", (0.42, 0.43, 0.44), metal=1.0, rough=0.38)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["poly"] = principled("WheelPoly", (0.85, 0.55, 0.08), rough=0.55)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    M["ledAmber"] = principled("LEDAmber", (1.0, 0.55, 0.05), rough=0.3, Emission_Color=(1.0, 0.55, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=6.0)[0]
    M["hazard"] = principled("HazardStripe", (0.9, 0.7, 0.05), rough=0.5)[0]
    # 랙: 파란 기둥 + 주황 빔 (일반적인 팔레트 랙 도장), 아연도금 가새
    m, nt, b = principled("RackBlue", (0.03, 0.14, 0.38), rough=0.38, Coat_Weight=0.2)
    add_noise_bump(nt, b, scale=120, strength=0.01); M["upright"] = m
    M["beam"] = principled("BeamOrange", (0.82, 0.25, 0.02), rough=0.36, Coat_Weight=0.2)[0]
    m, nt, b = principled("Galvanized", (0.62, 0.64, 0.66), metal=1.0, rough=0.38)
    add_noise_bump(nt, b, scale=35, strength=0.02, rough_var=(0.28, 0.5)); M["galv"] = m
    # 팔레트·적재물
    m, nt, b = principled("PalletWood", (0.62, 0.47, 0.30), rough=0.75)
    add_noise_bump(nt, b, scale=60, strength=0.15, detail=10, rough_var=(0.65, 0.85)); M["wood"] = m
    M["woodDark"] = principled("PalletBlock", (0.45, 0.33, 0.20), rough=0.8)[0]
    for nm, col, r in (("Cardboard", (0.52, 0.36, 0.20), 0.6), ("CardboardWhite", (0.68, 0.65, 0.60), 0.55),
                       ("DrumBlue", (0.05, 0.18, 0.55), 0.35), ("BagWhite", (0.72, 0.72, 0.70), 0.5),
                       ("ProdRed", (0.70, 0.08, 0.06), 0.45), ("ProdGreen", (0.12, 0.45, 0.22), 0.45)):
        m, nt, b = principled(nm, col, rough=r)
        add_noise_bump(nt, b, scale=90, strength=0.04); M[nm] = m
    M["wrap"] = principled("StretchWrap", (0.85, 0.87, 0.88), rough=0.12, Coat_Weight=0.6, Alpha=0.35)[0]
    M["label"] = principled("Label", (0.95, 0.95, 0.93), rough=0.55)[0]
    # 컨베이어
    M["convFrame"] = principled("ConveyorGrey", (0.30, 0.32, 0.34), metal=0.4, rough=0.42)[0]
    M["chain"] = principled("ChainSteel", (0.12, 0.12, 0.13), metal=1.0, rough=0.5)[0]
    M["motor"] = principled("MotorBlue", (0.06, 0.20, 0.42), rough=0.4)[0]
    # 건물
    m, nt, b = principled("EpoxyFloor", (0.55, 0.57, 0.58), rough=0.22, Coat_Weight=0.5, Coat_Roughness=0.08)
    nz, bp, tc = add_noise_bump(nt, b, scale=3.0, strength=0.01, detail=8.0, rough_var=(0.12, 0.3))
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.36, 0.37, 0.38, 1); ramp.color_ramp.elements[1].color = (0.45, 0.46, 0.47, 1)
    nt.links.new(nz.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["floor"] = m
    M["wall"] = principled("WallWhite", (0.80, 0.81, 0.82), rough=0.6)[0]
    M["panel"] = principled("WallPanel", (0.42, 0.45, 0.48), metal=0.3, rough=0.45)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["steel"] = principled("SteelBeam", (0.70, 0.71, 0.72), metal=0.6, rough=0.4)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=10.0)[0]
    M["safetyYellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["safetyGreen"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.45)[0]
    M["white"] = principled("WhiteLine", (0.88, 0.89, 0.90), rough=0.4)[0]
    M["dark"] = principled("DarkOpening", (0.03, 0.03, 0.035), rough=0.9)[0]
    return M

# ---------------------------------------------------------------- 크레인
def make_crane(M, coll, name="StackerCrane"):
    """일반형 단일 마스트 팔레트 스태커크레인.
    원점 = 통로 중심 바닥, 승강대 가운데가 x = 0 (마스트는 +x 쪽).
    자식: Frame (주행대·마스트·상부 주행대·제어반·사다리), Carriage (z 로 이동, 원점 = 포크 윗면),
          Carriage 의 자식 ForkMid / ForkTop (y 로 이동, 3단 텔레스코픽 포크)."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    zt = top_z()
    F = Parts()
    # 하부 주행대 (상자형) + 차륜 2개 + 수평 안내 롤러 + 범퍼 + 주행 모터
    F.box((BASE_X1 - BASE_X0, 2 * BASE_Y, BASE_Z1 - BASE_Z0), ((BASE_X0 + BASE_X1) / 2, 0, (BASE_Z0 + BASE_Z1) / 2), M["yellow"])
    F.box((BASE_X1 - BASE_X0 - 0.3, 2 * BASE_Y + 0.004, 0.06), ((BASE_X0 + BASE_X1) / 2, 0, BASE_Z1 - 0.12), M["grey"])
    for wx_ in WHEEL_X:
        F.cyl(WHEEL_R, 0.09, (wx_, 0, 0.12 + WHEEL_R), M["poly"], axis="Y", seg=28)
        F.cyl(0.07, 0.10, (wx_, 0, 0.12 + WHEEL_R), M["hub"], axis="Y", seg=16)
        for sy in (-1, 1):
            F.cyl(0.04, 0.05, (wx_ + (0.28 if wx_ < 0 else -0.28), sy * 0.075, 0.10), M["poly"], seg=16)
            F.box((0.10, 0.03, 0.18), (wx_ + (0.28 if wx_ < 0 else -0.28), sy * 0.12, 0.21), M["grey"])
    for xb, s in ((BASE_X0, -1), (BASE_X1, 1)):
        F.box((0.16, 0.36, 0.22), (xb + s * 0.08, 0, 0.50), M["black"])
        F.box((0.012, 0.30, 0.10), (xb + s * 0.165, 0, 0.50), M["hazard"])
    F.box((0.36, 0.30, 0.30), (WHEEL_X[1], -BASE_Y - 0.15, 0.48), M["grey"])        # 주행 감속기
    F.cyl(0.13, 0.36, (WHEEL_X[1], -BASE_Y - 0.15, 0.82), M["grey"], seg=24)          # 주행 모터 (세로)
    F.box((0.10, 0.12, 0.12), (BASE_X0 - 0.06, 0.20, 0.82), M["black"])               # 주행 위치 레이저
    F.box((0.004, 0.06, 0.06), (BASE_X0 - 0.112, 0.20, 0.82), M["lens"])
    # 마스트 (상자형 강판) + 승강대 안내 레일 + 승강 와이어 2줄
    mz0, mz1 = BASE_Z1, zt + 0.20
    F.box((MAST_X1 - MAST_X0, 2 * MAST_Y, mz1 - mz0), ((MAST_X0 + MAST_X1) / 2, 0, (mz0 + mz1) / 2), M["yellow"])
    for sy in (-1, 1):
        F.box((0.035, 0.05, mz1 - mz0 - 0.3), (MAST_X0 - 0.012, sy * (MAST_Y - 0.06), (mz0 + mz1) / 2 + 0.1), M["railSteel"])
        F.box((0.012, 0.012, mz1 - mz0 - 0.4), (MAST_X0 - 0.07, sy * 0.10, (mz0 + mz1) / 2 + 0.1), M["chain"])
    for k in range(int((mz1 - mz0) / 2.5)):                                            # 마스트 이음 플랜지
        F.box((MAST_X1 - MAST_X0 + 0.02, 2 * MAST_Y + 0.02, 0.03), ((MAST_X0 + MAST_X1) / 2, 0, mz0 + 2.5 * (k + 1)), M["yellow"])
    # 승강 드럼·모터 (하부, +x 쪽)
    F.cyl(0.22, 0.34, (1.88, -0.14, 0.95), M["grey"], axis="Y", seg=32)
    F.box((0.36, 0.26, 0.34), (1.88, 0.18, 0.92), M["grey"])
    F.cyl(0.14, 0.30, (1.88, 0.18, 1.27), M["motor"], seg=24)
    # 제어반 (+y 쪽, 하부 주행대 위)
    F.box((0.62, 0.34, 1.65), (1.93, BASE_Y - 0.12, BASE_Z1 + 0.85 + 0.45), M["cabinet"])
    F.box((0.003, 0.24, 0.30), (1.93 - 0.312, BASE_Y - 0.12, BASE_Z1 + 1.95), M["black"])
    F.box((0.003, 0.05, 0.02), (1.93 - 0.314, BASE_Y - 0.06, BASE_Z1 + 1.70), M["ledGreen"])
    F.box((0.003, 0.05, 0.02), (1.93 - 0.314, BASE_Y - 0.18, BASE_Z1 + 1.70), M["ledAmber"])
    # 사다리 (-y 면)
    ly = -MAST_Y - 0.05
    for xr in (MAST_X0 + 0.08, MAST_X1 - 0.08):
        F.box((0.04, 0.03, mz1 - mz0 - 1.0), (xr, ly, (mz0 + mz1) / 2 + 0.3), M["galv"])
    z = mz0 + 1.0
    while z < mz1 - 0.3:
        F.box((MAST_X1 - MAST_X0 - 0.16, 0.025, 0.025), ((MAST_X0 + MAST_X1) / 2, ly, z), M["galv"]); z += 0.3
    # 상부 주행대 + 안내 롤러 (상부 가이드 레일을 양쪽에서 잡음) + 경광등
    F.box((1.30, 0.60, 0.22), (1.15, 0, zt + 0.31), M["yellow"])
    for xr in (0.65, 1.65):
        for sy in (-1, 1):
            F.cyl(0.05, 0.06, (xr, sy * 0.085, zt + 0.48), M["poly"], seg=16)
            F.box((0.12, 0.04, 0.05), (xr, sy * 0.12, zt + 0.445), M["grey"])
    F.cyl(0.05, 0.10, (1.55, -0.20, zt + 0.47), M["black"], seg=16)
    F.cyl(0.045, 0.08, (1.55, -0.20, zt + 0.56), M["ledAmber"], seg=16)
    frame = F.build("Frame", coll, bev=0.006); frame.parent = root

    # 승강대 (원점 = 포크 윗면)
    car = bpy.data.objects.new("Carriage", None); link_coll(car, coll); car.parent = root
    K = Parts()
    K.box((1.24, 1.00, 0.13), (0, 0, -0.175), M["grey"])                                  # 바닥판
    for sy in (-1, 1):                                                                       # 마스트 쪽 수직 프레임 (기둥 2개 + 가로대)
        K.box((0.18, 0.12, 1.80), (0.71, sy * 0.40, -0.24 + 0.90), M["grey"])
    for zc in (0.25, 0.85, 1.45):
        K.box((0.14, 0.70, 0.10), (0.73, 0, zc), M["grey"])
    K.box((0.06, 0.10, 1.40), (0.76, -0.18, 0.75), M["chain"])                                # 케이블 덕트
    for sy in (-1, 1):
        for zr in (-0.05, 1.35):
            K.cyl(0.05, 0.05, (MAST_X0 - 0.06, sy * (MAST_Y - 0.06), zr), M["poly"], axis="X", seg=16)
        K.box((0.05, 0.05, 1.70), (-0.60, sy * 0.47, -0.11 + 0.85), M["grey"])             # 앞쪽 기둥
        K.box((1.24, 0.05, 0.06), (0, sy * 0.47, 1.62), M["grey"])                         # 위 옆 보
        K.box((1.20, 0.006, 0.05), (0, sy * 0.503, -0.15), M["hazard"])                     # 바닥판 옆 경고띠
        K.box((0.04, 0.04, 0.06), (-0.55, sy * 0.47, -0.08), M["black"])                    # 팔레트 감지 센서
        K.box((0.004, 0.02, 0.02), (-0.572, sy * 0.47, -0.08), M["lens"])
    K.box((0.05, 0.99, 0.06), (-0.60, 0, 1.62), M["grey"])                                 # 위 앞 보 (헤드 가드)
    K.box((0.05, 0.99, 0.06), (0.60, 0, 1.62), M["grey"])
    for sx in (-1, 1):                                                                       # 포크 고정단
        K.box((0.14, 1.15, 0.035), (sx * FORK_X, 0, -0.0925), M["fork"])
        K.box((0.16, 0.30, 0.06), (sx * FORK_X, 0, -0.14), M["chain"])
    K.box((0.12, 0.10, 0.06), (0.71, 0, 1.12), M["black"])
    K.box((0.004, 0.06, 0.03), (0.618, 0, 1.12), M["ledGreen"])
    body = K.build("CarriageBody", coll, bev=0.004); body.parent = car
    fm = Parts()
    for sx in (-1, 1): fm.box((0.12, 1.10, 0.040), (sx * FORK_X, 0, -0.055), M["fork"])
    mid = fm.build("ForkMid", coll, bev=0.003); mid.parent = car
    ft = Parts()
    for sx in (-1, 1):
        ft.box((0.10, 1.15, 0.035), (sx * FORK_X, 0, -0.0175), M["fork"])
        for sy in (-1, 1): ft.box((0.10, 0.02, 0.012), (sx * FORK_X, sy * 0.58, -0.006), M["black"])
    top = ft.build("ForkTop", coll, bev=0.003); top.parent = car
    return root

# ---------------------------------------------------------------- 팔레트 (원점 = 팔레트 바닥 가운데, 긴 쪽 = y)
def make_pallets(M, coll):
    """4종: 0 = 골판지 상자 + 랩, 1 = 흰 상자, 2 = 파란 드럼 4개, 3 = 포대 + 혼합 상자.
    각 팔레트는 'Deck' 메쉬와 높이 1.0 m 기준 'Load' 자식 (z 로 늘려 적재 높이를 맞춤)."""
    out = []
    rnd = random.Random(21)
    for v in range(4):
        root = bpy.data.objects.new(f"Pallet_v{v}", None); link_coll(root, coll)
        P = Parts()
        for x in (-0.44, -0.22, 0.0, 0.22, 0.44): P.box((0.12 if abs(x) in (0.44, 0.0) else 0.10, 1.20, 0.022), (x, 0, 0.133), M["wood"])
        for y in (-0.55, 0.0, 0.55): P.box((1.00, 0.10, 0.022), (0, y, 0.111), M["wood"])
        for x in (-0.45, 0.0, 0.45):
            for y in (-0.55, 0.0, 0.55): P.box((0.10, 0.10 if y else 0.14, 0.078), (x, y, 0.061), M["woodDark"])
            P.box((0.10, 1.20, 0.022), (x, 0, 0.011), M["wood"])
        deck = P.build(f"Deck_v{v}", coll, bev=0.003); deck.parent = root
        L = Parts(); H = 1.0; z0 = 0.0
        if v in (0, 1):
            mat = M["Cardboard"] if v == 0 else M["CardboardWhite"]
            nzs = 4 if v == 0 else 5
            for k in range(nzs):
                h = H / nzs
                for ix in (-1, 1):
                    for iy in (-1, 0, 1):
                        L.box((0.48, 0.385, h - 0.006), (ix * 0.245, iy * 0.39, z0 + k * h + h / 2), mat)
                        L.box((0.10, 0.002, 0.05), (ix * 0.245 - 0.12, iy * 0.39 + (0.1935 if iy >= 0 else -0.1935), z0 + k * h + h * 0.6), M["label"]) if iy != 0 else None
            if v == 0:
                L.box((0.995, 1.185, H * 0.92), (0, 0, H * 0.92 / 2 + 0.002), M["wrap"])
        elif v == 2:
            for ix in (-1, 1):
                for iy in (-1, 1):
                    L.cyl(0.23, H * 0.94, (ix * 0.25, iy * 0.29, H * 0.47), M["DrumBlue"], seg=28)
                    L.cyl(0.235, 0.025, (ix * 0.25, iy * 0.29, H * 0.32), M["DrumBlue"], seg=28)
                    L.cyl(0.235, 0.025, (ix * 0.25, iy * 0.29, H * 0.66), M["DrumBlue"], seg=28)
                    L.cyl(0.03, 0.02, (ix * 0.25 + 0.1, iy * 0.29, H * 0.945), M["black"], seg=12)
        else:
            for k in range(3):
                for iy in (-1, 1):
                    L.box((0.96, 0.57, 0.17), (0, iy * 0.295, k * 0.18 + 0.085), M["BagWhite"])
            for ix in (-1, 1):
                for iy in (-1, 1):
                    h = rnd.uniform(0.32, 0.44)
                    L.box((0.47, 0.57, h), (ix * 0.245, iy * 0.295, 0.54 + h / 2), M["ProdRed"] if (ix + iy) == 0 else M["Cardboard"])
        load = L.build(f"Load_v{v}", coll, bev=0.006); load.parent = root; load.location = (0, 0, C["palletH"])
        out.append(root)
    return out

# ---------------------------------------------------------------- 랙 + 레일
def make_racks(M, coll):
    nx, sW, nl, zt = C["nx"], C["slotW"], C["levels"], top_z()
    bayL = C["bay"] * sW; nb = nx // C["bay"]
    for (y0, y1, yf, a, side) in rack_rows():
        P = Parts()
        sgn = 1 if side == 0 else -1          # 통로 → 랙 안쪽 방향의 반대 (yf 에서 안쪽은 -sgn)
        yback = y0 if side == 0 else y1
        for k in range(nb + 1):
            x = k * bayL
            for yy, hz in ((yf, zt + 0.9), (yback, zt)):
                yc = yy - (UPR_Y / 2 if yy == y1 else -UPR_Y / 2)
                P.box((UPR_X, UPR_Y, hz), (x, yc, hz / 2), M["upright"])
                P.box((0.16, 0.14, 0.012), (x, yc, 0.006), M["galv"])
            # 프레임 가새 (기둥 앞뒤를 잇는 수평·대각선)
            ya, yb = y0 + UPR_Y, y1 - UPR_Y
            z = 0.3; up = True
            while z < zt - 0.9:
                P.span((x, ya, z), (x, yb, z), 0.035, 0.035, M["galv"])
                z2 = min(z + 1.2, zt - 0.2)
                P.span((x, ya if up else yb, z), (x, yb if up else ya, z2), 0.03, 0.03, M["galv"])
                z = z2; up = not up
            P.span((x, ya, zt - 0.1), (x, yb, zt - 0.1), 0.035, 0.035, M["galv"])
        # 빔 (단마다 앞뒤 한 쌍). 빔 윗면 = 팔레트 바닥 - 3 mm (면이 겹쳐 깜박이지 않게)
        for k in range(nb):
            xc = (k + 0.5) * bayL
            for l in range(nl):
                zb = level_z(l) - 0.003 - BEAM_Z / 2
                for yy in (y0 + UPR_Y / 2, y1 - UPR_Y / 2):
                    P.box((bayL - UPR_X - 0.004, BEAM_Y, BEAM_Z), (xc, yy, zb), M["beam"])
        P.build(f"Rack_a{a}_s{side}", coll)
    # 통로 위 연결보 + 상부 가이드 레일, 바닥 레일
    R = Parts()
    for a, ya in enumerate(AISLE_Y):
        for k in range(nb + 1):
            x = k * bayL
            R.box((0.12, C["aisleW"] + 2 * UPR_Y, 0.15), (x, ya, zt + 0.825), M["upright"])
        R.box((RAIL_X1 - 0.0, 0.06, 0.20), ((0.0 + RAIL_X1) / 2, ya, zt + 0.65), M["railSteel"])   # 랙 구간 상부 레일
        R.box((RAIL_X1 - RAIL_X0, 0.08, 0.12), ((RAIL_X0 + RAIL_X1) / 2, ya, 0.06), M["railSteel"])  # 바닥 레일
        R.box((RAIL_X1 - RAIL_X0, 0.30, 0.012), ((RAIL_X0 + RAIL_X1) / 2, ya, 0.006), M["galv"])     # 레일 받침판
        for xe, s in ((RAIL_X0, -1), (RAIL_X1, 1)):
            R.box((0.30, 0.40, 0.55), (xe + s * 0.15, ya, 0.275), M["safetyYellow"])                 # 끝 멈춤 블록
            R.box((0.15, 0.25, 0.25), (xe - s * 0.07, ya, 0.50), M["black"])                         # 완충기
        R.box((0.0 - RAIL_X0, 0.06, 0.20), (RAIL_X0 / 2, ya, zt + 0.65), M["railSteel"])
        # 위치 레이저 반사판 (앞 끝)
        R.box((0.02, 0.30, 0.30), (RAIL_X0 - 0.35, ya + 0.20, 0.82), M["white"])
    # 앞쪽 상부 레일 지지 문형 프레임: 랙 바로 앞, 기둥은 난간 바깥 (컨베이어 팔레트·카메라 동선과 겹치지 않게)
    xg, ya0, ya1, hz = -0.45, -1.1, DEPTH + 1.1, zt + 1.0   # 난간(y=-0.8, DEPTH+0.8) 바깥
    for yp in (ya0, ya1):
        R.box((0.25, 0.25, hz), (xg, yp, hz / 2), M["upright"])
    R.box((0.24, ya1 - ya0 + 0.25, 0.22), (xg, (ya0 + ya1) / 2, zt + 0.865), M["upright"])
    R.build("Rails", coll)

# ---------------------------------------------------------------- 입출고 컨베이어 + 벽
def make_conveyors(M, coll):
    cL, n = C["convL"], C["convN"]
    P = Parts()
    zt = C["level0Z"]
    for li, ly in enumerate(LANE_Y):
        x0, x1 = seg_x(n - 1) - cL / 2 - 0.9, seg_x(0) + cL / 2
        for sy in (-1, 1):
            P.box((x1 - x0, 0.06, 0.16), ((x0 + x1) / 2, ly + sy * 0.62, zt - 0.10), M["convFrame"])
        for k in range(n):
            xc = seg_x(k)
            for yc in (-0.45, 0.0, 0.45):
                P.box((cL - 0.04, 0.05, 0.03), (xc, ly + yc, zt - 0.0175), M["chain"])
                P.cyl(0.05, 0.07, (xc - cL / 2 + 0.08, ly + yc, zt - 0.06), M["chain"], axis="Y", seg=16)
            P.box((0.25, 1.20, 0.05), (xc + cL / 2 - 0.02, ly, zt - 0.20), M["convFrame"])
            for sy in (-1, 1):
                P.box((0.06, 0.06, zt - 0.18), (xc + cL / 2 - 0.02, ly + sy * 0.58, (zt - 0.18) / 2), M["convFrame"])
                P.box((0.12, 0.10, 0.012), (xc + cL / 2 - 0.02, ly + sy * 0.58, 0.006), M["galv"])
            P.box((0.22, 0.20, 0.18), (xc - 0.2, ly - 0.72, zt - 0.14), M["motor"])          # 구동 모터
            P.cyl(0.07, 0.20, (xc - 0.2, ly - 0.72, zt - 0.32), M["motor"], seg=16)
            P.box((0.04, 0.04, 0.30), (xc + 0.5, ly + 0.68, zt + 0.15), M["black"])          # 광전 센서
            P.box((0.004, 0.02, 0.02), (xc + 0.5, ly + 0.658, zt + 0.25), M["lens"])
        # P&D 칸 끝 멈춤 + 형상 검사 게이트 (입고)
        P.box((0.10, 1.30, 0.12), (seg_x(0) + cL / 2 + 0.06, ly, zt + 0.02), M["safetyYellow"])
        if li % 2 == 0:
            xg = seg_x(3)
            for sy in (-1, 1):
                P.box((0.10, 0.10, 2.55), (xg, ly + sy * 0.80, 1.275), M["safetyYellow"])
                P.box((0.012, 0.03, 1.9), (xg + 0.06, ly + sy * 0.75, 1.55), M["ledRed"])
            P.box((0.10, 1.70, 0.10), (xg, ly, 2.60), M["safetyYellow"])
            P.box((0.16, 0.22, 0.10), (xg, ly, 2.50), M["black"])
    P.build("Conveyors", coll)
    # 방화 칸막이 블록 (컨베이어 입구, 두께 2.6 m). 팔레트는 이 블록 안 어두운 통로에서 나타나고 사라짐
    W = Parts(); xw = wall_x(); y0, y1 = -2.5, DEPTH + 2.5; H = 4.5; th = 2.6
    ys = sorted(LANE_Y); edges = [y0]
    for ly in ys: edges += [ly - 0.80, ly + 0.80]
    edges.append(y1)
    for k in range(0, len(edges), 2):
        a, b = edges[k], edges[k + 1]
        if b > a: W.box((th, b - a, H), (xw - th / 2, (a + b) / 2, H / 2), M["panel"])
    W.box((th + 0.1, y1 - y0 + 0.1, 0.12), (xw - th / 2, (y0 + y1) / 2, H + 0.06), M["steel"])
    for ly in ys:
        W.box((th, 1.60, H - 2.6), (xw - th / 2, ly, 2.6 + (H - 2.6) / 2), M["panel"])
        for sy in (-1, 1): W.box((0.05, 0.10, 2.6), (xw + 0.025, ly + sy * 0.85, 1.3), M["hazard"])
        W.box((0.05, 1.80, 0.10), (xw + 0.025, ly, 2.65), M["hazard"])
        W.box((th - 0.3, 1.60, 2.6), (xw - 0.3 - (th - 0.3) / 2, ly, 1.3), M["dark"])       # 안쪽 어두운 통로
    W.build("PartitionWall", coll)

# ---------------------------------------------------------------- 바닥 표시 + 안전 난간
def make_floor_marks(M, coll):
    P = Parts()
    xw = wall_x()
    for y in (-0.55, DEPTH + 0.55):
        P.box((-xw, 0.08, 0.003), (xw / 2, y, 0.0015), M["safetyYellow"])
    P.box((0.08, DEPTH + 1.1, 0.003), (xw - 0.3, DEPTH / 2, 0.0015), M["safetyYellow"])
    for k in range(int(-xw / 0.6)):
        if k % 2 == 0: P.box((0.30, 0.14, 0.003), (xw + 0.3 + k * 0.6, -1.4, 0.0016), M["white"])
    # 난간 (기둥 + 가로대 3줄, 노랑): P&D 구역 양옆
    for y in (-0.80, DEPTH + 0.80):
        x0, x1 = xw + 0.2, -0.2
        n = int((x1 - x0) / 1.6)
        for i in range(n + 1):
            P.box((0.06, 0.06, 1.15), (x0 + (x1 - x0) * i / n, y, 0.575), M["safetyYellow"])
        for z in (0.35, 0.75, 1.12):
            P.box((x1 - x0, 0.045, 0.045), ((x0 + x1) / 2, y, z), M["safetyYellow"])
        P.box((x1 - x0, 0.015, 0.15), ((x0 + x1) / 2, y, 0.08), M["safetyYellow"])
    P.build("FloorMarks", coll)

# ---------------------------------------------------------------- 건물 (높은 자동창고 동)
def make_building(M, coll):
    cxw, cyw = GW() / 2 - 2.0, DEPTH / 2
    SX, SY, Hh = 70.0, 44.0, 26.0
    P = Parts()
    P.box((SX, SY, 0.02), (cxw, cyw, -0.01), M["floor"])
    P.box((SX * 0.9, 0.12, 0.004), (cxw, -9.0, 0.002), M["safetyGreen"])
    P.box((SX, 0.2, Hh), (cxw, cyw + SY / 2, Hh / 2), M["panel"])
    P.box((0.2, SY, Hh), (cxw - SX / 2, cyw, Hh / 2), M["panel"])
    P.box((0.2, SY, Hh), (cxw + SX / 2, cyw, Hh / 2), M["panel"])
    P.box((SX, 0.2, Hh), (cxw, cyw - SY / 2, Hh / 2), M["panel"])
    P.box((SX, SY, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-32, 36, 6):
        P.box((0.35, SY, 0.9), (cxw + k, cyw, Hh - 0.55), M["steel"])
    for k in range(-30, 36, 10):
        for yy in (cyw - SY / 2 + 0.3, cyw + SY / 2 - 0.3):
            P.box((0.5, 0.5, Hh), (cxw + k, yy, Hh / 2), M["steel"])
    P.box((SX, 0.4, 1.4), (cxw, cyw + SY / 2 - 0.3, 0.7), M["wall"])
    P.box((SX, 0.4, 1.4), (cxw, cyw - SY / 2 + 0.3, 0.7), M["wall"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-5, 6):
        for iy in range(-3, 4):
            L.box((2.2, 0.5, 0.06), (cxw + ix * 6.0, cyw + iy * 6.0, Hh - 1.1), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-5, 6):
        for iy in range(-3, 4):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 2.2; ld.size_y = 0.5
            ld.energy = 1000; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 6.0, cyw + iy * 6.0, Hh - 1.15); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라
# 30초 동선 (프레임 비율, 크레인0 기준 여부, 위치, 바라보는 점, 렌즈 mm). Unity StackerCraneCamera.cs 와 같은 값.
# rel = 1 이면 위치·바라보는 점이 크레인 0 의 (x, 통로 중심 y, 포크 윗면 z) 에서의 상대값.
# seed 6, warmup 624 s 궤적 기준: 크레인 0 이 1.6 s 에 입고 컨베이어 팔레트를 포크로 받고, 7.5~16 s 에 21 m 주행 + 8.7 m 승강,
# 17~21 s 에 6단(9.7 m) 칸에 넣고, 23~27 s 에 다음 칸으로 이동해 출고 팔레트를 꺼내기 시작 (카메라는 통로를 거슬러 물러남).
CAM_KEYS = [
    (0.000, 0, (-14.0, -10.0, 9.5), (6.0, 4.0, 6.0), 22),      # 전경: 앞 왼쪽 높이 (랙 18 m, 통로 2개)
    (0.050, 0, (-10.5, -6.5, 5.5), (-2.0, 1.2, 1.8), 24),
    (0.120, 0, (-5.2, -1.6, 4.6), (-1.8, 0.9, 1.3), 26),       # 입고 컨베이어 위: 포크가 옆으로 나가 팔레트를 받음
    (0.200, 0, (-6.2, 1.5, 3.4), (-1.8, 1.6, 1.5), 26),        # 크레인 뒤로 돌아 통로 쪽을 봄
    (0.250, 1, (-4.6, -0.25, 1.0), (0.8, 0.1, 0.5), 24),       # 출발: 크레인 뒤를 따라감 (주행 + 승강 동시)
    (0.400, 1, (-4.2, -0.30, 1.2), (1.0, 0.1, 0.4), 22),       # 최고 속도 4 m/s, 양옆 랙이 흘러감
    (0.540, 1, (-3.6, -0.35, 0.9), (0.3, 0.4, 0.2), 24),       # 도착, 위치 보정
    (0.640, 1, (-3.0, -0.45, 0.7), (0.0, 0.9, 0.1), 26),       # 포크가 +y 랙으로 들어가 팔레트를 내려놓음
    (0.720, 1, (-3.2, -0.40, 0.9), (0.4, 0.3, 0.0), 24),
    (0.800, 1, (-3.8, -0.40, 1.6), (1.0, 0.2, 0.0), 22),       # 다음 칸으로 이동
    (0.880, 0, (10.0, 1.75, 8.6), (24.4, 1.85, 8.4), 26),      # 통로를 거슬러 물러남 (크레인은 반대로 나아감)
    (1.000, 0, (2.5, 1.85, 8.9), (24.4, 1.85, 8.3), 45),       # 통로 끝에서 길게: 크레인이 출고 팔레트를 꺼냄
]

def catmull(p0, p1, p2, p3, t):
    t2, t3 = t * t, t * t * t
    return tuple(0.5 * (2 * p1[i] + (-p0[i] + p2[i]) * t + (2 * p0[i] - 5 * p1[i] + 4 * p2[i] - p3[i]) * t2 +
                        (-p0[i] + 3 * p1[i] - 3 * p2[i] + p3[i]) * t3) for i in range(3))

def clamp3(v, a, b):
    return tuple(min(max(v[i], min(a[i], b[i])), max(a[i], b[i])) for i in range(3))

def smoothstep(x): x = min(max(x, 0.0), 1.0); return x * x * (3 - 2 * x)

def cam_eval(u, ref):
    """Unity StackerCraneCamera.Evaluate 와 같은 계산. ref = 크레인0 (x, y, z)"""
    def W(k):
        f, rel, p, l, lens = k
        if rel: return (tuple(p[i] + ref[i] for i in range(3)), tuple(l[i] + ref[i] for i in range(3)), lens)
        return (p, l, lens)
    n = len(CAM_KEYS); i = 0
    while i < n - 2 and u > CAM_KEYS[i + 1][0]: i += 1
    k0, k1, k2, k3 = [W(CAM_KEYS[j]) for j in (max(0, i - 1), i, i + 1, min(n - 1, i + 2))]
    f1, f2 = CAM_KEYS[i][0], CAM_KEYS[i + 1][0]
    s = min(max((u - f1) / (f2 - f1), 0.0), 1.0)
    if i == 0: s = smoothstep(s * 0.5) * 2.0
    if i == n - 2: s = 1.0 - smoothstep((1.0 - s) * 0.5) * 2.0
    pos = clamp3(catmull(k0[0], k1[0], k2[0], k3[0], s), k1[0], k2[0])
    look = clamp3(catmull(k0[1], k1[1], k2[1], k3[1], s), k1[1], k2[1])
    lens = k1[2] + (k2[2] - k1[2]) * smoothstep(s)
    return pos, look, lens

def setup_camera(scene_coll, frames):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 24; cam_d.sensor_width = 36; cam_d.clip_start = 0.05; cam_d.clip_end = 300
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 8.0
    tr = TRACE; ay = tr["aisleY"][0]
    P, T, Ls = [[], [], []], [[], [], []], []
    for f in range(frames):
        k = tr["cranes"][0][min(f, len(tr["cranes"][0]) - 1)]
        pos, look, lens = cam_eval(f / max(1, frames - 1), (k[0], ay, k[1]))
        for i in range(3): P[i].append(pos[i]); T[i].append(look[i])
        Ls.append(lens)
    fs = list(range(1, frames + 1))
    for i in range(3):
        bake_channel(cam, "location", i, fs, P[i]); bake_channel(tgt, "location", i, fs, T[i])
    bake_channel(cam, "lens", -1, fs, Ls, data=cam_d)
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
        sc.view_settings.exposure = -0.7
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
                    sc.cycles.device = "GPU"; print("[StackerCrane] GPU:", dev); break
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
    """이 ID 의 F-커브 (Blender 4.4+ 슬롯 액션 대응)"""
    ad = idblock.animation_data; act = ad.action
    slot = getattr(ad, "action_slot", None)
    if slot is not None and hasattr(act, "layers"):
        for layer in act.layers:
            for strip in layer.strips:
                cb = strip.channelbag(slot) if hasattr(strip, "channelbag") else None
                if cb is not None: return list(cb.fcurves)
    return list(act.fcurves)

def bake_channel(ob, path, index, frames, values, interp="LINEAR", data=None, interps=None):
    """키프레임을 한 번에 넣기. interps: 키마다 보간 (0 = CONSTANT, 1 = LINEAR)"""
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
    fc.keyframe_points.foreach_set("interpolation", interps if interps else [ii] * n)
    fc.update()

def compress(vals, eps=1e-5):
    fs, vs = [], []
    n = len(vals)
    for i, v in enumerate(vals):
        if i == 0 or i == n - 1 or abs(v - vals[i - 1]) > eps or abs(vals[i + 1] - v) > eps:
            fs.append(i + 1); vs.append(v)
    return fs, vs

def apply_trace(cranes, pallet_tpl, coll):
    tr = TRACE
    R = tr["reach"]
    for ci, track in enumerate(tr["cranes"]):
        root = cranes[ci]
        root.location = (track[0][0], tr["aisleY"][ci], 0)
        car = next(c for c in root.children if c.name.startswith("Carriage"))
        mid = next(c for c in car.children if c.name.startswith("ForkMid"))
        top = next(c for c in car.children if c.name.startswith("ForkTop"))
        fs, vs = compress([k[0] for k in track]); bake_channel(root, "location", 0, fs, vs)
        fs, vs = compress([k[1] for k in track]); bake_channel(car, "location", 2, fs, vs)
        fs, vs = compress([k[2] * R * 0.5 for k in track]); bake_channel(mid, "location", 1, fs, vs)
        fs, vs = compress([k[2] * R for k in track]); bake_channel(top, "location", 1, fs, vs)
    tracks = tr["palletTracks"]
    for pid, p in enumerate(tr["initialPallets"]):
        load, v = tr["pallets"][pid]
        tpl = pallet_tpl[v]
        deck = next(c for c in tpl.children if c.name.startswith("Deck"))
        ld = next(c for c in tpl.children if c.name.startswith("Load"))
        ob = bpy.data.objects.new(f"Pallet_{pid}", None); coll.objects.link(ob)
        d = bpy.data.objects.new(f"Pallet_{pid}_deck", deck.data); coll.objects.link(d); d.parent = ob
        l = bpy.data.objects.new(f"Pallet_{pid}_load", ld.data); coll.objects.link(l); l.parent = ob
        l.location = (0, 0, C["palletH"]); l.scale = (1, 1, load)
        ob.location = tuple(p)
        keys = tracks.get(str(pid))
        if keys:
            fs = [k[0] + 1 for k in keys]
            # 숨김(z = -5) 으로 들어가거나 나올 때는 순간 이동 (벽 뒤에서만 일어남)
            ip = [0 if (j + 1 < len(keys) and (keys[j][3] < -1) != (keys[j + 1][3] < -1)) else 1 for j in range(len(keys))]
            for ax in range(3):
                bake_channel(ob, "location", ax, fs, [k[1 + ax] for k in keys], interps=ip)
    print(f"[StackerCrane] pallets={len(tr['initialPallets'])} moving={len(tracks)} events={len(tr['events'])}")
    return tr["frames"] + 1, tr["fps"]

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
    global TRACE, AISLE_Y, LANE_Y, DEPTH, REACH
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/stackercrane_trace.json 이 필요합니다 (node tools/export_stackercrane_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    AISLE_Y = TRACE["aisleY"]; LANE_Y = TRACE["laneY"]; DEPTH = TRACE["depthY"]; REACH = TRACE["reach"]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_crane = make_crane(M, assets, "StackerCrane")
    pallets = make_pallets(M, assets)
    if not a["no-export"]:
        export_fbx(a_crane, os.path.join(out, "stackercrane_crane.fbx"))
        export_glb(a_crane, os.path.join(out, "stackercrane_crane.glb"))
        for vi, ob in enumerate(pallets):
            export_fbx(ob, os.path.join(out, f"stackercrane_pallet_v{vi}.fbx"))
        export_glb(pallets[0], os.path.join(out, "stackercrane_pallet_v0.glb"))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("StackerCraneScene")
    make_building(M, scene)
    st = new_coll("Structure", scene)
    make_racks(M, st)
    make_conveyors(M, st)
    make_floor_marks(M, st)
    cc = new_coll("Cranes", scene)
    cranes = [make_crane(M, cc, f"Crane_{i}") for i in range(len(TRACE["cranes"]))]
    pc = new_coll("Pallets", scene)
    frames, fps = apply_trace(cranes, pallets, pc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[StackerCrane] cranes={len(cranes)} aisles={C['aisles']} levels={C['levels']} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "stackercrane_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"stackercrane_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "stackercrane_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[StackerCrane] video ->", mp4)
        else:
            print("[StackerCrane] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[StackerCrane] done ->", out)

if __name__ == "__main__":
    main()
