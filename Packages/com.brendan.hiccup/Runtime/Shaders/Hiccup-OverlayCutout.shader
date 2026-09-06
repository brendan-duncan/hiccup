// Overlay-mode cutout for world-space panels. When the DOM overlay sits behind a transparent canvas, the mesh
// writes color and alpha 0 (and depth) so the page shows through exactly where the panel is, and anything
// nearer in the scene covers it. Plain CG so it renders under Built-in, URP and HDRP (untagged pass).
Shader "Hiccup/Overlay Cutout"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        LOD 100

        Cull [_Cull]
        ZWrite On
        Blend Off
        ColorMask RGBA

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 vertex : SV_POSITION; };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
