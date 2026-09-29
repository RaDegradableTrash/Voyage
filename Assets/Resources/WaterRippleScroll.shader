Shader "Hidden/Voyage/WaterRippleScroll"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _ScrollOffset;

            struct Attributes { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float Frag(Varyings input) : SV_Target
            {
                float2 sourceUv = input.uv + _ScrollOffset.xy;
                if (any(sourceUv < 0.0) || any(sourceUv > 1.0)) return 0.0;
                return tex2D(_MainTex, sourceUv).r;
            }
            ENDHLSL
        }
    }
}
