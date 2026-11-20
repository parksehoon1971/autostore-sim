// Libiao AirRob 방식 토트 창고: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD + 장비명 캡션
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/랙/AirRob 레일/스테이션/AirRob/바닥 로봇/토트/조명/후처리/카메라를 코드로 만듭니다.
// 모델: Assets/AirRob/Resources/AirRobModels/airrob_unit.fbx, airrob_floorbot.fbx, airrob_tote_v0~1.fbx (Blender 스크립트 출력).
//       없으면 기본 도형 사용. airrob_unit 의 자식 Carriage(승강) / HookMid / Hook(양방향 훅) 을 움직입니다.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/airrob_blender.py 와 같은 값.
// z-fighting 방지: 겹치는 상자끼리 같은 평면에 면이 오지 않게 (보는 기둥보다 얇게, 붙는 장식은 3 mm 이상 띄움, 바닥 표시 6 mm).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AirRob
{
    public class AirRobView : MonoBehaviour
    {
        [Header("Simulation")]
        public AirRobConfig config = new AirRobConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적·카메라 동선과 같게 하려면 1342")]
        public float warmupSeconds = 1342f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_airrob_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;
        public bool showCaption = true;

        public AirRobSim Sim { get; private set; }
        public Camera Cam { get; private set; }

        // 장면 치수 (Blender 스크립트와 같은 값)
        public const float UprX = 0.07f, UprY = 0.05f, AirW = 0.764f, AirD = 0.82f, BotL = 0.70f, BotW = 0.46f;

        class AirT { public Transform root, car, mid, hook; public Vector3 carRest, midRest, hookRest; }
        readonly List<AirT> airT = new List<AirT>();
        readonly List<Transform> botT = new List<Transform>();
        readonly List<float> botYaw = new List<float>();
        readonly List<Vector2> botLast = new List<Vector2>();
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
        float RackD => (float)Sim.rackDepthY;
        public float TopZ => LevelZ(config.levels - 1) + (float)config.toteH + 0.15f;
        public float MastH => TopZ + 0.25f;
        float AisleY(int a) => (float)Sim.AisleY(a);
        float Reach => (float)Sim.Reach;

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new AirRobSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            BuildEnvironment();
            BuildRacks();
            BuildAirRails();
            BuildFloorMarks();
            BuildStations();
            BuildAirRobs();
            BuildBots();
            BuildTotes();
            BuildCameraAndPost();
            SyncAll(true);
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        void Update()
        {
            acc += Time.deltaTime * simSpeed;
            int guard = 0;
            while (acc >= subStep - 1e-6 && guard++ < 2000) { Sim.Step(subStep); acc -= subStep; }
            SyncAll(false);
        }

        // ------------------------------------------------------------------ 동기화
        void SyncAll(bool first)
        {
            float R = Reach, dz = (float)config.deckZ;
            for (int i = 0; i < Sim.airs.Count; i++)
            {
                var A = Sim.airs[i]; var T = airT[i];
                T.root.position = U(A.px, AisleY(i), 0);
                var up = new Vector3(0, (float)A.pz - dz, 0);
                if (T.car) T.car.position = T.root.position + T.carRest + up;
                if (T.mid) T.mid.position = T.root.position + T.midRest + up + new Vector3(0, 0, (float)A.hook * R * 0.5f);
                if (T.hook) T.hook.position = T.root.position + T.hookRest + up + new Vector3(0, 0, (float)A.hook * R);
            }
            for (int i = 0; i < Sim.robots.Count; i++)
            {
                var r = Sim.robots[i];
                var p = new Vector2((float)r.px, (float)r.py);
                var d = p - botLast[i];
                if (!first && d.sqrMagnitude > 1e-8f) botYaw[i] = Mathf.Abs(d.x) >= Mathf.Abs(d.y) ? 0f : 90f;
                botLast[i] = p;
                botT[i].position = U(r.px, r.py, 0);
                botT[i].rotation = Quaternion.Euler(0, botYaw[i], 0);
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
            public Material upright, beam, galv, rail, tote, toteDark, label, barcode, airWhite, extrusion, groove, accent, chassis, botGrey,
                rubber, poly, hub, lens, led, ledAmber, ledRed, ledGreen, alu, yellow, white, black, screen, mat, mesh, floor, wall, ceiling,
                steel, lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                upright = Coat("RackBlue", new Color(0.04f, 0.16f, 0.42f), 0.62f);
                beam = Coat("BeamOrange", new Color(0.85f, 0.30f, 0.03f), 0.6f);
                galv = Lit("Galvanized", new Color(0.62f, 0.64f, 0.66f), 1f, 0.62f);
                Noise(galv, new Color(0.58f, 0.60f, 0.62f), new Color(0.68f, 0.70f, 0.72f), 6f, 4);
                rail = Lit("RailSteel", new Color(0.55f, 0.57f, 0.59f), 1f, 0.68f);
                tote = Lit("TotePP", Color.white, 0f, 0.52f);
                Noise(tote, new Color(0.19f, 0.26f, 0.35f), new Color(0.22f, 0.29f, 0.38f), 30f, 3);
                toteDark = Lit("ToteRim", new Color(0.10f, 0.12f, 0.15f), 0f, 0.5f);
                label = Lit("ToteLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                barcode = Lit("Barcode", new Color(0.02f, 0.02f, 0.02f), 0f, 0.4f);
                airWhite = Coat("AirWhite", new Color(0.88f, 0.89f, 0.90f), 0.7f);
                extrusion = Lit("Extrusion", new Color(0.72f, 0.73f, 0.75f), 1f, 0.65f);
                groove = Lit("ExtrusionGroove", new Color(0.10f, 0.10f, 0.11f), 0.6f, 0.5f);
                accent = Coat("AccentBlue", new Color(0.02f, 0.28f, 0.70f), 0.7f);
                chassis = Lit("ChassisGrey", new Color(0.10f, 0.105f, 0.11f), 0.3f, 0.55f);
                botGrey = Coat("BotGrey", new Color(0.32f, 0.34f, 0.36f), 0.6f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                poly = Lit("PolyWheel", new Color(0.85f, 0.55f, 0.10f), 0f, 0.5f);
                hub = Lit("HubAlu", new Color(0.70f, 0.71f, 0.73f), 1f, 0.7f);
                lens = Lit("SensorGlass", new Color(0.01f, 0.01f, 0.012f), 0f, 0.95f);
                led = Emissive("LEDCyan", new Color(0.2f, 0.8f, 1f), 5f);
                ledAmber = Emissive("LEDAmber", new Color(1f, 0.55f, 0.05f), 4f);
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
                        float g = Mathf.Lerp(0.52f, 0.64f, Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, new Color(g, g * 1.02f, g * 1.02f, 1));
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

        // 시뮬레이션 좌표로 박스 추가 (크기 sx, sy, sz / 중심 x, y, z) 와 z0..z1 범위 박스
        static void B(Batch b, float sx, float sy, float sz, float x, float y, float z, Material m) =>
            b.Box(new Vector3(sx, sz, sy), new Vector3(x, z, y), m);
        static void BZ(Batch b, float sx, float sy, float x, float y, float z0, float z1, Material m) =>
            B(b, sx, sy, z1 - z0, x, y, (z0 + z1) / 2, m);
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
        // 시뮬레이션 좌표 (x, y, z0..z1) 로 지정하는 기본 도형 상자 (모델이 없을 때)
        static void CubeZ(string name, float sx, float sy, float x, float y, float z0, float z1, Material m, Transform parent) =>
            Cube(name, new Vector3(sx, z1 - z0, sy), new Vector3(x, (z0 + z1) / 2, y), m, parent);

        // ------------------------------------------------------------------ 건물
        void BuildEnvironment()
        {
            float cxw = GW / 2, cyw = GD / 2;
            var env = new GameObject("Building").transform;
            const float S = 50f, Hh = 10f;
            var b = new Batch(cubeMesh, cylMesh);
            B(b, S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            BZ(b, S * 0.8f, 0.12f, cxw, -6.0f, 0f, 0.006f, M.green);
            B(b, S, 0.2f, Hh, cxw, cyw + 14, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw - 18, cyw, Hh / 2, M.wall);
            B(b, 0.2f, S, Hh, cxw + 20, cyw, Hh / 2, M.wall);
            B(b, S, 0.2f, Hh, cxw, cyw - 20, Hh / 2, M.wall);
            B(b, S, S, 0.2f, cxw, cyw, Hh + 0.1f, M.ceiling);
            for (int k = -18; k < 20; k += 4) B(b, 0.3f, S, 0.6f, cxw + k, cyw, Hh - 0.3f, M.steel);
            B(b, S, 0.5f, 0.06f, cxw, cyw + 13.7f, 1.2f, M.green);
            for (int k = -16; k < 20; k += 8) B(b, 0.4f, 0.4f, Hh, cxw + k, cyw + 13.6f, Hh / 2, M.steel);
            for (int ix = -3; ix <= 3; ix++)
                for (int iy = -3; iy <= 3; iy++)
                    B(b, 1.8f, 0.45f, 0.05f, cxw + ix * 5f, cyw + iy * 5f, Hh - 0.65f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 6f, cyw + iy * 6f, Hh - 0.8f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 30; l.intensity = 22f; l.color = new Color(1f, 0.98f, 0.95f);
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
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 4f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 랙: 통로 양쪽 단일 깊이 랙 (blender make_racks 와 같은 형상)
        void BuildRacks()
        {
            var c = config; float cX = (float)c.cellX, rd = (float)c.rackD, zt = TopZ;
            var root = new GameObject("Racks").transform;
            for (int a = 0; a < c.aisles; a++)
            {
                var b = new Batch(cubeMesh, cylMesh);
                foreach (int s in new[] { -1, 1 })
                {
                    float yc = RowY(Sim.TunnelRow(a, s)), ya = yc - s * rd / 2, yb = yc + s * rd / 2;
                    foreach (var (yf, sg) in new[] { (ya, s), (yb, -s) })
                    {
                        float yu = yf + sg * UprY / 2;
                        for (int k = 0; k <= c.nx; k++)
                        {
                            float x = k * cX;
                            BZ(b, UprX, UprY, x, yu, 0f, zt, M.upright);
                            BZ(b, 0.13f, 0.10f, x, yu, 0f, 0.008f, M.galv);
                            if (k < c.nx)
                            {
                                for (int l = 0; l < c.levels; l++)
                                {
                                    float z = LevelZ(l);
                                    BZ(b, cX - UprX, UprY - 0.012f, x + cX / 2, yu, z - 0.058f, z - 0.018f, M.beam);
                                }
                                BZ(b, cX - UprX, UprY - 0.012f, x + cX / 2, yu, zt - 0.06f, zt - 0.01f, M.beam);
                            }
                        }
                    }
                    float y0 = ya + s * UprY, y1 = yb - s * UprY;
                    for (int k = 0; k < c.nx; k++)
                        for (int col = -1; col <= 1; col += 2)
                        {
                            float xc = Wx(k) + col * (float)c.colOff;
                            for (int l = 0; l < c.levels; l++)
                            {
                                float z = LevelZ(l);
                                for (int e = -1; e <= 1; e += 2)
                                    B(b, 0.024f, Mathf.Abs(y1 - y0) - 0.004f, 0.026f, xc + e * 0.175f, (y0 + y1) / 2, z - 0.016f, M.galv);
                            }
                        }
                }
                b.Build("Aisle_" + a, root);
            }
            var t = new Batch(cubeMesh, cylMesh);
            for (int a = 0; a < c.aisles - 1; a++)
            {
                float y = (RowY(3 * a + 2) + RowY(3 * a + 3)) / 2;
                for (int k = 0; k <= c.nx; k += 2)
                    foreach (float z in new[] { 1.5f, 3f, 4.5f })
                        B(t, 0.03f, (float)c.flue + 2 * UprY + 0.02f, 0.03f, k * cX + 0.05f, y, z, M.galv);
            }
            t.Build("RackTies", root);
        }

        // ------------------------------------------------------------------ AirRob 레일 (바닥 레일 + 위 레일과 받침)
        void BuildAirRails()
        {
            var c = config;
            var b = new Batch(cubeMesh, cylMesh);
            float x0 = -0.15f, x1 = GW + 0.15f, Hm = MastH, zt = TopZ;
            for (int a = 0; a < c.aisles; a++)
            {
                float ya = AisleY(a);
                BZ(b, x1 - x0, 0.14f, (x0 + x1) / 2, ya, 0f, 0.006f, M.galv);
                BZ(b, x1 - x0, 0.05f, (x0 + x1) / 2, ya, 0.006f, 0.044f, M.rail);
                BZ(b, x1 - x0, 0.05f, (x0 + x1) / 2, ya, Hm + 0.004f, Hm + 0.05f, M.rail);
                for (int k = 0; k <= c.nx; k += 2)
                {
                    float x = k * (float)c.cellX + 0.06f;
                    BZ(b, 0.05f, (float)c.aisleW + 2 * UprY + 0.04f, x, ya, Hm + 0.05f, Hm + 0.09f, M.upright);
                    for (int s = -1; s <= 1; s += 2)
                        BZ(b, 0.05f, 0.05f, x, ya + s * ((float)c.aisleW / 2 + UprY / 2 + 0.005f), zt + 0.002f, Hm + 0.05f, M.upright);
                }
            }
            b.Build("AirRobRails", new GameObject("Rails").transform);
        }

        // ------------------------------------------------------------------ 바닥 표시 (두께 6 mm) + 안전 펜스
        void BuildFloorMarks()
        {
            var c = config;
            var b = new Batch(cubeMesh, cylMesh);
            float x0 = -1.0f, x1 = GW + 1.0f, zt = 0.006f;
            foreach (float y in new[] { (float)c.frontY - 0.40f, -2.25f }) BZ(b, x1 - x0, 0.06f, (x0 + x1) / 2, y, 0f, zt, M.yellow);
            foreach (float x in new[] { -1.0f, GW + 1.0f }) BZ(b, 0.06f, RackD + 1.2f, x, (RackD - 1.2f) / 2, 0f, zt, M.yellow);
            for (int k = 1; k < c.nx; k += 3)
            {
                float x = Wx(k);
                BZ(b, 0.30f, 0.05f, x, (float)c.frontY, 0f, zt, M.white);
                BZ(b, 0.10f, 0.12f, x - 0.17f, (float)c.frontY, 0f, zt, M.white);
            }
            for (int a = 0; a < c.aisles; a++)
                for (int s = -1; s <= 1; s += 2) BZ(b, 0.25f, 0.05f, -0.45f, RowY(Sim.TunnelRow(a, s)), 0f, zt, M.white);
            void Fence(float xa, float ya, float xb, float yb)
            {
                float L = Mathf.Sqrt((xb - xa) * (xb - xa) + (yb - ya) * (yb - ya)); int n = Mathf.Max(1, (int)(L / 1.5f));
                for (int i = 0; i <= n; i++) B(b, 0.06f, 0.06f, 2.4f, xa + (xb - xa) * i / n, ya + (yb - ya) * i / n, 1.2f, M.yellow);
                bool alongx = Mathf.Abs(xb - xa) > Mathf.Abs(yb - ya);
                float cx = (xa + xb) / 2, cy = (ya + yb) / 2;
                foreach (float z in new[] { 0.15f, 2.35f }) B(b, alongx ? L : 0.03f, alongx ? 0.03f : L, 0.03f, cx, cy, z, M.yellow);
                B(b, alongx ? L : 0.008f, alongx ? 0.008f : L, 2.15f, cx, cy, 1.25f, M.mesh);
            }
            float gy1 = GD + 0.6f;
            Fence(-1.8f, 0.2f, -1.8f, gy1); Fence(GW + 1.8f, 0.2f, GW + 1.8f, gy1); Fence(-1.8f, gy1, GW + 1.8f, gy1);
            b.Build("FloorMarks", new GameObject("Floor").transform);
        }

        // ------------------------------------------------------------------ 작업 스테이션 (y = -2 줄, 피킹 칸 = 시작 x)
        void BuildStations()
        {
            var c = config;
            var root = new GameObject("Stations").transform;
            var b = new Batch(cubeMesh, cylMesh);
            for (int si = 0; si < c.stations.Length; si++)
            {
                int x0 = c.stations[si];
                float xc = Wx(x0), yc = (float)c.spurY, yo = yc - 0.50f, hh = 2.2f;
                for (int sx = -1; sx <= 1; sx += 2) B(b, 0.08f, 0.9f, hh, xc + sx * 0.46f, yc, hh / 2, M.white);
                B(b, 1.00f, 0.9f, 0.10f, xc, yc, hh, M.white);
                B(b, 1.00f, 0.04f, 0.6f, xc, yc + 0.43f, hh - 0.30f, M.white);
                B(b, 1.2f, 0.36f, 0.04f, xc, yo - 0.10f, 0.80f, M.white);
                B(b, 1.2f, 0.03f, 0.40f, xc, yo + 0.07f, 0.60f, M.white);
                B(b, 0.20f, 0.004f, 0.035f, xc + 0.38f, yo - 0.285f, 0.822f, M.ledGreen);
                B(b, 0.50f, 0.03f, 0.32f, xc, yo + 0.05f, 1.50f, M.black);
                B(b, 0.46f, 0.004f, 0.28f, xc, yo + 0.032f, 1.50f, M.screen);
                B(b, 0.04f, 0.04f, 0.52f, xc, yo + 0.08f, 1.08f, M.alu);
                B(b, 0.9f, 0.12f, 0.04f, xc, yo + 0.1f, hh - 0.08f, M.lightPanel);
                BZ(b, 1.6f, 1.1f, xc, yo - 0.80f, 0f, 0.012f, M.mat);
                float ys = yo - 1.65f;
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
                Cy(b, 0.03f, 0.06f, xc + 0.46f, yc - 0.40f, hh + 0.10f, 'Z', M.ledRed);
                Cy(b, 0.03f, 0.06f, xc + 0.46f, yc - 0.40f, hh + 0.16f, 'Z', M.ledGreen);
                for (int k = 1; k < c.spurLen; k++) BZ(b, 0.5f, 0.04f, Wx(x0 + k), yc - 0.32f, 0f, 0.006f, M.white);
                var lg = new GameObject("StationSpot_" + si); lg.transform.SetParent(root);
                lg.transform.position = U(xc, yc - 0.1f, hh - 0.08f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                var li = lg.AddComponent<Light>(); li.type = LightType.Spot; li.spotAngle = 40; li.innerSpotAngle = 20;
                li.range = 4f; li.intensity = 0f; li.color = new Color(1f, 0.97f, 0.9f); li.shadows = LightShadows.None;
                spot.Add(li);
            }
            b.Build("StationParts", root);
        }

        // ------------------------------------------------------------------ AirRob (FBX: 몸체 + Carriage / HookMid / Hook)
        void BuildAirRobs()
        {
            var parent = new GameObject("AirRobs").transform;
            var prefab = Resources.Load<GameObject>("AirRobModels/airrob_unit");
            float dz = (float)config.deckZ;
            for (int i = 0; i < Sim.airs.Count; i++)
            {
                var root = new GameObject("AirRob_" + i).transform; root.SetParent(parent, false);
                var T = new AirT { root = root };
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    FitScale(go, MastH + 0.06f);
                    foreach (var t in go.GetComponentsInChildren<Transform>())
                    {
                        if (t.name == "Carriage") T.car = t;
                        else if (t.name == "HookMid") T.mid = t;
                        else if (t.name == "Hook") T.hook = t;
                    }
                }
                if (T.car == null) FallbackAirRob(root, T);
                T.carRest = T.car.position - root.position;
                T.midRest = T.mid ? T.mid.position - root.position : Vector3.zero;
                T.hookRest = T.hook ? T.hook.position - root.position : Vector3.zero;
                airT.Add(T);
            }
        }

        void FallbackAirRob(Transform root, AirT T)
        {
            float Hm = MastH, cx = 0.30f, dz = (float)config.deckZ, tD = (float)config.toteD;
            CubeZ("Bogie", AirW - 0.02f, 0.34f, 0, 0, 0.055f, 0.21f, M.chassis, root);
            CubeZ("BogieStripe", AirW - 0.06f, 0.30f, 0, 0, 0.21f, 0.235f, M.accent, root);
            CubeZ("ControlBin", 0.30f, 0.20f, 0, -0.27f, 0.07f, 0.31f, M.airWhite, root);
            for (int sx = -1; sx <= 1; sx += 2) CubeZ("Column", 0.08f, 0.10f, sx * cx, 0, 0.235f, Hm - 0.10f, M.extrusion, root);
            CubeZ("Head", AirW - 0.04f, 0.22f, 0, 0, Hm - 0.10f, Hm - 0.02f, M.airWhite, root);
            var car = new GameObject("Carriage").transform; car.SetParent(root, false); car.localPosition = new Vector3(0, dz, 0);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                CubeZ("Block", 0.14f, 0.16f, sx * cx, 0, -0.07f, 0.37f, M.airWhite, car);
                CubeZ("TrayRail", 0.025f, 0.66f, sx * 0.17f, 0, -0.032f, -0.004f, M.rail, car);
            }
            CubeZ("Tray", 0.50f, 0.70f, 0, 0, -0.07f, -0.034f, M.chassis, car);
            CubeZ("Drive", 0.50f, 0.30f, 0, 0, 0.345f, 0.385f, M.airWhite, car);
            var mid = new GameObject("HookMid").transform; mid.SetParent(car, false);
            CubeZ("Stage", 0.10f, 0.50f, 0, 0, 0.322f, 0.340f, M.extrusion, mid);
            var hook = new GameObject("Hook").transform; hook.SetParent(car, false);
            CubeZ("Blade", 0.07f, 0.56f, 0, 0, 0.305f, 0.320f, M.groove, hook);
            for (int sy = -1; sy <= 1; sy += 2) CubeZ("Finger", 0.09f, 0.016f, 0, sy * (tD / 2 + 0.012f), 0.235f, 0.320f, M.accent, hook);
            T.car = car; T.mid = mid; T.hook = hook;
        }

        // ------------------------------------------------------------------ 바닥 이송 로봇
        void BuildBots()
        {
            var parent = new GameObject("FloorBots").transform;
            var prefab = Resources.Load<GameObject>("AirRobModels/airrob_floorbot");
            float dz = (float)config.deckZ;
            foreach (var r in Sim.robots)
            {
                var root = new GameObject("FloorBot_" + r.id).transform; root.SetParent(parent, false);
                if (prefab)
                {
                    var go = Instantiate(prefab, root, false);
                    go.transform.localPosition = Vector3.zero;
                    M.Remap(go);
                    FitScale(go, dz);
                }
                else
                {
                    CubeZ("Body", BotL, BotW, 0, 0, 0.035f, dz - 0.05f, M.botGrey, root);
                    CubeZ("Deck", BotL - 0.06f, BotW - 0.06f, 0, 0, dz - 0.05f, dz - 0.012f, M.chassis, root);
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        CubeZ("Bumper", 0.03f, BotW + 0.01f, sx * (BotL / 2 + 0.008f), 0, 0.05f, 0.16f, M.black, root);
                        CubeZ("LED", 0.004f, BotW - 0.08f, sx * (BotL / 2 + 0.025f), 0, 0.10f, 0.115f, M.led, root);
                    }
                }
                botT.Add(root); botYaw.Add(0f); botLast.Add(new Vector2((float)r.px, (float)r.py));
            }
        }

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
                var pf = Resources.Load<GameObject>("AirRobModels/airrob_tote_v" + v);
                GameObject go;
                if (pf)
                {
                    go = new GameObject("ToteTpl_" + v); go.transform.SetParent(holder, false);
                    var m = Instantiate(pf, go.transform, false); m.transform.localPosition = Vector3.zero;
                    M.Remap(m); FitScale(m, (float)config.toteH + 0.01f);
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
                Cube("WallFront", new Vector3(W, H, t), new Vector3(0, H / 2, s * (D / 2 - t / 2)), M.tote, go.transform);
                Cube("WallSide", new Vector3(t, H, D - 2 * t), new Vector3(s * (W / 2 - t / 2), H / 2, 0), M.tote, go.transform);
                Cube("Handle", new Vector3(0.16f, 0.035f, 0.004f), new Vector3(0, H - 0.06f, s * (D / 2 + 0.003f)), M.toteDark, go.transform, false);
                Cube("Label", new Vector3(0.10f, 0.05f, 0.002f), new Vector3(-0.10f, H - 0.13f, s * (D / 2 + 0.004f)), M.label, go.transform, false);
            }
            if (v > 0)
            {
                var rnd = new System.Random(11 + v); float z = -D / 2 + 0.03f;
                for (int k = 0; k < 3; k++)
                {
                    float sz = 0.11f + (float)rnd.NextDouble() * 0.05f, sy = 0.08f + (float)rnd.NextDouble() * H * 0.6f;
                    Cube("Item", new Vector3(W - 0.10f, sy, sz), new Vector3(0, t + sy / 2, z + sz / 2), M.items[(v + k) % M.items.Length], go.transform);
                    z += sz + 0.01f;
                }
            }
            return go;
        }

        // ------------------------------------------------------------------ 카메라 + 후처리 + 캡션
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.75f, 0.77f, 0.8f);
            Cam.nearClipPlane = 0.05f; Cam.farClipPlane = 120f;   // 깊이 정밀도 확보 (먼 바닥 표시 깜빡임 방지)
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<AirRobCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<AirRobCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.4f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.5f); ca.contrast.Override(10f); ca.saturation.Override(5f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.18f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(8f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.2f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.08f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof;

            if (showCaption)
            {
                var cap = Cam.GetComponent<AirRobCaption>();
                if (cap == null) cap = Cam.gameObject.AddComponent<AirRobCaption>();
                cap.cine = cine;
            }
        }

        // ------------------------------------------------------------------ HUD (녹화 중에는 숨김)
        void OnGUI()
        {
            if (!showHud || Sim == null || (AirRobMovieCapture.IsRecording && AirRobMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 400, 250), GUI.skin.box);
            GUILayout.Label($"<b>Libiao AirRob-style simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Aisles {config.aisles}  Levels {config.levels}  AirRobs {Sim.airs.Count}  Floor bots {Sim.robots.Count}  Totes {Sim.totes}");
            GUILayout.Label($"Throughput {Sim.ThroughputPerHour:0} totes/h   Done {Sim.statDone}   Backlog {Sim.Backlog}");
            GUILayout.Label($"Lead {Sim.AvgLead:0} s   AirRob util {Sim.AirUtil:P0}   Bot util {Sim.BotUtil:P0}   Station util {Sim.StationUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
