// 수평형 + 수직형 캐러셀: 시뮬레이션 실행 + 사실적 URP 장면 자동 구성 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/궤도/캐비닛/캐리어/트레이/피킹 라이트/조명/후처리/카메라/자막을 코드로 만듭니다.
// 모델: Assets/Carousel/Resources/CarouselModels/carousel_hcarrier_v0~2.fbx, carousel_vtray_v0~1.fbx (Blender 스크립트 출력). 없으면 기본 도형 사용.
// 좌표: 시뮬레이션 (x, y, z-up) → Unity (x, z, y). 치수는 blender/carousel_blender.py 와 같은 값.
// 얇은 판(라벨·LED·띠)은 붙는 면에서 2 mm 이상 띄워 z-fighting(깜빡임)이 생기지 않게 했습니다.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Carousel
{
    public class CarouselView : MonoBehaviour
    {
        [Header("Simulation")]
        public CarouselConfig config = new CarouselConfig();
        [Tooltip("시작 전에 미리 돌려 운영 중인 상태로 만드는 시간(초). Blender 궤적과 같게 하려면 300")]
        public float warmupSeconds = 300f;
        [Range(0f, 10f)] public float simSpeed = 1f;
        [Tooltip("시뮬레이션 스텝 (tools/export_carousel_trace.js 와 같은 1/60 s)")]
        public float subStep = 1f / 60f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;
        public bool showCaptions = true;

        public CarouselSim Sim { get; private set; }
        public Camera Cam { get; private set; }
        // 배치 게임 모듈 모드: 건물·조명·카메라 없이 설비만 만들고, 옮겨 놓은 위치(transform.position)로 동기화
        [System.NonSerialized] public bool moduleMode;
        public static Vector3 UOff;   // U() 가 더하는 위치. 모듈 동기화 중에만 0 이 아님

        const float GAP = 0.002f;

        readonly List<List<Transform>> hCar = new List<List<Transform>>();
        readonly List<Transform> vTray = new List<Transform>();
        readonly List<List<GameObject>> hLed = new List<List<GameObject>>();
        readonly List<GameObject> hPut = new List<GameObject>(), vLed = new List<GameObject>(), vPut = new List<GameObject>();
        readonly Transform[] item = new Transform[2];
        Mats M;
        Mesh cubeMesh, cylMesh;
        double acc;

        // ------------------------------------------------------------------ 좌표 / 치수 (blender 와 같은 식)
        public static Vector3 U(double x, double y, double z) => UOff + new Vector3((float)x, (float)z, (float)y);
        float HShelfZ(int l) => (float)(config.hShelf0 + l * config.hShelfH);
        float HBinX(int b) => (float)((b - (config.hBins - 1) / 2.0) * (config.hW - 0.02) / config.hBins);
        float VBinX(int b) => (float)(-config.vW / 2 + 0.05 + (b + 0.5) * (config.vW - 0.10) / config.vBins);
        Vector3 HSlot(int s) { int col = s % 4, row = s / 4; return new Vector3((float)(config.hx[0] + config.hx[1]) / 2 + (col - 1.5f) * 0.50f, -1.60f, 0.80f + row * 0.42f); }
        Vector3 VSlot(int s) => new Vector3((float)config.vx + (s - 1.5f) * 0.50f, -1.35f, 0.86f);
        float VTop => (float)(Sim.vZt + config.vR + config.vH / 2 + 0.25);
        float VXi => (float)(config.vW / 2 + 0.14);
        float VYb => (float)(Sim.vBackY + config.vD / 2 + 0.09);

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            Sim = new CarouselSim(config);
            int n = Mathf.RoundToInt(warmupSeconds / subStep);
            for (int i = 0; i < n; i++) Sim.Step(subStep);
            M = new Mats();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylMesh = tmp.GetComponent<MeshFilter>().sharedMesh; Destroy(tmp);
            if (!moduleMode) BuildEnvironment();
            BuildHorizontal();
            BuildVertical();
            BuildCarriers();
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
            for (int k = 0; k < c.hCount; k++)
                for (int i = 0; i < hCar[k].Count; i++)
                {
                    Sim.HCarrierPose(k, i, out double x, out double y, out double a);
                    hCar[k][i].SetPositionAndRotation(U(x, y, 0), Quaternion.Euler(0, -(float)(a - Mathf.PI / 2) * Mathf.Rad2Deg, 0));
                }
            for (int i = 0; i < vTray.Count; i++)
            {
                Sim.VCarrierPose(i, out double y, out double z);
                vTray[i].position = U(c.vx, y, z);
            }
            for (int oi = 0; oi < 2; oi++)
            {
                var op = Sim.ops[oi]; var ln = op.line; bool on = op.state == 2 && ln != null;
                float u = (float)Sim.OpProgress(op);
                if (oi == 0)
                {
                    for (int k = 0; k < c.hCount; k++)
                        for (int l = 0; l < c.hShelves; l++) hLed[k][l].SetActive(on && ln.unit == k && ln.shelf == l && u < 0.55f);
                    for (int s = 0; s < hPut.Count; s++) hPut[s].SetActive(on && Sim.orders[ln.order].slot == s && u > 0.45f);
                }
                else
                {
                    for (int b = 0; b < vLed.Count; b++) vLed[b].SetActive(on && ln.bin == b && u < 0.55f);
                    for (int s = 0; s < vPut.Count; s++) vPut[s].SetActive(on && Sim.orders[ln.order].slot == s && u > 0.45f);
                }
                bool show = on && u > 0.18f && u < 0.82f;
                item[oi].gameObject.SetActive(show);
                if (!show) continue;
                Vector3 src, dst;
                int slot = Sim.orders[ln.order].slot;
                if (oi == 0)
                {
                    Sim.HCarrierPose(ln.unit, ln.carrier, out double x, out double y, out double a);
                    double rz = a - Mathf.PI / 2, lx = HBinX(ln.bin);
                    src = new Vector3((float)(x + lx * System.Math.Cos(rz)), (float)(y + lx * System.Math.Sin(rz)), HShelfZ(ln.shelf) + 0.10f);
                    dst = HSlot(slot);
                }
                else
                {
                    Sim.VCarrierPose(ln.carrier, out double y, out double z);
                    src = new Vector3((float)c.vx + VBinX(ln.bin), (float)y, (float)(z - c.vH / 2 + 0.12));
                    dst = VSlot(slot);
                }
                dst.z += 0.10f;
                float e = Mathf.SmoothStep(0, 1, Mathf.Clamp01((u - 0.22f) / 0.58f));
                var p = Vector3.Lerp(src, dst, e); p.z += 0.28f * Mathf.Sin(Mathf.PI * e);
                item[oi].position = U(p.x, p.y, p.z);
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material frame, wire, track, beam, binBlue, binGrey, binYellow, label, panel, panelDark, accent, glass, tray, rubber, chain,
                black, ledOff, ledOn, screen, alu, yellow, mat, tote, floor, wall, ceiling, steel, lightPanel, green;
            public Material[] items;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                frame = Coat("FramePowder", new Color(0.80f, 0.81f, 0.82f), 0.62f);
                wire = Lit("WireZinc", new Color(0.72f, 0.73f, 0.74f), 1f, 0.68f);
                track = Lit("TrackSteel", new Color(0.42f, 0.43f, 0.45f), 1f, 0.6f);
                beam = Coat("BeamBlue", new Color(0.05f, 0.20f, 0.45f), 0.6f);
                binBlue = Lit("BinBlue", new Color(0.03f, 0.17f, 0.48f), 0f, 0.55f);
                binGrey = Lit("BinGrey", new Color(0.30f, 0.31f, 0.33f), 0f, 0.55f);
                binYellow = Lit("BinYellow", new Color(0.85f, 0.60f, 0.04f), 0f, 0.55f);
                label = Lit("BinLabel", new Color(0.95f, 0.95f, 0.93f), 0f, 0.45f);
                panel = Coat("CabinetWhite", new Color(0.88f, 0.89f, 0.90f), 0.7f);
                panelDark = Lit("CabinetGraphite", new Color(0.13f, 0.14f, 0.15f), 0.2f, 0.6f);
                accent = Coat("AccentBlue", new Color(0.0f, 0.30f, 0.65f), 0.65f);
                glass = Glass("Glass");
                tray = Lit("TrayAlu", new Color(0.74f, 0.75f, 0.77f), 1f, 0.7f);
                rubber = Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                chain = Lit("ChainSteel", new Color(0.25f, 0.26f, 0.27f), 1f, 0.65f);
                black = Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.5f);
                ledOff = Lit("LEDOff", new Color(0.05f, 0.08f, 0.06f), 0f, 0.85f);
                ledOn = Emissive("LEDGreen", new Color(0.1f, 1f, 0.35f), 6f);
                Emissive("LEDAmber", new Color(1f, 0.55f, 0.05f), 5f);
                Emissive("LEDRed", new Color(1f, 0.1f, 0.05f), 4f);
                screen = Emissive("Screen", new Color(0.35f, 0.6f, 1f), 1.5f);
                alu = Lit("Aluminium", new Color(0.78f, 0.79f, 0.80f), 1f, 0.72f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                mat = Lit("FloorMat", new Color(0.06f, 0.06f, 0.065f), 0f, 0.1f);
                tote = Lit("ToteGrey", new Color(0.20f, 0.21f, 0.22f), 0f, 0.52f);
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
                m.SetFloat("_ClearCoatMask", 0.5f); m.SetFloat("_ClearCoatSmoothness", 0.9f);
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

            // 투명 유리 (URP Lit Transparent)
            Material Glass(string name)
            {
                var m = Lit(name, new Color(0.85f, 0.92f, 0.95f, 0.10f), 0f, 0.96f);
                m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
                return m;
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
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
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
            // 시뮬레이션 좌표 박스 (크기 sx, sy, sz / 중심 x, y, z / z 축 회전 rad)
            public void B(float sx, float sy, float sz, float x, float y, float z, Material m, float rot = 0f) =>
                Add(cube, Matrix4x4.TRS(new Vector3(x, z, y), Quaternion.Euler(0, -rot * Mathf.Rad2Deg, 0), new Vector3(sx, sz, sy)), m);
            public void Cy(float r, float len, float x, float y, float z, char axis, Material m)
            {
                var rot = axis == 'Z' ? Quaternion.identity : axis == 'X' ? Quaternion.Euler(0, 0, 90) : Quaternion.Euler(90, 0, 0);
                Add(cyl, Matrix4x4.TRS(new Vector3(x, z, y), rot, new Vector3(2 * r, len / 2, 2 * r)), m);
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
                    mr.shadowCastingMode = shadows && kv.Key.renderQueue < 3000 ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
                EquipFX.MeshFix.InflateByArea(built);   // 겹친 면 깜빡임 방지: 작은 재질(표시선·LED 등)을 살짝 앞으로
                return root;
            }
        }

        // 켜진 LED: 꺼진 LED 앞 1 mm 에 놓은 발광 상자. SetActive 로 켜고 끔
        GameObject Led(string name, Transform parent, float sx, float sy, float sz, float x, float y, float z)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name; Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = U(x, y, z); g.transform.localScale = new Vector3(sx, sz, sy);
            var mr = g.GetComponent<MeshRenderer>(); mr.sharedMaterial = M.ledOn; mr.shadowCastingMode = ShadowCastingMode.Off;
            g.SetActive(false);
            return g;
        }

        static void Tote(Batch b, Mats M, Vector3 c, float w, float d, float h)
        {
            float t = 0.008f;
            b.B(w, d, t, c.x, c.y, c.z + t / 2, M.tote);
            for (int sy = -1; sy <= 1; sy += 2) b.B(w, t, h, c.x, c.y + sy * (d / 2 - t / 2), c.z + h / 2, M.tote);
            for (int sx = -1; sx <= 1; sx += 2) b.B(t, d - 2 * t, h, c.x + sx * (w / 2 - t / 2), c.y, c.z + h / 2, M.tote);
        }

        // ------------------------------------------------------------------ 건물 (밝은 물류센터)
        void BuildEnvironment()
        {
            const float cxw = 3.6f, cyw = 3.0f, S = 50f, Hh = 9f;
            var env = new GameObject("Building").transform;
            var b = new Batch(cubeMesh, cylMesh);
            b.B(S, S, 0.02f, cxw, cyw, -0.01f, M.floor);
            b.B(S * 0.8f, 0.12f, 0.004f, cxw, -3.2f, 0.002f, M.green);
            b.B(S, 0.2f, Hh, cxw, cyw + 14, Hh / 2, M.wall);
            b.B(0.2f, S, Hh, cxw - 14, cyw, Hh / 2, M.wall);
            b.B(0.2f, S, Hh, cxw + 16, cyw, Hh / 2, M.wall);
            b.B(S, 0.2f, Hh, cxw, cyw - 18, Hh / 2, M.wall);
            b.B(S, S, 0.2f, cxw, cyw, Hh, M.ceiling);
            for (int k = -12; k < 16; k += 4) b.B(0.3f, S, 0.5f, cxw + k, cyw, Hh - 0.35f, M.steel);
            b.B(S, 0.5f, 0.06f, cxw, cyw + 13.8f, 1.2f, M.green);
            for (int k = -12; k < 16; k += 7) b.B(0.4f, 0.4f, Hh, cxw + k, cyw + 13.7f, Hh / 2, M.steel);
            for (int ix = -2; ix <= 2; ix++)
                for (int iy = -2; iy <= 2; iy++)
                    b.B(1.8f, 0.45f, 0.05f, cxw + ix * 4.5f, cyw + iy * 4.5f, Hh - 0.7f, M.lightPanel);
            b.Build("Shell", env);
            int shadowed = 0;
            for (int ix = -1; ix <= 1; ix++)
                for (int iy = -1; iy <= 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(cxw + ix * 5f, cyw + iy * 4.5f, Hh - 0.8f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 140; l.innerSpotAngle = 70;
                    l.range = 30; l.intensity = 18f; l.color = new Color(1f, 0.98f, 0.95f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.0f; sun.color = new Color(1f, 0.98f, 0.95f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.75f; sun.shadowBias = 0.02f; sun.shadowNormalBias = 0.3f;
            sun.transform.rotation = Quaternion.Euler(58, -30, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.80f, 0.82f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.67f, 0.69f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.45f, 0.46f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(cxw, cyw, 3f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(S, Hh * 2, S); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 수평형: 궤도, 등뼈 빔, 구동부, 피킹 라이트, 주문 선반
        void BuildHorizontal()
        {
            var c = config;
            var root = new GameObject("HorizontalCarousels").transform;
            var b = new Batch(cubeMesh, cylMesh);
            for (int k = 0; k < c.hCount; k++)
            {
                float x0 = (float)c.hx[k], y0 = (float)Sim.hY0, L = (float)Sim.hL, r = (float)c.hR, D = (float)c.hD;
                float ztr = (float)c.hTopZ + 0.035f;
                int n = (int)(Sim.hPer / 0.08);
                float seg = (float)Sim.hPer / n;
                for (int i = 0; i < n; i++)
                {
                    Sim.HPath(k, (i + 0.5) * seg, out double x, out double y, out double a);
                    float px = (float)(x + System.Math.Cos(a) * 0.03), py = (float)(y + System.Math.Sin(a) * 0.03), rot = (float)a + Mathf.PI / 2;
                    b.B(seg + 0.004f, 0.07f, 0.05f, px, py, ztr, M.track, rot);
                    b.B(seg + 0.004f, 0.06f, 0.012f, px, py, 0.006f, M.track, rot);
                }
                b.B(0.10f, L + 0.2f, 0.12f, x0, y0 + L / 2, ztr + 0.10f, M.beam);
                for (int j = 0; j < 5; j++)
                {
                    float y = y0 + L * j / 4f;
                    b.B(0.09f, 0.09f, ztr + 0.04f, x0, y, (ztr + 0.04f) / 2, M.beam);
                    b.B(2 * r - 0.04f, 0.05f, 0.05f, x0, y, ztr + 0.06f, M.beam);
                    b.B(0.30f, 0.30f, 0.012f, x0, y, 0.006f, M.steel);
                }
                for (int j = 0; j < 9; j++) b.B(2 * r - 0.02f, 0.03f, 0.03f, x0, y0 + L * j / 8f, ztr + 0.055f, M.track);
                b.B(0.42f, 0.42f, 0.30f, x0, y0 + L, ztr + 0.31f, M.panelDark);
                b.Cy(0.10f, 0.20f, x0, y0 + L, ztr + 0.55f, 'Z', M.steel);
                float e = r + D + 0.15f;
                for (int sx = -1; sx <= 1; sx += 2) b.B(0.06f, L + 2 * e, 0.003f, x0 + sx * e, y0 + L / 2, 0.0015f, M.yellow);
                b.B(2 * e + 0.06f, 0.06f, 0.003f, x0, y0 + L + e, 0.0015f, M.yellow);
                // 피킹 라이트 기둥
                int side = k == 0 ? 1 : -1;
                float lx = x0 + side * 0.42f, ly = -0.12f;
                b.B(0.05f, 0.05f, 2.35f, lx, ly, 1.175f, M.panelDark);
                b.B(0.18f, 0.05f, 0.10f, lx, ly - 0.03f, 2.30f, M.black);
                b.B(0.15f, GAP, 0.06f, lx, ly - 0.056f - GAP / 2, 2.30f, M.screen);
                var row = new List<GameObject>();
                for (int l = 0; l < c.hShelves; l++)
                {
                    float z = HShelfZ(l) + 0.10f;
                    b.B(0.08f, 0.03f, 0.05f, lx, ly - 0.04f, z, M.black);
                    b.B(0.05f, GAP, 0.03f, lx, ly - 0.056f - GAP / 2, z, M.ledOff);
                    row.Add(Led($"HLed_{k}_{l}", root, 0.052f, 0.004f, 0.032f, lx, ly - 0.059f, z));
                }
                hLed.Add(row);
            }
            // 작업자 매트 + 주문 선반 (put wall)
            float cx = (float)(c.hx[0] + c.hx[1]) / 2;
            b.B(3.4f, 1.0f, 0.012f, cx, -0.85f, 0.006f, M.mat);
            for (int r_ = 0; r_ < 2; r_++) b.B(2.1f, 0.46f, 0.025f, cx, -1.60f, 0.80f + r_ * 0.42f - 0.0125f - GAP, M.alu);
            foreach (float xx in new[] { cx - 1.05f, cx + 1.05f })
                foreach (float yy in new[] { -1.83f, -1.37f }) b.B(0.035f, 0.035f, 1.35f, xx, yy, 0.675f, M.alu);
            for (int s = 0; s < c.hSlots; s++)
            {
                var p = HSlot(s);
                Tote(b, M, p, 0.40f, 0.36f, 0.22f);
                b.B(0.06f, GAP, 0.02f, p.x, p.y + 0.23f + GAP / 2 + 0.002f, p.z - 0.04f, M.ledOff);
                hPut.Add(Led("HPut_" + s, root, 0.062f, 0.004f, 0.022f, p.x, p.y + 0.237f, p.z - 0.04f));
            }
            b.Build("Structure", root);
        }

        // ------------------------------------------------------------------ 수직형 캐비닛
        void BuildVertical()
        {
            var c = config;
            var root = new GameObject("VerticalCarousel").transform;
            var b = new Batch(cubeMesh, cylMesh);
            float x0 = (float)c.vx, W = (float)c.vW, xi = VXi, yb = VYb, top = VTop;
            float zw0 = (float)(c.vWinZ - c.vH / 2 - 0.05), zw1 = (float)(c.vWinZ + c.vH / 2 + 0.10);
            for (int sx = -1; sx <= 1; sx += 2)
                foreach (float y in new[] { 0f, yb }) b.B(0.08f, 0.08f, top, x0 + sx * xi, y, top / 2, M.panelDark);
            b.B(2 * xi + 0.08f, yb + 0.08f, 0.10f, x0, yb / 2, 0.05f, M.panelDark);
            b.B(2 * xi + 0.08f, yb + 0.08f, 0.08f, x0, yb / 2, top + 0.04f, M.panel);
            float fw = 2 * xi - 0.08f;
            b.B(fw, 0.02f, zw0 - 0.10f, x0, 0f, 0.10f + (zw0 - 0.10f) / 2, M.panel);
            // 작업창 위 앞판 + 점검 유리창
            float gz0 = zw1 + 0.35f, gz1 = top - 0.75f, gw = fw - 0.50f;
            b.B(fw, 0.02f, gz0 - zw1, x0, 0f, (zw1 + gz0) / 2, M.panel);
            b.B(fw, 0.02f, top - gz1, x0, 0f, (gz1 + top) / 2, M.panel);
            for (int sx = -1; sx <= 1; sx += 2) b.B((fw - gw) / 2, 0.02f, gz1 - gz0, x0 + sx * (gw + fw) / 4, 0f, (gz0 + gz1) / 2, M.panel);
            b.B(gw, 0.008f, gz1 - gz0, x0, 0f, (gz0 + gz1) / 2, M.glass);
            for (float z = gz0 + 1.25f; z < gz1 - 0.3f; z += 1.25f) b.B(gw, 0.03f, 0.03f, x0, -0.02f, z, M.panelDark);
            foreach (float zz in new[] { gz0, gz1 }) b.B(gw + 0.06f, 0.03f, 0.04f, x0, -0.02f, zz, M.panelDark);
            for (int sx = -1; sx <= 1; sx += 2) b.B(0.04f, 0.03f, gz1 - gz0, x0 + sx * (gw / 2 + 0.01f), -0.02f, (gz0 + gz1) / 2, M.panelDark);
            b.B(fw, GAP, 0.08f, x0, -0.01f - GAP / 2 - 0.001f, top - 0.40f, M.accent);
            // 작업창 카운터 + 라이트 바
            b.B(fw + 0.04f, 0.24f, 0.03f, x0, -0.10f, zw0 - 0.015f, M.panelDark);
            b.B(fw, 0.06f, 0.05f, x0, -0.03f, zw1 + 0.025f + GAP, M.panelDark);
            for (int bi = 0; bi < c.vBins; bi++)
            {
                float x = x0 + VBinX(bi);
                b.B(0.10f, GAP, 0.025f, x, -0.06f - GAP / 2 - 0.001f, zw1 + 0.025f + GAP, M.ledOff);
                vLed.Add(Led("VLed_" + bi, root, 0.102f, 0.004f, 0.027f, x, -0.065f, zw1 + 0.025f + GAP));
            }
            // 조작반
            b.B(0.05f, 0.05f, 0.55f, x0 + xi + 0.12f, -0.05f, zw1 + 0.05f, M.alu);
            b.B(0.36f, 0.04f, 0.26f, x0 + xi + 0.12f, -0.10f, zw1 + 0.38f, M.black);
            b.B(0.33f, GAP, 0.23f, x0 + xi + 0.12f, -0.12f - GAP / 2 - 0.001f, zw1 + 0.38f, M.screen);
            // 뒤판 + 옆 유리
            b.B(fw, 0.02f, top - 0.10f, x0, yb, 0.10f + (top - 0.10f) / 2, M.panel);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                b.B(0.012f, yb - 0.08f, top - 0.30f, x0 + sx * xi, yb / 2, 0.10f + (top - 0.30f) / 2 + 0.05f, M.glass);
                foreach (float z in new[] { 0.12f, top - 0.10f }) b.B(0.05f, yb - 0.08f, 0.06f, x0 + sx * xi, yb / 2, z, M.panelDark);
                foreach (float fr in new[] { 1f / 3, 2f / 3 }) b.B(0.05f, yb - 0.08f, 0.04f, x0 + sx * xi, yb / 2, 0.12f + fr * (top - 0.22f), M.panelDark);
                // 체인 + 스프로킷
                float xc = x0 + sx * (W / 2 + 0.06f);
                foreach (float y in new[] { (float)c.vFrontY, (float)Sim.vBackY }) b.B(0.02f, 0.03f, (float)Sim.vL, xc, y, (float)(c.vZb + Sim.vL / 2), M.chain);
                foreach (float zc in new[] { (float)c.vZb, (float)Sim.vZt })
                {
                    b.Cy((float)c.vR - 0.03f, 0.02f, xc, (float)(c.vFrontY + Sim.vBackY) / 2, zc, 'X', M.chain);
                    b.Cy(0.05f, 0.08f, xc, (float)(c.vFrontY + Sim.vBackY) / 2, zc, 'X', M.track);
                }
            }
            // 작업자 매트 + 주문 카트
            b.B(2.6f, 0.9f, 0.012f, x0, -0.70f, 0.006f, M.mat);
            b.B(2.1f, 0.46f, 0.025f, x0, -1.35f, 0.86f - 0.0125f - GAP, M.alu);
            b.B(2.1f, 0.46f, 0.025f, x0, -1.35f, 0.30f, M.alu);
            foreach (float xx in new[] { x0 - 1.0f, x0 + 1.0f })
                foreach (float yy in new[] { -1.55f, -1.15f })
                {
                    b.B(0.035f, 0.035f, 0.95f, xx, yy, 0.535f, M.alu);
                    b.Cy(0.05f, 0.03f, xx, yy, 0.05f, 'X', M.rubber);
                }
            for (int s = 0; s < c.vSlots; s++)
            {
                var p = VSlot(s);
                Tote(b, M, p, 0.40f, 0.36f, 0.22f);
                b.B(0.06f, GAP, 0.02f, p.x, p.y + 0.23f + GAP / 2 + 0.002f, p.z - 0.04f, M.ledOff);
                vPut.Add(Led("VPut_" + s, root, 0.062f, 0.004f, 0.022f, p.x, p.y + 0.237f, p.z - 0.04f));
            }
            b.Build("Cabinet", root);
        }

        // ------------------------------------------------------------------ 캐리어 / 트레이 / 옮겨지는 물건
        void BuildCarriers()
        {
            var c = config;
            var holder = new GameObject("Templates").transform; holder.gameObject.SetActive(false);
            var hT = new List<GameObject>(); var vT = new List<GameObject>();
            for (int v = 0; v < 3; v++) hT.Add(Template("CarouselModels/carousel_hcarrier_v" + v, holder, true, v));
            for (int v = 0; v < 2; v++) vT.Add(Template("CarouselModels/carousel_vtray_v" + v, holder, false, v));
            var parent = new GameObject("Carriers").transform;
            var rnd = new System.Random(5);
            for (int k = 0; k < c.hCount; k++)
            {
                var list = new List<Transform>();
                for (int i = 0; i < c.hCarriers; i++)
                {
                    var go = Instantiate(hT[rnd.Next(3)], parent, false); go.name = $"HCarrier_{k}_{i}"; go.SetActive(true);
                    list.Add(go.transform);
                }
                hCar.Add(list);
            }
            for (int i = 0; i < c.vCarriers; i++)
            {
                var go = Instantiate(vT[i % 2], parent, false); go.name = "VTray_" + i; go.SetActive(true);
                vTray.Add(go.transform);
            }
            for (int oi = 0; oi < 2; oi++)
            {
                var it = new GameObject(oi == 0 ? "PickItem_H" : "PickItem_V").transform; it.SetParent(parent, false);
                var b = new Batch(cubeMesh, cylMesh);
                b.B(0.14f, 0.12f, 0.09f, 0, 0, 0, M.items[3]);
                b.B(0.06f, GAP, 0.03f, 0, -0.061f - GAP / 2, 0.01f, M.label);
                b.Build("Mesh", it);
                item[oi] = it; it.gameObject.SetActive(false);
            }
        }

        // FBX 템플릿 (라벨이 있는 쪽이 앞인지 확인해 FBX 축 변환 차이를 바로잡음). 없으면 기본 도형.
        GameObject Template(string res, Transform holder, bool horizontal, int v)
        {
            var root = new GameObject(System.IO.Path.GetFileName(res)); root.transform.SetParent(holder, false);
            var pf = Resources.Load<GameObject>(res);
            if (pf)
            {
                var m = Instantiate(pf, root.transform, false); m.transform.localPosition = Vector3.zero;
                M.Remap(m);
                FitHeight(m, horizontal ? (float)config.hTopZ : (float)config.vH);
                // 수평형: 빈 앞(라벨)이 +z (시뮬 +y = 바깥), 수직형: 작업창 쪽 -z
                float z = LabelMeanZ(m);
                if (!float.IsNaN(z) && Mathf.Sign(z) != (horizontal ? 1f : -1f)) m.transform.localRotation = Quaternion.Euler(0, 180, 0) * m.transform.localRotation;
                EquipFX.MeshFix.ResolveModel(m);   // 모델 안 겹친 면 분리 (깜빡임 방지)
            }
            else if (horizontal) FallbackHCarrier(root.transform, v);
            else FallbackVTray(root.transform, v);
            return root;
        }

        // FBX 단위가 어긋나면 높이로 보정 (비활성 상태에서도 되도록 메쉬 bounds 사용)
        static void FitHeight(GameObject go, float targetH)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    float y = go.transform.InverseTransformPoint(mf.transform.TransformPoint(c)).y;
                    lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y);
                }
            }
            if (hi <= lo) return;
            float sc = targetH / (hi - lo);
            if (sc < 0.8f || sc > 1.25f) go.transform.localScale *= sc;
        }

        static float LabelMeanZ(GameObject go)
        {
            double sum = 0; int n = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh; var mr = mf.GetComponent<MeshRenderer>();
                if (mesh == null || mr == null || !mesh.isReadable) continue;
                var mats = mr.sharedMaterials; var verts = mesh.vertices;
                for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                {
                    if (mats[s] == null || !mats[s].name.StartsWith("BinLabel")) continue;
                    foreach (int idx in mesh.GetIndices(s))
                    {
                        var p = go.transform.InverseTransformPoint(mf.transform.TransformPoint(verts[idx]));
                        sum += p.z; n++;
                    }
                }
            }
            return n > 0 ? (float)(sum / n) : float.NaN;
        }

        void FallbackHCarrier(Transform root, int v)
        {
            var c = config; float W = (float)c.hW, D = (float)c.hD;
            var rnd = new System.Random(11 + v);
            var b = new Batch(cubeMesh, cylMesh);
            float zt = HShelfZ(c.hShelves - 1) + 0.37f;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2) b.B(0.022f, 0.022f, zt - 0.10f, sx * (W / 2 - 0.011f), sy * (D / 2 - 0.011f), 0.10f + (zt - 0.10f) / 2, M.frame);
            b.B(W, D, 0.03f, 0, 0, zt + 0.015f, M.frame);
            b.B(0.04f, 0.04f, (float)c.hTopZ - zt - 0.03f, 0, -D / 2 + 0.03f, (zt + 0.03f + (float)c.hTopZ) / 2, M.track);
            var bins = new[] { M.binBlue, M.binBlue, M.binGrey, M.binYellow };
            for (int l = 0; l < c.hShelves; l++)
            {
                float z = HShelfZ(l);
                b.B(W - 0.02f, D - 0.02f, 0.012f, 0, 0, z - 0.006f, M.wire);
                var bm = bins[(l + 11 + v) % bins.Length];
                for (int bi = 0; bi < c.hBins; bi++)
                {
                    float bw = (W - 0.04f) / c.hBins - 0.008f, bd = D - 0.06f, bh = 0.17f, x = HBinX(bi), z0 = z + GAP;
                    b.B(bw, bd, 0.006f, x, 0, z0 + 0.003f, bm);
                    b.B(bw, 0.006f, bh, x, -bd / 2 + 0.003f, z0 + bh / 2, bm);
                    for (int sx = -1; sx <= 1; sx += 2) b.B(0.006f, bd - 0.012f, bh * 0.9f, x + sx * (bw / 2 - 0.003f), 0, z0 + bh * 0.45f, bm);
                    b.B(bw, 0.006f, bh * 0.5f, x, bd / 2 - 0.003f, z0 + bh * 0.25f, bm);
                    b.B(bw * 0.4f, GAP, bh * 0.22f, x, bd / 2 + GAP / 2 + 0.001f, z0 + bh * 0.27f, M.label);
                    float sz = bh * (0.35f + (float)rnd.NextDouble() * 0.5f);
                    b.B(bw - 0.03f, bd * 0.6f, sz, x, -bd * 0.15f, z0 + 0.008f + sz / 2, M.items[rnd.Next(M.items.Length)]);
                }
            }
            b.Build("Fallback", root);
        }

        void FallbackVTray(Transform root, int v)
        {
            var c = config; float W = (float)c.vW, D = (float)c.vD, H = (float)c.vH, zf = -H / 2 + 0.04f;
            var rnd = new System.Random(21 + v);
            var b = new Batch(cubeMesh, cylMesh);
            b.B(W, D, 0.02f, 0, 0, zf - 0.01f, M.tray);
            b.B(W, 0.015f, 0.10f, 0, D / 2 - 0.0075f, zf + 0.05f + GAP, M.tray);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                b.B(0.015f, 0.05f, H - 0.02f, sx * (W / 2 + 0.0075f + GAP), 0, 0, M.frame);
                b.B(0.015f, D, 0.03f, sx * (W / 2 + 0.0075f + GAP), 0, zf - 0.005f, M.frame);
            }
            for (int bi = 0; bi < c.vBins; bi++)
            {
                float bw = (W - 0.10f) / c.vBins - 0.02f, bd = D - 0.08f, bh = 0.15f, x = VBinX(bi), z0 = zf + GAP;
                var bm = (bi + 21 + v) % 5 != 0 ? M.binBlue : M.binGrey;
                b.B(bw, bd, 0.006f, x, 0, z0 + 0.003f, bm);
                b.B(bw, 0.006f, bh, x, bd / 2 - 0.003f, z0 + bh / 2, bm);
                for (int sx = -1; sx <= 1; sx += 2) b.B(0.006f, bd - 0.012f, bh * 0.9f, x + sx * (bw / 2 - 0.003f), 0, z0 + bh * 0.45f, bm);
                b.B(bw, 0.006f, bh * 0.5f, x, -bd / 2 + 0.003f, z0 + bh * 0.25f, bm);
                b.B(bw * 0.4f, GAP, bh * 0.22f, x, -bd / 2 - GAP / 2 - 0.001f, z0 + bh * 0.27f, M.label);
                float sz = bh * (0.35f + (float)rnd.NextDouble() * 0.5f);
                b.B(bw - 0.03f, bd * 0.6f, sz, x, bd * 0.15f, z0 + 0.008f + sz / 2, M.items[rnd.Next(M.items.Length)]);
            }
            b.Build("Fallback", root);
        }

        // ------------------------------------------------------------------ 카메라 + 후처리 + 자막
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.75f, 0.77f, 0.8f);
            // near 를 너무 작게 잡으면 깊이 정밀도가 떨어져 먼 면이 깜빡임 → 0.05 m
            Cam.nearClipPlane = 0.05f; Cam.farClipPlane = 120f;
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            Cam.cullingMask &= ~(1 << CarouselCaption.UiLayer);
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<CarouselCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<CarouselCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.35f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.35f); ca.contrast.Override(12f); ca.saturation.Override(4f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.18f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(6f); dof.focalLength.Override(26f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.25f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.06f);
            var wb = prof.Add<WhiteBalance>(true); wb.temperature.Override(-3f);
            vol.sharedProfile = prof;
            cine.dof = dof;

            if (showCaptions)
            {
                var cap = gameObject.GetComponent<CarouselCaption>();
                if (cap == null) cap = gameObject.AddComponent<CarouselCaption>();
                cap.Init(Cam, cine);
            }
        }

        // ------------------------------------------------------------------ HUD (녹화에는 안 들어감)
        void OnGUI()
        {
            if (!showHud || Sim == null || (CarouselMovieCapture.IsRecording && CarouselMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 420, 210), GUI.skin.box);
            GUILayout.Label($"<b>Carousel picking simulation</b>  t = {Sim.t:0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Horizontal pod: {Sim.HLinesPerHour:0} lines/h  lead {Sim.HLead:0} s  operator {Sim.HOpUtil:P0}  carousel {Sim.HCarouselUtil:P0}");
            GUILayout.Label($"Vertical: {Sim.VLinesPerHour:0} lines/h  lead {Sim.VLead:0} s  operator {Sim.VOpUtil:P0}  carousel {Sim.VCarouselUtil:P0}");
            GUILayout.Label($"Speed x{simSpeed:0.0}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0f, 10f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
