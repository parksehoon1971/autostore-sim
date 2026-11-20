"""
수평형 + 수직형 캐러셀 (회전 선반) 피킹 — 사실적 Blender 장면 + 시뮬레이션 기반 30초 애니메이션
(Blender 4.2 ~ 5.x, Cycles / EEVEE)

사용 예 (저장소 루트에서)
  node tools/export_carousel_trace.js --warmup 300 --seed 22   # blender/carousel_trace.json 생성
  # 1) 장면 생성 + 정지 이미지 (+ Unity용 FBX 내보내기)
  blender -b -P blender/carousel_blender.py -- --out blender/out/carousel --trace blender/carousel_trace.json --still 181,586
  # 2) 30초 영상 (GPU 권장)
  blender -b -P blender/carousel_blender.py -- --out blender/out/carousel --trace blender/carousel_trace.json --anim --gpu --res 1920x1080 --samples 128

옵션
  --out DIR          출력 폴더
  --trace FILE       tools/export_carousel_trace.js 로 만든 궤적 JSON (필수)
  --still N[,M..]    지정 프레임 정지 렌더 (PNG)
  --anim             전체 애니메이션 렌더 (PNG 시퀀스 → frames/ ; ffmpeg 있으면 mp4로 합침)
  --frames A-B       렌더할 프레임 범위
  --res WxH          해상도 (기본 1920x1080)
  --samples N        Cycles 샘플 (기본 96)
  --step N           N프레임마다 렌더 (미리보기용)
  --gpu              GPU 렌더 (OPTIX > CUDA > HIP > METAL 자동)
  --engine eevee     EEVEE 로 렌더 (GPU 없는 PC에서 빠름)
  --no-export        FBX/GLB 내보내기 생략
  --no-caption       장비 이름 자막(합성) 생략

치수·속도는 공개 카탈로그의 일반적인 범위에서 잡은 가정입니다 (docs/carousel-research.md). 특정 제조사 도면이 아니며 로고는 넣지 않았습니다.
"""
import bpy, bmesh, math, random, sys, os, json, shutil, subprocess
from mathutils import Vector

def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else argv[1:]
    a = {"out": "./out", "trace": None, "still": None, "anim": False, "frames": None, "res": "1920x1080",
         "samples": 96, "gpu": False, "no-export": False, "no-caption": False, "step": 1, "engine": "cycles"}
    i = 0
    while i < len(argv):
        k = argv[i].lstrip("-")
        if k in ("anim", "gpu", "no-export", "no-caption"):
            a[k] = True; i += 1
        elif k in a:
            a[k] = argv[i + 1]; i += 2
        else:
            i += 1
    a["out"] = os.path.abspath(a["out"])
    return a

# 시뮬레이션 설정 (trace.json 의 cfg 로 덮어씀). web/carousel-sim-core.js DEFAULTS 와 같은 값
C = dict(hCount=2, hCarriers=26, hPitch=0.62, hR=0.40, hW=0.52, hD=0.46, hShelves=6, hBins=2, hShelf0=0.30, hShelfH=0.33,
         hTopZ=2.45, hx=[0, 2.3], hSlots=8, vCarriers=20, vPitch=0.66, vR=0.46, vW=2.45, vD=0.45, vH=0.40, vBins=6,
         vx=6.6, vFrontY=0.33, vZb=0.80, vWinZ=1.05, vSlots=4)
G = {}   # 파생 값: hL, hY0, vL, vZt, vBackY, hPer, vPer

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
        sc.view_settings.exposure = -0.6
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
                    sc.cycles.device = "GPU"; print("[Carousel] GPU:", dev); break
            except Exception:
                continue
    elif not eevee:
        sc.cycles.device = "CPU"
    world = bpy.data.worlds.new("World"); sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.78, 0.80, 0.83, 1); bg.inputs[1].default_value = 0.35

# ---------------------------------------------------------------- 키프레임
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

# ---------------------------------------------------------------- 파생 치수
def derive():
    c = C
    G["hPer"] = c["hCarriers"] * c["hPitch"]
    G["hL"] = (G["hPer"] - 2 * math.pi * c["hR"]) / 2
    G["hY0"] = c["hR"] + c["hD"]
    G["vPer"] = c["vCarriers"] * c["vPitch"]
    G["vL"] = (G["vPer"] - 2 * math.pi * c["vR"]) / 2
    G["vZt"] = c["vZb"] + G["vL"]
    G["vBackY"] = c["vFrontY"] + 2 * c["vR"]
    G["vTop"] = G["vZt"] + c["vR"] + c["vH"] / 2 + 0.25      # 캐비닛 높이
    G["vXi"] = c["vW"] / 2 + 0.14                             # 캐비닛 옆판 x 반폭
    G["vYb"] = G["vBackY"] + c["vD"] / 2 + 0.09               # 뒤판 y

def fmod(a, n): return ((a % n) + n) % n

# ---------------------------------------------------------------- 궤도 형상 (web/carousel-sim-core.js 와 같은 식)
def h_path(k, s):
    r, L, x0, y0 = C["hR"], G["hL"], C["hx"][k], G["hY0"]
    s = fmod(s, G["hPer"]); q = math.pi * r / 2
    if s < q: a = -math.pi / 2 + s / r; return x0 + r * math.cos(a), y0 + r * math.sin(a), a
    s -= q
    if s < L: return x0 + r, y0 + s, 0.0
    s -= L
    if s < 2 * q: a = s / r; return x0 + r * math.cos(a), y0 + L + r * math.sin(a), a
    s -= 2 * q
    if s < L: return x0 - r, y0 + L - s, math.pi
    s -= L
    a = math.pi + s / r; return x0 + r * math.cos(a), y0 + r * math.sin(a), a

def h_pose(k, i, step):
    s = fmod((i + step) * C["hPitch"], G["hPer"])
    x, y, a = h_path(k, s); h = C["hD"] / 2
    return x + math.cos(a) * h, y + math.sin(a) * h, a

def v_path(u):
    r, yF, yB, zb, zt = C["vR"], C["vFrontY"], G["vBackY"], C["vZb"], G["vZt"]
    u = fmod(u, G["vPer"]); a0 = zt - C["vWinZ"]
    if u < a0: return yF, C["vWinZ"] + u
    u -= a0; hc = math.pi * r
    if u < hc: a = u / r; return yF + r - r * math.cos(a), zt + r * math.sin(a)
    u -= hc
    if u < G["vL"]: return yB, zt - u
    u -= G["vL"]
    if u < hc: a = u / r; return yB - r + r * math.cos(a), zb - r * math.sin(a)
    u -= hc
    return yF, zb + u

def v_pose(i, step):
    return v_path(fmod((i + step) * C["vPitch"], G["vPer"]))

# 선반 단 / 빈 / 주문 칸 위치 (Unity CarouselView 와 같은 값)
def h_shelf_z(l): return C["hShelf0"] + l * C["hShelfH"]
def h_bin_x(b): return (b - (C["hBins"] - 1) / 2) * (C["hW"] - 0.02) / C["hBins"]
def v_bin_x(b): return -C["vW"] / 2 + 0.05 + (b + 0.5) * (C["vW"] - 0.10) / C["vBins"]
def h_slot_pos(s):
    col, row = s % 4, s // 4
    return (sum(C["hx"]) / 2 + (col - 1.5) * 0.50, -1.60, 0.80 + row * 0.42)
def v_slot_pos(s):
    return (C["vx"] + (s - 1.5) * 0.50, -1.35, 0.86)

# ---------------------------------------------------------------- 재질 (절차적 PBR)
def build_materials():
    M = {}
    m, nt, b = principled("FramePowder", (0.80, 0.81, 0.82), metal=0.0, rough=0.38, Coat_Weight=0.2)
    add_noise_bump(nt, b, scale=300, strength=0.01); M["frame"] = m
    m, nt, b = principled("WireZinc", (0.72, 0.73, 0.74), metal=1.0, rough=0.32)
    add_noise_bump(nt, b, scale=60, strength=0.02, rough_var=(0.25, 0.42)); M["wire"] = m
    M["track"] = principled("TrackSteel", (0.42, 0.43, 0.45), metal=1.0, rough=0.4)[0]
    M["beam"] = principled("BeamBlue", (0.05, 0.20, 0.45), rough=0.4, Coat_Weight=0.3)[0]
    for nm, col in (("BinBlue", (0.03, 0.17, 0.48)), ("BinGrey", (0.30, 0.31, 0.33)), ("BinYellow", (0.85, 0.60, 0.04))):
        m, nt, b = principled(nm, col, rough=0.45)
        add_noise_bump(nt, b, scale=500, strength=0.02, rough_var=(0.38, 0.52)); M[nm] = m
    M["label"] = principled("BinLabel", (0.95, 0.95, 0.93), rough=0.55)[0]
    M["panel"] = principled("CabinetWhite", (0.88, 0.89, 0.90), rough=0.32, Coat_Weight=0.3, Coat_Roughness=0.1)[0]
    M["panelDark"] = principled("CabinetGraphite", (0.13, 0.14, 0.15), metal=0.2, rough=0.4)[0]
    M["accent"] = principled("AccentBlue", (0.0, 0.30, 0.65), rough=0.35, Coat_Weight=0.4)[0]
    m, nt, b = principled("Glass", (0.92, 0.95, 0.96), rough=0.0, Transmission_Weight=1.0, IOR=1.05)
    try: m.blend_method = "BLEND"
    except Exception: pass
    M["glass"] = m
    M["tray"] = principled("TrayAlu", (0.74, 0.75, 0.77), metal=1.0, rough=0.3)[0]
    M["rubber"] = principled("Rubber", (0.015, 0.015, 0.015), rough=0.85)[0]
    M["chain"] = principled("ChainSteel", (0.25, 0.26, 0.27), metal=1.0, rough=0.35)[0]
    M["black"] = principled("BlackPlastic", (0.02, 0.02, 0.022), rough=0.5)[0]
    M["ledOff"] = principled("LEDOff", (0.05, 0.08, 0.06), rough=0.2, Coat_Weight=1.0)[0]
    M["ledOn"] = principled("LEDGreen", (0.1, 1.0, 0.35), rough=0.3, Emission_Color=(0.1, 1.0, 0.35, 1), Emission_Strength=14.0)[0]
    M["ledAmber"] = principled("LEDAmber", (1.0, 0.55, 0.05), rough=0.3, Emission_Color=(1.0, 0.55, 0.05, 1), Emission_Strength=10.0)[0]
    M["ledRed"] = principled("LEDRed", (1.0, 0.1, 0.05), rough=0.3, Emission_Color=(1.0, 0.1, 0.05, 1), Emission_Strength=8.0)[0]
    M["screen"] = principled("Screen", (0.02, 0.1, 0.25), rough=0.15, Emission_Color=(0.35, 0.6, 1.0, 1), Emission_Strength=2.5)[0]
    M["alu"] = principled("Aluminium", (0.78, 0.79, 0.80), metal=1.0, rough=0.28)[0]
    M["yellow"] = principled("SafetyYellow", (0.92, 0.65, 0.03), rough=0.45)[0]
    M["mat"] = principled("FloorMat", (0.06, 0.06, 0.065), rough=0.9)[0]
    M["tote"] = principled("ToteGrey", (0.20, 0.21, 0.22), rough=0.48)[0]
    for nm, col in (("Cardboard", (0.52, 0.36, 0.20)), ("CardboardWhite", (0.78, 0.74, 0.68)), ("ProdBlue", (0.08, 0.25, 0.62)),
                    ("ProdRed", (0.70, 0.08, 0.06)), ("ProdGreen", (0.12, 0.45, 0.22)), ("ProdYellow", (0.90, 0.72, 0.10))):
        m, nt, b = principled(nm, col, rough=0.6)
        add_noise_bump(nt, b, scale=90, strength=0.05); M[nm] = m
    M["items"] = [M[k] for k in ("Cardboard", "CardboardWhite", "ProdBlue", "ProdRed", "ProdGreen", "ProdYellow")]
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

# 겹치는 면 없이 (z-fighting 방지) 얇은 판은 붙는 면에서 2 mm 이상 띄움
GAP = 0.002

def shelf_bin(P, M, rnd, w, d, h, x, y, z, bm):
    """앞이 낮은 선반용 플라스틱 빈 (바닥 + 세 벽 + 낮은 앞벽 + 라벨) + 안의 물건. (x, y, z) = 바닥 아래 중심, +y = 앞"""
    t = 0.006
    P.box((w, d, t), (x, y, z + t / 2), bm)
    P.box((w, t, h), (x, y - d / 2 + t / 2, z + h / 2), bm)
    for sx in (-1, 1):
        P.box((t, d - 2 * t, h * 0.9), (x + sx * (w / 2 - t / 2), y, z + h * 0.45), bm)
    P.box((w, t, h * 0.5), (x, y + d / 2 - t / 2, z + h * 0.25), bm)
    P.box((w * 0.40, GAP, h * 0.22), (x, y + d / 2 + GAP / 2 + 0.001, z + h * 0.27), M["label"])
    # 물건 1~2개 (벽에서 4 mm 이상 띄움)
    n = 1 + (rnd.random() < 0.5)
    xx = x - w / 2 + t + 0.006
    for _ in range(n):
        sx = (w - 2 * t - 0.02) / n - 0.006
        sz = h * (0.35 + rnd.random() * 0.55); sy = d * (0.45 + rnd.random() * 0.4)
        P.box((sx, sy, sz), (xx + sx / 2, y - d / 2 + t + 0.006 + sy / 2, z + t + 0.002 + sz / 2), rnd.choice(M["items"]))
        xx += sx + 0.008

def make_h_carrier(M, coll, name, seed):
    """수평형 캐리어 (로컬: x 접선 = 폭, +y 바깥쪽 = 빈 앞, z 바닥부터). 원점 = 캐리어 바닥 중심"""
    W, D = C["hW"], C["hD"]; rnd = random.Random(seed)
    P = Parts(); Wd = Parts()
    zt = C["hShelf0"] + (C["hShelves"] - 1) * C["hShelfH"] + 0.37
    for sx in (-1, 1):
        for sy in (-1, 1):
            P.box((0.022, 0.022, zt - 0.10), (sx * (W / 2 - 0.011), sy * (D / 2 - 0.011), 0.10 + (zt - 0.10) / 2), M["frame"])
    P.box((W, D, 0.03), (0, 0, zt + 0.015), M["frame"])
    P.box((0.04, 0.04, C["hTopZ"] - zt - 0.03), (0, -D / 2 + 0.03, (zt + 0.03 + C["hTopZ"]) / 2), M["track"])
    P.box((0.10, 0.05, 0.05), (0, -D / 2 + 0.03, C["hTopZ"] - 0.02), M["black"])                     # 트롤리
    P.cyl(0.035, 0.03, (0, -D / 2 + 0.03, 0.07), M["rubber"])                                         # 아래 가이드 롤러
    P.box((W - 0.05, 0.03, 0.03), (0, -D / 2 + 0.03, 0.10), M["frame"])
    bins = [M["BinBlue"], M["BinBlue"], M["BinGrey"], M["BinYellow"]]
    for l in range(C["hShelves"]):
        z = h_shelf_z(l)
        # 와이어 선반: 테두리 + 가로 철사 (판과 겹치지 않게 각자 떨어진 막대)
        for sy in (-1, 1): Wd.box((W - 0.05, 0.012, 0.016), (0, sy * (D / 2 - 0.02), z - 0.008), M["wire"])
        for sx in (-1, 1): Wd.box((0.012, D - 0.052, 0.016), (sx * (W / 2 - 0.03), 0, z - 0.008), M["wire"])
        for k in range(9): Wd.box((W - 0.072, 0.004, 0.004), (0, -D / 2 + 0.05 + k * (D - 0.10) / 8, z - 0.004), M["wire"])
        bm = bins[(l + seed) % len(bins)] if rnd.random() < 0.85 else M["BinGrey"]
        for b in range(C["hBins"]):
            bw = (W - 0.04) / C["hBins"] - 0.008
            shelf_bin(P, M, rnd, bw, D - 0.06, 0.17, h_bin_x(b), 0.0, z + GAP, bm)
    ob = P.build(name, coll, bev=0.0015)
    wo = Wd.build(name + "_Wire", coll)
    wo.parent = ob
    return ob

def make_v_tray(M, coll, name, seed):
    """수직형 트레이 (로컬 원점 = 트레이 중심, 늘 수평. +y 뒤, -y 앞 = 작업창 쪽)"""
    W, D, H = C["vW"], C["vD"], C["vH"]; rnd = random.Random(seed)
    P = Parts()
    zf = -H / 2 + 0.04
    P.box((W, D, 0.02), (0, 0, zf - 0.01), M["tray"])                                # 트레이 바닥 (윗면 zf)
    P.box((W, 0.015, 0.10), (0, D / 2 - 0.0075, zf + 0.05 + GAP), M["tray"])          # 뒤 턱
    P.box((W, 0.012, 0.035), (0, -D / 2 + 0.006, zf + 0.0175 + GAP), M["tray"])       # 앞 턱
    for sx in (-1, 1):
        # 옆 걸이: 가운데 세로 띠 + 아래 받침 (옆에서도 빈이 보이게 판 대신 띠)
        P.box((0.015, 0.05, H - 0.02), (sx * (W / 2 + 0.0075 + GAP), 0, 0), M["frame"])
        P.box((0.015, D, 0.03), (sx * (W / 2 + 0.0075 + GAP), 0, zf - 0.005), M["frame"])
        P.cyl(0.02, 0.05, (sx * (W / 2 + 0.04), 0, H / 2 - 0.05), M["track"], axis="X")
    for b in range(C["vBins"]):
        bw = (W - 0.10) / C["vBins"] - 0.02
        bm = M["BinBlue"] if (b + seed) % 5 else M["BinGrey"]
        # 트레이에서는 빈 앞이 -y (작업창 쪽): 빈을 만든 뒤 y 를 뒤집음
        Q = Parts(); shelf_bin(Q, M, rnd, bw, D - 0.08, 0.15, 0, 0, 0, bm)
        for v in Q.bm.verts: v.co.y = -v.co.y; v.co.x += v_bin_x(b); v.co.z += zf + GAP
        for f in Q.bm.faces: f.normal_flip()
        merge_parts(P, Q)
    return P.build(name, coll, bev=0.0015)

def merge_parts(P, Q):
    """Q 의 bmesh 를 P 에 합침 (재질 인덱스 맞춤)"""
    me = bpy.data.meshes.new("tmp"); Q.bm.to_mesh(me); Q.bm.free()
    remap = [P._mi(m) for m in Q.mats]
    base = len(P.bm.faces)
    P.bm.from_mesh(me)
    P.bm.faces.ensure_lookup_table()
    for f in P.bm.faces[base:]: f.material_index = remap[f.material_index] if f.material_index < len(remap) else 0
    bpy.data.meshes.remove(me)

# ---------------------------------------------------------------- 수평형 구조물 (궤도·프레임·피킹 라이트)
def make_h_structure(M, coll):
    P = Parts(); lights = []
    for k in range(C["hCount"]):
        x0, y0, L, r, D = C["hx"][k], G["hY0"], G["hL"], C["hR"], C["hD"]
        ztr = C["hTopZ"] + 0.035
        # 위 궤도 (캐리어 걸이 선을 따라 짧은 상자를 이어 붙임) + 바닥 가이드 레일
        n = int(G["hPer"] / 0.08)
        for i in range(n):
            s = (i + 0.5) * G["hPer"] / n
            x, y, a = h_path(k, s); off = 0.03
            px, py = x + math.cos(a) * off, y + math.sin(a) * off
            ln = G["hPer"] / n + 0.004
            rot = a + math.pi / 2
            box_rot(P, (ln, 0.07, 0.05), (px, py, ztr), rot, M["track"])
            box_rot(P, (ln, 0.06, 0.012), (px, py, 0.006), rot, M["track"])
        # 가운데 등뼈 빔 + 기둥 + 가로 팔 (캐리어 안쪽 면 사이 0.8 m 공간)
        P.box((0.10, L + 0.2, 0.12), (x0, y0 + L / 2, ztr + 0.10), M["beam"])
        npost = 5
        for j in range(npost):
            y = y0 + L * j / (npost - 1)
            P.box((0.09, 0.09, ztr + 0.04), (x0, y, (ztr + 0.04) / 2), M["beam"])
            P.box((2 * r - 0.04, 0.05, 0.05), (x0, y, ztr + 0.06), M["beam"])
            P.box((0.30, 0.30, 0.012), (x0, y, 0.006), M["steel"])
        for j in range(9):
            y = y0 + L * j / 8
            P.box((2 * r - 0.02, 0.03, 0.03), (x0, y, ztr + 0.025 + 0.03), M["track"])
        # 뒤쪽 끝 구동부
        P.box((0.42, 0.42, 0.30), (x0, y0 + L, ztr + 0.31), M["panelDark"])
        P.cyl(0.10, 0.20, (x0, y0 + L, ztr + 0.55), M["steel"])
        # 바닥 표시 (캐리어 돌아가는 영역 둘레)
        e = r + D + 0.15
        for sx in (-1, 1): P.box((0.06, L + 2 * e, 0.003), (x0 + sx * e, y0 + L / 2, 0.0015), M["yellow"])
        P.box((2 * e + 0.06, 0.06, 0.003), (x0, y0 + L + e, 0.0015), M["yellow"])
        # 피킹 라이트 기둥 (두 캐러셀 사이 쪽, 피킹면 앞)
        sx = 1 if k == 0 else -1
        lx, ly = x0 + sx * 0.42, -0.12
        P.box((0.05, 0.05, 2.35), (lx, ly, 1.175), M["panelDark"])
        P.box((0.18, 0.05, 0.10), (lx, ly - 0.03, 2.30), M["black"])                     # 수량 표시창
        P.box((0.15, GAP, 0.06), (lx, ly - 0.056 - GAP / 2, 2.30), M["screen"])
        row = []
        for l in range(C["hShelves"]):
            z = h_shelf_z(l) + 0.10
            P.box((0.08, 0.03, 0.05), (lx, ly - 0.04, z), M["black"])
            P.box((0.05, GAP, 0.03), (lx, ly - 0.056 - GAP / 2, z), M["ledOff"])
            row.append(led_obj(M, coll, f"HLed_{k}_{l}", (lx, ly - 0.059, z), (0.052, 0.004, 0.032)))
        lights.append(row)
    # 작업자 매트 + 뒤 주문 선반 (put wall, 작업자 쪽 +y 를 향함)
    cx = sum(C["hx"]) / 2
    P.box((3.4, 1.0, 0.012), (cx, -0.85, 0.006), M["mat"])
    puts = []
    for row_ in range(2):
        z = 0.80 + row_ * 0.42
        P.box((2.1, 0.46, 0.025), (cx, -1.60, z - 0.0125 - GAP), M["alu"])
    for xx in (cx - 1.05, cx + 1.05):
        for yy in (-1.83, -1.37):
            P.box((0.035, 0.035, 1.35), (xx, yy, 0.675), M["alu"])
    for s in range(C["hSlots"]):
        x, y, z = h_slot_pos(s)
        tote(P, M, (x, y, z), 0.40, 0.36, 0.22)
        P.box((0.06, GAP, 0.02), (x, y + 0.23 + GAP / 2 + 0.002, z - 0.04), M["ledOff"])
        puts.append(led_obj(M, coll, f"HPut_{s}", (x, y + 0.237, z - 0.04), (0.062, 0.004, 0.022)))
    P.build("HorizontalStructure", coll, bev=0.0)
    return lights, puts

def tote(P, M, c, w, d, h):
    x, y, z = c; t = 0.008
    P.box((w, d, t), (x, y, z + t / 2), M["tote"])
    for sy in (-1, 1): P.box((w, t, h), (x, y + sy * (d / 2 - t / 2), z + h / 2), M["tote"])
    for sx in (-1, 1): P.box((t, d - 2 * t, h), (x + sx * (w / 2 - t / 2), y, z + h / 2), M["tote"])

def box_rot(P, size, loc, rot, m):
    mi = P._mi(m)
    r = bmesh.ops.create_cube(P.bm, size=1.0)
    cs, sn = math.cos(rot), math.sin(rot)
    for v in r["verts"]:
        x, y, z = v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]
        v.co = Vector((loc[0] + x * cs - y * sn, loc[1] + x * sn + y * cs, loc[2] + z))
    for f in {f for v in r["verts"] for f in v.link_faces}: f.material_index = mi

def led_obj(M, coll, name, loc, size):
    """켜진 LED (꺼진 LED 앞 1 mm 에 놓이고 hide_render 로 켜고 끔)"""
    P = Parts(); P.box(size, (0, 0, 0), M["ledOn"])
    ob = P.build(name, coll); ob.location = loc
    return ob

# ---------------------------------------------------------------- 수직형 캐비닛
def make_v_structure(M, coll):
    P = Parts(); G_ = Parts()
    x0, W = C["vx"], C["vW"]; xi = G["vXi"]; yb = G["vYb"]; top = G["vTop"]
    zw0, zw1 = C["vWinZ"] - C["vH"] / 2 - 0.05, C["vWinZ"] + C["vH"] / 2 + 0.10
    # 기둥 (네 모서리) + 받침
    for sx in (-1, 1):
        for y in (0.0, yb):
            P.box((0.08, 0.08, top), (x0 + sx * xi, y, top / 2), M["panelDark"])
    P.box((2 * xi + 0.08, yb + 0.08, 0.10), (x0, yb / 2, 0.05), M["panelDark"])
    P.box((2 * xi + 0.08, yb + 0.08, 0.08), (x0, yb / 2, top + 0.04), M["panel"])
    # 앞판: 작업창 아래 / 위 (작업창 = 트레이가 서는 높이), 파란 띠
    fw = 2 * xi - 0.08
    P.box((fw, 0.02, zw0 - 0.10), (x0, 0.0, 0.10 + (zw0 - 0.10) / 2), M["panel"])
    # 작업창 위 앞판: 가운데 세로로 긴 점검 유리창 (안의 트레이 순환이 보이게) + 둘레 판
    gz0, gz1, gw = zw1 + 0.35, top - 0.75, fw - 0.50
    P.box((fw, 0.02, gz0 - zw1), (x0, 0.0, (zw1 + gz0) / 2), M["panel"])
    P.box((fw, 0.02, top - gz1), (x0, 0.0, (gz1 + top) / 2), M["panel"])
    for sx in (-1, 1):
        P.box(((fw - gw) / 2, 0.02, gz1 - gz0), (x0 + sx * (gw + fw) / 4, 0.0, (gz0 + gz1) / 2), M["panel"])
    G_.box((gw, 0.008, gz1 - gz0), (x0, 0.0, (gz0 + gz1) / 2), M["glass"])
    z = gz0 + 1.25
    while z < gz1 - 0.3:                                                       # 유리창 가로 살
        P.box((gw, 0.03, 0.03), (x0, -0.02, z), M["panelDark"]); z += 1.25
    for zz in (gz0, gz1):
        P.box((gw + 0.06, 0.03, 0.04), (x0, -0.02, zz), M["panelDark"])
    for sx in (-1, 1):
        P.box((0.04, 0.03, gz1 - gz0), (x0 + sx * (gw / 2 + 0.01), -0.02, (gz0 + gz1) / 2), M["panelDark"])
    P.box((fw, GAP, 0.08), (x0, -0.01 - GAP / 2 - 0.001, top - 0.40), M["accent"])
    # 작업창 둘레 + 카운터 + 위쪽 라이트 바
    P.box((fw + 0.04, 0.24, 0.03), (x0, -0.10, zw0 - 0.015), M["panelDark"])
    P.box((fw, 0.06, 0.05), (x0, -0.03, zw1 + 0.025 + GAP), M["panelDark"])
    leds = []
    for b in range(C["vBins"]):
        x = x0 + v_bin_x(b)
        P.box((0.10, GAP, 0.025), (x, -0.06 - GAP / 2 - 0.001, zw1 + 0.025 + GAP), M["ledOff"])
        leds.append(led_obj(M, coll, f"VLed_{b}", (x, -0.065, zw1 + 0.025 + GAP), (0.102, 0.004, 0.027)))
    # 조작반 (오른쪽)
    P.box((0.05, 0.05, 0.55), (x0 + xi + 0.12, -0.05, zw1 + 0.05), M["alu"])
    P.box((0.36, 0.04, 0.26), (x0 + xi + 0.12, -0.10, zw1 + 0.38), M["black"])
    P.box((0.33, GAP, 0.23), (x0 + xi + 0.12, -0.12 - GAP / 2 - 0.001, zw1 + 0.38), M["screen"])
    # 뒤판 (불투명) + 옆판 유리 (안의 트레이가 보이도록)
    P.box((fw, 0.02, top - 0.10), (x0, yb, 0.10 + (top - 0.10) / 2), M["panel"])
    for sx in (-1, 1):
        G_.box((0.012, yb - 0.08, top - 0.30), (x0 + sx * xi, yb / 2, 0.10 + (top - 0.30) / 2 + 0.05), M["glass"])
        for z in (0.12, top - 0.10):
            P.box((0.05, yb - 0.08, 0.06), (x0 + sx * xi, yb / 2, z), M["panelDark"])
        for frac in (1 / 3, 2 / 3):
            P.box((0.05, yb - 0.08, 0.04), (x0 + sx * xi, yb / 2, 0.12 + frac * (top - 0.22)), M["panelDark"])
    # 체인 (양옆 고정 루프) + 스프로킷
    for sx in (-1, 1):
        xc = x0 + sx * (W / 2 + 0.06)
        for side, y in ((0, C["vFrontY"]), (1, G["vBackY"])):
            P.box((0.02, 0.03, G["vL"]), (xc, y, C["vZb"] + G["vL"] / 2), M["chain"])
        for zc in (C["vZb"], G["vZt"]):
            P.cyl(C["vR"] - 0.03, 0.02, (xc, (C["vFrontY"] + G["vBackY"]) / 2, zc), M["chain"], axis="X", seg=32)
            P.cyl(0.05, 0.08, (xc, (C["vFrontY"] + G["vBackY"]) / 2, zc), M["track"], axis="X")
    # 작업자 매트 + 주문 카트
    P.box((2.6, 0.9, 0.012), (x0, -0.70, 0.006), M["mat"])
    P.box((2.1, 0.46, 0.025), (x0, -1.35, 0.86 - 0.0125 - GAP), M["alu"])
    P.box((2.1, 0.46, 0.025), (x0, -1.35, 0.30), M["alu"])
    for xx in (x0 - 1.0, x0 + 1.0):
        for yy in (-1.55, -1.15):
            P.box((0.035, 0.035, 0.95), (xx, yy, 0.475 + 0.06), M["alu"])
            P.cyl(0.05, 0.03, (xx, yy, 0.05), M["rubber"], axis="X")
    puts = []
    for s in range(C["vSlots"]):
        x, y, z = v_slot_pos(s)
        tote(P, M, (x, y, z), 0.40, 0.36, 0.22)
        P.box((0.06, GAP, 0.02), (x, y + 0.23 + GAP / 2 + 0.002, z - 0.04), M["ledOff"])
        puts.append(led_obj(M, coll, f"VPut_{s}", (x, y + 0.237, z - 0.04), (0.062, 0.004, 0.022)))
    P.build("VerticalCabinet", coll, bev=0.0)
    go = G_.build("VerticalGlass", coll)
    go.visible_shadow = False
    return leds, puts

# ---------------------------------------------------------------- 건물
def make_building(M, coll):
    cxw, cyw = 3.6, 3.0
    S = 50.0; Hh = 9.0
    P = Parts()
    P.box((S, S, 0.02), (cxw, cyw, -0.01), M["floor"])
    P.box((S * 0.8, 0.12, 0.004), (cxw, -3.2, 0.002), M["greenline"])
    P.box((S, 0.2, Hh), (cxw, cyw + 14, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw - 14, cyw, Hh / 2), M["wall"])
    P.box((0.2, S, Hh), (cxw + 16, cyw, Hh / 2), M["wall"])
    P.box((S, 0.2, Hh), (cxw, cyw - 18, Hh / 2), M["wall"])
    P.box((S, S, 0.2), (cxw, cyw, Hh), M["ceiling"])
    for k in range(-12, 16, 4):
        P.box((0.3, S, 0.5), (cxw + k, cyw, Hh - 0.35), M["steel"])
    P.box((S, 0.5, 0.06), (cxw, cyw + 13.8, 1.2), M["greenline"])
    for k in range(-12, 16, 7):
        P.box((0.4, 0.4, Hh), (cxw + k, cyw + 13.7, Hh / 2), M["steel"])
    P.build("Shell", coll)
    L = Parts()
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            L.box((1.8, 0.45, 0.05), (cxw + ix * 4.5, cyw + iy * 4.5, Hh - 0.7), M["lightpanel"])
    L.build("LightPanels", coll)
    for ix in range(-2, 3):
        for iy in range(-2, 3):
            ld = bpy.data.lights.new("HighBay", "AREA"); ld.shape = "RECTANGLE"; ld.size = 1.8; ld.size_y = 0.45
            ld.energy = 260; ld.color = (1.0, 0.98, 0.95)
            lo = bpy.data.objects.new("HighBay", ld); lo.location = (cxw + ix * 4.5, cyw + iy * 4.5, Hh - 0.75); link_coll(lo, coll)

# ---------------------------------------------------------------- 카메라
# 30초 동선 (프레임 비율, 위치, 바라보는 점, 렌즈 mm). Unity CarouselCamera.cs 와 같은 값. seed 22, warmup 300 s 기준
CAM_KEYS = [
    (0.000, (-5.2, -6.8, 4.6), (3.4, 2.2, 1.4), 24),     # 전경: 왼쪽 수평형 포드 2대 + 오른쪽 수직형
    (0.110, (-3.6, -4.2, 2.6), (0.3, 0.6, 1.2), 26),     # 수평형 피킹면으로 다가감
    (0.200, (-1.9, -1.6, 1.2), (0.0, 0.5, 1.1), 24),     # 낮게: 앞쪽 끝에서 캐리어가 돌아 나옴 (5.8~12.6 s 회전)
    (0.300, (-1.5, 1.4, 1.5), (0.0, 4.5, 1.2), 22),      # 옆을 따라가며 흘러가는 캐리어
    (0.400, (-1.5, 5.0, 1.6), (0.2, 7.5, 1.3), 22),
    (0.440, (-2.8, 1.5, 3.8), (0.6, 2.5, 1.0), 24),      # 옆으로 비켜 올라감
    (0.480, (1.15, -3.2, 5.2), (1.15, 2.2, 0.9), 24),    # 위로 빠져 포드 전체 (캐러셀 2 회전 12.5~17.9 s)
    (0.567, (4.4, -4.2, 3.4), (6.6, 0.6, 2.4), 24),      # 수직형으로 넘어감 (캐비닛 전체)
    (0.650, (5.6, -1.9, 1.5), (6.6, 0.3, 1.1), 30),      # 작업창: 피킹 라이트 + 물건을 꺼내 주문 토트로
    (0.750, (5.0, -2.4, 2.0), (6.6, 0.3, 2.0), 26),      # 앞 유리창으로 트레이가 내려오기 시작 (22.3 s~)
    (0.867, (5.4, -2.6, 4.7), (6.6, 0.3, 4.4), 26),      # 카메라는 올라가고 트레이는 내려감
    (0.933, (4.6, -4.6, 4.6), (6.6, 0.6, 3.0), 24),
    (1.000, (12.0, -7.5, 5.0), (3.6, 2.4, 2.2), 24),     # 빠지며 전경
]

def _cr(p0, p1, p2, p3, t):
    t2, t3 = t * t, t * t * t
    return [0.5 * (2 * p1[i] + (-p0[i] + p2[i]) * t + (2 * p0[i] - 5 * p1[i] + 4 * p2[i] - p3[i]) * t2 + (-p0[i] + 3 * p1[i] - 3 * p2[i] + p3[i]) * t3) for i in range(3)]

def _clamp(v, a, b):
    return [min(max(v[i], min(a[i], b[i])), max(a[i], b[i])) for i in range(3)]

def _smooth(x): x = min(1.0, max(0.0, x)); return x * x * (3 - 2 * x)

def cam_eval(u):
    """Unity CarouselCamera.Evaluate 와 같은 식: 축마다 두 키 사이로 제한한 Catmull-Rom, 처음·끝만 부드럽게"""
    K = CAM_KEYS; n = len(K)
    i = 0
    while i < n - 2 and u > K[i + 1][0]: i += 1
    k0, k1, k2, k3 = K[max(0, i - 1)], K[i], K[i + 1], K[min(n - 1, i + 2)]
    s = 0.0 if k2[0] == k1[0] else min(1.0, max(0.0, (u - k1[0]) / (k2[0] - k1[0])))
    if i == 0: s = _smooth(s * 0.5) * 2
    if i == n - 2: s = _smooth(0.5 + s * 0.5) * 2 - 1
    pos = _clamp(_cr(k0[1], k1[1], k2[1], k3[1], s), k1[1], k2[1])
    look = _clamp(_cr(k0[2], k1[2], k2[2], k3[2], s), k1[2], k2[2])
    lens = k1[3] + (k2[3] - k1[3]) * _smooth(s)
    return pos, look, lens

def setup_camera(scene_coll, frames):
    cam_d = bpy.data.cameras.new("Cam"); cam_d.lens = 28; cam_d.sensor_width = 36; cam_d.clip_start = 0.03
    cam = bpy.data.objects.new("Camera", cam_d); link_coll(cam, scene_coll)
    tgt = bpy.data.objects.new("CamTarget", None); link_coll(tgt, scene_coll)
    con = cam.constraints.new("TRACK_TO"); con.target = tgt; con.track_axis = "TRACK_NEGATIVE_Z"; con.up_axis = "UP_Y"
    cam_d.dof.use_dof = True; cam_d.dof.focus_object = tgt; cam_d.dof.aperture_fstop = 5.6
    ev = [cam_eval(f / max(1, frames - 1)) for f in range(frames)]
    fr = list(range(1, frames + 1))
    for ax in range(3):
        bake_channel(cam, "location", ax, fr, [e[0][ax] for e in ev])
        bake_channel(tgt, "location", ax, fr, [e[1][ax] for e in ev])
    bake_channel(cam_d, "lens", -1, fr, [e[2] for e in ev])
    return cam

# 장비 이름 자막 (Unity CarouselCaption.cs 와 같은 시간·문구)
CAPTIONS = [
    (0.3, 3.4, "CAROUSEL STORAGE", "Horizontal pick pod  +  vertical carousel  ·  goods-to-person"),
    (3.8, 14.6, "HORIZONTAL CAROUSEL", "2 units × 26 carriers  ·  bidirectional  ·  pick-to-light pod"),
    (17.2, 28.0, "VERTICAL CAROUSEL", "20 trays  ·  2.45 m wide  ·  ergonomic window at 1.05 m"),
]

def draw_caption(path, t):
    """렌더된 PNG 위에 자막 합성 (Pillow 가 있을 때만)"""
    try:
        from PIL import Image, ImageDraw, ImageFont
    except Exception:
        return False
    cap = next((c for c in CAPTIONS if c[0] <= t <= c[1]), None)
    if not cap: return False
    t0, t1, title, sub = cap
    a = min(1.0, (t - t0) / 0.5, (t1 - t) / 0.5)
    im = Image.open(path).convert("RGBA"); W, H = im.size; s = H / 1080
    ov = Image.new("RGBA", im.size, (0, 0, 0, 0)); d = ImageDraw.Draw(ov)
    def font(sz, bold):
        for nm in (("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"), ("arialbd.ttf" if bold else "arial.ttf")):
            for base in ("", "/usr/share/fonts/truetype/dejavu/", "C:/Windows/Fonts/"):
                try: return ImageFont.truetype(base + nm, int(sz))
                except Exception: pass
        return ImageFont.load_default()
    ft, fs = font(46 * s, True), font(26 * s, False)
    slide = (1 - a) * 30 * s
    x, y = 84 * s - slide, H - 190 * s
    tw = max(d.textlength(title, font=ft) + 0.0 * s, d.textlength(sub, font=fs))
    d.rectangle([x - 28 * s, y - 22 * s, x + tw + 36 * s, y + 104 * s], fill=(12, 16, 22, int(150 * a)))
    d.rectangle([x - 28 * s, y - 22 * s, x - 22 * s, y + 104 * s], fill=(40, 160, 255, int(255 * a)))
    d.text((x, y - 4 * s), title, font=ft, fill=(255, 255, 255, int(255 * a)))
    d.text((x + 2 * s, y + 58 * s), sub, font=fs, fill=(200, 215, 230, int(235 * a)))
    Image.alpha_composite(im, ov).convert("RGB").save(path)
    return True

# ---------------------------------------------------------------- 애니메이션 (궤적)
def key_bool(ob, frames_vals):
    """hide_render 켜고 끄기 (바뀌는 프레임만 키)"""
    prev = None
    for f, v in frames_vals:
        if v != prev:
            ob.hide_render = v; ob.hide_viewport = v
            ob.keyframe_insert("hide_render", frame=f); ob.keyframe_insert("hide_viewport", frame=f)
            prev = v

def unwrap(vals):
    out = [vals[0]]
    for v in vals[1:]:
        p = out[-1]; v += round((p - v) / (2 * math.pi)) * 2 * math.pi; out.append(v)
    return out

def bin_world_h(k, carrier, step, shelf, b):
    x, y, a = h_pose(k, carrier, step)
    lx, ly = h_bin_x(b), 0.0
    # 로컬 +y = 바깥 법선 (각도 a), 로컬 +x = 접선
    rz = a - math.pi / 2
    wx_ = x + lx * math.cos(rz) - ly * math.sin(rz); wy_ = y + lx * math.sin(rz) + ly * math.cos(rz)
    return (wx_, wy_, h_shelf_z(shelf) + 0.10)

def bin_world_v(carrier, step, b):
    y, z = v_pose(carrier, step)
    return (C["vx"] + v_bin_x(b), y, z - C["vH"] / 2 + 0.12)

def item_path(src, dst, u):
    e = min(1.0, max(0.0, (u - 0.22) / 0.58)); e = e * e * (3 - 2 * e)
    p = [src[i] + (dst[i] - src[i]) * e for i in range(3)]
    p[2] += 0.28 * math.sin(math.pi * e)
    return p

def apply_trace(hcar, vtray, hleds, hputs, vleds, vputs, items, coll):
    tr = TRACE; nf = tr["frames"] + 1
    hc = C["hCount"]
    for k in range(hc):
        steps = tr["units"][k]
        for i, ob in enumerate(hcar[k]):
            xs, ys, rs = [], [], []
            for f in range(nf):
                x, y, a = h_pose(k, i, steps[f]); xs.append(x); ys.append(y); rs.append(a - math.pi / 2)
            rs = unwrap(rs)
            for ax, vals in ((0, xs), (1, ys)):
                fs, vs = compress(vals); bake_channel(ob, "location", ax, fs, vs)
            fs, vs = compress(rs); bake_channel(ob, "rotation_euler", 2, fs, vs)
    steps = tr["units"][hc]
    for i, ob in enumerate(vtray):
        ys, zs = [], []
        for f in range(nf):
            y, z = v_pose(i, steps[f]); ys.append(y); zs.append(z)
        ob.location.x = C["vx"]
        for ax, vals in ((1, ys), (2, zs)):
            fs, vs = compress(vals); bake_channel(ob, "location", ax, fs, vs)
    # 피킹 라이트 / 풋 라이트 / 옮겨지는 물건
    ops = tr["ops"]
    for oi, opt in enumerate(ops):
        it = items[oi]
        vis, xs, ys, zs = [], [], [], []
        led_state = {}
        for f in range(nf):
            at, st, u, unit, car, shelf, b, slot, wf = opt[f]
            on = st == 2
            if oi == 0:
                for k in range(hc):
                    for l in range(C["hShelves"]): led_state.setdefault(("h", k, l), []).append(on and unit == k and shelf == l and u < 0.55)
                for s in range(C["hSlots"]): led_state.setdefault(("hp", s), []).append(on and slot == s and u > 0.45)
            else:
                for bb in range(C["vBins"]): led_state.setdefault(("v", bb), []).append(on and b == bb and u < 0.55)
                for s in range(C["vSlots"]): led_state.setdefault(("vp", s), []).append(on and slot == s and u > 0.45)
            show = on and 0.18 < u < 0.82
            vis.append(show)
            if on:
                if oi == 0:
                    src = bin_world_h(unit, car, tr["units"][unit][f], shelf, b); dst = h_slot_pos(slot)
                else:
                    src = bin_world_v(car, tr["units"][hc][f], b); dst = v_slot_pos(slot)
                dst = (dst[0], dst[1], dst[2] + 0.10)
                p = item_path(src, dst, u)
            else:
                p = (it.location.x, it.location.y, -1.0) if not xs else (xs[-1], ys[-1], zs[-1])
            xs.append(p[0]); ys.append(p[1]); zs.append(p[2])
        for ax, vals in ((0, xs), (1, ys), (2, zs)):
            fs, vs = compress(vals); bake_channel(it, "location", ax, fs, vs)
        key_bool(it, [(f + 1, not v) for f, v in enumerate(vis)])
        for key, arr in led_state.items():
            if key[0] == "h": ob = hleds[key[1]][key[2]]
            elif key[0] == "hp": ob = hputs[key[1]]
            elif key[0] == "v": ob = vleds[key[1]]
            else: ob = vputs[key[1]]
            key_bool(ob, [(f + 1, not v) for f, v in enumerate(arr)])
    print(f"[Carousel] frames={nf} moves={len(tr['events'])}")
    return nf, tr["fps"]

def make_item(M, coll, name):
    P = Parts(); P.box((0.14, 0.12, 0.09), (0, 0, 0), M["ProdRed"]); P.box((0.06, GAP, 0.03), (0, -0.061 - GAP / 2, 0.01), M["label"])
    return P.build(name, coll, bev=0.003)

# ---------------------------------------------------------------- 메인
TRACE = None
def main():
    global TRACE
    a = parse_args()
    out = a["out"]; os.makedirs(out, exist_ok=True)
    random.seed(7)
    if not a["trace"]:
        raise SystemExit("--trace blender/carousel_trace.json 이 필요합니다 (node tools/export_carousel_trace.js)")
    TRACE = json.load(open(a["trace"]))
    for k in C:
        if k in TRACE["cfg"]: C[k] = TRACE["cfg"][k]
    derive()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    M = build_materials()

    # --- 개별 에셋 (Unity 용): 캐리어 3종, 트레이 2종
    assets = new_coll("Assets")
    hvar = [make_h_carrier(M, assets, f"HCarrier_v{v}", 11 + v) for v in range(3)]
    vvar = [make_v_tray(M, assets, f"VTray_v{v}", 21 + v) for v in range(2)]
    if not a["no-export"]:
        for v, ob in enumerate(hvar):
            export_fbx(ob, os.path.join(out, f"carousel_hcarrier_v{v}.fbx"))
        for v, ob in enumerate(vvar):
            export_fbx(ob, os.path.join(out, f"carousel_vtray_v{v}.fbx"), ("MESH",))
        export_glb(hvar[0], os.path.join(out, "carousel_hcarrier_v0.glb"))
        export_glb(vvar[0], os.path.join(out, "carousel_vtray_v0.glb"))
    assets.hide_render = True; assets.hide_viewport = True

    # --- 장면
    scene = new_coll("CarouselScene")
    make_building(M, scene)
    st = new_coll("Structure", scene)
    hleds, hputs = make_h_structure(M, st)
    vleds, vputs = make_v_structure(M, st)
    cc = new_coll("Carriers", scene)
    rnd = random.Random(5)
    hcar = []
    for k in range(C["hCount"]):
        row = []
        for i in range(C["hCarriers"]):
            src = hvar[rnd.randrange(3)]
            ob = bpy.data.objects.new(f"HCarrier_{k}_{i}", src.data); cc.objects.link(ob)
            w = bpy.data.objects.new(f"HCarrier_{k}_{i}_Wire", src.children[0].data); cc.objects.link(w); w.parent = ob
            row.append(ob)
        hcar.append(row)
    vtray = []
    for i in range(C["vCarriers"]):
        ob = bpy.data.objects.new(f"VTray_{i}", vvar[i % 2].data); cc.objects.link(ob); vtray.append(ob)
    items = [make_item(M, cc, "PickItem_H"), make_item(M, cc, "PickItem_V")]
    frames, fps = apply_trace(hcar, vtray, hleds, hputs, vleds, vputs, items, cc)
    setup_camera(scene, frames)
    bpy.context.scene.camera = bpy.data.objects["Camera"]
    setup_render(a, frames, fps)
    print(f"[Carousel] H {C['hCount']}x{C['hCarriers']}  V {C['vCarriers']}  frames={frames} fps={fps}")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "carousel_scene.blend"))

    sc = bpy.context.scene
    if a["still"]:
        for f in [int(v) for v in str(a["still"]).split(",")]:
            sc.frame_set(f)
            p = os.path.join(out, f"carousel_still_{f:04d}.png")
            sc.render.filepath = p
            bpy.ops.render.render(write_still=True)
            if not a["no-caption"]: draw_caption(p, (f - 1) / fps)
    if a["anim"]:
        if a["frames"]:
            s, e = [int(v) for v in a["frames"].split("-")]; sc.frame_start, sc.frame_end = s, e
        fdir = os.path.join(out, "frames"); os.makedirs(fdir, exist_ok=True)
        sc.render.image_settings.file_format = "PNG"
        sc.render.filepath = os.path.join(fdir, "f_")
        sc.render.use_overwrite = False; sc.render.use_placeholder = True
        step = int(a["step"]); sc.frame_step = step
        bpy.ops.render.render(animation=True)
        if not a["no-caption"]:
            for fn in sorted(os.listdir(fdir)):
                if fn.startswith("f_") and fn.endswith(".png"):
                    draw_caption(os.path.join(fdir, fn), (int(fn[2:-4]) - 1) / fps)
        ff = shutil.which("ffmpeg")
        if ff:
            mp4 = os.path.join(out, "carousel_30s.mp4")
            subprocess.run([ff, "-y", "-framerate", str(fps / step), "-pattern_type", "glob", "-i", os.path.join(fdir, "f_*.png"),
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", mp4])
            print("[Carousel] video ->", mp4)
        else:
            print("[Carousel] ffmpeg 없음: frames/ 폴더의 PNG를 영상 편집기로 합치세요")
    print("[Carousel] done ->", out)

if __name__ == "__main__":
    main()
