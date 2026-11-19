// Libiao 3D 소터: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/강철 플랫폼/슈트/자루/투입대/로봇/소포/조명/후처리/카메라를 코드로 만듭니다.
// 로봇 모델: Assets/Libiao/Resources/LibiaoModels/libiao_robot.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y)
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Libiao
{
    public class LibiaoView : MonoBehaviour
    {
        [Header("Simulation")]
        public SorterConfig config = new SorterConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초)")]
        public float warmupSeconds = 600f;
        [Range(0.1f, 10f)] public float simSpeed = 1f;
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public LibiaoSim Sim { get; private set; }
        public Camera Cam { get; private set; }

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float DeckT = 0.035f, Hole = 0.58f, BagTop = 2.08f, FunnelH = 0.22f;
        const float RobotL = 0.56f, RobotW = 0.50f, RobotH = 0.24f, TrayZ = 0.30f;

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
        Mesh cubeMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        // Blender 오일러 XYZ (R = Rz·Ry·Rx, 오른손) → Unity
        public static Quaternion R(double rx, double ry, double rz) =>
            Quaternion.AngleAxis((float)(-rz * Mathf.Rad2Deg), Vector3.up) *
            Quaternion.AngleAxis((float)(-ry * Mathf.Rad2Deg), Vector3.forward) *
            Quaternion.AngleAxis((float)(-rx * Mathf.Rad2Deg), Vector3.right);

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new LibiaoSim(config);
            for (double t = 0; t < warmupSeconds; t += subStep) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildPlatform();
            BuildSacks();
            BuildStations();
            BuildRobots();
            BuildParcelTemplates();
            BuildCameraAndPost();
            SyncAll();
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        void Update()
        {
            acc += Time.deltaTime * simSpeed;
            int guard = 0;
            while (acc >= subStep && guard++ < 2000) { Sim.Step(subStep); acc -= subStep; }
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
                else if (settled.Contains(id)) continue;   // 자루 안에 멈춘 소포는 다시 계산하지 않음
                var q = Sim.ParcelPose(id);
                go.transform.SetPositionAndRotation(U(q.x, q.y, q.z), R(q.rx, q.ry, q.rz));
                if (p.state == PState.Bag) settled.Add(id);
            }
        }

        // ------------------------------------------------------------------ 재질
        class Mats
        {
            public Material deck, frame, steel, funnel, stainless, yellow, black, rubber, white, robot, tray, qr, qrw,
                ledGreen, ledBlue, ledRed, screen, belt, tape, label, polyGrey, polyWhite, floor, wall, ceiling, lightPanel, ledStrip;
            public Material[] card, sack;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                deck = Lit("DeckSteel", Color.white, 0.35f, 0.55f);
                Noise(deck, new Color(0.24f, 0.255f, 0.27f), new Color(0.36f, 0.38f, 0.40f), 3f, 8);
                frame = Coat("FrameBlue", new Color(0.05f, 0.16f, 0.36f), 0.6f);
                steel = Lit("Galvanized", new Color(0.62f, 0.63f, 0.64f), 1f, 0.62f);
                funnel = Lit("FunnelSteel", new Color(0.62f, 0.63f, 0.64f), 1f, 0.55f); funnel.SetFloat("_Cull", 0);
                stainless = Lit("Stainless", new Color(0.75f, 0.75f, 0.76f), 1f, 0.8f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                white = Lit("WhitePlastic", new Color(0.85f, 0.86f, 0.87f), 0f, 0.65f);
                robot = Coat("RobotYellow", new Color(0.95f, 0.62f, 0.02f), 0.7f);
                tray = Lit("TrayGrey", new Color(0.18f, 0.19f, 0.2f), 0.6f, 0.6f);
                qr = Lit("QRMarker", new Color(0.01f, 0.01f, 0.01f), 0f, 0.4f);
                qrw = Lit("QRWhite", new Color(0.8f, 0.8f, 0.8f), 0f, 0.4f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 5f);
                ledBlue = Emissive("LEDBlue", new Color(0.2f, 0.5f, 1f), 4f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                screen = Emissive("Screen", new Color(0.15f, 0.45f, 0.9f), 1.5f);
                belt = Lit("ConveyorBelt", new Color(0.03f, 0.03f, 0.035f), 0f, 0.3f);
                card = new[]
                {
                    Lit("Cardboard", Color.white, 0f, 0.25f), Lit("CardboardDark", Color.white, 0f, 0.25f), Lit("CardboardWhite", Color.white, 0f, 0.25f),
                };
                Noise(card[0], new Color(0.47f, 0.32f, 0.17f), new Color(0.56f, 0.40f, 0.23f), 40f, 3);
                Noise(card[1], new Color(0.37f, 0.24f, 0.12f), new Color(0.46f, 0.31f, 0.17f), 40f, 3);
                Noise(card[2], new Color(0.72f, 0.68f, 0.62f), new Color(0.82f, 0.78f, 0.72f), 40f, 3);
                tape = Lit("PackingTape", new Color(0.55f, 0.42f, 0.22f), 0f, 0.85f);
                label = Lit("ShippingLabel", new Color(0.92f, 0.92f, 0.9f), 0f, 0.45f);
                polyGrey = Lit("PolyGrey", new Color(0.18f, 0.19f, 0.2f), 0f, 0.75f);
                polyWhite = Lit("PolyWhite", new Color(0.82f, 0.83f, 0.84f), 0f, 0.7f);
                sack = new[]
                {
                    Lit("SackWhite", new Color(0.80f, 0.80f, 0.78f), 0f, 0.3f), Lit("SackBlue", new Color(0.08f, 0.22f, 0.55f), 0f, 0.3f),
                    Lit("SackGreen", new Color(0.10f, 0.38f, 0.20f), 0f, 0.3f),
                };
                foreach (var s in sack) { s.SetFloat("_Cull", 0); Weave(s); }
                wall = Lit("WallPanel", new Color(0.45f, 0.48f, 0.52f), 0.2f, 0.45f);
                ceiling = Lit("Ceiling", new Color(0.22f, 0.22f, 0.24f), 0f, 0.2f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.97f, 0.92f), 6f);
                ledStrip = Emissive("LEDStrip", new Color(1f, 0.98f, 0.95f), 3f);
                floor = Concrete();
            }

            static Shader LitShader => Shader.Find("Universal Render Pipeline/Lit");

            Material Lit(string name, Color c, float metal, float smooth)
            {
                var m = new Material(LitShader) { name = name };
                m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
                byName[name] = m; return m;
            }

            Material Coat(string name, Color c, float smooth)
            {
                var sh = Shader.Find("Universal Render Pipeline/Complex Lit");
                if (sh == null) return Lit(name, c, 0f, smooth);
                var m = new Material(sh) { name = name };
                m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", 0f); m.SetFloat("_Smoothness", smooth);
                m.SetFloat("_ClearCoatMask", 0.7f); m.SetFloat("_ClearCoatSmoothness", 0.92f);
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

            // 직조 PP 자루: 격자 노멀맵
            static void Weave(Material m)
            {
                const int N = 64;
                var nrm = new Texture2D(N, N, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float a = Mathf.Sin(x * Mathf.PI * 2 / 8f), b = Mathf.Sin(y * Mathf.PI * 2 / 8f);
                        bool over = ((x / 4) + (y / 4)) % 2 == 0;
                        var n = new Vector3(over ? a * 0.35f : 0, over ? 0 : b * 0.35f, 1).normalized;
                        nrm.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, 1, 1));
                    }
                nrm.Apply();
                m.SetTexture("_BumpMap", nrm); m.SetTextureScale("_BumpMap", new Vector2(12, 24)); m.SetFloat("_BumpScale", 0.6f);
                m.EnableKeyword("_NORMALMAP");
            }

            // 폴리싱 콘크리트 (AutoStore 장면과 같은 방식)
            Material Concrete()
            {
                const int N = 512;
                var alb = new Texture2D(N, N, TextureFormat.RGBA32, true);
                var nrm = new Texture2D(N, N, TextureFormat.RGBA32, true, true);
                var mg = new Texture2D(N, N, TextureFormat.RGBA32, true, true);
                var h = new float[N * N];
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = 0, a = 0.5f, f = 4f / N;
                        for (int o = 0; o < 6; o++) { v += a * Mathf.PerlinNoise(x * f + 13.1f * o, y * f + 7.7f * o); a *= 0.5f; f *= 2f; }
                        h[y * N + x] = v;
                    }
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float v = h[y * N + x];
                        float g = Mathf.Lerp(0.19f, 0.34f, Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, new Color(g, g * 0.99f, g * 0.97f, 1));
                        float dx = (h[y * N + (x + 1) % N] - h[y * N + (x + N - 1) % N]) * 6f;
                        float dy = (h[((y + 1) % N) * N + x] - h[((y + N - 1) % N) * N + x]) * 6f;
                        var n = new Vector3(-dx, -dy, 1).normalized;
                        nrm.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, 1, 1));
                        float s = Mathf.Lerp(0.45f, 0.82f, Mathf.PerlinNoise(x * 0.03f, y * 0.03f));
                        mg.SetPixel(x, y, new Color(0, 0, 0, s));
                    }
                alb.Apply(); nrm.Apply(); mg.Apply();
                alb.wrapMode = nrm.wrapMode = mg.wrapMode = TextureWrapMode.Repeat;
                var m = Lit("PolishedConcrete", Color.white, 0f, 1f);
                m.SetTexture("_BaseMap", alb); m.SetTextureScale("_BaseMap", new Vector2(8, 8));
                m.SetTexture("_BumpMap", nrm); m.SetFloat("_BumpScale", 0.6f); m.EnableKeyword("_NORMALMAP");
                m.SetTexture("_MetallicGlossMap", mg); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                return m;
            }

            // FBX 에서 가져온 재질을 이름으로 URP 재질로 교체
            public void Remap(GameObject go)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    var arr = r.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] == null) continue;
                        foreach (var kv in byName)
                            if (arr[i].name.StartsWith(kv.Key)) { arr[i] = kv.Value; break; }
                    }
                    r.sharedMaterials = arr;
                }
            }
        }

        // ------------------------------------------------------------------ 메쉬 묶음 (재질별로 합쳐 드로우콜 절감)
        class Batch
        {
            readonly Dictionary<Material, List<CombineInstance>> parts = new Dictionary<Material, List<CombineInstance>>();
            readonly Mesh cube;
            public Batch(Mesh cube) { this.cube = cube; }
            public void Box(Vector3 size, Vector3 center, Material m) => Add(cube, Matrix4x4.TRS(center, Quaternion.identity, size), m);
            public void Box(Vector3 size, Vector3 center, Quaternion rot, Material m) => Add(cube, Matrix4x4.TRS(center, rot, size), m);
            public void Add(Mesh mesh, Matrix4x4 mtx, Material m)
            {
                if (!parts.TryGetValue(m, out var l)) { l = new List<CombineInstance>(); parts[m] = l; }
                l.Add(new CombineInstance { mesh = mesh, transform = mtx });
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

        // ------------------------------------------------------------------ 헬퍼
        float Cx(int i) => (float)Sim.Cx(i);
        float Cy(int j) => (float)Sim.Cy(j);
        // 시뮬레이션 좌표로 박스 추가 (크기 sx, sy, sz / 중심 x, y, z)
        static void B(Batch b, float sx, float sy, float sz, float x, float y, float z, Material m) =>
            b.Box(new Vector3(sx, sz, sy), new Vector3(x, z, y), m);

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

        // ------------------------------------------------------------------ 건물
        void BuildEnvironment()
        {
            var c = config;
            float W = c.nx * c.cell, cxw = W / 2, cyw = Cy(c.ny / 2);
            var env = new GameObject("Building").transform;
            const float S = 44f, Hh = 10f;
            var b = new Batch(cubeMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            float D = (c.ny + 6) * c.cell;
            B(b, W + 3.2f, 0.08f, 0.004f, cxw, Cy(-3) - 2.6f, 0.002f, M.yellow);
            B(b, W + 3.2f, 0.08f, 0.004f, cxw, Cy(c.ny + 2) + 2.6f, 0.002f, M.yellow);
            B(b, 0.08f, D + 5.2f, 0.004f, -1.6f, cyw, 0.002f, M.yellow);
            B(b, 0.08f, D + 5.2f, 0.004f, W + 2.4f, cyw, 0.002f, M.yellow);
            B(b, S, 0.2f, Hh, cxw, cyw + 14, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 15, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 17, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 17, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -14; k <= 14; k += 2) B(b, 0.06f, 0.08f, Hh, cxw + k, cyw + 13.88f, Hh / 2, M.frame);
            for (int ix = -2; ix <= 2; ix++)
                for (int iy = -2; iy <= 2; iy++)
                    B(b, 1.4f, 0.35f, 0.05f, cxw + ix * 4.5f, cyw + iy * 4.5f, Hh - 0.4f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 4.5f, cyw + iy * 4.5f, Hh - 0.5f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 130; l.innerSpotAngle = 60;
                    l.range = 30; l.intensity = 14f; l.color = new Color(1f, 0.95f, 0.88f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.0f; sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.85f;
            sun.transform.rotation = Quaternion.Euler(55, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.64f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.51f, 0.53f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.30f, 0.30f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 4f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 플랫폼
        bool IsStationCell(int i, int j) => Sim.IsStationCell(i, j);

        void BuildPlatform()
        {
            var c = config; float cell = c.cell, Z = c.deckZ, W = c.nx * cell;
            int jLo = -3, jHi = c.ny + 2;
            var root = new GameObject("Platform").transform;
            var deck = new Batch(cubeMesh);
            for (int j = jLo; j <= jHi; j++)
                for (int i = 0; i < c.nx; i++)
                {
                    float x = Cx(i), y = Cy(j);
                    if (Sim.InDeck(i, j) && !Sim.IsAisle(i, j))
                    {
                        float r = (cell - Hole) / 2;
                        B(deck, cell, r, DeckT, x, y + (cell - r) / 2, Z - DeckT / 2, M.deck);
                        B(deck, cell, r, DeckT, x, y - (cell - r) / 2, Z - DeckT / 2, M.deck);
                        B(deck, r, Hole, DeckT, x + (cell - r) / 2, y, Z - DeckT / 2, M.deck);
                        B(deck, r, Hole, DeckT, x - (cell - r) / 2, y, Z - DeckT / 2, M.deck);
                        // 노란 경고 립
                        B(deck, Hole + 0.02f, 0.01f, 0.006f, x, y + Hole / 2, Z + 0.003f, M.yellow);
                        B(deck, Hole + 0.02f, 0.01f, 0.006f, x, y - Hole / 2, Z + 0.003f, M.yellow);
                        B(deck, 0.01f, Hole, 0.006f, x + Hole / 2, y, Z + 0.003f, M.yellow);
                        B(deck, 0.01f, Hole, 0.006f, x - Hole / 2, y, Z + 0.003f, M.yellow);
                        deck.Add(FunnelMesh(), Matrix4x4.Translate(U(x, y, Z - DeckT)), M.funnel);
                    }
                    else
                    {
                        B(deck, cell, cell, DeckT, x, y, Z - DeckT / 2, M.deck);
                        if (Sim.IsAisle(i, j) || IsStationCell(i, j))
                        {
                            B(deck, 0.07f, 0.07f, 0.001f, x, y, Z + 0.0006f, M.qr);
                            B(deck, 0.09f, 0.09f, 0.0008f, x, y, Z + 0.0002f, M.qrw);
                        }
                    }
                }
            deck.Build("Deck", root);
            // 하부 구조
            var s = new Batch(cubeMesh);
            float y0 = Cy(jLo) - cell / 2, y1 = Cy(jHi) + cell / 2;
            for (int j = jLo; j <= jHi; j++)
                if (Sim.IsRow(j) || j == jLo || j == jHi) B(s, W, 0.10f, 0.16f, W / 2, Cy(j), Z - DeckT - 0.08f, M.frame);
            for (int i = 0; i < c.nx; i++)
                if (Sim.IsCol(i) || i == 0 || i == c.nx - 1) B(s, 0.08f, y1 - y0, 0.12f, Cx(i), (y0 + y1) / 2, Z - DeckT - 0.06f, M.frame);
            int mid = c.aisleStep * (((c.ny - 1) / c.aisleStep) / 2);   // 가운데 통로 행은 기둥 없음 (점검 통로)
            var rows = new List<int> { jLo, jHi };
            for (int j = 0; j < c.ny; j += c.aisleStep) if (j != mid) rows.Add(j);
            foreach (int j in rows)
                for (int i = 0; i < c.nx; i += c.aisleStep)
                {
                    B(s, 0.12f, 0.12f, Z - DeckT, Cx(i), Cy(j), (Z - DeckT) / 2, M.frame);
                    B(s, 0.26f, 0.26f, 0.015f, Cx(i), Cy(j), 0.0075f, M.steel);
                }
            // 난간
            foreach (float z in new[] { Z + 0.5f, Z + 1.05f })
            {
                B(s, 0.04f, y1 - y0, 0.04f, -0.05f, (y0 + y1) / 2, z, M.yellow);
                B(s, 0.04f, y1 - y0, 0.04f, W + 0.05f, (y0 + y1) / 2, z, M.yellow);
            }
            B(s, 0.01f, y1 - y0, 0.12f, -0.05f, (y0 + y1) / 2, Z + 0.06f, M.yellow);
            B(s, 0.01f, y1 - y0, 0.12f, W + 0.05f, (y0 + y1) / 2, Z + 0.06f, M.yellow);
            for (float k = y0; k <= y1 + 1e-4f; k += 1.4f)
            {
                B(s, 0.04f, 0.04f, 1.05f, -0.05f, k, Z + 0.525f, M.yellow);
                B(s, 0.04f, 0.04f, 1.05f, W + 0.05f, k, Z + 0.525f, M.yellow);
            }
            // 계단 (오른쪽 앞)
            int n = 14; float rise = Z / n, run = 0.26f, L = n * run;
            for (int k = 0; k < n; k++) B(s, 1.0f, run, 0.03f, W + 0.65f, y0 + 0.6f + k * run, (k + 1) * rise - 0.015f, M.steel);
            foreach (float xo in new[] { W + 0.13f, W + 1.17f })
                s.Box(new Vector3(0.05f, 0.18f, Mathf.Sqrt(L * L + Z * Z)), U(xo, y0 + 0.6f + L / 2, Z / 2),
                      Quaternion.AngleAxis(-Mathf.Atan2(Z, L) * Mathf.Rad2Deg, Vector3.right), M.frame);
            s.Build("Structure", root);
            // 데크 하부 LED 바 + 조명
            var led = new Batch(cubeMesh);
            for (int j = 0; j < c.ny; j += c.aisleStep)
            {
                B(led, W - 0.4f, 0.05f, 0.02f, W / 2, Cy(j) + 0.12f, Z - DeckT - 0.17f, M.ledStrip);
                for (int k = 0; k < 4; k++)
                {
                    var lg = new GameObject("UnderDeckLight"); lg.transform.SetParent(root);
                    lg.transform.position = U(W * (k + 0.5f) / 4, Cy(j) + 0.12f, Z - DeckT - 0.22f);
                    lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 150; l.innerSpotAngle = 90;
                    l.range = 5f; l.intensity = 3f; l.color = new Color(1f, 0.97f, 0.92f); l.shadows = LightShadows.None;
                }
            }
            led.Build("UnderDeckLEDs", root, false);
        }

        Mesh funnelMesh;
        // 슈트 깔때기 (원점 = 구멍 중심, 데크 하면). 양면 재질 사용
        Mesh FunnelMesh()
        {
            if (funnelMesh) return funnelMesh;
            float top = Hole / 2, bot = 0.25f, h = FunnelH;
            var v = new List<Vector3>(); var tri = new List<int>();
            Vector2[] cs = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            for (int k = 0; k < 4; k++)
            {
                var a = cs[k]; var b2 = cs[(k + 1) % 4];
                int o = v.Count;
                v.Add(new Vector3(a.x * top, 0, a.y * top)); v.Add(new Vector3(b2.x * top, 0, b2.y * top));
                v.Add(new Vector3(b2.x * bot, -h, b2.y * bot)); v.Add(new Vector3(a.x * bot, -h, a.y * bot));
                tri.AddRange(new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
            }
            funnelMesh = new Mesh { name = "Funnel" };
            funnelMesh.SetVertices(v); funnelMesh.SetTriangles(tri, 0); funnelMesh.RecalculateNormals(); funnelMesh.RecalculateBounds();
            return funnelMesh;
        }

        // ------------------------------------------------------------------ 자루
        Mesh SackMesh(float half, float bot, float top, int seed)
        {
            const int per = 6, rings = 10;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            var rnd = new System.Random(seed); float ph = (float)rnd.NextDouble() * 6;
            int ringN = per * 4;
            for (int r = 0; r <= rings; r++)
            {
                float u = r / (float)rings;
                float yy = bot + u * (top - bot);
                for (int k = 0; k < ringN; k++)
                {
                    int side = k / per; float f = (k % per) / (float)per;
                    Vector2 p = side == 0 ? new Vector2(-1 + 2 * f, -1) : side == 1 ? new Vector2(1, -1 + 2 * f) : side == 2 ? new Vector2(1 - 2 * f, 1) : new Vector2(-1, 1 - 2 * f);
                    float bulge = 1f + 0.10f * Mathf.Sin(Mathf.PI * Mathf.Min(1, (1 - u) * 1.2f + 0.0f)) + 0.02f * Mathf.Sin(p.x * 8 + p.y * 6 + ph + u * 9);
                    v.Add(new Vector3(p.x * half * bulge, yy, p.y * half * bulge));
                    uv.Add(new Vector2(k / (float)ringN * 4, u * 2));
                }
            }
            for (int r = 0; r < rings; r++)
                for (int k = 0; k < ringN; k++)
                {
                    int a = r * ringN + k, b2 = r * ringN + (k + 1) % ringN, c2 = a + ringN, d = b2 + ringN;
                    tri.AddRange(new[] { a, c2, b2, b2, c2, d });
                }
            int center = v.Count; v.Add(new Vector3(0, bot - 0.02f, 0)); uv.Add(new Vector2(0.5f, 0));
            for (int k = 0; k < ringN; k++) tri.AddRange(new[] { center, k, (k + 1) % ringN });
            var m = new Mesh { name = "Sack" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        void BuildSacks()
        {
            var c = config; float Z = c.deckZ, s = 0.29f;
            var b = new Batch(cubeMesh);
            var rnd = new System.Random(11);
            var sackMeshes = new Mesh[4];
            for (int k = 0; k < 4; k++) sackMeshes[k] = SackMesh(s - 0.01f, c.bagBottom - 0.02f, BagTop, k);
            float hz = Z - DeckT - BagTop;
            foreach (var ch in Sim.chutes)
            {
                float x = Cx(ch.i), y = Cy(ch.j);
                B(b, 2 * s, 0.025f, 0.025f, x, y + s, BagTop, M.steel);
                B(b, 2 * s, 0.025f, 0.025f, x, y - s, BagTop, M.steel);
                B(b, 0.025f, 2 * s, 0.025f, x + s, y, BagTop, M.steel);
                B(b, 0.025f, 2 * s, 0.025f, x - s, y, BagTop, M.steel);
                for (int a = -1; a <= 1; a += 2)
                    for (int e = -1; e <= 1; e += 2)
                        B(b, 0.016f, 0.016f, hz, x + a * s, y + e * s, BagTop + hz / 2, M.steel);
                double u1 = rnd.NextDouble(), u2 = rnd.NextDouble();
                var mat = u1 < 0.6 ? M.sack[0] : (u2 < 0.6 ? M.sack[1] : M.sack[2]);
                b.Add(sackMeshes[ch.id % 4], Matrix4x4.TRS(U(x, y, 0), Quaternion.Euler(0, (ch.id % 2) * 90, 0), Vector3.one), mat);
                // 링에 걸친 입구 접힘
                B(b, 2 * s + 0.02f, 0.012f, 0.07f, x, y + s + 0.005f, BagTop - 0.03f, mat);
                B(b, 2 * s + 0.02f, 0.012f, 0.07f, x, y - s - 0.005f, BagTop - 0.03f, mat);
                B(b, 0.012f, 2 * s + 0.02f, 0.07f, x + s + 0.005f, y, BagTop - 0.03f, mat);
                B(b, 0.012f, 2 * s + 0.02f, 0.07f, x - s - 0.005f, y, BagTop - 0.03f, mat);
            }
            b.Build("Sacks", null);
        }

        // ------------------------------------------------------------------ 투입대
        void BuildStations()
        {
            var c = config; float Z = c.deckZ;
            var b = new Batch(cubeMesh);
            foreach (var st in Sim.stations)
            {
                float side = st.side, x = Cx(st.x), y = side < 0 ? Cy(-3) : Cy(c.ny + 2);
                B(b, 0.62f, 0.62f, 0.04f, x, y, Z + 0.18f, M.stainless);
                for (int a = -1; a <= 1; a += 2)
                    for (int e = -1; e <= 1; e += 2) B(b, 0.04f, 0.04f, 0.16f, x + a * 0.27f, y + e * 0.27f, Z + 0.08f, M.steel);
                float Lc = 1.9f, yc = y + side * (0.31f + Lc / 2);
                B(b, 0.5f, Lc, 0.03f, x, yc, Z + 0.185f, M.belt);
                for (int a = -1; a <= 1; a += 2) B(b, 0.04f, Lc, 0.09f, x + a * 0.27f, yc, Z + 0.17f, M.steel);
                for (int k = 0; k < 3; k++)
                    for (int a = -1; a <= 1; a += 2) B(b, 0.05f, 0.05f, Z + 0.15f, x + a * 0.25f, y + side * (0.5f + k * 0.75f), (Z + 0.15f) / 2, M.steel);
                for (int a = -1; a <= 1; a += 2) B(b, 0.05f, 0.06f, 0.75f, x + a * 0.32f, y + side * 0.75f, Z + 0.575f, M.white);
                B(b, 0.70f, 0.12f, 0.08f, x, y + side * 0.75f, Z + 0.95f, M.white);
                B(b, 0.4f, 0.02f, 0.01f, x, y + side * 0.75f, Z + 0.905f, M.ledRed);
                B(b, 0.04f, 0.04f, 1.3f, x + 0.48f, y + side * 0.1f, Z + 0.65f, M.steel);
                B(b, 0.36f, 0.03f, 0.24f, x + 0.48f, y + side * 0.1f, Z + 1.35f, M.black);
                B(b, 0.32f, 0.005f, 0.2f, x + 0.48f, y + side * 0.1f - side * 0.016f, Z + 1.35f, M.screen);
            }
            b.Build("Stations", null);
        }

        // ------------------------------------------------------------------ 로봇
        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            var prefab = Resources.Load<GameObject>("LibiaoModels/libiao_robot");
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
                    // 크기 보정 (단위 문제 대비)
                    var rs = go.GetComponentsInChildren<Renderer>(); var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
                    float sc = RobotL / Mathf.Max(1e-4f, Mathf.Max(bb.size.x, bb.size.z));
                    if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
                }
                else
                {
                    Cube("Body", new Vector3(RobotL, RobotH - 0.04f, RobotW), new Vector3(0, 0.03f + (RobotH - 0.04f) / 2, 0), M.robot, root);
                    Cube("Skirt", new Vector3(RobotL + 0.012f, 0.035f, RobotW + 0.012f), new Vector3(0, 0.038f, 0), M.black, root);
                    Cube("Lid", new Vector3(RobotL - 0.06f, 0.012f, RobotW - 0.06f), new Vector3(0, RobotH - 0.004f, 0), M.black, root);
                    Cube("LEDf", new Vector3(0.004f, 0.012f, 0.2f), new Vector3(RobotL / 2 + 0.001f, 0.19f, 0), M.ledGreen, root, false);
                    Cube("LEDb", new Vector3(0.004f, 0.012f, 0.2f), new Vector3(-RobotL / 2 - 0.001f, 0.19f, 0), M.ledBlue, root, false);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Pivot", new Vector3(0.05f, TrayZ - RobotH - 0.01f, 0.06f), new Vector3(sx * (RobotL / 2 - 0.08f), RobotH + (TrayZ - RobotH - 0.01f) / 2, 0), M.tray, root);
                    tray = new GameObject("Tray").transform; tray.SetParent(root, false); tray.localPosition = new Vector3(0, TrayZ, 0);
                    Cube("Plate", new Vector3(RobotL - 0.02f, 0.018f, RobotW + 0.02f), new Vector3(0, -0.009f, 0), M.tray, tray);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Lip", new Vector3(0.015f, 0.05f, RobotW + 0.02f), new Vector3(sx * (RobotL / 2 - 0.018f), 0.016f, 0), M.tray, tray);
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
                float sx = (float)LibiaoSim.ParcelSize[type, 0], sy = (float)LibiaoSim.ParcelSize[type, 1], sz = (float)LibiaoSim.ParcelSize[type, 2];
                int variants = type <= 2 ? 3 : 1;
                for (int v = 0; v < variants; v++)
                {
                    var go = new GameObject("ParcelTpl_" + LibiaoSim.ParcelName[type] + v); go.transform.SetParent(holder, false);
                    if (type <= 2)
                    {
                        Cube("Box", new Vector3(sx, sz, sy), Vector3.zero, M.card[v], go.transform);
                        Cube("Tape", new Vector3(sx + 0.002f, 0.0015f, 0.05f), new Vector3(0, sz / 2 + 0.0005f, 0), M.tape, go.transform, false);
                        Cube("Label", new Vector3(sx * 0.35f, 0.001f, sy * 0.4f), new Vector3(-sx * 0.2f, sz / 2 + 0.0008f, -sy * 0.15f), M.label, go.transform, false);
                    }
                    else
                    {
                        var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = PillowMesh(sx, sy, sz);
                        go.AddComponent<MeshRenderer>().sharedMaterial = type == 3 ? M.polyGrey : M.polyWhite;
                        Cube("Label", new Vector3(sx * 0.4f, 0.001f, sy * 0.35f), new Vector3(sx * 0.1f, sz / 2 * 0.95f, 0), M.label, go.transform, false);
                    }
                    templates.Add(go); pools.Add(new Stack<GameObject>());
                }
            }
        }

        // 비닐 봉투: 가장자리로 갈수록 얇아지는 베개 형태 (시뮬레이션 x→Unity x, y→z, z→y)
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
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
            Cam.nearClipPlane = 0.03f; Cam.farClipPlane = 200f;
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<LibiaoCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<LibiaoCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.5f); bl.threshold.Override(1.0f); bl.scatter.Override(0.65f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.8f); ca.contrast.Override(12f); ca.saturation.Override(-2f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.22f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(8f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.25f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.1f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-4f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (LibiaoMovieCapture.IsRecording && LibiaoMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 340, 230), GUI.skin.box);
            GUILayout.Label($"<b>Libiao 3D sorter simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Robots {Sim.robots.Count}  Chutes {Sim.chutes.Count}  Stations {Sim.stations.Count}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} parcels/h   Sorted {Sim.delivered}");
            GUILayout.Label($"Robot blocked {Sim.BlockedShare:P0}   Parcels in view {Sim.alive.Count}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0.1f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
