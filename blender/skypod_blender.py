"""
Exotec Skypod 방식 "바닥 주행 + 랙 등반" 로봇 창고 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  node tools/export_skypod_trace.js --warmup 680       # blender/skypod_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/skypod_blender.py -- --out blender/out/skypod --trace blender/skypod_trace.json --still 1,520
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/skypod_blender.py -- --out blender/out/skypod --trace blender/skypod_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_skypod_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더 (GPU 없는 PC에서 빠름)
  --no-export        FBX/GLB 내보내기 생략

공개 수치: 바닥 최고 4 m/s, 최대 등반 높이 14 m, 적재 30 kg, 토트 650 x 450 mm (높이 220/320/420).
랙 칸 폭·단 간격·로봇 치수·색 등은 사진을 보고 잡은 가정입니다 (docs/skypod-research.md). Exotec 제품 도면이 아닙니다.
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
C = dict(nx=16, aisles=4, levels=20, cellX=0.72, aisleW=0.80, rackD=0.47, flue=0.04, level0Z=1.25, levelH=0.42,
         toteW=0.65, toteD=0.45, toteH=0.32, bayZ=0.40, stations=[1, 6, 11], spurLen=5, frontY=-0.80, spurY=-1.75)
ROW_Y = []            # 줄 중심 y (trace.json 의 rowY)
UPR_X, UPR_Y = 0.06, 0.09        # 랙 기둥 단면 (x 폭, 깊이)
ROBOT_L, ROBOT_W, ROBOT_H = 0.62, 0.62, 1.10

def wx(i): return (i + 0.5) * C["cellX"]
def row_y(j): return C["frontY"] if j == -1 else C["spurY"] if j == -2 else ROW_Y[j]
def level_z(l): return C["level0Z"] + l * C["levelH"]
def GW(): return C["nx"] * C["cellX"]
def GD(): return DEPTH
def top_z(): return level_z(C["levels"] - 1) + C["toteH"] + 0.18
def aisle_y(a): return ROW_Y[2 * a + 1]
def reach(): return C["aisleW"] / 2 + C["rackD"] / 2
DEPTH = 7.12

# ---------------------------------------------------------------- 유틸 (rapyuta_blender.py 와 같은 방식)
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
    # 아연도금 강재 랙 기둥: 은회색 금속 + 결정 무늬 (노이즈로 거칠기 변화)
    m, nt, b = principled("Galvanized", (0.62, 0.64, 0.66), metal=1.0, rough=0.38)
    add_noise_bump(nt, b, scale=35, strength=0.02, rough_var=(0.28, 0.5)); M["galv"] = m
    # 기둥 앞면 톱니 (로봇 등반 기어가 맞물리는 면): 가로 줄무늬 범프
    m, nt, b = principled("RackTeeth", (0.20, 0.21, 0.22), metal=1.0, rough=0.45)
    tc = nt.nodes.new("ShaderNodeTexCoord"); wv = nt.nodes.new("ShaderNodeTexWave")
    wv.wave_type = "BANDS"; wv.bands_direction = "Z"; setin(wv, "Scale", 60.0)
    nt.links.new(tc.outputs["Object"], wv.inputs["Vector"])
    bp = nt.nodes.new("ShaderNodeBump"); setin(bp, "Strength", 0.6)
    nt.links.new(wv.outputs["Fac"], bp.inputs["Height"]); nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
    M["teeth"] = m
    M["rail"] = principled("RailSteel", (0.55, 0.57, 0.59), metal=1.0, rough=0.42)[0]
    M["beam"] = principled("BeamBlue", (0.05, 0.17, 0.42), rough=0.4, Coat_Weight=0.3)[0]
    # 토트: 진회색 PP (미세 요철)
    m, nt, b = principled("TotePP", (0.18, 0.19, 0.20), rough=0.48)
    add_noise_bump(nt, b, scale=400, strength=0.03, rough_var=(0.4, 0.55)); M["tote"] = m
    M["toteDark"] = principled("ToteRim", (0.10, 0.10, 0.11), rough=0.5)[0]
    M["label"] = principled("ToteLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["barcode"] = principled("Barcode", (0.02, 0.02, 0.02), rough=0.6)[0]
    # 로봇: 흰 몸체 + 진회색 섀시 + 청록 띠
    m, nt, b = principled("RobotWhite", (0.86, 0.87, 0.88), rough=0.3, Coat_Weight=0.5, Coat_Roughness=0.12)
    add_noise_bump(nt, b, scale=200, strength=0.01); M["robot"] = m
    M["chassis"] = principled("ChassisGrey", (0.10, 0.105, 0.11), metal=0.3, rough=0.45)[0]
    M["teal"] = principled("AccentTeal", (0.0, 0.48, 0.50), rough=0.35, Coat_Weight=0.4)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["gear"] = principled("GearSteel", (0.35, 0.36, 0.37), metal=1.0, rough=0.35)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["led"] = principled("LEDCyan", (0.2, 0.8, 1.0), rough=0.3, Emission_Color=(0.2, 0.8, 1.0, 1), Emission_Strength=8.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    # 스테이션
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
    # 건물: 밝은 에폭시 바닥, 흰 벽
    m, nt, b = principled("EpoxyFloor", (0.62, 0.63, 0.64), rough=0.22, Coat_Weight=0.5, Coat_Roughness=0.08)
    nz, bp, tc = add_noise_bump(nt, b, scale=3.0, strength=0.01, detail=8.0, rough_var=(0.12, 0.3))
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.55, 0.56, 0.57, 1); ramp.color_ramp.elements[1].color = (0.66, 0.67, 0.68, 1)
    nt.links.new(nz.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["floor"] = m
    M["wall"] = principled("WallWhite", (0.80, 0.81, 0.82), rough=0.6)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["steel"] = principled("SteelBeam", (0.70, 0.71, 0.72), metal=0.6, rough=0.4)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=10.0)[0]
    M["greenline"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.45)[0]
    return M

# ---------------------------------------------------------------- 에셋: 로봇
def make_robot(M, coll, name="Robot"):
    """Skypod 로봇 근사: 흰 세로형 몸체, 가운데 토트 칸 (양옆이 열려 토트가 옆으로 드나듦),
    네 모서리 등반 기어 (랙 기둥 톱니에 맞물림), 바닥 구동 바퀴, 위 라이다.
    원점 = 바닥 중심, 길이 = +X. 자식 'Arm' 을 Y로 움직여 텔레스코픽 암을 폄."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, H = ROBOT_L, ROBOT_W, ROBOT_H
    bz0, bz1 = C["bayZ"] - 0.02, C["bayZ"] + C["toteH"] + 0.04     # 토트 칸 아래·위
    P = Parts()
    # 아래 섀시 (구동부·배터리)
    P.box((L - 0.04, W - 0.10, bz0 - 0.06), (0, 0, 0.06 + (bz0 - 0.06) / 2), M["chassis"])
    P.box((L, W - 0.06, 0.05), (0, 0, bz0 - 0.025), M["robot"])                       # 토트 칸 바닥판
    # 앞뒤 기둥 (토트 칸 양 끝, x 방향 양쪽)
    for sx in (-1, 1):
        P.box((0.07, W - 0.06, bz1 - bz0), (sx * (L / 2 - 0.035), 0, (bz0 + bz1) / 2), M["robot"])
        P.box((0.004, 0.24, 0.05), (sx * (L / 2 + 0.001), 0, 0.22), M["lens"])           # 앞뒤 센서 창
        P.box((0.004, W - 0.16, 0.012), (sx * (L / 2 + 0.001), 0, bz1 + 0.05), M["led"])  # 상태 LED 띠
    # 위 몸체 (모터·제어부) + 청록 띠 + 라이다
    hz = H - bz1
    P.box((L, W - 0.06, hz), (0, 0, bz1 + hz / 2), M["robot"])
    for sy in (-1, 1):
        P.box((L - 0.10, 0.004, 0.035), (0, sy * ((W - 0.06) / 2 + 0.001), bz1 + hz * 0.55), M["teal"])
        P.box((0.18, 0.004, 0.06), (0.12, sy * ((W - 0.06) / 2 + 0.001), bz1 + hz * 0.25), M["chassis"])
    P.cyl(0.055, 0.05, (0, 0, H + 0.025), M["black"], seg=24)
    P.cyl(0.050, 0.02, (0, 0, H + 0.06), M["lens"], seg=24)
    body = P.build(name + "_Body", coll, bev=0.006); body.parent = root
    # 등반 기어 (네 모서리, 아래·위 두 쌍) + 바닥 바퀴
    G = Parts()
    gy = C["aisleW"] / 2 - 0.035
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = sx * (L / 2 - 0.01), sy * gy
            for z in (0.16, H - 0.12):
                G.box((0.10, abs(y) - (W - 0.06) / 2 + 0.02, 0.06), (sx * (L / 2 - 0.06), sy * ((W - 0.06) / 2 + (abs(y) - (W - 0.06) / 2) / 2), z), M["chassis"])
                G.cyl(0.042, 0.03, (x, y, z), M["gear"], axis="X", seg=20)
                for k in range(12):
                    a = k / 12 * 2 * math.pi
                    G.box((0.03, 0.012, 0.012), (x, y + 0.045 * math.cos(a), z + 0.045 * math.sin(a)), M["gear"])
            G.cyl(0.06, 0.05, (sx * (L / 2 - 0.12), sy * (W / 2 - 0.11), 0.06), M["rubber"], axis="Y", seg=20)
            G.cyl(0.03, 0.052, (sx * (L / 2 - 0.12), sy * (W / 2 - 0.11), 0.06), M["hub"], axis="Y", seg=16)
    wheels = G.build(name + "_Gears", coll); wheels.parent = root
    # 텔레스코픽 암 (토트 칸 양 끝의 포크 2개 + 끝 손가락). 원점 = 로봇 중심, y 로 이동
    A = Parts()
    for sx in (-1, 1):
        A.box((0.025, C["toteD"] + 0.02, 0.05), (sx * (C["toteW"] / 2 + 0.008), 0, C["bayZ"] + 0.10), M["alu"])
        for sy in (-1, 1):
            A.box((0.03, 0.012, 0.05), (sx * (C["toteW"] / 2 - 0.004), sy * (C["toteD"] / 2 + 0.006), C["bayZ"] + 0.12), M["black"])
    arm = A.build("Arm", coll); arm.parent = root
    return root

# ---------------------------------------------------------------- 에셋: 토트 (원점 = 바닥 중심)
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
    for sy in (-1, 1):
        P.box((w1 + 0.014, 0.010, 0.02), (0, sy * (d1 / 2 + 0.004), H - 0.01), M["toteDark"])
        for k in (-2, -1, 0, 1, 2):
            P.box((0.012, 0.008, H * 0.7), (k * 0.12, sy * (d0 / 2 + 0.005), H * 0.48), M["tote"])
        P.box((0.18, 0.004, 0.035), (0, sy * (d1 / 2 + 0.002), H - 0.055), M["toteDark"])      # 손잡이 홈 (긴 면 = 랙 앞뒤)
        P.box((0.10, 0.002, 0.055), (-0.18, sy * (d1 / 2 + 0.004), H - 0.12), M["label"])
        P.box((0.07, 0.001, 0.016), (-0.18, sy * (d1 / 2 + 0.005), H - 0.13), M["barcode"])
    for sx in (-1, 1):
        P.box((0.010, d1 + 0.014, 0.02), (sx * (w1 / 2 + 0.004), 0, H - 0.01), M["toteDark"])
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
    for vi, fl in enumerate([[], ["Cardboard", "ProdBlue", "CardboardWhite", "ProdRed"]]):
        parts = tote_shell(f"Tote_v{vi}", M, coll, H)
        x = -C["toteW"] / 2 + 0.03
        for nm in fl:
            sx = rnd.uniform(0.11, 0.17); sy = rnd.uniform(0.2, C["toteD"] - 0.06); sz = rnd.uniform(0.08, H * 0.75)
            if x + sx > C["toteW"] / 2 - 0.03: break
            P = Parts(); P.box((sx, sy, sz), (x + sx / 2, rnd.uniform(-0.03, 0.03), 0.006 + sz / 2), M[nm])
            parts.append(P.build("item", coll, bev=0.004)); x += sx + 0.01
        out.append(join(parts, f"SkypodTote_v{vi}"))
    return out

# ---------------------------------------------------------------- 랙 (통로 양쪽 면에 기둥, 깊이 방향 레일에 토트)
def make_racks(M, coll):
    nx, cX, nl = C["nx"], C["cellX"], C["levels"]
    zt = top_z()
    P = Parts(); R = Parts()
    for a in range(C["aisles"]):
        ya = aisle_y(a)
        for sgn in (-1, 1):
            yf = ya + sgn * C["aisleW"] / 2           # 통로 쪽 면
            yc = yf + sgn * UPR_Y / 2                 # 기둥 중심 (랙 안쪽)
            yb = yf + sgn * C["rackD"]                # 랙 뒤 끝
            for k in range(nx + 1):
                x = k * cX
                P.box((UPR_X, UPR_Y, zt), (x, yc, zt / 2), M["galv"])
                P.box((0.022, 0.006, zt - 0.1), (x, yf + sgn * 0.003 * -1, zt / 2), M["teeth"])   # 톱니 면 (통로 쪽)
                P.box((0.14, 0.14, 0.012), (x, yc, 0.006), M["galv"])                              # 바닥 베이스판
                # 깊이 방향 토트 레일 (L 앵글): 기둥 양옆
                for l in range(nl):
                    z = level_z(l)
                    for dx in (-1, 1):
                        if (k == 0 and dx < 0) or (k == nx and dx > 0): continue
                        R.box((0.03, C["rackD"] - 0.01, 0.004), (x + dx * (UPR_X / 2 + 0.015), (yf + yb) / 2, z - 0.002), M["rail"])
                        R.box((0.003, C["rackD"] - 0.01, 0.03), (x + dx * (UPR_X / 2 + 0.0015), (yf + yb) / 2, z + 0.013), M["rail"])
                # 뒤쪽 받침 (레일 끝)
                if k < nx:
                    for l in range(0, nl, 1):
                        R.box((cX - UPR_X, 0.02, 0.02), (x + cX / 2, yb - sgn * 0.01, level_z(l) - 0.01), M["rail"])
            # 맨 위 보 + 맨 아래 단 밑 보 (로봇 지나가는 높이 위)
            P.box((GW() + UPR_X, 0.08, 0.10), (GW() / 2, yc, zt - 0.05), M["beam"])
            P.box((GW() + UPR_X, 0.05, 0.06), (GW() / 2, yc, C["level0Z"] - 0.06), M["beam"])
    # 등지는 랙 사이 (flue) 가로 연결대, 높이 3 m 마다
    for j in range(2, 2 * C["aisles"], 2):
        y = ROW_Y[j]
        for k in range(0, nx + 1, 2):
            for z in (3.0, 6.0, 9.0):
                if z < zt: P.box((0.04, 2 * C["rackD"] + C["flue"], 0.04), (k * cX, y, z), M["galv"])
    P.build("RackUprights", coll)
    R.build("RackRails", coll)
    # 맨 위 테두리·가로 보 (위에서 볼 때 구조감)
    T = Parts()
    T.box((0.06, GD(), 0.06), (0, GD() / 2, zt + 0.03), M["beam"])
    T.box((0.06, GD(), 0.06), (GW(), GD() / 2, zt + 0.03), M["beam"])
    for k in range(4, C["nx"], 4):
        T.box((0.05, GD(), 0.05), (k * cX, GD() / 2, zt + 0.025), M["galv"])
    T.build("RackTop", coll)

# ---------------------------------------------------------------- 바닥 표시 + 안전 펜스
def make_floor_marks(M, coll):
    cX = C["cellX"]
    P = Parts()
    x0, x1 = -0.75, GW() + 0.75
    # 앞쪽 주행로 테두리 (노랑), 스테이션 줄 테두리
    for y in (C["frontY"] - 0.45, -2.25):
        P.box((x1 - x0, 0.06, 0.003), ((x0 + x1) / 2, y, 0.0015), M["yellow"])
    # 왼쪽·오른쪽 통로 띠
    for x in (-0.75, GW() + 0.75):
        P.box((0.06, GD() + 1.25, 0.003), (x, (GD() - 1.25) / 2, 0.0015), M["yellow"])
    # 방향 화살표 (앞쪽 주행로: -x)
    for k in range(1, C["nx"], 3):
        x = wx(k)
        P.box((0.30, 0.05, 0.003), (x, C["frontY"], 0.0016), M["white"])
        P.box((0.10, 0.12, 0.003), (x - 0.17, C["frontY"], 0.0016), M["white"])
    # 안전 펜스 (옆·뒤, 높이 2.4 m)
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
    gy0, gy1 = -0.3, GD() + 1.2
    fence(-1.6, gy0, -1.6, gy1); fence(GW() + 1.6, gy0, GW() + 1.6, gy1); fence(-1.6, gy1, GW() + 1.6, gy1)
    P.build("FloorMarks", coll)

# ---------------------------------------------------------------- 피킹 스테이션 (y = -2 줄, 피킹 칸 = 시작 x)
def make_stations(M, coll):
    cX = C["cellX"]
    lights = []
    P = Parts()
    for si, x0 in enumerate(C["stations"]):
        xc = wx(x0); yc = C["spurY"]; yo = yc - 0.55       # 작업자 쪽 면
        hh = 2.3
        # 로봇이 들어오는 틀 (양옆 기둥 + 위 덮개), 작업자 쪽은 허리 높이 카운터
        for sx in (-1, 1):
            P.box((0.08, 1.0, hh), (xc + sx * 0.48, yc, hh / 2), M["white"])
        P.box((1.04, 1.0, 0.10), (xc, yc, hh), M["white"])
        P.box((1.04, 0.04, 0.6), (xc, yc + 0.48, hh - 0.30), M["white"])                    # 뒤 가림판 (랙 쪽)
        P.box((1.2, 0.40, 0.04), (xc, yo - 0.12, 0.88), M["white"])                         # 카운터
        P.box((1.2, 0.03, 0.88), (xc, yo + 0.07, 0.44), M["white"])
        P.box((0.20, 0.004, 0.035), (xc + 0.38, yo - 0.32, 0.902), M["ledGreen"])          # 확인 버튼 줄
        # 화면 (작업자를 향함) + 조명
        P.box((0.50, 0.03, 0.32), (xc, yo + 0.05, 1.55), M["black"])
        P.box((0.46, 0.004, 0.28), (xc, yo + 0.034, 1.55), M["screen"])
        P.box((0.04, 0.04, 0.6), (xc, yo + 0.08, 1.15), M["alu"])
        P.box((0.9, 0.12, 0.04), (xc, yo + 0.1, hh - 0.08), M["lightpanel"])
        # 작업자 매트 + 뒤 주문 토트 선반 (put-to-light)
        P.box((1.6, 1.1, 0.012), (xc, yo - 0.85, 0.006), M["mat"])
        ys = yo - 1.75
        for lv, zz in enumerate((0.35, 0.85, 1.35)):
            P.box((1.6, 0.5, 0.025), (xc, ys, zz), M["alu"])
            for k in range(3):
                x = xc + (k - 1) * 0.5
                P.box((0.40, 0.32, 0.22), (x, ys, zz + 0.125), M["Cardboard"] if (k + lv) % 3 else M["CardboardWhite"])
                P.box((0.05, 0.004, 0.02), (x, ys + 0.255, zz + 0.012), M["ledGreen" if (k + lv + si) % 4 == 0 else "black"])
        for xx in (xc - 0.8, xc + 0.8):
            for yy in (ys - 0.24, ys + 0.24):
                P.box((0.035, 0.035, 1.6), (xx, yy, 0.8), M["alu"])
        # 상태등
        P.cyl(0.03, 0.06, (xc + 0.48, yc - 0.45, hh + 0.10), M["ledRed"], seg=16)
        P.cyl(0.03, 0.06, (xc + 0.48, yc - 0.45, hh + 0.16), M["ledGreen"], seg=16)
        # 대기 칸 바닥 표시
        for k in range(1, C["spurLen"]):
            P.box((0.5, 0.04, 0.003), (wx(x0 + k), yc - 0.35, 0.0016), M["white"])
        # 피킹 중인 토트를 비추는 스포트
        ld = bpy.data.lights.new(f"StationSpot_{si}", "SPOT"); ld.energy = 0.0; ld.spot_size = math.radians(40)
        ld.spot_blend = 0.6; ld.color = (1.0, 0.97, 0.9); ld.shadow_soft_size = 0.05
        lo = bpy.data.objects.new(f"StationSpot_{si}", ld); lo.location = (xc, yc - 0.1, hh - 0.08); link_coll(lo, coll)
        lights.append(lo)
    P.build("Stations", coll)
    return lights

# ---------------------------------------------------------------- 건물 (밝은 고층 물류센터)
def make_building(M, coll):
    cxw, cyw = GW() / 2, GD() / 2
    S = 60.0; Hh = 14.0
    P = Parts()
    P.box((S, S, 0.02), (cxw, cyw, -0.01), M["floor"])
    P.box((S * 0.8, 0.12, 0.004), (cxw, -6.2, 0.002), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 18, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 20, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 22, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 22, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-18, 20, 4):
        P.box((0.3, S, 0.6), (cxw + k, cyw, Hh - 0.4), M["steel"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 17.8, 1.2), M["greenline"])
    for k in range(-16, 20, 8):                                     # 벽 기둥
        P.box((0.4, 0.4, Hh), (cxw + k, cyw + 17.7, Hh / 2), M["steel"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            L.box((1.8, 0.45, 0.05), (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.8), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.8; ld.size_y = 0.45
            ld.energy = 600; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.85); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity SkypodCamera.cs 와 같은 값.
# seed 3, warmup 680 s 궤적 기준: 로봇 5 가 앞쪽 통로(y = 0.89) x = 6.12 칸에서 13 s 에 6.7 m 를 올라가
# 앞쪽 랙의 토트를 꺼냄 (17~19 s), 21~23 s 에 내려옴. 다른 시드에서도 동선은 그대로지만 장면 속 동작은 달라집니다.
CAM_KEYS = [
    (0.000, (-6.5, -9.5, 7.5), (6.0, 3.2, 3.8), 24),      # 전경: 앞 왼쪽 높이 (랙 높이 약 9.8 m)
    (0.100, (-1.0, -7.5, 3.6), (3.0, -0.5, 1.6), 26),
    (0.200, (3.4, -5.4, 2.4), (1.1, -1.8, 0.85), 30),     # 왼쪽 스테이션: 로봇이 토트를 싣고 와 작업자가 피킹
    (0.267, (1.2, -4.8, 2.0), (-0.2, -1.0, 1.1), 28),
    (0.333, (-1.0, -1.6, 1.8), (1.5, 0.89, 1.4), 26),     # 왼쪽 모서리를 돌아
    (0.390, (-1.4, 0.89, 1.8), (6.1, 0.89, 1.6), 24),     # 앞쪽 통로 입구: 통로 안을 봄
    (0.440, (1.2, 0.89, 2.3), (6.1, 0.89, 2.8), 26),      # 로봇이 기둥을 잡고 올라가기 시작 (13 s)
    (0.533, (3.3, 0.89, 5.4), (6.12, 0.89, 6.6), 28),     # 함께 올라감
    (0.600, (3.9, 0.89, 7.0), (6.12, 0.55, 7.25), 30),    # 6.7 m: 암이 앞쪽 랙에서 토트를 꺼냄 (17~19 s)
    (0.667, (3.9, 0.89, 6.6), (6.12, 0.89, 6.4), 30),
    (0.733, (2.6, 0.89, 3.0), (6.12, 0.89, 1.4), 28),     # 토트를 싣고 내려옴 (20~23 s)
    (0.800, (-0.8, 0.89, 2.0), (5.0, 0.89, 1.0), 24),     # 통로 입구로 물러남, 로봇은 스테이션으로 출발
    (0.867, (-4.0, -4.5, 4.0), (4.0, 1.5, 2.5), 24),
    (1.000, (14.5, -10.0, 8.0), (6.0, 3.4, 3.5), 24),     # 빠지며 전경 (앞 오른쪽)
]

def setup_camera(scene_coll, frames):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36; cam_d.clip_start = 0.03
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
        sc.view_settings.exposure = -0.4
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
                    sc.cycles.device = "GPU"; print("[Skypod] GPU:", dev); break
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

def bake_channel(ob, path, index, frames, values, interp="LINEAR", data=None):
    """키프레임을 한 번에 넣기 (keyframe_insert 반복보다 훨씬 빠름). data: 라이트처럼 ob.data 의 속성"""
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
    """값이 바뀌는 곳만 키로 남김 (선형 보간 결과는 같음)"""
    fs, vs = [], []
    n = len(vals)
    for i, v in enumerate(vals):
        if i == 0 or i == n - 1 or abs(v - vals[i - 1]) > eps or abs(vals[i + 1] - v) > eps:
            fs.append(i + 1); vs.append(v)
    return fs, vs

def apply_trace(robots, tote_meshes, spots, coll):
    tr = TRACE
    nf = tr["frames"] + 1
    R = reach()
    for ri, track in enumerate(tr["robots"]):
        root = robots[ri]
        arm = next(c for c in root.children if c.name.startswith("Arm"))
        for ax in range(3):
            fs, vs = compress([k[ax] for k in track])
            bake_channel(root, "location", ax, fs, vs)
        fs, vs = compress([k[3] * R for k in track])
        bake_channel(arm, "location", 1, fs, vs)
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
    # 스테이션 스포트: 피킹 중일 때 켜짐
    for si, cur in enumerate(tr["stations"]):
        lo = spots[si]
        fs, es = [], []
        for f, s in enumerate(cur):
            if f == 0 or (s >= 0) != (cur[f - 1] >= 0):
                fs.append(f + 1); es.append(40.0 if s >= 0 else 0.0)
        bake_channel(lo, "energy", -1, fs, es, "CONSTANT", data=lo.data)
        lo.rotation_euler = (0, 0, 0)
    print(f"[Skypod] totes={len(tr['initialTotes'])} moving={len(tracks)} climbs={len(tr['events'])}")
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
    global TRACE, ROW_Y, DEPTH
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/skypod_trace.json 이 필요합니다 (node tools/export_skypod_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    ROW_Y = TRACE["rowY"]; DEPTH = TRACE["depthY"]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "SkypodRobot")
    tote_meshes = make_tote_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_robot, os.path.join(out, "skypod_robot.fbx"))
        export_glb(a_robot, os.path.join(out, "skypod_robot.glb"))
        for vi, ob in enumerate(tote_meshes):
            export_fbx(ob, os.path.join(out, f"skypod_tote_v{vi}.fbx"), ("MESH",))
        export_glb(tote_meshes[1], os.path.join(out, "skypod_tote_v1.glb"))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("SkypodScene")
    make_building(M, scene)
    sc_ = new_coll("Structure", scene)
    make_racks(M, sc_)
    make_floor_marks(M, sc_)
    spots = make_stations(M, scene)
    rc = new_coll("Robots", scene)
    robots = [make_robot(M, rc, f"Robot_{i}") for i in range(len(TRACE["robots"]))]
    tc = new_coll("Totes", scene)
    frames, fps = apply_trace(robots, tote_meshes, spots, tc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[Skypod] robots={len(robots)} aisles={C['aisles']} levels={C['levels']} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "skypod_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"skypod_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "skypod_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[Skypod] video ->", mp4)
        else:
            print("[Skypod] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[Skypod] done ->", out)

if __name__ == "__main__":
    main()
