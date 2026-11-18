// AutoStore 시뮬레이션 실행 + 사실적 URP 장면 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/그리드/빈/로봇/포트/조명/후처리/카메라를 모두 코드로 만듭니다.
// 모델: Assets/AutoStore/Resources/Models/autostore_{robot,bin,port}.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AutoStore
{
    public class AutoStoreView : MonoBehaviour
    {
        [Header("Simulation")]
        public SimConfig config = new SimConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초)")]
        public float warmupSeconds = 900f;
        [Range(0.1f, 20f)] public float simSpeed = 1f;
        public float subStep = 0.02f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public AutoStoreSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        public Transform FocusTarget { get; private set; }

        readonly List<Transform> robotT = new List<Transform>();
        readonly List<Transform> gripT = new List<Transform>();
        readonly List<Vector3> gripRest = new List<Vector3>();
        Transform[] binT;
        int lastVersion = -1;
        Mats M;
        float topY;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new AutoStoreSim(config);
            for (double t = 0; t < warmupSeconds; t += 0.05) Sim.Step(0.05);
            topY = (float)Sim.topZ;
            M = new Mats();
            BuildEnvironment();
            BuildGrid();
            BuildPorts();
            BuildBins();
            BuildRobots();
            BuildCameraAndPost();
            SyncAll(true);
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        void Update()
        {
            float dt = Time.deltaTime * simSpeed;
            int n = Mathf.Max(1, Mathf.CeilToInt(dt / subStep));
            for (int i = 0; i < n; i++) Sim.Step(dt / n);
            SyncAll(false);
        }

        // ------------------------------------------------------------------ 동기화
        static Vector3 U(double x, double h, double y) => new Vector3((float)x, (float)h, (float)y);

        void SyncAll(bool force)
        {
            for (int i = 0; i < Sim.robots.Count; i++)
            {
                var r = Sim.robots[i];
                robotT[i].position = U(r.px, topY, r.py);
                if (gripT[i]) gripT[i].localPosition = gripRest[i] - gripT[i].parent.InverseTransformVector(Vector3.up * (float)r.g);
            }
            bool all = force || Sim.version != lastVersion;
            lastVersion = Sim.version;
            for (int id = 0; id < binT.Length; id++)
            {
                var k = Sim.binLoc[id].kind;
                if (!all && k != BinKind.Robot) continue;
                var p = Sim.BinPose(id);
                binT[id].position = U(p.x, p.h, p.y);
            }
        }

        // ------------------------------------------------------------------ 재질
        class Mats
        {
            public Material alu, rail, bin, binBlue, red, dark, rubber, steel, yellow, ledGreen, port, screen, floor, wall, ceiling, lightPanel;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                alu = Lit("Aluminium", new Color(0.86f, 0.87f, 0.88f), 1f, 0.72f);
                rail = Lit("RailAnodized", new Color(0.42f, 0.43f, 0.45f), 0.9f, 0.66f);
                bin = Lit("BinPlastic", new Color(0.105f, 0.11f, 0.12f), 0f, 0.55f);
                binBlue = Lit("BinPlasticBlue", new Color(0.03f, 0.14f, 0.42f), 0f, 0.58f);
                red = Coat("RobotPaintRed", new Color(0.62f, 0.015f, 0.02f), 0.68f);
                dark = Lit("RobotTopBlack", new Color(0.025f, 0.025f, 0.028f), 0f, 0.45f);
                rubber = Lit("Rubber", new Color(0.02f, 0.02f, 0.02f), 0f, 0.15f);
                steel = Lit("Steel", new Color(0.6f, 0.6f, 0.62f), 1f, 0.75f);
                yellow = Lit("SafetyYellow", new Color(0.9f, 0.62f, 0.02f), 0f, 0.5f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 6f);
                port = Lit("PowderCoatWhite", new Color(0.78f, 0.79f, 0.80f), 0f, 0.45f);
                screen = Emissive("Screen", new Color(0.15f, 0.45f, 0.9f), 1.5f);
                wall = Lit("WallPanel", new Color(0.30f, 0.33f, 0.37f), 0.3f, 0.5f);
                ceiling = Lit("Ceiling", new Color(0.08f, 0.08f, 0.09f), 0f, 0.2f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.97f, 0.92f), 6f);
                floor = Concrete();
                byName["Glass"] = port;
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
                m.SetFloat("_ClearCoatMask", 0.8f); m.SetFloat("_ClearCoatSmoothness", 0.94f);
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

            // 폴리싱 콘크리트: 절차적 알베도 + 노멀 + 매끄러움 맵
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

            // FBX에서 가져온 재질을 이름으로 URP 재질로 교체
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

        // ------------------------------------------------------------------ 헬퍼
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

        GameObject LoadModel(string res)
        {
            var prefab = Resources.Load<GameObject>("Models/" + res);
            if (!prefab) return null;
            var go = Instantiate(prefab);
            M.Remap(go);
            return go;
        }

        // FBX 축/단위가 달라도 기대 크기·방향에 맞춤
        static void Normalize(GameObject go, float expectX, float expectZ, bool frontNegZ)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            Bounds b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            if (expectX > 0)
            {
                bool swapped = Mathf.Abs(b.size.x - expectZ) < Mathf.Abs(b.size.x - expectX) && Mathf.Abs(expectX - expectZ) > 0.05f;
                if (swapped) { go.transform.Rotate(0, 90, 0, Space.World); }
                rs = go.GetComponentsInChildren<Renderer>(); b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                float s = expectX / Mathf.Max(1e-4f, b.size.x);
                if (s < 0.8f || s > 1.25f) go.transform.localScale *= s;
            }
            if (frontNegZ)
            {
                rs = go.GetComponentsInChildren<Renderer>(); b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                if (b.center.z > go.transform.position.z) go.transform.Rotate(0, 180, 0, Space.World);
            }
        }

        // ------------------------------------------------------------------ 환경
        void BuildEnvironment()
        {
            var c = config; float cx = c.nx * c.cellX / 2, cz = c.ny * c.cellY / 2;
            var env = new GameObject("Building").transform;
            const float S = 40f, Hh = 11f;
            Cube("Floor", new Vector3(S, 0.02f, S), new Vector3(cx, -0.01f, cz), M.floor, env);
            Cube("Line", new Vector3(c.nx * c.cellX + 1.6f, 0.004f, 0.08f), new Vector3(cx, 0.002f, -2f), M.yellow, env, false);
            Cube("Line", new Vector3(c.nx * c.cellX + 1.6f, 0.004f, 0.08f), new Vector3(cx, 0.002f, c.ny * c.cellY + 0.8f), M.yellow, env, false);
            Cube("Line", new Vector3(0.08f, 0.004f, c.ny * c.cellY + 2.8f), new Vector3(-0.8f, 0.002f, cz - 0.6f), M.yellow, env, false);
            Cube("Line", new Vector3(0.08f, 0.004f, c.ny * c.cellY + 2.8f), new Vector3(c.nx * c.cellX + 0.8f, 0.002f, cz - 0.6f), M.yellow, env, false);
            Cube("WallBack", new Vector3(S, Hh, 0.2f), new Vector3(cx, Hh / 2, cz + 12), M.wall, env);
            Cube("WallFront", new Vector3(S, Hh, 0.2f), new Vector3(cx, Hh / 2, cz - 16), M.wall, env);
            Cube("WallLeft", new Vector3(0.2f, Hh, S), new Vector3(cx - 14, Hh / 2, cz), M.wall, env);
            Cube("WallRight", new Vector3(0.2f, Hh, S), new Vector3(cx + 16, Hh / 2, cz), M.wall, env);
            Cube("Ceiling", new Vector3(S, 0.2f, S), new Vector3(cx, Hh, cz), M.ceiling, env);
            for (int k = -12; k <= 12; k += 2)
                Cube("WallRib", new Vector3(0.06f, Hh, 0.08f), new Vector3(cx + k, Hh / 2, cz + 11.88f), M.rail, env);
            int shadowed = 0;
            for (int ix = -2; ix <= 2; ix++)
                for (int iz = -2; iz <= 2; iz++)
                {
                    float x = cx + ix * 4.5f, z = cz + iz * 4.5f;
                    Cube("LightPanel", new Vector3(1.4f, 0.05f, 0.35f), new Vector3(x, Hh - 0.4f, z), M.lightPanel, env, false);
                    if (Mathf.Abs(ix) > 1 || Mathf.Abs(iz) > 1) continue;
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = new Vector3(x, Hh - 0.5f, z); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 130; l.innerSpotAngle = 60;
                    l.range = 25; l.intensity = 9f; l.color = new Color(1f, 0.95f, 0.88f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            // 주광 (천창 + 전체 그림자)
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f; sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.85f;
            sun.transform.rotation = Quaternion.Euler(55, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.44f, 0.48f);
            RenderSettings.ambientEquatorColor = new Color(0.30f, 0.31f, 0.33f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.16f);
            RenderSettings.skybox = null;
            // 반사 프로브 (금속 레일·도장면 반사)
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = new Vector3(cx, 4f, cz - 2f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        void BuildGrid()
        {
            var c = config; var g = new GameObject("Grid").transform;
            float H = topY, post = 0.045f, rh = 0.07f, W = c.nx * c.cellX, D = c.ny * c.cellY;
            for (int i = 0; i <= c.nx; i++)
                for (int j = 0; j <= c.ny; j++)
                    Cube("Post", new Vector3(post, H, post), new Vector3(i * c.cellX, H / 2, j * c.cellY), M.alu, g);
            for (int j = 0; j <= c.ny; j++)
                foreach (int s in new[] { -1, 1 })
                    Cube("RailX", new Vector3(W + post, rh, 0.022f), new Vector3(W / 2, H - rh / 2, j * c.cellY + s * 0.014f), M.rail, g);
            for (int i = 0; i <= c.nx; i++)
                foreach (int s in new[] { -1, 1 })
                    Cube("RailZ", new Vector3(0.022f, rh * 0.8f, D + post), new Vector3(i * c.cellX + s * 0.014f, H - rh * 0.4f, D / 2), M.rail, g);
            foreach (int px in c.ports)
            {
                float x0 = px * c.cellX;
                for (int sx = 0; sx <= 1; sx++)
                {
                    Cube("PortRail", new Vector3(0.044f, rh * 0.8f, c.cellY), new Vector3(x0 + sx * c.cellX, H - rh * 0.4f, -c.cellY / 2), M.rail, g);
                    Cube("PortPost", new Vector3(post, H, post), new Vector3(x0 + sx * c.cellX, H / 2, -c.cellY), M.alu, g);
                }
                Cube("PortRail", new Vector3(c.cellX, rh, 0.044f), new Vector3(x0 + c.cellX / 2, H - rh / 2, -c.cellY), M.rail, g);
            }
            foreach (float z in new[] { 0.15f, H * 0.5f })
            {
                Cube("Brace", new Vector3(W, 0.05f, 0.02f), new Vector3(W / 2, z, -0.01f), M.alu, g);
                Cube("Brace", new Vector3(W, 0.05f, 0.02f), new Vector3(W / 2, z, D + 0.01f), M.alu, g);
                Cube("Brace", new Vector3(0.02f, 0.05f, D), new Vector3(-0.01f, z, D / 2), M.alu, g);
                Cube("Brace", new Vector3(0.02f, 0.05f, D), new Vector3(W + 0.01f, z, D / 2), M.alu, g);
            }
            foreach (float z in new[] { H + 0.55f, H + 1.05f })
            {
                Cube("Handrail", new Vector3(W + 0.2f, 0.03f, 0.03f), new Vector3(W / 2, z, D + 0.1f), M.yellow, g);
                Cube("Handrail", new Vector3(0.03f, 0.03f, D + 0.2f), new Vector3(W + 0.1f, z, D / 2), M.yellow, g);
            }
            for (int i = 0; i <= c.nx; i += 3) Cube("HandPost", new Vector3(0.03f, 1.05f, 0.03f), new Vector3(i * c.cellX, H + 0.525f, D + 0.1f), M.yellow, g);
            for (int j = 0; j <= c.ny; j += 3) Cube("HandPost", new Vector3(0.03f, 1.05f, 0.03f), new Vector3(W + 0.1f, H + 0.525f, j * c.cellY), M.yellow, g);
        }

        void BuildPorts()
        {
            var parent = new GameObject("Ports").transform;
            foreach (var p in Sim.ports)
            {
                var pos = U(Sim.WorldX(p.x), 0, Sim.WorldY(-1));
                var go = LoadModel("autostore_port");
                if (go)
                {
                    go.transform.position = pos;
                    Normalize(go, 0, 0, true);
                }
                else
                {
                    go = new GameObject("Port");
                    go.transform.position = pos;
                    Cube("Deck", new Vector3(config.cellX, 0.04f, config.cellY), new Vector3(0, config.portZ - 0.03f, 0), M.port, go.transform);
                    Cube("Cabinet", new Vector3(config.cellX + 0.3f, 0.88f, 0.5f), new Vector3(0, 0.44f, -0.52f), M.port, go.transform);
                    Cube("Screen", new Vector3(0.42f, 0.27f, 0.025f), new Vector3(0, config.portZ + 0.78f, -0.30f), M.screen, go.transform);
                }
                go.name = "Port_" + p.id; go.transform.SetParent(parent, true);
            }
        }

        void BuildBins()
        {
            var parent = new GameObject("Bins").transform;
            var prefab = Resources.Load<GameObject>("Models/autostore_bin");
            binT = new Transform[Sim.bins];
            var rnd = new System.Random(7);
            for (int id = 0; id < Sim.bins; id++)
            {
                GameObject go;
                if (prefab)
                {
                    go = Instantiate(prefab, parent);
                    M.Remap(go);
                    if (id == 0) Normalize(go, (float)0.649, 0.449f, false);
                    else { go.transform.rotation = binT[0].rotation; go.transform.localScale = binT[0].localScale; }
                }
                else
                {
                    go = new GameObject("Bin");
                    go.transform.SetParent(parent);
                    Cube("Body", new Vector3(0.649f, 0.31f, 0.449f), new Vector3(0, 0.165f, 0), M.bin, go.transform);
                }
                if (rnd.NextDouble() < 0.05)
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterial = M.binBlue;
                go.name = "Bin_" + id; binT[id] = go.transform;
            }
        }

        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            foreach (var r in Sim.robots)
            {
                var go = LoadModel("autostore_robot");
                if (go) { go.transform.position = Vector3.zero; Normalize(go, 0.7f, 0.5f, false); }
                else
                {
                    go = new GameObject("Robot");
                    Cube("Body", new Vector3(0.66f, 0.39f, 0.47f), new Vector3(0, 0.245f, 0), M.red, go.transform);
                    Cube("Top", new Vector3(0.66f, 0.15f, 0.47f), new Vector3(0, 0.515f, 0), M.red, go.transform);
                    Cube("Cap", new Vector3(0.61f, 0.03f, 0.42f), new Vector3(0, 0.605f, 0), M.dark, go.transform);
                    var gp = Cube("Gripper", new Vector3(0.62f, 0.025f, 0.42f), new Vector3(0, 0.42f, 0), M.dark, go.transform);
                    gp.name = "Gripper";
                }
                go.name = "Robot_" + r.id; go.transform.SetParent(parent, true);
                Transform grip = null;
                foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name.StartsWith("Gripper")) { grip = t; break; }
                robotT.Add(go.transform); gripT.Add(grip); gripRest.Add(grip ? grip.localPosition : Vector3.zero);
            }
        }

        // ------------------------------------------------------------------ 카메라 + 후처리
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
            Cam.nearClipPlane = 0.05f; Cam.farClipPlane = 200f;
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            FocusTarget = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<CinematicCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<CinematicCamera>();
            cine.view = this; cine.target = FocusTarget; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.6f); bl.threshold.Override(1.0f); bl.scatter.Override(0.65f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.35f); ca.contrast.Override(12f); ca.saturation.Override(-4f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.22f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(10f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.25f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.12f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-4f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (MovieCapture.IsRecording && MovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 330, 260), GUI.skin.box);
            GUILayout.Label($"<b>AutoStore simulation</b>  t = {Sim.t:0} s", Rich());
            GUILayout.Label($"Robots {Sim.robots.Count}  Ports {Sim.ports.Count}  Bins {Sim.bins}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} bins/h   Backlog {Sim.Backlog}");
            GUILayout.Label($"Avg lead {Sim.AvgLead:0} s   Digs/fetch {Sim.DigsPerFetch:0.00}");
            GUILayout.Label($"Robot util {Sim.RobotUtil:P0}   Port util {Sim.PortUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0.1f, 20f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
        static GUIStyle Rich() { var s = new GUIStyle(GUI.skin.label) { richText = true }; return s; }
    }
}
