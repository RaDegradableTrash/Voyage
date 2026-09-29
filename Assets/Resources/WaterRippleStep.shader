Shader "Hidden/Voyage/WaterRippleStep"
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

            sampler2D _CurrentTex;
            sampler2D _PreviousTex;
            float4 _CurrentTex_TexelSize;
            float _Damping;

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
                float2 texel = _CurrentTex_TexelSize.xy;
                float left = tex2D(_CurrentTex, input.uv - float2(texel.x, 0)).r;
                float right = tex2D(_CurrentTex, input.uv + float2(texel.x, 0)).r;
                float down = tex2D(_CurrentTex, input.uv - float2(0, texel.y)).r;
                float up = tex2D(_CurrentTex, input.uv + float2(0, texel.y)).r;
                float previous = tex2D(_PreviousTex, input.uv).r;
                return ((left + right + down + up) * .5 - previous) * _Damping;
            }
            ENDHLSL
        }
    }
}
