"""
Libiao(立镖) T-Sort 핑거형 플랫폼 로봇 소터 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  node tools/export_tsorter_trace.js                   # blender/tsorter_trace.json 생성 (seed 3, warmup 600 s)
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/tsorter_blender.py -- --out blender/out/tsorter --trace blender/tsorter_trace.json --still 1,240,380,520,800
  # 2) 30초 영상 (GPU 권장. GPU가 없으면 --engine eevee)
  blender -b -P blender/tsorter_blender.py -- --out blender/out/tsorter --trace blender/tsorter_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_tsorter_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더 (GPU 없는 PC에서 빠름)
  --no-export        FBX/GLB 내보내기 생략

공개 자료: 핑거형 플랫폼(단층/복층), 투입 스테이션(수동 투입 + 자동 스캔), 충전 파일, 분류 로봇(JTRobot II 480x380x230 mm),
슈트(크기·재질 맞춤). 셀 피치·데크 높이·속도 등은 사진을 보고 잡은 가정입니다 (docs/tsorter-research.md). Libiao 제품 도면이 아닙니다.
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

# 시뮬레이션 설정 (trace.json 의 config 로 덮어씀)
C = dict(fingers=6, fingerLen=14, mainX=5, cell=0.60, deckZ=1.10, stations=[2, 8, 14, 20], binBottom=0.16)
DECK_T = 0.03                     # 데크 판 두께
ROBOT_L, ROBOT_W, ROBOT_H = 0.48, 0.38, 0.19    # 차체 (트레이 제외). JTRobot II 480 x 380 x 230 mm
TRAY_Z = 0.23                     # 데크 위 트레이 상면 (시뮬레이션 trayZ - deckZ)
BIN_W, BIN_Z0, BIN_TOP = 0.50, 0.11, 0.74      # 슈트 통 (바닥 판 위 높이, 입구 높이)
CHUTE_Z0 = 0.80                   # 슈트 칸막이 아래 끝

def cx(i): return (i + 0.5) * C["cell"]
def cy(j): return (j + 0.5) * C["cell"]
def NY(): return 4 * C["fingers"]
def TIP(): return C["mainX"] + C["fingerLen"] - 1
def MAIN_W(): return C["mainX"] * C["cell"]
def X_END(): return (TIP() + 1) * C["cell"]
def lane_y(k, side): return cy(4 * k + 1 + side)
def gap_y(k): return 4 * k * C["cell"]      # 핑거 k 아래 틈의 가운데 (행 4k-1 과 4k 경계)

# ---------------------------------------------------------------- 유틸
def setin(node, name, value):
    if name in node.inputs:
        node.inputs[name].default_value = value

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
    def box(self, size, loc, m, rz=0.0):
        mi = self._mi(m)
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        cs, sn = math.cos(rz), math.sin(rz)
        for v in r["verts"]:
            x, y, z = v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]
            v.co = Vector((x * cs - y * sn + loc[0], x * sn + y * cs + loc[1], z + loc[2]))
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
    def build(self, name, coll, bev=0.0, parent=None):
        me = bpy.data.meshes.new(name); self.bm.to_mesh(me); self.bm.free()
        for m in self.mats: me.materials.append(m)
        ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
        if bev: bevel(ob, bev, 1)
        if parent: ob.parent = parent
        return ob

# ---------------------------------------------------------------- 재질 (절차적 PBR)
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
    # 데크: 짙은 회색 분체도장 강판 + 바퀴 자국 얼룩 (거칠기 변화)
    m, nt, b = principled("DeckDark", (0.075, 0.08, 0.085), metal=0.2, rough=0.5)
    nz, bp, tc = add_noise_bump(nt, b, scale=40, strength=0.02, rough_var=(0.38, 0.62), coord="Object")
    n2 = nt.nodes.new("ShaderNodeTexNoise"); setin(n2, "Scale", 1.5); setin(n2, "Detail", 8.0)
    nt.links.new(tc.outputs["Object"], n2.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.06, 0.064, 0.068, 1); ramp.color_ramp.elements[1].color = (0.11, 0.115, 0.12, 1)
    nt.links.new(n2.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["deck"] = m
    m, nt, b = principled("FrameGrey", (0.30, 0.32, 0.34), metal=0.4, rough=0.42, Coat_Weight=0.2, Coat_Roughness=0.2)
    add_noise_bump(nt, b, scale=300, strength=0.02); M["frame"] = m
    M["steel"] = principled("Galvanized", (0.62, 0.63, 0.64), metal=1.0, rough=0.35)[0]
    add_noise_bump(M["steel"].node_tree, M["steel"].node_tree.nodes["Principled BSDF"], scale=80, strength=0.02, rough_var=(0.25, 0.5))
    M["stainless"] = principled("Stainless", (0.75, 0.75, 0.76), metal=1.0, rough=0.2, Anisotropic=0.5)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["white"] = principled("WhitePlastic", (0.85, 0.86, 0.87), rough=0.35)[0]
    m, nt, b = principled("RobotYellow", (0.95, 0.60, 0.02), rough=0.3, Coat_Weight=0.7, Coat_Roughness=0.08)
    add_noise_bump(nt, b, scale=150, strength=0.01); M["robot"] = m
    M["tray"] = principled("TrayGrey", (0.18, 0.19, 0.2), metal=0.6, rough=0.4)[0]
    M["qr"] = principled("QRMarker", (0.01, 0.01, 0.01), rough=0.6)[0]
    M["qrw"] = principled("QRWhite", (0.8, 0.8, 0.8), rough=0.6)[0]
    M["lane"] = principled("LaneWhite", (0.75, 0.76, 0.76), rough=0.6)[0]
    M["led_green"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=10.0)[0]
    M["led_blue"] = principled("LEDBlue", (0.2, 0.5, 1.0), rough=0.3, Emission_Color=(0.2, 0.5, 1.0, 1), Emission_Strength=8.0)[0]
    M["led_red"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.15, 0.45, 0.9, 1), Emission_Strength=2.5)[0]
    M["belt"] = principled("ConveyorBelt", (0.03, 0.03, 0.035), rough=0.7)[0]
    add_noise_bump(M["belt"].node_tree, M["belt"].node_tree.nodes["Principled BSDF"], scale=600, strength=0.15)
    # 슈트 통: 베이지 PP (공식 그림의 베이지 슈트), 약한 SSS
    m, nt, b = principled("BinBeige", (0.72, 0.63, 0.48), rough=0.55, Subsurface_Weight=0.05)
    add_noise_bump(nt, b, scale=250, strength=0.02, rough_var=(0.45, 0.65)); M["bin"] = m
    M["tag"] = principled("BinTag", (0.92, 0.92, 0.9), rough=0.5)[0]
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardDark", (0.42, 0.28, 0.15)), ("CardboardWhite", (0.78, 0.74, 0.68))):
        m, nt, b = principled(nm, col, rough=0.75, Sheen_Weight=0.1)
        add_noise_bump(nt, b, scale=90, strength=0.08, rough_var=(0.65, 0.85)); M[nm] = m
    M["tape"] = principled("PackingTape", (0.55, 0.42, 0.22), rough=0.15, Coat_Weight=0.4)[0]
    M["label"] = principled("ShippingLabel", (0.92, 0.92, 0.9), rough=0.55)[0]
    for nm, col in (("PolyGrey", (0.18, 0.19, 0.2)), ("PolyWhite", (0.82, 0.83, 0.84))):
        m, nt, b = principled(nm, col, rough=0.25, Coat_Weight=0.5, Coat_Roughness=0.1, Subsurface_Weight=0.1)
        add_noise_bump(nt, b, scale=8, strength=0.35, detail=3.0, rough_var=(0.15, 0.4)); M[nm] = m
    # 바닥: 밝은 에폭시 (반사 + 얼룩)
    m, nt, b = principled("EpoxyFloor", (0.5, 0.5, 0.5), rough=0.25)
    tc = nt.nodes.new("ShaderNodeTexCoord")
    n1 = nt.nodes.new("ShaderNodeTexNoise"); setin(n1, "Scale", 2.5); setin(n1, "Detail", 10.0); setin(n1, "Roughness", 0.65)
    nt.links.new(tc.outputs["Object"], n1.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.40, 0.41, 0.42, 1); ramp.color_ramp.elements[1].color = (0.52, 0.53, 0.54, 1)
    nt.links.new(n1.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    add_noise_bump(nt, b, scale=60, strength=0.03, rough_var=(0.12, 0.35)); M["floor"] = m
    M["greenline"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.55)[0]
    M["wall"] = principled("WallWhite", (0.62, 0.64, 0.66), rough=0.4)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["beam"] = principled("SteelBeam", (0.70, 0.71, 0.72), metal=0.6, rough=0.4)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=8.0)[0]
    M["mat"] = principled("FloorMat", (0.06, 0.06, 0.065), rough=0.9)[0]
    return M

# ---------------------------------------------------------------- 에셋
def make_robot(M, coll, name="Robot"):
    """JTRobot II 형 틸트 트레이 분류 로봇 (480 x 380 x 230 mm). 원점 = 데크 상면 중심, 앞 = +X. 자식 'Tray' 는 X축으로 기울어짐."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, H = ROBOT_L, ROBOT_W, ROBOT_H
    P = Parts()
    P.box((L, W, H - 0.035), (0, 0, 0.03 + (H - 0.035) / 2), M["robot"])
    P.box((L - 0.04, W - 0.04, 0.03), (0, 0, 0.022), M["black"])               # 하부 스커트 (차체보다 안쪽)
    for sx in (-1, 1):
        P.box((0.016, W * 0.78, 0.04), (sx * (L / 2 + 0.006), 0, 0.065), M["rubber"])     # 범퍼
        P.box((0.004, 0.10, 0.025), (sx * (L / 2 + 0.002), 0, 0.13), M["black"])          # 센서 창
        P.box((0.004, 0.18, 0.010), (sx * (L / 2 + 0.002), 0, H - 0.03), M["led_green"] if sx > 0 else M["led_blue"])
    for sy in (-1, 1):
        P.box((L * 0.6, 0.004, 0.02), (0, sy * (W / 2 + 0.002), H - 0.06), M["black"])     # 옆 홈
    body = P.build(name + "_Body", coll, bev=0.006, parent=root)
    Wp = Parts()
    for sy in (-1, 1):
        Wp.cyl(0.04, 0.026, (0, sy * (W / 2 - 0.045), 0.04), M["rubber"], axis="Y", seg=20)
    for sx in (-1, 1):
        for sy in (-1, 1):
            Wp.cyl(0.018, 0.018, (sx * (L / 2 - 0.07), sy * (W / 2 - 0.07), 0.018), M["rubber"], axis="Y", seg=12)
    Wp.build(name + "_Wheels", coll, parent=root)
    # 틸트 축 지지대 (앞뒤)
    Pv = Parts()
    for sx in (-1, 1):
        Pv.box((0.04, 0.05, TRAY_Z - H - 0.012), (sx * (L / 2 - 0.07), 0, H + (TRAY_Z - H - 0.012) / 2), M["tray"])
    Pv.build(name + "_Pivot", coll, parent=root)
    # 트레이: 앞뒤 턱, 좌우 열림 (옆으로 쏟음)
    T = Parts()
    T.box((L - 0.02, W + 0.04, 0.016), (0, 0, -0.008), M["tray"])
    for sx in (-1, 1):
        T.box((0.014, W + 0.04, 0.045), (sx * (L / 2 - 0.017), 0, 0.0225), M["tray"])
    T.box((L - 0.07, W - 0.02, 0.003), (0, 0, 0.0015), M["rubber"])
    tray = T.build("Tray", coll, bev=0.003, parent=root)
    tray.location = (0, 0, TRAY_Z)
    return root

def make_parcel_meshes(M, coll):
    """소포 종류별 메쉬 (원점 = 중심). 박스: 테이프+라벨, 봉투: 둥근 베개 형태"""
    out = []
    for ti, (sx, sy, sz, nm) in enumerate(TRACE["parcelTypes"]):
        if nm.startswith("box"):
            P = Parts()
            P.box((sx, sy, sz), (0, 0, 0), M["Cardboard"])
            ob = P.build("pb", coll, bev=0.005)
            Q = Parts()
            Q.box((sx + 0.003, 0.05, 0.0015), (0, 0, sz / 2 + 0.0009), M["tape"])
            Q.box((sx * 0.35, sy * 0.4, 0.0012), (-sx * 0.2, -sy * 0.15, sz / 2 + 0.0024), M["label"])
            ob2 = Q.build("pt", coll)
            ob = join([ob, ob2], f"Parcel_{nm}")
        else:
            me = bpy.data.meshes.new(nm); bm = bmesh.new()
            bmesh.ops.create_cube(bm, size=1.0)
            bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=4, use_grid_fill=True)
            for v in bm.verts:
                x, y, z = v.co.x * 2, v.co.y * 2, v.co.z * 2
                pinch = (1 - 0.65 * max(abs(x), abs(y)) ** 3)
                v.co = Vector((x * sx / 2, y * sy / 2, z * sz / 2 * pinch))
            bm.to_mesh(me); bm.free()
            for p in me.polygons: p.use_smooth = True
            ob = bpy.data.objects.new("pbody", me); link_coll(ob, coll)
            me.materials.append(M["PolyWhite"] if "white" in nm else M["PolyGrey"])
            Q = Parts(); Q.box((sx * 0.4, sy * 0.35, 0.0012), (sx * 0.1, 0, sz / 2 * 0.97), M["label"])
            ob = join([ob, Q.build("pl", coll)], f"Parcel_{nm}")
        out.append(ob)
    return out

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
    cl = objs[0].users_collection
    (cl[0] if cl else bpy.context.scene.collection).objects.link(ob)
    for o in objs:
        d = o.data; bpy.data.objects.remove(o, do_unlink=True)
        if d.users == 0: bpy.data.meshes.remove(d)
    return ob

def make_bin(M, coll, name="SortBin"):
    """슈트 통: 위가 열린 베이지 PP 상자 + 바퀴 달린 받침. 원점 = 바닥 중심"""
    me = bpy.data.meshes.new(name); bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    top = [f for f in bm.faces if f.normal.z > 0.9]
    bmesh.ops.delete(bm, geom=top, context="FACES_ONLY")
    h = BIN_TOP - BIN_Z0
    for v in bm.verts:
        u = v.co.z + 0.5
        s = 0.94 + 0.06 * u                         # 아래가 약간 좁음 (적층용 테이퍼)
        v.co = Vector((v.co.x * BIN_W * s, v.co.y * BIN_W * s, BIN_Z0 + u * h))
    bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.006)
    bm.to_mesh(me); bm.free()
    shell = bpy.data.objects.new(name + "_Shell", me); me.materials.append(M["bin"]); link_coll(shell, coll)
    bevel(shell, 0.003, 1)
    P = Parts()
    for sy in (-1, 1):                               # 손잡이 턱 + 라벨
        P.box((BIN_W * 0.9, 0.018, 0.03), (0, sy * (BIN_W / 2 + 0.006), BIN_TOP - 0.02), M["bin"])
        P.box((0.14, 0.003, 0.08), (0.12, sy * (BIN_W / 2 + 0.004), BIN_TOP - 0.12), M["tag"])
    P.box((BIN_W + 0.02, BIN_W + 0.02, 0.025), (0, 0, 0.085), M["frame"])     # 받침
    for a in (-1, 1):
        for b in (-1, 1):
            P.cyl(0.03, 0.022, (a * (BIN_W / 2 - 0.04), b * (BIN_W / 2 - 0.04), 0.035), M["rubber"], axis="Y", seg=12)
            P.box((0.03, 0.03, 0.03), (a * (BIN_W / 2 - 0.04), b * (BIN_W / 2 - 0.04), 0.065), M["steel"])
    ob = join([shell, P.build("bp", coll)], name)
    return ob

# ---------------------------------------------------------------- 플랫폼 (메인 데크 + 핑거 + 하부 구조 + 슈트)
def make_platform(M, coll):
    cell, Z, F = C["cell"], C["deckZ"], C["fingers"]
    MW, XE, ny = MAIN_W(), X_END(), NY()
    D = Parts()
    # 메인 데크 (스테이션 분기 칸 + 충전 줄 포함). 판끼리 겹치지 않게 경계에서 1 mm 띄움
    D.box((MW, ny * cell, DECK_T), (MW / 2, ny * cell / 2, Z - DECK_T / 2), M["deck"])
    D.box((MW, cell - 0.002, DECK_T), (MW / 2, ny * cell + cell / 2 + 0.001, Z - DECK_T / 2), M["deck"])     # 충전 줄
    for s in C["stations"]:
        D.box((cell - 0.002, 2 * cell, DECK_T), (-cell / 2 - 0.001, cy(s) + cell / 2, Z - DECK_T / 2), M["deck"])
    for k in range(F):
        y0 = (4 * k + 1) * cell
        D.box((XE - MW - 0.002, 2 * cell, DECK_T), ((MW + XE) / 2 + 0.001, y0 + cell, Z - DECK_T / 2), M["deck"])
    D.build("Deck", coll)
    # 데크 위 표시 (3 mm 띄움): QR 마커, 핑거 차선 구분 점선, 핑거 입구 노란 띠
    Q = Parts(); zq = Z + 0.0015
    for j in range(ny):
        for i in range(-1, TIP() + 1):
            lane = (0 <= i < C["mainX"]) or (i >= C["mainX"] and j % 4 in (1, 2))
            st = i == -1 and any(j in (s, s + 1) for s in C["stations"])
            if lane or st:
                Q.box((0.09, 0.09, 0.001), (cx(i), cy(j), zq), M["qrw"])
                Q.box((0.065, 0.065, 0.001), (cx(i), cy(j), zq + 0.0012), M["qr"])
    for k in range(F):
        yb = (4 * k + 2) * cell
        x = MW + 0.25
        while x < XE - 0.2:
            Q.box((0.28, 0.025, 0.001), (x + 0.14, yb, zq), M["lane"]); x += 0.6
        Q.box((0.05, 2 * cell - 0.06, 0.001), (MW + 0.06, yb, zq), M["yellow"])
    Q.build("DeckMarks", coll)
    # 핑거 옆면 띠 (윗면이 아닌 옆면에 붙여 z-파이팅 없음) + 끝단 범퍼
    E = Parts()
    for k in range(F):
        for yy, sg in (((4 * k + 1) * cell, -1), ((4 * k + 3) * cell, 1)):
            E.box((XE - MW, 0.012, 0.07), ((MW + XE) / 2, yy + sg * 0.007, Z - 0.035), M["yellow"])
        E.box((0.04, 2 * cell + 0.03, 0.10), (XE + 0.02, (4 * k + 2) * cell, Z - 0.02), M["black"])
    for yy, sg in ((0.0, -1), ((ny + 1) * cell, 1)):
        E.box((MW, 0.012, 0.07), (MW / 2, yy + sg * 0.007, Z - 0.035), M["yellow"])
    E.build("EdgeTrim", coll)
    # 하부 구조: 핑거마다 세로보 2줄 + 가로보 + 가운데 기둥, 메인 데크 격자 기둥
    S = Parts(); zb = Z - DECK_T
    for k in range(F):
        yc = (4 * k + 2) * cell
        for sg in (-1, 1):
            S.box((XE - MW, 0.05, 0.10), ((MW + XE) / 2, yc + sg * 0.45, zb - 0.05), M["frame"])
        x = MW + 0.6
        while x < XE:
            S.box((0.05, 0.95, 0.06), (x, yc, zb - 0.07), M["frame"])
            S.box((0.07, 0.07, zb - 0.10), (x, yc, (zb - 0.10) / 2), M["frame"])
            S.box((0.20, 0.20, 0.012), (x, yc, 0.006), M["steel"])
            x += 1.2
    for j in range(0, ny + 2, 2):
        y = min(max(j * cell, 0.06), (ny + 1) * cell - 0.06)
        S.box((MW, 0.05, 0.10), (MW / 2, y, zb - 0.05), M["frame"])
        for x in (0.06, MW / 2, MW - 0.06):
            S.box((0.08, 0.08, zb - 0.10), (x, y, (zb - 0.10) / 2), M["frame"])
            S.box((0.20, 0.20, 0.012), (x, y, 0.006), M["steel"])
    S.build("Substructure", coll)
    # 슈트 칸막이: 핑거 옆 틈에 셀 경계마다 세로 판 + 틈 가운데 긴 칸막이 (두 줄 통을 나눔)
    P = Parts(); hz = (Z - DECK_T - 0.01) - CHUTE_Z0
    for k in range(F + 1):
        yg = gap_y(k)
        for side in (-1, 1):
            if (k == 0 and side < 0) or (k == F and side > 0): continue
            ye = yg + side * 0.6                      # 핑거 가장자리
            for i in range(C["mainX"], TIP() + 2):
                x = i * cell
                P.box((0.004, 0.56, hz), (x, (yg + ye) / 2, CHUTE_Z0 + hz / 2), M["stainless"])
        if 0 < k < F:
            P.box((XE - MW, 0.004, hz), ((MW + XE) / 2, yg, CHUTE_Z0 + hz / 2), M["stainless"])
    P.build("ChuteDividers", coll)
    # 계단 (메인 데크 뒤, 점검용)
    T = Parts(); n = 7; rise = Z / n; run = 0.27
    for s_ in range(n - 1):
        T.box((0.9, run, 0.03), (MW / 2, -run * (s_ + 0.5) - 0.02, Z - (s_ + 1) * rise - 0.015), M["steel"])
    for xo in (MW / 2 - 0.47, MW / 2 + 0.47):
        T.box((0.04, (n - 1) * run, 0.12), (xo, -(n - 1) * run / 2 - 0.02, Z / 2 + 0.05), M["frame"])
    T.build("Stair", coll)

def make_bins(M, coll, bin_ob):
    """슈트 칸마다 통 하나 (인스턴스)"""
    rnd = random.Random(11)
    for idx, (i, j, side) in enumerate(TRACE["chutes"]):
        ob = bpy.data.objects.new(f"Bin_{idx}", bin_ob.data); link_coll(ob, coll)
        ob.location = (cx(i) + (rnd.random() - 0.5) * 0.02, cy(j), 0)
        ob.rotation_euler = (0, 0, (rnd.random() - 0.5) * 0.04)

def make_stations(M, coll):
    """투입 스테이션: 적재대 + 바깥에서 들어오는 벨트 컨베이어 + 스캐너 아치 + 모니터 + 작업자 매트 + 상태등"""
    Z = C["deckZ"]; P = Parts()
    for s in C["stations"]:
        y = cy(s + 1); xt = cx(-2) + 0.08
        P.box((0.46, 0.56, 0.04), (xt, y, Z + 0.10), M["stainless"])                         # 적재대 상면 z = deckZ + 0.12
        for a in (-1, 1):
            for b in (-1, 1):
                P.box((0.04, 0.04, Z + 0.08), (xt + a * 0.19, y + b * 0.24, (Z + 0.08) / 2), M["steel"])
        Lc = 1.9; xc0 = xt - 0.23
        P.box((Lc, 0.46, 0.03), (xc0 - Lc / 2, y, Z + 0.105), M["belt"])                    # 컨베이어 (‑x 방향에서 들어옴)
        for b in (-1, 1):
            P.box((Lc, 0.04, 0.09), (xc0 - Lc / 2, y + b * 0.25, Z + 0.09), M["steel"])
        for kk in range(3):
            for b in (-1, 1):
                P.box((0.05, 0.05, Z + 0.05), (xc0 - 0.3 - kk * 0.7, y + b * 0.23, (Z + 0.05) / 2), M["steel"])
        xa = xc0 - 0.55                                                                        # 스캐너 아치
        for b in (-1, 1):
            P.box((0.06, 0.05, 0.65), (xa, y + b * 0.31, Z + 0.12 + 0.325), M["white"])
        P.box((0.12, 0.68, 0.08), (xa, y, Z + 0.80), M["white"])
        P.box((0.02, 0.38, 0.008), (xa, y, Z + 0.757), M["led_red"])
        P.box((0.04, 0.04, 1.30), (xt - 0.05, y + 0.42, 0.65 + Z - 0.45), M["steel"])        # 모니터 기둥
        P.box((0.03, 0.34, 0.22), (xt - 0.05, y + 0.42, Z + 0.85), M["black"])
        P.box((0.004, 0.30, 0.18), (xt - 0.068, y + 0.42, Z + 0.85), M["screen"])
        P.box((0.9, 1.1, 0.012), (xt - 0.75, y + 0.15, 0.006), M["mat"])                    # 작업자 매트
        P.cyl(0.028, 0.05, (cx(-1), cy(s) - 0.24, Z + 0.025), M["led_green"], seg=12)        # 대기 칸 상태등
    P.build("Stations", coll)
    # 충전 파일 (메인 데크 뒤 끝 줄)
    Q = Parts(); yc = (NY() + 0.5) * C["cell"]
    for i in range(C["mainX"]):
        Q.box((0.30, 0.10, 0.32), (cx(i), yc + 0.2, Z + 0.16), M["white"])
        Q.box((0.22, 0.004, 0.012), (cx(i), yc + 0.148, Z + 0.26), M["led_blue"])
        Q.box((0.10, 0.04, 0.02), (cx(i), yc + 0.13, Z + 0.05), M["stainless"])
    Q.build("ChargingPiles", coll)

def make_building(M, coll):
    cxw, cyw = X_END() / 2, NY() * C["cell"] / 2
    S = 60.0; Hh = 11.0
    P = Parts()
    P.box((S, S, 0.02), (cxw, cyw, -0.01), M["floor"])
    for (sx, sy, x, y) in [(X_END() + 4.6, 0.10, cxw - 0.8, -0.9), (X_END() + 4.6, 0.10, cxw - 0.8, NY() * C["cell"] + 1.4),
                           (0.10, NY() * C["cell"] + 2.3, -3.1, cyw + 0.25), (0.10, NY() * C["cell"] + 2.3, X_END() + 1.5, cyw + 0.25)]:
        P.box((sx, sy, 0.004), (x, y, 0.002), M["yellow"])
    P.box((S * 0.8, 0.14, 0.004), (cxw, -3.4, 0.002), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 17, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 18, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 20, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 20, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-18, 20, 4):
        P.box((0.3, S, 0.6), (cxw + k, cyw, Hh - 0.4), M["beam"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 16.8, 1.2), M["greenline"])
    for k in range(-16, 20, 8):
        P.box((0.4, 0.4, Hh), (cxw + k, cyw + 16.7, Hh / 2), M["beam"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            L.box((1.8, 0.45, 0.05), (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.8), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.8; ld.size_y = 0.45
            ld.energy = 520; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.85); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity TSorterCamera.cs 와 같은 값.
# seed 3, warmup 600 s 궤적 기준:
#   로봇 29 가 7~13 s 에 핑거 2 의 나가는 차선(y = 5.7)을 달려 12.9 s 에 x = 11.1 에서 아래쪽 슈트로 투하
#   로봇 25 가 17.1 s, 로봇 12 가 21.8 s 에 핑거 3 의 x = 7.5 에서 아래쪽 슈트로 투하 (틈 y = 7.2 에서 올려다봄)
#   로봇 17 이 27.0 s 에 핑거 3 의 x = 8.7 에서 투하
CAM_KEYS = [
    (0.000, (-5.5, -6.0, 7.0), (6.0, 7.0, 0.8), 22),      # 전경: 앞 왼쪽 높이 (메인 데크 + 빗살 핑거)
    (0.100, (-4.4, 1.6, 2.7), (-0.6, 5.6, 1.25), 30),     # 왼쪽 투입 스테이션으로 내려감
    (0.200, (-2.5, 4.0, 1.9), (-0.2, 6.1, 1.25), 32),     # 작업자가 스캔한 소포를 로봇에 올림
    (0.267, (1.2, 4.3, 2.0), (3.5, 5.7, 1.3), 28),        # 메인 데크 가장자리: 로봇 29 가 핑거 2 로 들어감
    (0.300, (4.4, 4.75, 1.85), (6.4, 5.7, 1.3), 26),      # 핑거 옆 틈 위에서 로봇과 나란히 달림
    (0.350, (8.4, 4.75, 1.85), (10.2, 5.7, 1.3), 26),
    (0.400, (10.0, 4.6, 2.2), (11.0, 5.65, 1.25), 32),
    (0.450, (11.2, 3.6, 2.3), (11.1, 5.2, 0.9), 30),      # 12.9 s 트레이 기울여 투하 → 통으로 떨어짐
    (0.500, (13.4, 6.8, 2.0), (9.0, 7.2, 0.9), 24),       # 핑거 끝을 돌아 핑거 2·3 사이 틈으로
    (0.567, (10.2, 7.2, 1.65), (7.4, 7.4, 0.95), 26),     # 틈 위 데크 높이: 양옆 핑거 사이로 17.1 s 로봇 25 투하
    (0.617, (9.2, 7.2, 1.6), (7.0, 7.45, 0.85), 28),
    (0.700, (9.5, 6.0, 4.5), (6.5, 7.6, 1.1), 28),        # 위로 솟아오름: 21.8 s 로봇 12 투하
    (0.767, (8.5, 5.0, 6.5), (6.0, 8.0, 1.1), 24),
    (0.850, (11.5, 13.5, 4.2), (8.0, 8.4, 1.2), 28),      # 뒤쪽으로 돌아 핑거 3
    (0.900, (10.4, 11.0, 2.9), (8.7, 8.0, 1.2), 32),      # 27.0 s 로봇 17 투하
    (1.000, (15.5, 16.0, 7.0), (5.5, 7.0, 0.8), 22),      # 빠지며 전경 (뒤 오른쪽)
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
        sc.view_settings.exposure = -1.1
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
                    sc.cycles.device = "GPU"; print("[TSorter] GPU:", dev); break
            except Exception:
                continue
    elif not eevee:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.70, 0.73, 0.78, 1); bg.inputs[1].default_value = 0.15

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

def bake_channel(ob, path, index, frames, values, interp="LINEAR"):
    """키프레임을 한 번에 넣기 (keyframe_insert 반복보다 훨씬 빠름)"""
    if not frames: return
    prop = getattr(ob, path)
    prop[index] = values[0]; ob.keyframe_insert(path, index=index, frame=frames[0])
    fc = next(f for f in iter_fcurves(ob) if f.data_path == path and f.array_index == index)
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

def unwrap(vals):
    out = [vals[0]]
    for v in vals[1:]:
        p = out[-1]
        while v - p > math.pi: v -= 2 * math.pi
        while v - p < -math.pi: v += 2 * math.pi
        out.append(v)
    return out

def apply_trace(robots, parcel_meshes, coll, M):
    tr = TRACE
    nf = tr["frames"] + 1
    for ri, track in enumerate(tr["robots"]):
        root = robots[ri]
        tray = next(c for c in root.children if c.name.startswith("Tray"))
        for ax in range(2):
            fs, vs = compress([k[ax] for k in track]); bake_channel(root, "location", ax, fs, vs)
        bake_channel(root, "location", 2, [1], [C["deckZ"]])
        fs, vs = compress(unwrap([k[2] for k in track])); bake_channel(root, "rotation_euler", 2, fs, vs)
        fs, vs = compress([-k[3] for k in track]); bake_channel(tray, "rotation_euler", 0, fs, vs)
    card = [M["Cardboard"], M["CardboardDark"], M["CardboardWhite"]]
    n_moving = 0
    for sid, P in tr["parcels"].items():
        mesh = parcel_meshes[P["type"]].data
        if tr["parcelTypes"][P["type"]][3].startswith("box"):
            variant = card[0] if P["tint"] < 0.55 else card[1] if P["tint"] < 0.85 else card[2]
            key = f"{mesh.name}_{variant.name}"
            m2 = bpy.data.meshes.get(key)
            if m2 is None:
                m2 = mesh.copy(); m2.name = key; m2.materials[0] = variant
            mesh = m2
        ob = bpy.data.objects.new(f"Parcel_{sid}", mesh); coll.objects.link(ob)
        keys = P["keys"]
        k0 = keys[0]
        ob.location = k0[1:4]; ob.rotation_euler = k0[4:7]
        if len(keys) > 1:
            n_moving += 1
            fs = [k[0] + 1 for k in keys]
            for ax in range(3):
                bake_channel(ob, "location", ax, fs, [k[1 + ax] for k in keys])
                bake_channel(ob, "rotation_euler", ax, fs, [k[4 + ax] for k in keys])
        if P["born"] > 0 or P["died"] >= 0:
            fs, vs = [], []
            if P["born"] > 0: fs += [1, P["born"] + 1]; vs += [0.0, 1.0]
            else: fs += [1]; vs += [1.0]
            if P["died"] >= 0: fs += [P["died"] + 1]; vs += [0.0]
            for ax in range(3): bake_channel(ob, "scale", ax, fs, vs, "CONSTANT")
    print(f"[TSorter] parcels={len(tr['parcels'])} moving={n_moving} drops={len(tr['drops'])}")
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
        raise SystemExit("--trace blender/tsorter_trace.json 이 필요합니다 (node tools/export_tsorter_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["config"]: C[k] = TRACE["config"][k]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "TSorterRobot")
    a_bin = make_bin(M, assets, "TSorterBin")
    parcel_meshes = make_parcel_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_robot, os.path.join(out, "tsorter_robot.fbx"))
        export_glb(a_robot, os.path.join(out, "tsorter_robot.glb"))
        export_fbx(a_bin, os.path.join(out, "tsorter_bin.fbx"), ("MESH",))
        for ob in parcel_meshes:
            export_fbx(ob, os.path.join(out, "tsorter_" + ob.name.lower() + ".fbx"), ("MESH",))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("TSorterScene")
    make_building(M, scene)
    st = new_coll("Structure", scene)
    make_platform(M, st)
    make_stations(M, st)
    bc = new_coll("Bins", scene)
    make_bins(M, bc, a_bin)
    rc = new_coll("Robots", scene)
    robots = [make_robot(M, rc, f"Robot_{i}") for i in range(len(TRACE["robots"]))]
    pc = new_coll("Parcels", scene)
    frames, fps = apply_trace(robots, parcel_meshes, pc, M)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[TSorter] robots={len(robots)} fingers={C['fingers']} chutes={len(TRACE['chutes'])} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "tsorter_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"tsorter_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "tsorter_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[TSorter] video ->", mp4)
        else:
            print("[TSorter] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[TSorter] done ->", out)

if __name__ == "__main__":
    main()
