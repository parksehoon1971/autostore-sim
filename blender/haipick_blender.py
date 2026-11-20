"""
HaiPick Climb 방식 랙 등반 로봇 창고 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  node tools/export_haipick_trace.js                 # blender/haipick_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/haipick_blender.py -- --out blender/out/haipick --trace blender/haipick_trace.json --still 1,300
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/haipick_blender.py -- --out blender/out/haipick --trace blender/haipick_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_haipick_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --no-export        FBX/GLB 내보내기 생략

공개 수치: 랙 높이 최대 12 m, 통로 900 mm, 주행 4 m/s, 등반 1 m/s, 랙 한쪽 면 가이드 레일 + 등반 팔 2개.
칸 피치·토트 크기·로봇 치수·작업대 형태는 가정입니다 (docs/haipick-research.md). Hai Robotics 제품 도면이 아닙니다.
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector

def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else argv[1:]
    a = {"out": "./out", "trace": None, "still": None, "anim": False, "frames": None, "res": "1920x1080",
         "samples": 96, "gpu": False, "no-export": False, "step": 1}
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
C = dict(nx=22, cellX=0.55, lanes="AARRARRARRA", aisleW=0.90, rackD=0.62, rackBase=0.78, levelH=0.42, levels=26,
         beamH=0.08, toteW=0.40, toteD=0.60, toteH=0.30, robotL=0.48, robotW=0.75, deckH=0.32,
         stations=[[2, 4], [9, 4], [16, 4]], stationDepth=0.90, presentZ=0.53)
LANE_Y, LANE_W = [], []
UPRIGHT = 0.05         # 랙 기둥 단면
RAIL_OFF = 0.035       # 가이드 레일: 랙 앞면에서 통로 쪽으로
ARM_UP = 0.36          # 등반 팔이 올라가는 높이
ARM_OUT = 0.045        # 등반 팔 그리퍼가 레일 쪽으로 나가는 거리
FORK_REACH = 0.62      # 포크가 랙 안으로 뻗는 길이

def wx(i): return (i + 0.5) * C["cellX"]
def GW(): return C["nx"] * C["cellX"]
def GD(): return LANE_Y[-1] + LANE_W[-1] / 2
def rack_top(): return C["rackBase"] + C["levels"] * C["levelH"]
def face_side(j):
    """랙 레인 j 가 마주보는 통로 쪽 (−1: y 작은 쪽, +1: y 큰 쪽)"""
    return -1 if j > 0 and C["lanes"][j - 1] == "A" else 1


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

def mesh_box(name, size):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    bm.to_mesh(me); bm.free()
    return me

def box(name, size, loc, material, coll=None, parent=None, bev=0.0):
    ob = bpy.data.objects.new(name, mesh_box(name, size))
    ob.location = loc
    ob.data.materials.append(material)
    link_coll(ob, coll)
    if parent: ob.parent = parent
    if bev: bevel(ob, bev)
    return ob

def cyl(name, r, depth, loc, rot, material, coll=None, parent=None, verts=24):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=verts, radius1=r, radius2=r, depth=depth)
    bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    ob.location = loc; ob.rotation_euler = rot
    me.materials.append(material)
    link_coll(ob, coll)
    if parent: ob.parent = parent
    return ob

def new_coll(name, parent=None):
    c = bpy.data.collections.new(name)
    (parent or bpy.context.scene.collection).children.link(c)
    return c

def join(objs, name):
    bm = bmesh.new()
    mats = []
    for o in objs:
        me = o.data.copy()
        me.transform(o.matrix_basis)
        idx_map = {}
        for i, m in enumerate(me.materials):
            if m not in mats: mats.append(m)
            idx_map[i] = mats.index(m)
        for p in me.polygons:
            p.material_index = idx_map.get(p.material_index, 0)
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    for m in mats: me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    colls = objs[0].users_collection
    (colls[0] if colls else bpy.context.scene.collection).objects.link(ob)
    for o in objs:
        d = o.data
        bpy.data.objects.remove(o, do_unlink=True)
        if d.users == 0: bpy.data.meshes.remove(d)
    return ob

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
    def build(self, name, coll):
        me = bpy.data.meshes.new(name); self.bm.to_mesh(me); self.bm.free()
        for m in self.mats: me.materials.append(m)
        ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
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
    # 랙: 아연도금 강판 (밝은 회색, 미세 반짝임)
    m, nt, b = principled("RackGalv", (0.62, 0.64, 0.66), metal=0.85, rough=0.38)
    add_noise_bump(nt, b, scale=300, strength=0.02, rough_var=(0.3, 0.48)); M["rack"] = m
    M["rackDark"] = principled("RackBeam", (0.30, 0.33, 0.37), metal=0.7, rough=0.42)[0]
    # 가이드 레일: 어두운 알루미늄 + 노란 표식
    m, nt, b = principled("RailAlu", (0.22, 0.23, 0.25), metal=1.0, rough=0.32)
    add_noise_bump(nt, b, scale=80, strength=0.02); M["rail"] = m
    M["rack_teeth"] = principled("RailTeeth", (0.08, 0.08, 0.09), metal=1.0, rough=0.5)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    # 토트: 밝은 회색 PP
    m, nt, b = principled("TotePP", (0.50, 0.53, 0.56), rough=0.45, Subsurface_Weight=0.05, Subsurface_Radius=(0.01, 0.01, 0.01))
    add_noise_bump(nt, b, scale=400, strength=0.03, rough_var=(0.4, 0.55)); M["tote"] = m
    M["toteDark"] = principled("ToteHandle", (0.30, 0.32, 0.34), rough=0.5)[0]
    M["label"] = principled("ToteLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["barcode"] = principled("Barcode", (0.02, 0.02, 0.02), rough=0.6)[0]
    # 로봇: 흰 몸체 (클리어코트), 어두운 회색 팔, 초록 LED
    m, nt, b = principled("RobotWhite", (0.86, 0.87, 0.88), rough=0.3, Coat_Weight=0.5, Coat_Roughness=0.1)
    add_noise_bump(nt, b, scale=200, strength=0.008); M["robot"] = m
    M["robotGrey"] = principled("RobotGrey", (0.16, 0.17, 0.18), metal=0.5, rough=0.35)[0]
    M["deck"] = principled("DeckGrey", (0.35, 0.36, 0.37), metal=0.6, rough=0.4)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    M["ledBlue"] = principled("LEDBlue", (0.1, 0.45, 1.0), rough=0.3, Emission_Color=(0.15, 0.5, 1.0, 1), Emission_Strength=6.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["hai"] = principled("HaiBlue", (0.02, 0.18, 0.55), rough=0.35)[0]
    # 작업대
    M["alu"] = principled("Aluminium", (0.78, 0.79, 0.80), metal=1.0, rough=0.28)[0]
    M["white"] = principled("WhitePanel", (0.88, 0.89, 0.90), rough=0.35, Coat_Weight=0.2)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.35, 0.6, 1.0, 1), Emission_Strength=2.5)[0]
    M["mat"] = principled("FloorMat", (0.06, 0.06, 0.065), rough=0.9)[0]
    M["wood"] = principled("Worktop", (0.55, 0.42, 0.28), rough=0.5)[0]
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardWhite", (0.78, 0.74, 0.68)), ("ProdBlue", (0.08, 0.25, 0.62)),
                    ("ProdRed", (0.70, 0.08, 0.06)), ("ProdGreen", (0.12, 0.45, 0.22)), ("ProdYellow", (0.90, 0.72, 0.10))):
        m, nt, b = principled(nm, col, rough=0.6)
        add_noise_bump(nt, b, scale=90, strength=0.05); M[nm] = m
    # 건물
    m, nt, b = principled("EpoxyFloor", (0.62, 0.63, 0.64), rough=0.22, Coat_Weight=0.5, Coat_Roughness=0.08)
    nz, bp, tc = add_noise_bump(nt, b, scale=3.0, strength=0.01, detail=8.0, rough_var=(0.12, 0.3))
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.55, 0.56, 0.57, 1); ramp.color_ramp.elements[1].color = (0.66, 0.67, 0.68, 1)
    nt.links.new(nz.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["floor"] = m
    M["wall"] = principled("WallWhite", (0.80, 0.81, 0.82), rough=0.6)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["beam"] = principled("SteelBeam", (0.70, 0.71, 0.72), metal=0.6, rough=0.4)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=10.0)[0]
    M["greenline"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.45)[0]
    return M

# ---------------------------------------------------------------- 에셋: 로봇 (HaiClimber 근사)
def make_robot(M, coll, name="Robot"):
    """흰 몸체 + 바닥 초록 LED 띠 + 토트를 싣는 상판 + 포크. 양 끝(y = ±)에 등반 팔 2개씩.
    원점 = 바닥 중심. x 폭 robotL, y 길이 robotW.
    자식: 'Fork' (y 로 뻗음), 'ArmP' / 'ArmN' (+y / −y 쪽 등반 팔, z 로 올라가고 그리퍼가 x 로 나감)"""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, dk = C["robotL"], C["robotW"], C["deckH"]
    P = Parts()
    P.box((L - 0.02, W - 0.02, 0.05), (0, 0, 0.045), M["robotGrey"])                  # 아래 스커트
    P.box((L, W, 0.17), (0, 0, 0.155), M["robot"])                                     # 몸체
    P.box((L - 0.06, W - 0.10, 0.05), (0, 0, 0.265), M["robotGrey"])                   # 상판 받침
    P.box((L - 0.04, W - 0.06, 0.012), (0, 0, dk - 0.006), M["deck"])                   # 상판
    for sy in (-1, 1):
        P.box((L - 0.04, 0.004, 0.012), (0, sy * (W / 2 - 0.007), 0.045), M["ledGreen"])  # 초록 LED 띠
        P.box((0.12, 0.004, 0.03), (0, sy * (W / 2 + 0.001), 0.17), M["lens"])          # 센서 창
        P.box((0.08, 0.003, 0.02), (0.13, sy * (W / 2 + 0.001), 0.20), M["hai"])         # 파란 로고 자리
    for sx in (-1, 1):
        P.box((0.004, W - 0.06, 0.012), (sx * (L / 2 - 0.007), 0, 0.045), M["ledGreen"])
        P.box((0.004, 0.10, 0.03), (sx * (L / 2 + 0.001), 0, 0.17), M["lens"])
        # 몸체 양옆 고정 기둥 (등반 팔이 들어 있는 케이스)
        for sy in (-1, 1):
            P.box((0.05, 0.07, 0.36), (sx * (L / 2 - 0.03), sy * (W / 2 - 0.05), 0.24 + 0.18), M["robotGrey"])
    for sx in (-1, 1):
        for sy in (-1, 1):
            P.cyl(0.045, 0.04, (sx * (L / 2 - 0.08), sy * (W / 2 - 0.12), 0.045), M["rubber"], axis="X", seg=20)
    body = P.build(name + "_Body", coll); bevel(body, 0.004, 1); body.parent = root
    # 포크 (원점 = 몸체 중심, y 로 side*fork*FORK_REACH 만큼 뻗음)
    F = Parts()
    for sx in (-1, 1):
        F.box((0.035, W - 0.12, 0.014), (sx * 0.14, 0, dk + 0.004), M["alu"])
        F.box((0.035, 0.03, 0.04), (sx * 0.14, (W - 0.12) / 2, dk + 0.02), M["robotGrey"])
        F.box((0.035, 0.03, 0.04), (sx * 0.14, -(W - 0.12) / 2, dk + 0.02), M["robotGrey"])
    fork = F.build("Fork", coll); fork.parent = root
    # 등반 팔: 케이스 위로 올라가는 기둥 + 레일 쪽 그리퍼 (원점 = 몸체 중심)
    for sgn, nm in ((1, "ArmP"), (-1, "ArmN")):
        A = Parts()
        for sx in (-1, 1):
            x0 = sx * (L / 2 - 0.03); y0 = sgn * (W / 2 - 0.05)
            A.box((0.032, 0.05, 0.40), (x0, y0, 0.40), M["alu"])                               # 안쪽 팔
            A.box((0.07, 0.08, 0.06), (x0 + sx * 0.012, y0 + sgn * 0.02, 0.61), M["robotGrey"])  # 그리퍼 몸
            A.cyl(0.028, 0.03, (x0 + sx * 0.04, y0 + sgn * 0.045, 0.61), M["rubber"], axis="X", seg=16)  # 레일 바퀴
            A.box((0.012, 0.012, 0.012), (x0, y0 + sgn * 0.03, 0.645), M["ledBlue"])
        arm = A.build(nm, coll); arm.parent = root
    return root

# ---------------------------------------------------------------- 에셋: 토트 (원점 = 바닥 중심, 긴 쪽 = y)
def tote_shell(name, M, coll):
    W, D, H, t = C["toteW"], C["toteD"], C["toteH"], 0.005
    w0, d0 = W - 0.02, D - 0.02
    bm = bmesh.new()
    def rect(w, d, z): return [bm.verts.new((sx * w / 2, sy * d / 2, z)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    ob_, ot_ = rect(w0, d0, 0.0), rect(W, D, H)
    ib_, it_ = rect(w0 - 2 * t, d0 - 2 * t, t), rect(W - 2 * t, D - 2 * t, H)
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
    parts = [ob]
    for sx in (-1, 1):
        parts.append(box("lip", (0.010, D + 0.012, 0.02), (sx * (W / 2 + 0.003), 0, H - 0.01), M["tote"], coll))
        for k in (-2, -1, 0, 1, 2):
            parts.append(box("rib", (0.008, 0.010, H * 0.7), (sx * (w0 / 2 + 0.004), k * 0.11, H * 0.5), M["tote"], coll))
    for sy in (-1, 1):
        parts.append(box("lip", (W + 0.012, 0.010, 0.02), (0, sy * (D / 2 + 0.003), H - 0.01), M["tote"], coll))
        parts.append(box("handle", (0.12, 0.004, 0.03), (0, sy * (D / 2 + 0.001), H - 0.05), M["toteDark"], coll))
        parts.append(box("label", (0.10, 0.002, 0.05), (-0.08, sy * (D / 2 + 0.002), H - 0.12), M["label"], coll))
        parts.append(box("bc", (0.06, 0.001, 0.016), (-0.08, sy * (D / 2 + 0.003), H - 0.13), M["barcode"], coll))
    return parts

def make_tote_meshes(M, coll):
    """토트 3종: 0 = 빈 통, 1~2 = 내용물 (위에서 보일 때 다양하게)"""
    H = C["toteH"]; rnd = random.Random(11)
    fills = [[], ["Cardboard", "ProdBlue", "CardboardWhite"], ["ProdRed", "ProdYellow", "ProdGreen", "Cardboard"]]
    out = []
    for vi, fl in enumerate(fills):
        parts = tote_shell(f"Tote_v{vi}", M, coll)
        y = -C["toteD"] / 2 + 0.03
        for nm in fl:
            sy = rnd.uniform(0.10, 0.17); sx = rnd.uniform(0.15, C["toteW"] - 0.06); sz = rnd.uniform(0.06, H * 0.7)
            if y + sy > C["toteD"] / 2 - 0.03: break
            parts.append(bevel(box("item", (sx, sy, sz), (rnd.uniform(-0.02, 0.02), y + sy / 2, 0.006 + sz / 2), M[nm], coll), 0.004))
            y += sy + 0.01
        out.append(join(parts, f"HaiTote_v{vi}"))
    return out

# ---------------------------------------------------------------- 랙: 기둥 + 받침 앵글 + 보 + 통로 쪽 가이드 레일
def make_racks(M, coll):
    nx, cX, lv, lh, base = C["nx"], C["cellX"], C["levels"], C["levelH"], C["rackBase"]
    H = rack_top() + 0.12
    for j, ch in enumerate(C["lanes"]):
        if ch != "R": continue
        yc, D = LANE_Y[j], C["rackD"]
        s = face_side(j)
        yf, yb = yc + s * (D / 2 - UPRIGHT / 2), yc - s * (D / 2 - UPRIGHT / 2)   # 앞(통로 쪽) / 뒤 기둥 줄
        P = Parts()
        for i in range(nx + 1):
            x = i * cX
            for yy in (yf, yb):
                P.box((UPRIGHT, UPRIGHT, H), (x, yy, H / 2), M["rack"])
                P.box((0.12, 0.12, 0.01), (x, yy, 0.005), M["rackDark"])               # 바닥 판
            # 옆 프레임 가새 (아래 칸, 랙 밑 통로 위)
            for k in range(0, lv, 4):
                z = base + k * lh - 0.06
                P.box((0.03, D - UPRIGHT, 0.03), (x, yc, z), M["rackDark"])
            # 받침 앵글: 단마다 토트 양쪽 가장자리를 받침
            for k in range(lv):
                z = base + k * lh - 0.012
                P.box((0.15 if 0 < i < nx else 0.09, D - 0.04, 0.012), (x if 0 < i < nx else x + (0.03 if i == 0 else -0.03), yc, z), M["rack"])
        # 앞뒤 가로 보 (바닥 위 첫 단 아래, 6단마다, 꼭대기)
        for k in [0] + list(range(6, lv, 6)) + [lv]:
            z = base + k * lh - 0.05
            for yy in (yf, yb):
                P.box((GW(), 0.04, 0.07), (GW() / 2, yy, z), M["rackDark"])
        rk = P.build(f"Rack_{j}", coll)
        # 가이드 레일 (통로 쪽 기둥마다 바닥 ~ 꼭대기)
        R = Parts()
        yr = yc + s * (D / 2 + RAIL_OFF - 0.012)
        for i in range(nx + 1):
            x = i * cX
            R.box((0.04, 0.024, H - 0.05), (x, yr, (H - 0.05) / 2 + 0.02), M["rail"])
            R.box((0.014, 0.006, H - 0.1), (x, yr + s * 0.014, (H - 0.1) / 2 + 0.05), M["rack_teeth"])
            R.box((0.05, 0.03, 0.02), (x, yr, 0.01), M["yellow"])
        R.build(f"Rails_{j}", coll)

# ---------------------------------------------------------------- 작업대 (앞쪽 y < 0 줄: 피킹 칸 + 대기 칸)
def make_stations(M, coll):
    cX, Dp = C["cellX"], C["stationDepth"]
    P = Parts()
    for si, (x0, n) in enumerate(C["stations"]):
        xa, xb = x0 * cX, (x0 + n) * cX
        xp = wx(x0)
        # 바닥: 칸 표시 + 노란 테두리
        P.box((xb - xa, Dp, 0.004), ((xa + xb) / 2, -Dp / 2, 0.002), M["mat"])
        for k in range(n + 1):
            P.box((0.03, Dp, 0.005), (xa + k * cX, -Dp / 2, 0.0025), M["yellow"])
        P.box((xb - xa, 0.03, 0.005), ((xa + xb) / 2, -Dp, 0.0025), M["yellow"])
        # 피킹 칸 등반 프레임 (작업자 쪽 레일 2개)
        yr = -Dp / 2 - (C["robotW"] / 2 + RAIL_OFF) + 0.012
        for sx in (-1, 1):
            P.box((0.04, 0.024, 1.45), (xp + sx * cX / 2, yr, 0.725), M["rail"])
            P.box((0.014, 0.006, 1.4), (xp + sx * cX / 2, yr - 0.014, 0.72), M["rack_teeth"])
        P.box((cX + 0.06, 0.06, 0.06), (xp, yr, 1.47), M["rail"])
        # 작업자 쪽 선반 (토트 바로 앞, 허리 높이) + 모니터 + 완료 버튼 + 경광등
        yw = -Dp - 0.30
        P.box((cX + 0.9, 0.45, 0.04), (xp + 0.2, yw, 0.80), M["wood"])
        for sx in (-1, 1):
            P.box((0.05, 0.4, 0.78), (xp + 0.2 + sx * (cX / 2 + 0.4), yw, 0.39), M["alu"])
        P.box((0.04, 0.04, 0.80), (xp + cX / 2 + 0.45, yw + 0.12, 1.20), M["alu"])
        P.box((0.34, 0.03, 0.22), (xp + cX / 2 + 0.36, yw + 0.10, 1.45), M["black"])
        P.box((0.31, 0.004, 0.19), (xp + cX / 2 + 0.36, yw + 0.084, 1.45), M["screen"])
        P.box((0.07, 0.07, 0.02), (xp - cX / 2 - 0.08, yw - 0.08, 0.83), M["black"])
        P.cyl(0.022, 0.015, (xp - cX / 2 - 0.08, yw - 0.08, 0.845), M["ledGreen"], seg=16)
        P.box((0.04, 0.04, 2.1), (xp - cX / 2 - 0.25, yw + 0.15, 1.05), M["alu"])
        P.cyl(0.035, 0.07, (xp - cX / 2 - 0.25, yw + 0.15, 2.14), M["ledGreen"], seg=16)
        P.cyl(0.035, 0.07, (xp - cX / 2 - 0.25, yw + 0.15, 2.21), M["yellow"], seg=16)
        P.cyl(0.035, 0.07, (xp - cX / 2 - 0.25, yw + 0.15, 2.28), M["ledRed"], seg=16)
        # 작업자 매트 + 뒤쪽 주문 상자 선반 (풋 월)
        P.box((cX + 1.0, 1.0, 0.012), (xp, yw - 0.75, 0.006), M["mat"])
        ys = yw - 1.75
        for lvl, zz in enumerate((0.25, 0.70, 1.15)):
            P.box((1.5, 0.5, 0.025), (xp, ys, zz), M["alu"])
            for k in range(3):
                xc = xp - 0.5 + k * 0.5
                P.box((0.40, 0.32, 0.24), (xc, ys, zz + 0.135), M["Cardboard"] if (k + lvl) % 3 else M["CardboardWhite"])
                P.box((0.04, 0.004, 0.02), (xc, ys + 0.255, zz + 0.012), M["ledGreen" if (k + lvl + si) % 4 == 0 else "black"])
        for xx in (xp - 0.75, xp + 0.75):
            for yy in (ys - 0.24, ys + 0.24):
                P.box((0.035, 0.035, 1.45), (xx, yy, 0.725), M["alu"])
    P.build("Stations", coll)

# ---------------------------------------------------------------- 건물 (밝은 물류센터, 천장 15 m)
def make_building(M, coll):
    cxw, cyw = GW() / 2, GD() / 2
    S = 56.0; Hh = 15.0
    box("Floor", (S, S, 0.02), (cxw, cyw, -0.01), M["floor"], coll)
    P = Parts()
    m = 1.0
    for (sx, sy, x, y) in [(GW() + 2 * m, 0.08, cxw, -3.6), (GW() + 2 * m, 0.08, cxw, GD() + m),
                           (0.08, GD() + m + 3.6, -m, (GD() + m - 3.6) / 2), (0.08, GD() + m + 3.6, GW() + m, (GD() + m - 3.6) / 2)]:
        P.box((sx, sy, 0.004), (x, y, 0.002), M["yellow"])
    P.box((S * 0.8, 0.12, 0.004), (cxw, -5.6, 0.002), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 18, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 18, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 20, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 20, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-18, 20, 4):
        P.box((0.3, S, 0.6), (cxw + k, cyw, Hh - 0.4), M["beam"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 17.8, 1.2), M["greenline"])
    P.build("Shell", coll)
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            x, y = cxw + ix * 5.0, cyw + iy * 5.0
            box("LightPanel", (1.6, 0.4, 0.05), (x, y, Hh - 0.9), M["lightpanel"], coll)
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.6; ld.size_y = 0.4
            ld.energy = 480; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (x, y, Hh - 0.95); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity HaiPickCamera.cs 와 같은 값.
# seed 10, warmup 600 s 궤적 기준: 로봇 10 이 뒤쪽 통로(y 5.63)에서 7.2 m 를 올라가 토트를 꺼내고 (0.8~11.4 s),
# 내려와 랙 밑을 지나 작업대 쪽으로 감 (20.5~27 s). 작업대 3 (x 9.1) 에서 로봇 2 가 토트를 내밂 (13.8~20.3 s).
CAM_KEYS = [
    (0.000, (17.5, -7.0, 8.0), (6.0, 4.0, 4.0), 24),       # 전경: 앞 오른쪽 높이 (12 m 랙)
    (0.117, (13.6, 4.2, 2.4), (9.1, 5.63, 2.0), 30),       # 뒤쪽 통로 끝으로
    (0.200, (12.6, 5.63, 4.9), (9.075, 5.63, 4.9), 35),    # 통로 안: 로봇이 레일을 타고 올라감
    (0.290, (11.2, 5.55, 7.7), (9.075, 5.9, 7.55), 35),    # 7.2 m: 포크로 토트를 꺼냄
    (0.367, (10.4, 5.45, 8.3), (9.075, 6.1, 7.5), 30),
    (0.433, (12.8, 5.63, 9.8), (9.075, 5.63, 6.4), 28),    # 위로 빠지며 내려가는 로봇
    (0.500, (12.0, -3.6, 4.0), (9.0, -0.4, 1.0), 28),      # 앞으로 날아와 작업대
    (0.567, (10.9, -3.3, 2.0), (9.075, -0.5, 1.0), 35),   # 작업대: 로봇이 올라가 토트를 내밂
    (0.640, (7.8, -3.2, 1.9), (9.1, -0.4, 0.95), 38),
    (0.680, (7.975, -1.5, 0.48), (7.975, 3.0, 0.42), 24),  # 랙 밑으로 들어감 (칸 가운데, 높이 0.48 m)
    (0.733, (7.975, 3.3, 0.45), (9.075, 4.9, 0.38), 24),   # 통로 4: 로봇이 랙 밑을 지나 나옴
    (0.800, (8.2, 3.49, 0.5), (10.3, 3.49, 0.42), 26),
    (0.867, (8.6, 3.49, 0.55), (10.725, 2.2, 0.42), 28),   # 앞쪽 랙 밑으로 다시 들어가는 로봇
    (0.933, (13.5, 3.49, 1.6), (9.0, 3.49, 1.0), 26),      # 통로 끝으로 빠져나옴
    (1.000, (18.0, -6.0, 7.5), (6.0, 3.5, 3.5), 24),       # 빠지며 전경
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
    if a["gpu"]:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for dev in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
            try:
                prefs.compute_device_type = dev
                prefs.get_devices()
                if any(d.type == dev for d in prefs.devices):
                    for d in prefs.devices: d.use = d.type == dev
                    sc.cycles.device = "GPU"; print("[HaiPick] GPU:", dev); break
            except Exception:
                continue
    else:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.78, 0.80, 0.83, 1); bg.inputs[1].default_value = 0.35

# ---------------------------------------------------------------- 애니메이션 (궤적)
def iter_fcurves(idblock):
    """이 ID 의 F-커브 (Blender 4.4+ 슬롯 액션: 오브젝트와 데이터가 같은 액션의 다른 슬롯을 쓸 수 있음)"""
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


def apply_trace(robots, tote_meshes, coll):
    tr = TRACE
    nf = tr["frames"] + 1
    for ri, track in enumerate(tr["robots"]):
        root = robots[ri]
        ch = {c.name.split(".")[0]: c for c in root.children}
        fork = next(c for c in root.children if c.name.startswith("Fork"))
        armP = next(c for c in root.children if c.name.startswith("ArmP"))
        armN = next(c for c in root.children if c.name.startswith("ArmN"))
        for ax in range(3):
            fs, vs = compress([k[ax] for k in track])
            bake_channel(root, "location", ax, fs, vs)
        # 등반 팔: 잡는 쪽(side) 팔만 올라감
        fs, vs = compress([k[3] * ARM_UP if k[5] > 0 else 0.0 for k in track]); bake_channel(armP, "location", 2, fs, vs)
        fs, vs = compress([k[3] * ARM_UP if k[5] < 0 else 0.0 for k in track]); bake_channel(armN, "location", 2, fs, vs)
        fs, vs = compress([k[4] * FORK_REACH * k[5] for k in track]); bake_channel(fork, "location", 1, fs, vs)
    rnd = random.Random(5)
    tracks = tr["toteTracks"]
    for tid, p in enumerate(tr["initialTotes"]):
        v = 0 if rnd.random() < 0.2 else 1 + rnd.randrange(2)
        ob = bpy.data.objects.new(f"Tote_{tid}", tote_meshes[v].data); coll.objects.link(ob)
        ob.location = (p[0], p[1], p[2])
        ob.rotation_euler = (0, 0, math.pi if rnd.random() < 0.5 else 0)
        keys = tracks.get(str(tid))
        if keys:
            fs = [k[0] + 1 for k in keys]
            for ax in range(3):
                bake_channel(ob, "location", ax, fs, [k[1 + ax] for k in keys])
    print(f"[HaiPick] totes={len(tr['initialTotes'])} moving={len(tracks)} climbs={len(tr['events'])}")
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

# ---------------------------------------------------------------- 메인
TRACE = None
def main():
    global TRACE
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/haipick_trace.json 이 필요합니다 (node tools/export_haipick_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    LANE_Y[:] = TRACE["laneY"]; LANE_W[:] = TRACE["laneW"]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "HaiClimber")
    tote_meshes = make_tote_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_robot, os.path.join(out, "haipick_robot.fbx"))
        export_glb(a_robot, os.path.join(out, "haipick_robot.glb"))
        for vi, ob in enumerate(tote_meshes):
            export_fbx(ob, os.path.join(out, f"haipick_tote_v{vi}.fbx"), ("MESH",))
        export_glb(tote_meshes[1], os.path.join(out, "haipick_tote_v1.glb"))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("HaiPickScene")
    make_building(M, scene)
    rc_ = new_coll("Racks", scene)
    make_racks(M, rc_)
    make_stations(M, scene)
    rc = new_coll("Robots", scene)
    robots = [make_robot(M, rc, f"Robot_{i}") for i in range(len(TRACE["robots"]))]
    tc = new_coll("Totes", scene)
    frames, fps = apply_trace(robots, tote_meshes, tc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[HaiPick] robots={len(robots)} levels={C['levels']} top={rack_top():.2f} m frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "haipick_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"haipick_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "haipick_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[HaiPick] video ->", mp4)
        else:
            print("[HaiPick] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[HaiPick] done ->", out)

if __name__ == "__main__":
    main()
