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
        public int cargo = (int)CargoKind.Pallet;   // 이 물류센터가 다루는 화물
        public SiteDef site;                         // 물류센터 건물 · 출입문
        public List<MoverSave> movers = new List<MoverSave>();   // 하역 수단
        public bool moversGiven;                     // 기본 하역 수단(지게차 1 · 작업자 1)을 이미 줌
        public bool unlimited;                       // 무제한 모드 (돈 · 마감 없음)
        public int balance;                          // 자금 모드 잔고 (만원)
        public bool fundsInit;
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
            else
            {
                var pt = UnityEngine.InputSystem.Pointer.current;   // touch screens / pens with no Mouse device
                if (pt != null)
                {
                    i.mouse = pt.position.ReadValue(); i.mouseDelta = pt.delta.ReadValue();
                    i.lDown = pt.press.wasPressedThisFrame; i.lHeld = pt.press.isPressed; i.lUp = pt.press.wasReleasedThisFrame;
                }
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
        public float floorW = 320f, floorD = 200f;
        const float FloorX0 = -80f, FloorZ0 = -50f;   // 바닥(부지) 최소 모서리

        public static LayoutWorld Instance { get; private set; }
        public readonly List<PlacedModule> modules = new List<PlacedModule>();
        public PlacedModule Selected { get; private set; }
        public PartRef SelectedPart { get; private set; }
        public event Action SelectionChanged, LayoutChanged, CargoChanged;
        public CargoKind Cargo { get; private set; } = CargoKind.Pallet;
        // 자금 모드: 화주 주문 보상 · 패널티로 잔고가 움직이고 마감이 있음. 무제한 모드: 돈과 마감 없이 자유롭게 설계
        public bool Unlimited { get; private set; }

        // ---------------------------------------------------------------- 자금 (자금 모드에서만)
        public const int StartCapital = 300000;     // 시작 자본 30억원 (만원)
        public const float Resale = 0.7f;           // 지우면 구매비의 70% 를 돌려받음

        public bool CanAfford(int cost) => Unlimited || Traffic.balance >= cost;

        bool Pay(int cost, string what)
        {
            if (Unlimited || cost <= 0) return true;
            if (Traffic.balance < cost)
            {
                Notice?.Invoke($"잔고가 부족합니다. {what}: {EquipCatalog.Money(cost)} 필요, 잔고 {EquipCatalog.Money(Traffic.balance)}");
                return false;
            }
            Traffic.balance -= cost;
            Notice?.Invoke($"{what} 구매  −{EquipCatalog.Money(cost)}  (잔고 {EquipCatalog.Money(Traffic.balance)})");
            return true;
        }

        void Refund(int cost, string what)
        {
            if (Unlimited || cost <= 0) return;
            int r = Mathf.RoundToInt(cost * Resale);
            Traffic.balance += r;
            Notice?.Invoke($"{what} 매각  +{EquipCatalog.Money(r)}");
        }

        // 자금을 시작 자본으로 되돌림 (설비·수단은 그대로)
        public void ResetFunds()
        {
            Traffic.balance = StartCapital;
            Save();
            Notice?.Invoke($"잔고를 시작 자본 {EquipCatalog.Money(StartCapital)}으로 되돌렸습니다");
        }

        public void SetUnlimited(bool on)
        {
            if (Unlimited == on) return;
            Unlimited = on;
            Save();
            CargoChanged?.Invoke();
        }
        public bool CanHandle(EquipEntry e) => e != null && e.Handles(Cargo);
        // 물류센터 건물
        public SiteDef Site { get; private set; } = NewSite();
        public float FloorY => Site.h;
        public bool DrawingSite { get; private set; }
        public bool BuildingSelected { get; private set; }
        public int SelectedDoor { get; private set; } = -1;
        public event Action SiteChanged, DrawingChanged;
        // 트럭 · 하역 수단
        public Traffic Traffic { get; private set; }
        public Mover SelectedMover { get; private set; }
        public event Action<string> Notice;
        int dragMover = -1; Mover pressMover; bool moverMoved;
        public bool DraggingMover => dragMover >= 0;
        Vector3 siteStart; bool siteDragging; int pressDoor = -1; bool doorMoved;
        // 빈 곳(땅 · 건물 바닥)을 눌러 끌면 화면 이동
        bool pressEmpty, pressOnBuilding, panning; Vector3 panAnchor; float panH;
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
        FloorPlan plan;
        readonly List<FootprintFrame> warnFrames = new List<FootprintFrame>();
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
            plan = new FloorPlan(transform, floorMat.GetTexture("_BaseMap"));
            Traffic = new Traffic(transform) { balance = StartCapital };
            Traffic.Notice += msg => { Notice?.Invoke(msg); Save(); };
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
            floorMat = new Material(HudShaders.Lit) { name = "LayoutFloor" };
            floorMat.SetTexture("_BaseMap", tex); floorMat.SetColor("_BaseColor", Color.white);
            floorMat.SetTextureScale("_BaseMap", new Vector2(floorW / 10f, floorD / 10f));
            floorMat.SetFloat("_Smoothness", 0.25f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor"; floor.transform.SetParent(transform, false);
            floor.transform.localScale = new Vector3(floorW, 0.1f, floorD);
            floor.transform.position = new Vector3(FloorX0 + floorW / 2, -0.05f, FloorZ0 + floorD / 2);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
            Destroy(floor.GetComponent<Collider>());
            focus = new Vector3(45f, 0, 40f); dist = 160f; pitch = 55f; yaw = 20f;

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
            bool onGround = Ground(ray, FloorY, out var gp);   // 설비는 올린 바닥 위
            foreach (var f in warnFrames) f.y = FloorY;
            ghost.y = selFrame.y = FloorY;

            // 물류센터 영역 그리기 (그리는 동안 트럭은 멈춤)
            if (DrawingSite) { SiteDrawing(inp, ray, overUi); EndFrame(); return; }
            if (!moversGiven && Site.drawn) GiveDefaultMovers();
            Traffic.unlimited = Unlimited;
            Traffic.Tick(Time.deltaTime, Site, Cargo);

            // 라이브러리에서 하역 수단을 끌어 오는 중
            if (dragMover >= 0)
            {
                var size = new Vector2(1.6f, 2.6f);
                var min = new Vector2(gp.x - size.x / 2, gp.z - size.y / 2);
                bool ok = onGround && !overUi && MoverSpotOk(new Vector2(gp.x, gp.z));
                if (onGround && !overUi) ghost.Set(min, size, ok ? HudStyle.Ok : HudStyle.Bad, null); else ghost.Hide();
                EndFrame();
                if (inp.esc) { dragMover = -1; ghost.Hide(); }
                else if (inp.lUp)
                {
                    int k = dragMover; dragMover = -1; ghost.Hide();
                    if (ok) AddMover(k, new Vector2(gp.x, gp.z));
                    else if (overUi && (inp.mouse - dragStartMouse).magnitude < 6f) AddMoverNearDock(k);
                }
                return;
            }

            // 라이브러리에서 끌어 오는 중
            if (dragEntry != null)
            {
                var size = SizeOf(dragEntry);
                var min = Snap(new Vector2(gp.x - size.x / 2, gp.z - size.y / 2));
                bool ok = onGround && !overUi && Free(min, size, null) && CanHandle(dragEntry);
                if (onGround && !overUi) ghost.Set(min, size, ok ? HudStyle.Ok : HudStyle.Bad, dragEntry.name);
                else ghost.Hide();
                EndFrame();
                if (inp.esc) { dragEntry = null; ghost.Hide(); }
                else if (inp.lUp)
                {
                    var e = dragEntry; dragEntry = null; ghost.Hide();
                    if (ok) { if (Pay(EquipCatalog.PriceOf(e), e.name)) Place(e, min, null); }
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
                pressDoor = -1; doorMoved = false; pressMover = null; moverMoved = false;
                pressEmpty = false; panning = false;
                var mv = part == null ? PickMover(ray) : null;
                if (mv != null) { pressModule = null; pressMover = mv; SelectMover(mv); }
                else if (pressModule == null)
                {
                    // 설비가 아니면: 출입문 → 건물 바닥 → 빈 땅
                    pressDoor = PickDoor(ray);
                    if (pressDoor >= 0) SelectDoor(pressDoor);
                    else
                    {
                        // 여백: 끌면 화면 이동, 끌지 않고 떼면 건물 선택(건물 바닥) 또는 선택 해제
                        pressOnBuilding = onGround && InsideBuilding(gp);
                        panH = pressOnBuilding ? FloorY : 0f;
                        pressEmpty = Ground(ray, panH, out panAnchor);
                        if (!pressOnBuilding) Select(null);
                    }
                }
                else if (pressModule != Selected || part != SelectedPart || BuildingSelected || SelectedDoor >= 0) Select(pressModule, part);
                if (pressModule != null && onGround) { moveStartPos = pressModule.transform.position; grabOffset = pressModule.transform.position - gp; }
            }
            // 여백 끌기: 누른 땅 점이 마우스를 따라오도록 화면 이동
            if (inp.lHeld && pressEmpty)
            {
                if (!panning && (inp.mouse - pressMouse).magnitude > 4f) panning = true;
                if (panning && CameraMode == CamMode.FirstPerson) FpLook(inp.mouseDelta);   // 1인칭: 끌어서 둘러보기
                else if (panning && CameraMode == CamMode.Orbit && Ground(ray, panH, out var ph))
                {
                    var dlt = panAnchor - ph; dlt.y = 0;
                    focus += dlt;
                    focus.x = Mathf.Clamp(focus.x, FloorX0, FloorX0 + floorW); focus.z = Mathf.Clamp(focus.z, FloorZ0, FloorZ0 + floorD);
                    UpdateCamera(false);
                }
            }
            if (inp.lUp && pressEmpty)
            {
                if (!panning && pressOnBuilding) SelectBuilding();
                pressEmpty = false; panning = false;
            }
            // 하역 수단 끌어서 옮기기
            if (inp.lHeld && pressMover != null && onGround && (moverMoved || (inp.mouse - pressMouse).magnitude > 6f))
            {
                moverMoved = true;
                var q = new Vector2(gp.x, gp.z);
                if (MoverSpotOk(q)) pressMover.t.position = new Vector3(q.x, FloorY, q.y);
            }
            if (inp.lUp && pressMover != null) { if (moverMoved) Save(); pressMover = null; }
            // 출입문 끌기: 가장 가까운 벽을 따라 이동
            if (inp.lHeld && pressDoor >= 0 && pressDoor < Site.doors.Count && onGround && (doorMoved || (inp.mouse - pressMouse).magnitude > 6f))
            {
                doorMoved = true;
                Plan.NearestWall(Site, new Vector2(gp.x, gp.z), out int side, out float pos);
                var d = Site.doors[pressDoor];
                if (d.side != side || Mathf.Abs(d.pos - pos) > 0.05f) { d.side = side; d.pos = Mathf.Round(pos * 2f) / 2f; SiteChanged?.Invoke(); }
            }
            if (inp.lUp && pressDoor >= 0) { if (doorMoved) { RelocateBlocked(); Save(); } pressDoor = -1; }
            if (inp.lHeld && pressModule != null && pressModule.built && onGround)
            {
                if (moving == null && (inp.mouse - pressMouse).magnitude > 6f) moving = pressModule;
                if (moving != null)
                {
                    var want = gp + grabOffset;
                    var b = moving.localBounds;
                    var min = Snap(new Vector2(want.x + b.min.x, want.z + b.min.z));
                    moving.transform.position = new Vector3(min.x - b.min.x, FloorY, min.y - b.min.z);
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
            else if (inp.del && SelectedDoor >= 0 && !HudInput.Typing) RemoveDoor(SelectedDoor);
            else if (inp.del && SelectedMover != null && !HudInput.Typing) RemoveMover(SelectedMover);
            if (inp.f && Selected != null && !HudInput.Typing) FocusOn(Selected);

            // 화물을 처리할 수 없는 설비는 빨간 틀
            int wf = 0;
            foreach (var m in modules)
            {
                if (m == null || !m.built || CanHandle(m.entry) || m == moving) continue;
                if (wf >= warnFrames.Count) warnFrames.Add(new FootprintFrame("CargoWarning", transform));
                warnFrames[wf++].Set(m.Min, m.Size, HudStyle.Bad, null);
            }
            for (int k = wf; k < warnFrames.Count; k++) warnFrames[k].Hide();
            EndFrame();

            if (Selected != null && moving == null) selFrame.Set(Selected.Min, Selected.Size, HudStyle.Accent, null);
            else if (Selected == null) selFrame.Hide();
            if (SelectedPart != null && (SelectedPart.t == null || SelectedPart.module == null)) Select(Selected);
            if (SelectedPart != null && SelectedPart.t.gameObject.activeInHierarchy) partBox.Set(SelectedPart.WorldBounds(), HudStyle.Accent);
            else if (SelectedMover != null && SelectedMover.t != null) partBox.Set(new Bounds(SelectedMover.t.position + Vector3.up * 1.2f, new Vector3(1.6f, 2.4f, 1.6f)), HudStyle.Accent);
            else partBox.Hide();
        }

        // 도면·화물·동선은 매 프레임 바뀐 것만 다시 그림
        void EndFrame()
        {
            plan.UpdateDrawing(Site, SelectedDoor);
            plan.UpdateCargo(Site, Cargo);
            plan.UpdateFlows(Site, modules, Cargo, !DrawingSite);
        }

        // ------------------------------------------------------------------ 물류센터 건물
        static SiteDef NewSite()
        {
            var s = new SiteDef();
            Plan.DefaultDoors(s);
            return s;
        }

        public void BeginSiteDrawing()
        {
            DrawingSite = true; siteDragging = false; dragEntry = null;
            ghost.Hide();
            DrawingChanged?.Invoke();   // 안내 배너가 먼저 뜨도록 (아래에서 예외가 나도 그리기 안내는 보임)
            try { ExitCamera(); Select(null); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public void EndSiteDrawing(bool useDefault)
        {
            if (useDefault)
            {
                var s = NewSite(); s.h = Site.h; s.drawn = true;
                ApplySite(s, true);
            }
            else if (!Site.drawn) { var s = Site.Clone(); s.drawn = true; ApplySite(s, false); }
            DrawingSite = false; siteDragging = false; ghost.Hide();
            DrawingChanged?.Invoke();
        }

        void SiteDrawing(LayoutInput inp, Ray ray, bool overUi)
        {
            bool on = Ground(ray, 0f, out var p);
            if (inp.esc) { EndSiteDrawing(false); return; }
            if (inp.lDown && !overUi && on) { siteDragging = true; siteStart = p; }
            if (siteDragging && on)
            {
                var r = SiteRect(siteStart, p);
                bool ok = r.width >= Plan.MinSize && r.height >= Plan.MinSize;
                ghost.y = 0f;
                ghost.Set(r.min, r.size, ok ? HudStyle.Accent : HudStyle.Bad, null);
                if (inp.lUp)
                {
                    siteDragging = false; ghost.Hide();
                    if (!ok) Notice?.Invoke($"영역이 너무 작습니다 ({r.width:0} × {r.height:0} m). 최소 {Plan.MinSize:0} × {Plan.MinSize:0} m 이상 끌어 주세요. 휠로 축소하면 넓게 그릴 수 있습니다.");
                    if (ok)
                    {
                        var s = Site.Clone(); s.x = r.x; s.z = r.y; s.w = r.width; s.d = r.height; s.drawn = true;
                        // 문은 입·출고 지점을 마주하는 벽에 다시 배치 (개수는 유지)
                        int nIn = 0, nOut = 0; foreach (var d in s.doors) { if (d.kind == 0) nIn++; else nOut++; }
                        s.doors.Clear();
                        Plan.SetDoorCount(s, 0, Mathf.Max(1, nIn)); Plan.SetDoorCount(s, 1, Mathf.Max(1, nOut));
                        ApplySite(s, true);
                        DrawingSite = false;
                        DrawingChanged?.Invoke();
                        SelectBuilding();
                    }
                }
            }
            else if (inp.lUp) { siteDragging = false; ghost.Hide(); }
        }

        static Rect SiteRect(Vector3 a, Vector3 b)
        {
            float x0 = Mathf.Round(Mathf.Min(a.x, b.x)), x1 = Mathf.Round(Mathf.Max(a.x, b.x));
            float z0 = Mathf.Round(Mathf.Min(a.z, b.z)), z1 = Mathf.Round(Mathf.Max(a.z, b.z));
            var al = Plan.Allowed;
            x0 = Mathf.Clamp(x0, al.xMin, al.xMax); x1 = Mathf.Clamp(x1, al.xMin, al.xMax);
            z0 = Mathf.Clamp(z0, al.yMin, al.yMax); z1 = Mathf.Clamp(z1, al.yMin, al.yMax);
            return Rect.MinMaxRect(x0, z0, x1, z1);
        }

        // 속성 패널 / 그리기에서 건물을 바꿈. 설비는 새 바닥 높이로 올리고, 건물 밖·문 앞으로 밀려난 설비는 근처 빈자리로
        public void ApplySite(SiteDef s, bool keepDoorsInRange)
        {
            s.w = Mathf.Max(Plan.MinSize, s.w); s.d = Mathf.Max(Plan.MinSize, s.d);
            s.h = Mathf.Clamp(s.h, 0f, 2f);
            foreach (var d in s.doors) d.pos = Plan.ClampPos(s, d.side, d.pos);
            Site = s;
            if (SelectedDoor >= Site.doors.Count) SelectedDoor = -1;
            foreach (var m in modules) if (m != null) { var p = m.transform.position; p.y = FloorY; m.transform.position = p; }
            foreach (var mv in Traffic.movers) { var p = mv.t.position; p.y = FloorY; mv.t.position = p; }
            RelocateBlocked();
            Save();
            SiteChanged?.Invoke();
            LayoutChanged?.Invoke();
        }

        public void SetDoorCount(int kind, int n)
        {
            var s = Site.Clone();
            Plan.SetDoorCount(s, kind, n);
            SelectedDoor = -1;
            ApplySite(s, true);
            SelectionChanged?.Invoke();
        }

        public void UpdateDoor(int i, int kind, int side, float pos)
        {
            if (i < 0 || i >= Site.doors.Count) return;
            var s = Site.Clone();
            var d = s.doors[i]; d.kind = kind; d.side = side; d.pos = pos;
            ApplySite(s, true);
        }

        public void RemoveDoor(int i)
        {
            if (i < 0 || i >= Site.doors.Count) return;
            var s = Site.Clone(); s.doors.RemoveAt(i);
            SelectedDoor = -1;
            ApplySite(s, true);
            SelectionChanged?.Invoke();
        }

        public void AddDoor(int kind)
        {
            var s = Site.Clone();
            int side = -1; foreach (var d in s.doors) if (d.kind == kind) { side = d.side; break; }
            if (side < 0) side = Plan.DockSide(s);
            // 같은 벽에서 다른 문과 가장 멀리 떨어진 자리
            float len = Plan.WallLen(s, side), best = len / 2, bestGap = -1;
            for (float p = Plan.DoorW; p < len - Plan.DoorW; p += 1f)
            {
                float g = float.MaxValue;
                foreach (var d in s.doors) if (d.side == side) g = Mathf.Min(g, Mathf.Abs(d.pos - p));
                if (g > bestGap) { bestGap = g; best = p; }
            }
            s.doors.Add(new DoorDef { kind = kind, side = side, pos = best });
            ApplySite(s, true);
            SelectDoor(s.doors.Count - 1);
        }

        void RelocateBlocked()
        {
            foreach (var m in modules)
            {
                if (m == null || !m.built || Free(m.Min, m.Size, m)) continue;
                if (FindFree(m.Min, m.Size, m, out var t))
                    m.transform.position = new Vector3(t.x - m.localBounds.min.x, FloorY, t.y - m.localBounds.min.z);
            }
        }

        bool InsideBuilding(Vector3 p) => p.x >= Site.x && p.x <= Site.x + Site.w && p.z >= Site.z && p.z <= Site.z + Site.d;

        int PickDoor(Ray ray)
        {
            int best = -1; float bd = 3.2f;
            foreach (var h in new[] { FloorY, 0f })
            {
                if (!Ground(ray, h, out var p)) continue;
                for (int i = 0; i < Site.doors.Count; i++)
                {
                    var c = Plan.DoorPoint(Site, Site.doors[i]);
                    float d = (new Vector2(p.x, p.z) - c).magnitude;
                    if (d < bd) { bd = d; best = i; }
                }
                if (best >= 0) return best;
            }
            return best;
        }

        public void SelectBuilding()
        {
            if (BuildingSelected && Selected == null) return;
            Selected = null; SelectedPart = null; BuildingSelected = true; SelectedDoor = -1; SelectedMover = null;
            SelectionChanged?.Invoke();
        }

        public void SelectDoor(int i)
        {
            if (SelectedDoor == i && Selected == null) return;
            Selected = null; SelectedPart = null; BuildingSelected = false; SelectedDoor = i; SelectedMover = null;
            SelectionChanged?.Invoke();
        }

        // ------------------------------------------------------------------ 하역 수단
        public bool BeginDragMover(int kind)
        {
            dragEntry = null; dragMover = kind; dragStartMouse = LayoutInput.Read().mouse;
            return true;
        }

        bool MoverSpotOk(Vector2 p)
        {
            if (!InsideBuilding(new Vector3(p.x, 0, p.y))) return false;
            if (p.x < Site.x + 1.5f || p.y < Site.z + 1.5f || p.x > Site.x + Site.w - 1.5f || p.y > Site.z + Site.d - 1.5f) return false;
            if (Plan.HitsStage(Site, p - new Vector2(0.8f, 0.8f), new Vector2(1.6f, 1.6f), 0f)) return false;   // 대기 구역 · 평치 위는 안 됨
            foreach (var m in modules)
            {
                if (m == null) continue;
                var mn = m.Min; var sz = m.Size;
                if (p.x > mn.x - 1f && p.x < mn.x + sz.x + 1f && p.y > mn.y - 1f && p.y < mn.y + sz.y + 1f) return false;
            }
            return true;
        }

        public Mover AddMover(int kind, Vector2 p) => Pay(MoverInfo.Price(kind), MoverInfo.Name(kind)) ? AddMover(kind, p, true) : null;

        Mover AddMover(int kind, Vector2 p, bool select)
        {
            var mv = Traffic.AddMover(kind, new Vector3(p.x, FloorY, p.y));
            if (select) SelectMover(mv);
            Save();
            return mv;
        }

        // 센터를 처음 정하면 지게차 1대와 작업자 1명을 기본으로 줌
        bool moversGiven;
        void GiveDefaultMovers()
        {
            moversGiven = true;
            if (Traffic.movers.Count == 0)
            {
                AddMoverNearDock(MoverInfo.Forklift, false);
                AddMoverNearDock(MoverInfo.Worker, false);
            }
            Save();
        }

        // 눌러서 추가: 해당 화물을 다루는 문 안쪽 대기 구역 옆 빈자리
        void AddMoverNearDock(int kind) { if (Pay(MoverInfo.Price(kind), MoverInfo.Name(kind))) AddMoverNearDock(kind, true); }

        void AddMoverNearDock(int kind, bool select)
        {
            foreach (var d in Site.doors)
            {
                var r = Plan.StageRect(Site, d); var n = -Plan.Outward(d.side);
                var c = r.center + n * (Plan.StageDepth / 2 + 2.5f);
                for (int k = 0; k < 12; k++)
                {
                    var q = c + new Vector2((k % 4 - 1.5f) * 2f, (k / 4) * 2.5f) * (d.side < 2 ? 1 : -1);
                    bool taken = false;
                    foreach (var o in Traffic.movers) if ((new Vector2(o.t.position.x, o.t.position.z) - q).sqrMagnitude < 2.5f) taken = true;
                    if (!taken && MoverSpotOk(q)) { AddMover(kind, q, select); return; }
                }
            }
            var cc = new Vector2(Site.x + Site.w / 2, Site.z + Site.d / 2);
            if (MoverSpotOk(cc)) AddMover(kind, cc, select);
        }

        public void RemoveMover(Mover m)
        {
            if (m == null) return;
            if (SelectedMover == m) Select(null);
            Refund(MoverInfo.Price(m.kind), MoverInfo.Name(m.kind));
            Traffic.RemoveMover(m);
            Save();
        }

        public void SelectMover(Mover m)
        {
            if (SelectedMover == m) return;
            Selected = null; SelectedPart = null; BuildingSelected = false; SelectedDoor = -1; SelectedMover = m;
            SelectionChanged?.Invoke();
        }

        Mover PickMover(Ray ray)
        {
            if (!Ground(ray, FloorY + 0.8f, out var p)) return null;
            Mover best = null; float bd = 1.6f * 1.6f;
            foreach (var m in Traffic.movers)
            {
                float d = (new Vector2(m.t.position.x - p.x, m.t.position.z - p.z)).sqrMagnitude;
                if (d < bd) { bd = d; best = m; }
            }
            return best;
        }

        // ---------------------------------------------------------------- 카메라 모드: 궤도 / 1인칭 / 추적
        public enum CamMode { Orbit, FirstPerson, Follow }
        public CamMode CameraMode { get; private set; }
        public string FollowName { get; private set; }
        public event Action CameraModeChanged;
        const float EyeH = 1.65f;
        Vector3 fpPos; float fpYaw, fpPitch;
        Transform followT; int cargoCursor = -1;

        float GroundH(Vector3 p) => InsideBuilding(p) ? FloorY : 0f;

        // 센터 안 첫 문 안쪽 통로에서 건물 안쪽을 보며 시작 (센터가 없으면 지금 보는 곳)
        public void EnterFirstPerson()
        {
            var p = new Vector3(focus.x, 0, focus.z); float y0 = yaw;
            if (Site.drawn && Site.doors.Count > 0)
            {
                var d = Site.doors[0]; var n = -Plan.Outward(d.side);
                var q = Plan.DoorInside(Site, d) + n * (Plan.StageDepth + 2f);
                p = new Vector3(q.x, 0, q.y);
                y0 = Mathf.Atan2(n.x, n.y) * Mathf.Rad2Deg;
            }
            fpPos = new Vector3(p.x, GroundH(p) + EyeH, p.z); fpYaw = y0; fpPitch = 0f;
            followT = null; FollowName = null;
            CameraMode = CamMode.FirstPerson;
            cam.fieldOfView = 65f;
            CameraModeChanged?.Invoke();
        }

        public void Follow(Transform t, string name)
        {
            if (t == null) return;
            if (CameraMode != CamMode.Follow) { dist = 14f; pitch = 35f; }
            if (CameraMode == CamMode.FirstPerson) yaw = fpYaw;
            followT = t; FollowName = name;
            CameraMode = CamMode.Follow;
            cam.fieldOfView = 40f;
            CameraModeChanged?.Invoke();
        }

        // 선택한 하역 수단 · 설비 부품(로봇 · 셔틀 등)을 따라감. 아무것도 없으면 첫 하역 수단
        public bool FollowEquipment()
        {
            if (SelectedMover != null) { Follow(SelectedMover.t, SelectedMover.t.name); return true; }
            if (SelectedPart != null && SelectedPart.t != null) { Follow(SelectedPart.t, $"{SelectedPart.module.entry.name} {SelectedPart.Label}"); return true; }
            if (Traffic.movers.Count > 0) { var m = Traffic.movers[0]; Follow(m.t, m.t.name); return true; }
            return false;
        }

        // 화물을 차례로 따라감 (옮기는 중인 화물부터)
        public bool FollowCargo()
        {
            var t = Traffic.NextCargo(ref cargoCursor, out var label);
            if (t == null) return false;
            Follow(t, label);
            return true;
        }

        public void ExitCamera()
        {
            if (CameraMode == CamMode.Orbit) return;
            if (CameraMode == CamMode.FirstPerson) { focus = new Vector3(fpPos.x, 0, fpPos.z); yaw = fpYaw; pitch = 50f; dist = 40f; }
            focus.y = 0;
            CameraMode = CamMode.Orbit; followT = null; FollowName = null;
            cam.fieldOfView = 40f;
            CameraModeChanged?.Invoke();
        }

        void FpLook(Vector2 delta)
        {
            fpYaw += delta.x * 0.2f;
            fpPitch = Mathf.Clamp(fpPitch - delta.y * 0.2f, -70f, 70f);
        }

        bool FpBlocked(Vector3 p)
        {
            foreach (var m in modules)
            {
                if (m == null) continue;
                var mn = m.Min; var sz = m.Size;
                if (p.x > mn.x - 0.3f && p.x < mn.x + sz.x + 0.3f && p.z > mn.y - 0.3f && p.z < mn.y + sz.y + 0.3f) return true;
            }
            return false;
        }

        // 1인칭: WASD 걷기 (Shift 달리기), 오른쪽 끌기 · 여백 끌기로 둘러보기, Q/E 돌기, Esc 나가기
        void FirstPersonControls(LayoutInput i, float dt)
        {
            if (i.esc) { ExitCamera(); return; }
            if (i.rHeld) FpLook(i.mouseDelta);
            if (!HudInput.Typing)
            {
                if (i.q) fpYaw -= 90f * dt; if (i.e) fpYaw += 90f * dt;
                var f = Quaternion.Euler(0, fpYaw, 0) * Vector3.forward; var r = Quaternion.Euler(0, fpYaw, 0) * Vector3.right;
                var mv = Vector3.zero;
                if (i.w) mv += f; if (i.s) mv -= f; if (i.d) mv += r; if (i.a) mv -= r;
                if (mv.sqrMagnitude > 0.01f)
                {
                    var np = fpPos + mv.normalized * (i.shift ? 9f : 4f) * dt;
                    np.x = Mathf.Clamp(np.x, FloorX0, FloorX0 + floorW); np.z = Mathf.Clamp(np.z, FloorZ0, FloorZ0 + floorD);
                    if (!FpBlocked(np)) fpPos = np;
                    else if (!FpBlocked(new Vector3(np.x, fpPos.y, fpPos.z))) fpPos.x = np.x;   // 벽 따라 미끄러지기
                    else if (!FpBlocked(new Vector3(fpPos.x, fpPos.y, np.z))) fpPos.z = np.z;
                }
            }
            fpPos.y = Mathf.Lerp(fpPos.y, GroundH(fpPos) + EyeH, 1f - Mathf.Exp(-10f * dt));   // 바닥 높이가 바뀌면 부드럽게
            cam.transform.SetPositionAndRotation(fpPos, Quaternion.Euler(fpPitch, fpYaw, 0));
        }

        // 추적: 대상을 가운데 두고 따라감. 오른쪽 끌기 회전, 휠 거리, Esc 나가기
        void FollowControls(LayoutInput i, bool overUi, float dt)
        {
            if (followT == null) { ExitCamera(); Notice?.Invoke("추적하던 대상이 사라져 추적을 끝냈습니다"); return; }
            if (i.esc) { ExitCamera(); return; }
            if (i.rHeld) { yaw += i.mouseDelta.x * 0.25f; pitch = Mathf.Clamp(pitch - i.mouseDelta.y * 0.2f, 5f, 88f); }
            if (!HudInput.Typing) { if (i.q) yaw += 70f * dt; if (i.e) yaw -= 70f * dt; }
            if (!overUi && Mathf.Abs(i.scroll) > 0.001f) dist = Mathf.Clamp(dist * Mathf.Pow(0.85f, i.scroll), 3f, 80f);
            var tp = followT.position + Vector3.up * 1.2f;
            focus = Vector3.Lerp(focus, tp, 1f - Mathf.Exp(-8f * dt));
            UpdateCamera(false);
        }

        void CameraControls(LayoutInput i, bool overUi)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (CameraMode == CamMode.FirstPerson) { FirstPersonControls(i, dt); return; }
            if (CameraMode == CamMode.Follow) { FollowControls(i, overUi, dt); return; }
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
                    // 바닥을 잡고 끄는 느낌: 마우스를 움직인 방향으로 장면이 따라옴
                    focus -= (right * i.mouseDelta.x + fwd * i.mouseDelta.y) * dist * 0.0018f;
                }
                if (!overUi && Mathf.Abs(i.scroll) > 0.001f)
                {
                    // 마우스가 가리키는 바닥 점 쪽으로 확대·축소
                    float nd = Mathf.Clamp(dist * Mathf.Pow(0.85f, i.scroll), 3f, 260f);
                    if (Ground(cam.ScreenPointToRay(i.mouse), FloorY, out var gp))
                    {
                        float k = 1f - nd / dist;
                        var tgt = new Vector3(gp.x, 0, gp.z);
                        focus += (tgt - focus) * k;
                    }
                    dist = nd;
                }
            }
            focus.x = Mathf.Clamp(focus.x, FloorX0, FloorX0 + floorW); focus.z = Mathf.Clamp(focus.z, FloorZ0, FloorZ0 + floorD);
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
        public bool BeginDrag(EquipEntry e)
        {
            if (!CanHandle(e)) return false;
            if (!CanAfford(EquipCatalog.PriceOf(e)))
            {
                Notice?.Invoke($"잔고가 부족합니다. {e.name}: {EquipCatalog.Money(EquipCatalog.PriceOf(e))} 필요, 잔고 {EquipCatalog.Money(Traffic.balance)}");
                return true;   // 끌기는 시작하지 않음 (안내만)
            }
            dragEntry = e; dragStartMouse = LayoutInput.Read().mouse;
            return true;
        }

        public void SetCargo(CargoKind k)
        {
            if (k == CargoKind.None) k = CargoKind.Pallet;
            if (Cargo == k) return;
            Cargo = k;
            if (dragEntry != null && !CanHandle(dragEntry)) { dragEntry = null; ghost.Hide(); }
            Save();
            CargoChanged?.Invoke();
            LayoutChanged?.Invoke();
        }

        public Vector2 SizeOf(EquipEntry e) =>
            sizeCache.TryGetValue(e.id, out var s) ? new Vector2(s.x, s.z) : new Vector2(12f, 10f);

        static Vector2 Snap(Vector2 v) => new Vector2(Mathf.Round(v.x), Mathf.Round(v.y));

        bool Free(Vector2 min, Vector2 size, PlacedModule except)
        {
            const float gap = 0.5f;   // 설비 사이 최소 통로 0.5 m
            if (!Plan.Inside(Site, min, size) || Plan.HitsStage(Site, min, size, gap)) return false;   // 건물 안, 도크 대기 구역 밖
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
            if (FindFree(c, size, null, out var min) && Pay(EquipCatalog.PriceOf(e), e.name)) Place(e, min, null);
        }

        // start 근처에서 나선형으로 빈자리 찾기 (건물 안, 다른 설비·도크와 겹치지 않는 곳)
        bool FindFree(Vector2 start, Vector2 size, PlacedModule except, out Vector2 res)
        {
            for (int r = 0; r < 60; r++)
                for (int k = 0; k < Mathf.Max(1, r * 8); k++)
                {
                    float a = k / Mathf.Max(1f, r * 8f) * Mathf.PI * 2;
                    res = Snap(start + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 2f);
                    if (Free(res, size, except)) return true;
                }
            res = start;
            return false;
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
            go.transform.position = new Vector3(min.x, FloorY, min.y);
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
            m.transform.position = new Vector3(min.x - b.min.x, FloorY, min.y - b.min.z);
            m.built = true;
            ScanParts(m);
            if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene) SceneManager.SetActiveScene(prevActive);
            // 실제 크기로 겹치면 근처 빈자리로
            if (!Free(m.Min, m.Size, m) && FindFree(m.Min, m.Size, m, out var t))
                m.transform.position = new Vector3(t.x - b.min.x, FloorY, t.y - b.min.z);
            if (select) Select(m);
            Save();
            LayoutChanged?.Invoke();
        }

        public void Remove(PlacedModule m)
        {
            if (m == null) return;
            Refund(EquipCatalog.PriceOf(m.entry), m.entry.name);
            modules.Remove(m);
            if (Selected == m) Select(null);
            Destroy(m.gameObject);
            Save();
            LayoutChanged?.Invoke();
        }

        public void ClearAll()
        {
            int back = 0;
            foreach (var m in modules) if (m != null) { back += EquipCatalog.PriceOf(m.entry); Destroy(m.gameObject); }
            if (back > 0) Refund(back, "설비 전부");
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
            if (Selected == m && SelectedPart == part && !BuildingSelected && SelectedDoor < 0 && SelectedMover == null) return;
            Selected = m; SelectedPart = part; BuildingSelected = false; SelectedDoor = -1; SelectedMover = null;
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

        static bool Ground(Ray ray, float height, out Vector3 p)
        {
            var pl = new Plane(Vector3.up, new Vector3(0, height, 0));
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
            s.cargo = (int)Cargo;
            s.site = Site;
            foreach (var mv in Traffic.movers) s.movers.Add(new MoverSave { kind = mv.kind, x = mv.t.position.x, z = mv.t.position.z });
            s.moversGiven = moversGiven;
            s.unlimited = Unlimited;
            s.balance = Traffic.balance; s.fundsInit = true;
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
            var ck = (CargoKind)s.cargo;
            if (Array.IndexOf(CargoInfo.All, ck) >= 0) Cargo = ck;
            if (s.site != null && s.site.w >= Plan.MinSize && s.site.d >= Plan.MinSize) { Site = s.site; if (Site.doors == null) Site.doors = new List<DoorDef>(); if (Site.doors.Count == 0) Plan.DefaultDoors(Site); }
            for (int i = 0; i < s.sizeIds.Count && i < s.sizes.Count; i++) sizeCache[s.sizeIds[i]] = s.sizes[i];
            if (s.movers != null) foreach (var mv in s.movers) Traffic.AddMover(mv.kind, new Vector3(mv.x, FloorY, mv.z));
            moversGiven = s.moversGiven || (s.movers != null && s.movers.Count > 0);
            Unlimited = s.unlimited;
            Traffic.balance = s.fundsInit ? s.balance : StartCapital;
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
            mat = new Material(HudShaders.Unlit) { name = name };
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
            mat = new Material(HudShaders.Unlit) { name = name };
            for (int i = 0; i < 4; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(root, false);
                var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
                bars[i] = g.transform;
            }
        }

        public float y;   // 그릴 바닥 높이

        public void Hide() => root.gameObject.SetActive(false);

        public void Set(Vector2 min, Vector2 size, Color c, string label)
        {
            root.gameObject.SetActive(true);
            root.position = new Vector3(0, y, 0);
            mat.SetColor("_BaseColor", c);
            const float w = 0.18f, h = 0.06f;
            float x0 = min.x, z0 = min.y, x1 = min.x + size.x, z1 = min.y + size.y;
            bars[0].localPosition = new Vector3((x0 + x1) / 2, h / 2, z0); bars[0].localScale = new Vector3(size.x + w, h, w);
            bars[1].localPosition = new Vector3((x0 + x1) / 2, h / 2, z1); bars[1].localScale = new Vector3(size.x + w, h, w);
            bars[2].localPosition = new Vector3(x0, h / 2, (z0 + z1) / 2); bars[2].localScale = new Vector3(w, h, size.y + w);
            bars[3].localPosition = new Vector3(x1, h / 2, (z0 + z1) / 2); bars[3].localScale = new Vector3(w, h, size.y + w);
        }
    }
}
