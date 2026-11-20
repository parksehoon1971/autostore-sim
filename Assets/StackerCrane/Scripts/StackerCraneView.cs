// 팔레트용 스태커크레인 (단위 하중 AS/RS): 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + 장비명 자막 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/랙/레일/컨베이어/크레인/팔레트/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/StackerCrane/Resources/StackerCraneModels/stackercrane_crane.fbx, stackercrane_pallet_v0~3.fbx
//       (blender/stackercrane_blender.py 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/stackercrane_blender.py 와 같은 값.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StackerCrane
{
    public class StackerCraneView : MonoBehaviour
    {
        [Header("Simulation")]
        public StackerCraneConfig config = new StackerCraneConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 624")]
        public float warmupSeconds = 624f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_stackercrane_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;
        public bool showCaptions = true;

        public StackerCraneSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        /// <summary>영상 기준 시간 (워밍업 끝 = 0)</summary>
        public float MovieTime => Sim == null ? 0f : (float)(Sim.t - warmupSeconds);

        // 장면 치수 (Blender 스크립트와 같은 값)
        const float UprX = 0.10f, UprY = 0.09f, BeamY = 0.05f, BeamZ = 0.12f;
        const float MastX0 = 0.875f, MastX1 = 1.425f, MastY = 0.25f;
        const float BaseX0 = -0.75f, BaseX1 = 2.30f, BaseY = 0.38f, BaseZ0 = 0.30f, BaseZ1 = 0.70f;
        const float RailX0 = -3.6f, RailX1 = 30.6f, ForkX = 0.30f;

        readonly List<Transform> craneT = new List<Transform>(), carT = new List<Transform>(), midT = new List<Transform>(), topT = new List<Transform>();
        readonly List<Transform> palT = new List<Transform>();
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 / 치수
        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        float GW => config.nx * (float)config.slotW;
        float Depth => (float)Sim.depthY;
        float TopZ => (float)Sim.LevelZ(config.levels - 1) + 1.75f;
        float AisleY(int a) => (float)Sim.AisleY(a);
        float LevelZ(int l) => (float)Sim.LevelZ(l);
        float SegX(int k) => (float)Sim.SegX(k);
        float WallX => (float)(config.pdX - (config.convN - 0.5) * config.convL);

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new StackerCraneSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildRacks();
            BuildConveyors();
            BuildFloorMarks();
            BuildCranes();
            BuildPallets();
            BuildCameraAndPost();
            if (showCaptions) gameObject.AddComponent<StackerCraneCaption>().Init(this, Cam);
            SyncAll();
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        void Update()
        {
            acc += Time.deltaTime * simSpeed;
            int guard = 0;
            while (acc >= subStep - 1e-6 && guard++ < 2000) { Sim.Step(subStep); acc -= subStep; }
            SyncAll();
        }

        // ------------------------------------------------------------------ 동기화 (월드 좌표로 직접 놓아 FBX 축 변환과 무관하게)
        void SyncAll()
        {
            float R = (float)Sim.reach;
            for (int i = 0; i < Sim.cranes.Count; i++)
            {
                var cr = Sim.cranes[i];
                var p = U(cr.x, AisleY(cr.aisle), 0);
                craneT[i].position = p;
                carT[i].position = p + new Vector3(0, (float)cr.z, 0);
                if (midT[i]) midT[i].position = carT[i].position + new Vector3(0, 0, (float)cr.fork * R * 0.5f);
                if (topT[i]) topT[i].position = carT[i].position + new Vector3(0, 0, (float)cr.fork * R);
            }
            for (int id = 0; id < palT.Count; id++)
            {
                Sim.PalletPose(id, out double x, out double y, out double z);
                var t = palT[id];
                bool vis = z > -1;
                if (t.gameObject.activeSelf != vis) t.gameObject.SetActive(vis);
                t.position = U(x, y, z);
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material yellow, grey, cabinet, fork, railSteel, rubber, poly, hub, black, lens, ledGreen, ledAmber, ledRed, hazard,
                upright, beam, galv, wood, woodDark, cardboard, cardboardWhite, drum, bag, prodRed, prodGreen, wrap, label,
                convFrame, chain, motor, floor, wall, panel, ceiling, steel, lightPanel, safetyYellow, safetyGreen, white, dark;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                yellow = Coat("CraneYellow", new Color(0.80f, 0.52f, 0.03f), 0.62f);
                grey = Lit("CarriageGrey", new Color(0.26f, 0.27f, 0.29f), 0.3f, 0.55f);
                cabinet = Lit("CabinetGrey", new Color(0.72f, 0.73f, 0.72f), 0f, 0.58f);
                fork = Lit("ForkSteel", new Color(0.55f, 0.57f, 0.59f), 1f, 0.68f);
                railSteel = Lit("RailSteel", new Color(0.42f, 0.43f, 0.44f), 1f, 0.6f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                poly = Lit("WheelPoly", new Color(0.85f, 0.55f, 0.08f), 0f, 0.45f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 4f);
                ledAmber = Emissive("LEDAmber", new Color(1f, 0.55f, 0.05f), 4f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                hazard = Lit("HazardStripe", new Color(0.9f, 0.7f, 0.05f), 0f, 0.5f);
                Stripes(hazard);
                upright = Coat("RackBlue", new Color(0.03f, 0.14f, 0.38f), 0.62f);
                beam = Coat("BeamOrange", new Color(0.82f, 0.25f, 0.02f), 0.64f);
                galv = Lit("Galvanized", new Color(0.62f, 0.64f, 0.66f), 1f, 0.62f);
                Noise(galv, new Color(0.58f, 0.60f, 0.62f), new Color(0.68f, 0.70f, 0.72f), 6f, 4);
                wood = Lit("PalletWood", Color.white, 0f, 0.25f);
                Noise(wood, new Color(0.55f, 0.41f, 0.25f), new Color(0.68f, 0.53f, 0.35f), 24f, 5);
                woodDark = Lit("PalletBlock", new Color(0.45f, 0.33f, 0.20f), 0f, 0.2f);
                cardboard = Lit("Cardboard", new Color(0.52f, 0.36f, 0.20f), 0f, 0.4f);
                cardboardWhite = Lit("CardboardWhite", new Color(0.68f, 0.65f, 0.60f), 0f, 0.45f);
                drum = Lit("DrumBlue", new Color(0.05f, 0.18f, 0.55f), 0f, 0.65f);
                bag = Lit("BagWhite", new Color(0.72f, 0.72f, 0.70f), 0f, 0.5f);
                prodRed = Lit("ProdRed", new Color(0.70f, 0.08f, 0.06f), 0f, 0.55f);
                prodGreen = Lit("ProdGreen", new Color(0.12f, 0.45f, 0.22f), 0f, 0.55f);
                wrap = Transparent("StretchWrap", new Color(0.85f, 0.87f, 0.88f, 0.28f), 0.9f);
                label = Lit("Label", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                convFrame = Lit("ConveyorGrey", new Color(0.30f, 0.32f, 0.34f), 0.4f, 0.58f);
                chain = Lit("ChainSteel", new Color(0.12f, 0.12f, 0.13f), 1f, 0.5f);
                motor = Lit("MotorBlue", new Color(0.06f, 0.20f, 0.42f), 0f, 0.6f);
                floor = Epoxy();
                wall = Lit("WallWhite", new Color(0.80f, 0.81f, 0.82f), 0f, 0.4f);
                panel = Lit("WallPanel", new Color(0.42f, 0.45f, 0.48f), 0.3f, 0.55f);
                ceiling = Lit("Ceiling", new Color(0.55f, 0.56f, 0.58f), 0f, 0.2f);
                steel = Lit("SteelBeam", new Color(0.70f, 0.71f, 0.72f), 0.6f, 0.6f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.98f, 0.95f), 6f);
                safetyYellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                safetyGreen = Lit("SafetyGreen", new Color(0.1f, 0.55f, 0.25f), 0f, 0.55f);
                white = Lit("WhiteLine", new Color(0.88f, 0.89f, 0.90f), 0f, 0.6f);
                dark = Lit("DarkOpening", new Color(0.03f, 0.03f, 0.035f), 0f, 0.1f);
            }

            static Shader LitShader => Shader.Find("Universal Render Pipeline/Lit");

            Material Lit(string name, Color c, float metal, float smooth)
            {
                var m = new Material(LitShader) { name = name, enableInstancing = true };
                m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
                byName[name] = m; return m;
            }

            Material Coat(string name, Color c, float smooth)
            {
                var sh = Shader.Find("Universal Render Pipeline/Complex Lit");
                if (sh == null) return Lit(name, c, 0f, smooth);
                var m = new Material(sh) { name = name };
                m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", 0f); m.SetFloat("_Smoothness", smooth);
                m.SetFloat("_ClearCoatMask", 0.5f); m.SetFloat("_ClearCoatSmoothness", 0.85f);
                m.EnableKeyword("_CLEARCOAT");
                byName[name] = m; return m;
            }

            Material Emissive(string name, Color c, float intensity)
            {
                var m = Lit(name, c, 0f, 0.7f);
                m.SetColor("_EmissionColor", c * intensity);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                return m;
            }

            // 반투명 (스트레치 랩)
            Material Transparent(string name, Color c, float smooth)
            {
                var m = Lit(name, c, 0f, smooth);
                m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
                return m;
            }

            // 두 색 사이 프랙탈 노이즈 알베도
            static void Noise(Material m, Color a, Color b, float tiles, int oct)
            {
                const int N = 256;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = 0, amp = 0.5f, f = 4f / N;
                        for (int o = 0; o < oct; o++) { v += amp * Mathf.PerlinNoise(x * f + 31.7f * o, y * f * 0.25f + 11.3f * o); amp *= 0.5f; f *= 2f; }
                        tex.SetPixel(x, y, Color.Lerp(a, b, Mathf.SmoothStep(0.2f, 0.8f, v)));
                    }
                tex.Apply();
                m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(tiles / 8f, tiles / 8f));
            }

            // 노랑·검정 사선 경고띠
            static void Stripes(Material m)
            {
                const int N = 64;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
                        tex.SetPixel(x, y, ((x + y) / 16) % 2 == 0 ? new Color(0.92f, 0.70f, 0.05f) : new Color(0.05f, 0.05f, 0.05f));
                tex.Apply();
                m.SetColor("_BaseColor", Color.white);
                m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(4, 1));
            }

            // 에폭시 바닥: 은은한 얼룩 + 반사 (거칠기 변화)
            Material Epoxy()
            {
                const int N = 512;
                var alb = new Texture2D(N, N, TextureFormat.RGBA32, true);
                var mg = new Texture2D(N, N, TextureFormat.RGBA32, true, true);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = 0, a = 0.5f, f = 3f / N;
                        for (int o = 0; o < 6; o++) { v += a * Mathf.PerlinNoise(x * f + 13.1f * o, y * f + 7.7f * o); a *= 0.5f; f *= 2f; }
                        float g = Mathf.Lerp(0.40f, 0.50f, Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, new Color(g, g * 1.005f, g * 1.01f, 1));
                        float s = Mathf.Lerp(0.70f, 0.90f, Mathf.PerlinNoise(x * 0.02f + 5f, y * 0.02f));
                        mg.SetPixel(x, y, new Color(0, 0, 0, s));
                    }
                alb.Apply(); mg.Apply();
                alb.wrapMode = mg.wrapMode = TextureWrapMode.Repeat;
                var m = Lit("EpoxyFloor", Color.white, 0f, 1f);
                m.SetTexture("_BaseMap", alb); m.SetTextureScale("_BaseMap", new Vector2(8, 8));
                m.SetTexture("_MetallicGlossMap", mg); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                return m;
            }

            // FBX 에서 가져온 재질을 이름으로 URP 재질로 교체 (가장 긴 이름이 맞는 것)
            public void Remap(GameObject go)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    var arr = r.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] == null) continue;
                        string n = arr[i].name; Material best = null; int bl = -1;
                        foreach (var kv in byName)
                            if (n.StartsWith(kv.Key) && kv.Key.Length > bl) { best = kv.Value; bl = kv.Key.Length; }
                        if (best != null) arr[i] = best;
                    }
                    r.sharedMaterials = arr;
                }
            }
        }

        // ------------------------------------------------------------------ 메쉬 묶음 (재질별로 합쳐 드로우콜 절감)
        class Batch
        {
            readonly Dictionary<Material, List<CombineInstance>> parts = new Dictionary<Material, List<CombineInstance>>();
            readonly Mesh cube, cyl;
            public Batch(Mesh cube, Mesh cyl) { this.cube = cube; this.cyl = cyl; }
            public void Add(Mesh mesh, Matrix4x4 mtx, Material m)
            {
                if (!parts.TryGetValue(m, out var l)) { l = new List<CombineInstance>(); parts[m] = l; }
                l.Add(new CombineInstance { mesh = mesh, transform = mtx });
            }
            // 시뮬레이션 좌표 (크기 sx, sy, sz / 중심 x, y, z)
            public void B(float sx, float sy, float sz, float x, float y, float z, Material m) =>
                Add(cube, Matrix4x4.TRS(new Vector3(x, z, y), Quaternion.identity, new Vector3(sx, sz, sy)), m);
            public void Cyl(float r, float len, float x, float y, float z, char axis, Material m)
            {
                var rot = axis == 'Z' ? Quaternion.identity : axis == 'X' ? Quaternion.Euler(0, 0, 90) : Quaternion.Euler(90, 0, 0);
                Add(cyl, Matrix4x4.TRS(new Vector3(x, z, y), rot, new Vector3(2 * r, len / 2, 2 * r)), m);
            }
            // 점 a → b 를 잇는 각재 (시뮬레이션 좌표)
            public void Span(Vector3 a, Vector3 b, float w, float h, Material m)
            {
                Vector3 ua = new Vector3(a.x, a.z, a.y), ub = new Vector3(b.x, b.z, b.y);
                var d = ub - ua;
                Add(cube, Matrix4x4.TRS((ua + ub) / 2, Quaternion.LookRotation(d.normalized, Mathf.Abs(d.normalized.y) < 0.9f ? Vector3.up : Vector3.right),
                    new Vector3(w, h, d.magnitude)), m);
            }
            public Transform Build(string name, Transform parent, bool shadows = true)
            {
                var root = new GameObject(name).transform; root.SetParent(parent, false);
                foreach (var kv in parts)
                {
                    var mesh = new Mesh { name = name + "_" + kv.Key.name, indexFormat = IndexFormat.UInt32 };
                    mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                    mesh.RecalculateBounds();
                    var go = new GameObject(kv.Key.name); go.transform.SetParent(root, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = kv.Key;
                    mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
                return root;
            }
        }

        // ------------------------------------------------------------------ 건물 (높은 자동창고 동)
        void BuildEnvironment()
        {
            float cxw = GW / 2 - 2f, cyw = Depth / 2;
            const float SX = 70f, SY = 44f, Hh = 26f;
            var env = new GameObject("Building").transform;
            var b = new Batch(cubeMesh, cylMesh);
            b.B(SX, SY, 0.02f, cxw, cyw, -0.01f, M.floor);
            b.B(SX * 0.9f, 0.12f, 0.006f, cxw, -9.0f, 0.003f, M.safetyGreen);
            b.B(SX, 0.2f, Hh, cxw, cyw + SY / 2, Hh / 2, M.panel);
            b.B(0.2f, SY, Hh, cxw - SX / 2, cyw, Hh / 2, M.panel);
            b.B(0.2f, SY, Hh, cxw + SX / 2, cyw, Hh / 2, M.panel);
            b.B(SX, 0.2f, Hh, cxw, cyw - SY / 2, Hh / 2, M.panel);
            b.B(SX, SY, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -32; k < 36; k += 6) b.B(0.35f, SY, 0.9f, cxw + k, cyw, Hh - 0.55f, M.steel);
            for (int k = -30; k < 36; k += 10)
                foreach (float yy in new[] { cyw - SY / 2 + 0.3f, cyw + SY / 2 - 0.3f }) b.B(0.5f, 0.5f, Hh, cxw + k, yy, Hh / 2, M.steel);
            b.B(SX, 0.4f, 1.4f, cxw, cyw + SY / 2 - 0.3f, 0.7f, M.wall);
            b.B(SX, 0.4f, 1.4f, cxw, cyw - SY / 2 + 0.3f, 0.7f, M.wall);
            for (int ix = -5; ix <= 5; ix++)
                for (int iy = -3; iy <= 3; iy++) b.B(2.2f, 0.5f, 0.06f, cxw + ix * 6f, cyw + iy * 6f, Hh - 1.1f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 12f, cyw + iy * 9f, Hh - 1.3f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 60; l.intensity = 60f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.0f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.75f; sun.shadowNormalBias = 0.6f; sun.shadowBias = 0.05f;
            sun.transform.rotation = Quaternion.Euler(64, -28, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.72f, 0.74f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.58f, 0.59f, 0.61f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.38f, 0.39f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 8f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(SX, Hh * 2, SY); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 랙 + 레일 (blender make_racks 와 같은 형상)
        void BuildRacks()
        {
            var c = config; float sW = (float)c.slotW, bayL = c.bay * sW, zt = TopZ, aw = (float)c.aisleW, rd = (float)c.rackD;
            int nb = c.nx / c.bay;
            var root = new GameObject("Racks").transform;
            for (int a = 0; a < c.aisles; a++)
                for (int side = 0; side < 2; side++)
                {
                    float ya = AisleY(a), h = aw / 2;
                    float y0 = side == 0 ? ya - h - rd : ya + h, y1 = side == 0 ? ya - h : ya + h + rd;
                    float yf = side == 0 ? y1 : y0, yback = side == 0 ? y0 : y1;
                    var b = new Batch(cubeMesh, cylMesh);
                    for (int k = 0; k <= nb; k++)
                    {
                        float x = k * bayL;
                        for (int q = 0; q < 2; q++)
                        {
                            float yy = q == 0 ? yf : yback, hz = q == 0 ? zt + 0.9f : zt;
                            float yc = yy == y1 ? yy - UprY / 2 : yy + UprY / 2;
                            b.B(UprX, UprY, hz, x, yc, hz / 2, M.upright);
                            b.B(0.16f, 0.14f, 0.012f, x, yc, 0.006f, M.galv);
                        }
                        float ya2 = y0 + UprY, yb2 = y1 - UprY, z = 0.3f; bool up = true;
                        while (z < zt - 0.9f)
                        {
                            b.Span(new Vector3(x, ya2, z), new Vector3(x, yb2, z), 0.035f, 0.035f, M.galv);
                            float z2 = Mathf.Min(z + 1.2f, zt - 0.2f);
                            b.Span(new Vector3(x, up ? ya2 : yb2, z), new Vector3(x, up ? yb2 : ya2, z2), 0.03f, 0.03f, M.galv);
                            z = z2; up = !up;
                        }
                        b.Span(new Vector3(x, ya2, zt - 0.1f), new Vector3(x, yb2, zt - 0.1f), 0.035f, 0.035f, M.galv);
                    }
                    for (int k = 0; k < nb; k++)
                    {
                        float xc = (k + 0.5f) * bayL;
                        for (int l = 0; l < c.levels; l++)
                        {
                            float zb = LevelZ(l) - 0.003f - BeamZ / 2;
                            foreach (float yy in new[] { y0 + UprY / 2, y1 - UprY / 2 })
                                b.B(bayL - UprX - 0.004f, BeamY, BeamZ, xc, yy, zb, M.beam);
                        }
                    }
                    b.Build($"Rack_a{a}_s{side}", root);
                }
            var R = new Batch(cubeMesh, cylMesh);
            for (int a = 0; a < c.aisles; a++)
            {
                float ya = AisleY(a);
                for (int k = 0; k <= nb; k++) R.B(0.12f, aw + 2 * UprY, 0.15f, k * bayL, ya, zt + 0.825f, M.upright);
                R.B(RailX1, 0.06f, 0.20f, RailX1 / 2, ya, zt + 0.65f, M.railSteel);
                R.B(RailX1 - RailX0, 0.08f, 0.12f, (RailX0 + RailX1) / 2, ya, 0.06f, M.railSteel);
                R.B(RailX1 - RailX0, 0.30f, 0.012f, (RailX0 + RailX1) / 2, ya, 0.006f, M.galv);
                for (int q = 0; q < 2; q++)
                {
                    float xe = q == 0 ? RailX0 : RailX1, s = q == 0 ? -1f : 1f;
                    R.B(0.30f, 0.40f, 0.55f, xe + s * 0.15f, ya, 0.275f, M.safetyYellow);
                    R.B(0.15f, 0.25f, 0.25f, xe - s * 0.07f, ya, 0.50f, M.black);
                }
                foreach (float xg in new[] { RailX0 + 0.3f })
                {
                    for (int sy = -1; sy <= 1; sy += 2) R.B(0.20f, 0.20f, zt + 0.9f, xg, ya + sy * (aw / 2 + 0.12f), (zt + 0.9f) / 2, M.upright);
                    R.B(0.22f, aw + 0.5f, 0.20f, xg, ya, zt + 0.85f, M.upright);
                }
                R.B(-RailX0, 0.06f, 0.20f, RailX0 / 2, ya, zt + 0.65f, M.railSteel);
                R.B(0.02f, 0.30f, 0.30f, RailX0 - 0.35f, ya + 0.20f, 0.82f, M.white);
            }
            R.Build("Rails", root);
        }

        // ------------------------------------------------------------------ 입출고 컨베이어 + 칸막이 블록
        void BuildConveyors()
        {
            var c = config; float cL = (float)c.convL, zt = (float)c.level0Z; int n = c.convN;
            var root = new GameObject("Conveyors").transform;
            var P = new Batch(cubeMesh, cylMesh);
            var lanes = new List<float>();
            for (int li = 0; li < Sim.lanes.Count; li++)
            {
                float ly = (float)Sim.LaneY(li); lanes.Add(ly);
                float x0 = SegX(n - 1) - cL / 2 - 0.9f, x1 = SegX(0) + cL / 2;
                for (int sy = -1; sy <= 1; sy += 2) P.B(x1 - x0, 0.06f, 0.16f, (x0 + x1) / 2, ly + sy * 0.62f, zt - 0.10f, M.convFrame);
                for (int k = 0; k < n; k++)
                {
                    float xc = SegX(k);
                    foreach (float yc in new[] { -0.45f, 0f, 0.45f })
                    {
                        P.B(cL - 0.04f, 0.05f, 0.03f, xc, ly + yc, zt - 0.0175f, M.chain);
                        P.Cyl(0.05f, 0.07f, xc - cL / 2 + 0.08f, ly + yc, zt - 0.06f, 'Y', M.chain);
                    }
                    P.B(0.25f, 1.20f, 0.05f, xc + cL / 2 - 0.02f, ly, zt - 0.20f, M.convFrame);
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        P.B(0.06f, 0.06f, zt - 0.18f, xc + cL / 2 - 0.02f, ly + sy * 0.58f, (zt - 0.18f) / 2, M.convFrame);
                        P.B(0.12f, 0.10f, 0.012f, xc + cL / 2 - 0.02f, ly + sy * 0.58f, 0.006f, M.galv);
                    }
                    P.B(0.22f, 0.20f, 0.18f, xc - 0.2f, ly - 0.72f, zt - 0.14f, M.motor);
                    P.Cyl(0.07f, 0.20f, xc - 0.2f, ly - 0.72f, zt - 0.32f, 'Z', M.motor);
                    P.B(0.04f, 0.04f, 0.30f, xc + 0.5f, ly + 0.68f, zt + 0.15f, M.black);
                    P.B(0.004f, 0.02f, 0.02f, xc + 0.5f, ly + 0.658f, zt + 0.25f, M.lens);
                }
                P.B(0.10f, 1.30f, 0.12f, SegX(0) + cL / 2 + 0.06f, ly, zt + 0.02f, M.safetyYellow);
                if (li % 2 == 0)
                {
                    float xg = SegX(3);
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        P.B(0.10f, 0.10f, 2.55f, xg, ly + sy * 0.80f, 1.275f, M.safetyYellow);
                        P.B(0.012f, 0.03f, 1.9f, xg + 0.06f, ly + sy * 0.75f, 1.55f, M.ledRed);
                    }
                    P.B(0.10f, 1.70f, 0.10f, xg, ly, 2.60f, M.safetyYellow);
                    P.B(0.16f, 0.22f, 0.10f, xg, ly, 2.50f, M.black);
                }
            }
            P.Build("ConveyorParts", root);
            // 칸막이 블록 (두께 2.6 m): 팔레트는 이 안의 어두운 통로에서 나타나고 사라짐
            var W = new Batch(cubeMesh, cylMesh);
            float xw = WallX, wy0 = -2.5f, wy1 = Depth + 2.5f, H = 4.5f, th = 2.6f;
            lanes.Sort();
            var edges = new List<float> { wy0 };
            foreach (var ly in lanes) { edges.Add(ly - 0.80f); edges.Add(ly + 0.80f); }
            edges.Add(wy1);
            for (int k = 0; k < edges.Count; k += 2)
                if (edges[k + 1] > edges[k]) W.B(th, edges[k + 1] - edges[k], H, xw - th / 2, (edges[k] + edges[k + 1]) / 2, H / 2, M.panel);
            W.B(th + 0.1f, wy1 - wy0 + 0.1f, 0.12f, xw - th / 2, (wy0 + wy1) / 2, H + 0.06f, M.steel);
            foreach (var ly in lanes)
            {
                W.B(th, 1.60f, H - 2.6f, xw - th / 2, ly, 2.6f + (H - 2.6f) / 2, M.panel);
                for (int sy = -1; sy <= 1; sy += 2) W.B(0.05f, 0.10f, 2.6f, xw + 0.025f, ly + sy * 0.85f, 1.3f, M.hazard);
                W.B(0.05f, 1.80f, 0.10f, xw + 0.025f, ly, 2.65f, M.hazard);
                W.B(th - 0.3f, 1.60f, 2.6f, xw - 0.3f - (th - 0.3f) / 2, ly, 1.3f, M.dark);
            }
            W.Build("PartitionBlock", root);
        }

        // ------------------------------------------------------------------ 바닥 표시 + 안전 난간
        void BuildFloorMarks()
        {
            var b = new Batch(cubeMesh, cylMesh);
            float xw = WallX, D = Depth;
            foreach (float y in new[] { -0.55f, D + 0.55f }) b.B(-xw, 0.08f, 0.006f, xw / 2, y, 0.003f, M.safetyYellow);
            b.B(0.08f, D + 1.1f, 0.006f, xw - 0.3f, D / 2, 0.003f, M.safetyYellow);
            for (int k = 0; k < (int)(-xw / 0.6f); k++) if (k % 2 == 0) b.B(0.30f, 0.14f, 0.006f, xw + 0.3f + k * 0.6f, -1.4f, 0.003f, M.white);
            foreach (float y in new[] { -0.80f, D + 0.80f })
            {
                float x0 = xw + 0.2f, x1 = -0.2f; int n = (int)((x1 - x0) / 1.6f);
                for (int i = 0; i <= n; i++) b.B(0.06f, 0.06f, 1.15f, x0 + (x1 - x0) * i / n, y, 0.575f, M.safetyYellow);
                foreach (float z in new[] { 0.35f, 0.75f, 1.12f }) b.B(x1 - x0, 0.045f, 0.045f, (x0 + x1) / 2, y, z, M.safetyYellow);
                b.B(x1 - x0, 0.015f, 0.15f, (x0 + x1) / 2, y, 0.08f, M.safetyYellow);
            }
            b.Build("FloorMarks", null);
        }

        // ------------------------------------------------------------------ 크레인
        void BuildCranes()
        {
            var parent = new GameObject("Cranes").transform;
            var prefab = Resources.Load<GameObject>("StackerCraneModels/stackercrane_crane");
            foreach (var cr in Sim.cranes)
            {
                var root = new GameObject("Crane_" + cr.id).transform; root.SetParent(parent, false);
                Transform car = null, mid = null, top = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    FitScale(go, TopZ + 0.62f);
                    foreach (var t in go.GetComponentsInChildren<Transform>())
                    {
                        if (t.name.StartsWith("Carriage") && !t.name.StartsWith("CarriageBody")) car = t;
                        else if (t.name.StartsWith("ForkMid")) mid = t;
                        else if (t.name.StartsWith("ForkTop")) top = t;
                    }
                    // 축 변환으로 모델이 세로축으로 180° 돌아 들어왔으면 (마스트가 -x 쪽) 바로잡음
                    var frame = go.GetComponentsInChildren<Renderer>();
                    foreach (var r in frame)
                        if (r.name.StartsWith("Frame") && r.bounds.center.x < root.position.x) { go.transform.localRotation = Quaternion.Euler(0, 180, 0); break; }
                }
                if (car == null || mid == null || top == null) { car = FallbackCrane(root, out mid, out top); }
                // 포크 단은 승강대 아래로 옮겨 두고 월드 좌표로 움직임
                craneT.Add(root); carT.Add(car); midT.Add(mid); topT.Add(top);
            }
        }

        // 기본 도형 크레인 (FBX 가 없을 때)
        Transform FallbackCrane(Transform root, out Transform mid, out Transform top)
        {
            float zt = TopZ;
            var f = new Batch(cubeMesh, cylMesh);
            f.B(BaseX1 - BaseX0, 2 * BaseY, BaseZ1 - BaseZ0, (BaseX0 + BaseX1) / 2, 0, (BaseZ0 + BaseZ1) / 2, M.yellow);
            foreach (float wx in new[] { -0.42f, 1.98f }) f.Cyl(0.18f, 0.09f, wx, 0, 0.30f, 'Y', M.poly);
            f.B(MastX1 - MastX0, 2 * MastY, zt + 0.2f - BaseZ1, (MastX0 + MastX1) / 2, 0, (BaseZ1 + zt + 0.2f) / 2, M.yellow);
            f.B(1.30f, 0.60f, 0.22f, 1.15f, 0, zt + 0.31f, M.yellow);
            f.B(0.62f, 0.34f, 1.65f, 1.93f, BaseY - 0.12f, BaseZ1 + 1.30f, M.cabinet);
            f.Build("Frame", root);
            var car = new GameObject("Carriage").transform; car.SetParent(root, false);
            var k = new Batch(cubeMesh, cylMesh);
            k.B(1.24f, 1.00f, 0.13f, 0, 0, -0.175f, M.grey);
            for (int sy = -1; sy <= 1; sy += 2)
            {
                k.B(0.18f, 0.12f, 1.80f, 0.71f, sy * 0.40f, 0.66f, M.grey);
                k.B(0.05f, 0.05f, 1.70f, -0.60f, sy * 0.47f, 0.74f, M.grey);
                k.B(1.24f, 0.05f, 0.06f, 0, sy * 0.47f, 1.62f, M.grey);
            }
            foreach (int sx in new[] { -1, 1 }) k.B(0.14f, 1.15f, 0.035f, sx * ForkX, 0, -0.0925f, M.fork);
            k.Build("CarriageBody", car);
            var m = new Batch(cubeMesh, cylMesh);
            foreach (int sx in new[] { -1, 1 }) m.B(0.12f, 1.10f, 0.040f, sx * ForkX, 0, -0.055f, M.fork);
            mid = m.Build("ForkMid", car);
            var t = new Batch(cubeMesh, cylMesh);
            foreach (int sx in new[] { -1, 1 }) t.B(0.10f, 1.15f, 0.035f, sx * ForkX, 0, -0.0175f, M.fork);
            top = t.Build("ForkTop", car);
            return car;
        }

        // 단위가 어긋나면 크기 보정 (FBX 축 변환은 임포터의 bakeAxisConversion 이 처리)
        static void FitScale(GameObject go, float targetH)
        {
            var rs = go.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return;
            var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
            float sc = targetH / Mathf.Max(1e-4f, bb.size.y);
            if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
        }

        // ------------------------------------------------------------------ 팔레트
        void BuildPallets()
        {
            var parent = new GameObject("Pallets").transform;
            var holder = new GameObject("PalletTemplates").transform; holder.gameObject.SetActive(false);
            var tpl = new List<GameObject>();
            for (int v = 0; v < 4; v++)
            {
                var pf = Resources.Load<GameObject>("StackerCraneModels/stackercrane_pallet_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("PalletTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m); FitScale(m, (float)config.palletH + 1.0f);
                }
                else go = FallbackPallet(v, holder);
                tpl.Add(go);
            }
            for (int id = 0; id < Sim.pal.Count; id++)
            {
                var p = Sim.pal[id];
                var go = Instantiate(tpl[p.variant], parent, false); go.name = "Pallet_" + id;
                // 적재물 높이: Load 자식을 세로로 늘림 (월드 높이로 확인)
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("Load"))
                    {
                        var r = t.GetComponentInChildren<Renderer>();
                        float h0 = r ? r.bounds.size.y : 1f;
                        var s = t.localScale; t.localScale = new Vector3(s.x, s.y * (float)p.load, s.z);
                        if (r && Mathf.Abs(r.bounds.size.y - h0) < 1e-3f && Mathf.Abs((float)p.load - 1f) > 0.01f)
                            t.localScale = new Vector3(s.x, s.y, s.z * (float)p.load);   // 로컬 z 가 위쪽인 경우
                        break;
                    }
                palT.Add(go.transform);
            }
        }

        GameObject FallbackPallet(int v, Transform holder)
        {
            var go = new GameObject("PalletTpl_" + v); go.transform.SetParent(holder, false);
            var d = new Batch(cubeMesh, cylMesh);
            foreach (float x in new[] { -0.44f, -0.22f, 0f, 0.22f, 0.44f }) d.B(0.11f, 1.20f, 0.022f, x, 0, 0.133f, M.wood);
            foreach (float y in new[] { -0.55f, 0f, 0.55f }) d.B(1.00f, 0.10f, 0.022f, 0, y, 0.111f, M.wood);
            foreach (float x in new[] { -0.45f, 0f, 0.45f })
            {
                foreach (float y in new[] { -0.55f, 0f, 0.55f }) d.B(0.10f, 0.10f, 0.078f, x, y, 0.061f, M.woodDark);
                d.B(0.10f, 1.20f, 0.022f, x, 0, 0.011f, M.wood);
            }
            d.Build("Deck_v" + v, go.transform);
            var l = new Batch(cubeMesh, cylMesh);
            var mat = v == 0 ? M.cardboard : v == 1 ? M.cardboardWhite : v == 2 ? M.drum : M.bag;
            l.B(0.98f, 1.18f, 1.0f, 0, 0, 0.5f, mat);
            var lt = l.Build("Load_v" + v, go.transform);
            lt.localPosition = new Vector3(0, (float)config.palletH, 0);
            return go;
        }

        // ------------------------------------------------------------------ 카메라 + 후처리
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.62f, 0.64f, 0.67f);
            Cam.nearClipPlane = 0.1f; Cam.farClipPlane = 250f;
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<StackerCraneCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<StackerCraneCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.35f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.2f); ca.contrast.Override(14f); ca.saturation.Override(4f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.2f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(8f); dof.focalLength.Override(28f); dof.aperture.Override(8f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.35f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.08f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (StackerCraneMovieCapture.IsRecording && StackerCraneMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 400, 230), GUI.skin.box);
            GUILayout.Label($"<b>Stacker crane (unit-load AS/RS) simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Aisles {config.aisles}  Bays {config.nx}  Levels {config.levels}  Pallets stored {Sim.Occupancy:P0}");
            GUILayout.Label($"Moves {Sim.MovesPerHour:0} /h   Dual cycles {Sim.DualShare:P0}   Avg cycle {Sim.AvgCycle:0} s");
            GUILayout.Label($"Crane util {Sim.CraneUtil:P0}   Stored {Sim.statStored}   Retrieved {Sim.statRetrieved}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            showCaptions = GUILayout.Toggle(showCaptions, "Captions");
            GUILayout.EndArea();
        }
    }
}
