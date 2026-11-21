// 통합 HUD (플레이 모드, UI Toolkit). 두 가지 모드가 있습니다.
//  ■ 배치 (기본): 넓은 메인 공간에 왼쪽 라이브러리의 설비를 끌어다 설치하는 게임. 설비를 클릭하면 오른쪽 속성 패널에
//    속성이 나오고, 그 왼쪽 대시보드에 공장 전체 합계와 선택한 설비의 실시간 KPI 가 나옵니다. (LayoutWorld.cs)
//  ■ 애니메이션 (옵션): 설비 하나의 30초 시네마틱 장면. 타임라인·카메라·자막·반복·녹화.
// 위쪽 막대: 모드 전환 / 재생·일시정지 / 속도 / (애니메이션) 타임라인 등 / 테마(다크·라이트) / HUD 숨김 / 닫기
// 단축키: H HUD 숨김/표시, Space 재생/일시정지, (애니메이션) R 처음부터, ← → 5초 이동, C 카메라
//         (배치) 마우스 오른쪽 끌기 회전, 가운데 끌기·WASD 이동, 휠 확대, F 선택 설비로, Delete 삭제, Esc 취소
// 녹화(애니메이션 > <설비> > Record 30s Movie) 중에는 HUD 가 자동으로 숨고 시간도 건드리지 않습니다.
// 끄기: 메뉴 애니메이션 > 통합 HUD > 플레이 모드에서 HUD 표시
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace EquipFX
{
    // 마우스가 HUD 위에 있으면 장면 카메라의 마우스 조작을 막음 / 글자 입력 중이면 단축키를 막음
    public static class HudInput
    {
        public static bool PointerOverUi;
        public static bool Typing;

        // 마우스 휠 값 → 눈금 수. Input System 은 플랫폼·버전에 따라 한 눈금이 ±120 또는 ±1 이므로 둘 다 맞춤
        public static float WheelNotches(float raw)
        {
            if (Mathf.Abs(raw) >= 20f) raw /= 120f;
            return Mathf.Clamp(raw, -4f, 4f);
        }
    }

    public class EquipHud : MonoBehaviour
    {
        public const string PrefEnabled = "EquipFX.Hud.Enabled";
        public const float MovieLength = 30f;
        const float FastForward = 24f;
        const float RateWindow = 60f;
        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        // 에디터 쪽에서 채움 (Assets/Common/Editor/EquipHudEditor.cs)
        public static Func<string, bool> EditorLoadScene;
        public static Action<EquipEntry> EditorRecord;
        public static Action EditorQuit;
        public static Func<bool> EditorRecordPending;   // 에디터 녹화 메뉴로 플레이 모드가 시작됐는지

        public static EquipHud Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; HudInput.PointerOverUi = false; HudInput.Typing = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || PlayerPrefs.GetInt(PrefEnabled, 1) == 0 || Instance != null) return;
            var go = new GameObject("EquipHud");
            DontDestroyOnLoad(go);
            go.AddComponent<EquipHud>();
        }

        // ------------------------------------------------------------------ 상태
        EquipEntry entry;              // 애니메이션: 열린 설비 / 배치: 선택한 설비 종류
        Component view;                // 애니메이션: 장면의 View / 배치: 선택한 모듈의 View
        LayoutWorld world;             // 배치 모드면 메인 공간
        PlacedModule selModule;
        float speed = 1f;
        bool paused, loop = true, captionsOn = true, hudVisible = true, recording, loading, recordPending;
        float clockStart = -1f, ffTarget = -1f, pendingSeek = -1f;
        float nextUiRefresh, toastUntil;
        bool overTop, overLeft, overDash, overRight, overPopup, overModal, plantDirty = true, marksSet;
        static bool cargoAsked;   // 이번 실행에서 화물 선택 창을 보였는지
        readonly Dictionary<string, Dictionary<string, double>> overrides = new Dictionary<string, Dictionary<string, double>>();
        readonly List<Vector2> samples = new List<Vector2>();
        readonly List<float> rates = new List<float>();
        int sceneSerial;

        bool LayoutMode => world != null;
        float Elapsed => clockStart < 0 ? 0f : Time.time - clockStart;

        // ------------------------------------------------------------------ UI 요소
        UIDocument doc;
        VisualElement root, topBar, leftPanel, dashPanel, rightPanel, loadingOverlay, animTools, layoutTools;
        Label toastLbl, loadingLbl, timeLbl;
        IconButton playBtn;
        TimelineBar timeline;
        Button modeLayoutBtn, modeAnimBtn, camCineBtn, camOrbitBtn, captionBtn, loopBtn, recBtn;
        HudDropdown speedDd, themeDd;
        // 화물 선택 · 도크 이름표
        VisualElement cargoModal, cargoTiles;
        Button cargoChip;
        Label cargoLbl;
        CargoKind modalPick = CargoKind.Pallet;
        bool modalStartFlow, siteDirty, overBanner, modalUnlimited;
        Button modeFundsBtn, modeFreeBtn;
        // 메인 화면 탭: 3D / 물류흐름도(MFD)
        VisualElement mfdPanel;
        MfdView mfd;
        Label mfdSummary;
        Button tab3dBtn, tabMfdBtn;
        bool mfdOn, overMfd, cadOn;
        VisualElement cadPanel;
        CadView cad;
        Button tabCadBtn;
        string mfdSig;
        VisualElement siteBanner, siteBox;
        Label siteBannerLbl, paramTitle;
        Button siteBannerKeep;
        static readonly HashSet<string> collapsedGroups = new HashSet<string>();
        readonly Dictionary<EquipEntry, VisualElement> cards = new Dictionary<EquipEntry, VisualElement>();
        // 대시보드
        VisualElement plantBox, plantTiles, plantList, selDashBox, kpiGrid;
        Label selDashTitle, tpLbl, tpUnitLbl, recentLbl, chartMaxLbl, simTimeLbl, robotsLbl;
        Sparkline chart;
        readonly List<(KpiDef def, Label val)> kpiLabels = new List<(KpiDef, Label)>();
        readonly List<(PlacedModule m, Label val)> plantRows = new List<(PlacedModule, Label)>();
        readonly Dictionary<string, Label> plantUnitLbls = new Dictionary<string, Label>();
        Label plantCountLbl, plantRobotLbl, balanceLbl, inDoneLbl, outDoneLbl, trucksLbl;
        // 속성
        VisualElement propBody, paramBox, specBox, specBody, moduleBox;
        Label catLbl, titleLbl, koLbl, descLbl, posLbl;
        // 하위 오브젝트 (로봇·셔틀·토트 …)
        VisualElement partCard, partRowsBox;
        Label partTitleLbl, partSubLbl;
        PartRef shownPart;
        readonly List<(string key, Label val)> partRows = new List<(string, Label)>();
        readonly List<(string key, string val)> partTmp = new List<(string, string)>();
        readonly List<(ParamDef def, TextField field, FieldInfo fi)> paramFields = new List<(ParamDef, TextField, FieldInfo)>();

        // ------------------------------------------------------------------ 시작
        void Awake()
        {
            Instance = this;
            Time.timeScale = 1f;
            HudStyle.Dark = PlayerPrefs.GetInt(HudStyle.PrefTheme, 0) == 0;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureEventSystem();
            CreateDocument();
            BuildContent();
            DetectScene();
            recordPending = RecordPending();
        }

        void Start()
        {
            // 배치가 기본: 어떤 장면에서 Play 해도 배치 공간으로 시작 (녹화 메뉴로 시작한 경우만 그 설비 애니메이션 그대로)
            if (world == null && !recording && !recordPending) OpenLayout();
        }

        bool RecordPending()
        {
            if (EditorRecordPending != null && EditorRecordPending()) return true;
            if (entry == null || entry.captureType == null || view == null) return false;
            var cap = view.GetComponent(entry.captureType);
            var f = entry.captureType.GetField("recordOnPlay", BindingFlags.Instance | BindingFlags.Public);
            return cap != null && f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(cap);
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this) { Instance = null; HudInput.PointerOverUi = false; }
            if (!recording) Time.timeScale = 1f;
        }

        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem (HUD)");
            es.transform.SetParent(transform, false);
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            var m = es.AddComponent<InputSystemUIInputModule>();
            m.AssignDefaultActions();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode) => DetectScene();

        void DetectScene()
        {
            var w = FindFirstObjectByType<LayoutWorld>();
            if (w != null) { AttachWorld(w); return; }
            DetachWorld();
            foreach (var e in EquipCatalog.All)
            {
                var v = FindFirstObjectByType(e.viewType) as Component;
                if (v != null && !GetBool(v, "moduleMode", false)) { Attach(e, v); return; }
            }
            if (!loading) { entry = null; view = null; RefreshEntryUi(); }
        }

        void AttachWorld(LayoutWorld w)
        {
            if (world == w) return;
            DetachWorld();
            world = w;
            world.SelectionChanged += OnSelectionChanged;
            world.LayoutChanged += OnLayoutChanged;
            world.CargoChanged += OnCargoChanged;
            world.SiteChanged += OnSiteChanged;
            world.Notice += OnNotice;
            world.DrawingChanged += OnDrawingChanged;
            entry = null; view = null; selModule = null; clockStart = -1f; ffTarget = -1f; pendingSeek = -1f;
            samples.Clear(); rates.Clear(); plantDirty = true;
            RefreshEntryUi(); UpdateModeUi(); ApplyTimeScale();
        }

        void DetachWorld()
        {
            if (world == null) return;
            world.SelectionChanged -= OnSelectionChanged;
            world.LayoutChanged -= OnLayoutChanged;
            world.CargoChanged -= OnCargoChanged;
            world.SiteChanged -= OnSiteChanged;
            world.Notice -= OnNotice;
            world.DrawingChanged -= OnDrawingChanged;
            world = null; selModule = null; plantDirty = true;
            UpdateModeUi();
        }

        void OnSelectionChanged()
        {
            var newSel = world != null ? world.Selected : null;
            if (newSel != null && newSel == selModule) { RefreshPart(); return; }
            siteDirty = false;   // 같은 설비 안에서 하위 오브젝트만 바뀜
            selModule = newSel;
            entry = selModule != null ? selModule.entry : null;
            view = selModule != null ? selModule.view : null;
            samples.Clear(); rates.Clear();
            RefreshEntryUi();
        }

        void OnLayoutChanged() => plantDirty = true;

        void OnCargoChanged() { RefreshCards(); RefreshEntryUi(false); plantDirty = true; }

        void OnSiteChanged() => siteDirty = true;

        void OnNotice(string msg) { Toast(msg); toastUntil = Time.unscaledTime + 6f; }

        void OnDrawingChanged()
        {
            if (siteBanner == null || world == null) return;
            siteBanner.style.display = world.DrawingSite ? DisplayStyle.Flex : DisplayStyle.None;
            siteBannerKeep.style.display = world.Site.drawn ? DisplayStyle.Flex : DisplayStyle.None;
            if (!world.DrawingSite) { overBanner = false; UpdOver(); }
        }

        void Attach(EquipEntry e, Component v)
        {
            bool changed = entry != e;
            entry = e; view = v; clockStart = -1f; marksSet = false;
            samples.Clear(); rates.Clear();
            SetField(v, "showHud", false);
            if (overrides.TryGetValue(e.id, out var map)) ApplyPars(v, map);
            RefreshEntryUi(changed);
            UpdateModeUi();
            ApplyTimeScale();
        }

        // ------------------------------------------------------------------ 매 프레임
        void Update()
        {
            if (!recording && (Time.captureFramerate > 0 || CaptionPresenter.Recording))
            {
                recording = true; ffTarget = -1f; pendingSeek = -1f;
                Time.timeScale = 1f;
                SetCaptions(true);
                root.style.display = DisplayStyle.None;
                HudInput.PointerOverUi = false;
            }
            if (recording) return;

            HudInput.Typing = TypingInField();
            HandleKeys();

            if (LayoutMode)
            {
                if (selModule == null && world.Selected != null) OnSelectionChanged();
                if (selModule != null && view == null && selModule.view != null) { view = selModule.view; RefreshEntryUi(); }
            }
            else
            {
                if (view == null && entry != null && !loading) { entry = null; RefreshEntryUi(); }
                if (view != null && entry != null && clockStart < 0 && entry.ready(view))
                {
                    clockStart = Time.time;
                    if (pendingSeek > 0) ffTarget = pendingSeek;
                    pendingSeek = -1f;
                    ApplyTimeScale();
                }
                if (ffTarget >= 0 && clockStart >= 0)
                {
                    float rem = ffTarget - Elapsed;
                    if (rem <= 0.002f) { ffTarget = -1f; ApplyTimeScale(); }
                    else Time.timeScale = Mathf.Clamp(rem / Mathf.Max(Time.unscaledDeltaTime, 1f / 240f), 0.01f, FastForward);
                }
                else if (loop && !loading && clockStart >= 0 && Elapsed >= MovieLength) Restart(0f);
            }

            if (LayoutMode && !loading && !cargoAsked) ShowCargoModal(true);
            UpdateDockLabels();
            Sample();
            UpdateUiFast();
            if (Time.unscaledTime >= nextUiRefresh) { nextUiRefresh = Time.unscaledTime + 0.2f; UpdateUiSlow(); }
        }

        bool Ready => view != null && entry != null && entry.ready(view) && (!LayoutMode || (selModule != null && selModule.built));

        void Sample()
        {
            if (!Ready) return;
            float t = (float)entry.simTime(view), done = (float)entry.doneCount(view);
            if (samples.Count > 0 && t - samples[samples.Count - 1].x < 1f) return;
            samples.Add(new Vector2(t, done));
            int j = samples.Count - 1;
            while (j > 0 && t - samples[j - 1].x <= RateWindow) j--;
            var a = samples[j];
            float dt = t - a.x;
            if (dt >= 5f) rates.Add((done - a.y) / dt * 3600f);
            if (samples.Count > 400) samples.RemoveRange(0, samples.Count - 400);
            if (rates.Count > 180) rates.RemoveRange(0, rates.Count - 180);
        }

        // ------------------------------------------------------------------ 키보드
        void HandleKeys()
        {
            if (HudInput.Typing) return;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; if (k == null) return;
            bool h = k.hKey.wasPressedThisFrame, sp = k.spaceKey.wasPressedThisFrame, r = k.rKey.wasPressedThisFrame,
                 l = k.leftArrowKey.wasPressedThisFrame, rt = k.rightArrowKey.wasPressedThisFrame, c = k.cKey.wasPressedThisFrame;
#else
            bool h = Input.GetKeyDown(KeyCode.H), sp = Input.GetKeyDown(KeyCode.Space), r = Input.GetKeyDown(KeyCode.R),
                 l = Input.GetKeyDown(KeyCode.LeftArrow), rt = Input.GetKeyDown(KeyCode.RightArrow), c = Input.GetKeyDown(KeyCode.C);
#endif
            if (h) SetHudVisible(!hudVisible);
            if (sp) TogglePause();
            if (LayoutMode) return;
            if (r) Restart(0f);
            if (l) Seek(CurrentTime() - 5f);
            if (rt) Seek(CurrentTime() + 5f);
            if (c) SetCinematic(!GetBool(view, "cinematicCamera", true));
        }

        bool TypingInField()
        {
            var fe = root?.panel?.focusController?.focusedElement as VisualElement;
            return fe != null && (fe is TextField || fe.GetFirstAncestorOfType<TextField>() != null);
        }

        // ------------------------------------------------------------------ 재생 제어
        float CurrentTime() => ffTarget >= 0 ? ffTarget : pendingSeek > 0 ? pendingSeek : Elapsed;

        void ApplyTimeScale()
        {
            if (recording) return;
            if (!LayoutMode && (ffTarget >= 0 || loading)) { Time.timeScale = 1f; return; }
            Time.timeScale = paused ? 0f : speed;
        }

        void TogglePause() { paused = !paused; ApplyTimeScale(); UpdateControls(); }

        void SetSpeed(float s) { speed = s; paused = false; ApplyTimeScale(); UpdateControls(); }

        public void Seek(float t)
        {
            if (entry == null || LayoutMode) return;
            t = Mathf.Clamp(t, 0f, MovieLength - 0.05f);
            if (clockStart >= 0 && !loading && t >= Elapsed) { ffTarget = t; return; }
            Restart(t);
        }

        public void Restart(float seekTo)
        {
            if (entry == null || loading || LayoutMode) return;
            StartCoroutine(LoadRoutine(entry, seekTo));
        }

        public void OpenAnimation(EquipEntry e)
        {
            if (e == null || loading) return;
            StartCoroutine(LoadRoutine(e, 0f));
        }

        public void OpenLayout()
        {
            if (loading || LayoutMode) return;
            StartCoroutine(LayoutRoutine());
        }

        void ShowLoading(string msg)
        {
            loadingLbl.text = msg;
            loadingOverlay.style.display = DisplayStyle.Flex;
        }

        // 새 빈 장면을 만들어 활성화하고 나머지 장면은 내림
        IEnumerator FreshScene(string name)
        {
            var s = SceneManager.CreateScene($"{name} (HUD {++sceneSerial})");
            SceneManager.SetActiveScene(s);
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc == s || !sc.isLoaded) continue;
                var op = SceneManager.UnloadSceneAsync(sc);
                while (op != null && !op.isDone) yield return null;
            }
        }

        bool LoadSceneFile(string path)
        {
            if (Application.isEditor) return EditorLoadScene != null && EditorLoadScene(path);
            if (SceneUtility.GetBuildIndexByScenePath(path) < 0) return false;
            SceneManager.LoadSceneAsync(path, LoadSceneMode.Single);
            return true;
        }

        IEnumerator LayoutRoutine()
        {
            loading = true; ffTarget = -1f; pendingSeek = -1f; view = null; entry = null;
            ShowLoading("배치 공간 여는 중…");
            Time.timeScale = 1f;
            yield return null;
            bool viaFile = LoadSceneFile(LayoutWorld.ScenePath);
            if (viaFile)
            {
                float until = Time.unscaledTime + 15f;
                while (world == null && Time.unscaledTime < until) yield return null;
                if (world == null) viaFile = false;
            }
            if (!viaFile)
            {
                yield return StartCoroutine(FreshScene("LayoutSpace"));
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
                var w = new GameObject("LayoutWorld").AddComponent<LayoutWorld>();
                AttachWorld(w);
            }
            Resources.UnloadUnusedAssets();
            loading = false;
            loadingOverlay.style.display = DisplayStyle.None;
            ApplyTimeScale(); UpdateModeUi(); UpdateControls();
            if (world != null && world.modules.Count == 0) Toast("왼쪽 라이브러리에서 설비를 끌어다 바닥에 놓으세요");
        }

        IEnumerator LoadRoutine(EquipEntry e, float seekTo)
        {
            loading = true; ffTarget = -1f; pendingSeek = seekTo > 0 ? seekTo : -1f;
            clockStart = -1f; view = null;
            bool changed = entry != e;
            entry = e;
            if (changed) RefreshEntryUi(true);
            ApplyTimeScale();
            ShowLoading($"{e.name} 불러오는 중…");
            yield return null;

            bool viaFile = LoadSceneFile(e.scenePath);
            if (viaFile)
            {
                float until = Time.unscaledTime + 15f;
                while (view == null && Time.unscaledTime < until) yield return null;
                if (view == null) viaFile = false;
            }
            if (!viaFile)
            {
                DetachWorld();
                yield return StartCoroutine(FreshScene(e.rootName));
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
                var rootGo = new GameObject(e.rootName);
                var v = rootGo.AddComponent(e.viewType);
                if (e.captureType != null) rootGo.AddComponent(e.captureType);
                Attach(e, v);
            }
            Resources.UnloadUnusedAssets();
            loading = false;
            loadingOverlay.style.display = DisplayStyle.None;
            ApplyTimeScale(); UpdateModeUi(); UpdateControls();
        }

        // ------------------------------------------------------------------ 카메라 · 자막 · HUD
        void SetCinematic(bool on)
        {
            if (view == null || LayoutMode) return;
            SetField(view, "cinematicCamera", on);
            UpdateControls();
        }

        void SetCaptions(bool on)
        {
            captionsOn = on;
            if (view != null && !LayoutMode) { SetField(view, "showCaption", on); SetField(view, "showCaptions", on); }
            foreach (var cp in FindObjectsByType<CaptionPresenter>(FindObjectsSortMode.None)) cp.show = on;
        }

        void SetHudVisible(bool on)
        {
            hudVisible = on;
            root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) { overTop = overLeft = overDash = overRight = overPopup = false; HudInput.PointerOverUi = overModal; }
            else Toast("H: HUD 숨기기 · Space: 재생/일시정지");
        }

        void SetTheme(bool dark)
        {
            if (HudStyle.Dark == dark) return;
            HudStyle.Dark = dark;
            PlayerPrefs.SetInt(HudStyle.PrefTheme, dark ? 0 : 1); PlayerPrefs.Save();
            // 색이 바뀌므로 HUD 를 다시 만듦 (상태는 그대로)
            BuildContent();
            RefreshEntryUi(); UpdateModeUi(); UpdateControls();
        }

        void Quit()
        {
            if (Application.isEditor) EditorQuit?.Invoke();
            else Application.Quit();
        }

        // ------------------------------------------------------------------ 속성 변경
        public static void ApplyPars(Component v, Dictionary<string, double> map)
        {
            if (v == null || map == null) return;
            var cfg = GetConfig(v);
            foreach (var kv in map)
            {
                var (target, fi) = ResolveField(v, cfg, kv.Key);
                if (fi == null) continue;
                try { fi.SetValue(target, Convert.ChangeType(kv.Value, fi.FieldType, CultureInfo.InvariantCulture)); }
                catch (Exception ex) { Debug.LogWarning($"[HUD] {kv.Key} 적용 실패: {ex.Message}"); }
            }
        }

        Dictionary<string, double> ReadParams()
        {
            var map = new Dictionary<string, double>();
            foreach (var (def, field, fi) in paramFields)
            {
                if (fi == null) continue;
                if (!double.TryParse(field.value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                d = Math.Max(def.min, Math.Min(def.max, d));
                if (fi.FieldType == typeof(int)) d = Math.Round(d);
                map[def.field] = d;
            }
            return map;
        }

        void ApplyParams()
        {
            if (entry == null) return;
            var map = ReadParams();
            if (LayoutMode)
            {
                if (selModule == null || !selModule.built) return;
                Toast($"{selModule.Label} 를 새 속성으로 다시 만듭니다");
                world.Rebuild(selModule, map);
                return;
            }
            overrides[entry.id] = map;
            Toast("속성을 적용했습니다. 처음부터 다시 시작합니다 (시네마틱 샷은 기본값 기준이라 어긋날 수 있습니다)");
            Restart(0f);
        }

        void ResetParams()
        {
            if (entry == null) return;
            if (LayoutMode)
            {
                if (selModule == null || !selModule.built) return;
                world.Rebuild(selModule, new Dictionary<string, double>());
                return;
            }
            overrides.Remove(entry.id);
            Toast("기본값으로 되돌려 다시 시작합니다");
            Restart(0f);
        }

        static object GetConfig(Component v) => v?.GetType().GetField("config", BindingFlags.Instance | BindingFlags.Public)?.GetValue(v);

        static (object, FieldInfo) ResolveField(Component v, object cfg, string name)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public;
            if (name.StartsWith("view.")) return ((object)v, v.GetType().GetField(name.Substring(5), F));
            if (cfg == null) return ((object)null, (FieldInfo)null);
            return (cfg, cfg.GetType().GetField(name, F));
        }

        static void SetField(object o, string name, bool value)
        {
            var f = o?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (f != null && f.FieldType == typeof(bool)) f.SetValue(o, value);
        }

        static bool GetBool(object o, string name, bool fallback)
        {
            var f = o?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return f != null && f.FieldType == typeof(bool) ? (bool)f.GetValue(o) : fallback;
        }

        // ================================================================== UI 만들기
        void CreateDocument()
        {
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.themeStyleSheet = Resources.Load<ThemeStyleSheet>("EquipHudTheme");
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 1f;
            ps.sortingOrder = 100;
            var uiGo = new GameObject("HUD UI");
            uiGo.SetActive(false);
            uiGo.transform.SetParent(transform, false);
            doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            uiGo.SetActive(true);
        }

        void BuildContent()
        {
            var docRoot = doc.rootVisualElement;
            docRoot.Clear();
            cards.Clear(); kpiLabels.Clear(); plantRows.Clear(); plantUnitLbls.Clear(); paramFields.Clear(); partRows.Clear(); shownPart = null;
            overTop = overLeft = overDash = overRight = overPopup = overModal = overBanner = overMfd = false; mfdSig = null; HudInput.PointerOverUi = false;
            docRoot.pickingMode = PickingMode.Ignore;
            var font = HudFont.Get();
            if (font.HasValue) docRoot.style.unityFontDefinition = font.Value;

            root = new VisualElement { pickingMode = PickingMode.Ignore };
            root.style.position = Position.Absolute; root.style.left = 0; root.style.right = 0; root.style.top = 0; root.style.bottom = 0;
            root.style.flexDirection = FlexDirection.Column;
            root.style.display = hudVisible && !recording ? DisplayStyle.Flex : DisplayStyle.None;
            docRoot.Add(root);

            BuildTopBar();
            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.style.flexGrow = 1; body.style.flexDirection = FlexDirection.Row;
            HudStyle.Pad(body, 12, 12);
            root.Add(body);
            BuildLeftPanel(body);
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore }; spacer.style.flexGrow = 1;
            body.Add(spacer);
            BuildMfdPanel(spacer);
            BuildDashPanel(body);
            BuildRightPanel(body);

            toastLbl = HudStyle.MakeLabel("", 13, HudStyle.Text);
            toastLbl.style.position = Position.Absolute; toastLbl.style.bottom = 24; toastLbl.style.alignSelf = Align.Center;
            toastLbl.style.backgroundColor = HudStyle.Panel; HudStyle.Pad(toastLbl, 10, 16); HudStyle.Radius(toastLbl, 20);
            toastLbl.style.display = DisplayStyle.None;
            root.Add(toastLbl);

            loadingOverlay = new VisualElement { pickingMode = PickingMode.Ignore };
            loadingOverlay.style.position = Position.Absolute; loadingOverlay.style.left = 0; loadingOverlay.style.right = 0;
            loadingOverlay.style.top = 0; loadingOverlay.style.bottom = 0;
            loadingOverlay.style.alignItems = Align.Center; loadingOverlay.style.justifyContent = Justify.Center;
            loadingLbl = HudStyle.MakeLabel("", 18, HudStyle.Strong, true);
            loadingLbl.style.backgroundColor = HudStyle.Panel; HudStyle.Pad(loadingLbl, 14, 24); HudStyle.Radius(loadingLbl, 14);
            loadingOverlay.Add(loadingLbl);
            loadingOverlay.style.display = loading ? DisplayStyle.Flex : DisplayStyle.None;
            docRoot.Add(loadingOverlay);

            BuildDockLabels();
            BuildSiteBanner();
            BuildCargoModal();
            RefreshCards();
            Hover(topBar, b => overTop = b); Hover(leftPanel, b => overLeft = b);
            Hover(dashPanel, b => overDash = b); Hover(rightPanel, b => overRight = b);
            plantDirty = true;
            UpdateModeUi();
            UpdateControls();
        }

        void Hover(VisualElement e, Action<bool> set)
        {
            void Upd() => UpdOver();
            e.RegisterCallback<PointerEnterEvent>(_ => { set(true); Upd(); });
            e.RegisterCallback<PointerLeaveEvent>(_ => { set(false); Upd(); });
        }

        void UpdOver() => HudInput.PointerOverUi = overTop || overLeft || overDash || overRight || overPopup || overModal || overBanner || overMfd;

        static VisualElement Panel()
        {
            var p = new VisualElement();
            p.style.backgroundColor = HudStyle.Panel; HudStyle.Radius(p, 14);
            return p;
        }

        static VisualElement Row()
        {
            var r = new VisualElement { pickingMode = PickingMode.Ignore };
            r.style.flexDirection = FlexDirection.Row; r.style.alignItems = Align.Center;
            return r;
        }

        static VisualElement Gap(float w)
        {
            var d = new VisualElement { pickingMode = PickingMode.Ignore };
            d.style.width = w; d.style.flexShrink = 0;
            return d;
        }

        static Label SectionTitle(string t)
        {
            var l = HudStyle.MakeLabel(t, 11, HudStyle.Muted, true);
            l.style.letterSpacing = 0.8f; HudStyle.Margin(l, 18, 0, 8, 0);
            return l;
        }

        static void StyleDropdown(DropdownField dd, float width)
        {
            dd.style.width = width; dd.style.height = 32; HudStyle.Margin(dd, 0, 2, 0, 2);
            var input = dd.Q(className: DropdownField.inputUssClassName);
            if (input != null)
            {
                input.style.backgroundColor = HudStyle.Tile; HudStyle.Border(input, 0, Color.clear); HudStyle.Radius(input, 8);
                input.style.color = HudStyle.Text; HudStyle.Pad(input, 0, 10);
            }
            dd.Query<TextElement>().ForEach(t => { t.style.color = HudStyle.Text; t.style.fontSize = 13; });
        }

        // ---------------------------------------------------------------- 위쪽 막대
        void BuildTopBar()
        {
            topBar = Panel();
            topBar.style.flexDirection = FlexDirection.Row; topBar.style.alignItems = Align.Center;
            topBar.style.height = 56; HudStyle.Margin(topBar, 12, 12, 0, 12); HudStyle.Pad(topBar, 0, 12);
            root.Add(topBar);

            // 브랜드 + 모드
            var dot = new VisualElement { pickingMode = PickingMode.Ignore };
            dot.style.width = 10; dot.style.height = 10; dot.style.backgroundColor = HudStyle.Accent; HudStyle.Radius(dot, 5);
            HudStyle.Margin(dot, 0, 10, 0, 4);
            topBar.Add(dot);
            var brand = HudStyle.MakeLabel("Equipment Studio", 15, HudStyle.Strong, true);
            topBar.Add(brand);
            topBar.Add(Gap(18));
            var seg = Row(); seg.pickingMode = PickingMode.Position;
            seg.style.backgroundColor = HudStyle.Tile; HudStyle.Radius(seg, 10); HudStyle.Pad(seg, 3, 3);
            modeLayoutBtn = HudStyle.MakeButton("배치", OpenLayout, 64);
            modeAnimBtn = HudStyle.MakeButton("애니메이션", () => OpenAnimation(entry ?? EquipCatalog.All[0]), 88);
            foreach (var b in new[] { modeLayoutBtn, modeAnimBtn }) { b.style.height = 28; HudStyle.Margin(b, 0, 0, 0, 0); seg.Add(b); }
            topBar.Add(seg);
            topBar.Add(Gap(18));

            // 재생
            playBtn = new IconButton(IconButton.Kind.Pause, 36, TogglePause, true);
            topBar.Add(playBtn);
            speedDd = new HudDropdown(new List<string> { "0.25×", "0.5×", "1×", "2×", "4×", "8×" }, SpeedIndex(), 80,
                i => { if (i >= 0 && i < Speeds.Length) SetSpeed(Speeds[i]); }, () => root);
            speedDd.popupOpenChanged = b => { overPopup = b; UpdOver(); };
            topBar.Add(speedDd);
            topBar.Add(Gap(8));

            // 애니메이션 도구
            animTools = Row(); animTools.style.flexGrow = 1; animTools.pickingMode = PickingMode.Position;
            animTools.Add(new IconButton(IconButton.Kind.Restart, 32, () => Restart(0f)));
            animTools.Add(new IconButton(IconButton.Kind.Back, 32, () => Seek(CurrentTime() - 5f)));
            animTools.Add(new IconButton(IconButton.Kind.Forward, 32, () => Seek(CurrentTime() + 5f)));
            timeline = new TimelineBar { length = MovieLength };
            timeline.onSeek = Seek;
            animTools.Add(timeline);
            timeLbl = HudStyle.MakeLabel("0.0 / 30 s", 12, HudStyle.Muted);
            timeLbl.style.width = 88; timeLbl.style.unityTextAlign = TextAnchor.MiddleRight; timeLbl.style.flexShrink = 0;
            animTools.Add(timeLbl);
            animTools.Add(Gap(10));
            camCineBtn = HudStyle.MakeButton("시네마틱", () => SetCinematic(true));
            camOrbitBtn = HudStyle.MakeButton("자유 시점", () => SetCinematic(false));
            captionBtn = HudStyle.MakeButton("자막", () => { SetCaptions(!captionsOn); UpdateControls(); });
            loopBtn = HudStyle.MakeButton("반복", () => { loop = !loop; UpdateControls(); });
            animTools.Add(camCineBtn); animTools.Add(camOrbitBtn); animTools.Add(Gap(6)); animTools.Add(captionBtn); animTools.Add(loopBtn);
            if (Application.isEditor)
            {
                recBtn = HudStyle.MakeButton("● 녹화", () => { if (entry != null) EditorRecord?.Invoke(entry); });
                recBtn.style.color = HudStyle.Rec;
                animTools.Add(recBtn);
            }
            topBar.Add(animTools);

            // 배치 도구
            layoutTools = Row(); layoutTools.style.flexGrow = 1; layoutTools.pickingMode = PickingMode.Position;
            tab3dBtn = HudStyle.MakeButton("3D", () => SetMfd(false));
            tab3dBtn.tooltip = "3D 배치 화면";
            tabMfdBtn = HudStyle.MakeButton("물류흐름도", () => SetMfd(true));
            tabCadBtn = HudStyle.MakeButton("CAD", () => SetView(2));
            tabCadBtn.tooltip = "지금 배치를 위에서 본 2D 도면";
            tabMfdBtn.tooltip = "놓은 출입문 · 하역 수단 · 설비로 자동으로 만든 물류흐름도(MFD)";
            layoutTools.Add(tab3dBtn); layoutTools.Add(tabMfdBtn); layoutTools.Add(tabCadBtn);
            layoutTools.Add(Gap(8));
            cargoChip = HudStyle.MakeButton("화물", ShowCargoModal);
            cargoChip.tooltip = "물류센터가 다루는 화물 바꾸기";
            layoutTools.Add(cargoChip);
            layoutTools.Add(Gap(8));
            var hint = HudStyle.MakeLabel("여백 끌기 화면 이동 · 클릭 선택 · 설비 끌어 옮기기 · Del 삭제 · F 맞춤 · 오른쪽 끌기 회전 · 휠 확대", 12, HudStyle.Muted);
            hint.style.flexGrow = 1; hint.style.overflow = Overflow.Hidden; hint.style.textOverflow = TextOverflow.Ellipsis;
            layoutTools.Add(hint);
            layoutTools.Add(HudStyle.MakeButton("모두 지우기", () => world?.ClearAll()));
            topBar.Add(layoutTools);

            // 오른쪽: 테마 · 숨김 · 닫기
            topBar.Add(Gap(10));
            themeDd = new HudDropdown(new List<string> { "다크", "라이트" }, HudStyle.Dark ? 0 : 1, 92, i => SetTheme(i == 0), () => root);
            themeDd.popupOpenChanged = b => { overPopup = b; UpdOver(); };
            topBar.Add(themeDd);
            var hide = new IconButton(IconButton.Kind.Hide, 32, () => SetHudVisible(false));
            topBar.Add(hide);
            var close = new IconButton(IconButton.Kind.Close, 32, Quit) { hoverBg = HudStyle.Rec };
            topBar.Add(close);
        }

        int SpeedIndex()
        {
            int best = 2;
            for (int i = 0; i < Speeds.Length; i++) if (Mathf.Abs(Speeds[i] - speed) < Mathf.Abs(Speeds[best] - speed)) best = i;
            return best;
        }

        static string SpeedLabel(float s) => s < 1 ? $"{s:0.##}×" : $"{s:0}×";

        // ---------------------------------------------------------------- 왼쪽: 라이브러리
        void BuildLeftPanel(VisualElement body)
        {
            leftPanel = Panel();
            leftPanel.style.width = 280; leftPanel.style.flexShrink = 0; HudStyle.Pad(leftPanel, 16, 14);
            body.Add(leftPanel);

            var head = Row();
            var t = HudStyle.MakeLabel("라이브러리", 15, HudStyle.Strong, true); t.style.flexGrow = 1;
            var count = HudStyle.MakeLabel($"{EquipCatalog.All.Count}", 12, HudStyle.Muted);
            head.Add(t); head.Add(count);
            leftPanel.Add(head);
            var sub = HudStyle.MakeLabel("끌어서 바닥에 설치", 12, HudStyle.Muted); HudStyle.Margin(sub, 2, 0, 0, 0);
            leftPanel.Add(sub);

            var sv = HudStyle.MakeScroll();
            sv.style.flexGrow = 1; HudStyle.Margin(sv, 6, 0, 0, 0);
            leftPanel.Add(sv);

            // 건물 · 입출고 (센터의 뼈대)
            var site = Group(sv, "건물 · 입출고", 3);
            site.Add(ToolCard("물류센터 영역", "바닥을 끌어 건물 직사각형을 다시 그림", () => { if (LayoutMode) world?.BeginSiteDrawing(); else Toast("배치 모드에서 쓸 수 있습니다"); }));
            site.Add(ToolCard("입고 도크 문", "입고 쪽 벽에 출입문 추가 (끌어서 이동)", () => { if (LayoutMode) world?.AddDoor(0); else Toast("배치 모드에서 쓸 수 있습니다"); }));
            site.Add(ToolCard("출고 도크 문", "출고 쪽 벽에 출입문 추가 (끌어서 이동)", () => { if (LayoutMode) world?.AddDoor(1); else Toast("배치 모드에서 쓸 수 있습니다"); }));
            // 하역 수단 (트럭 상하차)
            var movers = Group(sv, "하역 수단", MoverInfo.All.Length);
            foreach (var k in MoverInfo.All) movers.Add(MoverCard(k));
            // 설비 묶음
            foreach (var g in EquipCatalog.Groups)
            {
                var list = EquipCatalog.All.FindAll(e => EquipCatalog.GroupOf(e) == g);
                if (list.Count == 0) continue;
                var box = Group(sv, g, list.Count);
                foreach (var e in list) box.Add(MakeCard(e));
            }
        }

        // 접었다 펼 수 있는 묶음 머리 + 내용 상자
        VisualElement Group(VisualElement parent, string title, int count)
        {
            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row; head.style.alignItems = Align.Center;
            HudStyle.Pad(head, 7, 8); HudStyle.Radius(head, 8); HudStyle.Margin(head, 8, 0, 2, 0);
            bool open = !collapsedGroups.Contains(title);
            var chev = new VisualElement { pickingMode = PickingMode.Ignore };
            chev.style.width = 12; chev.style.height = 12; chev.style.flexShrink = 0; HudStyle.Margin(chev, 0, 8, 0, 0);
            chev.generateVisualContent += mgc =>
            {
                var pp = mgc.painter2D; pp.strokeColor = HudStyle.Muted; pp.lineWidth = 1.8f; pp.lineCap = LineCap.Round; pp.lineJoin = LineJoin.Round;
                pp.BeginPath();
                if (!collapsedGroups.Contains(title)) { pp.MoveTo(new Vector2(2f, 4f)); pp.LineTo(new Vector2(6f, 8f)); pp.LineTo(new Vector2(10f, 4f)); }
                else { pp.MoveTo(new Vector2(4f, 2f)); pp.LineTo(new Vector2(8f, 6f)); pp.LineTo(new Vector2(4f, 10f)); }
                pp.Stroke();
            };
            var t = HudStyle.MakeLabel(title, 12, HudStyle.Strong, true); t.style.flexGrow = 1;
            var n = HudStyle.MakeLabel(count.ToString(), 11, HudStyle.Muted);
            head.Add(chev); head.Add(t); head.Add(n);
            var box = new VisualElement();
            HudStyle.Margin(box, 0, 0, 0, 8);
            box.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            head.RegisterCallback<PointerEnterEvent>(_ => head.style.backgroundColor = HudStyle.TileHover);
            head.RegisterCallback<PointerLeaveEvent>(_ => head.style.backgroundColor = Color.clear);
            head.RegisterCallback<ClickEvent>(_ =>
            {
                bool nowOpen = collapsedGroups.Contains(title);
                if (nowOpen) collapsedGroups.Remove(title); else collapsedGroups.Add(title);
                box.style.display = nowOpen ? DisplayStyle.Flex : DisplayStyle.None;
                chev.MarkDirtyRepaint();
            });
            parent.Add(head); parent.Add(box);
            return box;
        }

        VisualElement MoverCard(int kind)
        {
            var c = ToolCard(MoverInfo.Name(kind), MoverInfo.Desc(kind) + $" · {EquipCatalog.Money(MoverInfo.Price(kind))} · 끌어서 센터 안에 놓기", null);
            c.RegisterCallback<PointerDownEvent>(ev =>
            {
                if (ev.button != 0) return;
                if (!LayoutMode || world == null) { Toast("배치 모드에서 쓸 수 있습니다"); return; }
                SetMfd(false);
                world.BeginDragMover(kind);
                Toast($"{MoverInfo.Name(kind)}: 센터 바닥에 놓으세요 (누르기만 하면 문 근처에 자동 배치)");
            });
            return c;
        }

        VisualElement ToolCard(string name, string desc, Action onClick)
        {
            var c = new VisualElement();
            c.style.flexDirection = FlexDirection.Row; c.style.alignItems = Align.Center;
            HudStyle.Radius(c, 10); HudStyle.Pad(c, 8, 10); HudStyle.Margin(c, 0, 0, 2, 0);
            var txt = new VisualElement { pickingMode = PickingMode.Ignore }; txt.style.flexGrow = 1; txt.style.flexShrink = 1;
            txt.Add(HudStyle.MakeLabel(name, 13, HudStyle.Strong, true));
            var d = HudStyle.MakeLabel(desc, 11, HudStyle.Muted); d.style.whiteSpace = WhiteSpace.Normal;
            txt.Add(d);
            c.Add(txt);
            c.RegisterCallback<PointerEnterEvent>(_ => c.style.backgroundColor = HudStyle.TileHover);
            c.RegisterCallback<PointerLeaveEvent>(_ => c.style.backgroundColor = Color.clear);
            if (onClick != null) c.RegisterCallback<ClickEvent>(_ => onClick());
            return c;
        }

        VisualElement MakeCard(EquipEntry e)
        {
            var c = new VisualElement();
            c.style.flexDirection = FlexDirection.Row; c.style.alignItems = Align.Center;
            c.style.backgroundColor = Color.clear; HudStyle.Radius(c, 10); HudStyle.Pad(c, 8, 10); HudStyle.Margin(c, 0, 0, 2, 0);
            var txt = new VisualElement { pickingMode = PickingMode.Ignore }; txt.style.flexGrow = 1; txt.style.flexShrink = 1;
            var n = HudStyle.MakeLabel(e.name, 13, HudStyle.Strong, true);
            var k = HudStyle.MakeLabel(e.korean, 11, HudStyle.Muted); k.style.whiteSpace = WhiteSpace.Normal;
            var cg = HudStyle.MakeLabel("화물: " + CargoInfo.Names(e.cargo) + " · " + EquipCatalog.Money(EquipCatalog.PriceOf(e)), 10, HudStyle.Muted); cg.name = "cargo"; HudStyle.Margin(cg, 2, 0, 0, 0);
            txt.Add(n); txt.Add(k); txt.Add(cg);
            c.Add(txt);
            // 애니메이션으로 보기
            var play = new IconButton(IconButton.Kind.Play, 26, () => OpenAnimation(e));
            play.tooltip = "30초 애니메이션 보기";
            play.RegisterCallback<PointerDownEvent>(ev => ev.StopPropagation());
            c.Add(play);
            c.RegisterCallback<PointerEnterEvent>(_ => { if (!IsCurrent(e)) c.style.backgroundColor = HudStyle.TileHover; });
            c.RegisterCallback<PointerLeaveEvent>(_ => { if (!IsCurrent(e)) c.style.backgroundColor = Color.clear; });
            c.RegisterCallback<PointerDownEvent>(ev =>
            {
                if (ev.button != 0) return;
                if (!LayoutMode || world == null) return;
                SetMfd(false);
                if (world.BeginDrag(e)) Toast($"{e.name}: 바닥에 놓으세요 (Esc 취소)");
                else Toast($"{e.name} 은(는) {CargoInfo.Name(world.Cargo)}을(를) 처리할 수 없습니다. 처리 화물: {CargoInfo.Names(e.cargo)}");
            });
            c.RegisterCallback<ClickEvent>(_ => { if (!LayoutMode) OpenAnimation(e); });
            cards[e] = c;
            return c;
        }

        bool IsCurrent(EquipEntry e) => entry == e;

        // ---------------------------------------------------------------- 대시보드 (속성 패널 왼쪽)
        void BuildDashPanel(VisualElement body)
        {
            dashPanel = Panel();
            dashPanel.style.width = 300; dashPanel.style.flexShrink = 0; HudStyle.Pad(dashPanel, 16, 14); HudStyle.Margin(dashPanel, 0, 10, 0, 0);
            body.Add(dashPanel);
            var t = HudStyle.MakeLabel("대시보드", 15, HudStyle.Strong, true);
            dashPanel.Add(t);
            var sv = HudStyle.MakeScroll();
            sv.style.flexGrow = 1;
            dashPanel.Add(sv);

            // 공장 전체 (배치 모드)
            plantBox = new VisualElement { pickingMode = PickingMode.Ignore };
            plantBox.Add(SectionTitle("공장 전체"));
            plantTiles = new VisualElement { pickingMode = PickingMode.Ignore };
            plantTiles.style.flexDirection = FlexDirection.Row; plantTiles.style.flexWrap = Wrap.Wrap; plantTiles.style.justifyContent = Justify.SpaceBetween;
            plantBox.Add(plantTiles);
            plantBox.Add(SectionTitle("설치된 설비"));
            plantList = new VisualElement();
            plantBox.Add(plantList);
            sv.Add(plantBox);

            // 선택한 설비
            selDashBox = new VisualElement { pickingMode = PickingMode.Ignore };
            selDashTitle = SectionTitle("선택한 설비");
            selDashBox.Add(selDashTitle);
            var hero = new VisualElement { pickingMode = PickingMode.Ignore };
            hero.style.backgroundColor = HudStyle.Tile; HudStyle.Radius(hero, 12); HudStyle.Pad(hero, 12, 14);
            var heroTop = Row(); heroTop.style.alignItems = Align.FlexEnd;
            tpLbl = HudStyle.MakeLabel("—", 30, HudStyle.Strong, true);
            tpUnitLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted); HudStyle.Margin(tpUnitLbl, 0, 0, 6, 6);
            heroTop.Add(tpLbl); heroTop.Add(tpUnitLbl);
            var heroCap = HudStyle.MakeLabel("처리량 (누적 평균)", 11, HudStyle.Muted);
            recentLbl = HudStyle.MakeLabel("최근 1분 —", 12, HudStyle.Accent, true); HudStyle.Margin(recentLbl, 4, 0, 8, 0);
            var chartWrap = new VisualElement { pickingMode = PickingMode.Ignore };
            chart = new Sparkline();
            chartMaxLbl = HudStyle.MakeLabel("", 10, HudStyle.Muted);
            chartMaxLbl.style.position = Position.Absolute; chartMaxLbl.style.left = 2; chartMaxLbl.style.top = -2;
            chartWrap.Add(chart); chartWrap.Add(chartMaxLbl);
            hero.Add(heroTop); hero.Add(heroCap); hero.Add(recentLbl); hero.Add(chartWrap);
            selDashBox.Add(hero);
            var info = Row(); HudStyle.Margin(info, 8, 0, 0, 0);
            simTimeLbl = HudStyle.MakeLabel("", 11, HudStyle.Muted); simTimeLbl.style.flexGrow = 1;
            robotsLbl = HudStyle.MakeLabel("", 11, HudStyle.Muted);
            info.Add(simTimeLbl); info.Add(robotsLbl);
            selDashBox.Add(info);
            kpiGrid = new VisualElement { pickingMode = PickingMode.Ignore };
            kpiGrid.style.flexDirection = FlexDirection.Row; kpiGrid.style.flexWrap = Wrap.Wrap; kpiGrid.style.justifyContent = Justify.SpaceBetween;
            HudStyle.Margin(kpiGrid, 8, 0, 0, 0);
            selDashBox.Add(kpiGrid);
            sv.Add(selDashBox);
        }

        static (VisualElement tile, Label val) Tile(string label, string unit, float widthPct = 48.5f)
        {
            var tile = new VisualElement { pickingMode = PickingMode.Ignore };
            tile.style.width = Length.Percent(widthPct); tile.style.backgroundColor = HudStyle.Tile;
            HudStyle.Radius(tile, 10); HudStyle.Pad(tile, 9, 11); HudStyle.Margin(tile, 0, 0, 6, 0);
            var row = Row(); row.style.alignItems = Align.FlexEnd;
            var val = HudStyle.MakeLabel("—", 17, HudStyle.Strong, true);
            var u = HudStyle.MakeLabel(unit, 10, HudStyle.Muted); HudStyle.Margin(u, 0, 0, 3, 4);
            row.Add(val); row.Add(u);
            var lbl = HudStyle.MakeLabel(label, 11, HudStyle.Muted);
            tile.Add(row); tile.Add(lbl);
            return (tile, val);
        }

        void RebuildPlant()
        {
            plantDirty = false;
            plantTiles.Clear(); plantList.Clear(); plantRows.Clear(); plantUnitLbls.Clear();
            if (!LayoutMode) return;
            var (tb, vb) = Tile("잔고", ""); plantTiles.Add(tb); balanceLbl = vb;
            var (ti, vi) = Tile("입고 처리", "개"); plantTiles.Add(ti); inDoneLbl = vi;
            var (to, vo) = Tile("출고 처리", "개"); plantTiles.Add(to); outDoneLbl = vo;
            var (tt, vt) = Tile("대기 트럭", "대"); plantTiles.Add(tt); trucksLbl = vt;
            var (t1, v1) = Tile("설치된 설비", "대"); plantTiles.Add(t1); plantCountLbl = v1;
            var (t2, v2) = Tile("이동 장비 합계", "대"); plantTiles.Add(t2); plantRobotLbl = v2;
            var units = new List<string>();
            foreach (var m in world.modules) if (m != null && !units.Contains(m.entry.throughputUnit)) units.Add(m.entry.throughputUnit);
            foreach (var u in units) { var (t, v) = Tile("처리량 합계", u); plantTiles.Add(t); plantUnitLbls[u] = v; }

            if (world.modules.Count == 0)
            {
                var l = HudStyle.MakeLabel("아직 설치된 설비가 없습니다. 왼쪽 라이브러리에서 끌어다 놓으세요.", 12, HudStyle.Muted);
                l.style.whiteSpace = WhiteSpace.Normal; plantList.Add(l);
                return;
            }
            foreach (var m in world.modules)
            {
                if (m == null) continue;
                var pm = m;
                var row = Row(); row.pickingMode = PickingMode.Position;
                HudStyle.Pad(row, 7, 10); HudStyle.Radius(row, 8); HudStyle.Margin(row, 0, 0, 2, 0);
                bool sel = pm == selModule;
                row.style.backgroundColor = sel ? HudStyle.AccentDim : Color.clear;
                var n = HudStyle.MakeLabel(pm.Label, 12, HudStyle.Text, sel); n.style.flexGrow = 1;
                var v = HudStyle.MakeLabel("…", 12, HudStyle.Muted);
                row.Add(n); row.Add(v);
                row.RegisterCallback<PointerEnterEvent>(_ => { if (pm != selModule) row.style.backgroundColor = HudStyle.TileHover; });
                row.RegisterCallback<PointerLeaveEvent>(_ => { if (pm != selModule) row.style.backgroundColor = Color.clear; });
                row.RegisterCallback<ClickEvent>(_ => { world.Select(pm); world.FocusOn(pm); });
                plantList.Add(row);
                plantRows.Add((pm, v));
            }
        }

        // ---------------------------------------------------------------- 오른쪽: 속성
        void BuildRightPanel(VisualElement body)
        {
            rightPanel = Panel();
            rightPanel.style.width = 320; rightPanel.style.flexShrink = 0; HudStyle.Pad(rightPanel, 16, 14);
            body.Add(rightPanel);
            var t = HudStyle.MakeLabel("속성", 15, HudStyle.Strong, true);
            rightPanel.Add(t);
            var sv = HudStyle.MakeScroll();
            sv.style.flexGrow = 1;
            rightPanel.Add(sv);
            propBody = sv;

            catLbl = HudStyle.MakeLabel("", 10, HudStyle.Accent, true); catLbl.style.letterSpacing = 1.2f; HudStyle.Margin(catLbl, 14, 0, 0, 0);
            titleLbl = HudStyle.MakeLabel("", 20, HudStyle.Strong, true); titleLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(titleLbl, 4, 0, 0, 0);
            koLbl = HudStyle.MakeLabel("", 12, HudStyle.Text); koLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(koLbl, 2, 0, 0, 0);
            descLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted); descLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(descLbl, 8, 0, 0, 0);

            // 클릭한 하위 오브젝트 카드 (설비 속성 위)
            partCard = new VisualElement();
            partCard.style.backgroundColor = HudStyle.AccentDim; HudStyle.Radius(partCard, 12); HudStyle.Pad(partCard, 12, 12);
            HudStyle.Margin(partCard, 12, 0, 4, 0);
            var ph = Row();
            var phText = new VisualElement { pickingMode = PickingMode.Ignore }; phText.style.flexGrow = 1; phText.style.flexShrink = 1;
            partTitleLbl = HudStyle.MakeLabel("", 16, HudStyle.Strong, true);
            partSubLbl = HudStyle.MakeLabel("", 11, HudStyle.Muted); HudStyle.Margin(partSubLbl, 2, 0, 0, 0);
            phText.Add(partTitleLbl); phText.Add(partSubLbl);
            var back = HudStyle.MakeButton("설비 전체", () => world?.Select(selModule));
            back.style.height = 26; back.style.fontSize = 11; HudStyle.Pad(back, 0, 10);
            ph.Add(phText); ph.Add(back);
            partCard.Add(ph);
            partRowsBox = new VisualElement { pickingMode = PickingMode.Ignore }; HudStyle.Margin(partRowsBox, 10, 0, 0, 0);
            partCard.Add(partRowsBox);
            partCard.style.display = DisplayStyle.None;

            sv.Add(partCard);
            cargoLbl = HudStyle.MakeLabel("", 12, HudStyle.Text, true); cargoLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(cargoLbl, 8, 0, 0, 0);
            sv.Add(catLbl); sv.Add(titleLbl); sv.Add(koLbl); sv.Add(descLbl); sv.Add(cargoLbl);

            // 배치 모드: 위치 · 동작
            moduleBox = new VisualElement();
            moduleBox.Add(SectionTitle("배치"));
            posLbl = HudStyle.MakeLabel("", 12, HudStyle.Text);
            moduleBox.Add(posLbl);
            var acts = Row(); HudStyle.Margin(acts, 8, 0, 0, 0);
            var anim = HudStyle.MakeButton("애니메이션 보기", () => { if (entry != null) OpenAnimation(entry); });
            var focus = HudStyle.MakeButton("화면 맞춤", () => world?.FocusOn(selModule));
            var del = HudStyle.MakeButton("삭제", () => { if (selModule != null) world?.Remove(selModule); });
            del.style.color = HudStyle.Rec;
            HudStyle.Margin(anim, 0, 4, 0, 0);
            acts.Add(anim); acts.Add(focus); acts.Add(del);
            moduleBox.Add(acts);
            sv.Add(moduleBox);

            // 물류센터 건물 / 출입문 속성
            siteBox = new VisualElement();
            siteBox.style.display = DisplayStyle.None;
            sv.Add(siteBox);

            paramTitle = SectionTitle("설정");
            sv.Add(paramTitle);
            paramBox = new VisualElement();
            sv.Add(paramBox);

            var specHead = Row(); HudStyle.Margin(specHead, 18, 0, 6, 0);
            var specT = HudStyle.MakeLabel("모델 사양", 11, HudStyle.Muted, true); specT.style.flexGrow = 1;
            var specFold = HudStyle.MakeButton("펼치기", null); specFold.style.height = 24; specFold.style.fontSize = 11; HudStyle.Pad(specFold, 0, 10);
            specHead.Add(specT); specHead.Add(specFold);
            specBox = new VisualElement(); specBox.Add(specHead);
            specBody = new VisualElement(); specBody.style.display = DisplayStyle.None;
            specBox.Add(specBody);
            specFold.clicked += () =>
            {
                bool open = specBody.style.display == DisplayStyle.None;
                specBody.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                specFold.text = open ? "접기" : "펼치기";
            };
            sv.Add(specBox);
        }

        // 선택(또는 열린) 설비가 바뀌면 대시보드·속성 내용을 다시 만듦
        void RefreshEntryUi(bool rebuild = true)
        {
            foreach (var kv in cards) kv.Value.style.backgroundColor = !LayoutMode && kv.Key == entry ? HudStyle.AccentDim : Color.clear;
            if (titleLbl == null) return;
            bool has = entry != null;
            catLbl.text = has ? entry.category.ToUpperInvariant() : "";
            titleLbl.text = has ? (LayoutMode && selModule != null ? selModule.Label : entry.name) : (LayoutMode ? "선택된 설비 없음" : "열린 설비 없음");
            koLbl.text = has ? entry.korean : (LayoutMode ? "바닥에 설치한 설비를 클릭하면 여기에 속성이 나옵니다." : "왼쪽 라이브러리에서 설비를 고르세요.");
            descLbl.text = has ? entry.desc : "";
            if (cargoLbl != null)
            {
                bool bad = has && LayoutMode && world != null && !world.CanHandle(entry);
                cargoLbl.text = !has ? "" : bad ? $"처리 화물: {CargoInfo.Names(entry.cargo)}  ·  지금 화물({CargoInfo.Short(world.Cargo)})은 처리할 수 없음"
                                              : $"처리 화물: {CargoInfo.Names(entry.cargo)}";
                cargoLbl.style.color = bad ? HudStyle.Bad : HudStyle.Text;
            }
            tpUnitLbl.text = has ? entry.throughputUnit : "";
            selDashBox.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            moduleBox.style.display = LayoutMode && selModule != null ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshPart();
            plantDirty = true;
            bool site = SiteSelected;
            siteBox.style.display = site ? DisplayStyle.Flex : DisplayStyle.None;
            paramTitle.style.display = paramBox.style.display = site ? DisplayStyle.None : DisplayStyle.Flex;
            if (site)
            {
                specBox.style.display = DisplayStyle.None;
                bool door = world.SelectedDoor >= 0 && world.SelectedDoor < world.Site.doors.Count;
                var mv = world.SelectedMover;
                catLbl.text = mv != null ? "하역 수단" : "물류센터";
                titleLbl.text = mv != null ? $"{MoverInfo.Name(mv.kind)} {mv.id}" : door ? Plan.DoorName(world.Site, world.SelectedDoor) : "물류센터 건물";
                koLbl.text = mv != null ? MoverInfo.Desc(mv.kind) + ". 트럭이 윙을 열고 기다리면 자동으로 상하차합니다."
                           : door ? "트럭이 붙는 도크 출입문. 장면에서 끌어 벽을 따라 옮기거나 아래에서 바꿀 수 있습니다."
                                  : "바닥은 트럭 적재함 높이만큼 올라가 있고, 입고 지점과 출고 지점을 마주하는 벽에 출입문이 있습니다.";
                descLbl.text = ""; cargoLbl.text = $"화물: {CargoInfo.Name(world.Cargo)}"; cargoLbl.style.color = HudStyle.Text;
                RebuildSiteBox();
                return;
            }
            if (!rebuild) return;

            kpiGrid.Clear(); kpiLabels.Clear();
            if (has)
                foreach (var k in entry.kpis)
                {
                    var (tile, val) = Tile(k.label, k.unit);
                    kpiGrid.Add(tile);
                    kpiLabels.Add((k, val));
                }
            RebuildParams();
            RebuildSpecs();
            marksSet = false;
            if (SiteSelected) specBox.style.display = DisplayStyle.None;
        }

        void RebuildParams()
        {
            paramBox.Clear(); paramFields.Clear();
            if (entry == null)
            {
                var l = HudStyle.MakeLabel(LayoutMode ? "설비를 선택하면 로봇 수·주문량 같은 설정을 바꿀 수 있습니다." : "", 12, HudStyle.Muted);
                l.style.whiteSpace = WhiteSpace.Normal; paramBox.Add(l);
                return;
            }
            var cfg = GetConfig(view);
            Dictionary<string, double> map = null;
            if (LayoutMode) map = selModule?.pars; else overrides.TryGetValue(entry.id, out map);
            if (entry.pars.Count == 0)
            {
                var l = HudStyle.MakeLabel("이 설비는 고정된 동작 주기로 움직여 바꿀 설정이 없습니다.", 12, HudStyle.Muted);
                l.style.whiteSpace = WhiteSpace.Normal; paramBox.Add(l);
                return;
            }
            foreach (var p in entry.pars)
            {
                object target = null; FieldInfo fi = null;
                if (view != null) (target, fi) = ResolveField(view, cfg, p.field);
                string cur = map != null && map.TryGetValue(p.field, out var ov) ? ov.ToString(CultureInfo.InvariantCulture)
                           : fi != null && target != null ? Convert.ToString(fi.GetValue(target), CultureInfo.InvariantCulture) : "";
                var row = Row(); HudStyle.Margin(row, 0, 0, 8, 0);
                var labels = new VisualElement { pickingMode = PickingMode.Ignore }; labels.style.flexGrow = 1;
                labels.Add(HudStyle.MakeLabel(p.label, 12, HudStyle.Text));
                labels.Add(HudStyle.MakeLabel($"{p.min:0.##} – {p.max:0.##}", 10, HudStyle.Muted));
                var tf = new TextField { value = cur, isDelayed = true };
                tf.style.width = 90; tf.style.height = 30; tf.style.fontSize = 13; HudStyle.Margin(tf, 0, 0, 0, 0);
                var input = tf.Q(className: TextField.inputUssClassName);
                if (input != null)
                {
                    input.style.backgroundColor = HudStyle.Field; input.style.color = HudStyle.Strong;
                    HudStyle.Border(input, 0, Color.clear); HudStyle.Radius(input, 8); HudStyle.Pad(input, 0, 8);
                }
                tf.Query<TextElement>().ForEach(te => te.style.color = HudStyle.Strong);
                row.Add(labels); row.Add(tf);
                paramBox.Add(row);
                paramFields.Add((p, tf, fi));
            }
            var btns = Row(); btns.style.justifyContent = Justify.FlexEnd; HudStyle.Margin(btns, 6, 0, 0, 0);
            var reset = HudStyle.MakeButton("기본값", ResetParams);
            var apply = HudStyle.MakeButton(LayoutMode ? "적용" : "적용 후 다시 시작", ApplyParams);
            HudStyle.SetOn(apply, true);
            btns.Add(reset); btns.Add(apply);
            paramBox.Add(btns);
        }

        void RebuildSpecs()
        {
            specBody.Clear();
            var cfg = GetConfig(view);
            if (cfg == null) { specBox.style.display = DisplayStyle.None; return; }
            specBox.style.display = DisplayStyle.Flex;
            foreach (var f in cfg.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string val;
                var o = f.GetValue(cfg);
                if (o is Array arr) { var parts = new List<string>(); foreach (var x in arr) parts.Add(Convert.ToString(x, CultureInfo.InvariantCulture)); val = string.Join(", ", parts); }
                else if (o is IFormattable fm) val = fm.ToString(o is float || o is double ? "0.###" : null, CultureInfo.InvariantCulture);
                else val = Convert.ToString(o, CultureInfo.InvariantCulture);
                var row = Row(); row.style.justifyContent = Justify.SpaceBetween; HudStyle.Margin(row, 0, 0, 3, 0);
                row.Add(HudStyle.MakeLabel(f.Name, 11, HudStyle.Muted));
                row.Add(HudStyle.MakeLabel(val, 11, HudStyle.Text));
                specBody.Add(row);
            }
        }

        void RefreshTimelineMarks()
        {
            if (timeline == null) return;
            var starts = new List<float>();
            if (view != null && !LayoutMode)
                foreach (var src in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                    if (src is ICaptionSource cs && cs.Seq != null)
                    {
                        foreach (var s in cs.Seq.shots) starts.Add(s.start);
                        marksSet = true;
                        break;
                    }
            timeline.SetMarks(starts);
        }

        // ---------------------------------------------------------------- 갱신
        void UpdateModeUi()
        {
            if (animTools == null) return;
            animTools.style.display = LayoutMode ? DisplayStyle.None : DisplayStyle.Flex;
            layoutTools.style.display = LayoutMode ? DisplayStyle.Flex : DisplayStyle.None;
            plantBox.style.display = LayoutMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (!LayoutMode && (mfdOn || cadOn)) SetMfd(false);
            UpdateMfdTabs();
            HudStyle.SetOn(modeLayoutBtn, LayoutMode);
            HudStyle.SetOn(modeAnimBtn, !LayoutMode && entry != null);
            selDashTitle.text = LayoutMode ? "선택한 설비" : "실시간";
            RefreshEntryUi(false);
        }

        void UpdateControls()
        {
            if (playBtn == null) return;
            playBtn.Icon = paused ? IconButton.Kind.Play : IconButton.Kind.Pause;
            speedDd?.SetIndexWithoutNotify(SpeedIndex());
            bool cine = GetBool(view, "cinematicCamera", true);
            HudStyle.SetOn(camCineBtn, cine); HudStyle.SetOn(camOrbitBtn, !cine);
            HudStyle.SetOn(captionBtn, captionsOn); HudStyle.SetOn(loopBtn, loop);
            if (recBtn != null) recBtn.SetEnabled(entry != null && EditorRecord != null && !LayoutMode);
        }

        void UpdateUiFast()
        {
            if (!LayoutMode && timeline != null)
            {
                timeline.SetValue(Mathf.Min(Elapsed, MovieLength));
                timeLbl.text = loading ? "불러오는 중"
                             : ffTarget >= 0 ? $"→ {ffTarget:0.0} s"
                             : Elapsed > MovieLength ? $"{Elapsed:0} s"
                             : $"{Elapsed:0.0} / {MovieLength:0} s";
            }
            if (toastLbl.style.display == DisplayStyle.Flex && Time.unscaledTime > toastUntil) toastLbl.style.display = DisplayStyle.None;
        }

        void UpdateUiSlow()
        {
            if (!hudVisible) return;
            if (!captionsOn && !LayoutMode) SetCaptions(false);
            if (!LayoutMode && !marksSet && clockStart >= 0) RefreshTimelineMarks();
            if (LayoutMode) UpdatePlant();
            if ((mfdOn || cadOn) && world != null && world.DrawingSite) SetMfd(false);
            if (mfdOn) UpdateMfd();
            if (cadOn && cad != null && world != null) { if (!cad.Bound) cad.Bind(world); cad.Refresh(); }
            if (siteDirty && !HudInput.Typing) { siteDirty = false; if (SiteSelected) RebuildSiteBox(); }
            if (world != null && world.SelectedMover != null && siteBox != null)
            {
                var stl = siteBox.Q<Label>("moverState");
                if (stl != null) stl.text = world.SelectedMover.State;
            }
            if (shownPart != null) UpdatePartRows();

            if (!Ready)
            {
                tpLbl.text = "—"; recentLbl.text = LayoutMode && selModule != null && !selModule.built ? "설치 중…" : "최근 1분 —";
                simTimeLbl.text = ""; robotsLbl.text = "";
                chart.SetValues(Array.Empty<float>()); chartMaxLbl.text = "";
                foreach (var (_, val) in kpiLabels) val.text = "—";
                if (posLbl != null && selModule != null) posLbl.text = "설치 중…";
                return;
            }
            tpLbl.text = entry.throughput(view).ToString("N0", CultureInfo.InvariantCulture);
            recentLbl.text = rates.Count > 0 ? $"최근 1분  {rates[rates.Count - 1].ToString("N0", CultureInfo.InvariantCulture)} {entry.throughputUnit}" : "최근 1분 — (집계 중)";
            chart.SetValues(rates);
            chartMaxLbl.text = rates.Count > 1 ? chart.Max.ToString("N0", CultureInfo.InvariantCulture) : "";
            simTimeLbl.text = $"시뮬레이션 t = {entry.simTime(view):0} s";
            robotsLbl.text = $"이동 장비 {entry.robotCount(view)}대";
            foreach (var (def, val) in kpiLabels) val.text = def.value(view) ?? "—";
            if (LayoutMode && selModule != null)
                posLbl.text = $"위치 X {selModule.Min.x:0} m, Z {selModule.Min.y:0} m   ·   크기 {selModule.Size.x:0.0} × {selModule.Size.y:0.0} m";
            UpdateControls();
        }

        // ---------------------------------------------------------------- 물류흐름도 (MFD)
        void BuildMfdPanel(VisualElement host)
        {
            mfdPanel = Panel();
            mfdPanel.style.flexGrow = 1; HudStyle.Margin(mfdPanel, 0, 10, 0, 10); HudStyle.Pad(mfdPanel, 14, 16);
            mfdPanel.style.display = mfdOn ? DisplayStyle.Flex : DisplayStyle.None;
            var head = Row();
            var t = HudStyle.MakeLabel("물류흐름도", 15, HudStyle.Strong, true);
            head.Add(t); head.Add(Gap(12));
            mfdSummary = HudStyle.MakeLabel("", 12, HudStyle.Muted);
            mfdSummary.style.flexGrow = 1;
            head.Add(mfdSummary);
            mfdPanel.Add(head);
            var legend = HudStyle.MakeLabel("하차 → 검수 → 평치 보관(또는 설비) → 출고장 → 상차 · 점선은 수단이 없어 멈춘 흐름 · 흐린 상자는 흐름에 참여하지 않음 · 상자를 누르면 선택", 11, HudStyle.Muted);
            HudStyle.Margin(legend, 4, 0, 0, 0);
            mfdPanel.Add(legend);
            mfd = new MfdView();
            mfd.clicked = OnMfdClick;
            mfdPanel.Add(mfd);
            host.Add(mfdPanel);
            Hover(mfdPanel, b => overMfd = b);

            // CAD 도면
            cadPanel = Panel();
            cadPanel.style.flexGrow = 1; HudStyle.Margin(cadPanel, 0, 10, 0, 10); HudStyle.Pad(cadPanel, 14, 16);
            cadPanel.style.display = cadOn ? DisplayStyle.Flex : DisplayStyle.None;
            var ch = Row();
            ch.Add(HudStyle.MakeLabel("CAD 도면", 15, HudStyle.Strong, true)); ch.Add(Gap(12));
            var chint = HudStyle.MakeLabel("위가 북쪽 · 휠 확대 · 끌어서 이동 · 설비를 누르면 선택 · 배치가 바뀌면 자동으로 다시 그림", 12, HudStyle.Muted);
            chint.style.flexGrow = 1; chint.style.overflow = Overflow.Hidden; chint.style.textOverflow = TextOverflow.Ellipsis;
            ch.Add(chint);
            ch.Add(HudStyle.MakeButton("맞춤", () => { cad?.Fit(); cad?.Refresh(); }));
            cadPanel.Add(ch);
            cad = new CadView();
            HudStyle.Margin(cad, 8, 0, 0, 0);
            cad.style.backgroundColor = HudStyle.Dark ? new Color(0.07f, 0.08f, 0.10f, 1f) : new Color(0.98f, 0.98f, 0.97f, 1f);
            HudStyle.Radius(cad, 10);
            cad.clicked = m => world?.Select(m);
            cadPanel.Add(cad);
            host.Add(cadPanel);
            Hover(cadPanel, b => overMfd = b);
        }

        void SetMfd(bool on) => SetView(on ? 1 : 0);

        // 메인 화면: 0 3D, 1 물류흐름도, 2 CAD 도면
        void SetView(int v)
        {
            if (v != 0 && (!LayoutMode || world == null)) return;
            if (v != 0 && world.DrawingSite) { Toast("센터 영역을 먼저 정하세요"); return; }
            mfdOn = v == 1; cadOn = v == 2;
            if (mfdPanel != null) mfdPanel.style.display = mfdOn ? DisplayStyle.Flex : DisplayStyle.None;
            if (cadPanel != null) cadPanel.style.display = cadOn ? DisplayStyle.Flex : DisplayStyle.None;
            if (v == 0) { overMfd = false; UpdOver(); }
            if (mfdOn) { mfdSig = null; UpdateMfd(); }
            if (cadOn && cad != null) { cad.Bind(world); }
            UpdateMfdTabs();
        }

        void UpdateMfdTabs()
        {
            if (dashPanel != null) dashPanel.style.display = mfdOn || cadOn ? DisplayStyle.None : DisplayStyle.Flex;
            if (tab3dBtn == null) return;
            HudStyle.SetOn(tab3dBtn, !mfdOn && !cadOn); HudStyle.SetOn(tabMfdBtn, mfdOn); HudStyle.SetOn(tabCadBtn, cadOn);
        }

        static string BayStatus(Traffic tr, int door)
        {
            foreach (var b in tr.bays)
            {
                if (b.door != door) continue;
                return b.state switch
                {
                    1 => "트럭 오는 중",
                    2 => "윙 여는 중",
                    3 => $"상하차 {b.done}/{b.units}" + (b.late ? " · 마감 초과" : ""),
                    4 => "윙 닫는 중",
                    5 => "트럭 출발",
                    _ => "트럭 기다림",
                };
            }
            return "—";
        }

        string MoverSub(CargoKind cargo, out bool any)
        {
            int fk = 0, wk = 0, busy = 0; any = false;
            foreach (var m in world.Traffic.movers)
            {
                if (m.kind == MoverInfo.Forklift) fk++; else wk++;
                if (MoverInfo.Handles(m.kind, cargo)) any = true;
                if (m.bay != null) busy++;
            }
            if (fk + wk == 0) return "없음 · 라이브러리 '하역 수단'에서 추가";
            var txt = new List<string>();
            if (fk > 0) txt.Add($"지게차 {fk}");
            if (wk > 0) txt.Add($"작업자 {wk}" + (MoverInfo.Handles(MoverInfo.Worker, cargo) ? "" : "(이 화물 불가)"));
            return string.Join(" · ", txt) + $" · 일하는 중 {busy}";
        }

        static bool HasWorker(Traffic tr) { foreach (var m in tr.movers) if (m.kind == MoverInfo.Worker) return true; return false; }

        static string QcSub(Traffic tr)
        {
            int wk = 0; foreach (var m in tr.movers) if (m.kind == MoverInfo.Worker) wk++;
            return wk == 0 ? $"작업자 없음 · 검수 대기 {tr.AwaitingQc}" : $"작업자 {wk} · 검수 대기 {tr.AwaitingQc} · 완료 {tr.inspected}";
        }

        static string BulkSub(Traffic tr) => $"재고 {tr.Stock}/{tr.StockSlots} · 적치 {tr.stored}";
        static string PickSub(Traffic tr) => $"출고 대기 {tr.OutboundStaged} · 출고 지시 {OrdersOpen(tr)}";

        static int OrdersOpen(Traffic tr) { int n = 0; foreach (var b in tr.bays) if (!b.inbound && b.state == 3) n += Mathf.Max(0, b.units - b.done); return n; }

        string ModuleSub(PlacedModule m)
        {
            if (!world.CanHandle(m.entry)) return $"{CargoInfo.Short(world.Cargo)} 처리 불가";
            if (!m.built || m.view == null || !m.entry.ready(m.view)) return "설치 중…";
            return $"{m.entry.throughput(m.view).ToString("N0", CultureInfo.InvariantCulture)} {m.entry.throughputUnit}";
        }

        // 놓인 것이 바뀌면 그래프를 다시 만들고, 아니면 숫자만 고침
        void UpdateMfd()
        {
            if (mfd == null || world == null) return;
            var s = world.Site; var tr = world.Traffic; var cargo = world.Cargo;
            var sb = new System.Text.StringBuilder();
            sb.Append((int)cargo).Append('|');
            foreach (var d in s.doors) sb.Append(d.kind);
            sb.Append('|');
            foreach (var m in tr.movers) sb.Append(m.kind);
            sb.Append('|');
            foreach (var m in world.modules) if (m != null) sb.Append(m.entry.id).Append(world.CanHandle(m.entry) ? '+' : '-').Append(',');
            string sig = sb.ToString();
            string moverText = MoverSub(cargo, out bool anyMover);
            if (sig != mfdSig)
            {
                mfdSig = sig;
                var ns = new List<MfdNode>(); var es = new List<MfdEdge>();
                Color inC = FloorPlan.InCol, outC = FloorPlan.OutCol, eqC = HudStyle.Accent;
                Color qcC = new Color(0.15f, 0.70f, 0.40f), bulkC = FloorPlan.BulkCol;
                ns.Add(new MfdNode { id = "gate", col = 0, title = "입고 지점", sub = $"화주 화물 · {CargoInfo.Short(cargo)}", color = inC });
                ns.Add(new MfdNode { id = "unload", col = 2, title = "하차 (입고)", sub = moverText, color = inC, dim = !anyMover });
                ns.Add(new MfdNode { id = "qc", col = 3, title = "검수", sub = QcSub(tr), color = qcC, dim = !HasWorker(tr) });
                ns.Add(new MfdNode { id = "bulk", col = 4, title = "평치 보관", sub = BulkSub(tr), color = bulkC });
                ns.Add(new MfdNode { id = "pick", col = 5, title = "출고장", sub = PickSub(tr), color = outC });
                ns.Add(new MfdNode { id = "load", col = 6, title = "상차 (출고)", sub = moverText, color = outC, dim = !anyMover });
                ns.Add(new MfdNode { id = "exit", col = 8, title = "출고 지점", sub = "화주 출고", color = outC });
                es.Add(new MfdEdge { a = "unload", b = "qc", color = inC, dashed = !anyMover });
                es.Add(new MfdEdge { a = "qc", b = "bulk", color = qcC, dashed = !HasWorker(tr) });
                es.Add(new MfdEdge { a = "bulk", b = "pick", color = bulkC });
                es.Add(new MfdEdge { a = "pick", b = "load", color = outC, dashed = !anyMover });
                for (int i = 0; i < s.doors.Count; i++)
                {
                    var d = s.doors[i]; string id = "door" + i;
                    bool inbound = d.kind == 0;
                    ns.Add(new MfdNode { id = id, col = inbound ? 1 : 7, title = Plan.DoorName(s, i), sub = BayStatus(tr, i), color = inbound ? inC : outC });
                    if (inbound) { es.Add(new MfdEdge { a = "gate", b = id, color = inC }); es.Add(new MfdEdge { a = id, b = "unload", color = inC, dashed = !anyMover }); }
                    else { es.Add(new MfdEdge { a = "load", b = id, color = outC, dashed = !anyMover }); es.Add(new MfdEdge { a = id, b = "exit", color = outC }); }
                }
                for (int k = 0; k < world.modules.Count; k++)
                {
                    var m = world.modules[k]; if (m == null) continue;
                    bool can = world.CanHandle(m.entry);
                    string id = "mod" + k;
                    ns.Add(new MfdNode { id = id, col = 4, title = m.entry.name, sub = ModuleSub(m), color = eqC, dim = !can });
                    if (!can) continue;
                    es.Add(new MfdEdge { a = "qc", b = id, color = eqC });
                    es.Add(new MfdEdge { a = id, b = "pick", color = eqC });
                }
                mfd.SetGraph(ns, es);
            }
            else
            {
                mfd.SetSub("unload", moverText); mfd.SetSub("load", moverText);
                mfd.SetSub("qc", QcSub(tr)); mfd.SetSub("bulk", BulkSub(tr)); mfd.SetSub("pick", PickSub(tr));
                for (int i = 0; i < s.doors.Count; i++) mfd.SetSub("door" + i, BayStatus(tr, i));
                for (int k = 0; k < world.modules.Count; k++) if (world.modules[k] != null) mfd.SetSub("mod" + k, ModuleSub(world.modules[k]));
            }
            if (mfdSummary != null)
                mfdSummary.text = (world.Unlimited ? "무제한 모드" : $"잔고 {EquipCatalog.Money(tr.balance)}") + $" · 입고 처리 {tr.inDone} · 출고 처리 {tr.outDone} · 정시 {tr.onTime} · 마감 초과 {tr.missed} · 대기 트럭 {tr.TrucksWaiting}";
        }

        void OnMfdClick(string id)
        {
            if (world == null) return;
            if (id.StartsWith("door") && int.TryParse(id.Substring(4), out int di) && di < world.Site.doors.Count) world.SelectDoor(di);
            else if (id.StartsWith("mod") && int.TryParse(id.Substring(3), out int mi) && mi < world.modules.Count && world.modules[mi] != null) world.Select(world.modules[mi]);
            else if ((id == "unload" || id == "load") && world.Traffic.movers.Count > 0) world.SelectMover(world.Traffic.movers[0]);
            else if (id == "unload" || id == "load") Toast("왼쪽 라이브러리 '하역 수단'에서 지게차나 작업자를 끌어다 센터 안에 놓으세요");
            else if (id == "gate" || id == "exit") Toast("입고·출고 지점은 부지 서쪽 진입로에 있습니다");
            else if (id == "qc") Toast("작업자가 입고 대기 구역의 화물을 검수합니다. 검수가 끝나야 평치로 옮깁니다.");
            else if (id == "bulk") Toast("평치 구역: 검수한 화물을 바닥에 적치합니다. 출고 지시가 나오면 여기서 출고장으로 옮깁니다.");
            else if (id == "pick") Toast("출고장: 출고문 안쪽 대기 구역. 여기 모인 화물을 트럭에 싣습니다.");
        }

        void UpdatePlant()
        {
            if (plantDirty || plantRows.Count != world.modules.Count) RebuildPlant();
            int built = 0, robots = 0;
            var sums = new Dictionary<string, double>();
            foreach (var (m, val) in plantRows)
            {
                if (m == null) continue;
                if (!m.built || m.view == null || !m.entry.ready(m.view)) { val.text = "설치 중…"; continue; }
                if (!world.CanHandle(m.entry)) { val.text = "화물 처리 불가"; val.style.color = HudStyle.Bad; continue; }
                val.style.color = HudStyle.Muted;
                built++;
                robots += m.entry.robotCount(m.view);
                double tp = m.entry.throughput(m.view);
                sums.TryGetValue(m.entry.throughputUnit, out var s0); sums[m.entry.throughputUnit] = s0 + tp;
                val.text = $"{tp.ToString("N0", CultureInfo.InvariantCulture)} {m.entry.throughputUnit}";
            }
            if (plantCountLbl != null) plantCountLbl.text = world.modules.Count.ToString();
            var tr = world.Traffic;
            if (balanceLbl != null && tr != null)
            {
                balanceLbl.text = world.Unlimited ? "무제한" : EquipCatalog.Money(tr.balance);
                balanceLbl.style.color = tr.balance < 0 ? HudStyle.Bad : tr.balance > 0 ? HudStyle.Ok : HudStyle.Strong;
                inDoneLbl.text = tr.inDone.ToString(); outDoneLbl.text = tr.outDone.ToString();
                trucksLbl.text = tr.TrucksWaiting.ToString();
            }
            if (plantRobotLbl != null) plantRobotLbl.text = robots.ToString();
            foreach (var kv in plantUnitLbls)
                kv.Value.text = sums.TryGetValue(kv.Key, out var s) ? s.ToString("N0", CultureInfo.InvariantCulture) : "—";
        }

        // ---------------------------------------------------------------- 하위 오브젝트 속성
        void RefreshPart()
        {
            if (partCard == null) return;
            var p = LayoutMode && world != null ? world.SelectedPart : null;
            if (p != null && p.module != selModule) p = null;
            if (p != shownPart) { partRowsBox.Clear(); partRows.Clear(); }
            shownPart = p;
            partCard.style.display = p != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (p == null) return;
            partTitleLbl.text = p.Label;
            partSubLbl.text = $"{p.module.Label} 의 하위 오브젝트  ·  Esc = 설비 전체";
            UpdatePartRows();
            (propBody as ScrollView)?.ScrollTo(partCard);
        }

        void UpdatePartRows()
        {
            var p = shownPart;
            if (p == null || p.t == null) return;
            partTmp.Clear();
            var w = p.t.position - (p.module != null ? p.module.transform.position : Vector3.zero);
            partTmp.Add(("위치 (설비 기준)", $"X {w.x:0.00}  Y {w.y:0.00}  Z {w.z:0.00} m"));
            var item = p.SimItem();
            if (item != null)
                foreach (var f in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var v = FormatPartValue(f.GetValue(item), f.FieldType, out bool show);
                    if (show) partTmp.Add((PartFieldName(f.Name), v));
                }
            else if (p.def.simList != null) partTmp.Add(("상태", "—"));

            bool same = partRows.Count == partTmp.Count;
            for (int i = 0; same && i < partTmp.Count; i++) same = partRows[i].key == partTmp[i].key;
            if (!same)
            {
                partRowsBox.Clear(); partRows.Clear();
                foreach (var (k, v) in partTmp)
                {
                    var row = Row(); row.style.justifyContent = Justify.SpaceBetween; HudStyle.Margin(row, 0, 0, 4, 0);
                    var kl = HudStyle.MakeLabel(k, 11, HudStyle.Muted); kl.style.flexShrink = 0; HudStyle.Margin(kl, 0, 10, 0, 0);
                    var vl = HudStyle.MakeLabel(v, 12, HudStyle.Strong); vl.style.unityTextAlign = TextAnchor.MiddleRight;
                    vl.style.whiteSpace = WhiteSpace.Normal; vl.style.flexShrink = 1;
                    row.Add(kl); row.Add(vl);
                    partRowsBox.Add(row);
                    partRows.Add((k, vl));
                }
            }
            else for (int i = 0; i < partTmp.Count; i++) partRows[i].val.text = partTmp[i].val;
        }

        static string FormatPartValue(object o, Type t, out bool show)
        {
            show = true;
            if (t == typeof(double) || t == typeof(float)) return Convert.ToDouble(o).ToString("0.##", CultureInfo.InvariantCulture);
            if (t == typeof(int) || t == typeof(long)) return Convert.ToString(o, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return (bool)o ? "예" : "아니오";
            if (t == typeof(string)) return string.IsNullOrEmpty((string)o) ? "—" : (string)o;
            if (t.IsEnum) return o.ToString();
            if (t == typeof(Vector3) || t == typeof(Vector2)) return o.ToString();
            // 작업 객체 등: Kind / kind 같은 이름 속성이 있으면 그 값
            if (!t.IsValueType && !(typeof(System.Collections.IEnumerable).IsAssignableFrom(t)))
            {
                var kp = t.GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public);
                if (kp != null && kp.PropertyType == typeof(string)) return o == null ? "없음" : (string)kp.GetValue(o);
                if (t.IsInterface)
                {
                    if (o == null) return "없음";
                    var kp2 = o.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public);
                    if (kp2 != null && kp2.PropertyType == typeof(string)) return (string)kp2.GetValue(o);
                }
            }
            show = false;
            return null;
        }

        static readonly Dictionary<string, string> PartNames = new Dictionary<string, string>
        {
            { "id", "번호" }, { "state", "상태" }, { "kind", "종류" }, { "task", "작업" },
            { "carry", "운반 중 (번호)" }, { "x", "x" }, { "y", "y" }, { "z", "z" },
            { "px", "위치 x" }, { "py", "위치 y" }, { "pz", "위치 z" },
            { "busyTime", "가동 시간 (s)" }, { "distance", "이동 거리 (m)" }, { "waitT", "대기 시간 (s)" }, { "blocked", "막힌 시간 (s)" },
            { "deliveries", "배송 횟수" }, { "returns", "반납 횟수" }, { "digs", "디깅 횟수" }, { "picked", "피킹 수" },
            { "count", "개수" }, { "received", "받은 수" }, { "load", "적재량" }, { "aisle", "통로" }, { "level", "단" },
            { "l", "단" }, { "a", "통로" }, { "side", "방향" }, { "arm", "암" }, { "fork", "포크" }, { "lift", "리프트" },
            { "cur", "현재 작업" }, { "slot", "슬롯" }, { "r", "로봇" }, { "s", "슬롯" }, { "u", "진행" }, { "g", "그리퍼 높이" },
        };

        static string PartFieldName(string n) => PartNames.TryGetValue(n, out var k) ? $"{k}" : n;

        // ---------------------------------------------------------------- 물류센터 건물 · 출입문
        bool SiteSelected => LayoutMode && world != null && (world.BuildingSelected || world.SelectedDoor >= 0 || world.SelectedMover != null);

        void BuildSiteBanner()
        {
            siteBanner = new VisualElement();
            siteBanner.style.position = Position.Absolute; siteBanner.style.top = 80; siteBanner.style.alignSelf = Align.Center;
            siteBanner.style.flexDirection = FlexDirection.Row; siteBanner.style.alignItems = Align.Center;
            siteBanner.style.backgroundColor = HudStyle.Panel; HudStyle.Radius(siteBanner, 14); HudStyle.Pad(siteBanner, 10, 16);
            HudStyle.Border(siteBanner, 2, HudStyle.Accent);
            var txt = new VisualElement { pickingMode = PickingMode.Ignore };
            txt.Add(HudStyle.MakeLabel("물류센터 영역 그리기", 15, HudStyle.Strong, true));
            siteBannerLbl = HudStyle.MakeLabel("바닥을 마우스로 끌어 건물 직사각형을 그리세요 (최소 20 × 20 m, 1 m 단위). 바닥은 트럭 상차 높이로 올라갑니다.", 12, HudStyle.Muted);
            HudStyle.Margin(siteBannerLbl, 3, 0, 0, 0);
            txt.Add(siteBannerLbl);
            siteBanner.Add(txt);
            siteBanner.Add(Gap(16));
            var def = HudStyle.MakeButton("기본 크기 140 × 80 m", () => world?.EndSiteDrawing(true));
            siteBannerKeep = HudStyle.MakeButton("지금 건물 유지", () => world?.EndSiteDrawing(false));
            siteBanner.Add(def); siteBanner.Add(siteBannerKeep);
            siteBanner.RegisterCallback<PointerEnterEvent>(_ => { overBanner = true; UpdOver(); });
            siteBanner.RegisterCallback<PointerLeaveEvent>(_ => { overBanner = false; UpdOver(); });
            siteBanner.style.display = DisplayStyle.None;
            root.Add(siteBanner);
            OnDrawingChanged();
        }

        TextField MakeField(string value, float width)
        {
            var tf = new TextField { value = value, isDelayed = true };
            tf.style.width = width; tf.style.height = 30; tf.style.fontSize = 13; HudStyle.Margin(tf, 0, 0, 0, 0);
            var input = tf.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.backgroundColor = HudStyle.Field; input.style.color = HudStyle.Strong;
                HudStyle.Border(input, 0, Color.clear); HudStyle.Radius(input, 8); HudStyle.Pad(input, 0, 8);
            }
            tf.Query<TextElement>().ForEach(te => te.style.color = HudStyle.Strong);
            return tf;
        }

        static float ParseF(string t, float fallback) =>
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        VisualElement FieldRow(string label, string hint, TextField tf)
        {
            var row = Row(); HudStyle.Margin(row, 0, 0, 8, 0);
            var labels = new VisualElement { pickingMode = PickingMode.Ignore }; labels.style.flexGrow = 1;
            labels.Add(HudStyle.MakeLabel(label, 12, HudStyle.Text));
            if (!string.IsNullOrEmpty(hint)) labels.Add(HudStyle.MakeLabel(hint, 10, HudStyle.Muted));
            row.Add(labels); row.Add(tf);
            return row;
        }

        VisualElement Stepper(string label, int value, Action<int> set)
        {
            var row = Row(); HudStyle.Margin(row, 0, 0, 8, 0);
            var l = HudStyle.MakeLabel(label, 12, HudStyle.Text); l.style.flexGrow = 1;
            var minus = HudStyle.MakeButton("−", () => set(value - 1), 32); minus.style.height = 28;
            var v = HudStyle.MakeLabel(value.ToString(), 14, HudStyle.Strong, true); v.style.width = 28; v.style.unityTextAlign = TextAnchor.MiddleCenter;
            var plus = HudStyle.MakeButton("+", () => set(value + 1), 32); plus.style.height = 28;
            row.Add(l); row.Add(minus); row.Add(v); row.Add(plus);
            return row;
        }

        void RebuildSiteBox()
        {
            if (siteBox == null || world == null) return;
            siteBox.Clear();
            var s = world.Site;
            int di = world.SelectedDoor;
            if (world.SelectedMover != null)
            {
                var mv = world.SelectedMover;
                siteBox.Add(SectionTitle("상태"));
                var st = HudStyle.MakeLabel(mv.State, 13, HudStyle.Strong, true); st.name = "moverState";
                siteBox.Add(st);
                var canDo = MoverInfo.Handles(mv.kind, world.Cargo);
                if (!canDo)
                {
                    var w = HudStyle.MakeLabel($"{CargoInfo.Name(world.Cargo)}은(는) 이 수단으로 하역할 수 없습니다. 지게차를 놓으세요.", 12, HudStyle.Bad);
                    w.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(w, 6, 0, 0, 0); siteBox.Add(w);
                }
                var acts = Row(); HudStyle.Margin(acts, 12, 0, 0, 0);
                var del = HudStyle.MakeButton("삭제", () => world.RemoveMover(mv)); del.style.color = HudStyle.Rec;
                acts.Add(del);
                siteBox.Add(acts);
                return;
            }
            if (di >= 0 && di < s.doors.Count)
            {
                var d = s.doors[di];
                siteBox.Add(SectionTitle("출입문"));
                var kinds = Row(); HudStyle.Margin(kinds, 0, 0, 8, 0);
                var kIn = HudStyle.MakeButton("입고", () => world.UpdateDoor(di, 0, d.side, d.pos), 70);
                var kOut = HudStyle.MakeButton("출고", () => world.UpdateDoor(di, 1, d.side, d.pos), 70);
                HudStyle.SetOn(kIn, d.kind == 0); HudStyle.SetOn(kOut, d.kind == 1);
                kinds.Add(HudStyle.MakeLabel("종류", 12, HudStyle.Text)); kinds.Add(Gap(10)); kinds.Add(kIn); kinds.Add(kOut);
                siteBox.Add(kinds);
                var walls = Row(); HudStyle.Margin(walls, 0, 0, 8, 0);
                walls.Add(HudStyle.MakeLabel("벽", 12, HudStyle.Text)); walls.Add(Gap(22));
                for (int k = 0; k < 4; k++)
                {
                    int kk = k;
                    var b = HudStyle.MakeButton(Plan.SideNames[k].Substring(0, 1), () => world.UpdateDoor(di, d.kind, kk, Plan.WallLen(world.Site, kk) / 2), 40);
                    HudStyle.SetOn(b, d.side == k);
                    walls.Add(b);
                }
                siteBox.Add(walls);
                var posF = MakeField(d.pos.ToString("0.#", CultureInfo.InvariantCulture), 90);
                posF.RegisterValueChangedCallback(ev => world.UpdateDoor(di, d.kind, d.side, ParseF(ev.newValue, d.pos)));
                siteBox.Add(FieldRow("위치 (m)", $"{Plan.SideNames[d.side]} 벽 모서리부터, 0 – {Plan.WallLen(s, d.side):0}", posF));
                var acts = Row(); HudStyle.Margin(acts, 6, 0, 0, 0);
                var back = HudStyle.MakeButton("건물 속성", () => world.SelectBuilding());
                var del = HudStyle.MakeButton("문 삭제", () => world.RemoveDoor(di)); del.style.color = HudStyle.Rec;
                acts.Add(back); acts.Add(del);
                siteBox.Add(acts);
                return;
            }

            siteBox.Add(SectionTitle("건물"));
            var fx = MakeField(s.x.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var fz = MakeField(s.z.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var fw = MakeField(s.w.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var fd = MakeField(s.d.ToString("0.#", CultureInfo.InvariantCulture), 90);
            var fh = MakeField(s.h.ToString("0.0#", CultureInfo.InvariantCulture), 90);
            siteBox.Add(FieldRow("위치 X (m)", "남서쪽 모서리", fx));
            siteBox.Add(FieldRow("위치 Z (m)", null, fz));
            siteBox.Add(FieldRow("폭 (m)", "동서 길이, 20 m 이상", fw));
            siteBox.Add(FieldRow("깊이 (m)", "남북 길이, 20 m 이상", fd));
            siteBox.Add(FieldRow("바닥 높이 (m)", "트럭 적재함 높이, 보통 1.1 – 1.3 m", fh));
            var apply = Row(); apply.style.justifyContent = Justify.FlexEnd; HudStyle.Margin(apply, 2, 0, 0, 0);
            var redraw = HudStyle.MakeButton("영역 다시 그리기", () => world.BeginSiteDrawing());
            var ok = HudStyle.MakeButton("적용", () =>
            {
                var n = world.Site.Clone();
                n.x = Mathf.Round(ParseF(fx.value, n.x)); n.z = Mathf.Round(ParseF(fz.value, n.z));
                n.w = Mathf.Round(ParseF(fw.value, n.w)); n.d = Mathf.Round(ParseF(fd.value, n.d));
                n.h = ParseF(fh.value, n.h);
                world.ApplySite(n, true);
                RebuildSiteBox();
            });
            HudStyle.SetOn(ok, true);
            apply.Add(redraw); apply.Add(ok);
            siteBox.Add(apply);

            siteBox.Add(SectionTitle("출입문"));
            int nIn = 0, nOut = 0; foreach (var d in s.doors) { if (d.kind == 0) nIn++; else nOut++; }
            siteBox.Add(Stepper("입고문 수", nIn, v => world.SetDoorCount(0, v)));
            siteBox.Add(Stepper("출고문 수", nOut, v => world.SetDoorCount(1, v)));
            var hint = HudStyle.MakeLabel("문은 장면에서 끌어 벽을 따라 옮길 수 있습니다. 문을 클릭하면 종류·벽·위치를 바꿀 수 있습니다.", 11, HudStyle.Muted);
            hint.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(hint, 2, 0, 8, 0);
            siteBox.Add(hint);
            for (int i = 0; i < s.doors.Count; i++)
            {
                int ii = i; var d = s.doors[i];
                var row = Row(); row.pickingMode = PickingMode.Position;
                HudStyle.Pad(row, 6, 10); HudStyle.Radius(row, 8); HudStyle.Margin(row, 0, 0, 2, 0);
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.style.width = 8; dot.style.height = 8; HudStyle.Radius(dot, 4); HudStyle.Margin(dot, 0, 8, 0, 0);
                dot.style.backgroundColor = d.kind == 0 ? FloorPlan.InCol : FloorPlan.OutCol;
                var n = HudStyle.MakeLabel(Plan.DoorName(s, i), 12, HudStyle.Text); n.style.flexGrow = 1;
                var v = HudStyle.MakeLabel($"{Plan.SideNames[d.side]} {d.pos:0.#} m", 11, HudStyle.Muted);
                row.Add(dot); row.Add(n); row.Add(v);
                row.RegisterCallback<PointerEnterEvent>(_ => row.style.backgroundColor = HudStyle.TileHover);
                row.RegisterCallback<PointerLeaveEvent>(_ => row.style.backgroundColor = Color.clear);
                row.RegisterCallback<ClickEvent>(_ => world.SelectDoor(ii));
                siteBox.Add(row);
            }
        }

        // ---------------------------------------------------------------- 화물 선택
        void RefreshCards()
        {
            foreach (var kv in cards)
            {
                bool ok = !LayoutMode || world == null || world.CanHandle(kv.Key);
                kv.Value.style.opacity = ok ? 1f : 0.4f;
                var cg = kv.Value.Q<Label>("cargo");
                if (cg != null) cg.style.color = ok ? HudStyle.Muted : HudStyle.Bad;
            }
            if (cargoChip != null && world != null) cargoChip.text = $"화물: {CargoInfo.Short(world.Cargo)} · {(world.Unlimited ? "무제한" : "자금")}  ▾";
        }

        void BuildCargoModal()
        {
            cargoModal = new VisualElement();
            cargoModal.style.position = Position.Absolute; cargoModal.style.left = 0; cargoModal.style.right = 0; cargoModal.style.top = 0; cargoModal.style.bottom = 0;
            cargoModal.style.backgroundColor = new Color(0, 0, 0, HudStyle.Dark ? 0.55f : 0.30f);
            cargoModal.style.alignItems = Align.Center; cargoModal.style.justifyContent = Justify.Center;
            cargoModal.RegisterCallback<PointerEnterEvent>(_ => { overModal = true; UpdOver(); });
            cargoModal.RegisterCallback<PointerLeaveEvent>(_ => { overModal = false; UpdOver(); });

            var card = new VisualElement();
            card.style.backgroundColor = HudStyle.Dark ? new Color(0.09f, 0.10f, 0.12f, 0.98f) : new Color(1, 1, 1, 0.99f);
            HudStyle.Radius(card, 18); HudStyle.Pad(card, 26, 28); card.style.maxWidth = 920;
            var t = HudStyle.MakeLabel("어떤 화물을 다루는 물류센터인가요?", 22, HudStyle.Strong, true);
            var st = HudStyle.MakeLabel("화물에 맞는 설비만 설치할 수 있습니다. 입고·출고 도크와 동선도 이 화물 기준으로 표시됩니다. 나중에 위쪽 막대의 화물 단추로 바꿀 수 있습니다.", 13, HudStyle.Muted);
            st.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(st, 6, 0, 18, 0);
            card.Add(t); card.Add(st);
            cargoTiles = new VisualElement { pickingMode = PickingMode.Ignore };
            cargoTiles.style.flexDirection = FlexDirection.Row; cargoTiles.style.flexWrap = Wrap.Wrap;
            card.Add(cargoTiles);
            // 게임 모드
            var mt = HudStyle.MakeLabel("게임 모드", 15, HudStyle.Strong, true); HudStyle.Margin(mt, 14, 0, 8, 0);
            card.Add(mt);
            var modes = Row();
            modeFundsBtn = HudStyle.MakeButton("자금 모드", () => { modalUnlimited = false; UpdateModeButtons(); }, 130);
            modeFundsBtn.tooltip = "화주 주문을 처리하면 보상, 마감을 넘기면 패널티. 잔고가 움직입니다.";
            modeFreeBtn = HudStyle.MakeButton("무제한 모드", () => { modalUnlimited = true; UpdateModeButtons(); }, 130);
            modeFreeBtn.tooltip = "돈과 마감 없이 자유롭게 센터를 설계합니다. 트럭과 상하차는 그대로 돌아갑니다.";
            modes.Add(modeFundsBtn); modes.Add(Gap(8)); modes.Add(modeFreeBtn); modes.Add(Gap(12));
            modeDescLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted);
            modeDescLbl.style.flexGrow = 1; modeDescLbl.style.whiteSpace = WhiteSpace.Normal;
            modes.Add(modeDescLbl);
            card.Add(modes);
            var btns = Row(); btns.style.justifyContent = Justify.FlexEnd; HudStyle.Margin(btns, 18, 0, 0, 0);
            var go = HudStyle.MakeButton("이 화물로 시작", () =>
            {
                world?.SetUnlimited(modalUnlimited);
                world?.SetCargo(modalPick);
                HideCargoModal();
                if (modalStartFlow && world != null) world.BeginSiteDrawing();   // 다음 단계: 물류센터 영역 그리기
            }, 140);
            go.style.height = 38; HudStyle.SetOn(go, true);
            btns.Add(go);
            card.Add(btns);
            cargoModal.Add(card);
            cargoModal.style.display = DisplayStyle.None;
            root.Add(cargoModal);
        }

        Label modeDescLbl;

        void UpdateModeButtons()
        {
            if (modeFundsBtn == null) return;
            HudStyle.SetOn(modeFundsBtn, !modalUnlimited); HudStyle.SetOn(modeFreeBtn, modalUnlimited);
            modeDescLbl.text = modalUnlimited
                ? "돈과 마감 없이 자유롭게 설계합니다. 설비도 무료입니다. 트럭과 상하차는 그대로 돌아갑니다."
                : $"시작 자본 {EquipCatalog.Money(LayoutWorld.StartCapital)}으로 설비와 하역 수단을 사고(지우면 70% 환불), 주문 보상 · 마감 패널티로 잔고가 움직입니다.";
        }

        void RebuildCargoTiles()
        {
            cargoTiles.Clear();
            foreach (var k in CargoInfo.All)
            {
                var kk = k;
                bool on = k == modalPick;
                var tile = new VisualElement();
                tile.style.width = 200; HudStyle.Pad(tile, 16, 14); HudStyle.Margin(tile, 0, 10, 10, 0); HudStyle.Radius(tile, 14);
                tile.style.backgroundColor = on ? HudStyle.AccentDim : HudStyle.Tile;
                HudStyle.Border(tile, 2, on ? HudStyle.Accent : Color.clear);
                tile.Add(new CargoIcon(k, 56));
                var n = HudStyle.MakeLabel(CargoInfo.Name(k) + (k == CargoKind.Pallet ? "  (기본)" : ""), 15, HudStyle.Strong, true); HudStyle.Margin(n, 10, 0, 0, 0);
                var d = HudStyle.MakeLabel(CargoInfo.Desc(k), 11, HudStyle.Muted); d.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(d, 4, 0, 0, 0);
                var names = new List<string>();
                foreach (var e in EquipCatalog.All) if (e.Handles(k)) names.Add(e.name);
                var eq = HudStyle.MakeLabel("처리 설비: " + (names.Count > 0 ? string.Join(", ", names) : "없음"), 11, HudStyle.Text);
                eq.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(eq, 8, 0, 0, 0);
                tile.Add(n); tile.Add(d); tile.Add(eq);
                tile.RegisterCallback<PointerEnterEvent>(_ => { if (kk != modalPick) tile.style.backgroundColor = HudStyle.TileHover; });
                tile.RegisterCallback<PointerLeaveEvent>(_ => { if (kk != modalPick) tile.style.backgroundColor = HudStyle.Tile; });
                tile.RegisterCallback<ClickEvent>(_ => { modalPick = kk; RebuildCargoTiles(); });
                cargoTiles.Add(tile);
            }
        }

        void ShowCargoModal() => ShowCargoModal(false);

        void ShowCargoModal(bool startFlow)
        {
            modalStartFlow = startFlow;
            cargoAsked = true;
            if (cargoModal == null || !LayoutMode || world == null) return;
            modalPick = world.Cargo;
            modalUnlimited = world.Unlimited;
            RebuildCargoTiles();
            UpdateModeButtons();
            cargoModal.style.display = DisplayStyle.Flex;
            overModal = true; UpdOver();
        }

        void HideCargoModal()
        {
            if (cargoModal == null) return;
            cargoModal.style.display = DisplayStyle.None;
            overModal = false; UpdOver();
            RefreshCards();
        }

        // 장면 위에 떠 있는 이름표: 입고·출고 지점, 출입문, 건물 크기
        readonly List<Label> tags = new List<Label>();
        int tagUsed;

        void BuildDockLabels() { tags.Clear(); }

        void UpdateDockLabels()
        {
            tagUsed = 0;
            var cam = Camera.main;
            if (LayoutMode && world != null && cam != null && root.panel != null && hudVisible)
            {
                var s = world.Site;
                var inC = FloorPlan.InCol; inC.a = 0.92f; var outC = FloorPlan.OutCol; outC.a = 0.92f;
                Tag(cam, new Vector3(Plan.InPoint.x + 1.5f, 2.2f, Plan.InPoint.y), $"입고 지점 · {CargoInfo.Short(world.Cargo)}", inC, 12);
                Tag(cam, new Vector3(Plan.OutPoint.x + 1.5f, 5.2f, Plan.OutPoint.y), "출고 지점", outC, 12);
                if (s.doors.Count > 0)
                {
                    var br = Plan.BulkRect(s); var bc = FloorPlan.BulkCol; bc.a = 0.9f;
                    Tag(cam, new Vector3(br.center.x, s.h + 2.2f, br.center.y), $"평치 구역 · 재고 {world.Traffic.Stock}/{world.Traffic.StockSlots}", bc, 11);
                }
                var yardSides = new HashSet<int>();
                foreach (var d in s.doors)
                    if (yardSides.Add(d.side))
                    {
                        var ar = Plan.Apron(s, d.side);
                        Tag(cam, new Vector3(ar.center.x, 0.6f, ar.center.y), "트럭 주차장", new Color(0.22f, 0.24f, 0.28f, 0.8f), 11);
                    }
                for (int i = 0; i < s.doors.Count; i++)
                {
                    var d = s.doors[i];
                    var p = Plan.DoorPoint(s, d) + Plan.Outward(d.side) * 2.5f;
                    var c = d.kind == 0 ? inC : outC;
                    if (i == world.SelectedDoor) c = HudStyle.Accent;
                    Tag(cam, new Vector3(p.x, s.h + 1.6f, p.y), Plan.DoorName(s, i), c, 11);
                }
                foreach (var (pos, text, late) in world.Traffic.Tags(s))
                    Tag(cam, pos, text, late ? new Color(0.85f, 0.20f, 0.20f, 0.92f) : new Color(0.15f, 0.17f, 0.20f, 0.88f), 11);
                var top = new Vector3(s.x + s.w / 2, 0.5f, s.z - 6f);
                Tag(cam, top, $"{s.w:0} × {s.d:0} m  ·  바닥 높이 {s.h:0.0#} m", new Color(0.2f, 0.22f, 0.26f, 0.85f), 11);
            }
            for (int i = tagUsed; i < tags.Count; i++) tags[i].style.display = DisplayStyle.None;
        }

        void Tag(Camera cam, Vector3 w3, string text, Color bg, float size)
        {
            if (Vector3.Dot(cam.transform.forward, w3 - cam.transform.position) <= 0) return;
            if (tagUsed >= tags.Count)
            {
                var nl = HudStyle.MakeLabel("", 12, Color.white, true);
                nl.style.position = Position.Absolute; HudStyle.Pad(nl, 3, 9); HudStyle.Radius(nl, 9);
                nl.style.whiteSpace = WhiteSpace.NoWrap;
                root.Insert(0, nl);   // 패널 아래에 깔리도록 맨 앞
                tags.Add(nl);
            }
            var l = tags[tagUsed++];
            var p = RuntimePanelUtils.CameraTransformWorldToPanel(root.panel, w3, cam);
            if (l.text != text) l.text = text;
            l.style.fontSize = size; l.style.backgroundColor = bg;
            l.style.display = DisplayStyle.Flex;
            l.style.left = p.x; l.style.top = p.y;
            l.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
        }

        void Toast(string msg)
        {
            if (toastLbl == null) return;
            toastLbl.text = msg; toastLbl.style.display = DisplayStyle.Flex;
            toastUntil = Time.unscaledTime + 4f;
        }
    }

    // 한글 UI 글꼴: Windows 맑은 고딕을 OS 에서 바로 씀 (추가 에셋 없음). 실패하면 테마 기본 글꼴.
    static class HudFont
    {
        static StyleFontDefinition? cached;

        public static StyleFontDefinition? Get()
        {
            if (cached.HasValue) return cached;
            try
            {
                MethodInfo from = null;
                foreach (var m in typeof(FontDefinition).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "FromSDFFont" && m.GetParameters().Length == 1) { from = m; break; }
                if (from == null) return null;
                foreach (var family in new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Segoe UI" })
                {
                    var fa = CreateOsFontAsset(family);
                    if (fa == null || !from.GetParameters()[0].ParameterType.IsInstanceOfType(fa)) continue;
                    var def = (FontDefinition)from.Invoke(null, new[] { fa });
                    cached = new StyleFontDefinition(def);
                    return cached;
                }
            }
            catch (Exception e) { Debug.LogWarning("[HUD] 한글 글꼴을 만들지 못해 기본 글꼴을 씁니다: " + e.Message); }
            return null;
        }

        static object CreateOsFontAsset(string family)
        {
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType("UnityEngine.TextCore.Text.FontAsset");
                    if (t != null) break;
                }
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "CreateFontAsset") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(string)) continue;
                    var args = new object[ps.Length];
                    args[0] = family; args[1] = "Regular";
                    for (int i = 2; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(int) && i == 2 ? 90 : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                    var fa = m.Invoke(null, args);
                    if (fa == null) continue;
                    var multi = t.GetProperty("isMultiAtlasTexturesEnabled");
                    if (multi != null && multi.CanWrite) multi.SetValue(fa, true);
                    return fa;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
