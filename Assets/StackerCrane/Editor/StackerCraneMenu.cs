// 에디터 메뉴: 스태커크레인 씬 생성 / 열기 / 30초 영상 녹화
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StackerCrane.EditorTools
{
    public static class StackerCraneMenu
    {
        public const string ScenePath = "Assets/StackerCrane/Scenes/StackerCraneWarehouse.unity";

        [MenuItem("애니메이션/스태커크레인/Create or Reset Stacker Crane Scene", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
            var root = new GameObject("StackerCraneWarehouse");
            root.AddComponent<StackerCraneView>();
            root.AddComponent<StackerCraneMovieCapture>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) { list.Add(new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            Debug.Log("[StackerCrane] 씬 생성: " + ScenePath + "  ▶ Play 를 누르세요.");
        }

        [MenuItem("애니메이션/스태커크레인/Open Stacker Crane Scene", priority = 2)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) CreateScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("애니메이션/스태커크레인/Record 30s Movie (FHD 1920x1080, 2x 슈퍼샘플링)", priority = 20)]
        public static void Record() { StartRecording(1080); }

        [MenuItem("애니메이션/스태커크레인/Record 30s Movie (4K 3840x2160)", priority = 21)]
        public static void Record4K() { StartRecording(2160); }

        static void StartRecording(int height)
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("플레이 모드를 먼저 종료하세요."); return; }
            OpenScene();
            SessionState.SetBool(StackerCraneMovieCapture.SessionKey, true);
            SessionState.SetInt(StackerCraneMovieCapture.SessionHeightKey, height);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("애니메이션/스태커크레인/Open Recordings Folder", priority = 22)]
        public static void OpenRecordings()
        {
            var p = Path.GetFullPath("Recordings"); Directory.CreateDirectory(p); EditorUtility.RevealInFinder(p);
        }
    }
}
