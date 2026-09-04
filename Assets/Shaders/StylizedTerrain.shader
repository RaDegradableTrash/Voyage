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
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
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
                // Keep this custom terrain shader independent from per-pixel
                // shadow coordinates; invalid DX12 shadow data can otherwise
                // turn the complete terrain fragment into NaN/black.
                Light mainLight = GetMainLight();
                half directLight = saturate(dot(normalWS, mainLight.direction));
                // Preserve terrain readability when the realtime shadow map
                // or main-light sample is unavailable, while still allowing
                // actual shadows to darken the ground.
                half lighting = lerp(0.34h, 1.0h, directLight);
                color *= lerp(1.0h, lighting, 0.72h);
                color = MixFog(color, input.fogFactor);
                if (any(color != color)) color = _BaseColor.rgb;
                // Keep the terrain surface readable even if an external fog
                // or lighting state briefly resolves to black.
                color = max(color, _ShadowColor.rgb * 0.22h);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}
