// 에디터 메뉴: Rapyuta ASRS 씬 생성 / 열기 / 30초 영상 녹화
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Rapyuta.EditorTools
{
    public static class RapyutaMenu
    {
        public const string ScenePath = "Assets/Rapyuta/Scenes/RapyutaASRS.unity";

        [MenuItem("애니메이션/Rapyuta/Create or Reset ASRS Scene", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
            var root = new GameObject("RapyutaASRS");
            root.AddComponent<RapyutaView>();
            root.AddComponent<RapyutaMovieCapture>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) { list.Add(new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            Debug.Log("[Rapyuta] 씬 생성: " + ScenePath + "  ▶ Play 를 누르세요.");
        }

        [MenuItem("애니메이션/Rapyuta/Open ASRS Scene", priority = 2)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("애니메이션/Rapyuta/Record 30s Movie (MP4, 1920x1080)", priority = 20)]
        public static void Record()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            OpenScene();
            SessionState.SetBool(RapyutaMovieCapture.SessionKey, true);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("애니메이션/Rapyuta/Open Recordings Folder", priority = 21)]
        public static void OpenRecordings()
        {
            var p = Path.GetFullPath("Recordings"); Directory.CreateDirectory(p); EditorUtility.RevealInFinder(p);
        }
    }

    // FBX 가져오기 설정 (Blender 출력)
    public class RapyutaModelImporter : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.Contains("Rapyuta/Resources/RapyutaModels")) return;
            var mi = (ModelImporter)assetImporter;
            mi.importCameras = false; mi.importLights = false; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.bakeAxisConversion = true;
        }
    }
}
