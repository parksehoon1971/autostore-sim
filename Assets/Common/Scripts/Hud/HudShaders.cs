// 런타임에 코드로 만드는 머티리얼의 셰이더 찾기.
// 빌드에서는 어떤 에셋도 쓰지 않는 셰이더가 빠지므로 Shader.Find 가 null 을 돌려줌 (새 PC 빌드에서 배치 화면이 먹통이 된 원인).
// 에디터 스크립트(HudShaderAssets)가 Resources/HudShaders 에 머티리얼을 만들어 두어 빌드에 셰이더가 들어가게 하고,
// 그래도 없으면 쓸 수 있는 다른 셰이더로 대신함.
using UnityEngine;

namespace EquipFX
{
    public static class HudShaders
    {
        public const string LitName = "Universal Render Pipeline/Lit";
        public const string UnlitName = "Universal Render Pipeline/Unlit";
        public const string ComplexLitName = "Universal Render Pipeline/Complex Lit";
        public const string ResourceFolder = "HudShaders";

        static Shader lit, unlit;

        public static Shader Lit => lit != null ? lit : (lit = Find(LitName, "Lit", "Universal Render Pipeline/Simple Lit", "Standard", "Sprites/Default"));
        public static Shader Unlit => unlit != null ? unlit : (unlit = Find(UnlitName, "Unlit", LitName, "Unlit/Color", "Sprites/Default"));

        static Shader Find(string name, string resource, params string[] fallbacks)
        {
            var s = Shader.Find(name);
            if (s != null) return s;
            var m = Resources.Load<Material>(ResourceFolder + "/" + resource);
            if (m != null && m.shader != null) return m.shader;
            foreach (var f in fallbacks)
            {
                s = Shader.Find(f);
                if (s != null) { Debug.LogWarning($"[HUD] 셰이더 '{name}' 가 빌드에 없어 '{f}' 로 대신합니다."); return s; }
            }
            Debug.LogError($"[HUD] 셰이더 '{name}' 를 찾지 못했습니다.");
            return Shader.Find("Hidden/InternalErrorShader");
        }
    }
}
