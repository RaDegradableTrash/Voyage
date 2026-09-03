Shader "Voyage/Terrain/Stylized"
{
    Properties
    {
        _BaseColor ("Terrain Base", Color) = (0.20, 0.28, 0.105, 1)
        _ShadowColor ("Terrain Shadow", Color) = (0.11, 0.15, 0.05, 1)
        _RidgeColor ("Terrain Ridge", Color) = (0.42, 0.32, 0.10, 1)
        _MacroScale ("Macro Scale", Float) = 0.009
        _MacroStrength ("Macro Strength", Range(0,1)) = 0.35
        _HeightTint ("Height Tint", Range(0,1)) = 0.18
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShadowColor;
                float4 _RidgeColor;
                float _MacroScale;
                float _MacroStrength;
                float _HeightTint;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                half slope = saturate(normalWS.y);
                float macro = frac(sin(dot(floor(input.positionWS.xz * max(_MacroScale, 0.001)), float2(12.9898, 78.233))) * 43758.5453);
                macro = lerp(1.0, lerp(0.78, 1.22, macro), _MacroStrength);
                half heightBand = saturate(input.positionWS.y * 0.012 * _HeightTint + 0.5h);
                half3 color = lerp(_ShadowColor.rgb, _BaseColor.rgb, slope);
                color = lerp(color, _RidgeColor.rgb, saturate(heightBand * slope - 0.35h));
                color *= macro;
                Light mainLight = GetMainLight();
                half lighting = 0.55h + 0.45h * saturate(dot(normalWS, mainLight.direction));
                color *= lerp(1.0h, lighting, 0.72h);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}
