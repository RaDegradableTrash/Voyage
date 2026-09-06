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
        [HideInInspector] _TerrainLodProgress ("LOD progress", Float) = 1
        [HideInInspector] _TerrainLodOutgoing ("Outgoing LOD", Float) = 0
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
                float _TerrainLodProgress;
                float _TerrainLodOutgoing;
            CBUFFER_END
            float4 _VoyageTerrainView;
            float4 _VoyageGrassEnvironmentColor;
            float _VoyageGrassEnvironmentLight;

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
                float threshold = frac(52.9829189 * frac(dot(floor(input.positionCS.xy), float2(0.06711056, 0.00583715))));
                // Complementary masks keep exactly one surface during LOD changes.
                clip(_TerrainLodOutgoing > 0.5 ? threshold - _TerrainLodProgress - 0.00001 : _TerrainLodProgress - threshold);
                if (_VoyageTerrainView.w > 0.0)
                {
                    float viewDistance = distance(input.positionWS.xz, _VoyageTerrainView.xy);
                    float coverage = 1.0 - smoothstep(_VoyageTerrainView.z, _VoyageTerrainView.w, viewDistance);
                    clip(coverage - threshold - 0.00001);
                }
                half3 normalWS = normalize(input.normalWS);
                half slope = saturate(normalWS.y);
                float macro = frac(sin(dot(floor(input.positionWS.xz * max(_MacroScale, 0.001)), float2(12.9898, 78.233))) * 43758.5453);
                macro = lerp(1.0, lerp(0.78, 1.22, macro), _MacroStrength);
                half heightBand = saturate(input.positionWS.y * 0.012 * _HeightTint + 0.5h);
                half3 color = lerp(_ShadowColor.rgb, _BaseColor.rgb, slope);
                color = lerp(color, _RidgeColor.rgb, saturate(heightBand * slope - 0.35h));
                color *= macro;
                // Use the exact environment tint and intensity used by the grass.
                color *= max(_VoyageGrassEnvironmentColor.rgb, half3(0.35h, 0.35h, 0.35h)) * max(_VoyageGrassEnvironmentLight, 0.35h);
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
