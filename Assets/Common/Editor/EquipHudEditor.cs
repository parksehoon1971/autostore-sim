// 통합 HUD 의 에디터 연결: 플레이 모드 중 장면 파일 불러오기, HUD 의 녹화 단추 → 해당 설비 Record 30s Movie 메뉴,
// 메뉴 애니메이션 > 통합 HUD (HUD 켜기/끄기, 없는 설비 장면 한꺼번에 만들기).
using System;
using System.IO;
using System.Reflection;
using EquipFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EquipFXEditor
{
    [InitializeOnLoad]
    static class EquipHudEditor
    {
        const string Root = "애니메이션/통합 HUD/";
        const string ToggleItem = Root + "플레이 모드에서 HUD 표시";
        const string PendingRecordKey = "EquipFX.Hud.PendingRecord";

        static EquipHudEditor()
        {
            // 빌드 설정에 없어도 플레이 모드에서 장면 파일을 바로 엶
            EquipHud.EditorLoadScene = path =>
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
                return true;
            };
            // 녹화는 에디터 메뉴가 플레이 모드를 새로 시작해야 하므로: 플레이 모드 종료 → 편집 모드로 돌아오면 Record() 호출
            EquipHud.EditorRecord = e =>
            {
                SessionState.SetString(PendingRecordKey, e.menuType);
                EditorApplication.isPlaying = false;
            };
            // HUD 의 닫기 단추: 실행 파일에서는 Application.Quit(), 에디터에서는 플레이 모드 종료
            EquipHud.EditorQuit = () => EditorApplication.isPlaying = false;
            // 각 설비 Record 메뉴는 SessionState 에 '플레이 시 녹화' 표시를 남김 → 그때는 배치 공간으로 바꾸지 않음
            EquipHud.EditorRecordPending = () =>
            {
                foreach (var e in EquipCatalog.All)
                {
                    var f = e.captureType?.GetField("SessionKey", BindingFlags.Public | BindingFlags.Static);
                    if (f != null && f.IsLiteral && f.GetRawConstantValue() is string key && SessionState.GetBool(key, false)) return true;
                }
                return false;
            };
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredEditMode) return;
            var menu = SessionState.GetString(PendingRecordKey, "");
            if (menu == "") return;
            SessionState.EraseString(PendingRecordKey);
            EditorApplication.delayCall += () => InvokeMenu(menu, "Record");
        }

        static bool InvokeMenu(string typeName, string method)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName);
                var m = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) continue;
                m.Invoke(null, null);
                return true;
            }
            Debug.LogWarning($"[HUD] 메뉴 {typeName}.{method}() 를 찾지 못했습니다.");
            return false;
        }

        [MenuItem(ToggleItem, priority = 900)]
        static void Toggle()
        {
            bool on = PlayerPrefs.GetInt(EquipHud.PrefEnabled, 1) != 0;
            PlayerPrefs.SetInt(EquipHud.PrefEnabled, on ? 0 : 1);
            PlayerPrefs.Save();
            Debug.Log($"[HUD] 플레이 모드 통합 HUD {(on ? "끔" : "켬")} (다음 Play 부터 적용)");
        }

        [MenuItem(ToggleItem, true)]
        static bool ToggleCheck()
        {
            Menu.SetChecked(ToggleItem, PlayerPrefs.GetInt(EquipHud.PrefEnabled, 1) != 0);
            return true;
        }

        // 배치 게임 메인 공간 장면 (실행 파일의 첫 장면)
        [MenuItem("애니메이션/배치 게임/배치 공간 열기 (없으면 만들기)", priority = 0)]
        static void OpenLayoutScene()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var path = LayoutWorld.ScenePath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
                new GameObject("LayoutWorld").AddComponent<LayoutWorld>();
                EditorSceneManager.SaveScene(scene, path);
                Debug.Log("[배치 게임] 장면 생성: " + path + "  ▶ Play 를 누르세요.");
            }
            else EditorSceneManager.OpenScene(path);
            // 실행 파일을 만들면 배치 공간이 첫 장면이 되도록 빌드 목록 맨 앞에 둠
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        [MenuItem("애니메이션/배치 게임/저장된 배치 지우기", priority = 1)]
        static void ClearLayoutSave()
        {
            PlayerPrefs.DeleteKey(LayoutWorld.SaveKey); PlayerPrefs.Save();
            Debug.Log("[배치 게임] 저장된 배치를 지웠습니다.");
        }

        [MenuItem(Root + "모든 설비 장면 만들기 (없는 것만)", priority = 901)]
        static void CreateMissing()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var start = SceneManager.GetActiveScene().path;
            int made = 0;
            foreach (var e in EquipCatalog.All)
            {
                if (File.Exists(e.scenePath)) continue;
                if (InvokeMenu(e.menuType, "CreateScene")) made++;
            }
            if (!string.IsNullOrEmpty(start) && File.Exists(start)) EditorSceneManager.OpenScene(start);
            Debug.Log(made > 0 ? $"[HUD] 설비 장면 {made}개를 만들었습니다." : "[HUD] 모든 설비 장면이 이미 있습니다.");
        }
    }
}
