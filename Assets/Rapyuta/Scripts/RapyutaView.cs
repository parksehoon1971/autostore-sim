// Rapyuta ASRS 방식 다층 큐브 창고: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/층 패널/빨간 기둥/리프트/피킹 스테이션/로봇/빈/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/Rapyuta/Resources/RapyutaModels/rapyuta_robot.fbx, rapyuta_bin_v0~3.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/rapyuta_blender.py 와 같은 값.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Rapyuta
{
    public class RapyutaView : MonoBehaviour
    {
        [Header("Simulation")]
        public RapyutaConfig config = new RapyutaConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 600")]
        public float warmupSeconds = 600f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_rapyuta_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public RapyutaSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        // 배치 게임 모듈 모드: 건물·조명·카메라 없이 설비만 만들고, 옮겨 놓은 위치(transform.position)로 동기화
        [System.NonSerialized] public bool moduleMode;
        public static Vector3 UOff;   // U() 가 더하는 위치. 모듈 동기화 중에만 0 이 아님

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float PanelT = 0.025f, Pole = 0.045f, Pad = 0.06f, BinW = 0.65f, BinD = 0.44f;
        public const float RobotL = 0.56f, RobotW = 0.36f, PlateRest = 0.08f, PlateUp = 0.055f;
        const int PoleEvery = 2;

        readonly List<Transform> robotT = new List<Transform>();
        readonly List<Transform> plateT = new List<Transform>();
        readonly List<Vector3> plateRest = new List<Vector3>();
        readonly List<Transform> binT = new List<Transform>();
        readonly List<Transform> liftT = new List<Transform>();
        readonly List<Light> projector = new List<Light>();
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => UOff + new Vector3((float)x, (float)z, (float)y);
        float Wx(int i) => (float)Sim.WorldX(i);
        float Wy(int j) => (float)Sim.WorldY(j);
        float Deck(int f) => (float)Sim.DeckZ(f);
        float GW => config.nx * (float)config.cellX;
        float GD => config.ny * (float)config.cellY;
        float TopZ => Deck(config.nf);

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new RapyutaSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            if (!moduleMode) BuildEnvironment();
            BuildStructure();
            BuildLifts();
            BuildStations();
            BuildRobots();
            BuildBins();
            if (!moduleMode) BuildCameraAndPost();
            SyncAll();
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        void Update()
        {
            acc += Time.deltaTime * simSpeed;
            int guard = 0;
            while (acc >= subStep - 1e-6 && guard++ < 2000) { Sim.Step(subStep); acc -= subStep; }
            UOff = moduleMode ? transform.position : Vector3.zero;
            SyncAll();
            UOff = Vector3.zero;
        }

        // ------------------------------------------------------------------ 동기화
        void SyncAll()
        {
            var c = config;
            for (int i = 0; i < Sim.robots.Count; i++)
            {
                var r = Sim.robots[i];
                robotT[i].position = U(r.px, r.py, r.pz);
                if (plateT[i]) plateT[i].position = robotT[i].position + plateRest[i] + Vector3.up * (float)(r.lift / c.stroke * PlateUp);
            }
            float foot = (float)c.binFoot;
            for (int id = 0; id < binT.Count; id++)
            {
                var p = Sim.BinPose(id);
                binT[id].position = U(p.x, p.y, p.z + foot);
            }
            for (int i = 0; i < Sim.lifts.Count; i++)
            {
                var L = Sim.lifts[i];
                liftT[i].position = U(Sim.WorldX(L.x), Sim.WorldY(L.y), L.z);
            }
            float zf = Deck(c.stationFloor);
            for (int i = 0; i < Sim.stations.Count; i++)
            {
                var st = Sim.stations[i]; var lg = projector[i];
                if (st.cur >= 0)
                {
                    var s = Sim.slots[st.cur];
                    var p = lg.transform.position; p.x = Wx(s.x) + UOff.x; lg.transform.position = p;
                    lg.transform.rotation = Quaternion.LookRotation(U(Wx(s.x), -c.cellY / 2, zf + c.binFoot + c.binH) - p, Vector3.forward);
                    lg.intensity = 6f;
                }
                else lg.intensity = 0f;
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material pole, panel, grid, pad, bin, binDark, label, barcode, robot, plate, rubber, roller, hub, lens,
                led, ledRed, ledGreen, alu, chain, yellow, white, black, screen, mat, poly, floor, wall, ceiling, beam,
                lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                pole = Coat("PoleRed", new Color(0.62f, 0.035f, 0.03f), 0.62f);
                panel = Lit("PanelBlack", new Color(0.025f, 0.026f, 0.028f), 0f, 0.5f);
                grid = Lit("GridWhite", new Color(0.80f, 0.80f, 0.78f), 0f, 0.55f);
                pad = Lit("PadBlack", new Color(0.04f, 0.04f, 0.045f), 0f, 0.4f);
                bin = Lit("BinPP", Color.white, 0f, 0.58f);
                Noise(bin, new Color(0.83f, 0.84f, 0.83f), new Color(0.88f, 0.89f, 0.88f), 30f, 3);
                binDark = Lit("BinHandle", new Color(0.55f, 0.56f, 0.56f), 0f, 0.5f);
                label = Lit("BinLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                barcode = Lit("Barcode", new Color(0.02f, 0.02f, 0.02f), 0f, 0.4f);
                robot = Coat("RobotBlack", new Color(0.018f, 0.018f, 0.02f), 0.68f);
                plate = Lit("PlateGrey", new Color(0.30f, 0.31f, 0.32f), 0.6f, 0.65f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                roller = Lit("RollerGrey", new Color(0.12f, 0.12f, 0.13f), 0f, 0.4f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                led = Emissive("LEDCyan", new Color(0.2f, 0.8f, 1f), 5f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 4f);
                alu = Lit("Aluminium", new Color(0.78f, 0.79f, 0.80f), 1f, 0.72f);
                chain = Lit("ChainSteel", new Color(0.18f, 0.18f, 0.19f), 1f, 0.55f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                white = Coat("WhitePanel", new Color(0.88f, 0.89f, 0.90f), 0.65f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                screen = Emissive("Screen", new Color(0.35f, 0.6f, 1f), 1.5f);
                mat = Lit("FloorMat", new Color(0.06f, 0.06f, 0.065f), 0f, 0.1f);
                items = new[]
                {
                    Lit("Cardboard", new Color(0.52f, 0.36f, 0.20f), 0f, 0.4f), Lit("CardboardWhite", new Color(0.78f, 0.74f, 0.68f), 0f, 0.4f),
                    Lit("ProdBlue", new Color(0.08f, 0.25f, 0.62f), 0f, 0.4f), Lit("ProdRed", new Color(0.70f, 0.08f, 0.06f), 0f, 0.4f),
                    Lit("ProdGreen", new Color(0.12f, 0.45f, 0.22f), 0f, 0.4f), Lit("ProdYellow", new Color(0.90f, 0.72f, 0.10f), 0f, 0.4f),
                };
                poly = Lit("PolyClear", new Color(0.7f, 0.72f, 0.75f), 0f, 0.85f);
                floor = Epoxy();
                wall = Lit("WallWhite", new Color(0.80f, 0.81f, 0.82f), 0f, 0.4f);
                ceiling = Lit("Ceiling", new Color(0.55f, 0.56f, 0.58f), 0f, 0.2f);
                beam = Lit("SteelBeam", new Color(0.70f, 0.71f, 0.72f), 0.6f, 0.6f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.98f, 0.95f), 6f);
                green = Lit("SafetyGreen", new Color(0.1f, 0.55f, 0.25f), 0f, 0.55f);
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
                m.SetFloat("_ClearCoatMask", 0.6f); m.SetFloat("_ClearCoatSmoothness", 0.9f);
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

            // 두 색 사이 프랙탈 노이즈 알베도
            static void Noise(Material m, Color a, Color b, float tiles, int oct)
            {
                const int N = 256;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = 0, amp = 0.5f, f = 4f / N;
                        for (int o = 0; o < oct; o++) { v += amp * Mathf.PerlinNoise(x * f + 31.7f * o, y * f + 11.3f * o); amp *= 0.5f; f *= 2f; }
                        tex.SetPixel(x, y, Color.Lerp(a, b, Mathf.SmoothStep(0.2f, 0.8f, v)));
                    }
                tex.Apply();
                m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(tiles / 8f, tiles / 8f));
            }

            // 밝은 에폭시 바닥: 은은한 얼룩 + 반사 (거칠기 변화)
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
                        float g = Mathf.Lerp(0.55f, 0.66f, Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, new Color(g, g * 1.005f, g * 1.01f, 1));
                        float s = Mathf.Lerp(0.70f, 0.90f, Mathf.PerlinNoise(x * 0.02f + 5f, y * 0.02f));
                        mg.SetPixel(x, y, new Color(0, 0, 0, s));
                    }
                alb.Apply(); mg.Apply();
                alb.wrapMode = mg.wrapMode = TextureWrapMode.Repeat;
                var m = Lit("EpoxyFloor", Color.white, 0f, 1f);
                m.SetTexture("_BaseMap", alb); m.SetTextureScale("_BaseMap", new Vector2(6, 6));
                m.SetTexture("_MetallicGlossMap", mg); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                return m;
            }

            // FBX 에서 가져온 재질을 이름으로 URP 재질로 교체 (가장 긴 이름이 맞는 것: Cardboard / CardboardWhite 구분)
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
            public void Box(Vector3 size, Vector3 center, Material m) => Add(cube, Matrix4x4.TRS(center, Quaternion.identity, size), m);
            // Unity 원기둥: 높이 2, 반지름 0.5, 축 = Y
            public void Cyl(float r, float len, Vector3 center, Quaternion rot, Material m) =>
                Add(cyl, Matrix4x4.TRS(center, rot, new Vector3(2 * r, len / 2, 2 * r)), m);
            public void Add(Mesh mesh, Matrix4x4 mtx, Material m)
            {
                if (!parts.TryGetValue(m, out var l)) { l = new List<CombineInstance>(); parts[m] = l; }
                l.Add(new CombineInstance { mesh = mesh, transform = mtx });
            }
            public Transform Build(string name, Transform parent, bool shadows = true)
            {
                var root = new GameObject(name).transform; root.SetParent(parent, false);
                var built = new List<KeyValuePair<Material, Mesh>>();
                foreach (var kv in parts)
                {
                    var mesh = new Mesh { name = name + "_" + kv.Key.name, indexFormat = IndexFormat.UInt32 };
                    mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                    mesh.RecalculateBounds();
                    built.Add(new KeyValuePair<Material, Mesh>(kv.Key, mesh));
                    var go = new GameObject(kv.Key.name); go.transform.SetParent(root, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = kv.Key;
                    mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
                EquipFX.MeshFix.InflateByArea(built);   // 겹친 면 깜빡임 방지: 작은 재질(표시선·LED 등)을 살짝 앞으로
                return root;
            }
        }

        // 시뮬레이션 좌표로 박스 추가 (크기 sx, sy, sz / 중심 x, y, z)
        static void B(Batch b, float sx, float sy, float sz, float x, float y, float z, Material m) =>
            b.Box(new Vector3(sx, sz, sy), new Vector3(x, z, y), m);
        // 원기둥: axis 'X' | 'Y' | 'Z' (시뮬레이션 축)
        static void Cy(Batch b, float r, float len, float x, float y, float z, char axis, Material m)
        {
            var rot = axis == 'Z' ? Quaternion.identity : axis == 'X' ? Quaternion.Euler(0, 0, 90) : Quaternion.Euler(90, 0, 0);
            b.Cyl(r, len, new Vector3(x, z, y), rot, m);
        }

        static GameObject Cube(string name, Vector3 size, Vector3 center, Material m, Transform parent, bool shadows = true)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name; Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = center; g.transform.localScale = size;
            var mr = g.GetComponent<MeshRenderer>(); mr.sharedMaterial = m;
            mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return g;
        }

        // ------------------------------------------------------------------ 건물 (밝은 물류센터)
        void BuildEnvironment()
        {
            float cxw = GW / 2, cyw = GD / 2;
            var env = new GameObject("Building").transform;
            const float S = 48f, Hh = 9f, m = 1.2f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            B(b, GW + 2 * m, 0.08f, 0.004f, cxw, -m - 1.9f, 0.002f, M.yellow);
            B(b, GW + 2 * m, 0.08f, 0.004f, cxw, GD + m, 0.002f, M.yellow);
            B(b, 0.08f, GD + 2 * m + 1.9f, 0.004f, -m - 0.7f, cyw - 0.95f, 0.002f, M.yellow);
            B(b, 0.08f, GD + 2 * m + 1.9f, 0.004f, GW + m + 0.7f, cyw - 0.95f, 0.002f, M.yellow);
            B(b, S * 0.8f, 0.12f, 0.004f, cxw, -5.2f, 0.002f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 16, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 16, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 18, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 18, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -15; k <= 16; k += 3) B(b, 0.25f, S, 0.5f, cxw + k, cyw, Hh - 0.35f, M.beam);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 15.8f, 1.2f, M.green);
            for (int ix = -2; ix <= 2; ix++)
                for (int iy = -2; iy <= 2; iy++)
                    B(b, 1.6f, 0.4f, 0.05f, cxw + ix * 4.5f, cyw + iy * 4.5f, Hh - 0.7f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 4.5f, cyw + iy * 4.5f, Hh - 0.8f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 30; l.intensity = 18f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f;
            sun.transform.rotation = Quaternion.Euler(58, -30, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.80f, 0.82f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.67f, 0.69f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.45f, 0.46f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 4f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 구조: 층마다 패널 + 흰 격자선 + 다리 + 빨간 기둥
        bool PoleX(int i) => i % PoleEvery == 0 || i == config.nx;
        bool PoleY(int j) => j % PoleEvery == 0 || j == config.ny;

        void BuildStructure()
        {
            var c = config; float cX = (float)c.cellX, cY = (float)c.cellY, foot = (float)c.binFoot, ph = (float)c.floorH - PanelT;
            var root = new GameObject("Structure").transform;
            for (int f = 0; f < c.nf; f++)
            {
                var b = new Batch(cubeMesh, cylMesh);
                float z0 = Deck(f);
                B(b, GW + 0.04f, GD + 0.04f, PanelT, GW / 2, GD / 2, z0 - PanelT / 2, M.panel);
                // 흰 격자선: 이전 1.2 mm 두께라 패널과 번갈아 깜빡였음 → 3 mm
                for (int i = 0; i <= c.nx; i++) B(b, 0.012f, GD, 0.003f, i * cX, GD / 2, z0 + 0.0015f, M.grid);
                for (int j = 0; j <= c.ny; j++) B(b, GW, 0.012f, 0.003f, GW / 2, j * cY, z0 + 0.0015f, M.grid);
                for (int i = 0; i <= c.nx; i++)
                    for (int j = 0; j <= c.ny; j++)
                    {
                        float x = i * cX, y = j * cY;
                        if (PoleX(i) && PoleY(j))
                        {
                            B(b, Pole, Pole, ph, x, y, z0 + ph / 2, M.pole);
                            B(b, Pad + 0.01f, Pad + 0.01f, 0.02f, x, y, z0 + 0.01f, M.pad);
                            B(b, Pad + 0.01f, Pad + 0.01f, 0.025f, x, y, z0 + foot - 0.0125f, M.pad);
                        }
                        else B(b, Pad, Pad, foot, x, y, z0 + foot / 2, M.pad);
                    }
                if (f == c.stationFloor)
                    for (int s = 0; s < c.stationX0.Length; s++)
                    {
                        float xa = c.stationX0[s] * cX, xb = (c.stationX0[s] + c.stationN[s]) * cX;
                        B(b, xb - xa, cY, PanelT, (xa + xb) / 2, -cY / 2, z0 - PanelT / 2, M.panel);
                        for (int k = 0; k <= c.stationN[s]; k++)
                        {
                            B(b, 0.012f, cY, 0.003f, xa + k * cX, -cY / 2, z0 + 0.0015f, M.grid);
                            B(b, Pad, Pad, foot, xa + k * cX, -cY, z0 + foot / 2, M.pad);
                        }
                        B(b, Pole, Pole, z0 - PanelT, xa, -cY, (z0 - PanelT) / 2, M.pole);
                        B(b, Pole, Pole, z0 - PanelT, xb, -cY, (z0 - PanelT) / 2, M.pole);
                        B(b, xb - xa, 0.012f, 0.003f, (xa + xb) / 2, -cY, z0 + 0.0015f, M.grid);
                    }
                b.Build("Floor_" + f, root);
            }
            // 바닥 블록 + 맨 아래 짧은 기둥 + 맨 위 테두리
            var bb = new Batch(cubeMesh, cylMesh);
            float h0 = Deck(0) - PanelT - 0.03f;
            for (int i = 0; i <= c.nx; i++)
                for (int j = 0; j <= c.ny; j++)
                {
                    if (!(PoleX(i) && PoleY(j))) continue;
                    float x = i * cX, y = j * cY;
                    B(bb, 0.14f, 0.14f, 0.03f, x, y, 0.015f, M.pad);
                    B(bb, Pole, Pole, h0, x, y, 0.03f + h0 / 2, M.pole);
                }
            float zt = TopZ - PanelT;
            B(bb, GW + Pole, 0.05f, 0.04f, GW / 2, 0, zt + 0.02f, M.pole);
            B(bb, GW + Pole, 0.05f, 0.04f, GW / 2, GD, zt + 0.02f, M.pole);
            B(bb, 0.05f, GD + Pole, 0.04f, 0, GD / 2, zt + 0.02f, M.pole);
            B(bb, 0.05f, GD + Pole, 0.04f, GW, GD / 2, zt + 0.02f, M.pole);
            bb.Build("BaseAndTop", root);
        }

        // ------------------------------------------------------------------ 리프트 (체인 구동, 바깥 면은 열림)
        void BuildLifts()
        {
            var c = config; float cX = (float)c.cellX, cY = (float)c.cellY;
            float H = TopZ + 0.35f;
            var root = new GameObject("Lifts").transform;
            var b = new Batch(cubeMesh, cylMesh);
            float hx = cX / 2 - 0.02f, hy = cY / 2 + 0.03f;
            foreach (var L in Sim.lifts)
            {
                float x = Wx(L.x), y = Wy(L.y), o = L.x < 0 ? -1 : 1;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2) B(b, 0.04f, 0.04f, H, x + sx * hx, y + sy * hy, H / 2, M.alu);
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    float yy = y + sy * hy;
                    B(b, 0.014f, 0.02f, H - 0.1f, x + o * 0.06f, yy, H / 2, M.chain);
                    B(b, 0.014f, 0.02f, H - 0.1f, x - o * 0.06f, yy, H / 2, M.chain);
                    Cy(b, 0.05f, 0.03f, x, yy, H - 0.07f, 'Y', M.chain);
                    Cy(b, 0.05f, 0.03f, x, yy, 0.07f, 'Y', M.chain);
                    for (int f = 0; f <= c.nf; f++) B(b, cX - 0.02f, 0.03f, 0.03f, x, yy, Deck(f) - 0.04f, M.alu);
                }
                B(b, cX + 0.06f, cY + 0.12f, 0.05f, x, y, H + 0.025f, M.alu);
                B(b, 0.30f, 0.18f, 0.16f, x, y, H + 0.13f, M.black);
                B(b, 0.10f, cY + 0.12f, 0.06f, x, y, H - 0.02f, M.chain);
                B(b, cX + 0.1f, cY + 0.2f, 0.02f, x, y, 0.01f, M.pad);
                // 승강 플랫폼 (원점 = 상면)
                var pt = new GameObject("LiftPlatform_" + L.id).transform; pt.SetParent(root, false);
                Cube("Deck", new Vector3(cX - 0.06f, 0.025f, cY - 0.04f), new Vector3(0, -0.0125f, 0), M.plate, pt);
                Cube("EdgeA", new Vector3(cX - 0.06f, 0.006f, 0.02f), new Vector3(0, 0.003f, (cY - 0.04f) / 2 - 0.01f), M.yellow, pt, false);
                Cube("EdgeB", new Vector3(cX - 0.06f, 0.006f, 0.02f), new Vector3(0, 0.003f, -(cY - 0.04f) / 2 + 0.01f), M.yellow, pt, false);
                for (int sy = -1; sy <= 1; sy += 2)
                    Cube("Bracket", new Vector3(0.16f, 0.12f, 0.05f), new Vector3(0, -0.05f, sy * (hy - 0.03f)), M.chain, pt);
                liftT.Add(pt);
            }
            b.Build("Shafts", root);
        }

        // ------------------------------------------------------------------ 피킹 스테이션 (피킹층 앞 y = -1 행, 작업자는 바닥에 섬)
        void BuildStations()
        {
            var c = config; float cX = (float)c.cellX, cY = (float)c.cellY, zf = Deck(c.stationFloor);
            float binTop = zf + (float)(c.binFoot + c.binH);
            var root = new GameObject("Stations").transform;
            var b = new Batch(cubeMesh, cylMesh);
            for (int si = 0; si < c.stationX0.Length; si++)
            {
                int n = c.stationN[si];
                float xa = c.stationX0[si] * cX, xb = (c.stationX0[si] + n) * cX, xm = (xa + xb) / 2, L = xb - xa;
                float yf = -cY - 0.06f, hh = 2.35f;
                B(b, L + 0.1f, 0.18f, 0.03f, xm, yf - 0.07f, zf + 0.30f, M.white);
                B(b, L + 0.1f, 0.02f, 0.30f + zf, xm, yf + 0.01f, (0.30f + zf) / 2, M.white);
                for (int k = 0; k < n; k++)
                {
                    float xc = xa + (k + 0.5f) * cX;
                    B(b, 0.06f, 0.06f, 0.015f, xc + 0.2f, yf - 0.10f, zf + 0.322f, M.black);
                    Cy(b, 0.018f, 0.012f, xc + 0.2f, yf - 0.10f, zf + 0.335f, 'Z', M.ledGreen);
                    B(b, 0.30f, 0.02f, 0.20f, xc, yf - 0.02f, binTop + 0.38f, M.black);
                    B(b, 0.27f, 0.004f, 0.17f, xc, yf - 0.032f, binTop + 0.38f, M.screen);
                    B(b, 0.03f, 0.03f, 0.30f, xc, yf, binTop + 0.16f, M.white);
                }
                B(b, 0.06f, 0.06f, hh, xa - 0.08f, yf, hh / 2, M.white);
                B(b, 0.06f, 0.06f, hh, xb + 0.08f, yf, hh / 2, M.white);
                B(b, L + 0.22f, 0.10f, 0.10f, xm, yf, hh, M.white);
                B(b, 0.26f, 0.20f, 0.12f, xm, yf - 0.05f, hh - 0.11f, M.black);
                Cy(b, 0.035f, 0.02f, xm, yf - 0.05f, hh - 0.18f, 'Z', M.lens);
                foreach (float xx in new[] { xa + 0.1f, xb - 0.1f })
                {
                    B(b, 0.10f, 0.08f, 0.07f, xx, yf - 0.06f, hh - 0.09f, M.black);
                    Cy(b, 0.02f, 0.02f, xx, yf - 0.11f, hh - 0.09f, 'Y', M.lens);
                }
                Cy(b, 0.03f, 0.06f, xb + 0.08f, yf, hh + 0.08f, 'Z', M.ledRed);
                Cy(b, 0.03f, 0.06f, xb + 0.08f, yf, hh + 0.14f, 'Z', M.ledGreen);
                B(b, L + 0.3f, 1.1f, 0.012f, xm, yf - 0.75f, 0.006f, M.mat);
                float ys = yf - 1.75f;
                float[] lv = { 0.30f, 0.75f };
                for (int l = 0; l < lv.Length; l++)
                {
                    B(b, L, 0.5f, 0.025f, xm, ys, lv[l], M.alu);
                    for (int k = 0; k < n; k++)
                    {
                        float xc = xa + (k + 0.5f) * cX;
                        B(b, 0.36f, 0.30f, 0.22f, xc, ys, lv[l] + 0.125f, (k + l) % 3 != 0 ? M.items[0] : M.items[1]);
                        B(b, 0.04f, 0.004f, 0.02f, xc, ys + 0.255f, lv[l] + 0.012f, (k + l + si) % 4 == 0 ? M.ledGreen : M.black);
                    }
                }
                foreach (float xx in new[] { xa, xb })
                    foreach (float yy in new[] { ys - 0.24f, ys + 0.24f }) B(b, 0.035f, 0.035f, 1.05f, xx, yy, 0.525f, M.alu);
                // 피킹할 빈을 비추는 프로젝터 빛
                var lg = new GameObject("Projector_" + si); lg.transform.SetParent(root);
                lg.transform.position = U(xm, yf - 0.05f, hh - 0.2f);
                var li = lg.AddComponent<Light>(); li.type = LightType.Spot; li.spotAngle = 18; li.innerSpotAngle = 10;
                li.range = 4f; li.intensity = 0f; li.color = new Color(0.55f, 0.85f, 1f); li.shadows = LightShadows.None;
                projector.Add(li);
            }
            b.Build("StationParts", root);
        }

        // ------------------------------------------------------------------ 로봇
        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            var prefab = Resources.Load<GameObject>("RapyutaModels/rapyuta_robot");
            foreach (var r in Sim.robots)
            {
                var root = new GameObject("Robot_" + r.id).transform; root.SetParent(parent, false);
                Transform plate = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                    FitLongAxisX(go, RobotL);
                    foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name.StartsWith("LiftPlate")) plate = t;
                }
                else
                {
                    Cube("Body", new Vector3(RobotL, 0.052f, RobotW), new Vector3(0, 0.038f, 0), M.robot, root);
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        Cube("Lens", new Vector3(0.004f, 0.016f, 0.10f), new Vector3(sx * (RobotL / 2 + 0.001f), 0.045f, 0), M.lens, root, false);
                        Cube("LED", new Vector3(0.004f, 0.006f, 0.22f), new Vector3(sx * (RobotL / 2 + 0.001f), 0.028f, 0), M.led, root, false);
                        for (int sy = -1; sy <= 1; sy += 2)
                        {
                            var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(w.GetComponent<Collider>());
                            w.transform.SetParent(root, false); w.transform.localPosition = new Vector3(sx * (RobotL / 2 - 0.065f), 0.031f, sy * (RobotW / 2 - 0.035f));
                            w.transform.localRotation = Quaternion.Euler(90, 0, 0); w.transform.localScale = new Vector3(0.06f, 0.025f, 0.06f);
                            w.GetComponent<MeshRenderer>().sharedMaterial = M.roller;
                        }
                    }
                    plate = new GameObject("LiftPlate").transform; plate.SetParent(root, false); plate.localPosition = new Vector3(0, PlateRest, 0);
                    Cube("Plate", new Vector3(RobotL - 0.18f, 0.010f, RobotW - 0.06f), new Vector3(0, -0.005f, 0), M.plate, plate);
                }
                robotT.Add(root); plateT.Add(plate);
                plateRest.Add(plate ? plate.position - root.position : Vector3.zero);
            }
        }

        // 모델 긴 축이 Unity X(= 시뮬레이션 x)에 오도록 돌리고, 단위가 어긋나면 크기 보정
        static void FitLongAxisX(GameObject go, float targetLen)
        {
            var rs = go.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return;
            var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
            if (bb.size.z > bb.size.x * 1.05f) { go.transform.localRotation = Quaternion.Euler(0, 90, 0) * go.transform.localRotation; }
            float sc = targetLen / Mathf.Max(1e-4f, Mathf.Max(bb.size.x, bb.size.z));
            if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
        }

        // ------------------------------------------------------------------ 빈
        void BuildBins()
        {
            var parent = new GameObject("Bins").transform;
            var tpl = new List<GameObject>();
            var holder = new GameObject("BinTemplates").transform; holder.gameObject.SetActive(false);
            for (int v = 0; v < 4; v++)
            {
                var pf = Resources.Load<GameObject>("RapyutaModels/rapyuta_bin_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("BinTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m); FitLongAxisX(m, BinW + 0.02f); EquipFX.MeshFix.ResolveModel(m);
                }
                else go = FallbackBin(v, holder);
                tpl.Add(go);
            }
            var rnd = new System.Random(5);
            for (int id = 0; id < Sim.bins; id++)
            {
                int v = rnd.NextDouble() < 0.15 ? 0 : 1 + rnd.Next(3);
                var go = Instantiate(tpl[v], parent, false); go.name = "Bin_" + id;
                if (rnd.NextDouble() < 0.5) go.transform.localRotation = Quaternion.Euler(0, 180, 0);
                binT.Add(go.transform);
            }
        }

        GameObject FallbackBin(int v, Transform holder)
        {
            float H = (float)config.binH, t = 0.006f;
            var go = new GameObject("BinTpl_" + v); go.transform.SetParent(holder, false);
            Cube("Floor", new Vector3(BinW - 0.03f, t, BinD - 0.03f), new Vector3(0, t / 2, 0), M.bin, go.transform);
            for (int s = -1; s <= 1; s += 2)
            {
                Cube("WallL", new Vector3(BinW, H, t), new Vector3(0, H / 2, s * (BinD / 2 - t / 2)), M.bin, go.transform);
                Cube("WallS", new Vector3(t, H, BinD), new Vector3(s * (BinW / 2 - t / 2), H / 2, 0), M.bin, go.transform);
                Cube("Handle", new Vector3(0.004f, 0.035f, 0.14f), new Vector3(s * (BinW / 2 + 0.001f), H - 0.06f, 0), M.binDark, go.transform, false);
            }
            if (v > 0)
            {
                var rnd = new System.Random(11 + v); float x = -BinW / 2 + 0.03f;
                for (int k = 0; k < 3; k++)
                {
                    float sx = 0.12f + (float)rnd.NextDouble() * 0.08f, sz = 0.08f + (float)rnd.NextDouble() * H * 0.6f;
                    Cube("Item", new Vector3(sx, sz, 0.3f), new Vector3(x + sx / 2, t + sz / 2, 0), M.items[(v + k) % M.items.Length], go.transform);
                    x += sx + 0.01f;
                }
            }
            return go;
        }

        // ------------------------------------------------------------------ 카메라 + 후처리
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.75f, 0.77f, 0.8f);
            EquipFX.CameraFX.ConfigureClip(Cam, 0.06f, 90f);   // near 0.02 → 0.06: 원거리 깊이 정밀도 향상 (깜빡임 감소)
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<RapyutaCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<RapyutaCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.4f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.6f); ca.contrast.Override(10f); ca.saturation.Override(0f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.18f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(8f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.2f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.08f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof; cine.motionBlur = mb;

            // 모던 자막 (설비명 타이틀 + 샷 설명)
            var cap = gameObject.GetComponent<EquipFX.CaptionPresenter>();
            if (cap == null) cap = gameObject.AddComponent<EquipFX.CaptionPresenter>();
            cap.info = new EquipFX.CaptionInfo
            {
                category = "Multi-level Robotic ASRS",
                name = "Rapyuta ASRS",
                korean = "라퓨타 다층 로봇 자동창고",
                spec = $"{config.nf}층  ·  {config.nx}×{config.ny} 셀  ·  로봇 {config.robots}대  ·  층간 리프트 {Sim.lifts.Count}대",
                accent = new Color(0.30f, 0.78f, 1f),
            };
            cap.source = cine;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (RapyutaMovieCapture.IsRecording && RapyutaMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 360, 240), GUI.skin.box);
            GUILayout.Label($"<b>Rapyuta ASRS-style simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Floors {config.nf}  Robots {Sim.robots.Count}  Lifts {Sim.lifts.Count}  Bins {Sim.bins}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} bins/h   Done {Sim.statDone}   Backlog {Sim.Backlog}");
            GUILayout.Label($"Digs per fetch {Sim.DigsPerFetch:0.00}   Robot util {Sim.RobotUtil:P0}   Station util {Sim.StationUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
