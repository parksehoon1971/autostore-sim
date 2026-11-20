"""
다층 셔틀랙 (멀티 레벨 토트 셔틀 AS/RS) — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles / EEVEE). 특정 제조사 제품이 아닌 일반 구성이며 로고가 없습니다.

사용 예 (저장소 루트에서)
  node tools/export_shuttle_trace.js                     # blender/shuttle_trace.json + Unity 카메라 JSON 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/shuttle_blender.py -- --out blender/out/shuttle --trace blender/shuttle_trace.json --still 1,300,480,600,720
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/shuttle_blender.py -- --out blender/out/shuttle --trace blender/shuttle_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_shuttle_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더 (GPU 없는 PC에서 빠름)
  --no-export        FBX/GLB 내보내기 생략

치수·속도는 공개 자료의 일반적인 값 (셔틀 4 m/s, 2 m/s², 토트 600 x 400 mm) 과 가정입니다 (docs/shuttle-research.md).
겹치는 면(z-fighting)이 없도록 바닥 표시·띠·라벨은 모두 2 mm 이상 띄웠습니다.
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
C = dict(aisles=3, nx=26, levels=14, bufBays=2, bayW=0.70, aisleW=0.72, rackD=0.46, flue=0.06, level0Z=0.55, levelH=0.42,
         toteW=0.60, toteD=0.40, toteH=0.28, liftX=-0.75, xferOff=0.62, convZ=0.75, stationX=-4.6)
PITCH, DEPTH, REACH = 1.70, 4.98, 0.59
UPR_X, UPR_Y = 0.05, 0.07              # 랙 기둥 단면 (x 폭, 깊이)
SH_L, SH_W, SH_H = 0.86, 0.66, 0.12    # 셔틀 길이(x), 폭(y), 토트 아래 몸체 높이
MAST_X, MAST_Y = 0.78, 0.62            # 리프트 탑 바닥 크기

def wx(b): return (b + 0.5) * C["bayW"]
def buf_x(): return wx(C["bufBays"] - 1)
def aisle_y(a): return C["rackD"] + C["aisleW"] / 2 + a * PITCH
def level_z(l): return C["level0Z"] + l * C["levelH"]
def GW(): return C["nx"] * C["bayW"]
def top_z(): return level_z(C["levels"] - 1) + C["toteH"] + 0.2

# ---------------------------------------------------------------- 유틸 (skypod_blender.py 와 같은 방식)
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
    m, nt, b = principled("Galvanized", (0.62, 0.64, 0.66), metal=1.0, rough=0.38)
    add_noise_bump(nt, b, scale=35, strength=0.02, rough_var=(0.28, 0.5)); M["galv"] = m
    M["rail"] = principled("RailSteel", (0.55, 0.57, 0.59), metal=1.0, rough=0.42)[0]
    M["beam"] = principled("BeamOrange", (0.80, 0.28, 0.04), rough=0.4, Coat_Weight=0.3)[0]      # 랙 가로보 (흔한 주황 도장)
    M["frame"] = principled("FrameBlue", (0.06, 0.18, 0.40), rough=0.4, Coat_Weight=0.3)[0]      # 기둥 프레임 (청색 도장)
    # 토트: 청회색 PP (미세 요철)
    m, nt, b = principled("TotePP", (0.16, 0.30, 0.52), rough=0.45)
    add_noise_bump(nt, b, scale=400, strength=0.03, rough_var=(0.38, 0.52)); M["tote"] = m
    M["toteDark"] = principled("ToteRim", (0.08, 0.15, 0.28), rough=0.5)[0]
    M["label"] = principled("ToteLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["barcode"] = principled("Barcode", (0.02, 0.02, 0.02), rough=0.6)[0]
    # 셔틀: 연회색 몸체 + 진회색 섀시 + 앰버 띠
    m, nt, b = principled("ShuttleBody", (0.82, 0.83, 0.84), rough=0.3, Coat_Weight=0.5, Coat_Roughness=0.12)
    add_noise_bump(nt, b, scale=200, strength=0.01); M["body"] = m
    M["chassis"] = principled("ChassisGrey", (0.10, 0.105, 0.11), metal=0.3, rough=0.45)[0]
    M["accent"] = principled("AccentAmber", (0.95, 0.55, 0.05), rough=0.35, Coat_Weight=0.4)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["hub"] = principled("HubAlu", (0.70, 0.71, 0.73), metal=1.0, rough=0.3)[0]
    M["lens"] = principled("SensorGlass", (0.01, 0.01, 0.012), rough=0.05, Coat_Weight=1.0)[0]
    M["led"] = principled("LEDCyan", (0.2, 0.8, 1.0), rough=0.3, Emission_Color=(0.2, 0.8, 1.0, 1), Emission_Strength=8.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["ledGreen"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=8.0)[0]
    # 컨베이어 / 리프트
    M["roller"] = principled("RollerZinc", (0.72, 0.73, 0.75), metal=1.0, rough=0.25)[0]
    M["convFrame"] = principled("ConvFrame", (0.30, 0.31, 0.33), metal=0.6, rough=0.4)[0]
    M["belt"] = principled("BeltBlack", (0.03, 0.03, 0.032), rough=0.7)[0]
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

# ---------------------------------------------------------------- 에셋: 셔틀
def make_shuttle(M, coll, name="Shuttle"):
    """단층 셔틀 근사: 낮은 몸체 위에 토트가 실리고, 앞뒤 끝 하우징, 통로 레일 위 바퀴 4개,
    토트 양 끝을 잡는 텔레스코픽 암 2개 (자식 'Arm', y 로 움직임).
    원점 = 토트 바닥 중심 (셔틀 위 토트 자리), 진행 방향 = +X."""
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    L, W, H = SH_L, SH_W, SH_H
    gy = C["aisleW"] / 2 - 0.03          # 레일 y
    P = Parts()
    P.box((L - 0.04, W - 0.08, H - 0.03), (0, 0, -H / 2 - 0.01), M["chassis"])          # 몸체 (토트 아래)
    P.box((L - 0.30, W - 0.10, 0.012), (0, 0, -0.008), M["body"])                         # 토트 받침판 (윗면 = -0.002)
    for sx in (-1, 1):
        hx = sx * (L / 2 - 0.06)
        P.box((0.12, W - 0.04, H + 0.10), (hx, 0, (0.10 - H) / 2), M["body"])              # 앞뒤 하우징 (모터·제어부)
        P.box((0.004, W - 0.20, 0.022), (hx + sx * 0.062, 0, 0.03), M["accent"])           # 앰버 띠
        P.box((0.004, 0.10, 0.03), (hx + sx * 0.062, 0, -0.05), M["lens"])                 # 거리 센서 창
        P.box((0.10, 0.004, 0.012), (hx, (W - 0.04) / 2 + 0.002, 0.07), M["led"])          # 상태 LED
        P.box((0.10, 0.004, 0.012), (hx, -(W - 0.04) / 2 - 0.002, 0.07), M["led"])
    body = P.build(name + "_Body", coll, bev=0.005); body.parent = root
    G = Parts()
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = sx * (L / 2 - 0.13), sy * gy
            G.cyl(0.045, 0.03, (x, y, -H + 0.045), M["rubber"], axis="Y", seg=18)            # 레일 위 바퀴 (바닥 = 레일 윗면)
            G.cyl(0.022, 0.032, (x, y, -H + 0.045), M["hub"], axis="Y", seg=12)
    wheels = G.build(name + "_Wheels", coll); wheels.parent = root
    # 텔레스코픽 암: 토트 양 끝 (x) 에서 y 로 펴짐. 끝 손가락으로 토트를 당기고 밂
    A = Parts()
    for sx in (-1, 1):
        ax = sx * (C["toteW"] / 2 + 0.0095)     # 토트와 기둥 사이 25 mm 틈 안
        A.box((0.014, C["toteD"] + 0.04, 0.06), (ax, 0, 0.06), M["alu"])
        A.box((0.006, C["toteD"], 0.03), (ax + sx * 0.010, 0, 0.075), M["chassis"])
        for sy in (-1, 1):
            A.box((0.012, 0.014, 0.035), (ax, sy * (C["toteD"] / 2 + 0.012), 0.10), M["black"])
    arm = A.build("Arm", coll); arm.parent = root
    return root

# ---------------------------------------------------------------- 에셋: 리프트 캐리지 (원점 = 토트 바닥 중심)
def make_carriage(M, coll, name="LiftCarriage"):
    root = bpy.data.objects.new(name, None); link_coll(root, coll)
    P = Parts()
    w, d = C["toteW"] + 0.06, C["toteD"] + 0.10
    P.box((w, d, 0.09), (0, 0, -0.052), M["convFrame"])
    for k in range(8):
        P.cyl(0.022, d - 0.06, (-w / 2 + 0.05 + k * (w - 0.10) / 7, 0, -0.024), M["roller"], axis="Y", seg=10)
    for sy in (-1, 1):
        P.box((w + 0.04, 0.03, 0.12), (0, sy * (d / 2 + 0.015), -0.02), M["frame"])
        P.box((0.06, 0.04, 0.30), (0, sy * (d / 2 + 0.04), 0.0), M["chassis"])           # 마스트 안내 블록
        P.box((w - 0.1, 0.004, 0.014), (0, sy * (d / 2 + 0.032), 0.03), M["accent"])
    P.build(name + "_Body", coll, bev=0.003).parent = root
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
            P.box((0.012, 0.008, H * 0.7), (k * 0.11, sy * (d0 / 2 + 0.006), H * 0.48), M["tote"])
        P.box((0.10, 0.003, 0.055), (-0.17, sy * (d1 / 2 + 0.004), H - 0.11), M["label"])
        P.box((0.07, 0.002, 0.016), (-0.17, sy * (d1 / 2 + 0.0065), H - 0.12), M["barcode"])
    for sx in (-1, 1):
        P.box((0.010, d1 + 0.014, 0.02), (sx * (w1 / 2 + 0.004), 0, H - 0.01), M["toteDark"])
        P.box((0.004, 0.14, 0.03), (sx * (w1 / 2 + 0.002), 0, H - 0.05), M["toteDark"])     # 손잡이 홈 (짧은 면)
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
    for vi, fl in enumerate([[], ["Cardboard", "ProdRed", "CardboardWhite", "ProdBlue"]]):
        parts = tote_shell(f"Tote_v{vi}", M, coll, H)
        x = -C["toteW"] / 2 + 0.03
        for nm in fl:
            sx = rnd.uniform(0.10, 0.16); sy = rnd.uniform(0.18, C["toteD"] - 0.06); sz = rnd.uniform(0.07, H * 0.75)
            if x + sx > C["toteW"] / 2 - 0.03: break
            P = Parts(); P.box((sx, sy, sz), (x + sx / 2, rnd.uniform(-0.03, 0.03), 0.008 + sz / 2), M[nm])
            parts.append(P.build("item", coll, bev=0.004)); x += sx + 0.01
        out.append(join(parts, f"ShuttleTote_v{vi}"))
    return out

# ---------------------------------------------------------------- 랙 + 셔틀 레일 + 버퍼 컨베이어
def roller_bed(P, M, x0, x1, y, z_top, width, pitch=0.075, legs=False):
    """x 방향 롤러 컨베이어 (윗면 = z_top - 0.001: 토트 바닥과 1 mm 띄움)"""
    L = x1 - x0; xc = (x0 + x1) / 2; r = 0.022
    zc = z_top - 0.001 - r
    for sy in (-1, 1):
        P.box((L, 0.035, 0.09), (xc, y + sy * (width / 2 + 0.018), zc - 0.02), M["convFrame"])
    n = max(2, int(L / pitch))
    for k in range(n):
        P.cyl(r, width, (x0 + (k + 0.5) * L / n, y, zc), M["roller"], axis="Y", seg=8)
    if legs:
        for xx in [x0 + 0.1 + i * (L - 0.2) / max(1, int(L / 1.6)) for i in range(int(L / 1.6) + 1)]:
            for sy in (-1, 1):
                P.box((0.04, 0.04, zc - 0.06), (xx, y + sy * (width / 2 + 0.018), (zc - 0.06) / 2), M["convFrame"])

def make_racks(M, coll):
    nx, bW, nl = C["nx"], C["bayW"], C["levels"]
    zt = top_z(); gw = GW()
    P = Parts(); R = Parts(); B = Parts()
    for a in range(C["aisles"]):
        ya = aisle_y(a)
        for sgn in (-1, 1):
            yf = ya + sgn * C["aisleW"] / 2           # 통로 쪽 면
            yb = yf + sgn * C["rackD"]                # 랙 뒤 끝
            for k in range(C["bufBays"], nx + 1):        # 버퍼 칸 (bay 0~1) 에는 기둥 없이 컨베이어만
                x = k * bW
                for yy in (yf + sgn * UPR_Y / 2, yb - sgn * UPR_Y / 2):
                    P.box((UPR_X, UPR_Y, zt), (x, yy, zt / 2), M["frame"])
                    P.box((0.12, 0.12, 0.010), (x, yy, 0.005), M["galv"])
                # 기둥 사이 가새 (측면 프레임, 1.5 m 마다)
                for z in range(1, int(zt / 1.5) + 1):
                    P.box((0.025, C["rackD"] - 2 * UPR_Y, 0.025), (x, (yf + yb) / 2, z * 1.5), M["galv"])
            for l in range(nl):
                z = level_z(l)
                for k in range(C["bufBays"], nx):
                    xc = wx(k)
                    # 토트 받침 L 앵글 (깊이 방향, 칸 양쪽): 윗면 = z - 0.002
                    for dx in (-1, 1):
                        R.box((0.04, C["rackD"] - 0.02, 0.006), (xc + dx * (bW / 2 - 0.055), (yf + yb) / 2, z - 0.005), M["rail"])
                # 단 가로보 (앞·뒤)
                x0b = C["bufBays"] * bW
                for yy in (yf + sgn * 0.02, yb - sgn * 0.02):
                    R.box((gw - x0b, 0.03, 0.05), ((gw + x0b) / 2, yy, z - 0.035), M["beam"])
            P.box((gw + UPR_X, 0.06, 0.10), (gw / 2, yf + sgn * 0.035, zt + 0.05), M["beam"])
        # 셔틀 레일 (단마다 통로 양쪽): 윗면 = 셔틀 바퀴 아래
        gy = C["aisleW"] / 2 - 0.03
        for l in range(nl):
            zr = level_z(l) - SH_H - 0.012
            for sy in (-1, 1):
                R.box((gw, 0.05, 0.024), (gw / 2, ya + sy * gy, zr), M["rail"])
                R.box((gw, 0.006, 0.05), (gw / 2, ya + sy * (gy + 0.028), zr - 0.01), M["rail"])
            # 버퍼 컨베이어 (bay 0~1, 양쪽): 출고 = -y, 입고 = +y
            for sy in (-1, 1):
                roller_bed(B, M, C["liftX"] + C["xferOff"] - 0.27, C["bufBays"] * bW - 0.04, ya + sy * REACH, level_z(l), C["toteD"] + 0.06)
    # 등지는 랙 사이 연결대
    for a in range(C["aisles"] - 1):
        y = aisle_y(a) + C["aisleW"] / 2 + C["rackD"] + C["flue"] / 2
        for k in range(0, nx + 1, 2):
            for z in (2.0, 4.0, 6.0):
                if z < zt: P.box((0.04, C["flue"] + 2 * UPR_Y + 0.02, 0.04), (k * bW, y, z), M["galv"])
    P.build("RackFrames", coll)
    R.build("RackRails", coll)
    B.build("BufferConveyors", coll)

# ---------------------------------------------------------------- 리프트 탑 + 1층 컨베이어 + 스테이션
def make_front(M, coll):
    lights = []
    P = Parts(); Q = Parts()
    zt = top_z() + 0.35
    xl, xs, zc = C["liftX"], C["stationX"], C["convZ"]
    for a in range(C["aisles"]):
        ya = aisle_y(a)
        for sy in (-1, 1):
            y = ya + sy * REACH
            # 리프트 탑: 네 기둥 + 위 프레임 + 옆 가새
            for dx in (-1, 1):
                for dy in (-1, 1):
                    P.box((0.07, 0.07, zt), (xl + dx * MAST_X / 2, y + dy * MAST_Y / 2, zt / 2), M["frame"])
            for dz in (0.03, zt - 0.04):
                for dy in (-1, 1):
                    P.box((MAST_X + 0.07, 0.05, 0.06), (xl, y + dy * MAST_Y / 2, dz), M["frame"])
                for dx in (-1, 1):
                    P.box((0.05, MAST_Y + 0.07, 0.06), (xl + dx * MAST_X / 2, y, dz), M["frame"])
            for k in range(1, int(zt / 1.2)):
                P.box((0.02, 0.02, 0.02), (xl + MAST_X / 2, y, k * 1.2), M["galv"])
            # 안내 레일 (캐리지 양옆) + 구동 벨트
            for dy in (-1, 1):
                P.box((0.05, 0.03, zt - 0.1), (xl, y + dy * (C["toteD"] / 2 + 0.11), zt / 2), M["rail"])
            P.box((0.012, 0.03, zt - 0.2), (xl + MAST_X / 2 - 0.06, y + 0.12, zt / 2), M["belt"])
            # 위 구동 모터 + 상태등
            P.box((0.30, 0.30, 0.24), (xl, y, zt + 0.12), M["chassis"])
            P.cyl(0.03, 0.06, (xl + MAST_X / 2, y - MAST_Y / 2, zt + 0.06), M["ledGreen"], seg=12)
        # 1층 U자 컨베이어 (출고 → 스테이션 → 입고)
        yo, yi = ya - REACH, ya + REACH
        xg = xl - C["xferOff"]
        roller_bed(Q, M, xs + 0.31, xg + 0.27, yo, zc, C["toteD"] + 0.06, legs=True)
        roller_bed(Q, M, xs + 0.31, xg + 0.27, yi, zc, C["toteD"] + 0.06, legs=True)
        # 스테이션 피킹 칸 (y 방향 옮김 컨베이어): 벨트 줄
        for k in range(6):
            Q.box((0.025, 2 * REACH + C["toteD"] + 0.06, 0.012), (xs - 0.24 + k * 0.096, ya, zc - 0.007), M["belt"])
        Q.box((0.60, 2 * REACH + C["toteD"] + 0.10, 0.08), (xs, ya, zc - 0.06), M["convFrame"])
        for dx in (-1, 1):
            for dy in (-1, 1):
                Q.box((0.04, 0.04, zc - 0.1), (xs + dx * 0.25, ya + dy * (REACH + 0.2), (zc - 0.1) / 2), M["convFrame"])
        # 작업자 쪽: 카운터 + 화면 + 표시등 주문 선반 + 매트
        xo = xs - 0.55
        Q.box((0.40, 1.30, 0.04), (xo - 0.05, ya, 0.92), M["white"])
        Q.box((0.03, 1.30, 0.90), (xo + 0.14, ya, 0.45), M["white"])
        Q.box((0.20, 0.004, 0.03), (xo - 0.15, ya - 0.55, 0.942), M["ledGreen"])
        ysc = ya - REACH - 0.55
        Q.box((0.03, 0.38, 0.25), (xs - 0.2, ysc, 1.40), M["black"])               # 화면 (작업자 쪽 -x 를 향함)
        Q.box((0.004, 0.34, 0.21), (xs - 0.218, ysc, 1.40), M["screen"])
        Q.box((0.04, 0.04, 1.3), (xs - 0.18, ysc, 0.65), M["alu"])
        Q.box((1.1, 1.4, 0.012), (xo - 0.75, ya, 0.007), M["mat"])
        xp = xo - 1.55
        for lv, zz in enumerate((0.30, 0.80, 1.30)):
            Q.box((0.50, 1.40, 0.025), (xp, ya, zz), M["alu"])
            for k in range(3):
                y = ya + (k - 1) * 0.45
                Q.box((0.32, 0.40, 0.22), (xp, y, zz + 0.125), M["Cardboard"] if (k + lv) % 3 else M["CardboardWhite"])
                Q.box((0.004, 0.05, 0.02), (xp + 0.255, y, zz + 0.012), M["ledGreen" if (k + lv + a) % 4 == 0 else "black"])
        for yy in (ya - 0.72, ya + 0.72):
            for xx in (xp - 0.24, xp + 0.24):
                Q.box((0.035, 0.035, 1.6), (xx, yy, 0.8), M["alu"])
        # 피킹 토트를 비추는 스포트
        ld = bpy.data.lights.new(f"StationSpot_{a}", "SPOT"); ld.energy = 0.0; ld.spot_size = math.radians(45)
        ld.spot_blend = 0.6; ld.color = (1.0, 0.97, 0.9); ld.shadow_soft_size = 0.05
        lo = bpy.data.objects.new(f"StationSpot_{a}", ld); lo.location = (xs - 0.1, ya, 2.3); link_coll(lo, coll)
        lights.append(lo)
        Q.box((0.12, 0.9, 0.04), (xs - 0.1, ya, 2.32), M["lightpanel"])
        for dy in (-1, 1):
            Q.box((0.008, 0.008, 7.5), (xs - 0.1, ya + dy * 0.40, 2.34 + 3.75), M["alu"])   # 천장에 매단 줄
    P.build("LiftTowers", coll)
    Q.build("FloorConveyorsAndStations", coll)
    return lights

# ---------------------------------------------------------------- 바닥 표시 + 펜스 (모두 바닥에서 2 mm 이상 띄움)
def make_floor_marks(M, coll):
    P = Parts()
    gw, gd = GW(), DEPTH
    x0, x1 = C["stationX"] - 3.0, gw + 0.8
    for y in (-1.25, gd + 0.6):
        P.box((x1 - x0, 0.08, 0.002), ((x0 + x1) / 2, y, 0.003), M["yellow"])
    for x in (x0, C["liftX"] - 0.7):
        P.box((0.08, gd + 1.85, 0.002), (x, (gd - 0.65) / 2, 0.003), M["yellow"])
    # 보행로 줄무늬
    for k in range(int((x1 - 1.0) / 1.0)):
        P.box((0.35, 0.30, 0.002), (0.5 + k * 1.0, -1.55, 0.003), M["white"])
    def fence(xa, ya, xb, yb):
        L = math.hypot(xb - xa, yb - ya); n = max(1, int(L / 1.5))
        for i in range(n + 1):
            P.box((0.06, 0.06, 2.2), (xa + (xb - xa) * i / n, ya + (yb - ya) * i / n, 1.1), M["yellow"])
        alongx = abs(xb - xa) > abs(yb - ya)
        cx, cy = (xa + xb) / 2, (ya + yb) / 2
        for z in (0.15, 2.15):
            P.box((L if alongx else 0.03, 0.03 if alongx else L, 0.03), (cx, cy, z), M["yellow"])
        P.box((L if alongx else 0.008, 0.008 if alongx else L, 1.95), (cx, cy, 1.15), M["mesh"])
    fence(0.0, -0.9, gw + 0.6, -0.9)                              # 랙 앞 (보행로와 분리)
    fence(gw + 0.6, -0.9, gw + 0.6, gd + 0.9)
    P.build("FloorMarks", coll)

# ---------------------------------------------------------------- 건물 (밝은 물류센터)
def make_building(M, coll):
    cxw, cyw = GW() / 2 - 3, DEPTH / 2
    S = 60.0; Hh = 11.0
    P = Parts()
    P.box((S, S, 0.02), (cxw, cyw, -0.01), M["floor"])
    P.box((S * 0.8, 0.12, 0.002), (cxw, -6.2, 0.003), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 14, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 22, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 22, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 20, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-18, 20, 4):
        P.box((0.3, S, 0.6), (cxw + k, cyw, Hh - 0.4), M["steel"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 13.8, 1.2), M["greenline"])
    for k in range(-16, 20, 8):
        P.box((0.4, 0.4, Hh), (cxw + k, cyw + 13.7, Hh / 2), M["steel"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            L.box((1.8, 0.45, 0.05), (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.8), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-3, 4):
        for iy in range(-3, 4):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.8; ld.size_y = 0.45
            ld.energy = 550; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 5.0, cyw + iy * 5.0, Hh - 0.85); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라 / 렌더
def setup_camera(scene_coll, cam_track):
    """tools/export_shuttle_trace.js 가 만든 프레임별 카메라 (위치, 바라보는 점, 렌즈). Unity 와 같은 값."""
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36; cam_d.clip_start = 0.05
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 9.0
    fs = list(range(1, len(cam_track) + 1))
    for ax in range(3):
        bake_channel(cam, "location", ax, fs, [k[ax] for k in cam_track])
        bake_channel(tgt, "location", ax, fs, [k[3 + ax] for k in cam_track])
    bake_channel(cam_d, "lens", -1, fs, [k[6] for k in cam_track])
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
                    sc.cycles.device = "GPU"; print("[Shuttle] GPU:", dev); break
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

def bake_channel(tgt, path, index, frames, values, interp="LINEAR"):
    """키프레임을 한 번에 넣기 (keyframe_insert 반복보다 훨씬 빠름)"""
    if not frames: return
    if index >= 0:
        getattr(tgt, path)[index] = values[0]; tgt.keyframe_insert(path, index=index, frame=frames[0])
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

def apply_trace(shuttles, carriages, tote_meshes, spots, coll):
    tr = TRACE
    for si, track in enumerate(tr["shuttles"]):
        root = shuttles[si]
        arm = next(c for c in root.children if c.name.startswith("Arm"))
        fs, vs = compress([k[0] for k in track]); bake_channel(root, "location", 0, fs, vs)
        fs, vs = compress([k[1] * REACH for k in track]); bake_channel(arm, "location", 1, fs, vs)
    for li, track in enumerate(tr["lifts"]):
        fs, vs = compress([k[0] for k in track]); bake_channel(carriages[li], "location", 2, fs, vs)
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
                fs.append(f + 1); es.append(45.0 if s >= 0 else 0.0)
        bake_channel(lo.data, "energy", -1, fs, es, "CONSTANT")
    print(f"[Shuttle] totes={len(tr['initialTotes'])} moving={len(tracks)} hero={tr['hero']['tote']}")
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
    global TRACE, PITCH, DEPTH, REACH
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/shuttle_trace.json 이 필요합니다 (node tools/export_shuttle_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    PITCH, DEPTH, REACH = TRACE["pitch"], TRACE["depthY"], TRACE["reach"]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용)
    assets = new_coll("Assets")
    a_sh = make_shuttle(M, assets, "RackShuttle")
    a_car = make_carriage(M, assets, "LiftCarriage")
    tote_meshes = make_tote_meshes(M, assets)
    if not a["no-export"]:
        export_fbx(a_sh, os.path.join(out, "shuttle_shuttle.fbx"))
        export_glb(a_sh, os.path.join(out, "shuttle_shuttle.glb"))
        export_fbx(a_car, os.path.join(out, "shuttle_carriage.fbx"))
        for vi, ob in enumerate(tote_meshes):
            export_fbx(ob, os.path.join(out, f"shuttle_tote_v{vi}.fbx"), ("MESH",))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("ShuttleScene")
    make_building(M, scene)
    st = new_coll("Structure", scene)
    make_racks(M, st)
    make_floor_marks(M, st)
    spots = make_front(M, scene)
    shc = new_coll("Shuttles", scene)
    shuttles = []
    for si in range(len(TRACE["shuttles"])):
        ai, li = divmod(si, C["levels"])
        o = make_shuttle(M, shc, f"Shuttle_{ai}_{li}"); o.location = (buf_x(), aisle_y(ai), level_z(li)); shuttles.append(o)
    lc = new_coll("Lifts", scene)
    carriages = []
    for li in range(len(TRACE["lifts"])):
        ai, side = li // 2, (-1 if li % 2 == 0 else 1)
        o = make_carriage(M, lc, f"Carriage_{ai}_{'out' if side < 0 else 'in'}")
        o.location = (C["liftX"], aisle_y(ai) + side * REACH, C["convZ"]); carriages.append(o)
    tc = new_coll("Totes", scene)
    frames, fps = apply_trace(shuttles, carriages, tote_meshes, spots, tc)
    setup_camera(scene, TRACE["camera"])
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[Shuttle] shuttles={len(shuttles)} lifts={len(carriages)} aisles={C['aisles']} levels={C['levels']} frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "shuttle_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"shuttle_still_{f:04d}.png")
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
            mp4 = os.path.join(out, "shuttle_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[Shuttle] video ->", mp4)
        else:
            print("[Shuttle] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[Shuttle] done ->", out)

if __name__ == "__main__":
    main()
