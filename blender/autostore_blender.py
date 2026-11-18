"""
AutoStore 스타일 큐브 스토리지 — 사실적 Blender 장면 + 시뮬레이션 기반 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

사용 예 (저장소 루트에서)
  # 1) 모델/장면만 생성 (+ Unity용 FBX 내보내기)
  blender -b -P blender/autostore_blender.py -- --out blender/out
  # 2) 시뮬레이션 궤적으로 30초 애니메이션 장면 생성 + 정지 이미지 1장 렌더
  blender -b -P blender/autostore_blender.py -- --out blender/out --trace blender/trace.json --still 450
  # 3) 30초 영상 렌더 (GPU 권장)
  blender -b -P blender/autostore_blender.py -- --out blender/out --trace blender/trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_trace.js 로 만든 궤적 JSON (없으면 정지 장면)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용, 영상 fps = 30/N)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --no-export        FBX/GLB 내보내기 생략

치수는 공개 자료 기반 근사치입니다(330 mm 빈, 셀 피치 0.70 x 0.50 m). 실제 제품 도면이 아닙니다.
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector

# ---------------------------------------------------------------- 파라미터
GRID_X, GRID_Y, GRID_Z = 12, 8, 16
CELL_X, CELL_Y = 0.70, 0.50
BIN_L, BIN_W, BIN_H = 0.649, 0.449, 0.330
POST = 0.045
RAIL_H = 0.07
FILL = 0.80
PORTS_X = [2, 5, 8]
PORT_Z = 0.95
ROBOTS = [(1, 3), (4, 6), (7, 2), (10, 5), (6, 4), (3, 1), (9, 7), (11, 1)]
SEED = 7
TOP_Z = GRID_Z * BIN_H + 0.10

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

# ---------------------------------------------------------------- 유틸
def setin(node, name, value):
    if name in node.inputs:
        node.inputs[name].default_value = value

def link_coll(ob, coll):
    (coll or bpy.context.scene.collection).objects.link(ob)

def bevel(ob, w=0.004, seg=2):
    """모서리 베벨을 메쉬에 직접 적용 (모디파이어 대신 → 내보내기/병합이 단순해짐)"""
    bm = bmesh.new(); bm.from_mesh(ob.data)
    edges = [e for e in bm.edges if e.calc_face_angle(0) > 0.5]
    if edges:
        bmesh.ops.bevel(bm, geom=edges, offset=w, segments=seg, affect="EDGES", profile=0.5, clamp_overlap=True)
    bm.to_mesh(ob.data); bm.free()
    return ob

def mesh_box(name, size, loc=(0, 0, 0)):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0] + loc[0], v.co.y * size[1] + loc[1], v.co.z * size[2] + loc[2]))
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
    """여러 오브젝트를 하나의 메쉬로 합침 (재질 슬롯 유지)"""
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

def cell_center(i, j):
    return Vector(((i + 0.5) * CELL_X, (j + 0.5) * CELL_Y, 0))

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

def add_noise_bump(nt, b, scale=200.0, strength=0.05, detail=6.0, rough_var=None):
    tc = nt.nodes.new("ShaderNodeTexCoord")
    nz = nt.nodes.new("ShaderNodeTexNoise"); setin(nz, "Scale", scale); setin(nz, "Detail", detail)
    nt.links.new(tc.outputs["Object"], nz.inputs["Vector"])
    bp = nt.nodes.new("ShaderNodeBump"); setin(bp, "Strength", strength)
    nt.links.new(nz.outputs["Fac"], bp.inputs["Height"])
    nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
    if rough_var:
        mr = nt.nodes.new("ShaderNodeMapRange")
        setin(mr, "To Min", rough_var[0]); setin(mr, "To Max", rough_var[1])
        nt.links.new(nz.outputs["Fac"], mr.inputs["Value"])
        nt.links.new(mr.outputs["Result"], b.inputs["Roughness"])
    return nz

def build_materials():
    M = {}
    m, nt, b = principled("Aluminium", (0.86, 0.87, 0.88), metal=1.0, rough=0.28, Anisotropic=0.6)
    add_noise_bump(nt, b, scale=900, strength=0.03, rough_var=(0.22, 0.34)); M["alu"] = m
    m, nt, b = principled("RailAnodized", (0.42, 0.43, 0.45), metal=0.9, rough=0.32)
    add_noise_bump(nt, b, scale=600, strength=0.04, rough_var=(0.25, 0.45)); M["rail"] = m
    m, nt, b = principled("BinPlastic", (0.105, 0.11, 0.12), rough=0.45, Specular_IOR_Level=0.45)
    add_noise_bump(nt, b, scale=350, strength=0.06, rough_var=(0.38, 0.55)); M["bin"] = m
    m, nt, b = principled("BinPlasticBlue", (0.03, 0.14, 0.42), rough=0.42)
    add_noise_bump(nt, b, scale=350, strength=0.06, rough_var=(0.36, 0.5)); M["bin_blue"] = m
    m, nt, b = principled("RobotPaintRed", (0.62, 0.015, 0.02), rough=0.32, Coat_Weight=0.8, Coat_Roughness=0.06)
    add_noise_bump(nt, b, scale=120, strength=0.015); M["red"] = m
    m, nt, b = principled("RobotTopBlack", (0.025, 0.025, 0.028), rough=0.55)
    add_noise_bump(nt, b, scale=500, strength=0.08); M["dark"] = m
    M["rubber"] = principled("Rubber", (0.02, 0.02, 0.02), rough=0.85)[0]
    M["steel"] = principled("Steel", (0.6, 0.6, 0.62), metal=1.0, rough=0.25)[0]
    M["yellow"] = principled("SafetyYellow", (0.9, 0.62, 0.02), rough=0.5)[0]
    M["led_green"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=12.0)[0]
    M["led_blue"] = principled("LEDBlue", (0.2, 0.5, 1.0), rough=0.3, Emission_Color=(0.2, 0.5, 1.0, 1), Emission_Strength=6.0)[0]
    m, nt, b = principled("PowderCoatWhite", (0.78, 0.79, 0.80), rough=0.55)
    add_noise_bump(nt, b, scale=700, strength=0.08); M["port"] = m
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.15, 0.45, 0.9, 1), Emission_Strength=2.5)[0]
    M["glass"] = principled("Glass", (0.9, 0.95, 1.0), rough=0.02, Transmission_Weight=1.0)[0]
    # 콘크리트 바닥: 두 톤 노이즈 + 거칠기 변화 + 미세 범프
    m, nt, b = principled("PolishedConcrete", (0.4, 0.4, 0.4), rough=0.4)
    tc = nt.nodes.new("ShaderNodeTexCoord")
    n1 = nt.nodes.new("ShaderNodeTexNoise"); setin(n1, "Scale", 2.5); setin(n1, "Detail", 10.0); setin(n1, "Roughness", 0.65)
    nt.links.new(tc.outputs["Object"], n1.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.19, 0.19, 0.19, 1); ramp.color_ramp.elements[1].color = (0.34, 0.335, 0.33, 1)
    nt.links.new(n1.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    add_noise_bump(nt, b, scale=60, strength=0.05, rough_var=(0.18, 0.55)); M["floor"] = m
    m, nt, b = principled("WallPanel", (0.30, 0.33, 0.37), metal=0.3, rough=0.5); M["wall"] = m
    M["ceiling"] = principled("Ceiling", (0.08, 0.08, 0.09), rough=0.8)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.97, 0.92, 1), Emission_Strength=8.0)[0]
    return M

# ---------------------------------------------------------------- 에셋
def make_bin(M, coll, name="Bin", material=None):
    """리브와 상단 림이 있는 빈 (원점 = 바닥 중심)"""
    mat_ = material or M["bin"]
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    taper = 0.012  # 아래로 갈수록 살짝 좁아짐 (적층 빈 형태)
    for v in bm.verts:
        z = (v.co.z + 0.5) * (BIN_H - 0.02)
        s = 1.0 if v.co.z > 0 else 1.0 - taper
        v.co = Vector((v.co.x * (BIN_L - 0.016) * s, v.co.y * (BIN_W - 0.016) * s, z))
    top = [f for f in bm.faces if f.normal.z > 0.9]
    bmesh.ops.delete(bm, geom=top, context="FACES_ONLY")
    bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.006)
    bm.to_mesh(me); bm.free()
    shell = bpy.data.objects.new(name + "_shell", me); me.materials.append(mat_); link_coll(shell, coll)
    parts = [shell]
    # 상단 림 (바깥으로 나온 테두리)
    rz = BIN_H - 0.012
    for sy in (-1, 1):
        parts.append(box("rim", (BIN_L, 0.012, 0.024), (0, sy * (BIN_W / 2 - 0.006), rz), mat_, coll))
    for sx in (-1, 1):
        parts.append(box("rim", (0.012, BIN_W, 0.024), (sx * (BIN_L / 2 - 0.006), 0, rz), mat_, coll))
    # 세로 리브
    for i in range(7):
        x = -BIN_L / 2 + 0.06 + i * (BIN_L - 0.12) / 6
        for sy in (-1, 1):
            parts.append(box("rib", (0.008, 0.008, BIN_H - 0.05), (x, sy * (BIN_W / 2 - 0.007), (BIN_H - 0.05) / 2 + 0.01), mat_, coll))
    for i in range(5):
        y = -BIN_W / 2 + 0.05 + i * (BIN_W - 0.10) / 4
        for sx in (-1, 1):
            parts.append(box("rib", (0.008, 0.008, BIN_H - 0.05), (sx * (BIN_L / 2 - 0.007), y, (BIN_H - 0.05) / 2 + 0.01), mat_, coll))
    ob = join(parts, name)
    return ob

def make_robot(M, coll, name="Robot"):
    """R5 계열 단일 셀 로봇 근사. 원점 = 레일 상면 중심. 자식 'Gripper' = 리프팅 프레임"""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    Lx, Ly = CELL_X - 0.04, CELL_Y - 0.03
    cav_h = BIN_H + 0.06
    top0 = 0.05 + cav_h
    t = 0.022
    P = []
    P.append(bevel(box("p1", (Lx, t, cav_h), (0, Ly/2 - t/2, 0.05 + cav_h/2), M["red"], coll), 0.006))
    P.append(bevel(box("p2", (Lx, t, cav_h), (0, -Ly/2 + t/2, 0.05 + cav_h/2), M["red"], coll), 0.006))
    P.append(bevel(box("p3", (t, Ly, cav_h), (Lx/2 - t/2, 0, 0.05 + cav_h/2), M["red"], coll), 0.006))
    P.append(bevel(box("p4", (t, Ly, cav_h), (-Lx/2 + t/2, 0, 0.05 + cav_h/2), M["red"], coll), 0.006))
    P.append(bevel(box("top", (Lx, Ly, 0.15), (0, 0, top0 + 0.075), M["red"], coll), 0.012))
    P.append(bevel(box("cap", (Lx - 0.05, Ly - 0.05, 0.03), (0, 0, top0 + 0.16), M["dark"], coll), 0.008))
    # 상판 통풍 홈
    for i in range(6):
        P.append(box("vent", (0.012, Ly - 0.16, 0.006), (-0.15 + i * 0.06, 0, top0 + 0.177), M["rubber"], coll))
    # 측면 범퍼
    for sy in (-1, 1):
        P.append(bevel(box("bump", (Lx + 0.01, 0.018, 0.035), (0, sy * (Ly/2 + 0.004), 0.07), M["rubber"], coll), 0.005))
    body = join(P, name + "_Body"); body.parent = root
    for p in body.data.polygons: p.use_smooth = False
    # 안테나
    ant = cyl(name + "_Antenna", 0.006, 0.12, (Lx/2 - 0.06, Ly/2 - 0.06, top0 + 0.235), (0, 0, 0), M["rubber"], coll, root, 10)
    # 바퀴: X 세트(긴 변) + Y 세트(짧은 변)
    W = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            W.append(cyl("wx", 0.05, 0.028, (sx * (Lx/2 - 0.11), sy * (Ly/2 + 0.004), 0.05), (math.pi/2, 0, 0), M["rubber"], coll))
            W.append(cyl("hx", 0.022, 0.032, (sx * (Lx/2 - 0.11), sy * (Ly/2 + 0.004), 0.05), (math.pi/2, 0, 0), M["steel"], coll))
            W.append(cyl("wy", 0.05, 0.028, (sx * (Lx/2 + 0.004), sy * (Ly/2 - 0.09), 0.05), (0, math.pi/2, 0), M["rubber"], coll))
            W.append(cyl("hy", 0.022, 0.032, (sx * (Lx/2 + 0.004), sy * (Ly/2 - 0.09), 0.05), (0, math.pi/2, 0), M["steel"], coll))
    wheels = join(W, name + "_Wheels"); wheels.parent = root
    for p in wheels.data.polygons: p.use_smooth = True
    # 상태 LED 스트립 (앞/뒤)
    L = [box("l1", (Lx * 0.6, 0.006, 0.014), (0, -Ly/2 - 0.002, top0 + 0.11), M["led_green"], coll),
         box("l2", (Lx * 0.6, 0.006, 0.014), (0, Ly/2 + 0.002, top0 + 0.11), M["led_green"], coll)]
    led = join(L, name + "_LED"); led.parent = root
    # 그리퍼
    G = [bevel(box("gf", (BIN_L - 0.03, BIN_W - 0.03, 0.025), (0, 0, 0), M["dark"], coll), 0.004)]
    for sx in (-1, 1):
        for sy in (-1, 1):
            G.append(box("gp", (0.025, 0.025, 0.05), (sx * (BIN_L/2 - 0.04), sy * (BIN_W/2 - 0.04), -0.035), M["yellow"], coll))
    grip = join(G, "Gripper")
    grip.location = (0, 0, 0.05 + cav_h - 0.02); grip.parent = root
    # 승강 벨트 4가닥 (그리퍼를 따라 늘어남 → 애니메이션 시 Z 스케일)
    return root

def make_port(M, coll, name="Port"):
    """컨베이어 포트 근사. 원점 = 포트 셀 바닥 중심. 작업대는 -Y(그리드 바깥)."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    P = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            P.append(bevel(box("leg", (0.05, 0.05, PORT_Z), (sx * 0.32, sy * 0.22, PORT_Z/2), M["port"], coll), 0.004))
    P.append(bevel(box("deck", (CELL_X - 0.02, CELL_Y - 0.02, 0.04), (0, 0, PORT_Z - 0.03), M["port"], coll), 0.006))
    P.append(bevel(box("cab", (CELL_X + 0.30, 0.50, 0.88), (0, -0.52, 0.44), M["port"], coll), 0.015))
    P.append(bevel(box("top", (CELL_X + 0.34, 0.56, 0.035), (0, -0.52, 0.895), M["dark"], coll), 0.006))
    P.append(bevel(box("hood", (CELL_X + 0.30, 0.05, 1.0), (0, -0.25, PORT_Z + 0.70), M["port"], coll), 0.01))
    P.append(bevel(box("side", (0.05, 0.50, 1.6), (CELL_X/2 + 0.12, -0.02, 0.80 + 0.6), M["port"], coll), 0.01))
    P.append(bevel(box("side", (0.05, 0.50, 1.6), (-CELL_X/2 - 0.12, -0.02, 0.80 + 0.6), M["port"], coll), 0.01))
    # 킥플레이트
    P.append(box("kick", (CELL_X + 0.30, 0.02, 0.12), (0, -0.775, 0.06), M["dark"], coll))
    frame = join(P, name + "_Frame"); frame.parent = root
    scr = bevel(box(name + "_Screen", (0.42, 0.025, 0.27), (0, -0.30, PORT_Z + 0.78), M["screen"], coll), 0.004)
    scr.rotation_euler = (math.radians(-12), 0, 0); scr.parent = root
    # 라이트 커튼 기둥 + 신호등
    lamp = box(name + "_Light", (0.05, 0.05, 0.10), (CELL_X/2 + 0.12, -0.25, 2.0 + 0.05), M["led_green"], coll); lamp.parent = root
    return root

def make_grid(M, coll):
    P = []
    H = TOP_Z
    def posts_at(i, j):
        P.append(bevel(box("post", (POST, POST, H), (i * CELL_X, j * CELL_Y, H/2), M["alu"], coll), 0.004))
    for i in range(GRID_X + 1):
        for j in range(GRID_Y + 1):
            posts_at(i, j)
    # 상부 레일: 양방향 이중 트랙 (가운데 홈)
    for j in range(GRID_Y + 1):
        for sy in (-1, 1):
            P.append(box("rx", (GRID_X * CELL_X + POST, 0.022, RAIL_H), (GRID_X*CELL_X/2, j*CELL_Y + sy*0.014, H - RAIL_H/2), M["rail"], coll))
    for i in range(GRID_X + 1):
        for sx in (-1, 1):
            P.append(box("ry", (0.022, GRID_Y * CELL_Y + POST, RAIL_H * 0.8), (i*CELL_X + sx*0.014, GRID_Y*CELL_Y/2, H - RAIL_H*0.4), M["rail"], coll))
    # 포트 셀 (그리드 앞으로 한 칸 돌출)
    for px in PORTS_X:
        x0 = px * CELL_X
        for sx in (0, 1):
            P.append(box("pr", (0.044, CELL_Y, RAIL_H * 0.8), (x0 + sx*CELL_X, -CELL_Y/2, H - RAIL_H*0.4), M["rail"], coll))
            P.append(bevel(box("pp", (POST, POST, H), (x0 + sx*CELL_X, -CELL_Y, H/2), M["alu"], coll), 0.004))
        P.append(box("pr", (CELL_X, 0.044, RAIL_H), (x0 + CELL_X/2, -CELL_Y, H - RAIL_H/2), M["rail"], coll))
    # 외곽 하부/중간 브레이싱
    for z in (0.15, H * 0.5):
        P.append(box("bx", (GRID_X*CELL_X, 0.02, 0.05), (GRID_X*CELL_X/2, -0.01, z), M["alu"], coll))
        P.append(box("bx", (GRID_X*CELL_X, 0.02, 0.05), (GRID_X*CELL_X/2, GRID_Y*CELL_Y + 0.01, z), M["alu"], coll))
        P.append(box("by", (0.02, GRID_Y*CELL_Y, 0.05), (-0.01, GRID_Y*CELL_Y/2, z), M["alu"], coll))
        P.append(box("by", (0.02, GRID_Y*CELL_Y, 0.05), (GRID_X*CELL_X + 0.01, GRID_Y*CELL_Y/2, z), M["alu"], coll))
    # 상부 서비스 난간 (그리드 둘레)
    for z in (H + 0.55, H + 1.05):
        P.append(box("hr", (GRID_X*CELL_X + 0.2, 0.03, 0.03), (GRID_X*CELL_X/2, GRID_Y*CELL_Y + 0.1, z), M["yellow"], coll))
        P.append(box("hr", (0.03, GRID_Y*CELL_Y + 0.2, 0.03), (GRID_X*CELL_X + 0.1, GRID_Y*CELL_Y/2, z), M["yellow"], coll))
    for i in range(0, GRID_X + 1, 3):
        P.append(box("hp", (0.03, 0.03, 1.05), (i*CELL_X, GRID_Y*CELL_Y + 0.1, H + 0.525), M["yellow"], coll))
    for j in range(0, GRID_Y + 1, 3):
        P.append(box("hp", (0.03, 0.03, 1.05), (GRID_X*CELL_X + 0.1, j*CELL_Y, H + 0.525), M["yellow"], coll))
    return join(P, "Grid")

def make_building(M, coll):
    cx, cy = GRID_X * CELL_X / 2, GRID_Y * CELL_Y / 2
    S = 40.0
    box("Floor", (S, S, 0.02), (cx, cy, -0.01), M["floor"], coll)
    # 바닥 안전선 (그리드 둘레 + 포트 작업 구역)
    for (sx, sy, x, y) in [(GRID_X*CELL_X + 1.6, 0.08, cx, -2.0), (GRID_X*CELL_X + 1.6, 0.08, cx, GRID_Y*CELL_Y + 0.8),
                           (0.08, GRID_Y*CELL_Y + 2.8, -0.8, cy - 0.6), (0.08, GRID_Y*CELL_Y + 2.8, GRID_X*CELL_X + 0.8, cy - 0.6)]:
        box("SafetyLine", (sx, sy, 0.004), (x, y, 0.002), M["yellow"], coll)
    # 벽 / 천장
    wall_h = 11.0
    box("WallBack", (S, 0.2, wall_h), (cx, cy + 12, wall_h/2), M["wall"], coll)
    box("WallLeft", (0.2, S, wall_h), (cx - 14, cy, wall_h/2), M["wall"], coll)
    box("WallRight", (0.2, S, wall_h), (cx + 16, cy, wall_h/2), M["wall"], coll)
    box("WallFront", (S, 0.2, wall_h), (cx, cy - 16, wall_h/2), M["wall"], coll)
    box("Ceiling", (S, S, 0.2), (cx, cy, wall_h), M["ceiling"], coll)
    # 벽 패널 줄눈
    for k in range(-12, 13, 2):
        box("WallRib", (0.06, 0.08, wall_h), (cx + k, cy + 11.88, wall_h/2), M["rail"], coll)
    # 천장 고천장 조명 (발광 패널 + 면광원)
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            x, y = cx + ix * 4.5, cy + iy * 4.5
            box("LightPanel", (1.4, 0.35, 0.05), (x, y, wall_h - 0.4), M["lightpanel"], coll)
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.4; ld.size_y = 0.35
            ld.energy = 550; ld.color = (1.0, 0.96, 0.9)
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
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_yup=True,
                              export_apply=True)

# ---------------------------------------------------------------- 카메라 / 렌더
def setup_camera(scene_coll, frames):
    cx, cy = GRID_X * CELL_X / 2, GRID_Y * CELL_Y / 2
    T = TOP_Z
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 5.6
    # 30초 카메라 동선 (프레임 비율로 정의 → fps와 무관)
    keys = [
        (0.00, (cx + 10.5, cy - 11.0, T + 3.0), (cx, cy, T * 0.45), 24),   # 전경
        (0.22, (cx + 4.0, -5.2, 1.9),          (PORTS_X[1]*CELL_X + 0.35, -0.4, 1.1), 32),  # 포트 근접
        (0.34, (cx + 1.0, -6.5, T * 0.75),      (cx, cy * 0.5, T * 0.8), 26),         # 그리드 앞에서 상승
        (0.48, (cx - 2.5, -3.2, T + 1.6),       (cx - 0.5, cy * 0.7, T + 0.2), 26),   # 그리드 상부
        (0.70, (cx + 2.5, cy + 0.5, T + 2.6),   (cx - 1.0, cy + 0.3, T), 22),        # 로봇 위를 비행
        (1.00, (cx - 9.5, cy + 9.0, T + 4.0),   (cx, cy, T * 0.5), 24),             # 반대편 전경
    ]
    for frac, loc, look, lens in keys:
        f = 1 + round(frac * (frames - 1))
        cam.location = loc; cam.keyframe_insert("location", frame=f)
        tgt.location = look; tgt.keyframe_insert("location", frame=f)
        cam_d.lens = lens; cam_d.keyframe_insert("lens", frame=f)
    return cam

def setup_render(out, a, frames, fps):
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
                    sc.cycles.device = "GPU"; print("[AutoStore] GPU:", dev); break
            except Exception:
                continue
    else:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.55, 0.58, 0.62, 1); bg.inputs[1].default_value = 0.12

# ---------------------------------------------------------------- 애니메이션 (궤적)
def apply_trace(trace, robots, bins, ports):
    fps = trace["fps"]
    frames = trace["frames"] + 1
    # 로봇
    for ri, track in enumerate(trace["robots"]):
        if ri >= len(robots): break
        root = robots[ri]
        grip = next(c for c in root.children if c.name.startswith("Gripper"))
        g0 = grip.location.z
        for f, (x, y, g, carry, state) in enumerate(track):
            root.location = (x, y, TOP_Z); root.keyframe_insert("location", frame=f + 1)
            grip.location.z = g0 - g; grip.keyframe_insert("location", index=2, frame=f + 1)
    # 움직이는 빈
    for sid, keys in trace["binTracks"].items():
        ob = bins[int(sid)]
        for (f, x, y, z) in keys:
            ob.location = (x, y, z); ob.keyframe_insert("location", frame=max(1, f + 1))
    # 키프레임 보간: 프레임 단위 샘플이므로 선형
    for ob in list(robots) + [c for r in robots for c in r.children] + list(bins.values()):
        ad = ob.animation_data
        if ad and ad.action:
            for fc in iter_fcurves(ad.action):
                for kp in fc.keyframe_points: kp.interpolation = "LINEAR"
    return frames, fps

def iter_fcurves(action):
    if hasattr(action, "fcurves") and action.fcurves is not None:
        try:
            return list(action.fcurves)
        except Exception:
            pass
    out = []   # Blender 4.4+/5.x 레이어드 액션
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", []):
                out.extend(bag.fcurves)
    return out

# ---------------------------------------------------------------- 메인
def main():
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(SEED)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()
    trace = json.load(open(a["trace"])) if a["trace"] else None

    # --- 개별 에셋 (Unity / 웹용 내보내기)
    assets = new_coll("Assets")
    a_robot = make_robot(M, assets, "AutoStoreRobot")
    a_bin = make_bin(M, assets, "AutoStoreBin")
    a_port = make_port(M, assets, "AutoStorePort")
    if not a["no-export"]:
        for ob, nm in ((a_robot, "robot"), (a_bin, "bin"), (a_port, "port")):
            export_fbx(ob, os.path.join(out, f"autostore_{nm}.fbx"))
            export_glb(ob, os.path.join(out, f"autostore_{nm}.glb"))
    bin_mesh = a_bin.data
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("AutoStoreScene")
    make_building(M, scene)
    make_grid(M, scene)
    bins_coll = new_coll("Bins", scene)
    blue = bin_mesh.copy(); blue.materials[0] = M["bin_blue"]
    bins = {}
    if trace:
        for id_, (x, y, z) in enumerate(trace["initialBins"]):
            ob = bpy.data.objects.new(f"Bin_{id_}", blue if random.random() < 0.05 else bin_mesh)
            ob.location = (x, y, z); bins_coll.objects.link(ob); bins[id_] = ob
    else:
        n = 0
        for i in range(GRID_X):
            for j in range(GRID_Y):
                h = min(GRID_Z - 1, int(GRID_Z * min(1.0, max(0.0, random.gauss(FILL, 0.12)))))
                for k in range(h):
                    ob = bpy.data.objects.new(f"Bin_{n}", blue if random.random() < 0.05 else bin_mesh)
                    ob.location = cell_center(i, j) + Vector((0, 0, k * BIN_H + 0.005))
                    bins_coll.objects.link(ob); bins[n] = ob; n += 1
    ports = []
    for idx, px in enumerate(PORTS_X):
        p = make_port(M, scene, f"Port_{idx}"); p.location = cell_center(px, -1); ports.append(p)
    robots = []
    nrob = len(trace["robots"]) if trace else len(ROBOTS)
    for idx in range(nrob):
        r = make_robot(M, scene, f"Robot_{idx}")
        if not trace:
            rx, ry = ROBOTS[idx]; r.location = cell_center(rx, ry) + Vector((0, 0, TOP_Z))
        robots.append(r)

    frames, fps = (apply_trace(trace, robots, bins, ports) if trace else (900, 30))
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(out, a, frames, fps)
    print(f"[AutoStore] bins={len(bins)} robots={len(robots)} ports={len(ports)} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "autostore_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"autostore_still_{f:04d}.png")
            bpy.ops.render.render(write_still=True)
    if a["anim"]:
        if a["frames"]:
            s, e = [int(v) for v in a["frames"].split("-")]; sc.frame_start, sc.frame_end = s, e
        fdir = os.path.join(out, "frames"); os.makedirs(fdir, exist_ok=True)
        sc.render.image_settings.file_format = "PNG"
        sc.render.filepath = os.path.join(fdir, "f_")
        sc.render.use_overwrite = False; sc.render.use_placeholder = True   # 여러 프로세스 병렬 렌더/이어하기 가능
        step = int(a["step"]); sc.frame_step = step
        bpy.ops.render.render(animation=True)
        ff = shutil.which("ffmpeg")
        if ff:
            mp4 = os.path.join(out, "autostore_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[AutoStore] video ->", mp4)
        else:
            print("[AutoStore] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[AutoStore] done ->", out)

if __name__ == "__main__":
    main()
