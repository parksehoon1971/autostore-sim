// 에디터 메뉴: HaiPick Climb 씬 생성 / 열기 / 30초 영상 녹화
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HaiPick.EditorTools
{
    public static class HaiPickMenu
    {
        public const string ScenePath = "Assets/HaiPick/Scenes/HaiPickClimb.unity";

        [MenuItem("애니메이션/HaiPick/Create or Reset Climb Scene", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
            var root = new GameObject("HaiPickClimb");
            root.AddComponent<HaiPickView>();
            root.AddComponent<HaiPickMovieCapture>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) { list.Add(new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            Debug.Log("[HaiPick] 씬 생성: " + ScenePath + "  ▶ Play 를 누르세요.");
        }

        [MenuItem("애니메이션/HaiPick/Open Climb Scene", priority = 2)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("애니메이션/HaiPick/Record 30s Movie (MP4, 1920x1080)", priority = 20)]
        public static void Record()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            OpenScene();
            SessionState.SetBool(HaiPickMovieCapture.SessionKey, true);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("애니메이션/HaiPick/Open Recordings Folder", priority = 21)]
        public static void OpenRecordings()
        {
            var p = Path.GetFullPath("Recordings"); Directory.CreateDirectory(p); EditorUtility.RevealInFinder(p);
        }
    }

    // FBX 가져오기 설정 (Blender 출력)
    public class HaiPickModelImporter : AssetPostprocessor
    {
        // 버전을 올리면 Unity 가 모델을 다시 가져옵니다 (Read/Write 켜기: 겹친 면 자동 보정이 메쉬를 읽어야 함)
        public override uint GetVersion() => 2;

        void OnPreprocessModel()
        {
            if (!assetPath.Contains("HaiPick/Resources/HaiPickModels")) return;
            var mi = (ModelImporter)assetImporter;
            mi.importCameras = false; mi.importLights = false; mi.isReadable = true; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.bakeAxisConversion = true;
        }
    }
}
