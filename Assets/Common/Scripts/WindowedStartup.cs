using UnityEngine;

namespace EquipFX
{
    /// <summary>
    /// Standalone builds always start in a 1600x900 resizable window.
    /// Unity remembers the last screen mode per product in the registry, so a PC that
    /// once ran a fullscreen build would otherwise keep launching fullscreen even after
    /// Player Settings changed. Alt+Enter still toggles fullscreen during a session.
    /// </summary>
    static class WindowedStartup
    {
#if !UNITY_EDITOR && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX)
        const int Width = 1600, Height = 900;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
            var r = Screen.currentResolution;
            int w = Mathf.Min(Width, Mathf.Max(640, r.width - 80));
            int h = Mathf.Min(Height, Mathf.Max(360, r.height - 120));
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
        }
#endif
    }
}
