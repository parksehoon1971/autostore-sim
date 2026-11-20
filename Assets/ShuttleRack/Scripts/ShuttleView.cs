// 다층 셔틀랙: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/랙/셔틀 레일/버퍼·1층 컨베이어/리프트 탑/스테이션/셔틀/토트/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/ShuttleRack/Resources/ShuttleModels/shuttle_shuttle.fbx, shuttle_carriage.fbx, shuttle_tote_v0~1.fbx
//       (blender/shuttle_blender.py 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/shuttle_blender.py 와 같은 값.
// 겹치는 면(z-fighting)이 없도록 바닥 표시·띠는 2 mm 이상 띄우고, 카메라 near 0.08 / far 150 으로 깊이 정밀도를 확보합니다.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ShuttleRack
{
    public class ShuttleView : MonoBehaviour
    {
        [Header("Simulation")]
        public ShuttleConfig config = new ShuttleConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적·카메라와 같게 하려면 1200 (설정도 기본값 그대로)")]
        public float warmupSeconds = 1200f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_shuttle_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public ShuttleSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        // 배치 게임 모듈 모드: 건물·조명·카메라 없이 설비만 만들고, 옮겨 놓은 위치(transform.position)로 동기화
        [System.NonSerialized] public bool moduleMode;
        public static Vector3 UOff;   // U() 가 더하는 위치. 모듈 동기화 중에만 0 이 아님

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float UprX = 0.05f, UprY = 0.07f, ShL = 0.86f, ShW = 0.66f, ShH = 0.12f, MastX = 0.78f, MastY = 0.62f;
        public const int CaptionLayer = 31;

        readonly List<Transform> shT = new List<Transform>();
        readonly List<Transform> armT = new List<Transform>();
        readonly List<Vector3> armRest = new List<Vector3>();
        readonly List<Transform> carT = new List<Transform>();
        readonly List<Transform> toteT = new List<Transform>();
        readonly List<Light> spot = new List<Light>();
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => UOff + new Vector3((float)x, (float)z, (float)y);
        float Wx(int b) => (float)Sim.Wx(b);
        float AisleY(int a) => (float)Sim.AisleY(a);
        float LevelZ(int l) => (float)Sim.LevelZ(l);
        float GW => config.nx * (float)config.bayW;
        float GD => (float)Sim.depthY;
        float R => (float)Sim.R;
        float TopZ => LevelZ(config.levels - 1) + (float)config.toteH + 0.2f;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new ShuttleSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            if (!moduleMode) BuildEnvironment();
            BuildRacks();
            BuildFront();
            BuildFloorMarks();
            BuildShuttles();
            BuildCarriages();
            BuildTotes();
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
            for (int i = 0; i < Sim.shuttles.Count; i++)
            {
                var s = Sim.shuttles[i];
                shT[i].position = U(s.x, Sim.AisleY(s.a), Sim.LevelZ(s.l));
                if (armT[i]) armT[i].position = shT[i].position + armRest[i] + new Vector3(0, 0, (float)(s.side * s.arm) * R);
            }
            for (int i = 0; i < Sim.lifts.Count; i++)
            {
                var lf = Sim.lifts[i];
                carT[i].position = U(config.liftX, Sim.LiftY(lf), lf.z);
            }
            for (int id = 0; id < toteT.Count; id++)
            {
                var p = Sim.TotePose(id);
                toteT[id].position = U(p[0], p[1], p[2]);
            }
            for (int i = 0; i < Sim.stations.Count; i++) spot[i].intensity = Sim.stations[i].cur >= 0 ? 6f : 0f;
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material galv, rail, beam, frame, tote, toteDark, label, barcode, body, chassis, accent, rubber, hub, lens,
                led, ledRed, ledGreen, roller, convFrame, belt, alu, yellow, white, black, screen, mat, mesh, floor, wall, ceiling,
                steel, lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                galv = Lit("Galvanized", new Color(0.62f, 0.64f, 0.66f), 1f, 0.62f);
                Noise(galv, new Color(0.58f, 0.60f, 0.62f), new Color(0.68f, 0.70f, 0.72f), 6f, 4);
                rail = Lit("RailSteel", new Color(0.55f, 0.57f, 0.59f), 1f, 0.58f);
                beam = Coat("BeamOrange", new Color(0.80f, 0.28f, 0.04f), 0.6f);
                frame = Coat("FrameBlue", new Color(0.06f, 0.18f, 0.40f), 0.6f);
                tote = Lit("TotePP", Color.white, 0f, 0.55f);
                Noise(tote, new Color(0.15f, 0.29f, 0.50f), new Color(0.18f, 0.32f, 0.55f), 30f, 3);
                toteDark = Lit("ToteRim", new Color(0.08f, 0.15f, 0.28f), 0f, 0.5f);
                label = Lit("ToteLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                barcode = Lit("Barcode", new Color(0.02f, 0.02f, 0.02f), 0f, 0.4f);
                body = Coat("ShuttleBody", new Color(0.82f, 0.83f, 0.84f), 0.7f);
                chassis = Lit("ChassisGrey", new Color(0.10f, 0.105f, 0.11f), 0.3f, 0.55f);
                accent = Coat("AccentAmber", new Color(0.95f, 0.55f, 0.05f), 0.65f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                led = Emissive("LEDCyan", new Color(0.2f, 0.8f, 1f), 5f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 4f);
                roller = Lit("RollerZinc", new Color(0.72f, 0.73f, 0.75f), 1f, 0.75f);
                convFrame = Lit("ConvFrame", new Color(0.30f, 0.31f, 0.33f), 0.6f, 0.6f);
                belt = Lit("BeltBlack", new Color(0.03f, 0.03f, 0.032f), 0f, 0.3f);
                alu = Lit("Aluminium", new Color(0.78f, 0.79f, 0.80f), 1f, 0.72f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                white = Coat("WhitePanel", new Color(0.88f, 0.89f, 0.90f), 0.65f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                screen = Emissive("Screen", new Color(0.35f, 0.6f, 1f), 1.5f);
                mat = Lit("FloorMat", new Color(0.06f, 0.06f, 0.065f), 0f, 0.1f);
                mesh = Lit("FenceMesh", new Color(0.12f, 0.12f, 0.13f), 0.5f, 0.5f);
                items = new[]
                {
                    Lit("Cardboard", new Color(0.52f, 0.36f, 0.20f), 0f, 0.4f), Lit("CardboardWhite", new Color(0.78f, 0.74f, 0.68f), 0f, 0.4f),
                    Lit("ProdBlue", new Color(0.08f, 0.25f, 0.62f), 0f, 0.4f), Lit("ProdRed", new Color(0.70f, 0.08f, 0.06f), 0f, 0.4f),
                    Lit("ProdGreen", new Color(0.12f, 0.45f, 0.22f), 0f, 0.4f), Lit("ProdYellow", new Color(0.90f, 0.72f, 0.10f), 0f, 0.4f),
                };
                floor = Epoxy();
                wall = Lit("WallWhite", new Color(0.80f, 0.81f, 0.82f), 0f, 0.4f);
                ceiling = Lit("Ceiling", new Color(0.55f, 0.56f, 0.58f), 0f, 0.2f);
                steel = Lit("SteelBeam", new Color(0.70f, 0.71f, 0.72f), 0.6f, 0.6f);
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
        // 시뮬레이션 좌표 (크기 sx, sy, sz / 중심) 로 자식 큐브
        static GameObject CubeS(string name, float sx, float sy, float sz, float x, float y, float z, Material m, Transform parent, bool shadows = true) =>
            Cube(name, new Vector3(sx, sz, sy), new Vector3(x, z, y), m, parent, shadows);

        // ------------------------------------------------------------------ 건물 (blender make_building 과 같은 형상)
        void BuildEnvironment()
        {
            float cxw = GW / 2 - 3, cyw = GD / 2;
            var env = new GameObject("Building").transform;
            const float S = 60f, Hh = 11f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            B(b, S * 0.8f, 0.12f, 0.002f, cxw, -6.2f, 0.003f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 14, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 22, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 22, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 20, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -18; k < 20; k += 4) B(b, 0.3f, S, 0.6f, cxw + k, cyw, Hh - 0.4f, M.steel);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 13.8f, 1.2f, M.green);
            for (int k = -16; k < 20; k += 8) B(b, 0.4f, 0.4f, Hh, cxw + k, cyw + 13.7f, Hh / 2, M.steel);
            for (int ix = -3; ix <= 3; ix++)
                for (int iy = -3; iy <= 3; iy++)
                    B(b, 1.8f, 0.45f, 0.05f, cxw + ix * 5f, cyw + iy * 5f, Hh - 0.8f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 6f, cyw + iy * 6f, Hh - 0.9f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 35; l.intensity = 26f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f;
            sun.shadowBias = 0.05f; sun.shadowNormalBias = 0.4f;   // 그림자 줄무늬(셀프 섀도 깜빡임) 방지
            sun.transform.rotation = Quaternion.Euler(58, -35, 0);
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

        // x 방향 롤러 컨베이어 (윗면 = zTop - 0.001)
        void RollerBed(Batch b, float x0, float x1, float y, float zTop, float width, bool legs = false, float pitch = 0.075f)
        {
            float L = x1 - x0, xc = (x0 + x1) / 2, r = 0.022f, zc = zTop - 0.001f - r;
            for (int sy = -1; sy <= 1; sy += 2) B(b, L, 0.035f, 0.09f, xc, y + sy * (width / 2 + 0.018f), zc - 0.02f, M.convFrame);
            int n = Mathf.Max(2, (int)(L / pitch));
            for (int k = 0; k < n; k++) Cy(b, r, width, x0 + (k + 0.5f) * L / n, y, zc, 'Y', M.roller);
            if (legs)
            {
                int m = (int)(L / 1.6f);
                for (int i = 0; i <= m; i++)
                {
                    float xx = x0 + 0.1f + i * (L - 0.2f) / Mathf.Max(1, m);
                    for (int sy = -1; sy <= 1; sy += 2) B(b, 0.04f, 0.04f, zc - 0.06f, xx, y + sy * (width / 2 + 0.018f), (zc - 0.06f) / 2, M.convFrame);
                }
            }
        }

        // ------------------------------------------------------------------ 랙 + 셔틀 레일 + 버퍼 컨베이어 (blender make_racks 와 같은 형상)
        void BuildRacks()
        {
            var c = config; float bW = (float)c.bayW, rackD = (float)c.rackD, zt = TopZ, gw = GW;
            var root = new GameObject("Racks").transform;
            for (int a = 0; a < c.aisles; a++)
            {
                var P = new Batch(cubeMesh, cylMesh); var Rb = new Batch(cubeMesh, cylMesh); var Bb = new Batch(cubeMesh, cylMesh);
                float ya = AisleY(a);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    float yf = ya + sgn * (float)c.aisleW / 2, yb = yf + sgn * rackD;
                    for (int k = c.bufBays; k <= c.nx; k++)
                    {
                        float x = k * bW;
                        foreach (float yy in new[] { yf + sgn * UprY / 2, yb - sgn * UprY / 2 })
                        {
                            B(P, UprX, UprY, zt, x, yy, zt / 2, M.frame);
                            B(P, 0.12f, 0.12f, 0.010f, x, yy, 0.005f, M.galv);
                        }
                        for (int z = 1; z <= (int)(zt / 1.5f); z++) B(P, 0.025f, rackD - 2 * UprY, 0.025f, x, (yf + yb) / 2, z * 1.5f, M.galv);
                    }
                    float x0b = c.bufBays * bW;
                    for (int l = 0; l < c.levels; l++)
                    {
                        float z = LevelZ(l);
                        for (int k = c.bufBays; k < c.nx; k++)
                        {
                            float xc = Wx(k);
                            for (int dx = -1; dx <= 1; dx += 2) B(Rb, 0.04f, rackD - 0.02f, 0.006f, xc + dx * (bW / 2 - 0.055f), (yf + yb) / 2, z - 0.005f, M.rail);
                        }
                        foreach (float yy in new[] { yf + sgn * 0.02f, yb - sgn * 0.02f }) B(Rb, gw - x0b, 0.03f, 0.05f, (gw + x0b) / 2, yy, z - 0.035f, M.beam);
                    }
                    B(P, gw + UprX, 0.06f, 0.10f, gw / 2, yf + sgn * 0.035f, zt + 0.05f, M.beam);
                }
                float gy = (float)c.aisleW / 2 - 0.03f;
                for (int l = 0; l < c.levels; l++)
                {
                    float zr = LevelZ(l) - ShH - 0.012f;
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        B(Rb, gw, 0.05f, 0.024f, gw / 2, ya + sy * gy, zr, M.rail);
                        B(Rb, gw, 0.006f, 0.05f, gw / 2, ya + sy * (gy + 0.028f), zr - 0.01f, M.rail);
                        RollerBed(Bb, (float)(c.liftX + c.xferOff) - 0.27f, c.bufBays * bW - 0.04f, ya + sy * R, LevelZ(l), (float)c.toteD + 0.06f);
                    }
                }
                P.Build("Frames_" + a, root); Rb.Build("Rails_" + a, root); Bb.Build("BufferConveyors_" + a, root);
            }
            var t = new Batch(cubeMesh, cylMesh);
            for (int a = 0; a < c.aisles - 1; a++)
            {
                float y = AisleY(a) + (float)c.aisleW / 2 + rackD + (float)c.flue / 2;
                for (int k = 0; k <= c.nx; k += 2)
                    foreach (float z in new[] { 2f, 4f, 6f })
                        if (z < zt) B(t, 0.04f, (float)c.flue + 2 * UprY + 0.02f, 0.04f, k * bW, y, z, M.galv);
            }
            t.Build("RackTies", root);
        }

        // ------------------------------------------------------------------ 리프트 탑 + 1층 컨베이어 + 스테이션 (blender make_front)
        void BuildFront()
        {
            var c = config;
            var root = new GameObject("Front").transform;
            var P = new Batch(cubeMesh, cylMesh); var Q = new Batch(cubeMesh, cylMesh);
            float zt = TopZ + 0.35f, xl = (float)c.liftX, xs = (float)c.stationX, zc = (float)c.convZ, D = (float)c.toteD;
            for (int a = 0; a < c.aisles; a++)
            {
                float ya = AisleY(a);
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    float y = ya + sy * R;
                    for (int dx = -1; dx <= 1; dx += 2)
                        for (int dy = -1; dy <= 1; dy += 2) B(P, 0.07f, 0.07f, zt, xl + dx * MastX / 2, y + dy * MastY / 2, zt / 2, M.frame);
                    foreach (float dz in new[] { 0.03f, zt - 0.04f })
                    {
                        for (int dy = -1; dy <= 1; dy += 2) B(P, MastX + 0.07f, 0.05f, 0.06f, xl, y + dy * MastY / 2, dz, M.frame);
                        for (int dx = -1; dx <= 1; dx += 2) B(P, 0.05f, MastY + 0.07f, 0.06f, xl + dx * MastX / 2, y, dz, M.frame);
                    }
                    for (int k = 1; k < (int)(zt / 1.2f); k++) B(P, 0.02f, 0.02f, 0.02f, xl + MastX / 2, y, k * 1.2f, M.galv);
                    for (int dy = -1; dy <= 1; dy += 2) B(P, 0.05f, 0.03f, zt - 0.1f, xl, y + dy * (D / 2 + 0.11f), zt / 2, M.rail);
                    B(P, 0.012f, 0.03f, zt - 0.2f, xl + MastX / 2 - 0.06f, y + 0.12f, zt / 2, M.belt);
                    B(P, 0.30f, 0.30f, 0.24f, xl, y, zt + 0.12f, M.chassis);
                    Cy(P, 0.03f, 0.06f, xl + MastX / 2, y - MastY / 2, zt + 0.06f, 'Z', M.ledGreen);
                }
                float yo = ya - R, yi = ya + R, xg = xl - (float)c.xferOff;
                RollerBed(Q, xs + 0.31f, xg + 0.27f, yo, zc, D + 0.06f, true);
                RollerBed(Q, xs + 0.31f, xg + 0.27f, yi, zc, D + 0.06f, true);
                for (int k = 0; k < 6; k++) B(Q, 0.025f, 2 * R + D + 0.06f, 0.012f, xs - 0.24f + k * 0.096f, ya, zc - 0.007f, M.belt);
                B(Q, 0.60f, 2 * R + D + 0.10f, 0.08f, xs, ya, zc - 0.06f, M.convFrame);
                for (int dx = -1; dx <= 1; dx += 2)
                    for (int dy = -1; dy <= 1; dy += 2) B(Q, 0.04f, 0.04f, zc - 0.1f, xs + dx * 0.25f, ya + dy * (R + 0.2f), (zc - 0.1f) / 2, M.convFrame);
                float xo = xs - 0.55f;
                B(Q, 0.40f, 1.30f, 0.04f, xo - 0.05f, ya, 0.92f, M.white);
                B(Q, 0.03f, 1.30f, 0.90f, xo + 0.14f, ya, 0.45f, M.white);
                B(Q, 0.20f, 0.004f, 0.03f, xo - 0.15f, ya - 0.55f, 0.942f, M.ledGreen);
                float ysc = ya - R - 0.55f;
                B(Q, 0.03f, 0.38f, 0.25f, xs - 0.2f, ysc, 1.40f, M.black);
                B(Q, 0.004f, 0.34f, 0.21f, xs - 0.218f, ysc, 1.40f, M.screen);
                B(Q, 0.04f, 0.04f, 1.3f, xs - 0.18f, ysc, 0.65f, M.alu);
                B(Q, 1.1f, 1.4f, 0.012f, xo - 0.75f, ya, 0.007f, M.mat);
                float xp = xo - 1.55f;
                float[] lv = { 0.30f, 0.80f, 1.30f };
                for (int l = 0; l < lv.Length; l++)
                {
                    B(Q, 0.50f, 1.40f, 0.025f, xp, ya, lv[l], M.alu);
                    for (int k = 0; k < 3; k++)
                    {
                        float y = ya + (k - 1) * 0.45f;
                        B(Q, 0.32f, 0.40f, 0.22f, xp, y, lv[l] + 0.125f, (k + l) % 3 != 0 ? M.items[0] : M.items[1]);
                        B(Q, 0.004f, 0.05f, 0.02f, xp + 0.255f, y, lv[l] + 0.012f, (k + l + a) % 4 == 0 ? M.ledGreen : M.black);
                    }
                }
                foreach (float yy in new[] { ya - 0.72f, ya + 0.72f })
                    foreach (float xx in new[] { xp - 0.24f, xp + 0.24f }) B(Q, 0.035f, 0.035f, 1.6f, xx, yy, 0.8f, M.alu);
                B(Q, 0.12f, 0.9f, 0.04f, xs - 0.1f, ya, 2.32f, M.lightPanel);
                for (int dy = -1; dy <= 1; dy += 2) B(Q, 0.008f, 0.008f, 7.5f, xs - 0.1f, ya + dy * 0.40f, 2.34f + 3.75f, M.alu);
                var lg = new GameObject("StationSpot_" + a); lg.transform.SetParent(root);
                lg.transform.position = U(xs - 0.1f, ya, 2.28f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                var li = lg.AddComponent<Light>(); li.type = LightType.Spot; li.spotAngle = 45; li.innerSpotAngle = 22;
                li.range = 4f; li.intensity = 0f; li.color = new Color(1f, 0.97f, 0.9f); li.shadows = LightShadows.None;
                spot.Add(li);
            }
            P.Build("LiftTowers", root);
            Q.Build("FloorConveyorsAndStations", root);
        }

        // ------------------------------------------------------------------ 바닥 표시 + 펜스 (모두 바닥에서 2 mm 이상 띄움)
        void BuildFloorMarks()
        {
            var c = config;
            var b = new Batch(cubeMesh, cylMesh);
            float gw = GW, gd = GD, x0 = (float)c.stationX - 3f, x1 = gw + 0.8f;
            foreach (float y in new[] { -1.25f, gd + 0.6f }) B(b, x1 - x0, 0.08f, 0.002f, (x0 + x1) / 2, y, 0.003f, M.yellow);
            foreach (float x in new[] { x0, (float)c.liftX - 0.7f }) B(b, 0.08f, gd + 1.85f, 0.002f, x, (gd - 0.65f) / 2, 0.003f, M.yellow);
            for (int k = 0; k < (int)((x1 - 1f) / 1f); k++) B(b, 0.35f, 0.30f, 0.002f, 0.5f + k * 1f, -1.55f, 0.003f, M.white);
            void Fence(float xa, float ya, float xb, float yb)
            {
                float L = Mathf.Sqrt((xb - xa) * (xb - xa) + (yb - ya) * (yb - ya)); int n = Mathf.Max(1, (int)(L / 1.5f));
                for (int i = 0; i <= n; i++) B(b, 0.06f, 0.06f, 2.2f, xa + (xb - xa) * i / n, ya + (yb - ya) * i / n, 1.1f, M.yellow);
                bool alongx = Mathf.Abs(xb - xa) > Mathf.Abs(yb - ya);
                float cx = (xa + xb) / 2, cy = (ya + yb) / 2;
                foreach (float z in new[] { 0.15f, 2.15f }) B(b, alongx ? L : 0.03f, alongx ? 0.03f : L, 0.03f, cx, cy, z, M.yellow);
                B(b, alongx ? L : 0.008f, alongx ? 0.008f : L, 1.95f, cx, cy, 1.15f, M.mesh);
            }
            Fence(0f, -0.9f, gw + 0.6f, -0.9f); Fence(gw + 0.6f, -0.9f, gw + 0.6f, gd + 0.9f);
            b.Build("FloorMarks", new GameObject("Floor").transform);
        }

        // ------------------------------------------------------------------ 셔틀 (원점 = 셔틀 위 토트 바닥 중심)
        void BuildShuttles()
        {
            var parent = new GameObject("Shuttles").transform;
            var prefab = Resources.Load<GameObject>("ShuttleModels/shuttle_shuttle");
            float gy = (float)config.aisleW / 2 - 0.03f, tw = (float)config.toteW, td = (float)config.toteD;
            foreach (var s in Sim.shuttles)
            {
                var root = new GameObject($"Shuttle_{s.a}_{s.l}").transform; root.SetParent(parent, false);
                Transform arm = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                    foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name.StartsWith("Arm")) arm = t;
                }
                else
                {
                    CubeS("Body", ShL - 0.04f, ShW - 0.08f, ShH - 0.03f, 0, 0, -ShH / 2 - 0.01f, M.chassis, root);
                    CubeS("Deck", ShL - 0.30f, ShW - 0.10f, 0.012f, 0, 0, -0.008f, M.body, root);
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        float hx = sx * (ShL / 2 - 0.06f);
                        CubeS("Housing", 0.12f, ShW - 0.04f, ShH + 0.10f, hx, 0, (0.10f - ShH) / 2, M.body, root);
                        CubeS("Accent", 0.004f, ShW - 0.20f, 0.022f, hx + sx * 0.062f, 0, 0.03f, M.accent, root, false);
                        for (int sy = -1; sy <= 1; sy += 2)
                        {
                            var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(g.GetComponent<Collider>());
                            g.transform.SetParent(root, false); g.transform.localPosition = new Vector3(sx * (ShL / 2 - 0.13f), -ShH + 0.045f, sy * gy);
                            g.transform.localRotation = Quaternion.Euler(90, 0, 0); g.transform.localScale = new Vector3(0.09f, 0.015f, 0.09f);
                            g.GetComponent<MeshRenderer>().sharedMaterial = M.rubber;
                        }
                    }
                    arm = new GameObject("Arm").transform; arm.SetParent(root, false);
                    for (int sx = -1; sx <= 1; sx += 2)
                        CubeS("Tele", 0.014f, td + 0.04f, 0.06f, sx * (tw / 2 + 0.0095f), 0, 0.06f, M.alu, arm);
                }
                shT.Add(root); armT.Add(arm);
                armRest.Add(arm ? arm.position - root.position : Vector3.zero);
            }
        }

        // ------------------------------------------------------------------ 리프트 캐리지 (원점 = 토트 바닥 중심)
        void BuildCarriages()
        {
            var parent = new GameObject("LiftCarriages").transform;
            var prefab = Resources.Load<GameObject>("ShuttleModels/shuttle_carriage");
            float w = (float)config.toteW + 0.06f, d = (float)config.toteD + 0.10f;
            foreach (var lf in Sim.lifts)
            {
                var root = new GameObject($"Carriage_{lf.a}_{(lf.side < 0 ? "out" : "in")}").transform; root.SetParent(parent, false);
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false); go.transform.localPosition = Vector3.zero; M.Remap(go);
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                }
                else
                {
                    CubeS("Bed", w, d, 0.09f, 0, 0, -0.052f, M.convFrame, root);
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        CubeS("Side", w + 0.04f, 0.03f, 0.12f, 0, sy * (d / 2 + 0.015f), -0.02f, M.frame, root);
                        CubeS("Guide", 0.06f, 0.04f, 0.30f, 0, sy * (d / 2 + 0.04f), 0f, M.chassis, root);
                    }
                }
                carT.Add(root);
            }
        }

        // ------------------------------------------------------------------ 토트
        void BuildTotes()
        {
            var parent = new GameObject("Totes").transform;
            var tpl = new List<GameObject>();
            var holder = new GameObject("ToteTemplates").transform; holder.gameObject.SetActive(false);
            for (int v = 0; v < 2; v++)
            {
                var pf = Resources.Load<GameObject>("ShuttleModels/shuttle_tote_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m);
                    EquipFX.MeshFix.ResolveModel(m);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                }
                else go = FallbackTote(v, holder);
                tpl.Add(go);
            }
            var rnd = new System.Random(5);
            for (int id = 0; id < Sim.totes; id++)
            {
                int v = rnd.NextDouble() < 0.2 ? 0 : 1;
                var go = Instantiate(tpl[v], parent, false); go.name = "Tote_" + id;
                if (rnd.NextDouble() < 0.5) go.transform.localRotation = Quaternion.Euler(0, 180, 0);
                toteT.Add(go.transform);
            }
        }

        GameObject FallbackTote(int v, Transform holder)
        {
            float H = (float)config.toteH, W = (float)config.toteW, D = (float)config.toteD, t = 0.006f;
            var go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
            Cube("Floor", new Vector3(W - 0.02f, t, D - 0.02f), new Vector3(0, t / 2, 0), M.tote, go.transform);
            for (int s = -1; s <= 1; s += 2)
            {
                Cube("WallL", new Vector3(W, H, t), new Vector3(0, H / 2, s * (D / 2 - t / 2)), M.tote, go.transform);
                Cube("WallS", new Vector3(t, H, D - 2 * t), new Vector3(s * (W / 2 - t / 2), H / 2, 0), M.tote, go.transform);
                Cube("Label", new Vector3(0.10f, 0.055f, 0.003f), new Vector3(-0.17f, H - 0.11f, s * (D / 2 + 0.002f)), M.label, go.transform, false);
            }
            if (v > 0)
            {
                var rnd = new System.Random(11 + v); float x = -W / 2 + 0.03f;
                for (int k = 0; k < 3; k++)
                {
                    float sx = 0.11f + (float)rnd.NextDouble() * 0.05f, sz = 0.07f + (float)rnd.NextDouble() * H * 0.6f;
                    Cube("Item", new Vector3(sx, sz, 0.26f), new Vector3(x + sx / 2, t + 0.002f + sz / 2, 0), M.items[(v + k) % M.items.Length], go.transform);
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
            Cam.nearClipPlane = 0.08f; Cam.farClipPlane = 150f;         // 깊이 정밀도 확보 (먼 면 깜빡임 방지)
            Cam.cullingMask &= ~(1 << CaptionLayer);
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<ShuttleCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<ShuttleCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.4f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.45f); ca.contrast.Override(10f); ca.saturation.Override(0f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.18f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(6f); dof.focalLength.Override(28f); dof.aperture.Override(8f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.25f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.06f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || ShuttleMovieCapture.IsRecording) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 400, 240), GUI.skin.box);
            GUILayout.Label($"<b>Shuttle AS/RS simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Aisles {config.aisles}  Levels {config.levels}  Shuttles {Sim.shuttles.Count}  Lifts {Sim.lifts.Count}  Totes {Sim.totes}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} totes/h   Done {Sim.statDone}   Backlog {Sim.Backlog}");
            GUILayout.Label($"Lead {Sim.AvgLead:0} s   Shuttle util {Sim.ShuttleUtil:P0}   Lift util {Sim.LiftUtil:P0}   Station util {Sim.StationUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
