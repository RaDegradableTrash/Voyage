// GrassFlow packed GPU instances, with Voyage's stylized tuft geometry and shading.
Shader "GrassFlow/Voyage/Grass"
{
    Properties
    {
        _RootColor ("Root", Color) = (.19,.25,.10,1)
        _MidColor ("Leaf", Color) = (.46,.57,.22,1)
        _TipColor ("Sunlit tip", Color) = (.83,.78,.40,1)
        _WindGustStrength ("Wind", Float) = .3
        [HideInInspector] _MeadowLeafData ("Linear meadow leaf", Vector) = (.48,.43,.32,1)
        [HideInInspector] _MeadowTipData ("Linear meadow tip", Vector) = (.62,.57,.44,1)
        [HideInInspector] _MeadowStyle ("Meadow style", Float) = 1
        [HideInInspector] _Surface ("Surface", 2D) = "white" {}
        [HideInInspector] _PaintDensity ("Density", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
        float4 _RootColor, _MidColor, _TipColor;
        float4 _MeadowLeafData, _MeadowTipData;
        float _WindGustStrength;
        float _MeadowStyle;
        float4 _PatchMin, _PatchSize;
        CBUFFER_END
        TEXTURE2D(_Surface); SAMPLER(sampler_Surface);
        TEXTURE2D(_PaintDensity); SAMPLER(sampler_PaintDensity);
         #include "Assets/Shaders/VoyageMeadowLighting.hlsl"

        TEXTURE2D(_VoyageGrassInteraction); SAMPLER(sampler_VoyageGrassInteraction);
        TEXTURE2D(_VoyageGrassPermanentInteraction); SAMPLER(sampler_VoyageGrassPermanentInteraction);
        float4 _VoyageGrassInteractionWorld;
        struct PackedGrass { float3 position; uint facing; uint heightWidth; uint surfaceNormal; };
        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
        StructuredBuffer<PackedGrass> _GrassDataBuffer;
        #endif
        void setup() {}
        struct Attributes
        {
            float3 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            float2 rootOS : TEXCOORD1;
            float4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 gradient : TEXCOORD0;
            float3 tint : TEXCOORD1;
            float fog : TEXCOORD2; float3 positionWS : TEXCOORD3;
            float3 groundNormal : TEXCOORD4;
            float3 rootWS : TEXCOORD5;
        };
        Varyings vert(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            float3 root = 0;
            float2 facing = float2(0,1);
            float height = 1, width = 1, phase = 0;
            float3 groundNormal = float3(0,1,0);
            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            PackedGrass grass = _GrassDataBuffer[unity_InstanceID];
            root = grass.position;
            facing = float2(f16tof32(grass.facing), f16tof32(grass.facing >> 16));
            height = f16tof32(grass.heightWidth);
            width = min(f16tof32(grass.heightWidth >> 16), 1.6);
            float2 normalXZ = float2(f16tof32(grass.surfaceNormal),f16tof32(grass.surfaceNormal>>16));
            groundNormal = float3(normalXZ.x,sqrt(saturate(1-dot(normalXZ,normalXZ))),normalXZ.y);
            phase = dot(root.xz,float2(.17,.13));
            #endif
            float3 local = input.positionOS * float3(width, height, width);
            float3 position = root + float3(local.x*facing.y + local.z*facing.x, local.y, -local.x*facing.x + local.z*facing.y);
            float tip = input.uv.y;
            float valid = 1;
            if (_MeadowStyle > .5)
            {
                float2 leafRoot = input.rootOS * width;
                root.xz += float2(leafRoot.x*facing.y+leafRoot.y*facing.x,-leafRoot.x*facing.x+leafRoot.y*facing.y);
                float2 terrainUV = (root.xz-_PatchMin.xz)/_PatchSize.xz;
                float4 surface = float4((root.y-_PatchMin.y)/_PatchSize.y,1,0,1);
                if (distance(root,_WorldSpaceCameraPos)<32) surface = SAMPLE_TEXTURE2D_LOD(_Surface,sampler_Surface,terrainUV,0);
                float density = 1; if(distance(root,_WorldSpaceCameraPos)<32) density = SAMPLE_TEXTURE2D_LOD(_PaintDensity,sampler_PaintDensity,terrainUV,0).r;
                valid = all(terrainUV>=0) && all(terrainUV<=1) && surface.g>=.99 && density>.001;
                root.y = _PatchMin.y + surface.r*_PatchSize.y;
                position.y = root.y + local.y;
            }
            // Broad travelling waves plus small leaf variation; roots stay planted.
            float wave = sin(dot(root.xz,float2(.19,.10)) - _Time.y*1.65);
            wave += sin(dot(root.xz,float2(.043,-.066)) - _Time.y*.73)*.4;
            float flutter = sin(_Time.y*3.5 + phase + input.color.g*4) * .16;
            float windFade = 1-smoothstep(75,130,distance(root,_WorldSpaceCameraPos));
            position.xz += float2(.9,.4) * (wave+flutter) * _WindGustStrength * tip*tip * height * windFade;
            if (_VoyageGrassInteractionWorld.z > 1)
            {
                float2 uv = (root.xz-_VoyageGrassInteractionWorld.xy)/_VoyageGrassInteractionWorld.z+.5;
                if (all(uv>=0) && all(uv<=1))
                {
                    float3 contact = SAMPLE_TEXTURE2D_LOD(_VoyageGrassInteraction,sampler_VoyageGrassInteraction,uv,0).rgb;
                    contact += SAMPLE_TEXTURE2D_LOD(_VoyageGrassPermanentInteraction,sampler_VoyageGrassPermanentInteraction,uv,0).rgb*.42;
                    float2 bend = contact.rg;
                    float pressure = saturate(max(length(bend),contact.b));
                    position.xz += bend*local.y*.8;
                    position.y = lerp(position.y,root.y+tip*.06,pressure*tip);
                }
            }
            Varyings o;
            o.positionCS = TransformWorldToHClip(lerp(root,position,valid));
            o.gradient = float2(tip,input.color.r); o.positionWS = position;
            o.groundNormal = groundNormal; o.rootWS = root;
            // A shared patch tint avoids salt-and-pepper noise between individual leaves.
            float patch = sin(root.x*.047 + sin(root.z*.031)*2)*.5+.5;
            o.tint = lerp(float3(.83,1.02,.90),float3(1.10,.98,.80),patch);
            o.tint *= lerp(.90,1.08,input.color.g);
            if (_MeadowStyle > .5)
            {
                o.tint = lerp(float3(.95,.94,.87),float3(1.04,1.02,.95),patch);
                o.tint *= lerp(.96,1.04,input.color.g) * (1+wave*.055);
            }
            o.fog = ComputeFogFactor(o.positionCS.z);
            return o;
        }
        half4 frag(Varyings input) : SV_Target
        {
            float t = input.gradient.x;
            float3 rootColor = _MeadowStyle>.5 ? VoyageGroundAlbedo(input.rootWS) : _RootColor.rgb;
            float blend = smoothstep(.16,.85,t);
            float3 leaf = lerp(rootColor,_MeadowStyle>.5 ? _MeadowLeafData.rgb : _MidColor.rgb,blend);
            leaf = lerp(leaf,_MeadowStyle>.5 ? _MeadowTipData.rgb : _TipColor.rgb,smoothstep(.65,1,t)*.55);
            // Two planar faces form a visible folded blade, with no shiny plastic highlight.
            leaf *= lerp(float3(1,1,1),lerp(.85,1.08,input.gradient.y)*input.tint,blend);
            float3 normal = normalize(input.groundNormal);
            // The lower blade uses the same position and normal as the ground,
            // including shadows. No separate root AO, tint or up-facing lighting.
            leaf = VoyageSurfaceLight(leaf,lerp(input.rootWS,input.positionWS,blend),normal);
            return half4(MixFog(leaf,input.fog),1);
        }
        half4 depthFrag(Varyings input) : SV_Target { return 0; }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment depthFrag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            ENDHLSL
        }
    }
}
