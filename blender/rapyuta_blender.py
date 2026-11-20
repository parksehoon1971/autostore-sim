"""
Rapyuta ASRS 방식 다층 큐브 창고 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  node tools/export_rapyuta_trace.js                 # blender/rapyuta_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/rapyuta_blender.py -- --out blender/out/rapyuta --trace blender/rapyuta_trace.json --still 1,560
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/rapyuta_blender.py -- --out blender/out/rapyuta --trace blender/rapyuta_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_rapyuta_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --no-export        FBX/GLB 내보내기 생략

치수: 층 높이 630 mm, 로봇 높이 80 mm, 빈 650 x 440 x 400 mm 는 Rapyuta 공개 자료 값.
셀 피치·다리 높이·기둥 간격 등은 가정입니다 (docs/rapyuta-research.md). Rapyuta 제품 도면이 아닙니다.
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
C = dict(nx=16, ny=13, nf=7, cellX=0.70, cellY=0.50, floorH=0.63, baseZ=0.12, binH=0.40, binFoot=0.10,
         robotH=0.08, aisleStep=6, bufferRows=2, lifts=[[-1, 3], [-1, 6], [-1, 9], [16, 4], [16, 8]],
         stationFloor=1, stations=[[3, 4], [9, 4]])
PANEL_T = 0.025        # 바닥 패널 두께
POLE = 0.045           # 빨간 GFRP 기둥 단면
POLE_EVERY = 2         # 기둥: 격자선 2칸마다
PAD = 0.06             # 빈을 받치는 다리(패드) 한 변 (높이 = binFoot)
BIN_W, BIN_D = 0.65, 0.44
ROBOT_L, ROBOT_W = 0.56, 0.36
PLATE_REST, PLATE_UP = 0.08, 0.055   # 리프트 판 상면 (정지 0.08 m, 최대 +0.055 m → 빈 바닥 0.135 m)
STROKE = 0.035                       # 시뮬레이션 cfg.stroke

def wx(i): return (i + 0.5) * C["cellX"]
def wy(j): return (j + 0.5) * C["cellY"]
def deck(f): return C["baseZ"] + f * C["floorH"]
def GW(): return C["nx"] * C["cellX"]
def GD(): return C["ny"] * C["cellY"]
def top_z(): return deck(C["nf"])

# ---------------------------------------------------------------- 유틸 (libiao_blender.py 와 같은 방식)
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
    # 빨간 GFRP 기둥: 유리섬유 결 (한 방향으로 늘린 노이즈) + 클리어코트
    m, nt, b = principled("PoleRed", (0.62, 0.035, 0.03), rough=0.38, Coat_Weight=0.4, Coat_Roughness=0.2)
    nz, bp, tc = add_noise_bump(nt, b, scale=60, strength=0.03, rough_var=(0.3, 0.5))
    mp = nt.nodes.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = (8.0, 8.0, 0.4)
    nt.links.new(tc.outputs["Object"], mp.inputs["Vector"]); nt.links.new(mp.outputs["Vector"], nz.inputs["Vector"])
    M["pole"] = m
    # 검은 바닥 패널 (약한 무광, 미세 요철)
    m, nt, b = principled("PanelBlack", (0.025, 0.026, 0.028), rough=0.5, Coat_Weight=0.15, Coat_Roughness=0.3)
    add_noise_bump(nt, b, scale=120, strength=0.02, rough_var=(0.4, 0.6)); M["panel"] = m
    M["gridline"] = principled("GridWhite", (0.80, 0.80, 0.78), rough=0.45)[0]
    M["pad"] = principled("PadBlack", (0.04, 0.04, 0.045), rough=0.6)[0]
    # 빈: 반투명 기미가 있는 유백색 PP (약한 서브서피스 + 미세 텍스처)
    m, nt, b = principled("BinPP", (0.86, 0.87, 0.86), rough=0.42, Subsurface_Weight=0.15, Subsurface_Radius=(0.02, 0.02, 0.02))
    add_noise_bump(nt, b, scale=400, strength=0.03, rough_var=(0.36, 0.5)); M["bin"] = m
    M["binDark"] = principled("BinHandle", (0.55, 0.56, 0.56), rough=0.5)[0]
    M["label"] = principled("BinLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["barcode"] = principled("Barcode", (0.02, 0.02, 0.02), rough=0.6)[0]
    # 로봇: 검은 몸체 + 회색 상판
    m, nt, b = principled("RobotBlack", (0.018, 0.018, 0.02), rough=0.32, Coat_Weight=0.6, Coat_Roughness=0.12)
    add_noise_bump(nt, b, scale=200, strength=0.01); M["robot"] = m
    M["plate"] = principled("PlateGrey", (0.30, 0.31, 0.32), metal=0.6, rough=0.35)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["roller"] = principled("RollerGrey", (0.12, 0.12, 0.13), rough=0.6)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["led"] = principled("LEDCyan", (0.2, 0.8, 1.0), rough=0.3, Emission_Color=(0.2, 0.8, 1.0, 1), Emission_Strength=8.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    # 리프트 / 스테이션
    M["alu"] = principled("Aluminium", (0.78, 0.79, 0.80), metal=1.0, rough=0.28)[0]
    M["chain"] = principled("ChainSteel", (0.18, 0.18, 0.19), metal=1.0, rough=0.45)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["white"] = principled("WhitePanel", (0.88, 0.89, 0.90), rough=0.35, Coat_Weight=0.2)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.35, 0.6, 1.0, 1), Emission_Strength=2.5)[0]
    M["mat"] = principled("FloorMat", (0.06, 0.06, 0.065), rough=0.9)[0]
    # 내용물 (상품 상자/파우치)
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardWhite", (0.78, 0.74, 0.68)), ("ProdBlue", (0.08, 0.25, 0.62)),
                    ("ProdRed", (0.70, 0.08, 0.06)), ("ProdGreen", (0.12, 0.45, 0.22)), ("ProdYellow", (0.90, 0.72, 0.10))):
        m, nt, b = principled(nm, col, rough=0.6)
        add_noise_bump(nt, b, scale=90, strength=0.05); M[nm] = m
    M["poly"] = principled("PolyClear", (0.7, 0.72, 0.75), rough=0.15, Coat_Weight=0.6)[0]
    # 건물: 밝은 에폭시 바닥, 흰 벽
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

# ---------------------------------------------------------------- 에셋: 로봇
def make_robot(M, coll, name="Robot"):
    """Rapyuta ASRS 로봇 근사: 높이 80 mm 납작한 검은 몸체, 모서리 메카넘 휠, 들어 올리는 상판.
    원점 = 바닥 상면 중심, 긴 쪽 = +X. 자식 'LiftPlate' 를 Z로 0~0.055 m 올림."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W = ROBOT_L, ROBOT_W
    P = Parts()
    # 몸체: 휠 사이 가운데 부분 + 앞뒤 범퍼
    P.box((L - 0.14, W, 0.052), (0, 0, 0.012 + 0.026), M["robot"])
    for sx in (-1, 1):
        P.box((0.07, W - 0.13, 0.052), (sx * (L / 2 - 0.035), 0, 0.038), M["robot"])
        P.box((0.004, 0.10, 0.016), (sx * (L / 2 + 0.001), 0, 0.045), M["lens"])           # 앞뒤 센서 창
        P.box((0.004, 0.05, 0.006), (sx * (L / 2 + 0.001), 0.09, 0.028), M["led"])
        P.box((0.004, 0.05, 0.006), (sx * (L / 2 + 0.001), -0.09, 0.028), M["led"])
    P.box((L - 0.16, W - 0.04, 0.004), (0, 0, 0.066), M["plate"])                          # 상판 받침
    body = P.build(name + "_Body", coll); bevel(body, 0.003, 1); body.parent = root
    # 메카넘 휠: 허브 + 45° 기울어진 롤러
    Wp = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            cx_, cy_ = sx * (L / 2 - 0.065), sy * (W / 2 - 0.035)
            Wp.append(cyl("hub", 0.022, 0.05, (cx_, cy_, 0.031), (math.pi / 2, 0, 0), M["hub"], coll, None, 16))
            for k in range(8):
                a = k / 8 * 2 * math.pi
                rr = cyl("rol", 0.0075, 0.042, (cx_ + 0.023 * math.cos(a), cy_, 0.031 + 0.023 * math.sin(a)),
                         (0, 0, 0), M["roller"], coll, None, 8)
                # 롤러 축: 바퀴 접선 방향을 휠 축(Y) 쪽으로 45° 기울임
                t = Vector((-math.sin(a), 0, math.cos(a)))
                ax = (t + Vector((0, sx * sy, 0))).normalized()
                rr.rotation_euler = Vector((0, 0, 1)).rotation_difference(ax).to_euler()
                Wp.append(rr)
    wh = join(Wp, name + "_Wheels"); wh.parent = root
    # 들어 올리는 상판 (원점 = 상판 상면, 정지 시 z = PLATE_REST)
    T = Parts()
    T.box((L - 0.18, W - 0.06, 0.010), (0, 0, -0.005), M["plate"])
    for sx in (-1, 1):
        for sy in (-1, 1):
            T.box((0.06, 0.05, 0.003), (sx * 0.14, sy * 0.09, 0.0012), M["rubber"])
    plate = T.build("LiftPlate", coll); plate.location = (0, 0, PLATE_REST); plate.parent = root
    return root

# ---------------------------------------------------------------- 에셋: 빈 (원점 = 바닥 중심)
def bin_shell(name, M, coll, H):
    w0, d0, w1, d1, t = BIN_W - 0.025, BIN_D - 0.025, BIN_W, BIN_D, 0.006
    bm = bmesh.new()
    def rect(w, d, z): return [bm.verts.new((sx * w / 2, sy * d / 2, z)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    ob_, ot_ = rect(w0, d0, 0.0), rect(w1, d1, H)
    ib_, it_ = rect(w0 - 2 * t, d0 - 2 * t, t), rect(w1 - 2 * t, d1 - 2 * t, H)
    bm.faces.new(list(reversed(ob_)))
    for k in range(4):
        n = (k + 1) % 4
        bm.faces.new((ob_[k], ob_[n], ot_[n], ot_[k]))          # 바깥 벽
        bm.faces.new((ot_[k], ot_[n], it_[n], it_[k]))          # 윗면 테두리
        bm.faces.new((ib_[n], ib_[k], it_[k], it_[n]))          # 안쪽 벽
    bm.faces.new(ib_)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(M["bin"])
    ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
    parts = [ob]
    # 위 테두리 립 + 바깥 세로 리브 + 손잡이 홈 + 라벨
    for sy in (-1, 1):
        parts.append(box("lip", (w1 + 0.016, 0.010, 0.022), (0, sy * (d1 / 2 + 0.004), H - 0.011), M["bin"], coll))
        for k in (-2, -1, 0, 1, 2):
            parts.append(box("rib", (0.012, 0.010, H * 0.72), (k * 0.13, sy * (d0 / 2 + 0.006), H * 0.5), M["bin"], coll))
    for sx in (-1, 1):
        parts.append(box("lip", (0.010, d1 + 0.016, 0.022), (sx * (w1 / 2 + 0.004), 0, H - 0.011), M["bin"], coll))
        parts.append(box("handle", (0.004, 0.14, 0.035), (sx * (w1 / 2 + 0.001), 0, H - 0.06), M["binDark"], coll))
    parts.append(box("label", (0.002, 0.11, 0.06), (-(w1 / 2 + 0.003) + 0.006, -0.12, H - 0.13), M["label"], coll))
    parts.append(box("bc", (0.001, 0.07, 0.018), (-(w1 / 2 + 0.004) + 0.006, -0.12, H - 0.14), M["barcode"], coll))
    for sx in (-1, 1):   # 바닥 스키드 (로봇이 들어 올리는 면)
        parts.append(box("skid", (w0 - 0.04, 0.03, 0.008), (0, sx * (d0 / 2 - 0.03), 0.004), M["binDark"], coll))
    return parts

def make_bin_meshes(M, coll):
    """빈 4종: 0 = 빈 통, 1~3 = 내용물 조합 (위에서 보일 때 다양하게)"""
    H = C["binH"]; rnd = random.Random(11)
    fills = [[], ["Cardboard", "Cardboard", "CardboardWhite"], ["ProdBlue", "ProdRed", "ProdYellow", "poly"],
             ["ProdGreen", "CardboardWhite", "ProdBlue", "Cardboard"]]
    out = []
    for vi, fl in enumerate(fills):
        parts = bin_shell(f"Bin_v{vi}", M, coll, H)
        x = -BIN_W / 2 + 0.03
        for nm in fl:
            sx = rnd.uniform(0.12, 0.2); sy = rnd.uniform(0.18, BIN_D - 0.06); sz = rnd.uniform(0.08, H * 0.7)
            if x + sx > BIN_W / 2 - 0.03: break
            parts.append(bevel(box("item", (sx, sy, sz), (x + sx / 2, rnd.uniform(-0.03, 0.03), 0.006 + sz / 2), M[nm], coll), 0.004))
            x += sx + 0.01
        ob = join(parts, f"RapyutaBin_v{vi}")
        out.append(ob)
    return out

# ---------------------------------------------------------------- 구조: 층 모듈 (패널 + 흰 격자선 + 다리 + 기둥)
def is_pole_line_x(i): return i % POLE_EVERY == 0 or i == C["nx"]
def is_pole_line_y(j): return j % POLE_EVERY == 0 or j == C["ny"]

def make_floor_module(M, coll, f):
    nx, ny, cX, cY = C["nx"], C["ny"], C["cellX"], C["cellY"]
    z0 = deck(f)
    P = Parts()
    P.box((GW() + 0.04, GD() + 0.04, PANEL_T), (GW() / 2, GD() / 2, z0 - PANEL_T / 2), M["panel"])
    # 흰 격자선
    for i in range(nx + 1):
        P.box((0.012, GD(), 0.0012), (i * cX, GD() / 2, z0 + 0.0006), M["gridline"])
    for j in range(ny + 1):
        P.box((GW(), 0.012, 0.0012), (GW() / 2, j * cY, z0 + 0.0006), M["gridline"])
    # 다리 (빈 모서리 받침) — 기둥 없는 교차점
    for i in range(nx + 1):
        for j in range(ny + 1):
            if is_pole_line_x(i) and is_pole_line_y(j): continue
            P.box((PAD, PAD, C["binFoot"]), (i * cX, j * cY, z0 + C["binFoot"] / 2), M["pad"])
    # 기둥: 이 층 패널 위 ~ 다음 층 패널 밑
    ph = C["floorH"] - PANEL_T
    for i in range(nx + 1):
        for j in range(ny + 1):
            if not (is_pole_line_x(i) and is_pole_line_y(j)): continue
            x, y = i * cX, j * cY
            P.box((POLE, POLE, ph), (x, y, z0 + ph / 2), M["pole"])
            P.box((PAD + 0.01, PAD + 0.01, 0.02), (x, y, z0 + 0.01), M["pad"])            # 기둥 받침 블록
            P.box((PAD + 0.01, PAD + 0.01, 0.025), (x, y, z0 + C["binFoot"] - 0.0125), M["pad"])  # 빈 받침 브래킷
    # 피킹층: 앞쪽 스테이션 슬롯 패널
    if f == C["stationFloor"]:
        for x0, n in C["stations"]:
            xa, xb = x0 * cX, (x0 + n) * cX
            P.box((xb - xa, cY, PANEL_T), ((xa + xb) / 2, -cY / 2, z0 - PANEL_T / 2), M["panel"])
            for k in range(n + 1):
                P.box((0.012, cY, 0.0012), (xa + k * cX, -cY / 2, z0 + 0.0006), M["gridline"])
                P.box((PAD, PAD, C["binFoot"]), (xa + k * cX, -cY, z0 + C["binFoot"] / 2), M["pad"])
            for xx in (xa, xb):
                P.box((POLE, POLE, z0 - PANEL_T), (xx, -cY, (z0 - PANEL_T) / 2), M["pole"])
            P.box((xb - xa, 0.012, 0.0012), ((xa + xb) / 2, -cY, z0 + 0.0006), M["gridline"])
    return P.build(f"Floor_{f}", coll)

def make_structure(M, coll):
    nx, ny, cX, cY = C["nx"], C["ny"], C["cellX"], C["cellY"]
    for f in range(C["nf"]):
        make_floor_module(M, coll, f)
    P = Parts()
    # 바닥 블록 (앵커 없음) + 맨 아래층 아래 짧은 기둥
    for i in range(nx + 1):
        for j in range(ny + 1):
            if not (is_pole_line_x(i) and is_pole_line_y(j)): continue
            x, y = i * cX, j * cY
            P.box((0.14, 0.14, 0.03), (x, y, 0.015), M["pad"])
            P.box((POLE, POLE, deck(0) - PANEL_T - 0.03), (x, y, 0.03 + (deck(0) - PANEL_T - 0.03) / 2), M["pole"])
    # 맨 위 테두리 보
    zt = top_z() - PANEL_T
    for y in (0, GD()):
        P.box((GW() + POLE, 0.05, 0.04), (GW() / 2, y, zt + 0.02), M["pole"])
    for x in (0, GW()):
        P.box((0.05, GD() + POLE, 0.04), (x, GD() / 2, zt + 0.02), M["pole"])
    P.build("BaseAndTop", coll)

# ---------------------------------------------------------------- 리프트 (체인 구동)
def make_lifts(M, coll):
    cX, cY = C["cellX"], C["cellY"]
    H = top_z() + 0.35
    plats = []
    for li, (lx, ly) in enumerate(C["lifts"]):
        x, y = wx(lx), wy(ly)
        P = Parts()
        hx, hy = cX / 2 - 0.02, cY / 2 + 0.03
        for sx in (-1, 1):
            for sy in (-1, 1):
                P.box((0.04, 0.04, H), (x + sx * hx, y + sy * hy, H / 2), M["alu"])
        out = -1 if lx < 0 else 1       # 바깥쪽 (그리드 반대편)
        for sy in (-1, 1):
            yy = y + sy * hy
            P.box((0.014, 0.02, H - 0.1), (x + out * 0.06, yy, H / 2), M["chain"])        # 체인
            P.box((0.014, 0.02, H - 0.1), (x - out * 0.06, yy, H / 2), M["chain"])
            P.cyl(0.05, 0.03, (x, yy, H - 0.07), M["chain"], axis="Y")                    # 위 스프로킷
            P.cyl(0.05, 0.03, (x, yy, 0.07), M["chain"], axis="Y")
            for f in range(C["nf"] + 1):                                                   # 층마다 옆면 보 (바깥쪽 면은 열림)
                P.box((cX - 0.02, 0.03, 0.03), (x, yy, deck(f) - 0.04), M["alu"])
        P.box((cX + 0.06, cY + 0.12, 0.05), (x, y, H + 0.025), M["alu"])                 # 꼭대기 프레임
        P.box((0.30, 0.18, 0.16), (x, y + 0.0, H + 0.13), M["black"])                     # 모터
        P.box((0.10, cY + 0.12, 0.06), (x, y, H - 0.02), M["chain"])                       # 구동축
        P.box((cX + 0.1, cY + 0.2, 0.02), (x, y, 0.01), M["pad"])                          # 바닥 받침
        P.build(f"LiftShaft_{li}", coll)
        # 승강 플랫폼 (원점 = 상면, z = 리프트 높이)
        T = Parts()
        T.box((cX - 0.06, cY - 0.04, 0.025), (0, 0, -0.0125), M["plate"])
        T.box((cX - 0.06, 0.02, 0.006), (0, (cY - 0.04) / 2 - 0.01, 0.003), M["yellow"])
        T.box((cX - 0.06, 0.02, 0.006), (0, -(cY - 0.04) / 2 + 0.01, 0.003), M["yellow"])
        for sy in (-1, 1):
            T.box((0.16, 0.05, 0.12), (0, sy * (hy - 0.03), -0.05), M["chain"])            # 체인 연결 브래킷
        pl = T.build(f"LiftPlatform_{li}", coll); pl.location = (x, y, deck(0))
        plats.append(pl)
    return plats

# ---------------------------------------------------------------- 피킹 스테이션 (피킹층 앞, y = -1 행)
def make_stations(M, coll):
    cX, cY = C["cellX"], C["cellY"]
    zf = deck(C["stationFloor"])
    lights = []
    P = Parts()
    for si, (x0, n) in enumerate(C["stations"]):
        xa, xb = x0 * cX, (x0 + n) * cX; xm = (xa + xb) / 2; L = xb - xa
        yf = -cY - 0.06                        # 작업자 쪽 앞면
        # 허리 높이 앞 선반 (빈 바로 앞) + 완료 버튼
        P.box((L + 0.1, 0.18, 0.03), (xm, yf - 0.07, zf + 0.30), M["white"])
        P.box((L + 0.1, 0.02, 0.30 + zf), (xm, yf + 0.01, (0.30 + zf) / 2), M["white"])
        for k in range(n):
            xc = xa + (k + 0.5) * cX
            P.box((0.06, 0.06, 0.015), (xc + 0.2, yf - 0.10, zf + 0.322), M["black"])
            P.cyl(0.018, 0.012, (xc + 0.2, yf - 0.10, zf + 0.335), M["ledGreen"], seg=16)  # 완료 버튼
        # 양옆 기둥 + 위 보
        hh = 2.35
        for xx in (xa - 0.08, xb + 0.08):
            P.box((0.06, 0.06, hh), (xx, yf, hh / 2), M["white"])
        P.box((L + 0.22, 0.10, 0.10), (xm, yf, hh), M["white"])
        # 슬롯마다 디스플레이 (빈 위, 작업자 쪽을 향함)
        for k in range(n):
            xc = xa + (k + 0.5) * cX
            P.box((0.30, 0.02, 0.20), (xc, yf - 0.02, zf + C["binFoot"] + C["binH"] + 0.38), M["black"])
            P.box((0.27, 0.004, 0.17), (xc, yf - 0.032, zf + C["binFoot"] + C["binH"] + 0.38), M["screen"])
            P.box((0.03, 0.03, 0.30), (xc, yf, zf + C["binFoot"] + C["binH"] + 0.16), M["white"])
        # 프로젝터 + 모션 캡처 카메라 2대
        P.box((0.26, 0.20, 0.12), (xm, yf - 0.05, hh - 0.11), M["black"])
        P.cyl(0.035, 0.02, (xm, yf - 0.05, hh - 0.18), M["lens"], seg=16)
        for xx in (xa + 0.1, xb - 0.1):
            P.box((0.10, 0.08, 0.07), (xx, yf - 0.06, hh - 0.09), M["black"])
            P.cyl(0.02, 0.02, (xx, yf - 0.11, hh - 0.09), M["lens"], axis="Y", seg=12)
        # 경고등 (빨강) + 부저
        P.cyl(0.03, 0.06, (xb + 0.08, yf, hh + 0.08), M["ledRed"], seg=16)
        P.cyl(0.03, 0.06, (xb + 0.08, yf, hh + 0.14), M["ledGreen"], seg=16)
        # 작업자 매트 + 뒤쪽 주문 상자 선반
        P.box((L + 0.3, 1.1, 0.012), (xm, yf - 0.75, 0.006), M["mat"])
        ys = yf - 1.75
        for lv, zz in enumerate((0.30, 0.75)):
            P.box((L, 0.5, 0.025), (xm, ys, zz), M["alu"])
            for k in range(n):
                xc = xa + (k + 0.5) * cX
                P.box((0.36, 0.30, 0.22), (xc, ys, zz + 0.125), M["Cardboard"] if (k + lv) % 3 else M["CardboardWhite"])
                P.box((0.04, 0.004, 0.02), (xc, ys + 0.255, zz + 0.012), M["ledGreen" if (k + lv + si) % 4 == 0 else "black"])
        for xx in (xa, xb):
            for yy in (ys - 0.24, ys + 0.24):
                P.box((0.035, 0.035, 1.05), (xx, yy, 0.525), M["alu"])
        # 피킹 위치를 비추는 프로젝터 빛 (스포트, 애니메이션으로 위치 이동)
        ld = bpy.data.lights.new(f"Projector_{si}", "SPOT"); ld.energy = 0.0; ld.spot_size = math.radians(16)
        ld.spot_blend = 0.4; ld.color = (0.55, 0.85, 1.0); ld.shadow_soft_size = 0.02
        lo = bpy.data.objects.new(f"Projector_{si}", ld); lo.location = (xm, yf - 0.05, hh - 0.2); link_coll(lo, coll)
        lights.append(lo)
    P.build("Stations", coll)
    return lights

# ---------------------------------------------------------------- 건물 (밝은 물류센터)
def make_building(M, coll):
    cxw, cyw = GW() / 2, GD() / 2
    S = 48.0; Hh = 9.0
    box("Floor", (S, S, 0.02), (cxw, cyw, -0.01), M["floor"], coll)
    P = Parts()
    # 안전선: 구조물 둘레 (노랑) + 통행로 (초록)
    m = 1.2
    for (sx, sy, x, y) in [(GW() + 2 * m, 0.08, cxw, -m - 1.9), (GW() + 2 * m, 0.08, cxw, GD() + m),
                           (0.08, GD() + 2 * m + 1.9, -m - 0.7, cyw - 0.95), (0.08, GD() + 2 * m + 1.9, GW() + m + 0.7, cyw - 0.95)]:
        P.box((sx, sy, 0.004), (x, y, 0.002), M["yellow"])
    P.box((S * 0.8, 0.12, 0.004), (cxw, -5.2, 0.002), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 16, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 16, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 18, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 18, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-15, 17, 3):
        P.box((0.25, 0.4, 0.5), (cxw + k, cyw, Hh - 0.35), M["beam"])
        P.box((0.25, S, 0.5), (cxw + k, cyw, Hh - 0.35), M["beam"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 15.8, 1.2), M["greenline"])                     # 벽 띠
    P.build("Shell", coll)
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            x, y = cxw + ix * 4.5, cyw + iy * 4.5
            box("LightPanel", (1.6, 0.4, 0.05), (x, y, Hh - 0.7), M["lightpanel"], coll)
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.6; ld.size_y = 0.4
            ld.energy = 420; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (x, y, Hh - 0.75); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity RapyutaCamera.cs 와 같은 값.
# seed 3, warmup 600 s 궤적 기준: 로봇 10 이 왼쪽 리프트로 1층→4층 (12~15 s), 4층 통로 질주 (16~23 s),
# 앞쪽 빈을 치움 (26 s). 다른 시드에서도 동선은 그대로지만 장면 속 동작은 달라집니다.
CAM_KEYS = [
    (0.000, (14.5, -7.0, 6.2), (5.6, 3.0, 1.8), 24),      # 전경: 앞 오른쪽 높이
    (0.133, (8.8, -4.4, 2.6), (5.0, -0.2, 1.1), 28),      # 피킹 스테이션 쪽으로
    (0.267, (4.6, -3.4, 2.2), (3.6, -0.3, 1.1), 32),    # 스테이션 근접 (빈 + 디스플레이)
    (0.367, (-3.4, 0.6, 1.3), (-0.35, 3.25, 1.0), 28),    # 왼쪽 리프트
    (0.450, (-3.6, 1.3, 2.0), (-0.35, 3.25, 1.7), 28),    # 리프트와 함께 상승
    (0.517, (-3.0, 2.3, 3.2), (-0.2, 3.25, 2.8), 28),
    (0.567, (-0.9, 3.25, 2.95), (1.2, 3.25, 2.80), 24),   # 리프트 바깥 면으로 4층 통로 진입
    (0.633, (1.2, 3.25, 2.92), (3.5, 3.25, 2.78), 24),    # 층 사이 통로 (빈 아래로 로봇이 다님)
    (0.700, (5.0, 3.25, 2.92), (7.8, 3.25, 2.78), 24),
    (0.767, (7.8, 3.25, 2.95), (11.0, 3.1, 2.8), 24),
    (0.800, (11.4, 3.25, 2.98), (11.8, 2.0, 2.85), 24),   # 오른쪽 리프트 사이로 빠져나옴
    (0.850, (13.2, 1.9, 3.05), (10.2, 1.35, 2.8), 30),    # 앞쪽 빈을 들어 옮기는 장면
    (0.900, (13.0, 1.0, 3.1), (10.3, 1.4, 2.85), 32),
    (1.000, (16.5, -4.0, 6.0), (6.5, 3.0, 2.2), 24),      # 빠지며 전경
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
        sc.view_settings.exposure = 0.0
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
                    sc.cycles.device = "GPU"; print("[Rapyuta] GPU:", dev); break
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

def apply_trace(robots, bin_meshes, lifts, projectors, coll):
    tr = TRACE
    nf = tr["frames"] + 1
    for ri, track in enumerate(tr["robots"]):
        root = robots[ri]
        plate = next(c for c in root.children if c.name.startswith("LiftPlate"))
        for ax in range(3):
            fs, vs = compress([k[ax] for k in track])
            bake_channel(root, "location", ax, fs, vs)
        fs, vs = compress([PLATE_REST + k[3] / STROKE * PLATE_UP for k in track])
        bake_channel(plate, "location", 2, fs, vs)
    # 빈: 4종 메쉬를 공유 (인기 빈일수록 내용물이 적어 보이게 하지는 않음 — 무작위)
    rnd = random.Random(5)
    foot = C["binFoot"]
    tracks = tr["binTracks"]
    for bid, p in enumerate(tr["initialBins"]):
        v = 0 if rnd.random() < 0.15 else 1 + rnd.randrange(3)
        ob = bpy.data.objects.new(f"Bin_{bid}", bin_meshes[v].data); coll.objects.link(ob)
        ob.location = (p[0], p[1], p[2] + foot)
        ob.rotation_euler = (0, 0, math.pi if rnd.random() < 0.5 else 0)
        keys = tracks.get(str(bid))
        if keys:
            fs = [k[0] + 1 for k in keys]
            for ax in range(3):
                bake_channel(ob, "location", ax, fs, [k[1 + ax] + (foot if ax == 2 else 0) for k in keys])
    # 리프트 플랫폼
    for li, zs in enumerate(tr["lifts"]):
        fs, vs = compress(zs)
        bake_channel(lifts[li], "location", 2, fs, vs)
    # 프로젝터: 지금 피킹 중인 슬롯을 비춤
    slot_x = []
    for x0, n in C["stations"]:
        for k in range(n): slot_x.append(x0 + k)
    zf = deck(C["stationFloor"])
    for si, cur in enumerate(tr["stations"]):
        lo = projectors[si]
        fs, xs, es = [], [], []
        for f, s in enumerate(cur):
            if f == 0 or s != cur[f - 1]:
                fs.append(f + 1)
                xs.append(wx(slot_x[s]) if s >= 0 else lo.location.x)
                es.append(60.0 if s >= 0 else 0.0)
        bake_channel(lo, "location", 0, fs, xs, "CONSTANT")
        bake_channel(lo, "energy", -1, fs, es, "CONSTANT", data=lo.data)
        # 아래쪽 빈 윗면을 향하도록 (위치가 x 로만 움직이므로 기울기는 일정)
        d = Vector((0, -C["cellY"] / 2 - lo.location.y, zf + foot + C["binH"] - lo.location.z))
        lo.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    print(f"[Rapyuta] bins={len(tr['initialBins'])} moving={len(tracks)} digs={len(tr['events'])}")
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
    global TRACE
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/rapyuta_trace.json 이 필요합니다 (node tools/export_rapyuta_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "RapyutaRobot")
    bin_meshes = make_bin_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_robot, os.path.join(out, "rapyuta_robot.fbx"))
        export_glb(a_robot, os.path.join(out, "rapyuta_robot.glb"))
        for vi, ob in enumerate(bin_meshes):
            export_fbx(ob, os.path.join(out, f"rapyuta_bin_v{vi}.fbx"), ("MESH",))
        export_glb(bin_meshes[1], os.path.join(out, "rapyuta_bin_v1.glb"))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("RapyutaScene")
    make_building(M, scene)
    sc_ = new_coll("Structure", scene)
    make_structure(M, sc_)
    lifts = make_lifts(M, sc_)
    projectors = make_stations(M, scene)
    rc = new_coll("Robots", scene)
    robots = [make_robot(M, rc, f"Robot_{i}") for i in range(len(TRACE["robots"]))]
    bc = new_coll("Bins", scene)
    frames, fps = apply_trace(robots, bin_meshes, lifts, projectors, bc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[Rapyuta] robots={len(robots)} lifts={len(lifts)} floors={C['nf']} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "rapyuta_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"rapyuta_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "rapyuta_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[Rapyuta] video ->", mp4)
        else:
            print("[Rapyuta] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[Rapyuta] done ->", out)

if __name__ == "__main__":
    main()
