// Libiao T-Sort 핑거형 플랫폼 소터: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/메인 데크/핑거/슈트 칸막이/슈트 통/투입 스테이션/충전 파일/로봇/소포/조명/후처리/카메라/캡션을 코드로 만듭니다.
// 모델: Assets/TSorter/Resources/TSorterModels/tsorter_robot.fbx, tsorter_bin.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/tsorter_blender.py 와 같은 값.
// z-파이팅 방지: 같은 면에 겹치는 판이 없게 하고(경계 1 mm 띄움), 표시물은 1.5 mm 이상 띄우며, 근접 클립을 0.05 m 로 둡니다.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TSorter
{
    public class TSorterView : MonoBehaviour
    {
        [Header("Simulation")]
        public TSorterConfig config = new TSorterConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 600")]
        public float warmupSeconds = 600f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_tsorter_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;
        public bool showCaption = true;

        public TSorterSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        public TSorterCaption Caption { get; private set; }

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float DeckT = 0.03f, RobotL = 0.48f, RobotW = 0.38f, RobotH = 0.19f, TrayZ = 0.23f;
        public const float BinW = 0.50f, BinZ0 = 0.11f, BinTop = 0.74f, ChuteZ0 = 0.80f;

        readonly List<Transform> robotT = new List<Transform>();
        readonly List<Transform> trayT = new List<Transform>();
        readonly List<Quaternion> trayRest = new List<Quaternion>();
        readonly Dictionary<int, GameObject> parcelGo = new Dictionary<int, GameObject>();
        readonly Dictionary<int, int> parcelTpl = new Dictionary<int, int>();
        readonly HashSet<int> settled = new HashSet<int>();
        readonly List<GameObject> templates = new List<GameObject>();
        readonly List<Stack<GameObject>> pools = new List<Stack<GameObject>>();
        Transform parcelRoot;
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        // Blender 오일러 XYZ (R = Rz·Ry·Rx, 오른손) → Unity
        public static Quaternion R(double rx, double ry, double rz) =>
            Quaternion.AngleAxis((float)(-rz * Mathf.Rad2Deg), Vector3.up) *
            Quaternion.AngleAxis((float)(-ry * Mathf.Rad2Deg), Vector3.forward) *
            Quaternion.AngleAxis((float)(-rx * Mathf.Rad2Deg), Vector3.right);
        float Cell => (float)config.cell;
        float Z => (float)config.deckZ;
        float Cx(int i) => (i + 0.5f) * Cell;
        float Cy(int j) => (j + 0.5f) * Cell;
        int NY => 4 * config.fingers;
        int Tip => config.mainX + config.fingerLen - 1;
        float MW => config.mainX * Cell;
        float XE => (Tip + 1) * Cell;
        float GapY(int k) => 4 * k * Cell;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new TSorterSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildPlatform();
            BuildBins();
            BuildStations();
            BuildRobots();
            BuildParcelTemplates();
            BuildCameraAndPost();
            if (showCaption)
            {
                Caption = gameObject.GetComponent<TSorterCaption>();
                if (Caption == null) Caption = gameObject.AddComponent<TSorterCaption>();
                Caption.Init(this);
            }
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

        // ------------------------------------------------------------------ 동기화
        void SyncAll()
        {
            for (int i = 0; i < Sim.robots.Count; i++)
            {
                var r = Sim.robots[i];
                var t = robotT[i];
                t.position = U(r.x, r.y, config.deckZ);
                t.rotation = Quaternion.AngleAxis((float)(-r.h * Mathf.Rad2Deg), Vector3.up);
                if (trayT[i]) trayT[i].rotation = Quaternion.AngleAxis((float)(r.tilt * Mathf.Rad2Deg), t.right) * t.rotation * trayRest[i];
            }
            // 소포: 살아 있는 것만 오브젝트 유지 (풀 재사용)
            var seen = new HashSet<int>(Sim.alive);
            var drop = new List<int>();
            foreach (var kv in parcelGo) if (!seen.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (int id in drop)
            {
                var go = parcelGo[id]; go.SetActive(false); pools[parcelTpl[id]].Push(go);
                parcelGo.Remove(id); parcelTpl.Remove(id); settled.Remove(id);
            }
            foreach (int id in Sim.alive)
            {
                var p = Sim.parcels[id];
                if (!parcelGo.TryGetValue(id, out var go))
                {
                    int tpl = TemplateIndex(p);
                    go = pools[tpl].Count > 0 ? pools[tpl].Pop() : Instantiate(templates[tpl], parcelRoot);
                    go.SetActive(true); go.name = "Parcel_" + id;
                    parcelGo[id] = go; parcelTpl[id] = tpl;
                }
                else if (settled.Contains(id)) continue;   // 통 안에 멈춘 소포는 다시 계산하지 않음
                var q = Sim.ParcelPose(id);
                go.transform.SetPositionAndRotation(U(q.x, q.y, q.z), R(q.rx, q.ry, q.rz));
                if (p.state == PState.Bin) settled.Add(id);
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material deck, frame, steel, stainless, yellow, black, rubber, white, robot, tray, qr, qrw, lane,
                ledGreen, ledBlue, ledRed, screen, belt, bin, tag, tape, label, polyGrey, polyWhite, floor, green, wall, ceiling, beam, lightPanel, mat;
            public Material[] card;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                deck = Lit("DeckDark", Color.white, 0.2f, 0.5f);
                Noise(deck, new Color(0.06f, 0.064f, 0.068f), new Color(0.11f, 0.115f, 0.12f), 3f, 5);
                frame = Coat("FrameGrey", new Color(0.30f, 0.32f, 0.34f), 0.58f);
                steel = Lit("Galvanized", new Color(0.62f, 0.63f, 0.64f), 1f, 0.62f);
                Noise(steel, new Color(0.58f, 0.59f, 0.60f), new Color(0.68f, 0.69f, 0.70f), 6f, 4);
                stainless = Lit("Stainless", new Color(0.75f, 0.75f, 0.76f), 1f, 0.8f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                white = Coat("WhitePlastic", new Color(0.85f, 0.86f, 0.87f), 0.65f);
                robot = Coat("RobotYellow", new Color(0.95f, 0.60f, 0.02f), 0.7f);
                tray = Lit("TrayGrey", new Color(0.18f, 0.19f, 0.2f), 0.6f, 0.6f);
                qr = Lit("QRMarker", new Color(0.01f, 0.01f, 0.01f), 0f, 0.4f);
                qrw = Lit("QRWhite", new Color(0.8f, 0.8f, 0.8f), 0f, 0.4f);
                lane = Lit("LaneWhite", new Color(0.75f, 0.76f, 0.76f), 0f, 0.4f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 5f);
                ledBlue = Emissive("LEDBlue", new Color(0.2f, 0.5f, 1f), 5f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 5f);
                screen = Emissive("Screen", new Color(0.15f, 0.45f, 0.9f), 1.5f);
                belt = Lit("ConveyorBelt", new Color(0.03f, 0.03f, 0.035f), 0f, 0.3f);
                bin = Lit("BinBeige", Color.white, 0f, 0.42f);
                Noise(bin, new Color(0.68f, 0.59f, 0.44f), new Color(0.74f, 0.65f, 0.50f), 20f, 3);
                tag = Lit("BinTag", new Color(0.92f, 0.92f, 0.9f), 0f, 0.5f);
                card = new[]
                {
                    Lit("Cardboard", Color.white, 0f, 0.25f), Lit("CardboardDark", Color.white, 0f, 0.25f), Lit("CardboardWhite", Color.white, 0f, 0.25f),
                };
                Noise(card[0], new Color(0.47f, 0.32f, 0.17f), new Color(0.56f, 0.40f, 0.23f), 40f, 3);
                Noise(card[1], new Color(0.37f, 0.24f, 0.12f), new Color(0.46f, 0.31f, 0.17f), 40f, 3);
                Noise(card[2], new Color(0.72f, 0.68f, 0.62f), new Color(0.82f, 0.78f, 0.72f), 40f, 3);
                tape = Lit("PackingTape", new Color(0.55f, 0.42f, 0.22f), 0f, 0.8f);
                label = Lit("ShippingLabel", new Color(0.92f, 0.92f, 0.9f), 0f, 0.45f);
                polyGrey = Lit("PolyGrey", new Color(0.18f, 0.19f, 0.2f), 0f, 0.72f);
                polyWhite = Lit("PolyWhite", new Color(0.82f, 0.83f, 0.84f), 0f, 0.65f);
                floor = Epoxy();
                green = Lit("SafetyGreen", new Color(0.1f, 0.55f, 0.25f), 0f, 0.55f);
                wall = Lit("WallWhite", new Color(0.62f, 0.64f, 0.66f), 0f, 0.4f);
                ceiling = Lit("Ceiling", new Color(0.55f, 0.56f, 0.58f), 0f, 0.2f);
                beam = Lit("SteelBeam", new Color(0.70f, 0.71f, 0.72f), 0.6f, 0.6f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.98f, 0.95f), 6f);
                mat = Lit("FloorMat", new Color(0.06f, 0.06f, 0.065f), 0f, 0.1f);
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
                        float g = Mathf.Lerp(0.40f, 0.52f, Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, new Color(g, g * 1.005f, g * 1.01f, 1));
                        float s = Mathf.Lerp(0.65f, 0.88f, Mathf.PerlinNoise(x * 0.02f + 5f, y * 0.02f));
                        mg.SetPixel(x, y, new Color(0, 0, 0, s));
                    }
                alb.Apply(); mg.Apply();
                alb.wrapMode = mg.wrapMode = TextureWrapMode.Repeat;
                var m = Lit("EpoxyFloor", Color.white, 0f, 1f);
                m.SetTexture("_BaseMap", alb); m.SetTextureScale("_BaseMap", new Vector2(6, 6));
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
            public void Box(Vector3 size, Vector3 center, Material m) => Add(cube, Matrix4x4.TRS(center, Quaternion.identity, size), m);
            public void Cyl(float r, float len, Vector3 center, Quaternion rot, Material m) =>
                Add(cyl, Matrix4x4.TRS(center, rot, new Vector3(2 * r, len / 2, 2 * r)), m);
            public void Add(Mesh mesh, Matrix4x4 mtx, Material m, int sub = 0)
            {
                if (!parts.TryGetValue(m, out var l)) { l = new List<CombineInstance>(); parts[m] = l; }
                l.Add(new CombineInstance { mesh = mesh, transform = mtx, subMeshIndex = sub });
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

        // ------------------------------------------------------------------ 건물 (밝은 물류센터)
        void BuildEnvironment()
        {
            float cxw = XE / 2, cyw = NY * Cell / 2;
            var env = new GameObject("Building").transform;
            const float S = 60f, Hh = 11f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            float ny = NY * Cell;
            B(b, XE + 4.6f, 0.10f, 0.004f, cxw - 0.8f, -0.9f, 0.002f, M.yellow);
            B(b, XE + 4.6f, 0.10f, 0.004f, cxw - 0.8f, ny + 1.4f, 0.002f, M.yellow);
            B(b, 0.10f, ny + 2.3f, 0.004f, -3.1f, cyw + 0.25f, 0.002f, M.yellow);
            B(b, 0.10f, ny + 2.3f, 0.004f, XE + 1.5f, cyw + 0.25f, 0.002f, M.yellow);
            B(b, S * 0.8f, 0.14f, 0.004f, cxw, -3.4f, 0.002f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 17, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 18, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 20, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 20, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -18; k < 20; k += 4) B(b, 0.3f, S, 0.6f, cxw + k, cyw, Hh - 0.4f, M.beam);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 16.8f, 1.2f, M.green);
            for (int k = -16; k < 20; k += 8) B(b, 0.4f, 0.4f, Hh, cxw + k, cyw + 16.7f, Hh / 2, M.beam);
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
                    l.range = 30; l.intensity = 24f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.0f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f; sun.shadowBias = 0.02f; sun.shadowNormalBias = 0.3f;
            sun.transform.rotation = Quaternion.Euler(58, -30, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.80f, 0.83f);
            RenderSettings.ambientEquatorColor = new Color(0.62f, 0.63f, 0.65f);
            RenderSettings.ambientGroundColor = new Color(0.40f, 0.40f, 0.41f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 4f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 플랫폼 (blender make_platform 과 같은 형상)
        void BuildPlatform()
        {
            var c = config; float cell = Cell, z = Z; int ny = NY;
            var root = new GameObject("Platform").transform;
            var d = new Batch(cubeMesh, cylMesh);
            B(d, MW, ny * cell, DeckT, MW / 2, ny * cell / 2, z - DeckT / 2, M.deck);
            B(d, MW, cell - 0.002f, DeckT, MW / 2, ny * cell + cell / 2 + 0.001f, z - DeckT / 2, M.deck);
            foreach (int s in c.stations) B(d, cell - 0.002f, 2 * cell, DeckT, -cell / 2 - 0.001f, Cy(s) + cell / 2, z - DeckT / 2, M.deck);
            for (int k = 0; k < c.fingers; k++)
            {
                float y0 = (4 * k + 1) * cell;
                B(d, XE - MW - 0.002f, 2 * cell, DeckT, (MW + XE) / 2 + 0.001f, y0 + cell, z - DeckT / 2, M.deck);
            }
            d.Build("Deck", root);
            // 데크 위 표시 (그림자 없음, 1.5 mm 이상 띄움)
            var q = new Batch(cubeMesh, cylMesh); float zq = z + 0.0015f;
            for (int j = 0; j < ny; j++)
                for (int i = -1; i <= Tip; i++)
                {
                    int m4 = j & 3; bool lane = (i >= 0 && i < c.mainX) || (i >= c.mainX && (m4 == 1 || m4 == 2));
                    bool st = false; if (i == -1) foreach (int s in c.stations) if (j == s || j == s + 1) st = true;
                    if (!lane && !st) continue;
                    B(q, 0.09f, 0.09f, 0.001f, Cx(i), Cy(j), zq, M.qrw);
                    B(q, 0.065f, 0.065f, 0.001f, Cx(i), Cy(j), zq + 0.0015f, M.qr);
                }
            for (int k = 0; k < c.fingers; k++)
            {
                float yb = (4 * k + 2) * cell;
                for (float x = MW + 0.25f; x < XE - 0.2f; x += 0.6f) B(q, 0.28f, 0.025f, 0.001f, x + 0.14f, yb, zq, M.lane);
                B(q, 0.05f, 2 * cell - 0.06f, 0.001f, MW + 0.06f, yb, zq, M.yellow);
            }
            q.Build("DeckMarks", root, false);
            // 옆면 띠 + 끝 범퍼
            var e = new Batch(cubeMesh, cylMesh);
            for (int k = 0; k < c.fingers; k++)
            {
                B(e, XE - MW, 0.012f, 0.07f, (MW + XE) / 2, (4 * k + 1) * cell - 0.007f, z - 0.035f, M.yellow);
                B(e, XE - MW, 0.012f, 0.07f, (MW + XE) / 2, (4 * k + 3) * cell + 0.007f, z - 0.035f, M.yellow);
                B(e, 0.04f, 2 * cell + 0.03f, 0.10f, XE + 0.02f, (4 * k + 2) * cell, z - 0.02f, M.black);
            }
            B(e, MW, 0.012f, 0.07f, MW / 2, -0.007f, z - 0.035f, M.yellow);
            B(e, MW, 0.012f, 0.07f, MW / 2, (ny + 1) * cell + 0.007f, z - 0.035f, M.yellow);
            e.Build("EdgeTrim", root);
            // 하부 구조
            var s2 = new Batch(cubeMesh, cylMesh); float zb = z - DeckT;
            for (int k = 0; k < c.fingers; k++)
            {
                float yc = (4 * k + 2) * cell;
                for (int sg = -1; sg <= 1; sg += 2) B(s2, XE - MW, 0.05f, 0.10f, (MW + XE) / 2, yc + sg * 0.45f, zb - 0.05f, M.frame);
                for (float x = MW + 0.6f; x < XE; x += 1.2f)
                {
                    B(s2, 0.05f, 0.95f, 0.06f, x, yc, zb - 0.07f, M.frame);
                    B(s2, 0.07f, 0.07f, zb - 0.10f, x, yc, (zb - 0.10f) / 2, M.frame);
                    B(s2, 0.20f, 0.20f, 0.012f, x, yc, 0.006f, M.steel);
                }
            }
            for (int j = 0; j < ny + 2; j += 2)
            {
                float y = Mathf.Clamp(j * cell, 0.06f, (ny + 1) * cell - 0.06f);
                B(s2, MW, 0.05f, 0.10f, MW / 2, y, zb - 0.05f, M.frame);
                foreach (float x in new[] { 0.06f, MW / 2, MW - 0.06f })
                {
                    B(s2, 0.08f, 0.08f, zb - 0.10f, x, y, (zb - 0.10f) / 2, M.frame);
                    B(s2, 0.20f, 0.20f, 0.012f, x, y, 0.006f, M.steel);
                }
            }
            s2.Build("Substructure", root);
            // 슈트 칸막이
            var p = new Batch(cubeMesh, cylMesh); float hz = (z - DeckT - 0.01f) - ChuteZ0;
            for (int k = 0; k <= c.fingers; k++)
            {
                float yg = GapY(k);
                for (int side = -1; side <= 1; side += 2)
                {
                    if ((k == 0 && side < 0) || (k == c.fingers && side > 0)) continue;
                    float ye = yg + side * 0.6f;
                    for (int i = c.mainX; i <= Tip + 1; i++) B(p, 0.004f, 0.56f, hz, i * cell, (yg + ye) / 2, ChuteZ0 + hz / 2, M.stainless);
                }
                if (k > 0 && k < c.fingers) B(p, XE - MW, 0.004f, hz, (MW + XE) / 2, yg, ChuteZ0 + hz / 2, M.stainless);
            }
            p.Build("ChuteDividers", root);
            // 계단 (메인 데크 앞)
            var t = new Batch(cubeMesh, cylMesh); int n = 7; float rise = z / n, run = 0.27f;
            for (int s = 0; s < n - 1; s++) B(t, 0.9f, run, 0.03f, MW / 2, -run * (s + 0.5f) - 0.02f, z - (s + 1) * rise - 0.015f, M.steel);
            foreach (float xo in new[] { MW / 2 - 0.47f, MW / 2 + 0.47f }) B(t, 0.04f, (n - 1) * run, 0.12f, xo, -(n - 1) * run / 2 - 0.02f, z / 2 + 0.05f, M.frame);
            t.Build("Stair", root);
        }

        // ------------------------------------------------------------------ 슈트 통 (FBX 메쉬를 재질별로 합침)
        void BuildBins()
        {
            var root = new GameObject("Bins").transform;
            var b = new Batch(cubeMesh, cylMesh);
            var prefab = Resources.Load<GameObject>("TSorterModels/tsorter_bin");
            var parts = new List<(Mesh mesh, int sub, Material mat, Matrix4x4 local)>();
            if (prefab)
            {
                var inst = Instantiate(prefab); M.Remap(inst);
                inst.transform.position = Vector3.zero; inst.transform.rotation = Quaternion.identity;
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
                {
                    var mr = mf.GetComponent<MeshRenderer>(); if (!mr || !mf.sharedMesh) continue;
                    for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                        parts.Add((mf.sharedMesh, s, mr.sharedMaterials[Mathf.Min(s, mr.sharedMaterials.Length - 1)], mf.transform.localToWorldMatrix));
                }
                Destroy(inst);
            }
            var rnd = new System.Random(11);
            foreach (var ch in Sim.chutes)
            {
                float x = Cx(ch.i) + ((float)rnd.NextDouble() - 0.5f) * 0.02f, y = Cy(ch.j);
                var mtx = Matrix4x4.TRS(U(x, y, 0), Quaternion.Euler(0, ((float)rnd.NextDouble() - 0.5f) * 0.04f * Mathf.Rad2Deg, 0), Vector3.one);
                if (parts.Count > 0) foreach (var pt in parts) b.Add(pt.mesh, mtx * pt.local, pt.mat, pt.sub);
                else
                {
                    float h = BinTop - BinZ0, w = BinW, th = 0.006f;
                    B(b, w, w, th, x, y, BinZ0 + th / 2, M.bin);
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        B(b, w, th, h, x, y + sg * (w / 2 - th / 2), BinZ0 + h / 2, M.bin);
                        B(b, th, w, h, x + sg * (w / 2 - th / 2), y, BinZ0 + h / 2, M.bin);
                        B(b, 0.14f, 0.003f, 0.08f, x + 0.12f, y + sg * (w / 2 + 0.004f), BinTop - 0.12f, M.tag);
                    }
                    B(b, w + 0.02f, w + 0.02f, 0.025f, x, y, 0.085f, M.frame);
                }
            }
            b.Build("BinMeshes", root);
        }

        // ------------------------------------------------------------------ 투입 스테이션 + 충전 파일
        void BuildStations()
        {
            var b = new Batch(cubeMesh, cylMesh); float z = Z;
            foreach (int s in config.stations)
            {
                float y = Cy(s + 1), xt = Cx(-2) + 0.08f;
                B(b, 0.46f, 0.56f, 0.04f, xt, y, z + 0.10f, M.stainless);
                for (int a = -1; a <= 1; a += 2)
                    for (int e = -1; e <= 1; e += 2) B(b, 0.04f, 0.04f, z + 0.08f, xt + a * 0.19f, y + e * 0.24f, (z + 0.08f) / 2, M.steel);
                float Lc = 1.9f, xc0 = xt - 0.23f;
                B(b, Lc, 0.46f, 0.03f, xc0 - Lc / 2, y, z + 0.105f, M.belt);
                for (int e = -1; e <= 1; e += 2) B(b, Lc, 0.04f, 0.09f, xc0 - Lc / 2, y + e * 0.25f, z + 0.09f, M.steel);
                for (int k = 0; k < 3; k++)
                    for (int e = -1; e <= 1; e += 2) B(b, 0.05f, 0.05f, z + 0.05f, xc0 - 0.3f - k * 0.7f, y + e * 0.23f, (z + 0.05f) / 2, M.steel);
                float xa = xc0 - 0.55f;
                for (int e = -1; e <= 1; e += 2) B(b, 0.06f, 0.05f, 0.65f, xa, y + e * 0.31f, z + 0.12f + 0.325f, M.white);
                B(b, 0.12f, 0.68f, 0.08f, xa, y, z + 0.80f, M.white);
                B(b, 0.02f, 0.38f, 0.008f, xa, y, z + 0.757f, M.ledRed);
                B(b, 0.04f, 0.04f, 1.30f, xt - 0.05f, y + 0.42f, 0.65f + z - 0.45f, M.steel);
                B(b, 0.03f, 0.34f, 0.22f, xt - 0.05f, y + 0.42f, z + 0.85f, M.black);
                B(b, 0.004f, 0.30f, 0.18f, xt - 0.068f, y + 0.42f, z + 0.85f, M.screen);
                B(b, 0.9f, 1.1f, 0.012f, xt - 0.75f, y + 0.15f, 0.006f, M.mat);
                Cy(b, 0.028f, 0.05f, Cx(-1), Cy(s) - 0.24f, z + 0.025f, 'Z', M.ledGreen);
            }
            float yc = (NY + 0.5f) * Cell;
            for (int i = 0; i < config.mainX; i++)
            {
                B(b, 0.30f, 0.10f, 0.32f, Cx(i), yc + 0.2f, z + 0.16f, M.white);
                B(b, 0.22f, 0.004f, 0.012f, Cx(i), yc + 0.148f, z + 0.26f, M.ledBlue);
                B(b, 0.10f, 0.04f, 0.02f, Cx(i), yc + 0.13f, z + 0.05f, M.stainless);
            }
            b.Build("Stations", new GameObject("StationsAndChargers").transform);
        }

        // ------------------------------------------------------------------ 로봇
        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            var prefab = Resources.Load<GameObject>("TSorterModels/tsorter_robot");
            foreach (var r in Sim.robots)
            {
                var root = new GameObject("Robot_" + r.id).transform; root.SetParent(parent, false);
                Transform tray = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    // 앞 방향 확인: 'Pivot' 기둥 두 개가 앞뒤(X)로 놓여 있어야 함
                    Transform piv = null;
                    foreach (var t in go.GetComponentsInChildren<Transform>()) { if (t.name.Contains("Pivot")) piv = t; if (t.name.StartsWith("Tray")) tray = t; }
                    var pr = piv ? piv.GetComponent<Renderer>() : null;
                    if (pr && pr.bounds.size.z > pr.bounds.size.x) go.transform.localRotation = Quaternion.Euler(0, 90, 0) * go.transform.localRotation;
                    var rs = go.GetComponentsInChildren<Renderer>(); var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
                    float sc = RobotL / Mathf.Max(1e-4f, Mathf.Max(bb.size.x, bb.size.z));
                    if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                }
                else
                {
                    Cube("Body", new Vector3(RobotL, RobotH - 0.035f, RobotW), new Vector3(0, 0.03f + (RobotH - 0.035f) / 2, 0), M.robot, root);
                    Cube("Skirt", new Vector3(RobotL - 0.04f, 0.03f, RobotW - 0.04f), new Vector3(0, 0.022f, 0), M.black, root);
                    Cube("LEDf", new Vector3(0.004f, 0.010f, 0.18f), new Vector3(RobotL / 2 + 0.002f, RobotH - 0.03f, 0), M.ledGreen, root, false);
                    Cube("LEDb", new Vector3(0.004f, 0.010f, 0.18f), new Vector3(-RobotL / 2 - 0.002f, RobotH - 0.03f, 0), M.ledBlue, root, false);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Pivot", new Vector3(0.04f, TrayZ - RobotH - 0.012f, 0.05f), new Vector3(sx * (RobotL / 2 - 0.07f), RobotH + (TrayZ - RobotH - 0.012f) / 2, 0), M.tray, root);
                    tray = new GameObject("Tray").transform; tray.SetParent(root, false); tray.localPosition = new Vector3(0, TrayZ, 0);
                    Cube("Plate", new Vector3(RobotL - 0.02f, 0.016f, RobotW + 0.04f), new Vector3(0, -0.008f, 0), M.tray, tray);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Lip", new Vector3(0.014f, 0.045f, RobotW + 0.04f), new Vector3(sx * (RobotL / 2 - 0.017f), 0.0225f, 0), M.tray, tray);
                }
                robotT.Add(root); trayT.Add(tray);
                trayRest.Add(tray ? Quaternion.Inverse(root.rotation) * tray.rotation : Quaternion.identity);
            }
        }

        // ------------------------------------------------------------------ 소포 템플릿
        int TemplateIndex(Parcel p)
        {
            if (p.type <= 2) return p.type * 3 + (p.tint < 0.55 ? 0 : p.tint < 0.85 ? 1 : 2);
            return 9 + (p.type - 3);
        }

        void BuildParcelTemplates()
        {
            parcelRoot = new GameObject("Parcels").transform;
            var holder = new GameObject("ParcelTemplates").transform; holder.gameObject.SetActive(false);
            for (int type = 0; type < 5; type++)
            {
                float sx = (float)TSorterSim.ParcelSize[type, 0], sy = (float)TSorterSim.ParcelSize[type, 1], sz = (float)TSorterSim.ParcelSize[type, 2];
                int variants = type <= 2 ? 3 : 1;
                for (int v = 0; v < variants; v++)
                {
                    var go = new GameObject("ParcelTpl_" + TSorterSim.ParcelName[type] + v); go.transform.SetParent(holder, false);
                    if (type <= 2)
                    {
                        Cube("Box", new Vector3(sx, sz, sy), Vector3.zero, M.card[v], go.transform);
                        Cube("Tape", new Vector3(sx + 0.003f, 0.0015f, 0.05f), new Vector3(0, sz / 2 + 0.0009f, 0), M.tape, go.transform, false);
                        Cube("Label", new Vector3(sx * 0.35f, 0.0012f, sy * 0.4f), new Vector3(-sx * 0.2f, sz / 2 + 0.0024f, -sy * 0.15f), M.label, go.transform, false);
                    }
                    else
                    {
                        var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = PillowMesh(sx, sy, sz);
                        go.AddComponent<MeshRenderer>().sharedMaterial = type == 3 ? M.polyGrey : M.polyWhite;
                        Cube("Label", new Vector3(sx * 0.4f, 0.0012f, sy * 0.35f), new Vector3(sx * 0.1f, sz / 2 * 0.97f + 0.002f, 0), M.label, go.transform, false);
                    }
                    templates.Add(go); pools.Add(new Stack<GameObject>());
                }
            }
        }

        // 비닐 봉투: 가장자리로 갈수록 얇아지는 베개 형태
        static Mesh PillowMesh(float sx, float sy, float sz)
        {
            const int N = 8;
            var v = new List<Vector3>(); var tri = new List<int>();
            for (int face = 0; face < 2; face++)
            {
                int o = v.Count; float sgn = face == 0 ? 1 : -1;
                for (int a = 0; a <= N; a++)
                    for (int b = 0; b <= N; b++)
                    {
                        float x = -1 + 2f * a / N, y = -1 + 2f * b / N;
                        float pinch = 1 - Mathf.Pow(Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)), 3);
                        v.Add(new Vector3(x * sx / 2, sgn * sz / 2 * Mathf.Max(0.04f, pinch), y * sy / 2));
                    }
                for (int a = 0; a < N; a++)
                    for (int b = 0; b < N; b++)
                    {
                        int i0 = o + a * (N + 1) + b, i1 = i0 + 1, i2 = i0 + N + 1, i3 = i2 + 1;
                        if (face == 0) tri.AddRange(new[] { i0, i1, i2, i1, i3, i2 });
                        else tri.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
                    }
            }
            var m = new Mesh { name = "Pillow" };
            m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ 카메라 + 후처리
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.72f, 0.74f, 0.77f);
            Cam.nearClipPlane = 0.05f; Cam.farClipPlane = 120f;
            Cam.cullingMask &= ~(1 << TSorterCaption.Layer);
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<TSorterCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<TSorterCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.35f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.35f); ca.contrast.Override(12f); ca.saturation.Override(4f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.2f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(6f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.25f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.06f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || TSorterMovieCapture.IsRecording) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 400, 210), GUI.skin.box);
            GUILayout.Label($"<b>Libiao T-Sort (finger platform) simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Fingers {config.fingers} x {config.fingerLen}  Chutes {Sim.chutes.Count}  Robots {Sim.robots.Count}  Stations {Sim.stations.Count}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} parcels/h   Sorted {Sim.delivered}   Bin swaps {Sim.Swaps}");
            GUILayout.Label($"Blocked {Sim.BlockedShare:P0}   Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
