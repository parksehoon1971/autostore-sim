"""
Libiao(立镖) 방식 3D 소터 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/libiao_blender.py -- --out blender/out/libiao --trace blender/libiao_trace.json --still 300
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/libiao_blender.py -- --out blender/out/libiao --trace blender/libiao_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_libiao_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --no-export        FBX/GLB 내보내기 생략

치수는 공개 자료 기반 근사치입니다(셀 0.70 m, 데크 2.4 m). Libiao 제품 도면이 아닙니다.
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector

ARGS = {}
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

# 시뮬레이션 설정 (trace.json 의 config 로 덮어씀)
C = dict(nx=19, ny=13, cell=0.70, aisleStep=3, deckZ=2.40, stations=[2, 8, 14], stationsBack=[16, 10, 4], bagBottom=0.55)
DECK_T = 0.035        # 데크 판 두께
HOLE = 0.58           # 슈트 구멍 한 변
BAG_TOP = 2.08        # 자루 입구 높이 (데크 바로 아래 링에 걸림)
FUNNEL_H = 0.22       # 슈트 깔때기 높이
ROBOT_L, ROBOT_W, ROBOT_H = 0.56, 0.50, 0.24
TRAY_Z = 0.30         # 데크 위 트레이 상면 (시뮬레이션 trayZ - deckZ)

def cx(i): return (i + 0.5) * C["cell"]
def cy(j): return (j + 0.5) * C["cell"]
def is_row(j): return 0 <= j < C["ny"] and j % C["aisleStep"] == 0
def is_col(i): return 0 <= i < C["nx"] and i % C["aisleStep"] == 0
def in_deck(i, j): return 0 <= i < C["nx"] and 0 <= j < C["ny"]
def is_aisle(i, j): return in_deck(i, j) and (is_row(j) or is_col(i))

# ---------------------------------------------------------------- 유틸 (autostore_blender.py 와 같은 방식)
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
    # 데크: 회색 도장 강판 + 미세 오염/마모 (거칠기 변화)
    m, nt, b = principled("DeckSteel", (0.32, 0.34, 0.36), metal=0.35, rough=0.45)
    nz, bp, tc = add_noise_bump(nt, b, scale=40, strength=0.03, rough_var=(0.32, 0.62), coord="Generated")
    n2 = nt.nodes.new("ShaderNodeTexNoise"); setin(n2, "Scale", 3.0); setin(n2, "Detail", 8.0)
    nt.links.new(tc.outputs["Object"], n2.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.24, 0.255, 0.27, 1); ramp.color_ramp.elements[1].color = (0.36, 0.38, 0.40, 1)
    nt.links.new(n2.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    M["deck"] = m
    m, nt, b = principled("FrameBlue", (0.05, 0.16, 0.36), metal=0.2, rough=0.4, Coat_Weight=0.3, Coat_Roughness=0.15)
    add_noise_bump(nt, b, scale=300, strength=0.02); M["frame"] = m
    M["steel"] = principled("Galvanized", (0.62, 0.63, 0.64), metal=1.0, rough=0.35)[0]
    add_noise_bump(M["steel"].node_tree, M["steel"].node_tree.nodes["Principled BSDF"], scale=80, strength=0.02, rough_var=(0.25, 0.5))
    M["stainless"] = principled("Stainless", (0.75, 0.75, 0.76), metal=1.0, rough=0.18, Anisotropic=0.5)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["white"] = principled("WhitePlastic", (0.85, 0.86, 0.87), rough=0.35)[0]
    # 로봇 몸체: 노란 도장 (Mini Yellow) 클리어코트
    m, nt, b = principled("RobotYellow", (0.95, 0.62, 0.02), rough=0.3, Coat_Weight=0.7, Coat_Roughness=0.08)
    add_noise_bump(nt, b, scale=150, strength=0.01); M["robot"] = m
    M["tray"] = principled("TrayGrey", (0.18, 0.19, 0.2), metal=0.6, rough=0.4)[0]
    M["qr"] = principled("QRMarker", (0.01, 0.01, 0.01), rough=0.6)[0]
    M["qrw"] = principled("QRWhite", (0.8, 0.8, 0.8), rough=0.6)[0]
    M["led_green"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=10.0)[0]
    M["led_blue"] = principled("LEDBlue", (0.2, 0.5, 1.0), rough=0.3, Emission_Color=(0.2, 0.5, 1.0, 1), Emission_Strength=8.0)[0]
    M["led_red"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.15, 0.45, 0.9, 1), Emission_Strength=2.5)[0]
    M["belt"] = principled("ConveyorBelt", (0.03, 0.03, 0.035), rough=0.7)[0]
    add_noise_bump(M["belt"].node_tree, M["belt"].node_tree.nodes["Principled BSDF"], scale=600, strength=0.15)
    # 골판지: 기본색 + 섬유 노이즈 + 미세 요철
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardDark", (0.42, 0.28, 0.15)), ("CardboardWhite", (0.78, 0.74, 0.68))):
        m, nt, b = principled(nm, col, rough=0.75, Sheen_Weight=0.1)
        add_noise_bump(nt, b, scale=90, strength=0.08, rough_var=(0.65, 0.85)); M[nm] = m
    M["tape"] = principled("PackingTape", (0.55, 0.42, 0.22), rough=0.15, Coat_Weight=0.4)[0]
    M["label"] = principled("ShippingLabel", (0.92, 0.92, 0.9), rough=0.55)[0]
    # 비닐 봉투(폴리백): 약한 투과 + 매끈 + 주름 범프
    for nm, col in (("PolyGrey", (0.18, 0.19, 0.2)), ("PolyWhite", (0.82, 0.83, 0.84))):
        m, nt, b = principled(nm, col, rough=0.25, Coat_Weight=0.5, Coat_Roughness=0.1, Subsurface_Weight=0.1)
        add_noise_bump(nt, b, scale=8, strength=0.35, detail=3.0, rough_var=(0.15, 0.4)); M[nm] = m
    # 자루: 직조 PP (웨이브 텍스처로 직조 무늬)
    for nm, col in (("SackWhite", (0.80, 0.80, 0.78)), ("SackBlue", (0.08, 0.22, 0.55)), ("SackGreen", (0.10, 0.38, 0.20))):
        m, nt, b = principled(nm, col, rough=0.7, Sheen_Weight=0.3)
        tc = nt.nodes.new("ShaderNodeTexCoord")
        w1 = nt.nodes.new("ShaderNodeTexWave"); setin(w1, "Scale", 120.0); w1.wave_type = "BANDS"
        w2 = nt.nodes.new("ShaderNodeTexWave"); setin(w2, "Scale", 120.0); w2.wave_type = "BANDS"; w2.bands_direction = "Y"
        nt.links.new(tc.outputs["Object"], w1.inputs["Vector"]); nt.links.new(tc.outputs["Object"], w2.inputs["Vector"])
        mx = nt.nodes.new("ShaderNodeMath"); mx.operation = "MAXIMUM"
        nt.links.new(w1.outputs["Fac"], mx.inputs[0]); nt.links.new(w2.outputs["Fac"], mx.inputs[1])
        bp = nt.nodes.new("ShaderNodeBump"); setin(bp, "Strength", 0.15)
        nt.links.new(mx.outputs[0], bp.inputs["Height"]); nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
        M[nm] = m
    # 바닥/건물 (오토스토어 장면과 같은 톤)
    m, nt, b = principled("PolishedConcrete", (0.4, 0.4, 0.4), rough=0.4)
    tc = nt.nodes.new("ShaderNodeTexCoord")
    n1 = nt.nodes.new("ShaderNodeTexNoise"); setin(n1, "Scale", 2.5); setin(n1, "Detail", 10.0); setin(n1, "Roughness", 0.65)
    nt.links.new(tc.outputs["Object"], n1.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.19, 0.19, 0.19, 1); ramp.color_ramp.elements[1].color = (0.34, 0.335, 0.33, 1)
    nt.links.new(n1.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    add_noise_bump(nt, b, scale=60, strength=0.05, rough_var=(0.18, 0.55)); M["floor"] = m
    M["wall"] = principled("WallPanel", (0.30, 0.33, 0.37), metal=0.3, rough=0.5)[0]
    M["ceiling"] = principled("Ceiling", (0.08, 0.08, 0.09), rough=0.8)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.97, 0.92, 1), Emission_Strength=8.0)[0]
    M["ledstrip"] = principled("LEDStrip", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=3.0)[0]
    return M

# ---------------------------------------------------------------- 에셋
def make_robot(M, coll, name="Robot"):
    """소형 틸트 트레이 분류 로봇. 원점 = 데크 상면 중심, 앞 = +X. 자식 'Tray' 는 X축으로 기울어짐."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, H = ROBOT_L, ROBOT_W, ROBOT_H
    P = []
    P.append(bevel(box("chassis", (L, W, H - 0.04), (0, 0, 0.03 + (H - 0.04) / 2), M["robot"], coll), 0.03))
    P.append(bevel(box("skirt", (L + 0.012, W + 0.012, 0.035), (0, 0, 0.038), M["black"], coll), 0.008))
    P.append(bevel(box("lid", (L - 0.06, W - 0.06, 0.012), (0, 0, H - 0.004), M["black"], coll), 0.004))
    # 앞/뒤 범퍼 + 센서 창
    for sx in (-1, 1):
        P.append(bevel(box("bump", (0.02, W * 0.8, 0.05), (sx * (L / 2 + 0.006), 0, 0.075), M["rubber"], coll), 0.008))
        P.append(box("sensor", (0.006, 0.12, 0.03), (sx * (L / 2 + 0.002), 0, 0.15), M["black"], coll))
    body = join(P, name + "_Body"); body.parent = root
    W_ = []
    for sy in (-1, 1):
        W_.append(cyl("w", 0.045, 0.03, (0, sy * (W / 2 - 0.05), 0.045), (math.pi / 2, 0, 0), M["rubber"], coll))
    for sx in (-1, 1):
        for sy in (-1, 1):
            W_.append(cyl("c", 0.02, 0.02, (sx * (L / 2 - 0.08), sy * (W / 2 - 0.08), 0.02), (math.pi / 2, 0, 0), M["rubber"], coll, None, 12))
    wh = join(W_, name + "_Wheels"); wh.parent = root
    led = join([box("l", (0.004, 0.2, 0.012), (L / 2 + 0.001, 0, 0.19), M["led_green"], coll),
                box("l", (0.004, 0.2, 0.012), (-L / 2 - 0.001, 0, 0.19), M["led_blue"], coll)], name + "_LED")
    led.parent = root
    # 기둥(틸트 축 지지)
    post = join([bevel(box("pa", (0.05, 0.06, TRAY_Z - H - 0.01), (sx * (L / 2 - 0.08), 0, H + (TRAY_Z - H - 0.01) / 2), M["tray"], coll), 0.005)
                 for sx in (-1, 1)], name + "_Pivot")
    post.parent = root
    # 트레이: 앞뒤에 턱, 좌우는 열려 있어 옆으로 쏟음
    T = [bevel(box("tp", (L - 0.02, W + 0.02, 0.018), (0, 0, -0.009), M["tray"], coll), 0.004)]
    for sx in (-1, 1):
        T.append(bevel(box("tl", (0.015, W + 0.02, 0.05), (sx * (L / 2 - 0.018), 0, 0.016), M["tray"], coll), 0.004))
    T.append(box("tm", (L - 0.08, W - 0.04, 0.002), (0, 0, 0.001), M["rubber"], coll))
    tray = join(T, "Tray"); tray.location = (0, 0, TRAY_Z); tray.parent = root
    return root

def make_parcel_meshes(M, coll):
    """소포 종류별 메쉬 (원점 = 중심). 박스: 테이프+라벨, 봉투: 둥근 베개 형태"""
    out = []
    for ti, (sx, sy, sz, nm) in enumerate(TRACE["parcelTypes"]):
        if nm.startswith("box"):
            parts = [bevel(box("b", (sx, sy, sz), (0, 0, 0), M["Cardboard"], coll), 0.006)]
            parts.append(box("t", (sx + 0.002, 0.05, 0.0015), (0, 0, sz / 2 + 0.0005), M["tape"], coll))
            parts.append(box("lb", (sx * 0.35, sy * 0.4, 0.001), (-sx * 0.2, -sy * 0.15, sz / 2 + 0.0008), M["label"], coll))
            ob = join(parts, f"Parcel_{nm}")
        else:
            me = bpy.data.meshes.new(nm); bm = bmesh.new()
            bmesh.ops.create_cube(bm, size=1.0)
            bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=4, use_grid_fill=True)
            for v in bm.verts:
                x, y, z = v.co.x * 2, v.co.y * 2, v.co.z * 2  # -1..1
                pinch = (1 - 0.65 * max(abs(x), abs(y)) ** 3)  # 가장자리로 갈수록 얇아짐
                v.co = Vector((x * sx / 2, y * sy / 2, z * sz / 2 * pinch))
            bm.to_mesh(me); bm.free()
            for p in me.polygons: p.use_smooth = True
            ob = bpy.data.objects.new("pbody", me); link_coll(ob, coll)
            me.materials.append(M["PolyWhite"] if "white" in nm else M["PolyGrey"])
            lb = box("lb", (sx * 0.4, sy * 0.35, 0.001), (sx * 0.1, 0, sz / 2 * 0.95), M["label"], coll)
            ob = join([ob, lb], f"Parcel_{nm}")
        out.append(ob)
    return out

def make_station(M, coll, name, x, side):
    """작업자 투입대: 적재대(스테인리스) + 바깥에서 들어오는 벨트 컨베이어 + 스캐너 아치 + 모니터"""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    Z = C["deckZ"]
    P = []
    # 적재대: 데크 위 0.2 m (시뮬레이션 tablePose.z)
    P.append(bevel(box("table", (0.62, 0.62, 0.04), (0, 0, 0.18), M["stainless"], coll), 0.004))
    for a in (-1, 1):
        for b in (-1, 1):
            P.append(box("tleg", (0.04, 0.04, 0.16), (a * 0.27, b * 0.27, 0.08), M["steel"], coll))
    # 컨베이어 (바깥 방향 +side, 1.9 m)
    Lc = 1.9
    P.append(box("belt", (0.5, Lc, 0.03), (0, side * (0.31 + Lc / 2), 0.185), M["belt"], coll))
    for a in (-1, 1):
        P.append(bevel(box("rail", (0.04, Lc, 0.09), (a * 0.27, side * (0.31 + Lc / 2), 0.17), M["steel"], coll), 0.004))
    for k in range(3):
        for a in (-1, 1):
            P.append(box("cleg", (0.05, 0.05, Z + 0.15), (a * 0.25, side * (0.5 + k * 0.75), 0.15 - (Z + 0.15) / 2), M["steel"], coll))
    # 스캐너 아치
    for a in (-1, 1):
        P.append(bevel(box("arch", (0.05, 0.06, 0.75), (a * 0.32, side * 0.75, 0.2 + 0.375), M["white"], coll), 0.006))
    P.append(bevel(box("archtop", (0.70, 0.12, 0.08), (0, side * 0.75, 0.95), M["white"], coll), 0.01))
    frame = join(P, name + "_Frame"); frame.parent = root
    light = box(name + "_Scan", (0.4, 0.02, 0.01), (0, side * 0.75, 0.905), M["led_red"], coll); light.parent = root
    # 모니터 기둥
    mon = join([box("mp", (0.04, 0.04, 1.3), (0.48, side * 0.1, 0.65), M["steel"], coll),
                bevel(box("mb", (0.36, 0.03, 0.24), (0.48, side * 0.1, 1.35), M["black"], coll), 0.006)], name + "_Monitor")
    mon.parent = root
    scr = box(name + "_Screen", (0.32, 0.005, 0.2), (0.48, side * 0.1 - side * 0.016, 1.35), M["screen"], coll); scr.parent = root
    root.location = (x, cy(-3) if side < 0 else cy(C["ny"] + 2), Z)
    return root

def make_platform(M, coll):
    """데크(구멍 뚫린 강판) + QR 마커 + 슈트 깔때기 + 하부 보/기둥 + 난간 + 계단"""
    nx, ny, cell, Z = C["nx"], C["ny"], C["cell"], C["deckZ"]
    j_lo, j_hi = -3, ny + 2                 # 스테이션 행 포함
    P, QR = [], []
    for j in range(j_lo, j_hi + 1):
        for i in range(nx):
            x, y = cx(i), cy(j)
            if in_deck(i, j) and not is_aisle(i, j):
                # 슈트 구멍: 둘레 4장
                r = (cell - HOLE) / 2
                for (sx, sy, ox, oy) in ((cell, r, 0, (cell - r) / 2), (cell, r, 0, -(cell - r) / 2),
                                         (r, HOLE, (cell - r) / 2, 0), (r, HOLE, -(cell - r) / 2, 0)):
                    P.append(box("d", (sx, sy, DECK_T), (x + ox, y + oy, Z - DECK_T / 2), M["deck"], coll))
            else:
                P.append(box("d", (cell, cell, DECK_T), (x, y, Z - DECK_T / 2), M["deck"], coll))
                if is_aisle(i, j) or station_cell(i, j):
                    QR.append(box("q", (0.07, 0.07, 0.001), (x, y, Z + 0.0006), M["qr"], coll))
                    QR.append(box("qw", (0.09, 0.09, 0.0008), (x, y, Z + 0.0002), M["qrw"], coll))
    deck = join(P, "Deck")
    join(QR, "QRMarkers")
    # 슈트 깔때기(아래로 좁아짐) + 테두리
    F = []
    for j in range(ny):
        for i in range(nx):
            if is_aisle(i, j): continue
            x, y = cx(i), cy(j)
            me = bpy.data.meshes.new("funnel"); bm = bmesh.new()
            top, bot, h = HOLE / 2, 0.25, FUNNEL_H
            vs = [bm.verts.new((x + sx * top, y + sy * top, Z - DECK_T)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
            vb = [bm.verts.new((x + sx * bot, y + sy * bot, Z - DECK_T - h)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
            for k in range(4):
                bm.faces.new((vs[k], vs[(k + 1) % 4], vb[(k + 1) % 4], vb[k]))
            bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.004)
            bm.to_mesh(me); bm.free()
            ob = bpy.data.objects.new("funnel", me); me.materials.append(M["steel"]); link_coll(ob, coll)
            F.append(ob)
            # 구멍 테두리 (노란 경고 립)
            for (sx, sy, ox, oy) in ((HOLE + 0.02, 0.01, 0, HOLE / 2), (HOLE + 0.02, 0.01, 0, -HOLE / 2),
                                     (0.01, HOLE, HOLE / 2, 0), (0.01, HOLE, -HOLE / 2, 0)):
                F.append(box("lip", (sx, sy, 0.006), (x + ox, y + oy, Z + 0.003), M["yellow"], coll))
    join(F, "Chutes")
    # 하부 구조: 통로 중심선 아래 주보/보조보 + 교차점 기둥
    S = []
    y0, y1 = cy(j_lo) - cell / 2, cy(j_hi) + cell / 2
    W = nx * cell
    for j in range(j_lo, j_hi + 1):
        if is_row(j) or j in (j_lo, j_hi):
            S.append(box("beamx", (W, 0.10, 0.16), (W / 2, cy(j), Z - DECK_T - 0.08), M["frame"], coll))
    for i in range(nx):
        if is_col(i) or i in (0, nx - 1):
            S.append(box("beamy", (0.08, y1 - y0, 0.12), (cx(i), (y0 + y1) / 2, Z - DECK_T - 0.06), M["frame"], coll))
    mid = C["aisleStep"] * (((ny - 1) // C["aisleStep"]) // 2)   # 가운데 통로 행은 기둥 없음 (점검 통로)
    for j in [j for j in range(0, ny, C["aisleStep"]) if j != mid] + [j_lo, j_hi]:
        for i in range(0, nx, C["aisleStep"]):
            S.append(bevel(box("col", (0.12, 0.12, Z - DECK_T), (cx(i), cy(j), (Z - DECK_T) / 2), M["frame"], coll), 0.006))
            S.append(box("base", (0.26, 0.26, 0.015), (cx(i), cy(j), 0.0075), M["steel"], coll))
    join(S, "Substructure")
    # 난간 (데크 둘레, 스테이션 적재대 쪽은 개방)
    R = []
    for z in (Z + 0.5, Z + 1.05):
        R.append(box("hr", (0.04, y1 - y0, 0.04), (-0.05, (y0 + y1) / 2, z), M["yellow"], coll))
        R.append(box("hr", (0.04, y1 - y0, 0.04), (W + 0.05, (y0 + y1) / 2, z), M["yellow"], coll))
    R.append(box("toe", (0.01, y1 - y0, 0.12), (-0.05, (y0 + y1) / 2, Z + 0.06), M["yellow"], coll))
    R.append(box("toe", (0.01, y1 - y0, 0.12), (W + 0.05, (y0 + y1) / 2, Z + 0.06), M["yellow"], coll))
    k = y0
    while k <= y1 + 1e-6:
        for x in (-0.05, W + 0.05):
            R.append(box("hp", (0.04, 0.04, 1.05), (x, k, Z + 0.525), M["yellow"], coll))
        k += 1.4
    join(R, "Railing")
    # 계단 (오른쪽 앞, 바닥 → 데크)
    T = []
    n = 14; rise = Z / n; run = 0.26
    for s in range(n):
        T.append(box("step", (1.0, run, 0.03), (W + 0.65, y0 + 0.6 + s * run, (s + 1) * rise - 0.015), M["steel"], coll))
    L = n * run
    for xo in (W + 0.13, W + 1.17):
        stringer = box("str", (0.05, math.hypot(L, Z), 0.18), (xo, y0 + 0.6 + L / 2, Z / 2), M["frame"], coll)
        stringer.rotation_euler = (math.atan2(Z, L), 0, 0)
        T.append(stringer)
    join(T, "Stair")
    # 데크 하부 LED 조명 바 (보 아래)
    LS = []
    for j in range(0, ny, C["aisleStep"]):
        LS.append(box("ls", (W - 0.4, 0.05, 0.02), (W / 2, cy(j) + 0.12, Z - DECK_T - 0.17), M["ledstrip"], coll))
    join(LS, "UnderDeckLEDs")
    for j in range(0, ny, C["aisleStep"]):
        ld = bpy.data.lights.new("UnderDeck", "AREA"); ld.shape = "RECTANGLE"; ld.size = W - 0.4; ld.size_y = 0.08
        ld.energy = 220; ld.color = (1.0, 0.97, 0.92)
        lo = bpy.data.objects.new("UnderDeck", ld); lo.location = (W / 2, cy(j) + 0.12, Z - DECK_T - 0.19); link_coll(lo, coll)
    return deck

def station_cell(i, j):
    for s in C["stations"]:
        if j in (-1, -2) and i in (s, s + 1): return True
    for s in C["stationsBack"]:
        if j in (C["ny"], C["ny"] + 1) and i in (s, s - 1): return True
    return False

def make_sack_holders(M, coll):
    """슈트마다 자루 거치대(데크 아래에 매단 사각 링) + 자루(직조 PP)"""
    H, Sk = [], []
    rnd = random.Random(11)
    sacks = {nm: [] for nm in ("SackWhite", "SackBlue", "SackGreen")}
    for (i, j) in TRACE["chutes"]:
        x, y = cx(i), cy(j)
        s = 0.29   # 거치대 반폭
        for (sx, sy, ox, oy) in ((2 * s, 0.025, 0, s), (2 * s, 0.025, 0, -s), (0.025, 2 * s, s, 0), (0.025, 2 * s, -s, 0)):
            H.append(box("ring", (sx, sy, 0.025), (x + ox, y + oy, BAG_TOP), M["steel"], coll))
        hz = C["deckZ"] - DECK_T - BAG_TOP     # 링을 데크 하부에 매다는 봉
        for a in (-1, 1):
            for b in (-1, 1):
                H.append(box("hang", (0.016, 0.016, hz), (x + a * s, y + b * s, BAG_TOP + hz / 2), M["steel"], coll))
        # 자루: 위가 열린 상자 + 아래쪽으로 갈수록 볼록, 접힌 입구
        me = bpy.data.meshes.new("sack"); bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=5, use_grid_fill=True)
        top = [f for f in bm.faces if f.normal.z > 0.9 and f.calc_center_median().z > 0.49]
        bmesh.ops.delete(bm, geom=top, context="FACES_ONLY")
        bot = C["bagBottom"] - 0.02
        hgt = BAG_TOP - bot
        ph = rnd.random() * 6
        for v in bm.verts:
            u = v.co.z + 0.5                        # 0 아래 .. 1 위
            bulge = 1.0 + 0.10 * math.sin(math.pi * min(1, u * 1.2)) + 0.02 * math.sin(v.co.x * 17 + v.co.y * 13 + ph)
            v.co = Vector((x + v.co.x * 2 * (s - 0.01) * bulge, y + v.co.y * 2 * (s - 0.01) * bulge, bot + u * hgt))
        bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.004)
        bm.to_mesh(me); bm.free()
        for p in me.polygons: p.use_smooth = True
        ob = bpy.data.objects.new("sack", me); link_coll(ob, coll)
        nm = "SackWhite" if rnd.random() < 0.6 else ("SackBlue" if rnd.random() < 0.6 else "SackGreen")
        me.materials.append(M[nm]); sacks[nm].append(ob)
        # 입구 접힘 (거치대에 걸친 테두리)
        H.append(box("fold", (2 * s + 0.02, 0.012, 0.07), (x, y + s + 0.005, BAG_TOP - 0.03), M[nm], coll))
        H.append(box("fold", (2 * s + 0.02, 0.012, 0.07), (x, y - s - 0.005, BAG_TOP - 0.03), M[nm], coll))
        H.append(box("fold", (0.012, 2 * s + 0.02, 0.07), (x + s + 0.005, y, BAG_TOP - 0.03), M[nm], coll))
        H.append(box("fold", (0.012, 2 * s + 0.02, 0.07), (x - s - 0.005, y, BAG_TOP - 0.03), M[nm], coll))
    join(H, "SackHolders")
    for nm, obs in sacks.items():
        if obs: join(obs, "Sacks_" + nm)

def make_building(M, coll):
    W, D = C["nx"] * C["cell"], (C["ny"] + 6) * C["cell"]
    cxw, cyw = W / 2, cy(C["ny"] // 2)
    S = 44.0; wall_h = 10.0
    box("Floor", (S, S, 0.02), (cxw, cyw, -0.01), M["floor"], coll)
    for (sx, sy, x, y) in [(W + 3.2, 0.08, cxw, cy(-3) - 2.6), (W + 3.2, 0.08, cxw, cy(C["ny"] + 2) + 2.6),
                           (0.08, D + 5.2, -1.6, cyw), (0.08, D + 5.2, W + 1.6 + 0.8, cyw)]:
        box("SafetyLine", (sx, sy, 0.004), (x, y, 0.002), M["yellow"], coll)
    box("WallBack", (S, 0.2, wall_h), (cxw, cyw + 14, wall_h / 2), M["wall"], coll)
    box("WallLeft", (0.2, S, wall_h), (cxw - 15, cyw, wall_h / 2), M["wall"], coll)
    box("WallRight", (0.2, S, wall_h), (cxw + 17, cyw, wall_h / 2), M["wall"], coll)
    box("WallFront", (S, 0.2, wall_h), (cxw, cyw - 17, wall_h / 2), M["wall"], coll)
    box("Ceiling", (S, S, 0.2), (cxw, cyw, wall_h), M["ceiling"], coll)
    for k in range(-14, 15, 2):
        box("WallRib", (0.06, 0.08, wall_h), (cxw + k, cyw + 13.88, wall_h / 2), M["frame"], coll)
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            x, y = cxw + ix * 4.5, cyw + iy * 4.5
            box("LightPanel", (1.4, 0.35, 0.05), (x, y, wall_h - 0.4), M["lightpanel"], coll)
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.4; ld.size_y = 0.35
            ld.energy = 260; ld.color = (1.0, 0.96, 0.9)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (x, y, wall_h - 0.45); link_coll(lo, coll)

# ---------------------------------------------------------------- 내보내기
def select_tree(root):
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for c in root.children_recursive: c.select_set(True)
    bpy.context.view_layer.objects.active = root

def export_fbx(root, path):
    select_tree(root)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, apply_unit_scale=True,
                             apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, object_types={"EMPTY", "MESH"},
                             use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False)

def export_glb(root, path):
    select_tree(root)
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_yup=True, export_apply=True)

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity LibiaoCamera.cs 와 같은 값.
CAM_KEYS = [
    (0.00, (15.6, -9.0, 7.6), (6.6, 4.5, 1.8), 24),     # 전경: 앞 오른쪽 높이
    (0.13, (9.2, -4.6, 3.7), (5.95, -1.7, 2.6), 30),    # 앞쪽 투입 스테이션
    (0.22, (3.2, -3.6, 3.0), (1.6, 1.0, 2.0), 26),      # 왼쪽 앞으로 내려가며
    (0.28, (-1.9, -3.2, 1.9), (1.0, 1.5, 1.6), 24),     # 데크 왼쪽 모서리 바깥
    (0.35, (-1.4, 4.55, 1.6), (3.0, 4.0, 1.4), 22),     # 데크 아래 가운데 점검 통로로
    (0.45, (4.0, 4.55, 1.6), (8.0, 3.2, 1.3), 22),      # 자루 사이
    (0.55, (13.7, 4.55, 1.7), (10.0, 6.6, 1.6), 24),    # 오른쪽 끝
    (0.63, (15.4, 6.6, 3.7), (10.0, 5.4, 2.4), 24),     # 바깥으로 나와 상승
    (0.72, (11.0, 9.6, 3.6), (7.0, 7.3, 2.5), 28),      # 데크 위로
    (0.78, (8.4, 8.8, 3.2), (6.8, 7.35, 2.55), 35),     # 로봇이 슈트에 투하하는 순간
    (0.84, (8.0, 8.4, 3.3), (6.6, 7.2, 2.5), 35),
    (0.91, (12.6, 11.5, 5.0), (7.0, 5.0, 2.4), 26),
    (1.00, (-3.5, 16.0, 7.8), (6.6, 4.5, 1.8), 24),     # 뒤쪽 왼쪽 전경
]

def setup_camera(scene_coll, frames):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36
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
        sc.view_settings.exposure = -0.6
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
                    sc.cycles.device = "GPU"; print("[Libiao] GPU:", dev); break
            except Exception:
                continue
    else:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.55, 0.58, 0.62, 1); bg.inputs[1].default_value = 0.12

# ---------------------------------------------------------------- 애니메이션 (궤적)
def iter_fcurves(action):
    if hasattr(action, "fcurves") and action.fcurves is not None:
        try:
            return list(action.fcurves)
        except Exception:
            pass
    out = []
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", []):
                out.extend(bag.fcurves)
    return out

def bake_channel(ob, path, index, frames, values, interp="LINEAR"):
    """키프레임을 한 번에 넣기 (keyframe_insert 반복보다 훨씬 빠름)"""
    if not frames: return
    v0 = values[0]
    prop = getattr(ob, path)
    prop[index] = v0
    ob.keyframe_insert(path, index=index, frame=frames[0])
    fc = next(f for f in iter_fcurves(ob.animation_data.action) if f.data_path == path and f.array_index == index)
    n = len(frames)
    if n > 1:
        fc.keyframe_points.add(n - 1)
    co = []
    for f, v in zip(frames, values): co += [f, v]
    fc.keyframe_points.foreach_set("co", co)
    ii = {"LINEAR": 1, "CONSTANT": 0}[interp]
    fc.keyframe_points.foreach_set("interpolation", [ii] * n)
    fc.update()

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
    fr = list(range(1, nf + 1))
    for ri, track in enumerate(tr["robots"]):
        root = robots[ri]
        tray = next(c for c in root.children if c.name.startswith("Tray"))
        bake_channel(root, "location", 0, fr, [k[0] for k in track])
        bake_channel(root, "location", 1, fr, [k[1] for k in track])
        bake_channel(root, "location", 2, fr, [C["deckZ"]] * nf)
        bake_channel(root, "rotation_euler", 2, fr, unwrap([k[2] for k in track]))
        bake_channel(tray, "rotation_euler", 0, fr, [-k[3] for k in track])
    # 소포: 종류별 메쉬 인스턴스, 재질 변형
    card = [M["Cardboard"], M["CardboardDark"], M["CardboardWhite"]]
    n_moving = 0
    for sid, P in tr["parcels"].items():
        mesh = parcel_meshes[P["type"]].data
        if TRACE["parcelTypes"][P["type"]][3].startswith("box"):
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
        # 등장/퇴장: 스케일 0 (계단형)
        if P["born"] > 0 or P["died"] >= 0:
            fs, vs = [], []
            if P["born"] > 0: fs += [1, P["born"] + 1]; vs += [0.0, 1.0]
            else: fs += [1]; vs += [1.0]
            if P["died"] >= 0: fs += [P["died"] + 1]; vs += [0.0]
            for ax in range(3): bake_channel(ob, "scale", ax, fs, vs, "CONSTANT")
    print(f"[Libiao] parcels={len(tr['parcels'])} moving={n_moving}")
    return nf, tr["fps"]

# ---------------------------------------------------------------- 메인
TRACE = None
def main():
    global TRACE
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/libiao_trace.json 이 필요합니다 (node tools/export_libiao_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["config"]: C[k] = TRACE["config"][k]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "LibiaoRobot")
    parcel_meshes = make_parcel_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_robot, os.path.join(out, "libiao_robot.fbx"))
        export_glb(a_robot, os.path.join(out, "libiao_robot.glb"))
        for ob in parcel_meshes:
            nm = ob.name.replace("Parcel_", "")
            bpy.ops.object.select_all(action="DESELECT"); ob.select_set(True); bpy.context.view_layer.objects.active = ob
            bpy.ops.export_scene.fbx(filepath=os.path.join(out, f"libiao_parcel_{nm}.fbx"), use_selection=True, apply_unit_scale=True,
                                     apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
                                     object_types={"MESH"}, mesh_smooth_type="FACE", add_leaf_bones=False)
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("LibiaoScene")
    make_building(M, scene)
    make_platform(M, scene)
    make_sack_holders(M, scene)
    for idx, s in enumerate(C["stations"]):
        make_station(M, scene, f"Station_F{idx}", cx(s), -1)
    for idx, s in enumerate(C["stationsBack"]):
        make_station(M, scene, f"Station_B{idx}", cx(s), 1)
    robots = []
    rc = new_coll("Robots", scene)
    for idx in range(len(TRACE["robots"])):
        robots.append(make_robot(M, rc, f"Robot_{idx}"))
    pc = new_coll("Parcels", scene)
    frames, fps = apply_trace(robots, parcel_meshes, pc, M)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[Libiao] robots={len(robots)} chutes={len(TRACE['chutes'])} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "libiao_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"libiao_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "libiao_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[Libiao] video ->", mp4)
        else:
            print("[Libiao] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[Libiao] done ->", out)

if __name__ == "__main__":
    main()
