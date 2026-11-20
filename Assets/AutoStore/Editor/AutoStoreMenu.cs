// 에디터 메뉴: 씬 자동 생성 / 열기 / 30초 영상 녹화
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutoStore.EditorTools
{
    [InitializeOnLoad]
    public static class AutoStoreMenu
    {
        public const string ScenePath = "Assets/AutoStore/Scenes/AutoStoreSim.unity";

        static AutoStoreMenu()
        {
            // 프로젝트를 처음 열면 시뮬레이션 씬을 만들고 엽니다.
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (!File.Exists(ScenePath)) CreateScene();
                var active = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(active.path) && active.rootCount <= 2 && !active.isDirty)
                    EditorSceneManager.OpenScene(ScenePath);
            };
        }

        [MenuItem("애니메이션/AutoStore/Create or Reset Simulation Scene", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
            var root = new GameObject("AutoStore");
            root.AddComponent<AutoStoreView>();
            root.AddComponent<MovieCapture>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) { list.Insert(0, new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            Debug.Log("[AutoStore] 씬 생성: " + ScenePath + "  ▶ Play 를 누르세요.");
        }

        [MenuItem("애니메이션/AutoStore/Open Simulation Scene", priority = 2)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("애니메이션/AutoStore/Record 30s Movie (MP4, 1920x1080)", priority = 20)]
        public static void Record()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            OpenScene();
            SessionState.SetBool(MovieCapture.SessionKey, true);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("애니메이션/AutoStore/Open Recordings Folder", priority = 21)]
        public static void OpenRecordings()
        {
            var p = Path.GetFullPath("Recordings"); Directory.CreateDirectory(p); EditorUtility.RevealInFinder(p);
        }
    }

    // FBX 가져오기 설정 (Blender 출력)
    public class AutoStoreModelImporter : AssetPostprocessor
    {
        // 버전을 올리면 Unity 가 모델을 다시 가져옵니다 (Read/Write 켜기: 겹친 면 자동 보정이 메쉬를 읽어야 함)
        public override uint GetVersion() => 2;

        void OnPreprocessModel()
        {
            if (!assetPath.Contains("AutoStore/Resources/Models")) return;
            var mi = (ModelImporter)assetImporter;
            mi.importCameras = false; mi.importLights = false; mi.isReadable = true; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.bakeAxisConversion = true;
        }
    }
}
