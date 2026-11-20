"""
반자동 제함기 → 충전·상부 접지 컨베이어 → 반자동 봉함기(상하 테이핑) 포장 라인 — Blender 장면 + 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles)

참고: 듀팩코리아(듀펙장원기계) 반자동 제함기 DP-501, 반자동 테이핑기 DP-401 공개 사진·사양 (docs/caseline-research.md).
로고·상표는 넣지 않았고, 사진을 보고 일반적인 형태로 만든 모델입니다. 제조사 도면이 아닙니다.

사용 예 (저장소 루트에서)
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/caseline_blender.py -- --out blender/out/caseline --still 1,200,450
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/caseline_blender.py -- --out blender/out/caseline --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4, 없으면 Blender 내장 인코더)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용)
  --engine E         cycles (기본) | eevee
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > ONEAPI > METAL 자동)
  --no-export        FBX/GLB 내보내기 생략

동작 정의(타이밍·치수)는 caseline_kinematics.py 에 있고 Unity CaseLineKinematics.cs 와 같습니다.
"""
import bpy, bmesh, math, random, sys, os, shutil, subprocess
from mathutils import Vector, Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import caseline_kinematics as K

FPS = 30
SECONDS = 30
FRAMES = FPS * SECONDS

def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else argv[1:]
    a = {"out": "./out", "still": None, "anim": False, "frames": None, "res": "1920x1080",
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

TABLE = K.TABLE

# ---------------------------------------------------------------- 유틸
def link_coll(ob, coll):
    (coll or bpy.context.scene.collection).objects.link(ob)

def new_coll(name, parent=None):
    c = bpy.data.collections.new(name)
    (parent or bpy.context.scene.collection).children.link(c)
    return c

def empty(name, coll, parent=None, loc=(0, 0, 0)):
    ob = bpy.data.objects.new(name, None); ob.location = loc; link_coll(ob, coll)
    if parent: ob.parent = parent
    return ob

class Parts:
    """많은 상자/원기둥을 bmesh 하나로 모음 (재질별 인덱스). 원점 = 오브젝트 원점."""
    def __init__(self):
        self.bm = bmesh.new(); self.mats = []
    def _mi(self, m):
        if m not in self.mats: self.mats.append(m)
        return self.mats.index(m)
    def _faces(self, verts, m, smooth=False):
        mi = self._mi(m)
        for f in {f for v in verts for f in v.link_faces}:
            f.material_index = mi; f.smooth = smooth
    def box(self, size, loc, m):
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        for v in r["verts"]:
            v.co = Vector((v.co.x * size[0] + loc[0], v.co.y * size[1] + loc[1], v.co.z * size[2] + loc[2]))
        self._faces(r["verts"], m)
    def span(self, a, b, m):
        """두 모서리 점 사이 축정렬 상자"""
        self.box((abs(b[0] - a[0]), abs(b[1] - a[1]), abs(b[2] - a[2])),
                 ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2), m)
    def obox(self, size, mtx, m):
        """임의 변환 상자 (mtx: 4x4)"""
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        for v in r["verts"]:
            v.co = mtx @ Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
        self._faces(r["verts"], m)
    def cyl(self, r, depth, loc, m, axis="Z", seg=20, r2=None):
        out = bmesh.ops.create_cone(self.bm, cap_ends=True, segments=seg, radius1=r, radius2=r if r2 is None else r2, depth=depth)
        for v in out["verts"]:
            x, y, z = v.co
            if axis == "X": x, y, z = z, y, x
            elif axis == "Y": x, y, z = x, z, y
            v.co = Vector((x + loc[0], y + loc[1], z + loc[2]))
        self._faces(out["verts"], m, True)
    def rod(self, a, b, r, m, seg=12):
        a, b = Vector(a), Vector(b); d = b - a
        out = bmesh.ops.create_cone(self.bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=d.length)
        q = Vector((0, 0, 1)).rotation_difference(d.normalized())
        mtx = Matrix.Translation((a + b) / 2) @ q.to_matrix().to_4x4()
        for v in out["verts"]: v.co = mtx @ v.co
        self._faces(out["verts"], m, True)
    def build(self, name, coll, parent=None, bev=0.0):
        me = bpy.data.meshes.new(name); self.bm.to_mesh(me); self.bm.free()
        for m in self.mats: me.materials.append(m)
        ob = bpy.data.objects.new(name, me); link_coll(ob, coll)
        if parent: ob.parent = parent
        if bev:
            mod = ob.modifiers.new("Bevel", "BEVEL"); mod.width = bev; mod.segments = 2
            mod.limit_method = "ANGLE"; mod.harden_normals = False
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

def add_noise_bump(nt, b, scale=200.0, strength=0.05, detail=6.0, rough_var=None, coord="Object", stretch=None):
    tc = nt.nodes.new("ShaderNodeTexCoord")
    nz = nt.nodes.new("ShaderNodeTexNoise"); setin(nz, "Scale", scale); setin(nz, "Detail", detail)
    src = tc.outputs[coord]
    if stretch:
        mp = nt.nodes.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = stretch
        nt.links.new(src, mp.inputs["Vector"]); src = mp.outputs["Vector"]
    nt.links.new(src, nz.inputs["Vector"])
    bp = nt.nodes.new("ShaderNodeBump"); setin(bp, "Strength", strength)
    nt.links.new(nz.outputs["Fac"], bp.inputs["Height"])
    nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
    if rough_var:
        mr = nt.nodes.new("ShaderNodeMapRange")
        setin(mr, "To Min", rough_var[0]); setin(mr, "To Max", rough_var[1])
        nt.links.new(nz.outputs["Fac"], mr.inputs["Value"])
        nt.links.new(mr.outputs["Result"], b.inputs["Roughness"])
    return nz, bp, tc

def color_noise(nt, b, nz, c0, c1):
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (*c0, 1); ramp.color_ramp.elements[1].color = (*c1, 1)
    nt.links.new(nz.outputs["Fac"], ramp.inputs["Fac"]); nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])

def build_materials():
    M = {}
    # 봉함기 도장: 아이보리 분체도장 (사진의 밝은 크림색 프레임)
    m, nt, b = principled("FrameIvory", (0.80, 0.78, 0.70), rough=0.32, Coat_Weight=0.25, Coat_Roughness=0.15)
    add_noise_bump(nt, b, scale=300, strength=0.015); M["ivory"] = m
    # 제함기 몸체: 헤어라인 스테인리스 (한 방향으로 늘린 노이즈)
    m, nt, b = principled("Stainless", (0.74, 0.74, 0.73), metal=1.0, rough=0.28)
    add_noise_bump(nt, b, scale=400, strength=0.02, rough_var=(0.2, 0.34), stretch=(1.0, 1.0, 60.0)); M["sus"] = m
    M["alu"] = principled("Aluminium", (0.80, 0.81, 0.82), metal=1.0, rough=0.3)[0]
    M["zinc"] = principled("RollerZinc", (0.72, 0.73, 0.75), metal=1.0, rough=0.22)[0]
    M["steel"] = principled("SteelDark", (0.25, 0.26, 0.27), metal=0.8, rough=0.45)[0]
    M["frameGrey"] = principled("FrameGrey", (0.32, 0.34, 0.36), rough=0.4, Coat_Weight=0.2)[0]
    m, nt, b = principled("BeltBlack", (0.025, 0.025, 0.027), rough=0.7)
    add_noise_bump(nt, b, scale=900, strength=0.15); M["belt"] = m
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.45)[0]
    M["cup"] = principled("SuctionCup", (0.10, 0.10, 0.11), rough=0.55)[0]
    M["orange"] = principled("GuardOrange", (0.95, 0.55, 0.05), rough=0.4)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["red"] = principled("ButtonRed", (0.75, 0.03, 0.02), rough=0.3)[0]
    M["green"] = principled("ButtonGreen", (0.05, 0.6, 0.15), rough=0.3, Emission_Color=(0.1, 1.0, 0.3, 1), Emission_Strength=1.5)[0]
    M["blue"] = principled("HoseBlue", (0.05, 0.25, 0.65), rough=0.35)[0]
    M["screen"] = principled("Screen", (0.02, 0.08, 0.2), rough=0.15, Emission_Color=(0.35, 0.6, 1.0, 1), Emission_Strength=2.0)[0]
    # 골판지: 크라프트 갈색 + 미세 얼룩 + 요철
    m, nt, b = principled("Cardboard", (0.55, 0.38, 0.22), rough=0.82)
    nz, bp, tc = add_noise_bump(nt, b, scale=40, strength=0.04, detail=8.0)
    color_noise(nt, b, nz, (0.50, 0.34, 0.19), (0.60, 0.43, 0.26)); M["card"] = m
    # OPP 테이프: 반투명 갈색 광택
    M["tape"] = principled("TapeBrown", (0.50, 0.30, 0.10), rough=0.18, Coat_Weight=0.8, Coat_Roughness=0.05)[0]
    M["tapeRoll"] = principled("TapeRoll", (0.45, 0.26, 0.08), rough=0.25, Coat_Weight=0.6)[0]
    M["core"] = principled("PaperCore", (0.70, 0.62, 0.48), rough=0.8)[0]
    # 상품 단상자
    for key, nm, col in (("p0", "ProdWhite", (0.86, 0.86, 0.84)), ("p1", "ProdBlue", (0.08, 0.25, 0.62)),
                         ("p2", "ProdRed", (0.70, 0.08, 0.06)), ("p3", "ProdGreen", (0.12, 0.45, 0.22))):
        m, nt, b = principled(nm, col, rough=0.35, Coat_Weight=0.3)
        add_noise_bump(nt, b, scale=120, strength=0.02); M[key] = m
    # 건물
    m, nt, b = principled("EpoxyFloor", (0.10, 0.25, 0.17), rough=0.22, Coat_Weight=0.5, Coat_Roughness=0.08)
    nz, bp, tc = add_noise_bump(nt, b, scale=3.0, strength=0.01, detail=8.0, rough_var=(0.12, 0.3))
    color_noise(nt, b, nz, (0.08, 0.21, 0.14), (0.12, 0.28, 0.19)); M["floor"] = m     # 녹색 에폭시 (국내 공장 바닥)
    M["wall"] = principled("WallWhite", (0.80, 0.81, 0.82), rough=0.6)[0]
    M["panelWall"] = principled("SandwichPanel", (0.55, 0.58, 0.62), metal=0.3, rough=0.45)[0]
    M["ceiling"] = principled("Ceiling", (0.55, 0.56, 0.58), rough=0.8)[0]
    M["beam"] = principled("SteelBeam", (0.20, 0.32, 0.55), rough=0.45, Coat_Weight=0.2)[0]
    M["lightpanel"] = principled("LightPanel", (1, 1, 1), Emission_Color=(1.0, 0.98, 0.95, 1), Emission_Strength=10.0)[0]
    M["greenline"] = principled("SafetyGreen", (0.1, 0.55, 0.25), rough=0.45)[0]
    M["pallet"] = principled("PalletWood", (0.62, 0.48, 0.32), rough=0.75)[0]
    M["dark"] = principled("OpeningDark", (0.01, 0.01, 0.012), rough=0.9)[0]
    return M

# ---------------------------------------------------------------- 공통 부품
def caster(P, M, x, y, z_top):
    """바퀴 달린 다리 끝 (z_top 아래 0.08 m 높이)"""
    P.box((0.06, 0.06, 0.006), (x, y, z_top - 0.003), M["steel"])
    P.box((0.03, 0.05, 0.03), (x, y, z_top - 0.02), M["steel"])
    P.cyl(0.035, 0.028, (x, y, 0.035), M["rubber"], "Y", 20)
    P.cyl(0.016, 0.03, (x, y, 0.035), M["zinc"], "Y", 12)

def roller_conveyor(M, coll, root, x0, x1, name, pitch=0.075, width=0.46, legs_every=1.2, rollers=None):
    """구동 롤러 컨베이어 (상면 = TABLE). 롤러는 따로 오브젝트 (회전 애니메이션)"""
    P = Parts()
    fy = width / 2 + 0.03
    for sy in (-1, 1):
        P.span((x0, sy * fy - 0.003, TABLE - 0.10), (x1, sy * fy + 0.003, TABLE - 0.005), M["alu"])   # 측판
        P.span((x0, sy * (fy + 0.003), TABLE - 0.10), (x1, sy * (fy + 0.03), TABLE - 0.094), M["alu"])
    n = max(2, int(round((x1 - x0) / legs_every)) + 1)
    for i in range(n):
        x = x0 + 0.05 + (x1 - x0 - 0.1) * i / (n - 1)
        for sy in (-1, 1):
            P.span((x - 0.02, sy * fy - 0.02, 0.08), (x + 0.02, sy * fy + 0.02, TABLE - 0.10), M["alu"])
            P.box((0.08, 0.08, 0.01), (x, sy * fy, 0.005), M["steel"])
            P.cyl(0.008, 0.07, (x, sy * fy, 0.045), M["zinc"], "Z", 8)
        P.span((x - 0.015, -fy, 0.25), (x + 0.015, fy, 0.28), M["alu"])
    P.build(name + "_Frame", coll, root)
    # 롤러
    R = rollers if rollers is not None else []
    nr = int((x1 - x0 - 0.04) / pitch) + 1
    for i in range(nr):
        x = x0 + 0.02 + i * pitch + (x1 - x0 - 0.04 - (nr - 1) * pitch) / 2
        Q = Parts()
        Q.cyl(K.ROLLER_R, width, (0, 0, 0), M["zinc"], "Y", 20)
        Q.cyl(0.006, width + 0.06, (0, 0, 0), M["steel"], "Y", 8)
        ob = Q.build(f"Roller_{name}_{i:03d}", coll, root)
        ob.location = (x, 0, TABLE - K.ROLLER_R)
        R.append(ob)
    return R

# ---------------------------------------------------------------- 반자동 제함기
def make_erector(M, coll, root):
    """DP-501 류: 스테인리스 몸체, 베드 가운데 날개 접는 개구부, 뒤쪽 블랭크 매거진, 위쪽 흡착 이송 암.
    박스는 원점에서 세워지고 푸셔가 +x 로 밀어 컨베이어에 넘김."""
    P = Parts()
    x0, x1, y0, y1 = -0.62, 0.62, -0.40, 0.40
    zt = TABLE - 0.003
    # 몸체 판 (앞/뒤/양끝) — 아래쪽 0.25 m 까지
    P.span((x0, y0, 0.26), (x1, y0 + 0.003, zt - 0.06), M["sus"])
    P.span((x0, y1 - 0.003, 0.26), (x1, y1, zt - 0.06), M["sus"])
    P.span((x0, y0, 0.26), (x0 + 0.003, y1, zt - 0.06), M["sus"])
    P.span((x1 - 0.003, y0, 0.26), (x1, -0.25, zt - 0.06), M["sus"])
    P.span((x1 - 0.003, 0.25, 0.26), (x1, y1, zt - 0.06), M["sus"])
    P.span((x0, y0, 0.26), (x1, y1, 0.27), M["sus"])                                  # 바닥판
    # 상판 (베드): 개구부 x [-0.23, 0.62], |y| < 0.21 은 비움 + 박스 받침 레일
    P.span((x0 - 0.02, 0.21, zt - 0.06), (x1, y1 + 0.02, zt), M["sus"])
    P.span((x0 - 0.02, y0 - 0.02, zt - 0.06), (x1, -0.21, zt), M["sus"])
    P.span((x0 - 0.02, -0.21, zt - 0.06), (-0.23, 0.21, zt), M["sus"])
    for sy in (-1, 1):
        P.span((-0.23, sy * 0.156, zt - 0.02), (x1, sy * 0.21, zt), M["zinc"])
        # 날개 접기 플로 (개구부 안쪽 비스듬한 판)
        P.obox((0.36, 0.004, 0.10), Matrix.Translation((0.25, sy * 0.13, zt - 0.08)) @ Matrix.Rotation(sy * 0.35, 4, "X"), M["zinc"])
        # 낮은 측면 가이드 봉 (흡착 암이 박스를 내릴 때 걸리지 않게 낮게)
        P.rod((-0.25, sy * 0.175, TABLE + 0.03), (x1, sy * 0.175, TABLE + 0.03), 0.006, M["zinc"])
        for x in (-0.20, 0.55):
            P.span((x - 0.006, sy * 0.175, zt), (x + 0.006, sy * 0.22, TABLE + 0.036), M["zinc"])
    # 단변 날개 킥커 (개구부 아래 공압 패들)
    for sx in (-1, 1):
        P.box((0.02, 0.18, 0.08), (sx * 0.235, 0, TABLE - 0.13), M["zinc"])
        P.cyl(0.018, 0.12, (sx * 0.30, 0, TABLE - 0.16), M["alu"], "X", 16)
    # 다리 + 캐스터
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = sx * 0.56, sy * 0.34
            P.span((x - 0.025, y - 0.025, 0.08), (x + 0.025, y + 0.025, 0.27), M["sus"])
            caster(P, M, x, y, 0.08)
    # 모서리 오렌지 가드 (사진의 노란/주황 끝 캡)
    for sy in (-1, 1):
        P.box((0.05, 0.05, 0.10), (x0 - 0.01, sy * 0.40, zt + 0.02), M["orange"])
    # 푸셔 실린더 (몸체 왼쪽 끝 바깥)
    P.cyl(0.035, 0.36, (-0.78, 0, TABLE + 0.115), M["alu"], "X", 24)
    for x in (-0.95, -0.61):
        P.box((0.03, 0.09, 0.09), (x, 0, TABLE + 0.115), M["alu"])
    P.span((-0.97, -0.05, TABLE + 0.0), (-0.60, 0.05, TABLE + 0.07), M["sus"])
    P.span((-0.97, -0.05, 0.27), (-0.93, 0.05, TABLE + 0.07), M["sus"])
    # 조작 박스 (앞 왼쪽): 비상정지, 시작, 작은 화면
    P.span((-0.60, y0 - 0.09, TABLE - 0.30), (-0.36, y0 - 0.002, TABLE - 0.06), M["ivory"])
    P.cyl(0.022, 0.03, (-0.52, y0 - 0.105, TABLE - 0.12), M["red"], "Y", 20)
    P.cyl(0.014, 0.02, (-0.45, y0 - 0.1, TABLE - 0.12), M["green"], "Y", 16)
    P.span((-0.56, y0 - 0.093, TABLE - 0.24), (-0.40, y0 - 0.090, TABLE - 0.16), M["screen"])
    # 에어 유닛 (FRL)
    for k, x in enumerate((0.30, 0.37, 0.44)):
        P.cyl(0.022, 0.12, (x, y0 - 0.04, 0.50), M["alu"] if k != 1 else M["black"], "Z", 16)
    P.span((0.27, y0 - 0.02, 0.57), (0.47, y0 - 0.005, 0.60), M["alu"])
    P.build("Erector_Body", coll, root, bev=0.003)

    # 블랭크 매거진 (뒤쪽 와이어 랙, 납작 블랭크를 세워 적재)
    G = Parts()
    zb = K.REST_Z - K.F - 0.006
    yb0, yb1 = K.MAG_Y + K.W / 2 - 0.01, K.MAG_Y + K.W / 2 + 0.42
    xa, xb = -K.L / 2 - K.W - 0.04, K.L / 2 + 0.04
    for x in (xa, xb):
        G.rod((x, yb0, zb), (x, yb1, zb), 0.008, M["zinc"])
        G.rod((x, yb0 + 0.02, zb), (x, yb0 + 0.02, zb + 0.55), 0.007, M["zinc"])       # 측면 세움 가이드 (사진의 와이어)
        G.rod((x, yb0 + 0.02, zb + 0.55), (x, yb1, zb + 0.55), 0.007, M["zinc"])
        G.rod((x, yb1, zb), (x, yb1, zb + 0.55), 0.007, M["zinc"])
    for y in (yb0 + 0.06, (yb0 + yb1) / 2, yb1 - 0.02):
        G.rod((xa, y, zb), (xb, y, zb), 0.007, M["zinc"])
    for sx in (xa + 0.06, xb - 0.06):
        G.span((sx - 0.02, yb1 - 0.02, 0.0), (sx + 0.02, yb1 + 0.02, zb), M["sus"])
        G.span((sx - 0.02, yb0 - 0.02, 0.27), (sx + 0.02, yb0 + 0.02, zb), M["sus"])
    G.span((xa, yb0 - 0.02, zb - 0.02), (xb, yb0 + 0.02, zb), M["sus"])
    G.build("Erector_Magazine", coll, root)

    # 흡착 이송 갠트리 (포털 2개 + y 방향 보)
    Q = Parts()
    zbeam = 1.52
    # 앞쪽은 기둥 없이 외팔보 (작업자/카메라 시야 확보): 포털은 y = 0.40, 1.10
    for y in (0.40, 1.10):
        for sx in (-1, 1):
            Q.span((sx * 0.62 - 0.03, y - 0.03, 0.27 if y < 0.5 else 0.0), (sx * 0.62 + 0.03, y + 0.03, zbeam), M["alu"])
        Q.span((-0.65, y - 0.03, zbeam - 0.06), (0.65, y + 0.03, zbeam + 0.02), M["alu"])
    Q.span((0.10, -0.06, zbeam - 0.02), (0.18, 1.13, zbeam + 0.06), M["alu"])            # 주 보 (x = 0.14)
    Q.span((0.10, -0.06, zbeam + 0.06), (0.18, 1.13, zbeam + 0.08), M["black"])          # 케이블 덕트
    Q.span((0.08, -0.08, zbeam - 0.04), (0.20, -0.06, zbeam + 0.08), M["orange"])        # 끝 스토퍼
    Q.build("Erector_Gantry", coll, root)

    # 가동부: 푸셔 (원점 = 판 중심), 흡착 암 (원점 = 박스 원점 규약), 캐리지
    U = Parts()
    U.box((0.012, 0.24, 0.20), (0, 0, 0), M["sus"])
    U.box((0.004, 0.22, 0.18), (0.008, 0, 0), M["rubber"])
    U.cyl(0.012, 0.46, (-0.236, 0, 0), M["zinc"], "X", 16)
    pusher = U.build("Pusher", coll, root); pusher.location = (K.PUSH_REST, 0, TABLE + 0.115)

    A = Parts()
    yp = K.W / 2 + K.T + 0.03
    A.box((0.22, 0.012, 0.13), (0, yp, K.H / 2), M["alu"])
    for sx in (-1, 1):
        A.cyl(0.026, 0.02, (sx * 0.065, yp - 0.016, K.H / 2), M["cup"], "Y", 20, r2=0.018)
        A.cyl(0.008, 0.02, (sx * 0.065, yp + 0.016, K.H / 2), M["blue"], "Y", 10)
    A.box((0.04, 0.03, 0.10), (0, yp + 0.02, K.H / 2 + 0.11), M["alu"])
    A.cyl(0.012, 0.80, (0, yp + 0.02, K.H / 2 + 0.55), M["zinc"], "Z", 16)
    A.cyl(0.03, 0.30, (0, yp + 0.02, K.H / 2 + 1.05), M["alu"], "Z", 20)
    arm = A.build("PickArm", coll, root)
    C_ = Parts()
    C_.span((-0.06, -0.08, zbeam - 0.10), (0.10, 0.08, zbeam - 0.02), M["alu"])
    C_.span((0.08, -0.06, zbeam - 0.05), (0.10, 0.06, zbeam + 0.04), M["black"])
    carriage = C_.build("PickCarriage", coll, root)
    return pusher, arm, carriage

def make_blank_stack(M, coll, root, n=14):
    """매거진 뒤쪽 정적 블랭크 더미 (tau = -1 상태 판 모양을 합친 메쉬)"""
    P = Parts()
    slabs = K.box_slabs(-1.0)
    for i in range(1, n + 1):
        oy = K.MAG_Y + i * 0.017
        for nm, cen, ex, ey, ez, size in slabs:
            mtx = Matrix(((ex[0], ey[0], ez[0], cen[0]), (ex[1], ey[1], ez[1], cen[1] + oy),
                          (ex[2], ey[2], ez[2], cen[2] + K.REST_Z), (0, 0, 0, 1)))
            P.obox(size, mtx, M["card"])
    return P.build("Magazine_Blanks", coll, root)

# ---------------------------------------------------------------- 충전 슈트 + 상부 접지
def make_fill_and_fold(M, coll, root):
    P = Parts()
    a = K.CHUTE_A
    sy, sz = K.CHUTE_S; ey, ez = K.CHUTE_E
    ln = math.hypot(sy - ey, sz - ez) + 0.25
    my, mz = (sy + ey) / 2 + 0.10 * math.cos(a), (sz + ez) / 2 + 0.10 * math.sin(a)
    rot = Matrix.Rotation(a, 4, "X")
    n_off = Vector((0, -math.sin(a), math.cos(a)))           # 슈트 면 법선 (위)
    base = Vector((K.X_FILL, my, mz)) - n_off * (K.PACK[2] / 2 + 0.006)
    P.obox((0.46, ln, 0.008), Matrix.Translation(base) @ rot, M["sus"])
    for sx in (-1, 0, 1):
        P.obox((0.006, ln, 0.05), Matrix.Translation(base + Vector((sx * 0.225, 0, 0)) + n_off * 0.025) @ rot, M["sus"])
    for k in range(12):                                       # 중력 롤러 (슈트 위 작은 롤러 줄)
        t = -ln / 2 + 0.05 + k * (ln - 0.1) / 11
        c = base + Vector((0, math.cos(a), math.sin(a))) * t + n_off * 0.01
        P.cyl(0.008, 0.44, tuple(c), M["zinc"], "X", 10)
    for sx in (-1, 1):                                         # 지지 다리
        P.span((K.X_FILL + sx * 0.25 - 0.02, sy - 0.06, 0), (K.X_FILL + sx * 0.25 + 0.02, sy - 0.02, sz - 0.1), M["alu"])
    # 상품 공급 터널 (슈트 시작부를 덮음) + 뒤쪽 고가 벨트 컨베이어 (뒤 벽에서 들어옴)
    P.span((K.X_FILL - 0.27, sy - 0.25, sz - 0.12), (K.X_FILL + 0.27, sy + 0.40, sz + 0.24), M["ivory"])
    P.span((K.X_FILL - 0.25, sy - 0.252, sz - 0.08), (K.X_FILL + 0.25, sy - 0.25, sz + 0.20), M["dark"])
    P.span((K.X_FILL - 0.24, sy + 0.40, sz - 0.02), (K.X_FILL + 0.24, 4.5, sz + 0.02), M["belt"])
    for sx in (-1, 1):
        P.span((K.X_FILL + sx * 0.26 - 0.01, sy + 0.40, sz - 0.08), (K.X_FILL + sx * 0.26 + 0.01, 4.5, sz + 0.06), M["alu"])
        for y in (sy + 0.9, sy + 2.0, sy + 3.1):
            P.span((K.X_FILL + sx * 0.26 - 0.02, y - 0.02, 0), (K.X_FILL + sx * 0.26 + 0.02, y + 0.02, sz - 0.08), M["alu"])
    # 충전 위치 표시: 광전센서 기둥 + 경광등
    for sx in (-1, 1):
        x = K.X_FILL + sx * 0.32
        P.span((x - 0.015, -0.30, 0.0), (x + 0.015, -0.27, TABLE + 0.45), M["alu"])
        P.box((0.04, 0.03, 0.05), (x, -0.255, TABLE + 0.10), M["black"])
    P.span((K.X_FILL + 0.30, 0.27, 0.0), (K.X_FILL + 0.34, 0.30, TABLE + 0.45), M["alu"])
    P.cyl(0.03, 0.12, (K.X_FILL + 0.32, 0.285, TABLE + 0.51), M["green"], "Z", 16)       # 경광등 (뒤쪽)
    # 상부 접지: 앞 날개 고정 바, 킥커 지지, 플로 레일
    zt = K.REST_Z + K.H
    for sy_ in (-1, 1):
        P.span((K.X_BAR - 0.015, sy_ * 0.30 - 0.015, 0), (K.X_BAR + 0.015, sy_ * 0.30 + 0.015, K.KICK_Z + 0.05), M["alu"])
        P.span((K.X_KICK - 0.015, sy_ * 0.30 - 0.015, 0), (K.X_KICK + 0.015, sy_ * 0.30 + 0.015, K.KICK_Z + 0.05), M["alu"])
    P.rod((K.X_BAR, -0.30, zt + 0.035), (K.X_BAR, 0.30, zt + 0.035), 0.01, M["zinc"])
    P.rod((K.X_KICK, -0.30, K.KICK_Z), (K.X_KICK, 0.30, K.KICK_Z), 0.012, M["zinc"])
    P.cyl(0.02, 0.18, (K.X_KICK - 0.05, 0.31, K.KICK_Z + 0.08), M["alu"], "X", 16)       # 킥커 실린더
    for sy_ in (-1, 1):
        P.rod((3.25, sy_ * 0.20, zt + 0.16), (3.80, sy_ * 0.045, zt + 0.014), 0.008, M["zinc"])
        P.rod((3.25, sy_ * 0.20, zt + 0.16), (3.25, sy_ * 0.30, zt + 0.16), 0.008, M["zinc"])
        P.span((3.25 - 0.015, sy_ * 0.30 - 0.015, 0), (3.25 + 0.015, sy_ * 0.30 + 0.015, zt + 0.17), M["alu"])
        P.rod((3.80, sy_ * 0.045, zt + 0.014), (3.95, sy_ * 0.045, zt + 0.014), 0.008, M["zinc"])
    # 컨베이어 측면 가이드 (박스 폭)
    gy = K.W / 2 + K.T + 0.012
    for sy_ in (-1, 1):
        for z in (TABLE + 0.06, TABLE + 0.20):
            P.rod((0.75, sy_ * gy, z), (3.78, sy_ * gy, z), 0.006, M["zinc"])
        for x in (0.8, 1.5, 2.35, 3.0, 3.7):
            P.span((x - 0.006, sy_ * gy, z - 0.15), (x + 0.006, sy_ * (gy + 0.08), z + 0.006), M["zinc"])
            P.span((x - 0.006, sy_ * (gy + 0.074), TABLE - 0.10), (x + 0.006, sy_ * (gy + 0.086), z), M["zinc"])
    # 구동 모터 (컨베이어 아래)
    P.cyl(0.06, 0.20, (2.0, -0.12, TABLE - 0.20), M["frameGrey"], "Y", 24)
    P.span((1.93, -0.02, TABLE - 0.27), (2.07, 0.12, TABLE - 0.12), M["frameGrey"])
    P.build("FillAndFold", coll, root)
    # 킥커 팔 (원점 = 회전축, y 축 회전)
    Kp = Parts()
    Kp.cyl(0.022, 0.06, (0, -0.10, 0), M["alu"], "Y", 16)
    Kp.box((0.02, 0.02, 0.22), (0, -0.10, 0.11), M["alu"])
    Kp.box((0.05, 0.22, 0.02), (0, 0.0, 0.22), M["rubber"])
    Kp.box((0.03, 0.03, 0.02), (0, -0.10, 0.22), M["alu"])
    kick = Kp.build("Kicker", coll, root); kick.location = (K.X_KICK, 0, K.KICK_Z)
    return kick

# ---------------------------------------------------------------- 반자동 봉함기 (I 타입 상하 테이핑)
SX0, SX1 = 3.80, 4.80

def make_sealer(M, coll, root, rollers):
    """DP-401 류: 아이보리 프레임, 양쪽 사이드 벨트 구동, 위 테이프 헤드(높이 조절 문형), 아래 테이프 헤드.
    사양: 기계 W890 x H1780 x L1000 mm, 테이프 50 mm, 사이드 벨트, 최대 1400 box/h."""
    P = Parts()
    xh = K.X_HEAD
    zt = TABLE
    # 하부 프레임 (사각 파이프)
    for sx in (SX0 + 0.05, SX1 - 0.05):
        for sy in (-1, 1):
            P.span((sx - 0.03, sy * 0.40 - 0.03, 0.08), (sx + 0.03, sy * 0.40 + 0.03, zt - 0.05), M["ivory"])
            caster(P, M, sx, sy * 0.40, 0.08)
    for sy in (-1, 1):
        P.span((SX0, sy * 0.40 - 0.03, zt - 0.11), (SX1, sy * 0.40 + 0.03, zt - 0.05), M["ivory"])
        P.span((SX0, sy * 0.40 - 0.03, 0.28), (SX1, sy * 0.40 + 0.03, 0.33), M["ivory"])
        P.span((SX0, sy * 0.27, zt - 0.06), (SX1, sy * 0.30, zt - 0.03), M["ivory"])
    for sx in (SX0 + 0.05, SX1 - 0.05):
        P.span((sx - 0.03, -0.40, zt - 0.11), (sx + 0.03, 0.40, zt - 0.05), M["ivory"])
    # 하부 테이프 헤드 (베드 슬롯 아래) + 테이프 롤
    P.span((xh - 0.30, -0.035, zt - 0.20), (xh + 0.25, 0.035, zt - 0.01), M["zinc"])
    P.cyl(0.02, 0.06, (xh, 0, zt - 0.02), M["rubber"], "Y", 20)
    P.cyl(0.09, 0.05, (xh + 0.12, 0, zt - 0.32), M["tapeRoll"], "Y", 40)
    P.cyl(0.038, 0.052, (xh + 0.12, 0, zt - 0.32), M["core"], "Y", 24)
    # 기둥 2개 + 상부 크로스바 (문형) — 사진처럼 테이프 헤드 위치에
    for sy in (-1, 1):
        P.span((xh - 0.035, sy * 0.41 - 0.035, zt - 0.11), (xh + 0.035, sy * 0.41 + 0.035, 1.78), M["ivory"])
        P.rod((xh - 0.06, sy * 0.36, zt + 0.02), (xh - 0.06, sy * 0.36, 1.70), 0.012, M["zinc"])   # 높이 조절 가이드
    P.span((xh - 0.05, -0.445, 1.70), (xh + 0.05, 0.445, 1.78), M["ivory"])
    P.cyl(0.03, 0.04, (xh, 0, 1.80), M["black"], "Z", 20)                                  # 높이 조절 핸들
    P.rod((xh, 0, 1.80), (xh, 0, 1.86), 0.006, M["black"])
    # 상부 헤드 지지 암 (기둥 사이) + 테이프 헤드 하우징
    ztop = K.REST_Z + K.H + K.T
    zarm = ztop + 0.24
    P.span((xh - 0.05, -0.38, zarm - 0.04), (xh + 0.05, 0.38, zarm + 0.03), M["ivory"])
    P.span((SX0 + 0.15, -0.09, ztop + 0.018), (SX1 - 0.15, 0.09, ztop + 0.16), M["ivory"])
    P.span((SX0 + 0.15, -0.095, ztop + 0.017), (SX1 - 0.15, -0.09, ztop + 0.10), M["zinc"])
    P.span((SX0 + 0.15, 0.09, ztop + 0.017), (SX1 - 0.15, 0.095, ztop + 0.10), M["zinc"])
    P.cyl(0.02, 0.06, (xh, 0, ztop + 0.02), M["rubber"], "Y", 20)                           # 압착 롤러
    P.cyl(0.02, 0.06, (xh + 0.30, 0, ztop + 0.02), M["rubber"], "Y", 20)                    # 뒤 누름 롤러
    P.cyl(0.09, 0.05, (xh + 0.10, 0, zarm + 0.12), M["tapeRoll"], "Y", 40)                  # 위 테이프 롤
    P.cyl(0.038, 0.052, (xh + 0.10, 0, zarm + 0.12), M["core"], "Y", 24)
    P.cyl(0.01, 0.12, (xh + 0.10, 0, zarm + 0.12), M["zinc"], "Y", 10)
    P.span((xh + 0.06, -0.03, zarm + 0.03), (xh + 0.14, -0.026, zarm + 0.12), M["zinc"])
    P.obox((0.003, K.TAPE_W, 0.20), Matrix.Translation((xh + 0.03, 0, ztop + 0.12)) @ Matrix.Rotation(0.5, 4, "Y"), M["tape"])
    # 사이드 벨트 유닛 (좌우)
    by = K.W / 2 + K.T
    for sy in (-1, 1):
        y_in = sy * by
        P.span((SX0 + 0.05, sy * (by + 0.016), zt + 0.05), (SX1 - 0.05, sy * (by + 0.075), zt + 0.21), M["ivory"])
        P.span((SX0 + 0.07, y_in, zt + 0.06), (SX1 - 0.07, y_in + sy * 0.016, zt + 0.20), M["belt"])
        for x in (SX0 + 0.07, SX1 - 0.07):
            P.cyl(0.035, 0.15, (x, y_in + sy * 0.03, zt + 0.13), M["belt"], "Z", 24)
            P.cyl(0.02, 0.16, (x, y_in + sy * 0.03, zt + 0.13), M["zinc"], "Z", 12)
        P.cyl(0.055, 0.16, (SX1 - 0.16, sy * (by + 0.05), zt + 0.31), M["frameGrey"], "Z", 24)   # 벨트 모터
        P.box((0.12, 0.10, 0.08), (SX1 - 0.16, sy * (by + 0.05), zt + 0.25), M["frameGrey"])
        P.span((SX0 + 0.10, sy * (by + 0.075), zt + 0.10), (SX0 + 0.14, sy * 0.40, zt + 0.16), M["ivory"])
        P.span((SX1 - 0.14, sy * (by + 0.075), zt + 0.10), (SX1 - 0.10, sy * 0.40, zt + 0.16), M["ivory"])
    # 스위치 박스 (앞쪽 기둥)
    P.span((xh + 0.035, 0.36, zt + 0.25), (xh + 0.11, 0.46, zt + 0.47), M["ivory"])     # 스위치 박스 (뒤 기둥 옆)
    P.cyl(0.022, 0.03, (xh + 0.125, 0.41, zt + 0.40), M["red"], "X", 20)
    P.cyl(0.014, 0.02, (xh + 0.12, 0.41, zt + 0.32), M["green"], "X", 16)
    P.build("Sealer_Body", coll, root, bev=0.003)
    # 베드 롤러 (자유 롤러지만 회전시킴) — 테이프 헤드 슬롯 비움
    pitch = 0.07
    x = SX0 + 0.06; i = 0
    while x < SX1 - 0.04:
        if abs(x - xh) > 0.06:
            Q = Parts()
            Q.cyl(K.ROLLER_R, 0.50, (0, 0, 0), M["zinc"], "Y", 20)
            ob = Q.build(f"Roller_Sealer_{i:03d}", coll, root); ob.location = (x, 0, TABLE - K.ROLLER_R)
            rollers.append(ob); i += 1
        x += pitch

# ---------------------------------------------------------------- 건물
def make_building(M, coll):
    S = 40.0; Hh = 6.5
    P = Parts()
    P.box((S, S, 0.02), (4, 0, -0.01), M["floor"])
    # 안전선 (노랑): 라인 둘레, 통행로 (초록)
    for (a, b) in [((-1.6, -0.95), (10.0, -0.89)), ((-1.6, 2.0), (1.3, 2.06)), ((-1.66, -0.95), (-1.6, 2.06))]:
        P.span((a[0], a[1], 0), (b[0], b[1], 0.003), M["yellow"])
    P.span((-6, -3.2, 0), (14, -3.08, 0.003), M["greenline"])
    # 벽: 뒤 (y = 4.5, 상품 공급 개구부), 출구 칸막이 (x = 8.8, 컨베이어 개구부), 왼쪽
    P.span((-8, 4.5, 0), (K.X_FILL - 0.32, 4.7, Hh), M["panelWall"])
    P.span((K.X_FILL + 0.32, 4.5, 0), (16, 4.7, Hh), M["panelWall"])
    P.span((K.X_FILL - 0.32, 4.5, TABLE + 1.35), (K.X_FILL + 0.32, 4.7, Hh), M["panelWall"])
    P.span((K.X_FILL - 0.32, 4.5, 0), (K.X_FILL + 0.32, 4.7, TABLE + 0.75), M["panelWall"])
    P.span((K.X_FILL - 0.30, 4.62, TABLE + 0.75), (K.X_FILL + 0.30, 4.7, TABLE + 1.35), M["dark"])
    xw = 8.8
    P.span((xw, -8, 0), (xw + 0.2, -0.40, Hh), M["panelWall"])
    P.span((xw, 0.40, 0), (xw + 0.2, 4.5, Hh), M["panelWall"])
    P.span((xw, -0.40, TABLE + 0.55), (xw + 0.2, 0.40, Hh), M["panelWall"])
    P.span((xw + 0.15, -0.40, 0), (xw + 0.2, 0.40, TABLE - 0.12), M["panelWall"])
    P.span((xw + 0.6, -0.6, 0), (xw + 0.62, 0.6, Hh), M["dark"])                          # 개구부 너머 어두운 공간
    P.span((xw - 0.02, -0.46, TABLE + 0.55), (xw, 0.46, TABLE + 0.62), M["yellow"])        # 개구부 테두리
    for sy in (-1, 1):
        P.span((xw - 0.02, sy * 0.40 - 0.03, 0), (xw, sy * 0.40 + 0.03, TABLE + 0.62), M["yellow"])
    P.span((-8.2, -8, 0), (-8, 4.7, Hh), M["panelWall"])
    P.span((-8, -12, 0), (16, -11.8, Hh), M["wall"])
    P.box((24, 17, 0.2), (4, -3.6, Hh), M["ceiling"])
    # 기둥 (파란 H형강), 천장 보
    for x in (-4.0, 4.0, 12.0):
        for y in (4.2, -7.5):
            P.box((0.3, 0.3, Hh), (x, y, Hh / 2), M["beam"])
    for x in range(-7, 16, 3):
        P.box((0.2, 17, 0.4), (x, -3.6, Hh - 0.3), M["beam"])
    # 왼쪽 팔레트: 블랭크 묶음 / 오른쪽: 봉함된 박스 적재 팔레트
    for (px, py, kind) in ((-1.15, 1.25, "blank"), (6.6, 1.6, "box"), (7.9, 1.6, "box")):
        P.box((1.1, 1.1, 0.02), (px, py, 0.13), M["pallet"])
        for k in (-1, 0, 1):
            P.box((1.1, 0.1, 0.10), (px, py + k * 0.5, 0.06), M["pallet"])
            P.box((0.1, 1.1, 0.02), (px + k * 0.5, py, 0.01), M["pallet"])
        if kind == "blank":
            P.box((0.72, 1.0, 0.55), (px, py, 0.14 + 0.275), M["card"])
            for k in range(5):
                P.box((0.725, 1.005, 0.004), (px, py, 0.18 + k * 0.11), M["tape"])
        else:
            for lz in range(3):
                for ix in range(2):
                    for iy in range(3):
                        P.box((0.408, 0.308, 0.308), (px - 0.21 + ix * 0.42, py - 0.33 + iy * 0.33, 0.14 + 0.154 + lz * 0.31), M["card"])
                        P.box((0.41, K.TAPE_W, 0.0012), (px - 0.21 + ix * 0.42, py - 0.33 + iy * 0.33, 0.14 + 0.308 + lz * 0.31), M["tape"])
    P.build("Building", coll)
    for ix in range(-1, 4):
        for iy in range(-2, 1):
            x, y = ix * 3.0 + 1.0, iy * 3.0 + 1.0
            b = Parts(); b.box((1.4, 0.35, 0.05), (x, y, Hh - 0.6), M["lightpanel"]); b.build("LightPanel", coll)
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.4; ld.size_y = 0.35
            ld.energy = 200; ld.color = (1.0, 0.97, 0.92)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (x, y, Hh - 0.66); link_coll(lo, coll)

# ---------------------------------------------------------------- 박스 / 상품 오브젝트
def slab_mesh(name, size, mat):
    P = Parts(); P.box(size, (0, 0, 0), mat)
    me = bpy.data.meshes.new(name); P.bm.to_mesh(me); P.bm.free(); me.materials.append(mat)
    return me

def make_dynamic(M, coll):
    """박스 k 마다 판 12장 + 테이프 6장 + 상품 8개 오브젝트"""
    tau_s = K.box_slabs(3.0)
    slab_me = {nm: slab_mesh("M_" + nm, size, M["card"]) for nm, cen, ex, ey, ez, size in tau_s}
    tape_me = {}
    for nm, vis, cen, size in K.tape_parts(30.0):
        sz = (1.0, size[1], size[2]) if nm.startswith("Tape_") else size
        tape_me[nm] = slab_mesh("M_" + nm, sz, M["tape"])
    pack_me = []
    for v in range(4):
        P = Parts()
        px, py, pz = K.PACK
        P.box((px - 0.002, py - 0.002, pz - 0.002), (0, 0, 0), M[f"p{v}"])
        P.box((px, py, 0.03), (0, 0, pz * 0.18), M["p0"] if v else M["p1"])        # 인쇄 띠
        me = bpy.data.meshes.new(f"M_Pack{v}"); P.bm.to_mesh(me); P.bm.free()
        for m in P.mats: me.materials.append(m)
        pack_me.append(me)
    ks = sorted({k for f in range(FRAMES) for k in K.boxes_alive(f / FPS)})
    boxes = {}
    for k in ks:
        bc = new_coll(f"Box_{k:+03d}", coll)
        objs = {}
        for nm in slab_me:
            ob = bpy.data.objects.new(f"B{k}_{nm}", slab_me[nm]); link_coll(ob, bc); ob.rotation_mode = "QUATERNION"; objs[nm] = ob
        for nm in tape_me:
            ob = bpy.data.objects.new(f"B{k}_{nm}", tape_me[nm]); link_coll(ob, bc); objs[nm] = ob
        packs = []
        rnd = random.Random(100 + k)
        for j in range(8):
            ob = bpy.data.objects.new(f"B{k}_Pack{j}", pack_me[rnd.randrange(4)]); link_coll(ob, bc); packs.append(ob)
        boxes[k] = (objs, packs)
    return boxes

# ---------------------------------------------------------------- 키프레임
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
    if n > 1: fc.keyframe_points.add(n - 1)
    co = []
    for f, v in zip(frames, values): co += [f, v]
    fc.keyframe_points.foreach_set("co", co)
    ii = {"LINEAR": 1, "CONSTANT": 0}[interp]
    fc.keyframe_points.foreach_set("interpolation", [ii] * n)
    fc.update()

def compress(vals, eps=1e-6):
    fs, vs = [], []
    n = len(vals)
    for i, v in enumerate(vals):
        if i == 0 or i == n - 1 or abs(v - vals[i - 1]) > eps or abs(vals[i + 1] - v) > eps:
            fs.append(i + 1); vs.append(v)
    return fs, vs

def bake_series(ob, path, series, n):
    for ax in range(n):
        fs, vs = compress([s[ax] for s in series])
        bake_channel(ob, path, ax, fs, vs)

def basis_quat(ex, ey, ez, prev):
    q = Matrix(((ex[0], ey[0], ez[0]), (ex[1], ey[1], ez[1]), (ex[2], ey[2], ez[2]))).to_quaternion()
    if prev is not None and q.dot(prev) < 0: q.negate()
    return q

def animate(boxes, pusher, arm, carriage, kicker, rollers):
    ts = [f / FPS for f in range(FRAMES)]
    for k, (objs, packs) in boxes.items():
        series = {nm: ([], [], []) for nm in objs}
        prevq = {}
        pseries = [([], [], []) for _ in packs]
        for t in ts:
            tau = t - K.PHASE - k * K.PERIOD
            vis, root = K.box_pose(tau)
            tt = max(tau, 0.0)
            for nm, cen, ex, ey, ez, size in K.box_slabs(tt if tau >= 0 else -1.0):
                q = basis_quat(ex, ey, ez, prevq.get(nm)); prevq[nm] = q
                loc, rot, sc = series[nm]
                loc.append((root[0] + cen[0], root[1] + cen[1], root[2] + cen[2])); rot.append(tuple(q))
                sc.append((1, 1, 1) if vis else (0, 0, 0))
            for nm, on, cen, size in K.tape_parts(tt):
                loc, rot, sc = series[nm]
                loc.append((root[0] + cen[0], root[1] + cen[1], root[2] + cen[2]))
                s = 1.0 if (vis and on and tau >= 0) else 0.0
                sc.append(((size[0] if nm.startswith("Tape_") else 1.0) * s, s, s))
            for j in range(len(packs)):
                pv, pos, ang = K.pack_pose(tt, j)
                s = 1.0 if (vis and pv and tau >= 0) else 0.0
                pseries[j][0].append(pos); pseries[j][1].append((ang, 0, 0)); pseries[j][2].append((s, s, s))
        for nm, ob in objs.items():
            loc, rot, sc = series[nm]
            bake_series(ob, "location", loc, 3)
            if rot: bake_series(ob, "rotation_quaternion", rot, 4)
            bake_series(ob, "scale", sc, 3)
        for ob, (loc, rot, sc) in zip(packs, pseries):
            bake_series(ob, "location", loc, 3); bake_series(ob, "rotation_euler", rot, 3); bake_series(ob, "scale", sc, 3)
    # 기계 가동부
    bake_series(pusher, "location", [(K.pusher_x(t), 0, TABLE + 0.115) for t in ts], 3)
    arm_s = [K.pick_arm(t) for t in ts]
    bake_series(arm, "location", arm_s, 3)
    bake_series(carriage, "location", [(0, p[1] + K.W / 2 + K.T + 0.05, 0) for p in arm_s], 3)
    bake_series(kicker, "rotation_euler", [(0, K.kicker_angle(t), 0) for t in ts], 3)
    for r in rollers:
        bake_channel(r, "rotation_euler", 1, [1, FRAMES], [0.0, K.roller_angle((FRAMES - 1) / FPS)])

# ---------------------------------------------------------------- 카메라 / 렌더
# 30초 동선 (초, 위치, 바라보는 점, 렌즈 mm). Unity CaseLineCamera.cs 와 같은 값.
# 박스 1 (t = 5 s 에 집힘) 을 따라감: 제함 5~8 s, 충전 12~15 s, 상부 접지 17~20 s, 테이핑 21~23 s.
CAM_KEYS = [
    (0.0,  (-3.6, -4.4, 3.0), (2.6, 0.6, 0.7), 24),     # 전경: 라인 전체
    (4.0,  (-1.9, -2.8, 2.3), (-0.1, 0.4, 0.9), 26),    # 제함기 쪽으로
    (6.5,  (-0.6, -1.9, 1.95), (0.0, 0.2, 0.95), 28),   # 블랭크 펼침 + 바닥 날개 접힘
    (9.0,  (0.6, -1.9, 1.85), (0.6, 0.0, 0.85), 28),    # 푸셔가 컨베이어로 밀어냄
    (12.5, (1.5, -1.9, 2.0), (1.9, 0.3, 0.95), 28),     # 충전 슈트
    (15.5, (2.3, -1.8, 1.9), (2.5, 0.0, 0.95), 28),
    (18.5, (2.9, -1.7, 1.85), (3.2, 0.0, 1.00), 30),    # 상부 날개 접기 (바, 킥커, 플로)
    (21.0, (3.3, -1.5, 2.1), (4.2, 0.0, 1.00), 30),     # 봉함기 진입, 테이프 헤드
    (23.5, (5.4, -1.3, 1.95), (4.6, 0.0, 1.00), 30),    # 테이핑된 박스 배출 (위 테이프 + 앞 탭)
    (26.5, (6.6, -2.6, 2.0), (5.4, 0.2, 0.95), 26),
    (30.0, (8.0, -4.4, 2.9), (4.2, 0.5, 0.9), 24),      # 빠지며 라인 전경
]

def setup_camera(scene_coll):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36; cam_d.clip_start = 0.03
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 5.6
    for sec, loc, look, lens in CAM_KEYS:
        f = 1 + round(sec * FPS)
        cam.location = loc; cam.keyframe_insert("location", frame=f)
        tgt.location = look; tgt.keyframe_insert("location", frame=f)
        cam_d.lens = lens; cam_d.keyframe_insert("lens", frame=f)
    return cam

def setup_render(a):
    sc = bpy.context.scene
    eng = a["engine"].lower()
    if eng == "eevee":
        for e in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
            try: sc.render.engine = e; break
            except Exception: continue
        try: sc.eevee.taa_render_samples = int(a["samples"])
        except Exception: pass
        for attr in ("use_raytracing", "use_gtao", "use_shadows"):
            try: setattr(sc.eevee, attr, True)
            except Exception: pass
    else:
        sc.render.engine = "CYCLES"
        sc.cycles.samples = int(a["samples"])
        sc.cycles.use_adaptive_sampling = True
        try:
            sc.cycles.use_denoising = True; sc.cycles.denoiser = "OPENIMAGEDENOISE"
        except Exception:
            pass
        sc.cycles.max_bounces = 8
        sc.cycles.caustics_reflective = False; sc.cycles.caustics_refractive = False
        sc.cycles.blur_glossy = 1.0
        if a["gpu"]:
            prefs = bpy.context.preferences.addons["cycles"].preferences
            for dev in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
                try:
                    prefs.compute_device_type = dev; prefs.get_devices()
                    if any(d.type == dev for d in prefs.devices):
                        for d in prefs.devices: d.use = d.type == dev
                        sc.cycles.device = "GPU"; print("[CaseLine] GPU:", dev); break
                except Exception:
                    continue
        else:
            sc.cycles.device = "CPU"
    w, h = [int(v) for v in a["res"].lower().split("x")]
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.resolution_percentage = 100
    sc.render.fps = FPS
    sc.frame_start, sc.frame_end = 1, FRAMES
    sc.render.use_motion_blur = True
    sc.render.motion_blur_shutter = 0.5
    try:
        sc.view_settings.view_transform = "AgX"; sc.view_settings.look = "AgX - Medium High Contrast"
    except Exception:
        try: sc.view_settings.view_transform = "Filmic"
        except Exception: pass
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.78, 0.80, 0.83, 1); bg.inputs[1].default_value = 0.2

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
                             use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)

def export_glb(root, path):
    select_tree(root)
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_yup=True,
                              export_apply=True, export_animations=False)

def encode_video(fdir, fps, step, mp4):
    """PNG 시퀀스 → H.264 MP4. ffmpeg 가 있으면 사용, 없으면 Blender 내장 FFmpeg(시퀀서)로 인코딩 (autostore_blender.py 와 같은 방식)"""
    files = sorted(f for f in os.listdir(fdir) if f.startswith("f_") and f.endswith(".png") and os.path.getsize(os.path.join(fdir, f)) > 0)
    if not files:
        print("[CaseLine] 인코딩할 프레임 없음"); return
    rate = fps / step
    ff = shutil.which("ffmpeg")
    if ff:
        lst = os.path.join(fdir, "_list.txt")
        with open(lst, "w") as fh:
            for f in files: fh.write(f"file '{os.path.join(fdir, f)}'\nduration {1.0 / rate}\n")
        subprocess.run([ff, "-y", "-f", "concat", "-safe", "0", "-i", lst, "-c:v", "libx264", "-pix_fmt", "yuv420p",
                        "-crf", "18", "-r", str(rate), mp4])
        return
    src = bpy.context.scene.render
    enc = bpy.data.scenes.new("Encode")
    enc.sequence_editor_create()
    se = enc.sequence_editor
    coll = se.strips if hasattr(se, "strips") else se.sequences
    strip = coll.new_image(name="frames", filepath=os.path.join(fdir, files[0]), channel=1, frame_start=1)
    for f in files[1:]: strip.elements.append(f)
    enc.frame_start, enc.frame_end = 1, len(files)
    r = enc.render
    r.resolution_x, r.resolution_y, r.resolution_percentage = src.resolution_x, src.resolution_y, 100
    r.fps = int(round(rate)); r.fps_base = r.fps / rate
    ims = r.image_settings
    try: ims.media_type = "VIDEO"           # Blender 5.x
    except Exception: pass
    ims.file_format = "FFMPEG"
    r.ffmpeg.format = "MPEG4"; r.ffmpeg.codec = "H264"
    try: r.ffmpeg.constant_rate_factor = "HIGH"
    except Exception: pass
    try: enc.view_settings.view_transform = "Standard"
    except Exception: pass
    r.filepath = mp4
    bpy.ops.render.render(animation=True, scene=enc.name)

# ---------------------------------------------------------------- 메인
def main():
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    scene = new_coll("CaseLineScene")
    eq = new_coll("Equipment", scene)
    root = empty("CaseLineEquipment", eq)
    # Unity 정렬용 표지 (Blender 원점, +x, +y, +z 1 m)
    for nm, p in (("Marker_O", (0, 0, 0)), ("Marker_X", (1, 0, 0)), ("Marker_Y", (0, 1, 0)), ("Marker_Z", (0, 0, 1))):
        empty(nm, eq, root, p)
    pusher, arm, carriage = make_erector(M, eq, root)
    make_blank_stack(M, eq, root)
    rollers = []
    roller_conveyor(M, eq, root, 0.62, SX0, "Infeed", rollers=rollers)
    kicker = make_fill_and_fold(M, eq, root)
    make_sealer(M, eq, root, rollers)
    roller_conveyor(M, eq, root, SX1, 9.30, "Outfeed", rollers=rollers)
    if not a["no-export"]:
        export_fbx(root, os.path.join(out, "caseline_equipment.fbx"))
        export_glb(root, os.path.join(out, "caseline_equipment.glb"))

    make_building(M, new_coll("Building", scene))
    boxes = make_dynamic(M, new_coll("Boxes", scene))
    animate(boxes, pusher, arm, carriage, kicker, rollers)
    setup_camera(scene)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a)
    print(f"[CaseLine] boxes={len(boxes)} rollers={len(rollers)} frames={FRAMES}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "caseline_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            sc.render.filepath = os.path.join(out, f"caseline_still_{f:04d}.png")
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
        try:
            encode_video(fdir, FPS, step, os.path.join(out, "caseline_30s.mp4"))
            print("[CaseLine] video ->", os.path.join(out, "caseline_30s.mp4"))
        except Exception as ex:
            print("[CaseLine] 영상 합치기 실패 (frames/ PNG 는 남아 있음):", ex)
    print("[CaseLine] done ->", out)

if __name__ == "__main__":
    main()
