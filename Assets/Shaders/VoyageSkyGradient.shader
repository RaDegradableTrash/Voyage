Shader "Voyage/Sky/Gradient"
{
    Properties
    {
        _SkyTint ("Sky Tint", Color) = (0.42, 0.52, 0.62, 1)
        _GroundColor ("Ground Color", Color) = (0.32, 0.34, 0.35, 1)
        _Exposure ("Exposure", Float) = 0.8
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _SkyTint;
            fixed4 _GroundColor;
            float _Exposure;

            struct Attributes { float4 vertex : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.vertex);
                output.direction = mul((float3x3)unity_ObjectToWorld, input.vertex.xyz);
                return output;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float height = normalize(input.direction).y;
                float horizon = saturate(1.0 - abs(height) * 2.0);
                float upper = saturate(height * 0.5 + 0.5);
                fixed3 sky = lerp(_GroundColor.rgb, _SkyTint.rgb, upper);
                sky = lerp(sky, (_SkyTint.rgb + _GroundColor.rgb) * 0.5, horizon * 0.2);
                return fixed4(sky * exp2(_Exposure), 1.0);
            }
            ENDHLSL
        }
    }
}
