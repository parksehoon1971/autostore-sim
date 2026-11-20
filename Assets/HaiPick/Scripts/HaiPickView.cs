// HaiPick Climb 방식 랙 등반 로봇 창고: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/랙/가이드 레일/작업대/로봇/토트/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/HaiPick/Resources/HaiPickModels/haipick_robot.fbx, haipick_tote_v0~2.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/haipick_blender.py 와 같은 값.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HaiPick
{
    public class HaiPickView : MonoBehaviour
    {
        [Header("Simulation")]
        public HaiPickConfig config = new HaiPickConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 600")]
        public float warmupSeconds = 600f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_haipick_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public HaiPickSim Sim { get; private set; }
        public Camera Cam { get; private set; }

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float Upright = 0.05f, RailOff = 0.035f, ArmUp = 0.36f, ForkReach = 0.62f;

        readonly List<Transform> robotT = new List<Transform>();
        readonly List<Transform> armPos = new List<Transform>(), armNeg = new List<Transform>(), forkT = new List<Transform>();
        readonly List<Vector3> armPosRest = new List<Vector3>(), armNegRest = new List<Vector3>(), forkRest = new List<Vector3>();
        readonly List<Transform> toteT = new List<Transform>();
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        float GW => config.nx * (float)config.cellX;
        float GD => (float)Sim.depthY;
        float RackTop => (float)(config.rackBase + config.levels * config.levelH);
        int FaceSide(int j) => j > 0 && config.lanes[j - 1] == 'A' ? -1 : 1;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new HaiPickSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildRacks();
            BuildStations();
            BuildRobots();
            BuildTotes();
            BuildCameraAndPost();
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
                var p = U(r.px, r.py, r.pz);
                robotT[i].position = p;
                float up = (float)r.arm * ArmUp;
                if (armPos[i]) armPos[i].position = p + armPosRest[i] + Vector3.up * (r.side > 0 ? up : 0f);
                if (armNeg[i]) armNeg[i].position = p + armNegRest[i] + Vector3.up * (r.side < 0 ? up : 0f);
                if (forkT[i]) forkT[i].position = p + forkRest[i] + Vector3.forward * (float)(r.fork * ForkReach * r.side);
            }
            for (int id = 0; id < toteT.Count; id++)
            {
                var q = Sim.TotePose(id);
                toteT[id].position = U(q.x, q.y, q.z);
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material rack, rackDark, rail, teeth, yellow, black, tote, toteDark, label, barcode, robot, robotGrey, deck,
                rubber, hub, lens, ledGreen, ledBlue, ledRed, hai, alu, white, screen, mat, wood, floor, wall, ceiling, beam,
                lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                rack = Lit("RackGalv", new Color(0.62f, 0.64f, 0.66f), 0.85f, 0.62f);
                rackDark = Lit("RackBeam", new Color(0.30f, 0.33f, 0.37f), 0.7f, 0.58f);
                rail = Lit("RailAlu", new Color(0.22f, 0.23f, 0.25f), 1f, 0.68f);
                teeth = Lit("RailTeeth", new Color(0.08f, 0.08f, 0.09f), 1f, 0.5f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                tote = Lit("TotePP", Color.white, 0f, 0.55f);
                Noise(tote, new Color(0.48f, 0.51f, 0.54f), new Color(0.53f, 0.56f, 0.59f), 30f, 3);
                toteDark = Lit("ToteHandle", new Color(0.30f, 0.32f, 0.34f), 0f, 0.5f);
                label = Lit("ToteLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                barcode = Lit("Barcode", new Color(0.02f, 0.02f, 0.02f), 0f, 0.4f);
                robot = Coat("RobotWhite", new Color(0.86f, 0.87f, 0.88f), 0.7f);
                robotGrey = Lit("RobotGrey", new Color(0.16f, 0.17f, 0.18f), 0.5f, 0.65f);
                deck = Lit("DeckGrey", new Color(0.35f, 0.36f, 0.37f), 0.6f, 0.6f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 4f);
                ledBlue = Emissive("LEDBlue", new Color(0.15f, 0.5f, 1f), 4f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                hai = Lit("HaiBlue", new Color(0.02f, 0.18f, 0.55f), 0f, 0.65f);
                alu = Lit("Aluminium", new Color(0.78f, 0.79f, 0.80f), 1f, 0.72f);
                white = Coat("WhitePanel", new Color(0.88f, 0.89f, 0.90f), 0.65f);
                screen = Emissive("Screen", new Color(0.35f, 0.6f, 1f), 1.5f);
                mat = Lit("FloorMat", new Color(0.06f, 0.06f, 0.065f), 0f, 0.1f);
                wood = Lit("Worktop", new Color(0.55f, 0.42f, 0.28f), 0f, 0.5f);
                items = new[]
                {
                    Lit("Cardboard", new Color(0.52f, 0.36f, 0.20f), 0f, 0.4f), Lit("CardboardWhite", new Color(0.78f, 0.74f, 0.68f), 0f, 0.4f),
                    Lit("ProdBlue", new Color(0.08f, 0.25f, 0.62f), 0f, 0.4f), Lit("ProdRed", new Color(0.70f, 0.08f, 0.06f), 0f, 0.4f),
                    Lit("ProdGreen", new Color(0.12f, 0.45f, 0.22f), 0f, 0.4f), Lit("ProdYellow", new Color(0.90f, 0.72f, 0.10f), 0f, 0.4f),
                };
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

        // ------------------------------------------------------------------ 건물 (밝은 물류센터, 천장 15 m)
        void BuildEnvironment()
        {
            float cxw = GW / 2, cyw = GD / 2;
            var env = new GameObject("Building").transform;
            const float S = 56f, Hh = 15f, m = 1f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            B(b, GW + 2 * m, 0.08f, 0.004f, cxw, -3.6f, 0.002f, M.yellow);
            B(b, GW + 2 * m, 0.08f, 0.004f, cxw, GD + m, 0.002f, M.yellow);
            B(b, 0.08f, GD + m + 3.6f, 0.004f, -m, (GD + m - 3.6f) / 2, 0.002f, M.yellow);
            B(b, 0.08f, GD + m + 3.6f, 0.004f, GW + m, (GD + m - 3.6f) / 2, 0.002f, M.yellow);
            B(b, S * 0.8f, 0.12f, 0.004f, cxw, -5.6f, 0.002f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 18, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 18, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 20, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 20, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -18; k < 20; k += 4) B(b, 0.3f, S, 0.6f, cxw + k, cyw, Hh - 0.4f, M.beam);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 17.8f, 1.2f, M.green);
            for (int ix = -2; ix <= 2; ix++)
                for (int iy = -2; iy <= 2; iy++)
                    B(b, 1.6f, 0.4f, 0.05f, cxw + ix * 5f, cyw + iy * 5f, Hh - 0.9f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 5f, cyw + iy * 5f, Hh - 1f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 40; l.intensity = 30f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            // 작업대 위 보조 조명
            foreach (int x0 in config.stationX0)
            {
                var lg = new GameObject("StationLight"); lg.transform.SetParent(env);
                lg.transform.position = U((x0 + 0.5) * config.cellX, -1.6f, 3.2f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 90; l.innerSpotAngle = 50;
                l.range = 8; l.intensity = 6f; l.color = new Color(1f, 0.97f, 0.92f); l.shadows = LightShadows.None;
            }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.0f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.Euler(62, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.80f, 0.82f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.67f, 0.69f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.45f, 0.46f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 6f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 랙: 기둥 + 받침 앵글 + 보 + 통로 쪽 가이드 레일
        void BuildRacks()
        {
            var c = config; float cX = (float)c.cellX, D = (float)c.rackD, bz = (float)c.rackBase, lh = (float)c.levelH;
            float H = RackTop + 0.12f;
            var root = new GameObject("Racks").transform;
            for (int j = 0; j < Sim.nL; j++)
            {
                if (c.lanes[j] != 'R') continue;
                float yc = (float)Sim.laneY[j]; int s = FaceSide(j);
                float yf = yc + s * (D / 2 - Upright / 2), yb = yc - s * (D / 2 - Upright / 2);
                var b = new Batch(cubeMesh, cylMesh);
                for (int i = 0; i <= c.nx; i++)
                {
                    float x = i * cX;
                    foreach (float yy in new[] { yf, yb })
                    {
                        B(b, Upright, Upright, H, x, yy, H / 2, M.rack);
                        B(b, 0.12f, 0.12f, 0.01f, x, yy, 0.005f, M.rackDark);
                    }
                    for (int k = 0; k < c.levels; k += 4) B(b, 0.03f, D - Upright, 0.03f, x, yc, bz + k * lh - 0.06f, M.rackDark);
                    for (int k = 0; k < c.levels; k++)
                    {
                        float w = (i > 0 && i < c.nx) ? 0.15f : 0.09f;
                        float xx = (i > 0 && i < c.nx) ? x : x + (i == 0 ? 0.03f : -0.03f);
                        B(b, w, D - 0.04f, 0.012f, xx, yc, bz + k * lh - 0.012f, M.rack);
                    }
                }
                var beamLv = new List<int> { 0 };
                for (int k = 6; k < c.levels; k += 6) beamLv.Add(k);
                beamLv.Add(c.levels);
                foreach (int k in beamLv)
                    foreach (float yy in new[] { yf, yb }) B(b, GW, 0.04f, 0.07f, GW / 2, yy, bz + k * lh - 0.05f, M.rackDark);
                b.Build("Rack_" + j, root);
                var rb = new Batch(cubeMesh, cylMesh);
                float yr = yc + s * (D / 2 + RailOff - 0.012f);
                for (int i = 0; i <= c.nx; i++)
                {
                    float x = i * cX;
                    B(rb, 0.04f, 0.024f, H - 0.05f, x, yr, (H - 0.05f) / 2 + 0.02f, M.rail);
                    B(rb, 0.014f, 0.006f, H - 0.1f, x, yr + s * 0.014f, (H - 0.1f) / 2 + 0.05f, M.teeth);
                    B(rb, 0.05f, 0.03f, 0.02f, x, yr, 0.01f, M.yellow);
                }
                rb.Build("Rails_" + j, root);
            }
        }

        // ------------------------------------------------------------------ 작업대 (앞쪽 y < 0 줄: 피킹 칸 + 대기 칸)
        void BuildStations()
        {
            var c = config; float cX = (float)c.cellX, Dp = (float)c.stationDepth, rw = (float)c.robotW;
            var root = new GameObject("Stations").transform;
            var b = new Batch(cubeMesh, cylMesh);
            for (int si = 0; si < c.stationX0.Length; si++)
            {
                int x0 = c.stationX0[si], n = c.stationN[si];
                float xa = x0 * cX, xb = (x0 + n) * cX, xp = (x0 + 0.5f) * cX;
                B(b, xb - xa, Dp, 0.004f, (xa + xb) / 2, -Dp / 2, 0.002f, M.mat);
                // 노란 칸 표시: 매트(4 mm)와 윗면 차이가 1 mm 뿐이라 깜빡였음 → 8 mm
                for (int k = 0; k <= n; k++) B(b, 0.03f, Dp, 0.008f, xa + k * cX, -Dp / 2, 0.004f, M.yellow);
                B(b, xb - xa, 0.03f, 0.008f, (xa + xb) / 2, -Dp, 0.004f, M.yellow);
                float yr = -Dp / 2 - (rw / 2 + RailOff) + 0.012f;
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    B(b, 0.04f, 0.024f, 1.45f, xp + sx * cX / 2, yr, 0.725f, M.rail);
                    B(b, 0.014f, 0.006f, 1.4f, xp + sx * cX / 2, yr - 0.014f, 0.72f, M.teeth);
                }
                B(b, cX + 0.06f, 0.06f, 0.06f, xp, yr, 1.47f, M.rail);
                float yw = -Dp - 0.30f;
                B(b, cX + 0.9f, 0.45f, 0.04f, xp + 0.2f, yw, 0.80f, M.wood);
                for (int sx = -1; sx <= 1; sx += 2) B(b, 0.05f, 0.4f, 0.78f, xp + 0.2f + sx * (cX / 2 + 0.4f), yw, 0.39f, M.alu);
                B(b, 0.04f, 0.04f, 0.80f, xp + cX / 2 + 0.45f, yw + 0.12f, 1.20f, M.alu);
                B(b, 0.34f, 0.03f, 0.22f, xp + cX / 2 + 0.36f, yw + 0.10f, 1.45f, M.black);
                B(b, 0.31f, 0.004f, 0.19f, xp + cX / 2 + 0.36f, yw + 0.084f, 1.45f, M.screen);
                B(b, 0.07f, 0.07f, 0.02f, xp - cX / 2 - 0.08f, yw - 0.08f, 0.83f, M.black);
                Cy(b, 0.022f, 0.015f, xp - cX / 2 - 0.08f, yw - 0.08f, 0.845f, 'Z', M.ledGreen);
                B(b, 0.04f, 0.04f, 2.1f, xp - cX / 2 - 0.25f, yw + 0.15f, 1.05f, M.alu);
                Cy(b, 0.035f, 0.07f, xp - cX / 2 - 0.25f, yw + 0.15f, 2.14f, 'Z', M.ledGreen);
                Cy(b, 0.035f, 0.07f, xp - cX / 2 - 0.25f, yw + 0.15f, 2.21f, 'Z', M.yellow);
                Cy(b, 0.035f, 0.07f, xp - cX / 2 - 0.25f, yw + 0.15f, 2.28f, 'Z', M.ledRed);
                B(b, cX + 1.0f, 1.0f, 0.012f, xp, yw - 0.75f, 0.006f, M.mat);
                float ys = yw - 1.75f;
                float[] lv = { 0.25f, 0.70f, 1.15f };
                for (int l = 0; l < lv.Length; l++)
                {
                    B(b, 1.5f, 0.5f, 0.025f, xp, ys, lv[l], M.alu);
                    for (int k = 0; k < 3; k++)
                    {
                        float xc = xp - 0.5f + k * 0.5f;
                        B(b, 0.40f, 0.32f, 0.24f, xc, ys, lv[l] + 0.135f, (k + l) % 3 != 0 ? M.items[0] : M.items[1]);
                        B(b, 0.04f, 0.004f, 0.02f, xc, ys + 0.255f, lv[l] + 0.012f, (k + l + si) % 4 == 0 ? M.ledGreen : M.black);
                    }
                }
                foreach (float xx in new[] { xp - 0.75f, xp + 0.75f })
                    foreach (float yy in new[] { ys - 0.24f, ys + 0.24f }) B(b, 0.035f, 0.035f, 1.45f, xx, yy, 0.725f, M.alu);
            }
            b.Build("StationParts", root);
        }

        // ------------------------------------------------------------------ 로봇
        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            var prefab = Resources.Load<GameObject>("HaiPickModels/haipick_robot");
            float L = (float)config.robotL, W = (float)config.robotW, dk = (float)config.deckH;
            foreach (var r in Sim.robots)
            {
                var root = new GameObject("Robot_" + r.id).transform; root.SetParent(parent, false);
                Transform aP = null, aN = null, fork = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                    FitLongAxisZ(go, W);
                    foreach (var t in go.GetComponentsInChildren<Transform>())
                    {
                        if (t.name.StartsWith("ArmP")) aP = t;
                        else if (t.name.StartsWith("ArmN")) aN = t;
                        else if (t.name.StartsWith("Fork")) fork = t;
                    }
                    // 축 변환으로 +y 쪽 팔이 반대로 왔으면 바꿔 씀
                    if (aP && aN && Center(aP).z < Center(aN).z) { var tt = aP; aP = aN; aN = tt; }
                }
                else
                {
                    Cube("Skirt", new Vector3(L - 0.02f, 0.05f, W - 0.02f), new Vector3(0, 0.045f, 0), M.robotGrey, root);
                    Cube("Body", new Vector3(L, 0.17f, W), new Vector3(0, 0.155f, 0), M.robot, root);
                    Cube("Deck", new Vector3(L - 0.04f, 0.06f, W - 0.06f), new Vector3(0, dk - 0.03f, 0), M.deck, root);
                    for (int sz = -1; sz <= 1; sz += 2)
                        Cube("LED", new Vector3(L - 0.04f, 0.012f, 0.004f), new Vector3(0, 0.045f, sz * (W / 2 - 0.007f)), M.ledGreen, root, false);
                    fork = new GameObject("Fork").transform; fork.SetParent(root, false);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Tine", new Vector3(0.035f, 0.014f, W - 0.12f), new Vector3(sx * 0.14f, dk + 0.004f, 0), M.alu, fork);
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        var a = new GameObject(sg > 0 ? "ArmP" : "ArmN").transform; a.SetParent(root, false);
                        for (int sx = -1; sx <= 1; sx += 2)
                        {
                            float x0 = sx * (L / 2 - 0.03f), z0 = sg * (W / 2 - 0.05f);
                            Cube("Case", new Vector3(0.05f, 0.36f, 0.07f), new Vector3(x0, 0.42f, z0), M.robotGrey, root);
                            Cube("Mast", new Vector3(0.032f, 0.40f, 0.05f), new Vector3(x0, 0.40f, z0), M.alu, a);
                            Cube("Grip", new Vector3(0.07f, 0.06f, 0.08f), new Vector3(x0 + sx * 0.012f, 0.61f, z0 + sg * 0.02f), M.robotGrey, a);
                        }
                        if (sg > 0) aP = a; else aN = a;
                    }
                }
                robotT.Add(root); armPos.Add(aP); armNeg.Add(aN); forkT.Add(fork);
                armPosRest.Add(aP ? aP.position - root.position : Vector3.zero);
                armNegRest.Add(aN ? aN.position - root.position : Vector3.zero);
                forkRest.Add(fork ? fork.position - root.position : Vector3.zero);
            }
        }

        static Vector3 Center(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return t.position;
            var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
            return bb.center;
        }

        // 모델 긴 축이 Unity Z(= 시뮬레이션 y)에 오도록 돌리고, 단위가 어긋나면 크기 보정
        static void FitLongAxisZ(GameObject go, float targetLen)
        {
            var rs = go.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return;
            var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
            if (bb.size.x > bb.size.z * 1.05f) { go.transform.localRotation = Quaternion.Euler(0, 90, 0) * go.transform.localRotation; }
            float sc = targetLen / Mathf.Max(1e-4f, Mathf.Max(bb.size.x, bb.size.z));
            if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
        }

        // ------------------------------------------------------------------ 토트
        void BuildTotes()
        {
            var parent = new GameObject("Totes").transform;
            var tpl = new List<GameObject>();
            var holder = new GameObject("ToteTemplates").transform; holder.gameObject.SetActive(false);
            for (int v = 0; v < 3; v++)
            {
                var pf = Resources.Load<GameObject>("HaiPickModels/haipick_tote_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m); FitLongAxisZ(m, (float)config.toteD + 0.012f); EquipFX.MeshFix.ResolveModel(m);
                }
                else go = FallbackTote(v, holder);
                tpl.Add(go);
            }
            var rnd = new System.Random(5);
            for (int id = 0; id < Sim.totes; id++)
            {
                int v = rnd.NextDouble() < 0.2 ? 0 : 1 + rnd.Next(2);
                var go = Instantiate(tpl[v], parent, false); go.name = "Tote_" + id;
                if (rnd.NextDouble() < 0.5) go.transform.localRotation = Quaternion.Euler(0, 180, 0);
                toteT.Add(go.transform);
            }
        }

        GameObject FallbackTote(int v, Transform holder)
        {
            float W = (float)config.toteW, D = (float)config.toteD, H = (float)config.toteH, t = 0.005f;
            var go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
            Cube("Floor", new Vector3(W - 0.02f, t, D - 0.02f), new Vector3(0, t / 2, 0), M.tote, go.transform);
            for (int s = -1; s <= 1; s += 2)
            {
                Cube("WallL", new Vector3(t, H, D), new Vector3(s * (W / 2 - t / 2), H / 2, 0), M.tote, go.transform);
                Cube("WallS", new Vector3(W, H, t), new Vector3(0, H / 2, s * (D / 2 - t / 2)), M.tote, go.transform);
                Cube("Handle", new Vector3(0.12f, 0.03f, 0.004f), new Vector3(0, H - 0.05f, s * (D / 2 + 0.001f)), M.toteDark, go.transform, false);
                Cube("Label", new Vector3(0.10f, 0.05f, 0.002f), new Vector3(-0.08f, H - 0.12f, s * (D / 2 + 0.002f)), M.label, go.transform, false);
            }
            if (v > 0)
            {
                var rnd = new System.Random(11 + v); float z = -D / 2 + 0.03f;
                for (int k = 0; k < 3; k++)
                {
                    float sz = 0.10f + (float)rnd.NextDouble() * 0.07f, sy = 0.06f + (float)rnd.NextDouble() * H * 0.6f;
                    Cube("Item", new Vector3(0.25f, sy, sz), new Vector3(0, t + sy / 2, z + sz / 2), M.items[(v + k) % M.items.Length], go.transform);
                    z += sz + 0.01f;
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
            EquipFX.CameraFX.ConfigureClip(Cam, 0.06f, 120f);   // near 0.02 → 0.06: 원거리 깊이 정밀도 향상 (깜빡임 감소)
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<HaiPickCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<HaiPickCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.4f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.5f); ca.contrast.Override(10f); ca.saturation.Override(0f);
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
                category = "Autonomous Case-handling Robot",
                name = "HaiPick Climb",
                korean = "랙 등반형 토트 피킹 로봇",
                spec = $"로봇 {config.robots}대  ·  {config.levels}단 랙 (약 {config.rackBase + config.levels * config.levelH:0.0} m)  ·  작업대 {config.stationX0.Length}곳",
                accent = new Color(0.35f, 0.70f, 1f),
            };
            cap.source = cine;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (HaiPickMovieCapture.IsRecording && HaiPickMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 380, 240), GUI.skin.box);
            GUILayout.Label($"<b>HaiPick Climb-style simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Rack {RackTop:0.0} m x {config.levels} levels  Robots {Sim.robots.Count}  Totes {Sim.totes}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} totes/h   Done {Sim.statDone}   Backlog {Sim.Backlog}");
            GUILayout.Label($"Lead {Sim.AvgLead:0} s   Avg climb {Sim.AvgClimb:0.0} m   Robot util {Sim.RobotUtil:P0}   Station util {Sim.StationUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
