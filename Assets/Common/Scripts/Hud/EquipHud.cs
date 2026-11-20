// 통합 HUD (플레이 모드, UI Toolkit). 어떤 설비 장면에서 Play 를 눌러도 자동으로 뜹니다.
//  - 왼쪽: 솔루션 라이브러리. 지금까지 만든 설비 11종을 눌러 바로 전환 (장면 파일이 있으면 열고, 없으면 코드로 만듦)
//  - 위쪽: 처음부터 / 재생·일시정지 / ±5 s / 재생 속도 (0.25×~8×) / 30초 타임라인 (샷 경계 눈금, 끌어서 이동)
//          / 카메라 (시네마틱 · 자유 시점) / 자막 / 30초 반복 / 30초 녹화 (에디터) / HUD 숨김
//  - 오른쪽: 선택한 설비의 설명, 실시간 대시보드 (처리량·최근 1분 처리량 그래프·KPI), 속성 (로봇 수·주문량·시드 변경 → 다시 시작, 모델 사양)
// 단축키: H HUD 숨김/표시, Space 재생/일시정지, R 처음부터, ← → 5초 이동, C 카메라 전환
// 녹화(애니메이션 > <설비> > Record 30s Movie) 중에는 HUD 가 자동으로 숨고 시간도 건드리지 않습니다. 녹화 영상에는 HUD 가 들어가지 않습니다.
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
    // 마우스가 HUD 위에 있으면 장면 카메라의 마우스 오비트를 막음
    public static class HudInput
    {
        public static bool PointerOverUi;
    }

    public class EquipHud : MonoBehaviour
    {
        public const string PrefEnabled = "EquipFX.Hud.Enabled";
        public const float MovieLength = 30f;
        const float FastForward = 24f;            // 타임라인 앞으로 이동할 때 배속
        const float RateWindow = 60f;             // 최근 처리량 창 (시뮬레이션 초)
        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        // 에디터 쪽에서 채움 (Assets/Common/Editor/EquipHudEditor.cs)
        public static Func<string, bool> EditorLoadScene;     // 장면 파일 경로 → 불러오기 시작했으면 true
        public static Action<EquipEntry> EditorRecord;        // 플레이 모드를 끝내고 그 설비의 30초 녹화 메뉴 실행

        public static EquipHud Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; HudInput.PointerOverUi = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || PlayerPrefs.GetInt(PrefEnabled, 1) == 0 || Instance != null) return;
            var go = new GameObject("EquipHud");
            DontDestroyOnLoad(go);
            go.AddComponent<EquipHud>();
        }

        // ------------------------------------------------------------------ 상태
        EquipEntry entry;
        Component view;
        float speed = 1f;
        bool paused, loop = true, captionsOn = true, hudVisible = true, recording, loading;
        float clockStart = -1f, ffTarget = -1f, pendingSeek = -1f;
        float nextUiRefresh, toastUntil;
        bool overTop, overLeft, overRight;
        readonly Dictionary<string, Dictionary<string, double>> overrides = new Dictionary<string, Dictionary<string, double>>();
        readonly List<Vector2> samples = new List<Vector2>();    // (sim t, 누적 처리 건수)
        readonly List<float> rates = new List<float>();          // 최근 처리량 (/h) 이력
        int sceneSerial;
        bool marksSet;

        float Elapsed => clockStart < 0 ? 0f : Time.time - clockStart;

        // ------------------------------------------------------------------ UI 요소
        UIDocument doc;
        VisualElement root, topBar, leftPanel, rightPanel, leftBody, rightBody, loadingOverlay;
        Label nameLbl, timeLbl, toastLbl, loadingLbl;
        IconButton playBtn;
        TimelineBar timeline;
        readonly List<Button> speedBtns = new List<Button>();
        Button camCineBtn, camOrbitBtn, captionBtn, loopBtn, recBtn;
        readonly Dictionary<EquipEntry, VisualElement> cards = new Dictionary<EquipEntry, VisualElement>();
        // 오른쪽 패널
        Label catLbl, titleLbl, koLbl, descLbl, tpLbl, tpUnitLbl, recentLbl, chartMaxLbl, simTimeLbl, robotsLbl;
        Sparkline chart;
        VisualElement kpiGrid, paramBox, specBox, specBody;
        readonly List<(KpiDef def, Label val)> kpiLabels = new List<(KpiDef, Label)>();
        readonly List<(ParamDef def, TextField field, FieldInfo fi)> paramFields = new List<(ParamDef, TextField, FieldInfo)>();

        // ------------------------------------------------------------------ 시작
        void Awake()
        {
            Instance = this;
            Time.timeScale = 1f;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureEventSystem();
            BuildUi();
            DetectView();
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

        void OnSceneLoaded(Scene s, LoadSceneMode mode) => DetectView();

        // 현재 열린 장면에서 라이브러리에 있는 설비를 찾음 (Start 전에 불리므로 바꾼 속성을 여기서 적용)
        void DetectView()
        {
            foreach (var e in EquipCatalog.All)
            {
                var v = FindFirstObjectByType(e.viewType) as Component;
                if (v != null) { Attach(e, v); return; }
            }
            if (!loading) { entry = null; view = null; RefreshEntryUi(); }
        }

        void Attach(EquipEntry e, Component v)
        {
            bool changed = entry != e;
            entry = e; view = v; clockStart = -1f; marksSet = false;
            samples.Clear(); rates.Clear();
            SetField(v, "showHud", false);    // 장면별 예전 IMGUI HUD 는 끔 (같은 내용이 오른쪽 대시보드에 있음)
            ApplyOverrides(e, v);
            RefreshEntryUi(changed);
            ApplyTimeScale();
        }

        // ------------------------------------------------------------------ 매 프레임
        void Update()
        {
            // 녹화가 시작되면 (각 MovieCapture 가 captureFramerate 를 씀) HUD 를 숨기고 시간 제어를 멈춤
            if (!recording && (Time.captureFramerate > 0 || CaptionPresenter.Recording))
            {
                recording = true; ffTarget = -1f; pendingSeek = -1f;
                Time.timeScale = 1f;
                SetCaptions(true);
                root.style.display = DisplayStyle.None;
                HudInput.PointerOverUi = false;
            }
            if (recording) return;

            HandleKeys();

            if (view == null && entry != null && !loading) { entry = null; RefreshEntryUi(); }

            if (view != null && entry != null && clockStart < 0 && entry.ready(view))
            {
                clockStart = Time.time;
                if (pendingSeek > 0) ffTarget = pendingSeek;
                pendingSeek = -1f;
                RefreshTimelineMarks();
                ApplyTimeScale();
            }

            // 타임라인 앞으로 이동: 높은 배속으로 정확히 목표 시각까지 진행 (시뮬레이션·카메라·자막이 함께 감)
            if (ffTarget >= 0 && clockStart >= 0)
            {
                float rem = ffTarget - Elapsed;
                if (rem <= 0.002f) { ffTarget = -1f; ApplyTimeScale(); }
                else Time.timeScale = Mathf.Clamp(rem / Mathf.Max(Time.unscaledDeltaTime, 1f / 240f), 0.01f, FastForward);
            }
            else if (loop && !loading && clockStart >= 0 && Elapsed >= MovieLength)
                Restart(0f);   // 30초 영상 구간 반복: 장면을 다시 시작해 녹화와 같은 장면을 다시 보여 줌

            Sample();
            UpdateUiFast();
            if (Time.unscaledTime >= nextUiRefresh) { nextUiRefresh = Time.unscaledTime + 0.2f; UpdateUiSlow(); }
        }

        void Sample()
        {
            if (view == null || entry == null || clockStart < 0) return;
            float t = (float)entry.simTime(view), done = (float)entry.doneCount(view);
            if (samples.Count > 0 && t - samples[samples.Count - 1].x < 1f) return;
            samples.Add(new Vector2(t, done));
            // 최근 1분 처리량
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
            if (TypingInField()) return;
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
            if (ffTarget >= 0 || loading) { Time.timeScale = 1f; return; }
            Time.timeScale = paused ? 0f : speed;
        }

        void TogglePause() { paused = !paused; ApplyTimeScale(); UpdateControls(); }

        void SetSpeed(float s) { speed = s; if (paused) paused = false; ApplyTimeScale(); UpdateControls(); }

        public void Seek(float t)
        {
            if (entry == null) return;
            t = Mathf.Clamp(t, 0f, MovieLength - 0.05f);
            if (clockStart >= 0 && !loading && t >= Elapsed) { ffTarget = t; return; }
            Restart(t);   // 뒤로 가기: 처음부터 다시 만들어 그 시각까지 빠르게 진행 (시뮬레이션이 결정적이라 같은 장면)
        }

        public void Restart(float seekTo)
        {
            if (entry == null || loading) return;
            StartCoroutine(LoadRoutine(entry, seekTo));
        }

        public void Open(EquipEntry e)
        {
            if (e == null || loading) return;
            StartCoroutine(LoadRoutine(e, 0f));
        }

        IEnumerator LoadRoutine(EquipEntry e, float seekTo)
        {
            loading = true; ffTarget = -1f; pendingSeek = seekTo > 0 ? seekTo : -1f;
            clockStart = -1f; view = null;
            bool changed = entry != e;
            entry = e;
            if (changed) RefreshEntryUi(true);
            ApplyTimeScale();
            loadingLbl.text = $"{e.name} 불러오는 중…";
            loadingOverlay.style.display = DisplayStyle.Flex;
            yield return null;   // 안내 문구를 먼저 그림

            bool viaFile = false;
            if (Application.isEditor) viaFile = EditorLoadScene != null && EditorLoadScene(e.scenePath);
            else if (SceneUtility.GetBuildIndexByScenePath(e.scenePath) >= 0) { SceneManager.LoadSceneAsync(e.scenePath, LoadSceneMode.Single); viaFile = true; }

            if (viaFile)
            {
                float until = Time.unscaledTime + 15f;
                while (view == null && Time.unscaledTime < until) yield return null;
                if (view == null) viaFile = false;
            }
            if (!viaFile)
            {
                // 장면 파일이 없으면 각 설비 메뉴의 Create Scene 과 같은 구성을 코드로 만듦 (카메라 + 설비 루트)
                var s = SceneManager.CreateScene($"{e.rootName} (HUD {++sceneSerial})");
                SceneManager.SetActiveScene(s);
                for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (sc == s || !sc.isLoaded) continue;
                    var op = SceneManager.UnloadSceneAsync(sc);
                    while (op != null && !op.isDone) yield return null;
                }
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
                var rootGo = new GameObject(e.rootName);
                var v = rootGo.AddComponent(e.viewType);
                if (e.captureType != null) rootGo.AddComponent(e.captureType);
                Attach(e, v);
                if (Application.isEditor) Toast("장면 파일이 없어 코드로 만들었습니다 (메뉴 애니메이션 > 통합 HUD > 모든 설비 장면 만들기)");
            }
            Resources.UnloadUnusedAssets();
            loading = false;
            loadingOverlay.style.display = DisplayStyle.None;
            ApplyTimeScale();
            UpdateControls();
        }

        // ------------------------------------------------------------------ 카메라 · 자막
        void SetCinematic(bool on)
        {
            if (view == null) return;
            SetField(view, "cinematicCamera", on);
            UpdateControls();
        }

        void SetCaptions(bool on)
        {
            captionsOn = on;
            if (view != null) { SetField(view, "showCaption", on); SetField(view, "showCaptions", on); }
            foreach (var cp in FindObjectsByType<CaptionPresenter>(FindObjectsSortMode.None)) cp.show = on;
        }

        void SetHudVisible(bool on)
        {
            hudVisible = on;
            root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) { overTop = overLeft = overRight = false; HudInput.PointerOverUi = false; }
            else Toast("H: HUD 숨기기 · Space: 재생/일시정지 · R: 처음부터 · ←/→: 5초 이동 · C: 카메라");
        }

        // ------------------------------------------------------------------ 속성 변경
        void ApplyOverrides(EquipEntry e, Component v)
        {
            if (!overrides.TryGetValue(e.id, out var map)) return;
            var cfg = GetConfig(v);
            foreach (var kv in map)
            {
                var (target, fi) = ResolveField(v, cfg, kv.Key);
                if (fi == null) continue;
                try { fi.SetValue(target, Convert.ChangeType(kv.Value, fi.FieldType, CultureInfo.InvariantCulture)); }
                catch (Exception ex) { Debug.LogWarning($"[HUD] {kv.Key} 적용 실패: {ex.Message}"); }
            }
        }

        void ApplyParams()
        {
            if (entry == null) return;
            var map = new Dictionary<string, double>();
            foreach (var (def, field, fi) in paramFields)
            {
                if (fi == null) continue;
                if (!double.TryParse(field.value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                d = Math.Max(def.min, Math.Min(def.max, d));
                if (fi.FieldType == typeof(int)) d = Math.Round(d);
                map[def.field] = d;
            }
            overrides[entry.id] = map;
            Toast("속성을 적용했습니다. 처음부터 다시 시작합니다 (시네마틱 샷은 기본값 기준이라 어긋날 수 있습니다)");
            Restart(0f);
        }

        void ResetParams()
        {
            if (entry == null) return;
            overrides.Remove(entry.id);
            Toast("기본값으로 되돌려 다시 시작합니다");
            Restart(0f);
        }

        static object GetConfig(Component v) => v?.GetType().GetField("config", BindingFlags.Instance | BindingFlags.Public)?.GetValue(v);

        static (object, FieldInfo) ResolveField(Component v, object cfg, string name)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public;
            if (name.StartsWith("view.")) return (v, v.GetType().GetField(name.Substring(5), F));
            return cfg == null ? (null, null) : (cfg, cfg.GetType().GetField(name, F));
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

        // ------------------------------------------------------------------ UI 만들기
        void BuildUi()
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

            var docRoot = doc.rootVisualElement;
            docRoot.pickingMode = PickingMode.Ignore;
            var font = HudFont.Get();
            if (font.HasValue) docRoot.style.unityFontDefinition = font.Value;

            root = new VisualElement { pickingMode = PickingMode.Ignore };
            root.style.position = Position.Absolute; root.style.left = 0; root.style.right = 0; root.style.top = 0; root.style.bottom = 0;
            root.style.flexDirection = FlexDirection.Column;
            docRoot.Add(root);

            BuildTopBar();

            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.style.flexGrow = 1; body.style.flexDirection = FlexDirection.Row; body.style.justifyContent = Justify.SpaceBetween;
            HudStyle.Pad(body, 10, 12);
            root.Add(body);
            BuildLeftPanel(body);
            BuildRightPanel(body);

            // 안내 문구 · 불러오는 중
            toastLbl = HudStyle.MakeLabel("", 14, HudStyle.Text);
            toastLbl.style.position = Position.Absolute; toastLbl.style.bottom = 22; toastLbl.style.alignSelf = Align.Center;
            toastLbl.style.backgroundColor = HudStyle.Panel; HudStyle.Pad(toastLbl, 8, 14); HudStyle.Radius(toastLbl, 8);
            toastLbl.style.display = DisplayStyle.None;
            root.Add(toastLbl);

            loadingOverlay = new VisualElement { pickingMode = PickingMode.Ignore };
            loadingOverlay.style.position = Position.Absolute; loadingOverlay.style.left = 0; loadingOverlay.style.right = 0;
            loadingOverlay.style.top = 0; loadingOverlay.style.bottom = 0;
            loadingOverlay.style.alignItems = Align.Center; loadingOverlay.style.justifyContent = Justify.Center;
            loadingLbl = HudStyle.MakeLabel("", 22, Color.white, true);
            loadingLbl.style.backgroundColor = HudStyle.Panel; HudStyle.Pad(loadingLbl, 14, 22); HudStyle.Radius(loadingLbl, 10);
            loadingOverlay.Add(loadingLbl);
            loadingOverlay.style.display = DisplayStyle.None;
            docRoot.Add(loadingOverlay);

            Hover(topBar, b => overTop = b); Hover(leftPanel, b => overLeft = b); Hover(rightPanel, b => overRight = b);
            UpdateControls();
            Toast("H: HUD 숨기기 · Space: 재생/일시정지 · R: 처음부터 · ←/→: 5초 이동 · C: 카메라");
        }

        void Hover(VisualElement e, Action<bool> set)
        {
            e.RegisterCallback<PointerEnterEvent>(_ => { set(true); HudInput.PointerOverUi = overTop || overLeft || overRight; });
            e.RegisterCallback<PointerLeaveEvent>(_ => { set(false); HudInput.PointerOverUi = overTop || overLeft || overRight; });
        }

        static VisualElement Panel()
        {
            var p = new VisualElement();
            p.style.backgroundColor = HudStyle.Panel; HudStyle.Radius(p, 12); HudStyle.Border(p, 1, HudStyle.PanelLine);
            return p;
        }

        static VisualElement Row(float gap = 0)
        {
            var r = new VisualElement { pickingMode = PickingMode.Ignore };
            r.style.flexDirection = FlexDirection.Row; r.style.alignItems = Align.Center;
            return r;
        }

        static VisualElement Divider()
        {
            var d = new VisualElement { pickingMode = PickingMode.Ignore };
            d.style.width = 1; d.style.height = 30; d.style.backgroundColor = HudStyle.PanelLine; HudStyle.Margin(d, 0, 8, 0, 8);
            return d;
        }

        static Label SectionTitle(string t)
        {
            var l = HudStyle.MakeLabel(t, 12, HudStyle.Muted, true);
            l.style.letterSpacing = 1.5f; HudStyle.Margin(l, 14, 0, 8, 0);
            return l;
        }

        // ---------------------------------------------------------------- 위쪽 막대
        void BuildTopBar()
        {
            topBar = Panel();
            topBar.style.flexDirection = FlexDirection.Row; topBar.style.alignItems = Align.Center;
            topBar.style.height = 64; HudStyle.Margin(topBar, 12, 12, 0, 12); HudStyle.Pad(topBar, 0, 14);
            root.Add(topBar);

            var brand = new VisualElement { pickingMode = PickingMode.Ignore };
            brand.style.width = 250; brand.style.flexShrink = 0;
            var tag = HudStyle.MakeLabel("EQUIPMENT ANIMATION", 10, HudStyle.Accent, true); tag.style.letterSpacing = 2f;
            nameLbl = HudStyle.MakeLabel("설비를 고르세요", 18, Color.white, true);
            nameLbl.style.overflow = Overflow.Hidden; nameLbl.style.textOverflow = TextOverflow.Ellipsis;
            brand.Add(tag); brand.Add(nameLbl);
            topBar.Add(brand);
            topBar.Add(Divider());

            topBar.Add(new IconButton(IconButton.Kind.Restart, 34, () => Restart(0f)));
            topBar.Add(new IconButton(IconButton.Kind.Back, 34, () => Seek(CurrentTime() - 5f)));
            playBtn = new IconButton(IconButton.Kind.Pause, 44, TogglePause, true);
            topBar.Add(playBtn);
            topBar.Add(new IconButton(IconButton.Kind.Forward, 34, () => Seek(CurrentTime() + 5f)));
            topBar.Add(Divider());

            var speedLbl = HudStyle.MakeLabel("속도", 12, HudStyle.Muted); HudStyle.Margin(speedLbl, 0, 4, 0, 0);
            topBar.Add(speedLbl);
            foreach (var s in Speeds)
            {
                float sv = s;
                var b = HudStyle.MakeButton(s < 1 ? $"{s:0.##}×" : $"{s:0}×", () => SetSpeed(sv), 40);
                HudStyle.Margin(b, 0, 2, 0, 2); HudStyle.Pad(b, 0, 6);
                speedBtns.Add(b); topBar.Add(b);
            }
            topBar.Add(Divider());

            timeline = new TimelineBar { length = MovieLength };
            timeline.onSeek = Seek;
            topBar.Add(timeline);
            timeLbl = HudStyle.MakeLabel("0.0 / 30 s", 13, HudStyle.Text);
            timeLbl.style.width = 92; timeLbl.style.unityTextAlign = TextAnchor.MiddleRight; timeLbl.style.flexShrink = 0;
            topBar.Add(timeLbl);
            topBar.Add(Divider());

            camCineBtn = HudStyle.MakeButton("시네마틱", () => SetCinematic(true));
            camOrbitBtn = HudStyle.MakeButton("자유 시점", () => SetCinematic(false));
            HudStyle.Margin(camCineBtn, 0, 0, 0, 0); HudStyle.Margin(camOrbitBtn, 0, 0, 0, 2);
            topBar.Add(camCineBtn); topBar.Add(camOrbitBtn);
            topBar.Add(Divider());
            captionBtn = HudStyle.MakeButton("자막", () => { SetCaptions(!captionsOn); UpdateControls(); });
            loopBtn = HudStyle.MakeButton("30초 반복", () => { loop = !loop; UpdateControls(); });
            topBar.Add(captionBtn); topBar.Add(loopBtn);
            if (Application.isEditor)
            {
                recBtn = HudStyle.MakeButton("● 30초 녹화", () => { if (entry != null) EditorRecord?.Invoke(entry); });
                recBtn.style.color = HudStyle.Rec;
                topBar.Add(recBtn);
            }
            var hide = HudStyle.MakeButton("HUD 숨김 (H)", () => SetHudVisible(false));
            topBar.Add(hide);
        }

        // ---------------------------------------------------------------- 왼쪽: 라이브러리
        void BuildLeftPanel(VisualElement body)
        {
            leftPanel = Panel();
            leftPanel.style.width = 300; leftPanel.style.flexShrink = 0; HudStyle.Pad(leftPanel, 14, 14);
            body.Add(leftPanel);

            var head = Row();
            var t = HudStyle.MakeLabel("솔루션 라이브러리", 16, Color.white, true); t.style.flexGrow = 1;
            var count = HudStyle.MakeLabel($"{EquipCatalog.All.Count}종", 12, HudStyle.Muted);
            var fold = HudStyle.MakeButton("접기", null); fold.style.height = 24; HudStyle.Pad(fold, 0, 8); fold.style.fontSize = 11;
            head.Add(t); head.Add(count); head.Add(fold);
            leftPanel.Add(head);

            leftBody = new ScrollView(ScrollViewMode.Vertical);
            ((ScrollView)leftBody).horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            leftBody.style.flexGrow = 1; HudStyle.Margin(leftBody, 8, 0, 0, 0);
            leftPanel.Add(leftBody);
            fold.clicked += () => Fold(leftPanel, leftBody, fold, 300);

            string lastCat = null;
            foreach (var e in EquipCatalog.All)
            {
                string cat = e.category.Split('·')[0].Trim();
                if (cat != lastCat) { leftBody.Add(SectionTitle(cat)); lastCat = cat; }
                leftBody.Add(MakeCard(e));
            }
            var hint = HudStyle.MakeLabel("누르면 그 설비 장면으로 바뀝니다. 장면 파일이 없으면 코드로 만듭니다.", 11, HudStyle.Muted);
            hint.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(hint, 12, 0, 4, 0);
            leftBody.Add(hint);
        }

        static void Fold(VisualElement panel, VisualElement content, Button btn, float width)
        {
            bool open = content.style.display != DisplayStyle.None;
            content.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            panel.style.width = open ? StyleKeyword.Auto : (StyleLength)width;
            panel.style.alignSelf = open ? Align.FlexStart : Align.Stretch;
            btn.text = open ? "펼치기" : "접기";
        }

        VisualElement MakeCard(EquipEntry e)
        {
            var c = new VisualElement();
            c.style.backgroundColor = HudStyle.Tile; HudStyle.Radius(c, 8); HudStyle.Pad(c, 9, 12); HudStyle.Margin(c, 0, 0, 6, 0);
            HudStyle.Border(c, 1, Color.clear);
            var n = HudStyle.MakeLabel(e.name, 14, Color.white, true);
            var k = HudStyle.MakeLabel(e.korean, 12, HudStyle.Muted);
            k.style.whiteSpace = WhiteSpace.Normal;
            c.Add(n); c.Add(k);
            c.RegisterCallback<PointerEnterEvent>(_ => { if (entry != e) c.style.backgroundColor = HudStyle.TileHover; });
            c.RegisterCallback<PointerLeaveEvent>(_ => { if (entry != e) c.style.backgroundColor = HudStyle.Tile; });
            c.RegisterCallback<ClickEvent>(_ => Open(e));
            cards[e] = c;
            return c;
        }

        // ---------------------------------------------------------------- 오른쪽: 속성 · 대시보드
        void BuildRightPanel(VisualElement body)
        {
            rightPanel = Panel();
            rightPanel.style.width = 360; rightPanel.style.flexShrink = 0; HudStyle.Pad(rightPanel, 14, 14);
            body.Add(rightPanel);

            var head = Row();
            catLbl = HudStyle.MakeLabel("", 11, HudStyle.Accent, true); catLbl.style.letterSpacing = 1.5f; catLbl.style.flexGrow = 1;
            var fold = HudStyle.MakeButton("접기", null); fold.style.height = 24; HudStyle.Pad(fold, 0, 8); fold.style.fontSize = 11;
            head.Add(catLbl); head.Add(fold);
            rightPanel.Add(head);

            var sv = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            sv.style.flexGrow = 1;
            rightBody = sv;
            rightPanel.Add(sv);
            fold.clicked += () => Fold(rightPanel, rightBody, fold, 360);

            titleLbl = HudStyle.MakeLabel("", 22, Color.white, true); titleLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(titleLbl, 4, 0, 0, 0);
            koLbl = HudStyle.MakeLabel("", 13, HudStyle.Text); koLbl.style.whiteSpace = WhiteSpace.Normal;
            descLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted); descLbl.style.whiteSpace = WhiteSpace.Normal; HudStyle.Margin(descLbl, 6, 0, 0, 0);
            sv.Add(titleLbl); sv.Add(koLbl); sv.Add(descLbl);

            // 대시보드
            sv.Add(SectionTitle("대시보드"));
            var hero = new VisualElement(); hero.style.backgroundColor = HudStyle.Tile; HudStyle.Radius(hero, 10); HudStyle.Pad(hero, 12, 14);
            var heroTop = Row();
            heroTop.style.alignItems = Align.FlexEnd;
            tpLbl = HudStyle.MakeLabel("—", 34, Color.white, true);
            tpUnitLbl = HudStyle.MakeLabel("", 13, HudStyle.Muted); HudStyle.Margin(tpUnitLbl, 0, 0, 7, 6);
            heroTop.Add(tpLbl); heroTop.Add(tpUnitLbl);
            var heroCap = HudStyle.MakeLabel("처리량 (누적 평균)", 12, HudStyle.Muted);
            recentLbl = HudStyle.MakeLabel("최근 1분 —", 12, HudStyle.Accent); HudStyle.Margin(recentLbl, 2, 0, 8, 0);
            var chartWrap = new VisualElement { pickingMode = PickingMode.Ignore };
            chart = new Sparkline();
            chartMaxLbl = HudStyle.MakeLabel("", 10, HudStyle.Muted);
            chartMaxLbl.style.position = Position.Absolute; chartMaxLbl.style.left = 2; chartMaxLbl.style.top = -2;
            chartWrap.Add(chart); chartWrap.Add(chartMaxLbl);
            var chartCap = HudStyle.MakeLabel("최근 1분 처리량 추이 (시뮬레이션 시간 3분)", 10, HudStyle.Muted); HudStyle.Margin(chartCap, 4, 0, 0, 0);
            hero.Add(heroTop); hero.Add(heroCap); hero.Add(recentLbl); hero.Add(chartWrap); hero.Add(chartCap);
            sv.Add(hero);

            var info = Row(); HudStyle.Margin(info, 8, 0, 0, 0);
            simTimeLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted); simTimeLbl.style.flexGrow = 1;
            robotsLbl = HudStyle.MakeLabel("", 12, HudStyle.Muted);
            info.Add(simTimeLbl); info.Add(robotsLbl);
            sv.Add(info);

            kpiGrid = new VisualElement { pickingMode = PickingMode.Ignore };
            kpiGrid.style.flexDirection = FlexDirection.Row; kpiGrid.style.flexWrap = Wrap.Wrap; kpiGrid.style.justifyContent = Justify.SpaceBetween;
            HudStyle.Margin(kpiGrid, 8, 0, 0, 0);
            sv.Add(kpiGrid);

            // 속성
            sv.Add(SectionTitle("속성"));
            paramBox = new VisualElement();
            sv.Add(paramBox);

            var specHead = Row(); HudStyle.Margin(specHead, 12, 0, 4, 0);
            var specT = HudStyle.MakeLabel("모델 사양 (config)", 12, HudStyle.Text, true); specT.style.flexGrow = 1;
            var specFold = HudStyle.MakeButton("펼치기", null); specFold.style.height = 22; specFold.style.fontSize = 11; HudStyle.Pad(specFold, 0, 8);
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

        // 설비가 바뀌면 오른쪽 패널 내용을 다시 만듦
        void RefreshEntryUi(bool rebuild = true)
        {
            foreach (var kv in cards)
            {
                bool on = kv.Key == entry;
                kv.Value.style.backgroundColor = on ? HudStyle.AccentDim : HudStyle.Tile;
                HudStyle.Border(kv.Value, 1, on ? new Color(HudStyle.Accent.r, HudStyle.Accent.g, HudStyle.Accent.b, 0.6f) : Color.clear);
            }
            if (nameLbl == null) return;
            nameLbl.text = entry != null ? entry.name : "설비를 고르세요";
            catLbl.text = entry != null ? entry.category.ToUpperInvariant() : "";
            titleLbl.text = entry != null ? entry.name : "선택된 설비 없음";
            koLbl.text = entry != null ? entry.korean : "왼쪽 라이브러리에서 설비를 고르면 장면이 열립니다.";
            descLbl.text = entry != null ? entry.desc : "";
            tpUnitLbl.text = entry != null ? entry.throughputUnit : "";
            if (!rebuild) return;

            kpiGrid.Clear(); kpiLabels.Clear();
            if (entry != null)
                foreach (var k in entry.kpis)
                {
                    var tile = new VisualElement { pickingMode = PickingMode.Ignore };
                    tile.style.width = Length.Percent(48.5f); tile.style.backgroundColor = HudStyle.Tile;
                    HudStyle.Radius(tile, 8); HudStyle.Pad(tile, 8, 10); HudStyle.Margin(tile, 0, 0, 6, 0);
                    var row = Row(); row.style.alignItems = Align.FlexEnd;
                    var val = HudStyle.MakeLabel("—", 18, Color.white, true);
                    var unit = HudStyle.MakeLabel(k.unit, 11, HudStyle.Muted); HudStyle.Margin(unit, 0, 0, 3, 4);
                    row.Add(val); row.Add(unit);
                    var lbl = HudStyle.MakeLabel(k.label, 11, HudStyle.Muted);
                    tile.Add(row); tile.Add(lbl);
                    kpiGrid.Add(tile);
                    kpiLabels.Add((k, val));
                }
            RebuildParams();
            RebuildSpecs();
            RefreshTimelineMarks();
        }

        void RebuildParams()
        {
            paramBox.Clear(); paramFields.Clear();
            if (entry == null) return;
            var cfg = GetConfig(view);
            overrides.TryGetValue(entry.id, out var map);
            if (entry.pars.Count == 0)
            {
                var l = HudStyle.MakeLabel("이 장면은 고정된 동작 주기로 움직여 바꿀 속성이 없습니다.", 12, HudStyle.Muted);
                l.style.whiteSpace = WhiteSpace.Normal; paramBox.Add(l);
                return;
            }
            foreach (var p in entry.pars)
            {
                var (target, fi) = view != null ? ResolveField(view, cfg, p.field) : (null, null);
                string cur = map != null && map.TryGetValue(p.field, out var ov) ? ov.ToString(CultureInfo.InvariantCulture)
                           : fi != null && target != null ? Convert.ToString(fi.GetValue(target), CultureInfo.InvariantCulture) : "";
                var row = Row(); HudStyle.Margin(row, 0, 0, 6, 0);
                var lbl = HudStyle.MakeLabel(p.label, 12, HudStyle.Text); lbl.style.flexGrow = 1;
                var range = HudStyle.MakeLabel($"{p.min:0.##}–{p.max:0.##}", 10, HudStyle.Muted); HudStyle.Margin(range, 0, 8, 0, 0);
                var tf = new TextField { value = cur, isDelayed = true };
                tf.style.width = 84; tf.style.height = 26; tf.style.fontSize = 13; HudStyle.Margin(tf, 0, 0, 0, 0);
                var input = tf.Q(className: TextField.inputUssClassName);
                if (input != null)
                {
                    input.style.backgroundColor = new Color(0, 0, 0, 0.35f); input.style.color = Color.white;
                    HudStyle.Border(input, 1, HudStyle.PanelLine); HudStyle.Radius(input, 5);
                }
                row.Add(lbl); row.Add(range); row.Add(tf);
                paramBox.Add(row);
                paramFields.Add((p, tf, fi));
            }
            var btns = Row(); btns.style.justifyContent = Justify.FlexEnd; HudStyle.Margin(btns, 4, 0, 0, 0);
            var reset = HudStyle.MakeButton("기본값", ResetParams);
            var apply = HudStyle.MakeButton("적용 후 다시 시작", ApplyParams);
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
                var row = Row(); row.style.justifyContent = Justify.SpaceBetween; HudStyle.Margin(row, 0, 0, 2, 0);
                var a = HudStyle.MakeLabel(f.Name, 11, HudStyle.Muted);
                var b = HudStyle.MakeLabel(val, 11, HudStyle.Text);
                row.Add(a); row.Add(b);
                specBody.Add(row);
            }
        }

        void RefreshTimelineMarks()
        {
            if (timeline == null) return;
            var starts = new List<float>();
            if (view != null)
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
        void UpdateControls()
        {
            if (playBtn == null) return;
            playBtn.Icon = paused ? IconButton.Kind.Play : IconButton.Kind.Pause;
            for (int i = 0; i < Speeds.Length; i++) HudStyle.SetOn(speedBtns[i], Mathf.Approximately(Speeds[i], speed));
            bool cine = GetBool(view, "cinematicCamera", true);
            HudStyle.SetOn(camCineBtn, cine); HudStyle.SetOn(camOrbitBtn, !cine);
            HudStyle.SetOn(captionBtn, captionsOn); HudStyle.SetOn(loopBtn, loop);
            if (recBtn != null) recBtn.SetEnabled(entry != null && EditorRecord != null);
        }

        void UpdateUiFast()
        {
            timeline.SetValue(Mathf.Min(Elapsed, MovieLength));
            timeLbl.text = loading ? "불러오는 중"
                         : ffTarget >= 0 ? $"이동 → {ffTarget:0.0} s"
                         : Elapsed > MovieLength ? $"{Elapsed:0} s (반복 끔)"
                         : $"{Elapsed:0.0} / {MovieLength:0} s";
            if (toastLbl.style.display == DisplayStyle.Flex && Time.unscaledTime > toastUntil) toastLbl.style.display = DisplayStyle.None;
        }

        void UpdateUiSlow()
        {
            if (!hudVisible) return;
            if (!captionsOn) SetCaptions(false);
            if (!marksSet && clockStart >= 0) RefreshTimelineMarks();   // 설비가 Start 에서 자막을 새로 만들 수 있어 계속 맞춰 줌
            if (entry == null || view == null || !entry.ready(view))
            {
                tpLbl.text = "—"; recentLbl.text = "최근 1분 —"; simTimeLbl.text = ""; robotsLbl.text = "";
                chart.SetValues(Array.Empty<float>()); chartMaxLbl.text = "";
                foreach (var (_, val) in kpiLabels) val.text = "—";
                return;
            }
            tpLbl.text = entry.throughput(view).ToString("N0", CultureInfo.InvariantCulture);
            recentLbl.text = rates.Count > 0 ? $"최근 1분 {rates[rates.Count - 1].ToString("N0", CultureInfo.InvariantCulture)} {entry.throughputUnit}" : "최근 1분 — (집계 중)";
            chart.SetValues(rates);
            chartMaxLbl.text = rates.Count > 1 ? chart.Max.ToString("N0", CultureInfo.InvariantCulture) : "";
            simTimeLbl.text = $"시뮬레이션 t = {entry.simTime(view):0} s";
            robotsLbl.text = $"이동 장비 {entry.robotCount(view)}대";
            foreach (var (def, val) in kpiLabels) val.text = def.value(view) ?? "—";
            UpdateControls();
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

        // UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(familyName, styleName, pointSize)
        // Unity 버전마다 공개 여부가 달라 리플렉션으로 부름 (컴파일 오류 방지)
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
