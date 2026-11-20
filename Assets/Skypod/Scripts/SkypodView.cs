// Exotec Skypod 방식 로봇 창고: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/랙/스테이션/펜스/로봇/토트/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/Skypod/Resources/SkypodModels/skypod_robot.fbx, skypod_tote_v0~1.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/skypod_blender.py 와 같은 값.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Skypod
{
    public class SkypodView : MonoBehaviour
    {
        [Header("Simulation")]
        public SkypodConfig config = new SkypodConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 680")]
        public float warmupSeconds = 680f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_skypod_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public SkypodSim Sim { get; private set; }
        public Camera Cam { get; private set; }

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float UprX = 0.06f, UprY = 0.09f, RobotL = 0.62f, RobotW = 0.62f, RobotH = 1.10f;

        readonly List<Transform> robotT = new List<Transform>();
        readonly List<Transform> armT = new List<Transform>();
        readonly List<Vector3> armRest = new List<Vector3>();
        readonly List<Transform> toteT = new List<Transform>();
        readonly List<Light> spot = new List<Light>();
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 변환
        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        float Wx(int i) => (float)Sim.WorldX(i);
        float RowY(int j) => (float)Sim.RowY(j);
        float LevelZ(int l) => (float)Sim.LevelZ(l);
        float GW => config.nx * (float)config.cellX;
        float GD => (float)Sim.depthY;
        float TopZ => LevelZ(config.levels - 1) + (float)config.toteH + 0.18f;
        float AisleY(int a) => RowY(2 * a + 1);
        float Reach => (float)Sim.Reach;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new SkypodSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildRacks();
            BuildFloorMarks();
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
            float R = Reach;
            for (int i = 0; i < Sim.robots.Count; i++)
            {
                var r = Sim.robots[i];
                robotT[i].position = U(r.px, r.py, r.pz);
                if (armT[i]) armT[i].position = robotT[i].position + armRest[i] + new Vector3(0, 0, (float)(r.side * r.arm) * R);
            }
            for (int id = 0; id < toteT.Count; id++)
            {
                var p = Sim.TotePose(id);
                toteT[id].position = U(p.x, p.y, p.z);
            }
            for (int i = 0; i < Sim.stations.Count; i++) spot[i].intensity = Sim.stations[i].cur >= 0 ? 6f : 0f;
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material galv, teeth, rail, beam, tote, toteDark, label, barcode, robot, chassis, teal, rubber, gear, hub, lens,
                led, ledRed, ledGreen, alu, yellow, white, black, screen, mat, mesh, floor, wall, ceiling, steel, lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                galv = Lit("Galvanized", new Color(0.62f, 0.64f, 0.66f), 1f, 0.62f);
                Noise(galv, new Color(0.58f, 0.60f, 0.62f), new Color(0.68f, 0.70f, 0.72f), 6f, 4);
                teeth = Lit("RackTeeth", new Color(0.20f, 0.21f, 0.22f), 1f, 0.55f);
                Stripes(teeth, 60f);
                rail = Lit("RailSteel", new Color(0.55f, 0.57f, 0.59f), 1f, 0.58f);
                beam = Coat("BeamBlue", new Color(0.05f, 0.17f, 0.42f), 0.6f);
                tote = Lit("TotePP", Color.white, 0f, 0.52f);
                Noise(tote, new Color(0.17f, 0.18f, 0.19f), new Color(0.21f, 0.22f, 0.23f), 30f, 3);
                toteDark = Lit("ToteRim", new Color(0.10f, 0.10f, 0.11f), 0f, 0.5f);
                label = Lit("ToteLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                barcode = Lit("Barcode", new Color(0.02f, 0.02f, 0.02f), 0f, 0.4f);
                robot = Coat("RobotWhite", new Color(0.86f, 0.87f, 0.88f), 0.7f);
                chassis = Lit("ChassisGrey", new Color(0.10f, 0.105f, 0.11f), 0.3f, 0.55f);
                teal = Coat("AccentTeal", new Color(0.0f, 0.48f, 0.50f), 0.65f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                gear = Lit("GearSteel", new Color(0.35f, 0.36f, 0.37f), 1f, 0.65f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                led = Emissive("LEDCyan", new Color(0.2f, 0.8f, 1f), 5f);
                ledRed = Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                ledGreen = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 4f);
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

            // 기둥 톱니: 가로 줄무늬 알베도
            static void Stripes(Material m, float n)
            {
                var tex = new Texture2D(4, 16, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < 16; y++) for (int x = 0; x < 4; x++)
                        tex.SetPixel(x, y, y < 8 ? new Color(0.30f, 0.31f, 0.32f) : new Color(0.10f, 0.10f, 0.11f));
                tex.Apply();
                m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(1, n));
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

        // ------------------------------------------------------------------ 건물 (밝은 고층 물류센터)
        void BuildEnvironment()
        {
            float cxw = GW / 2, cyw = GD / 2;
            var env = new GameObject("Building").transform;
            const float S = 60f, Hh = 14f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            B(b, S * 0.8f, 0.12f, 0.004f, cxw, -6.2f, 0.002f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 18, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 20, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 22, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 22, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -18; k < 20; k += 4) B(b, 0.3f, S, 0.6f, cxw + k, cyw, Hh - 0.4f, M.steel);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 17.8f, 1.2f, M.green);
            for (int k = -16; k < 20; k += 8) B(b, 0.4f, 0.4f, Hh, cxw + k, cyw + 17.7f, Hh / 2, M.steel);
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
                    l.range = 40; l.intensity = 30f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f;
            sun.transform.rotation = Quaternion.Euler(62, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.80f, 0.82f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.67f, 0.69f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.45f, 0.46f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 5f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 랙: 통로 양쪽 면 기둥 + 깊이 방향 레일 (blender make_racks 와 같은 형상)
        void BuildRacks()
        {
            var c = config; float cX = (float)c.cellX, rackD = (float)c.rackD, zt = TopZ;
            var root = new GameObject("Racks").transform;
            for (int a = 0; a < c.aisles; a++)
            {
                var b = new Batch(cubeMesh, cylMesh);
                float ya = AisleY(a);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    float yf = ya + sgn * (float)c.aisleW / 2, yc = yf + sgn * UprY / 2, yb = yf + sgn * rackD;
                    for (int k = 0; k <= c.nx; k++)
                    {
                        float x = k * cX;
                        B(b, UprX, UprY, zt, x, yc, zt / 2, M.galv);
                        B(b, 0.022f, 0.006f, zt - 0.1f, x, yf - sgn * 0.003f, zt / 2, M.teeth);
                        B(b, 0.14f, 0.14f, 0.012f, x, yc, 0.006f, M.galv);
                        for (int l = 0; l < c.levels; l++)
                        {
                            float z = LevelZ(l);
                            for (int dx = -1; dx <= 1; dx += 2)
                            {
                                if ((k == 0 && dx < 0) || (k == c.nx && dx > 0)) continue;
                                B(b, 0.03f, rackD - 0.01f, 0.004f, x + dx * (UprX / 2 + 0.015f), (yf + yb) / 2, z - 0.002f, M.rail);
                                B(b, 0.003f, rackD - 0.01f, 0.03f, x + dx * (UprX / 2 + 0.0015f), (yf + yb) / 2, z + 0.013f, M.rail);
                            }
                            if (k < c.nx) B(b, cX - UprX, 0.02f, 0.02f, x + cX / 2, yb - sgn * 0.01f, z - 0.01f, M.rail);
                        }
                    }
                    B(b, GW + UprX, 0.08f, 0.10f, GW / 2, yc, zt - 0.05f, M.beam);
                    B(b, GW + UprX, 0.05f, 0.06f, GW / 2, yc, (float)c.level0Z - 0.06f, M.beam);
                }
                b.Build("Aisle_" + a, root);
            }
            var t = new Batch(cubeMesh, cylMesh);
            for (int j = 2; j < 2 * c.aisles; j += 2)
            {
                float y = RowY(j);
                for (int k = 0; k <= c.nx; k += 2)
                    foreach (float z in new[] { 3f, 6f, 9f })
                        if (z < zt) B(t, 0.04f, 2 * rackD + (float)c.flue, 0.04f, k * cX, y, z, M.galv);
            }
            B(t, 0.06f, GD, 0.06f, 0, GD / 2, zt + 0.03f, M.beam);
            B(t, 0.06f, GD, 0.06f, GW, GD / 2, zt + 0.03f, M.beam);
            for (int k = 4; k < c.nx; k += 4) B(t, 0.05f, GD, 0.05f, k * cX, GD / 2, zt + 0.025f, M.galv);
            t.Build("RackTiesAndTop", root);
        }

        // ------------------------------------------------------------------ 바닥 표시 + 안전 펜스
        void BuildFloorMarks()
        {
            var c = config;
            var b = new Batch(cubeMesh, cylMesh);
            float x0 = -0.75f, x1 = GW + 0.75f;
            foreach (float y in new[] { (float)c.frontY - 0.45f, -2.25f }) B(b, x1 - x0, 0.06f, 0.003f, (x0 + x1) / 2, y, 0.0015f, M.yellow);
            foreach (float x in new[] { -0.75f, GW + 0.75f }) B(b, 0.06f, GD + 1.25f, 0.003f, x, (GD - 1.25f) / 2, 0.0015f, M.yellow);
            for (int k = 1; k < c.nx; k += 3)
            {
                float x = Wx(k);
                B(b, 0.30f, 0.05f, 0.003f, x, (float)c.frontY, 0.0016f, M.white);
                B(b, 0.10f, 0.12f, 0.003f, x - 0.17f, (float)c.frontY, 0.0016f, M.white);
            }
            void Fence(float xa, float ya, float xb, float yb)
            {
                float L = Mathf.Sqrt((xb - xa) * (xb - xa) + (yb - ya) * (yb - ya)); int n = Mathf.Max(1, (int)(L / 1.5f));
                for (int i = 0; i <= n; i++) B(b, 0.06f, 0.06f, 2.4f, xa + (xb - xa) * i / n, ya + (yb - ya) * i / n, 1.2f, M.yellow);
                bool alongx = Mathf.Abs(xb - xa) > Mathf.Abs(yb - ya);
                float cx = (xa + xb) / 2, cy = (ya + yb) / 2;
                foreach (float z in new[] { 0.15f, 2.35f }) B(b, alongx ? L : 0.03f, alongx ? 0.03f : L, 0.03f, cx, cy, z, M.yellow);
                B(b, alongx ? L : 0.008f, alongx ? 0.008f : L, 2.15f, cx, cy, 1.25f, M.mesh);
            }
            float gy0 = -0.3f, gy1 = GD + 1.2f;
            Fence(-1.6f, gy0, -1.6f, gy1); Fence(GW + 1.6f, gy0, GW + 1.6f, gy1); Fence(-1.6f, gy1, GW + 1.6f, gy1);
            b.Build("FloorMarks", new GameObject("Floor").transform);
        }

        // ------------------------------------------------------------------ 피킹 스테이션 (y = -2 줄, 피킹 칸 = 시작 x)
        void BuildStations()
        {
            var c = config;
            var root = new GameObject("Stations").transform;
            var b = new Batch(cubeMesh, cylMesh);
            for (int si = 0; si < c.stations.Length; si++)
            {
                int x0 = c.stations[si];
                float xc = Wx(x0), yc = (float)c.spurY, yo = yc - 0.55f, hh = 2.3f;
                for (int sx = -1; sx <= 1; sx += 2) B(b, 0.08f, 1.0f, hh, xc + sx * 0.48f, yc, hh / 2, M.white);
                B(b, 1.04f, 1.0f, 0.10f, xc, yc, hh, M.white);
                B(b, 1.04f, 0.04f, 0.6f, xc, yc + 0.48f, hh - 0.30f, M.white);
                B(b, 1.2f, 0.40f, 0.04f, xc, yo - 0.12f, 0.88f, M.white);
                B(b, 1.2f, 0.03f, 0.88f, xc, yo + 0.07f, 0.44f, M.white);
                B(b, 0.20f, 0.004f, 0.035f, xc + 0.38f, yo - 0.32f, 0.902f, M.ledGreen);
                B(b, 0.50f, 0.03f, 0.32f, xc, yo + 0.05f, 1.55f, M.black);
                B(b, 0.46f, 0.004f, 0.28f, xc, yo + 0.034f, 1.55f, M.screen);
                B(b, 0.04f, 0.04f, 0.6f, xc, yo + 0.08f, 1.15f, M.alu);
                B(b, 0.9f, 0.12f, 0.04f, xc, yo + 0.1f, hh - 0.08f, M.lightPanel);
                B(b, 1.6f, 1.1f, 0.012f, xc, yo - 0.85f, 0.006f, M.mat);
                float ys = yo - 1.75f;
                float[] lv = { 0.35f, 0.85f, 1.35f };
                for (int l = 0; l < lv.Length; l++)
                {
                    B(b, 1.6f, 0.5f, 0.025f, xc, ys, lv[l], M.alu);
                    for (int k = 0; k < 3; k++)
                    {
                        float x = xc + (k - 1) * 0.5f;
                        B(b, 0.40f, 0.32f, 0.22f, x, ys, lv[l] + 0.125f, (k + l) % 3 != 0 ? M.items[0] : M.items[1]);
                        B(b, 0.05f, 0.004f, 0.02f, x, ys + 0.255f, lv[l] + 0.012f, (k + l + si) % 4 == 0 ? M.ledGreen : M.black);
                    }
                }
                foreach (float xx in new[] { xc - 0.8f, xc + 0.8f })
                    foreach (float yy in new[] { ys - 0.24f, ys + 0.24f }) B(b, 0.035f, 0.035f, 1.6f, xx, yy, 0.8f, M.alu);
                Cy(b, 0.03f, 0.06f, xc + 0.48f, yc - 0.45f, hh + 0.10f, 'Z', M.ledRed);
                Cy(b, 0.03f, 0.06f, xc + 0.48f, yc - 0.45f, hh + 0.16f, 'Z', M.ledGreen);
                for (int k = 1; k < c.spurLen; k++) B(b, 0.5f, 0.04f, 0.003f, Wx(x0 + k), yc - 0.35f, 0.0016f, M.white);
                var lg = new GameObject("StationSpot_" + si); lg.transform.SetParent(root);
                lg.transform.position = U(xc, yc - 0.1f, hh - 0.08f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                var li = lg.AddComponent<Light>(); li.type = LightType.Spot; li.spotAngle = 40; li.innerSpotAngle = 20;
                li.range = 4f; li.intensity = 0f; li.color = new Color(1f, 0.97f, 0.9f); li.shadows = LightShadows.None;
                spot.Add(li);
            }
            b.Build("StationParts", root);
        }

        // ------------------------------------------------------------------ 로봇
        void BuildRobots()
        {
            var parent = new GameObject("Robots").transform;
            var prefab = Resources.Load<GameObject>("SkypodModels/skypod_robot");
            float bay = (float)config.bayZ, gy = (float)config.aisleW / 2 - 0.035f;
            foreach (var r in Sim.robots)
            {
                var root = new GameObject("Robot_" + r.id).transform; root.SetParent(parent, false);
                Transform arm = null;
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    EquipFX.MeshFix.ResolveModel(go);   // 모델 안 겹친 면 분리 (깜빡임 방지)
                    FitScale(go, RobotH + 0.08f);
                    foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name.StartsWith("Arm")) arm = t;
                }
                else
                {
                    float bz0 = bay - 0.02f, bz1 = bay + (float)config.toteH + 0.04f;
                    Cube("Chassis", new Vector3(RobotL - 0.04f, bz0 - 0.06f, RobotW - 0.10f), new Vector3(0, 0.06f + (bz0 - 0.06f) / 2, 0), M.chassis, root);
                    Cube("BayFloor", new Vector3(RobotL, 0.05f, RobotW - 0.06f), new Vector3(0, bz0 - 0.025f, 0), M.robot, root);
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        Cube("Post", new Vector3(0.07f, bz1 - bz0, RobotW - 0.06f), new Vector3(sx * (RobotL / 2 - 0.035f), (bz0 + bz1) / 2, 0), M.robot, root);
                        Cube("LED", new Vector3(0.004f, 0.012f, RobotW - 0.16f), new Vector3(sx * (RobotL / 2 + 0.001f), bz1 + 0.05f, 0), M.led, root, false);
                        for (int sy = -1; sy <= 1; sy += 2)
                            foreach (float z in new[] { 0.16f, RobotH - 0.12f })
                            {
                                var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(g.GetComponent<Collider>());
                                g.transform.SetParent(root, false); g.transform.localPosition = new Vector3(sx * (RobotL / 2 - 0.01f), z, sy * gy);
                                g.transform.localRotation = Quaternion.Euler(0, 0, 90); g.transform.localScale = new Vector3(0.09f, 0.015f, 0.09f);
                                g.GetComponent<MeshRenderer>().sharedMaterial = M.gear;
                            }
                    }
                    float hz = RobotH - bz1;
                    Cube("Top", new Vector3(RobotL, hz, RobotW - 0.06f), new Vector3(0, bz1 + hz / 2, 0), M.robot, root);
                    for (int sy = -1; sy <= 1; sy += 2)
                        Cube("Stripe", new Vector3(RobotL - 0.10f, 0.035f, 0.004f), new Vector3(0, bz1 + hz * 0.55f, sy * ((RobotW - 0.06f) / 2 + 0.001f)), M.teal, root, false);
                    arm = new GameObject("Arm").transform; arm.SetParent(root, false);
                    for (int sx = -1; sx <= 1; sx += 2)
                        Cube("Fork", new Vector3(0.025f, 0.05f, (float)config.toteD + 0.02f), new Vector3(sx * ((float)config.toteW / 2 + 0.008f), bay + 0.10f, 0), M.alu, arm);
                }
                robotT.Add(root); armT.Add(arm);
                armRest.Add(arm ? arm.position - root.position : Vector3.zero);
            }
        }

        // 단위가 어긋나면 크기 보정 (FBX 축 변환은 임포터의 bakeAxisConversion 이 처리)
        static void FitScale(GameObject go, float targetH)
        {
            var rs = go.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return;
            var bb = rs[0].bounds; foreach (var x in rs) bb.Encapsulate(x.bounds);
            float sc = targetH / Mathf.Max(1e-4f, bb.size.y);
            if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
        }

        // ------------------------------------------------------------------ 토트
        void BuildTotes()
        {
            var parent = new GameObject("Totes").transform;
            var tpl = new List<GameObject>();
            var holder = new GameObject("ToteTemplates").transform; holder.gameObject.SetActive(false);
            for (int v = 0; v < 2; v++)
            {
                var pf = Resources.Load<GameObject>("SkypodModels/skypod_tote_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m); FitScale(m, (float)config.toteH + 0.01f); EquipFX.MeshFix.ResolveModel(m);
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
                Cube("WallS", new Vector3(t, H, D), new Vector3(s * (W / 2 - t / 2), H / 2, 0), M.tote, go.transform);
                Cube("Handle", new Vector3(0.18f, 0.035f, 0.004f), new Vector3(0, H - 0.055f, s * (D / 2 + 0.002f)), M.toteDark, go.transform, false);
                Cube("Label", new Vector3(0.10f, 0.055f, 0.002f), new Vector3(-0.18f, H - 0.12f, s * (D / 2 + 0.004f)), M.label, go.transform, false);
            }
            if (v > 0)
            {
                var rnd = new System.Random(11 + v); float x = -W / 2 + 0.03f;
                for (int k = 0; k < 3; k++)
                {
                    float sx = 0.12f + (float)rnd.NextDouble() * 0.06f, sz = 0.08f + (float)rnd.NextDouble() * H * 0.6f;
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
            EquipFX.CameraFX.ConfigureClip(Cam, 0.08f, 120f);   // near 0.02 → 0.08: 원거리 깊이 정밀도 향상 (깜빡임 감소)
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<SkypodCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<SkypodCamera>();
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
                category = "Rack-Climbing Robot ASRS",
                name = "Exotec Skypod",
                korean = "랙 등반 로봇 자동창고",
                spec = $"로봇 {config.robots}대  ·  통로 {config.aisles}개  ·  {config.levels}단 랙  ·  스테이션 {config.stations.Length}곳",
                accent = new Color(0.15f, 0.85f, 0.80f),
            };
            cap.source = cine;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || Sim == null || (SkypodMovieCapture.IsRecording && SkypodMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 380, 240), GUI.skin.box);
            GUILayout.Label($"<b>Exotec Skypod-style simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Aisles {config.aisles}  Levels {config.levels}  Robots {Sim.robots.Count}  Totes {Sim.totes}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} totes/h   Done {Sim.statDone}   Backlog {Sim.Backlog}");
            GUILayout.Label($"Lead {Sim.AvgLead:0} s   Avg climb {Sim.AvgClimb:0.0} m   Robot util {Sim.RobotUtil:P0}   Station util {Sim.StationUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
