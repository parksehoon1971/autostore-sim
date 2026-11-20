// 반자동 제함기 → 충전·상부 접지 컨베이어 → 반자동 봉함기 포장 라인: 장면 자동 구성 + 동작 재생 + HUD
// 빈 씬에 이 컴포넌트 하나만 있으면 건물/조명/후처리/카메라를 코드로 만들고,
// 설비(제함기·흡착 이송 암·컨베이어·충전 슈트·접지 장치·봉함기)는 Blender 출력 FBX 를 불러옵니다:
//   Assets/CaseLine/Resources/CaseLineModels/caseline_equipment.fbx  (blender/caseline_blender.py)
// 박스(벽 4 + 날개 8 + 테이프)와 상품은 CaseLineKinematics 로 매 프레임 자세를 계산합니다 (Blender 와 같은 식).
// 좌표: Blender (x, y, z-up) → Unity (x, z, y).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CaseLine
{
    public class CaseLineView : MonoBehaviour
    {
        [Header("Playback")]
        [Range(0f, 4f)] public float speed = 1f;
        [Tooltip("시작 시각 (초). 30초 영상은 0 에서 시작")]
        public float startTime = 0f;

        [Header("Presentation")]
        public bool cinematicCamera = true;
        public bool showHud = true;

        public Camera Cam { get; private set; }
        public double T { get; private set; }

        Mats M;
        Transform equip, pusher, arm, carriage, kicker;
        Quaternion kickerRest;
        readonly List<Transform> rollers = new List<Transform>();
        readonly List<Quaternion> rollerRest = new List<Quaternion>();
        readonly Dictionary<long, BoxRig> boxes = new Dictionary<long, BoxRig>();
        Transform boxParent;
        double clock;

        class BoxRig
        {
            public GameObject root;
            public Transform[] slabs = new Transform[12];
            public Transform[] tape = new Transform[6];
            public Transform[] packs = new Transform[8];
        }

        public static Vector3 U(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);
        public static Vector3 U(D3 v) => U(v.x, v.y, v.z);

        // ------------------------------------------------------------------ 시작
        void Start()
        {
            M = new Mats();
            BuildEnvironment();
            LoadEquipment();
            boxParent = new GameObject("Boxes").transform;
            BuildCameraAndPost();
            ResetClock();
            Apply();
            var probe = FindFirstObjectByType<ReflectionProbe>();
            if (probe) probe.RenderProbe();
        }

        public void ResetClock() { clock = startTime; }

        void Update()
        {
            clock += Time.deltaTime * speed;
            Apply();
        }

        // ------------------------------------------------------------------ 동작 적용
        void Apply()
        {
            double t = clock; T = t;
            if (pusher) pusher.position = U(K.PusherX(t), 0, K.TABLE + 0.115);
            var a = K.PickArm(t);
            if (arm) arm.position = U(a);
            if (carriage) carriage.position = U(0, a.y - 0.05, 0);   // 암 수직봉 위치 (흡착면 뒤 0.05 m)
            if (kicker) kicker.rotation = Quaternion.AngleAxis(-(float)(K.KickerAngle(t) * Mathf.Rad2Deg), Vector3.forward) * kickerRest;
            float ra = -(float)(K.RollerAngle(t) * Mathf.Rad2Deg % 360.0);
            var rq = Quaternion.AngleAxis(ra, Vector3.forward);
            for (int i = 0; i < rollers.Count; i++) rollers[i].rotation = rq * rollerRest[i];

            var alive = K.BoxesAlive(t);
            var keep = new HashSet<long>(alive);
            var stale = new List<long>();
            foreach (var kv in boxes) if (!keep.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var k in stale) { Destroy(boxes[k].root); boxes.Remove(k); }
            foreach (var k in alive)
            {
                if (!boxes.TryGetValue(k, out var rig)) { rig = MakeBox(k); boxes[k] = rig; }
                PoseBox(rig, t - K.PHASE - k * K.PERIOD);
            }
        }

        void PoseBox(BoxRig rig, double tau)
        {
            bool vis = K.BoxPose(tau, out D3 root);
            rig.root.SetActive(vis);
            if (!vis) return;
            double tt = System.Math.Max(tau, 0.0);
            var slabs = K.BoxSlabs(tau >= 0 ? tt : -1.0);
            for (int i = 0; i < slabs.Count; i++)
            {
                var s = slabs[i]; var tr = rig.slabs[i];
                tr.position = U(root + s.center);
                tr.rotation = Quaternion.LookRotation(U(s.ez), U(s.ey));
                tr.localScale = new Vector3((float)s.size.x, (float)s.size.y, (float)s.size.z);
            }
            var tp = K.TapeParts(tt);
            for (int i = 0; i < tp.Length; i++)
            {
                var p = tp[i]; var tr = rig.tape[i];
                bool on = p.on && tau >= 0;
                tr.gameObject.SetActive(on);
                if (!on) continue;
                tr.position = U(root + p.center);
                tr.localScale = U(p.size);
            }
            for (int j = 0; j < 8; j++)
            {
                bool pv = K.PackPose(tt, j, out D3 pos, out double ang);
                var tr = rig.packs[j];
                bool on = pv && tau >= 0;
                tr.gameObject.SetActive(on);
                if (!on) continue;
                tr.position = U(pos);
                tr.rotation = Quaternion.AngleAxis(-(float)(ang * Mathf.Rad2Deg), Vector3.right);
            }
        }

        BoxRig MakeBox(long k)
        {
            var rig = new BoxRig { root = new GameObject("Box_" + k) };
            rig.root.transform.SetParent(boxParent, false);
            var names = K.BoxSlabs(3.0);
            for (int i = 0; i < 12; i++) rig.slabs[i] = Cube(names[i].name, Vector3.one, Vector3.zero, M.card, rig.root.transform).transform;
            for (int i = 0; i < 6; i++) rig.tape[i] = Cube(K.TapeNames[i], Vector3.one, Vector3.zero, M.tape, rig.root.transform, false).transform;
            var rnd = new System.Random(100 + (int)k);
            var P = K.PACK;
            for (int j = 0; j < 8; j++)
            {
                var g = new GameObject("Pack" + j); g.transform.SetParent(rig.root.transform, false);
                int v = rnd.Next(4);
                Cube("Body", U(P.x - 0.002, P.y - 0.002, P.z - 0.002), Vector3.zero, M.prod[v], g.transform);
                Cube("Band", U(P.x, P.y, 0.03), U(0, 0, P.z * 0.18), v == 0 ? M.prod[1] : M.prod[0], g.transform, false);
                rig.packs[j] = g.transform;
            }
            return rig;
        }

        // ------------------------------------------------------------------ 설비 FBX
        void LoadEquipment()
        {
            var prefab = Resources.Load<GameObject>("CaseLineModels/caseline_equipment");
            if (!prefab) { Debug.LogError("[CaseLine] Resources/CaseLineModels/caseline_equipment.fbx 가 없습니다 (blender/caseline_blender.py 로 생성)"); return; }
            var go = Instantiate(prefab); go.name = "CaseLineEquipment";
            equip = go.transform;
            Align(equip);
            M.Remap(go);
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Pusher") pusher = t;
                else if (t.name == "PickArm") arm = t;
                else if (t.name == "PickCarriage") carriage = t;
                else if (t.name == "Kicker") kicker = t;
                else if (t.name.StartsWith("Roller_")) { rollers.Add(t); rollerRest.Add(t.rotation); }
                if (t.name.StartsWith("Marker_")) t.gameObject.SetActive(false);
                var r = t.GetComponent<MeshRenderer>(); if (r) r.shadowCastingMode = ShadowCastingMode.On;
            }
            if (kicker) kickerRest = kicker.rotation;
            Debug.Log($"[CaseLine] 설비 로드: rollers={rollers.Count} pusher={(pusher != null)} arm={(arm != null)} kicker={(kicker != null)}");
        }

        // FBX 축 변환/단위가 무엇이든, 표지 4개(Blender 원점·+x·+y·+z 1 m)를 U 좌표에 맞춤
        static void Align(Transform root)
        {
            Transform o = null, x = null, y = null, z = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Marker_O") o = t; else if (t.name == "Marker_X") x = t; else if (t.name == "Marker_Y") y = t; else if (t.name == "Marker_Z") z = t;
            }
            if (!o || !x || !y || !z) { Debug.LogWarning("[CaseLine] 정렬 표지 없음: FBX 를 그대로 둡니다"); return; }
            float s = 1f / Mathf.Max(1e-5f, (x.position - o.position).magnitude);
            root.localScale *= s;
            var ey = (y.position - o.position).normalized; var ez = (z.position - o.position).normalized;
            var fix = Quaternion.Inverse(Quaternion.LookRotation(ey, ez));
            root.rotation = fix * root.rotation;
            root.position -= o.position;
            if (Vector3.Dot(x.position - o.position, Vector3.right) < 0)
            {
                // 거울상으로 들어온 경우: 원점 기준 x 반전 부모 아래로
                var mirror = new GameObject("CaseLineEquipment_Mirror").transform;
                mirror.localScale = new Vector3(-1, 1, 1);
                root.SetParent(mirror, false);
                Debug.LogWarning("[CaseLine] FBX 가 거울상으로 들어와 x 를 뒤집었습니다");
            }
        }

        // ------------------------------------------------------------------ 재질 (Blender 재질 이름과 같게: FBX 재질을 이름으로 교체)
        class Mats
        {
            public Material card, tape, floor, wall, panelWall, ceiling, beam, lightPanel, yellow, green, pallet, dark;
            public Material[] prod;
            readonly Dictionary<string, Material> byName = new Dictionary<string, Material>();

            public Mats()
            {
                Coat("FrameIvory", new Color(0.80f, 0.78f, 0.70f), 0.62f);
                var sus = Lit("Stainless", new Color(0.74f, 0.74f, 0.73f), 1f, 0.72f); Brushed(sus);
                Lit("Aluminium", new Color(0.80f, 0.81f, 0.82f), 1f, 0.7f);
                Lit("RollerZinc", new Color(0.72f, 0.73f, 0.75f), 1f, 0.78f);
                Lit("SteelDark", new Color(0.25f, 0.26f, 0.27f), 0.8f, 0.55f);
                Lit("FrameGrey", new Color(0.32f, 0.34f, 0.36f), 0f, 0.6f);
                Lit("BeltBlack", new Color(0.025f, 0.025f, 0.027f), 0f, 0.3f);
                Lit("Rubber", new Color(0.015f, 0.015f, 0.015f), 0f, 0.15f);
                Lit("BlackPlastic", new Color(0.02f, 0.02f, 0.022f), 0f, 0.55f);
                Lit("SuctionCup", new Color(0.10f, 0.10f, 0.11f), 0f, 0.45f);
                Lit("GuardOrange", new Color(0.95f, 0.55f, 0.05f), 0f, 0.6f);
                yellow = Lit("SafetyYellow", new Color(0.92f, 0.65f, 0.03f), 0f, 0.55f);
                Lit("ButtonRed", new Color(0.75f, 0.03f, 0.02f), 0f, 0.7f);
                Emissive("ButtonGreen", new Color(0.1f, 1f, 0.3f), 1.5f);
                Lit("HoseBlue", new Color(0.05f, 0.25f, 0.65f), 0f, 0.65f);
                Emissive("Screen", new Color(0.35f, 0.6f, 1f), 1.5f);
                card = Lit("Cardboard", Color.white, 0f, 0.18f);
                Noise(card, new Color(0.50f, 0.34f, 0.19f), new Color(0.60f, 0.43f, 0.26f), 12f, 4);
                tape = Lit("TapeBrown", new Color(0.50f, 0.30f, 0.10f), 0f, 0.85f);
                Lit("TapeRoll", new Color(0.45f, 0.26f, 0.08f), 0f, 0.75f);
                Lit("PaperCore", new Color(0.70f, 0.62f, 0.48f), 0f, 0.2f);
                prod = new[]
                {
                    Lit("ProdWhite", new Color(0.86f, 0.86f, 0.84f), 0f, 0.65f), Lit("ProdBlue", new Color(0.08f, 0.25f, 0.62f), 0f, 0.65f),
                    Lit("ProdRed", new Color(0.70f, 0.08f, 0.06f), 0f, 0.65f), Lit("ProdGreen", new Color(0.12f, 0.45f, 0.22f), 0f, 0.65f),
                };
                floor = Epoxy();
                wall = Lit("WallWhite", new Color(0.80f, 0.81f, 0.82f), 0f, 0.4f);
                panelWall = Lit("SandwichPanel", new Color(0.55f, 0.58f, 0.62f), 0.3f, 0.55f);
                ceiling = Lit("Ceiling", new Color(0.55f, 0.56f, 0.58f), 0f, 0.2f);
                beam = Lit("SteelBeam", new Color(0.20f, 0.32f, 0.55f), 0f, 0.55f);
                lightPanel = Emissive("LightPanel", new Color(1f, 0.98f, 0.95f), 6f);
                green = Lit("SafetyGreen", new Color(0.1f, 0.55f, 0.25f), 0f, 0.55f);
                pallet = Lit("PalletWood", new Color(0.62f, 0.48f, 0.32f), 0f, 0.25f);
                dark = Lit("OpeningDark", new Color(0.01f, 0.01f, 0.012f), 0f, 0.1f);
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
                m.SetFloat("_ClearCoatMask", 0.4f); m.SetFloat("_ClearCoatSmoothness", 0.85f);
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

            // 헤어라인 스테인리스: 한 방향 줄무늬 거칠기
            static void Brushed(Material m)
            {
                const int N = 256;
                var mg = new Texture2D(N, N, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
                var rnd = new System.Random(3);
                var rows = new float[N]; for (int y = 0; y < N; y++) rows[y] = (float)rnd.NextDouble();
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                        mg.SetPixel(x, y, new Color(1f, 0, 0, Mathf.Lerp(0.62f, 0.8f, rows[y] * 0.7f + 0.3f * Mathf.PerlinNoise(x * 0.01f, y * 0.3f))));
                mg.Apply();
                m.SetTexture("_MetallicGlossMap", mg); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetTextureScale("_MetallicGlossMap", new Vector2(4, 4));
            }

            // 녹색 에폭시 바닥 (국내 공장): 은은한 얼룩 + 반사
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
                        var c = Color.Lerp(new Color(0.08f, 0.21f, 0.14f), new Color(0.12f, 0.28f, 0.19f), Mathf.SmoothStep(0.25f, 0.75f, v));
                        alb.SetPixel(x, y, c);
                        float s = Mathf.Lerp(0.70f, 0.88f, Mathf.PerlinNoise(x * 0.02f + 5f, y * 0.02f));
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

        // ------------------------------------------------------------------ 메쉬 묶음
        class Batch
        {
            readonly Dictionary<Material, List<CombineInstance>> parts = new Dictionary<Material, List<CombineInstance>>();
            readonly Mesh cube;
            public Batch(Mesh cube) { this.cube = cube; }
            // Blender 좌표 두 모서리 사이 상자
            public void Span(float x0, float y0, float z0, float x1, float y1, float z1, Material m)
            {
                var c = U((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
                var s = U(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0));
                Add(Matrix4x4.TRS(c, Quaternion.identity, s), m);
            }
            public void Box(float sx, float sy, float sz, float x, float y, float z, Material m) =>
                Span(x - sx / 2, y - sy / 2, z - sz / 2, x + sx / 2, y + sy / 2, z + sz / 2, m);
            void Add(Matrix4x4 mtx, Material m)
            {
                if (!parts.TryGetValue(m, out var l)) { l = new List<CombineInstance>(); parts[m] = l; }
                l.Add(new CombineInstance { mesh = cube, transform = mtx });
            }
            public Transform Build(string name, Transform parent)
            {
                var root = new GameObject(name).transform; root.SetParent(parent, false);
                foreach (var kv in parts)
                {
                    var mesh = new Mesh { name = name + "_" + kv.Key.name, indexFormat = IndexFormat.UInt32 };
                    mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                    mesh.RecalculateBounds();
                    var go = new GameObject(kv.Key.name); go.transform.SetParent(root, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = kv.Key;
                }
                return root;
            }
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

        // ------------------------------------------------------------------ 건물 (blender/caseline_blender.py make_building 과 같은 배치)
        void BuildEnvironment()
        {
            var env = new GameObject("Building").transform;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var b = new Batch(tmp.GetComponent<MeshFilter>().sharedMesh); Destroy(tmp);
            const float Hh = 6.5f, TB = (float)K.TABLE, XF = (float)K.X_FILL;
            b.Box(40, 40, 0.02f, 4, 0, -0.01f, M.floor);
            b.Span(-1.6f, -0.95f, 0, 10.0f, -0.89f, 0.003f, M.yellow);
            b.Span(-1.6f, 2.0f, 0, 1.3f, 2.06f, 0.003f, M.yellow);
            b.Span(-1.66f, -0.95f, 0, -1.6f, 2.06f, 0.003f, M.yellow);
            b.Span(-6, -3.2f, 0, 14, -3.08f, 0.003f, M.green);
            b.Span(-8, 4.5f, 0, XF - 0.32f, 4.7f, Hh, M.panelWall);
            b.Span(XF + 0.32f, 4.5f, 0, 16, 4.7f, Hh, M.panelWall);
            b.Span(XF - 0.32f, 4.5f, TB + 1.35f, XF + 0.32f, 4.7f, Hh, M.panelWall);
            b.Span(XF - 0.32f, 4.5f, 0, XF + 0.32f, 4.7f, TB + 0.75f, M.panelWall);
            b.Span(XF - 0.30f, 4.62f, TB + 0.75f, XF + 0.30f, 4.7f, TB + 1.35f, M.dark);
            const float xw = 8.8f;
            b.Span(xw, -8, 0, xw + 0.2f, -0.40f, Hh, M.panelWall);
            b.Span(xw, 0.40f, 0, xw + 0.2f, 4.5f, Hh, M.panelWall);
            b.Span(xw, -0.40f, TB + 0.55f, xw + 0.2f, 0.40f, Hh, M.panelWall);
            b.Span(xw + 0.15f, -0.40f, 0, xw + 0.2f, 0.40f, TB - 0.12f, M.panelWall);
            b.Span(xw + 0.6f, -0.6f, 0, xw + 0.62f, 0.6f, Hh, M.dark);
            b.Span(xw - 0.02f, -0.46f, TB + 0.55f, xw, 0.46f, TB + 0.62f, M.yellow);
            for (int sy = -1; sy <= 1; sy += 2) b.Span(xw - 0.02f, sy * 0.40f - 0.03f, 0, xw, sy * 0.40f + 0.03f, TB + 0.62f, M.yellow);
            b.Span(-8.2f, -8, 0, -8, 4.7f, Hh, M.panelWall);
            b.Span(-8, -12, 0, 16, -11.8f, Hh, M.wall);
            b.Box(24, 17, 0.2f, 4, -3.6f, Hh, M.ceiling);
            foreach (var x in new[] { -4f, 4f, 12f }) foreach (var y in new[] { 4.2f, -7.5f }) b.Box(0.3f, 0.3f, Hh, x, y, Hh / 2, M.beam);
            for (int x = -7; x < 16; x += 3) b.Box(0.2f, 17, 0.4f, x, -3.6f, Hh - 0.3f, M.beam);
            // 팔레트: 왼쪽 블랭크 묶음, 오른쪽 봉함된 박스 적재
            foreach (var (px, py, blank) in new[] { (-1.15f, 1.25f, true), (6.6f, 1.6f, false), (7.9f, 1.6f, false) })
            {
                b.Box(1.1f, 1.1f, 0.02f, px, py, 0.13f, M.pallet);
                for (int k = -1; k <= 1; k++) { b.Box(1.1f, 0.1f, 0.10f, px, py + k * 0.5f, 0.06f, M.pallet); b.Box(0.1f, 1.1f, 0.02f, px + k * 0.5f, py, 0.01f, M.pallet); }
                if (blank)
                {
                    b.Box(0.72f, 1.0f, 0.55f, px, py, 0.14f + 0.275f, M.card);
                    for (int k = 0; k < 5; k++) b.Box(0.725f, 1.005f, 0.004f, px, py, 0.18f + k * 0.11f, M.tape);
                }
                else
                    for (int lz = 0; lz < 3; lz++) for (int ix = 0; ix < 2; ix++) for (int iy = 0; iy < 3; iy++)
                            {
                                float cx = px - 0.21f + ix * 0.42f, cy = py - 0.33f + iy * 0.33f;
                                b.Box(0.408f, 0.308f, 0.308f, cx, cy, 0.14f + 0.154f + lz * 0.31f, M.card);
                                b.Box(0.41f, (float)K.TAPE_W, 0.0012f, cx, cy, 0.14f + 0.308f + lz * 0.31f, M.tape);
                            }
            }
            for (int ix = -1; ix < 4; ix++) for (int iy = -2; iy < 1; iy++) b.Box(1.4f, 0.35f, 0.05f, ix * 3f + 1f, iy * 3f + 1f, Hh - 0.6f, M.lightPanel);
            b.Build("Shell", env);

            int shadowed = 0;
            for (int ix = -1; ix < 4; ix++)
                for (int iy = -1; iy < 1; iy++)
                {
                    var lg = new GameObject("HighBay"); lg.transform.SetParent(env);
                    lg.transform.position = U(ix * 3f + 1f, iy * 3f + 1f, Hh - 0.7f); lg.transform.rotation = Quaternion.Euler(90, 0, 0);
                    var l = lg.AddComponent<Light>(); l.type = LightType.Spot; l.spotAngle = 130; l.innerSpotAngle = 60;
                    l.range = 14; l.intensity = 9f; l.color = new Color(1f, 0.97f, 0.92f);
                    l.shadows = shadowed++ < 4 ? LightShadows.Soft : LightShadows.None;
                }
            var sun = new GameObject("KeyLight").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 0.9f; sun.color = new Color(1f, 0.97f, 0.93f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.Euler(55, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.72f, 0.74f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.58f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.33f, 0.28f);
            RenderSettings.skybox = null;
            var pg = new GameObject("ReflectionProbe"); pg.transform.position = U(3, 0, 2.5f);
            var probe = pg.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(24, 13, 17); probe.resolution = 256; probe.hdr = true;
        }

        // ------------------------------------------------------------------ 카메라 + 후처리
        void BuildCameraAndPost()
        {
            Cam = Camera.main;
            if (!Cam) { var cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; Cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>(); }
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.7f, 0.72f, 0.75f);
            Cam.nearClipPlane = 0.02f; Cam.farClipPlane = 120f;
            Cam.usePhysicalProperties = true; Cam.sensorSize = new Vector2(36f, 20.25f); Cam.focalLength = 24f;
            var ucd = Cam.GetUniversalAdditionalCameraData();
            ucd.renderPostProcessing = true;
            ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            ucd.antialiasingQuality = AntialiasingQuality.High;
            ucd.renderShadows = true;

            var target = new GameObject("CameraTarget").transform;
            var cine = Cam.GetComponent<CaseLineCamera>();
            if (cine == null) cine = Cam.gameObject.AddComponent<CaseLineCamera>();
            cine.view = this; cine.target = target; cine.enabled = true;

            var vgo = new GameObject("PostProcessVolume");
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.ACES);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.35f); bl.threshold.Override(1.1f); bl.scatter.Override(0.6f);
            var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.Override(0.4f); ca.contrast.Override(10f); ca.saturation.Override(0f);
            var vg = prof.Add<Vignette>(true); vg.intensity.Override(0.18f); vg.smoothness.Override(0.45f);
            var dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(3f); dof.focalLength.Override(28f); dof.aperture.Override(5.6f);
            var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0.2f); mb.quality.Override(MotionBlurQuality.High);
            var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.08f);
            vol.sharedProfile = prof;
            cine.dof = dof;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || (CaseLineMovieCapture.IsRecording && CaseLineMovieCapture.HideHudWhileRecording)) return;
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUILayout.BeginArea(new Rect(12, 12, 380, 170), GUI.skin.box);
            K.Cycle(T, out long k, out double tau);
            GUILayout.Label($"<b>반자동 제함 → 충전 → 봉함 라인</b>  t = {T:0.0} s", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"박스 {K.L * 1000:0}x{K.W * 1000:0}x{K.H * 1000:0} mm   간격 {K.PERIOD:0.0} s ({3600 / K.PERIOD:0} box/h)");
            GUILayout.Label($"컨베이어 {K.V * 60:0} m/min   완성 박스 {System.Math.Max(0, k - 2)}");
            GUILayout.Label($"Speed x{speed:0.0}");
            speed = GUILayout.HorizontalSlider(speed, 0f, 4f);
            cinematicCamera = GUILayout.Toggle(cinematicCamera, "Cinematic camera (off = orbit with mouse)");
            GUILayout.EndArea();
        }
    }
}
