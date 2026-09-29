Shader "Hidden/Voyage/WaterRippleImpulse"
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
            float4 _RippleImpulses[16];
            int _RippleImpulseCount;

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
                float height = tex2D(_MainTex, input.uv).r;
                [unroll]
                for (int i = 0; i < 16; i++)
                {
                    if (i >= _RippleImpulseCount) break;
                    float4 impulse = _RippleImpulses[i];
                    float2 delta = input.uv - impulse.xy;
                    float sigma = max(impulse.z * .48, .00001);
                    height += exp(-dot(delta, delta) / (2.0 * sigma * sigma)) * impulse.w;
                }
                return height;
            }
            ENDHLSL
        }
    }
}
