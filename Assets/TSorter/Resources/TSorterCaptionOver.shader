// 캡션 레이어(투명 배경 RenderTexture)를 영상 프레임 위에 합성 (TSorterCaption / TSorterMovieCapture 에서 사용)
// UI 가 투명 배경에 그려지면 색은 c*a, 알파는 a*a 가 되므로 알파를 sqrt 로 되돌려 Blend One OneMinusSrcAlpha 로 정확히 합성합니다.
Shader "Hidden/TSorter/CaptionOver"
{
    Properties { _MainTex ("Caption", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" }
        ZTest Always ZWrite Off Cull Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.a = sqrt(saturate(c.a));
                return c;
            }
            ENDCG
        }
    }
}
