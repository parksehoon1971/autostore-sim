// 배치 게임의 메인 공간: 넓은 바닥 위에 라이브러리의 설비를 끌어다 놓고, 클릭해 선택하고, 끌어서 옮깁니다.
//  - 설비는 각 장면의 View 를 '모듈 모드'(건물·조명·카메라 없이 설비만)로 만들어 한 공간에 여러 대를 함께 돌립니다.
//  - 배치는 1 m 격자에 맞추고, 다른 설비와 겹치면 빨간 틀로 표시되고 놓을 수 없습니다. (현재 회전은 지원하지 않음)
//  - 배치는 자동 저장되어 (PlayerPrefs) 다음 실행이나 애니메이션 모드에서 돌아올 때 그대로 다시 만들어집니다.
// 조작: 마우스 오른쪽 끌기 = 회전, 가운데 끌기 / WASD = 이동, 휠 = 확대·축소, F = 선택한 설비로, Delete = 삭제, Esc = 취소
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace EquipFX
{
    [Serializable]
    public class LayoutItem
    {
        public string id;          // EquipEntry.id
        public float x, z;         // 설비 바닥 영역의 최소 모서리 (m)
        public string pars = "";   // 바꾼 속성 "field=value;field=value"
    }

    [Serializable]
    class LayoutSave
    {
        public List<LayoutItem> items = new List<LayoutItem>();
        public List<string> sizeIds = new List<string>();
        public List<Vector3> sizes = new List<Vector3>();
    }

    // 배치된 설비 하나 (모듈 루트)
    public class PlacedModule : MonoBehaviour
    {
        public EquipEntry entry;
        public Component view;
        public int serial;
        public bool built;
        public Bounds localBounds;                         // 모듈 루트 기준 설비 범위
        public Dictionary<string, double> pars = new Dictionary<string, double>();

        public readonly List<PartRef> parts = new List<PartRef>();       // 클릭할 수 있는 하위 오브젝트
        public readonly Dictionary<PartDef, int> partScan = new Dictionary<PartDef, int>();

        public Vector2 Min => new Vector2(transform.position.x + localBounds.min.x, transform.position.z + localBounds.min.z);
        public Vector2 Size => new Vector2(localBounds.size.x, localBounds.size.z);
        public string Label => $"{entry.name} #{serial}";
    }

    // 설비 안의 하위 오브젝트 하나 (로봇·셔틀·토트 …). 클릭 판정은 물리 없이 이 상자로 직접 계산
    public class PartRef
    {
        public PlacedModule module;
        public PartDef def;
        public int index;
        public Transform t;
        public Bounds local;      // t 기준 렌더러 범위
        public string Label => $"{def.label} #{index + 1}";

        public Bounds WorldBounds()
        {
            var c = local.center; var e = local.extents;
            var b = new Bounds(t.TransformPoint(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(t.TransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return b;
        }

        // 시뮬레이션에서 이 오브젝트에 해당하는 항목 (매번 새로 읽음: 구조체 배열도 최신 값)
        public object SimItem()
        {
            if (def.simList == null || module == null || module.view == null) return null;
            var sim = LayoutWorld.GetMember(module.view, "Sim");
            if (sim == null) return null;
            var list = LayoutWorld.GetMember(sim, def.simList) as System.Collections.IList;
            return list != null && index >= 0 && index < list.Count ? list[index] : null;
        }
    }

    public class LayoutInput
    {
        public Vector2 mouse, mouseDelta; public float scroll;
        public bool lDown, lHeld, lUp, rHeld, mHeld, shift;
        public bool del, esc, f, w, a, s, d, q, e;

        public static LayoutInput Read()
        {
            var i = new LayoutInput();
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current; var k = Keyboard.current;
            if (m != null)
            {
                i.mouse = m.position.ReadValue(); i.mouseDelta = m.delta.ReadValue(); i.scroll = HudInput.WheelNotches(m.scroll.ReadValue().y);
                i.lDown = m.leftButton.wasPressedThisFrame; i.lHeld = m.leftButton.isPressed; i.lUp = m.leftButton.wasReleasedThisFrame;
                i.rHeld = m.rightButton.isPressed; i.mHeld = m.middleButton.isPressed;
            }
            if (k != null)
            {
                i.del = k.deleteKey.wasPressedThisFrame || k.backspaceKey.wasPressedThisFrame; i.esc = k.escapeKey.wasPressedThisFrame;
                i.f = k.fKey.wasPressedThisFrame; i.shift = k.shiftKey.isPressed;
                i.w = k.wKey.isPressed || k.upArrowKey.isPressed; i.s = k.sKey.isPressed || k.downArrowKey.isPressed;
                i.a = k.aKey.isPressed; i.d = k.dKey.isPressed; i.q = k.qKey.isPressed; i.e = k.eKey.isPressed;
            }
#else
            i.mouse = Input.mousePosition; i.mouseDelta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
            i.scroll = Input.mouseScrollDelta.y;
            i.lDown = Input.GetMouseButtonDown(0); i.lHeld = Input.GetMouseButton(0); i.lUp = Input.GetMouseButtonUp(0);
            i.rHeld = Input.GetMouseButton(1); i.mHeld = Input.GetMouseButton(2);
            i.del = Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace); i.esc = Input.GetKeyDown(KeyCode.Escape);
            i.f = Input.GetKeyDown(KeyCode.F); i.shift = Input.GetKey(KeyCode.LeftShift);
            i.w = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow); i.s = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);
            i.a = Input.GetKey(KeyCode.A); i.d = Input.GetKey(KeyCode.D); i.q = Input.GetKey(KeyCode.Q); i.e = Input.GetKey(KeyCode.E);
#endif
            return i;
        }
    }

    public class LayoutWorld : MonoBehaviour
    {
        public const string ScenePath = "Assets/Common/Scenes/LayoutSpace.unity";
        public const string SaveKey = "EquipFX.Layout.v1";
        public float floorW = 160f, floorD = 100f;

        public static LayoutWorld Instance { get; private set; }
        public readonly List<PlacedModule> modules = new List<PlacedModule>();
        public PlacedModule Selected { get; private set; }
        public PartRef SelectedPart { get; private set; }
        public event Action SelectionChanged, LayoutChanged;
        public bool Busy => buildQueue.Count > 0 || building;
        public EquipEntry Dragging => dragEntry;

        Camera cam;
        // 카메라
        Vector3 focus = new Vector3(0, 0, 0);
        float yaw = 30f, pitch = 48f, dist = 55f;
        // 놓기 / 옮기기
        EquipEntry dragEntry; Vector2 dragStartMouse;
        PlacedModule pressModule, moving; Vector2 pressMouse; Vector3 moveStartPos, grabOffset; bool moveValid;
        FootprintFrame ghost, selFrame;
        OutlineBox partBox;
        Material floorMat;
        readonly Queue<IEnumerator> buildQueue = new Queue<IEnumerator>();
        bool building, loaded;
        int serial;
        static readonly Dictionary<string, Vector3> sizeCache = new Dictionary<string, Vector3>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; }

        // ------------------------------------------------------------------ 시작
        void Awake()
        {
            Instance = this;
            BuildWorld();
            ghost = new FootprintFrame("PlacementGhost", transform);
            selFrame = new FootprintFrame("SelectionFrame", transform);
            partBox = new OutlineBox("PartOutline", transform);
            ghost.Hide(); selFrame.Hide(); partBox.Hide();
        }

        void Start() => Load();

        void OnDestroy() { if (Instance == this) Instance = null; }

        void BuildWorld()
        {
            // 바닥: 1 m 가는 선 + 10 m 굵은 선
            var tex = new Texture2D(512, 512, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8, name = "LayoutGrid" };
            var bg = new Color(0.86f, 0.87f, 0.89f); var fine = new Color(0.78f, 0.79f, 0.82f); var bold = new Color(0.66f, 0.68f, 0.72f);
            var px = new Color[512 * 512];
            for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++)
                {
                    bool b = x < 3 || y < 3;
                    bool f = (x % 51 < 1) || (y % 51 < 1);
                    px[y * 512 + x] = b ? bold : f ? fine : bg;
                }
            tex.SetPixels(px); tex.Apply(true);
            floorMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "LayoutFloor" };
            floorMat.SetTexture("_BaseMap", tex); floorMat.SetColor("_BaseColor", Color.white);
            floorMat.SetTextureScale("_BaseMap", new Vector2(floorW / 10f, floorD / 10f));
            floorMat.SetFloat("_Smoothness", 0.25f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor"; floor.transform.SetParent(transform, false);
            floor.transform.localScale = new Vector3(floorW, 0.1f, floorD);
            floor.transform.position = new Vector3(floorW / 2 - 10f, -0.05f, floorD / 2 - 10f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
            Destroy(floor.GetComponent<Collider>());
            focus = new Vector3(20f, 0, 15f);

            // 조명
            var sunGo = new GameObject("Sun"); sunGo.transform.SetParent(transform, false);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.6f; sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.7f;
            sunGo.transform.rotation = Quaternion.Euler(50, -35, 0);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.65f, 0.70f);
            RenderSettings.ambientEquatorColor = new Color(0.48f, 0.50f, 0.53f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.30f, 0.31f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            // 카메라
            cam = Camera.main;
            if (cam == null)
            {
                var cg = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = cg.AddComponent<Camera>(); cg.AddComponent<AudioListener>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.80f, 0.82f, 0.85f);
            cam.nearClipPlane = 0.3f; cam.farClipPlane = 600f; cam.fieldOfView = 40f;
            var ucd = cam.GetUniversalAdditionalCameraData();
            if (ucd != null) { ucd.renderPostProcessing = true; ucd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; }

            var vgo = new GameObject("PostProcess"); vgo.transform.SetParent(transform, false);
            var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = prof.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.Neutral);
            var bl = prof.Add<Bloom>(true); bl.intensity.Override(0.15f); bl.threshold.Override(1.1f);
            vol.sharedProfile = prof;
            UpdateCamera(true);
        }

        // ------------------------------------------------------------------ 매 프레임
        void Update()
        {
            var inp = LayoutInput.Read();
            bool overUi = HudInput.PointerOverUi;
            if (!building && buildQueue.Count > 0) StartCoroutine(RunBuild(buildQueue.Dequeue()));

            CameraControls(inp, overUi);
            var ray = cam.ScreenPointToRay(inp.mouse);
            bool onGround = Ground(ray, out var gp);

            // 라이브러리에서 끌어 오는 중
            if (dragEntry != null)
            {
                var size = SizeOf(dragEntry);
                var min = Snap(new Vector2(gp.x - size.x / 2, gp.z - size.y / 2));
                bool ok = onGround && !overUi && Free(min, size, null);
                if (onGround && !overUi) ghost.Set(min, size, ok ? HudStyle.Ok : HudStyle.Bad, dragEntry.name);
                else ghost.Hide();
                if (inp.esc) { dragEntry = null; ghost.Hide(); }
                else if (inp.lUp)
                {
                    var e = dragEntry; dragEntry = null; ghost.Hide();
                    if (ok) Place(e, min, null);
                    else if (overUi && (inp.mouse - dragStartMouse).magnitude < 6f) PlaceNearView(e);   // 누르기만 함 → 화면 가운데 근처 빈자리
                }
                return;
            }

            // 설비 누르기 → 선택, 끌기 → 옮기기
            if (inp.lDown && !overUi)
            {
                var part = PickPart(ray, out float partD);
                pressModule = PickModule(ray);
                if (part != null) pressModule = part.module;
                pressMouse = inp.mouse;
                if (pressModule != Selected || part != SelectedPart) Select(pressModule, part);
                if (pressModule != null && onGround) { moveStartPos = pressModule.transform.position; grabOffset = pressModule.transform.position - gp; }
            }
            if (inp.lHeld && pressModule != null && pressModule.built && onGround)
            {
                if (moving == null && (inp.mouse - pressMouse).magnitude > 6f) moving = pressModule;
                if (moving != null)
                {
                    var want = gp + grabOffset;
                    var b = moving.localBounds;
                    var min = Snap(new Vector2(want.x + b.min.x, want.z + b.min.z));
                    moving.transform.position = new Vector3(min.x - b.min.x, 0, min.y - b.min.z);
                    moveValid = Free(moving.Min, moving.Size, moving);
                    selFrame.Set(moving.Min, moving.Size, moveValid ? HudStyle.Accent : HudStyle.Bad, null);
                }
            }
            if (inp.lUp)
            {
                if (moving != null)
                {
                    if (!moveValid) moving.transform.position = moveStartPos;
                    else LayoutChanged?.Invoke();
                    moving = null; Save();
                }
                pressModule = null;
            }
            if (inp.esc && moving == null) { if (SelectedPart != null) Select(Selected); else Select(null); }
            if (inp.del && Selected != null && !HudInput.Typing) Remove(Selected);
            if (inp.f && Selected != null && !HudInput.Typing) FocusOn(Selected);

            if (Selected != null && moving == null) selFrame.Set(Selected.Min, Selected.Size, HudStyle.Accent, null);
            else if (Selected == null) selFrame.Hide();
            if (SelectedPart != null && (SelectedPart.t == null || SelectedPart.module == null)) Select(Selected);
            if (SelectedPart != null && SelectedPart.t.gameObject.activeInHierarchy) partBox.Set(SelectedPart.WorldBounds(), HudStyle.Accent);
            else partBox.Hide();
        }

        void CameraControls(LayoutInput i, bool overUi)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (!HudInput.Typing)
            {
                var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward; var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                float sp = dist * 0.9f * (i.shift ? 2.5f : 1f) * dt;
                if (i.w) focus += fwd * sp; if (i.s) focus -= fwd * sp;
                if (i.d) focus += right * sp; if (i.a) focus -= right * sp;
                if (i.q) yaw += 70f * dt; if (i.e) yaw -= 70f * dt;
            }
            if (!overUi || i.rHeld || i.mHeld)
            {
                if (i.rHeld) { yaw += i.mouseDelta.x * 0.25f; pitch = Mathf.Clamp(pitch - i.mouseDelta.y * 0.2f, 12f, 88f); }
                if (i.mHeld)
                {
                    var right = cam.transform.right; right.y = 0; right.Normalize();
                    var fwd = Vector3.Cross(right, Vector3.up);
                    focus -= (right * i.mouseDelta.x - fwd * i.mouseDelta.y) * dist * 0.0018f;
                }
                if (!overUi && Mathf.Abs(i.scroll) > 0.001f)
                {
                    // 마우스가 가리키는 바닥 점 쪽으로 확대·축소
                    float nd = Mathf.Clamp(dist * Mathf.Pow(0.85f, i.scroll), 3f, 260f);
                    if (Ground(cam.ScreenPointToRay(i.mouse), out var gp))
                    {
                        float k = 1f - nd / dist;
                        var tgt = new Vector3(gp.x, 0, gp.z);
                        focus += (tgt - focus) * k;
                    }
                    dist = nd;
                }
            }
            focus.x = Mathf.Clamp(focus.x, -10f, floorW - 10f); focus.z = Mathf.Clamp(focus.z, -10f, floorD - 10f);
            UpdateCamera(false);
        }

        void UpdateCamera(bool snap)
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            cam.transform.SetPositionAndRotation(focus + rot * new Vector3(0, 0, -dist), rot);
        }

        public void FocusOn(PlacedModule m)
        {
            if (m == null) return;
            var c = m.transform.position + m.localBounds.center;
            focus = new Vector3(c.x, 0, c.z);
            dist = Mathf.Clamp(Mathf.Max(m.localBounds.size.x, m.localBounds.size.z) * 1.6f + 6f, 8f, 200f);
        }

        // ------------------------------------------------------------------ 배치
        public void BeginDrag(EquipEntry e) { dragEntry = e; dragStartMouse = LayoutInput.Read().mouse; }

        public Vector2 SizeOf(EquipEntry e) =>
            sizeCache.TryGetValue(e.id, out var s) ? new Vector2(s.x, s.z) : new Vector2(12f, 10f);

        static Vector2 Snap(Vector2 v) => new Vector2(Mathf.Round(v.x), Mathf.Round(v.y));

        bool Free(Vector2 min, Vector2 size, PlacedModule except)
        {
            const float gap = 0.5f;   // 설비 사이 최소 통로 0.5 m
            foreach (var m in modules)
            {
                if (m == except || m == null) continue;
                var mm = m.Min; var ms = m.Size;
                if (min.x < mm.x + ms.x + gap && mm.x < min.x + size.x + gap && min.y < mm.y + ms.y + gap && mm.y < min.y + size.y + gap) return false;
            }
            return true;
        }

        void PlaceNearView(EquipEntry e)
        {
            var size = SizeOf(e);
            var c = new Vector2(focus.x - size.x / 2, focus.z - size.y / 2);
            for (int r = 0; r < 40; r++)
                for (int k = 0; k < Mathf.Max(1, r * 8); k++)
                {
                    float a = k / Mathf.Max(1f, r * 8f) * Mathf.PI * 2;
                    var min = Snap(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 3f);
                    if (Free(min, size, null)) { Place(e, min, null); return; }
                }
        }

        public PlacedModule Place(EquipEntry e, Vector2 min, Dictionary<string, double> pars, bool select = true)
        {
            var go = new GameObject($"Module_{e.id}_{++serial}");
            SceneManager.MoveGameObjectToScene(go, gameObject.scene);
            go.transform.position = Vector3.zero;
            var m = go.AddComponent<PlacedModule>();
            m.entry = e; m.serial = CountOf(e) + 1;
            if (pars != null) m.pars = new Dictionary<string, double>(pars);
            // 만들어지기 전 자리: 예상 크기
            var s = SizeOf(e);
            m.localBounds = new Bounds(new Vector3(s.x / 2, 1, s.y / 2), new Vector3(s.x, 2, s.y));
            go.transform.position = new Vector3(min.x, 0, min.y);
            modules.Add(m);
            buildQueue.Enqueue(Build(m, min, select));
            LayoutChanged?.Invoke();
            return m;
        }

        int CountOf(EquipEntry e) { int n = 0; foreach (var m in modules) if (m != null && m.entry == e) n = Mathf.Max(n, m.serial); return n; }

        IEnumerator RunBuild(IEnumerator job)
        {
            building = true;
            yield return StartCoroutine(job);
            building = false;
        }

        // 설비 View 를 모듈 모드로 만들고 (원점에서), 새로 생긴 최상위 오브젝트를 모두 모듈 아래로 모은 뒤 자리로 옮김
        IEnumerator Build(PlacedModule m, Vector2 min, bool select)
        {
            if (m == null) yield break;
            var scene = gameObject.scene;
            var prevActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(scene);
            m.transform.position = Vector3.zero;
            var before = new HashSet<GameObject>(scene.GetRootGameObjects());
            var v = m.gameObject.AddComponent(m.entry.viewType);
            m.view = v;
            SetBool(v, "moduleMode", true); SetBool(v, "showHud", false);
            SetBool(v, "showCaption", false); SetBool(v, "showCaptions", false); SetBool(v, "cinematicCamera", false);
            EquipHud.ApplyPars(v, m.pars);
            // Start (시뮬레이션 워밍업 + 설비 만들기) 가 끝날 때까지
            for (int i = 0; i < 300 && m != null && !m.entry.ready(v); i++) yield return null;
            yield return null;
            if (m == null) yield break;
            foreach (var go in scene.GetRootGameObjects())
                if (!before.Contains(go) && go != m.gameObject) go.transform.SetParent(m.transform, true);

            var rs = m.GetComponentsInChildren<Renderer>(true);
            Bounds b = default; bool any = false;
            foreach (var r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) b = new Bounds(new Vector3(5, 1, 5), new Vector3(10, 2, 10));
            m.localBounds = b;   // 모듈 루트가 원점에 있으므로 월드 = 로컬
            sizeCache[m.entry.id] = b.size;
            m.transform.position = new Vector3(min.x - b.min.x, 0, min.y - b.min.z);
            m.built = true;
            ScanParts(m);
            if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene) SceneManager.SetActiveScene(prevActive);
            // 실제 크기로 겹치면 근처 빈자리로
            if (!Free(m.Min, m.Size, m))
                for (int r = 1; r < 60; r++)
                {
                    var t = Snap(min + new Vector2(r * 2f, 0));
                    if (Free(t, m.Size, m)) { m.transform.position = new Vector3(t.x - b.min.x, 0, t.y - b.min.z); break; }
                }
            if (select) Select(m);
            Save();
            LayoutChanged?.Invoke();
        }

        public void Remove(PlacedModule m)
        {
            if (m == null) return;
            modules.Remove(m);
            if (Selected == m) Select(null);
            Destroy(m.gameObject);
            Save();
            LayoutChanged?.Invoke();
        }

        public void ClearAll()
        {
            foreach (var m in modules) if (m != null) Destroy(m.gameObject);
            modules.Clear(); Select(null);
            Save(); LayoutChanged?.Invoke();
        }

        // 속성 바꿔 다시 만들기 (같은 자리)
        public void Rebuild(PlacedModule m, Dictionary<string, double> pars)
        {
            if (m == null || !m.built) return;
            var min = m.Min; var e = m.entry; int sr = m.serial;
            Select(null);
            modules.Remove(m); Destroy(m.gameObject);
            var n = Place(e, min, pars);
            n.serial = sr;
        }

        public void Select(PlacedModule m) => Select(m, null);

        public void Select(PlacedModule m, PartRef part)
        {
            if (part != null && part.module != m) part = null;
            if (Selected == m && SelectedPart == part) return;
            Selected = m; SelectedPart = part;
            SelectionChanged?.Invoke();
        }

        // ------------------------------------------------------------------ 하위 오브젝트
        public static object GetMember(object o, string name)
        {
            if (o == null || string.IsNullOrEmpty(name)) return null;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, F); if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, F); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            }
            return null;
        }

        static void CollectTransforms(object item, List<Transform> into)
        {
            if (item == null) return;
            if (item is Transform tr) { into.Add(tr); return; }
            if (item is GameObject go) { into.Add(go.transform); return; }
            if (item is System.Collections.IList l) { foreach (var x in l) if (x is Transform t2) into.Add(t2); return; }
            if (GetMember(item, "root") is Transform r) into.Add(r);
        }

        // View 의 Transform 목록을 읽어 클릭 상자를 만듦. 목록이 늘어나면 (나중에 생긴 오브젝트) 다음 클릭 때 더 붙임
        void ScanParts(PlacedModule m)
        {
            if (m == null || m.view == null || m.entry.parts.Count == 0) return;
            var tmp = new List<Transform>();
            foreach (var def in m.entry.parts)
            {
                if (!(GetMember(m.view, def.viewField) is System.Collections.IList list)) continue;
                m.partScan.TryGetValue(def, out int done);
                if (list.Count <= done) continue;
                for (int i = done; i < list.Count; i++)
                {
                    tmp.Clear(); CollectTransforms(list[i], tmp);
                    foreach (var t in tmp)
                    {
                        if (t == null || !t.IsChildOf(m.transform)) continue;
                        if (!LocalRendererBounds(t, out var b)) continue;
                        m.parts.Add(new PartRef { module = m, def = def, index = i, t = t, local = b });
                    }
                }
                m.partScan[def] = list.Count;
            }
        }

        static bool LocalRendererBounds(Transform t, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var lb = r.localBounds; var c = lb.center; var e = lb.extents;
                for (int i = 0; i < 8; i++)
                {
                    var w = r.transform.TransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    var p = t.InverseTransformPoint(w);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (any) b.size = Vector3.Max(b.size, Vector3.one * 0.02f);
            return any;
        }

        PartRef PickPart(Ray ray, out float bestD)
        {
            PartRef best = null; bestD = float.MaxValue;
            foreach (var m in modules)
            {
                if (m == null || !m.built) continue;
                var mb = m.localBounds; mb.center += m.transform.position; mb.Expand(1f);
                if (!mb.IntersectRay(ray)) continue;
                ScanParts(m);
                foreach (var p in m.parts)
                {
                    if (p.t == null || !p.t.gameObject.activeInHierarchy) continue;
                    var lr = new Ray(p.t.InverseTransformPoint(ray.origin), p.t.InverseTransformVector(ray.direction));
                    if (!p.local.IntersectRay(lr, out float ld)) continue;
                    float d = (p.t.TransformPoint(lr.GetPoint(ld)) - ray.origin).magnitude;
                    if (d < bestD) { bestD = d; best = p; }
                }
            }
            return best;
        }

        PlacedModule PickModule(Ray ray)
        {
            PlacedModule best = null; float bestD = float.MaxValue;
            foreach (var m in modules)
            {
                if (m == null) continue;
                var b = m.localBounds; b.center += m.transform.position;
                if (b.IntersectRay(ray, out float d) && d < bestD) { bestD = d; best = m; }
            }
            return best;
        }

        static bool Ground(Ray ray, out Vector3 p)
        {
            var pl = new Plane(Vector3.up, Vector3.zero);
            if (pl.Raycast(ray, out float d) && d < 2000f) { p = ray.GetPoint(d); return true; }
            p = Vector3.zero; return false;
        }

        static void SetBool(object o, string name, bool v)
        {
            var f = o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (f != null && f.FieldType == typeof(bool)) f.SetValue(o, v);
        }

        // ------------------------------------------------------------------ 저장 / 불러오기
        public void Save()
        {
            if (!loaded) return;
            var s = new LayoutSave();
            foreach (var m in modules)
            {
                if (m == null) continue;
                var parts = new List<string>();
                foreach (var kv in m.pars) parts.Add(kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                s.items.Add(new LayoutItem { id = m.entry.id, x = m.Min.x, z = m.Min.y, pars = string.Join(";", parts) });
            }
            foreach (var kv in sizeCache) { s.sizeIds.Add(kv.Key); s.sizes.Add(kv.Value); }
            PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(s));
            PlayerPrefs.Save();
        }

        void Load()
        {
            loaded = true;
            var json = PlayerPrefs.GetString(SaveKey, "");
            if (string.IsNullOrEmpty(json)) return;
            LayoutSave s;
            try { s = JsonUtility.FromJson<LayoutSave>(json); } catch (Exception) { return; }
            if (s == null) return;
            for (int i = 0; i < s.sizeIds.Count && i < s.sizes.Count; i++) sizeCache[s.sizeIds[i]] = s.sizes[i];
            foreach (var it in s.items)
            {
                var e = EquipCatalog.All.Find(x => x.id == it.id);
                if (e == null) continue;
                var pars = new Dictionary<string, double>();
                if (!string.IsNullOrEmpty(it.pars))
                    foreach (var p in it.pars.Split(';'))
                    {
                        var kv = p.Split('=');
                        if (kv.Length == 2 && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) pars[kv[0]] = d;
                    }
                Place(e, new Vector2(it.x, it.z), pars, false);
            }
        }
    }

    // 선택한 하위 오브젝트를 감싸는 얇은 상자 테두리 (12개 모서리)
    public class OutlineBox
    {
        readonly Transform root;
        readonly Transform[] bars = new Transform[12];
        readonly Material mat;

        public OutlineBox(string name, Transform parent)
        {
            root = new GameObject(name).transform; root.SetParent(parent, false);
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name };
            for (int i = 0; i < 12; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(root, false);
                var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
                bars[i] = g.transform;
            }
        }

        public void Hide() => root.gameObject.SetActive(false);

        public void Set(Bounds b, Color c)
        {
            root.gameObject.SetActive(true);
            mat.SetColor("_BaseColor", c);
            b.Expand(0.06f);
            float w = Mathf.Clamp(Mathf.Max(b.size.x, b.size.y, b.size.z) * 0.02f, 0.015f, 0.08f);
            var mn = b.min; var mx = b.max; var ce = b.center; var sz = b.size;
            int k = 0;
            for (int i = 0; i < 4; i++)
            {
                float y = (i & 1) == 0 ? mn.y : mx.y, z = (i & 2) == 0 ? mn.z : mx.z;
                bars[k].position = new Vector3(ce.x, y, z); bars[k++].localScale = new Vector3(sz.x + w, w, w);
                float x = (i & 1) == 0 ? mn.x : mx.x; z = (i & 2) == 0 ? mn.z : mx.z;
                bars[k].position = new Vector3(x, ce.y, z); bars[k++].localScale = new Vector3(w, sz.y + w, w);
                x = (i & 1) == 0 ? mn.x : mx.x; y = (i & 2) == 0 ? mn.y : mx.y;
                bars[k].position = new Vector3(x, y, ce.z); bars[k++].localScale = new Vector3(w, w, sz.z + w);
            }
        }
    }

    // 바닥에 그리는 설비 영역 틀 (놓을 자리 미리보기 · 선택 표시)
    public class FootprintFrame
    {
        readonly Transform root;
        readonly Transform[] bars = new Transform[4];
        readonly Material mat;

        public FootprintFrame(string name, Transform parent)
        {
            root = new GameObject(name).transform; root.SetParent(parent, false);
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name };
            for (int i = 0; i < 4; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(root, false);
                var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
                bars[i] = g.transform;
            }
        }

        public void Hide() => root.gameObject.SetActive(false);

        public void Set(Vector2 min, Vector2 size, Color c, string label)
        {
            root.gameObject.SetActive(true);
            mat.SetColor("_BaseColor", c);
            const float w = 0.18f, h = 0.06f;
            float x0 = min.x, z0 = min.y, x1 = min.x + size.x, z1 = min.y + size.y;
            bars[0].position = new Vector3((x0 + x1) / 2, h / 2, z0); bars[0].localScale = new Vector3(size.x + w, h, w);
            bars[1].position = new Vector3((x0 + x1) / 2, h / 2, z1); bars[1].localScale = new Vector3(size.x + w, h, w);
            bars[2].position = new Vector3(x0, h / 2, (z0 + z1) / 2); bars[2].localScale = new Vector3(w, h, size.y + w);
            bars[3].position = new Vector3(x1, h / 2, (z0 + z1) / 2); bars[3].localScale = new Vector3(w, h, size.y + w);
        }
    }
}
