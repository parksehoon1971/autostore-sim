// 에디터 메뉴: Libiao 3D 소터 씬 생성 / 열기 / 30초 영상 녹화
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Libiao.EditorTools
{
    public static class LibiaoMenu
    {
        public const string ScenePath = "Assets/Libiao/Scenes/LibiaoSorter.unity";

        [MenuItem("애니메이션/Libiao/Create or Reset Sorter Scene", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
            var root = new GameObject("LibiaoSorter");
            root.AddComponent<LibiaoView>();
            root.AddComponent<LibiaoMovieCapture>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) { list.Add(new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            Debug.Log("[Libiao] 씬 생성: " + ScenePath + "  ▶ Play 를 누르세요.");
        }

        [MenuItem("애니메이션/Libiao/Open Sorter Scene", priority = 2)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("애니메이션/Libiao/Record 30s Movie (MP4, 1920x1080)", priority = 20)]
        public static void Record()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            OpenScene();
            SessionState.SetBool(LibiaoMovieCapture.SessionKey, true);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("애니메이션/Libiao/Open Recordings Folder", priority = 21)]
        public static void OpenRecordings()
        {
            var p = Path.GetFullPath("Recordings"); Directory.CreateDirectory(p); EditorUtility.RevealInFinder(p);
        }
    }

    // FBX 가져오기 설정 (Blender 출력)
    public class LibiaoModelImporter : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.Contains("Libiao/Resources/LibiaoModels")) return;
            var mi = (ModelImporter)assetImporter;
            mi.importCameras = false; mi.importLights = false; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.bakeAxisConversion = true;
        }
    }
}
