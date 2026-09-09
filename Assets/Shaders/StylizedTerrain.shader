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
        [HideInInspector] _Horizon ("Persistent horizon", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShadowColor;
                float4 _RidgeColor;
                float _MacroScale;
                float _MacroStrength;
                float _HeightTint;
                float _TerrainLodProgress;
                float _TerrainLodOutgoing;
                float _Horizon;
            CBUFFER_END
        float4 _VoyageTerrainView;
        float _VoyageContourDepthPass;
        void ClipTerrainCoverage(float2 pixel, float3 world)
        {
            // Contours describe solid geometry, never the individual pixels of
            // a cross-fade. The color/depth passes retain complementary dithering.
            if(_VoyageContourDepthPass>.5)
            {
                clip(.5-_TerrainLodOutgoing);
                if(_VoyageTerrainView.w>0)
                {
                    float delta=distance(world.xz,_VoyageTerrainView.xy)-_VoyageTerrainView.w;
                    clip(_Horizon>.5 ? delta : -delta);
                }
                return;
            }
            float threshold=frac(52.9829189*frac(dot(floor(pixel),float2(.06711056,.00583715))));
            clip(_TerrainLodOutgoing>.5 ? threshold-_TerrainLodProgress-.00001 : _TerrainLodProgress-threshold);
            if(_VoyageTerrainView.w>0)
            {
                float coverage=1-smoothstep(_VoyageTerrainView.z,_VoyageTerrainView.w,distance(world.xz,_VoyageTerrainView.xy));
                // Complementary coverage: distant geometry must never poke
                // through detailed nearby terrain or create an overlapping seam.
                clip(_Horizon>.5 ? threshold-coverage : coverage-threshold-.00001);
            }
        }
        ENDHLSL
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"




            #include "Assets/Shaders/VoyageMeadowLighting.hlsl"

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
                ClipTerrainCoverage(input.positionCS.xy,input.positionWS);
                half3 normalWS = normalize(input.normalWS);
                half slope = saturate(normalWS.y);
                float macro = frac(sin(dot(floor(input.positionWS.xz * max(_MacroScale, 0.001)), float2(12.9898, 78.233))) * 43758.5453);
                macro = lerp(1.0, lerp(0.78, 1.22, macro), _MacroStrength);
                half heightBand = saturate(input.positionWS.y * 0.012 * _HeightTint + 0.5h);
                half3 color = VoyageGroundAlbedo(input.positionWS);
                color = VoyageSurfaceLight(color,input.positionWS,normalWS);
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct DepthVaryings { float4 positionCS:SV_POSITION; float3 world:TEXCOORD0; };
            DepthVaryings DepthVert(float3 positionOS:POSITION) { DepthVaryings o; o.world=TransformObjectToWorld(positionOS); o.positionCS=TransformWorldToHClip(o.world); return o; }
            half4 DepthFrag(DepthVaryings i):SV_Target { ClipTerrainCoverage(i.positionCS.xy,i.world); return 0; }
            ENDHLSL
        }
    }
}
