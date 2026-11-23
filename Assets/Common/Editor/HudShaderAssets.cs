// 배치 화면이 코드로 만드는 머티리얼의 셰이더(URP Lit · Unlit · Complex Lit)를 빌드에 넣기 위한 머티리얼.
// Shader.Find 로만 찾는 셰이더는 빌드에서 빠지므로, Resources/HudShaders 에 그 셰이더를 쓰는 머티리얼을 만들어 둠.
// 에디터를 열 때와 빌드 직전에 없으면 만듦. 자주 쓰는 키워드 조합도 같이 넣어 둠.
using EquipFX;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace EquipFXEditor
{
    [InitializeOnLoad]
    class HudShaderAssets : IPreprocessBuildWithReport
    {
        const string Folder = "Assets/Common/Resources/" + HudShaders.ResourceFolder;

        static HudShaderAssets() { EditorApplication.delayCall += () => Ensure(false); }

        public int callbackOrder => -100;
        public void OnPreprocessBuild(BuildReport report) => Ensure(true);

        [MenuItem("애니메이션/통합 HUD/빌드용 셰이더 머티리얼 다시 만들기")]
        static void Rebuild() => Ensure(true);

        static void Ensure(bool log)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            bool made = false;
            made |= Make("Lit", HudShaders.LitName, null);
            made |= Make("Lit_Emission", HudShaders.LitName, m => { m.SetColor("_EmissionColor", Color.white * 0.2f); m.EnableKeyword("_EMISSION"); });
            made |= Make("Lit_Normal", HudShaders.LitName, m => m.EnableKeyword("_NORMALMAP"));
            made |= Make("Lit_SpecGloss", HudShaders.LitName, m => { m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_METALLICSPECGLOSSMAP"); });
            made |= Make("Lit_AlphaClip", HudShaders.LitName, m => { m.SetFloat("_AlphaClip", 1f); m.EnableKeyword("_ALPHATEST_ON"); m.SetColor("_EmissionColor", Color.white * 0.2f); m.EnableKeyword("_EMISSION"); });
            made |= Make("Lit_Transparent", HudShaders.LitName, Transparent);
            made |= Make("Unlit", HudShaders.UnlitName, null);
            made |= Make("Unlit_Transparent", HudShaders.UnlitName, Transparent);
            made |= Make("ComplexLit", HudShaders.ComplexLitName, m => m.EnableKeyword("_CLEARCOAT"));
            if (made) AssetDatabase.SaveAssets();
            if (log || made) Debug.Log($"[HUD] 빌드용 셰이더 머티리얼 확인: {Folder}");
        }

        static void Transparent(Material m)
        {
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        static bool Make(string file, string shaderName, System.Action<Material> setup)
        {
            var path = $"{Folder}/{file}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null && existing.shader != null && existing.shader.name == shaderName) return false;
            var sh = Shader.Find(shaderName);
            if (sh == null) { Debug.LogWarning($"[HUD] 셰이더 '{shaderName}' 를 찾지 못해 {file}.mat 을 만들지 않았습니다."); return false; }
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Common/Resources", HudShaders.ResourceFolder);
            var m = new Material(sh) { name = file };
            setup?.Invoke(m);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);
            return true;
        }
    }
}
