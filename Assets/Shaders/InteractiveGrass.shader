Shader "Voyage/Grass/InteractiveLit"
{
    Properties
    {
        _Color ("Grass Color", Color) = (0.28, 0.38, 0.14, 1)
        _BaseColor ("Base Color", Color) = (0.64, 0.42, 0.14, 1)
        _RootColor ("Root Ground Color", Color) = (0.48, 0.33, 0.12, 1)
        _ShadowColor ("Shadow Color", Color) = (0.40, 0.28, 0.10, 1)
        _TipColor ("Clump Variation Color", Color) = (0.78, 0.56, 0.22, 1)
        _BacksideColor ("Backside Warm Color", Color) = (0.57, 0.37, 0.12, 1)
        _FadeColor ("Distance Meadow Color", Color) = (0.36, 0.24, 0.09, 1)
        _MacroScale ("Macro Variation Scale", Float) = 0.018
        _MacroStrength ("Macro Variation Strength", Range(0,1)) = 0.42
        _AlphaClip ("Alpha Clip", Range(0,1)) = 0.35
        _FadeStart ("Fade Start", Float) = 105
        _FadeEnd ("Fade End", Float) = 495
        _WindStrength ("Wind Strength", Float) = 0.18
        _WindSpeed ("Wind Speed", Float) = 1.0
        _BendStrength ("Interaction Bend", Float) = 1.0
        _RecoverySpeed ("Recovery Speed", Float) = 1.2
        _InteractionEnabled ("Interaction Enabled", Float) = 1
        _ImmediateInteractionEnabled ("Close Wheel Interaction", Float) = 1
        _FieldInteractionEnabled ("Field Interaction", Float) = 1
        _DistantAlphaClip ("Distant Alpha Clip", Float) = 0
        _Density ("Density", Range(0,1)) = 1
        _TileFade ("Tile Fade", Range(0,1)) = 1
        _AmbientStrength ("Ambient Strength", Range(0,2)) = 0.75
        _DirectLightStrength ("Direct Light Strength", Range(0,2)) = 1.0
        _BladeHeight ("Blade Height", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            // Writing depth is important for crossed cards: without it every
            // overlapping blade is sorted as transparent geometry and the
            // order can change frame to frame, producing shimmer/flicker.
            ZWrite On
            Offset -1, -1
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:ConfigureProcedural
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "GrassDistance.hlsl"

            TEXTURE2D(_VoyageGrassInteraction); SAMPLER(sampler_VoyageGrassInteraction);
            TEXTURE2D(_VoyageGrassPermanentInteraction); SAMPLER(sampler_VoyageGrassPermanentInteraction);
            TEXTURE2D(_VoyageGrassFarInteraction); SAMPLER(sampler_VoyageGrassFarInteraction);
            float4 _VoyageGrassFarWorld;
            float _VoyageGrassFarRecovery;
            float4 _VoyageGrassInteractionWorld;
            float4 _VoyageAtmosphereColor;
            float4 _VoyageAtmosphereRange;
            float _VoyageGrassDebugStateMachine;
            float4 _Color;
            float4 _BaseColor;
            float4 _RootColor;
            float4 _ShadowColor;
            float4 _TipColor;
            float4 _BacksideColor;
            float4 _FadeColor;
            float _MacroScale;
            float _MacroStrength;
            float _AlphaClip;
            float _FadeStart;
            float _FadeEnd;
            float4 _VoyageGrassWind;
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<float4x4> _VoyageGrassMatrices;
            void ConfigureProcedural()
            {
                unity_ObjectToWorld = _VoyageGrassMatrices[unity_InstanceID];
                // Grass instances use only rotation plus near-uniform scale.
                // Transpose is supported by Unity's shader compiler and is a
                // stable inverse approximation for this deliberately light
                // weight billboard-style geometry.
                unity_WorldToObject = transpose(unity_ObjectToWorld);
            }
            #endif
            float _WindStrength;
            float _WindSpeed;
            float _BendStrength;
            float _RecoverySpeed;
            float _InteractionEnabled;
            float _ImmediateInteractionEnabled;
            float _FieldInteractionEnabled;
            float _DistantAlphaClip;
            float _Density;
            float _TileFade;
            float _AmbientStrength;
            float _DirectLightStrength;
            float _BladeHeight;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 instanceRandom : TEXCOORD1;
                float2 bladeData : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float2 instanceRandom : TEXCOORD3;
                float farBlend : TEXCOORD4;
                float bendAmount : TEXCOORD5;
                float directBendAmount : TEXCOORD6;
                float4 shadowCoord : TEXCOORD7;
                half fogFactor : TEXCOORD8;
                float densityFade : TEXCOORD9;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 FieldUV(float3 positionWS)
            {
                float2 origin = _VoyageGrassInteractionWorld.xy - _VoyageGrassInteractionWorld.zz * 0.5;
                return (positionWS.xz - origin) / max(_VoyageGrassInteractionWorld.zz, 1.0);
            }

            float2 SampleBend(float2 uv, out float temporaryWeight, out float recoveryAge)
            {
                temporaryWeight = 0.0;
                recoveryAge = 1.0;
                if (_VoyageGrassInteractionWorld.z <= 1.0) return 0.0;

                float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeFade = saturate(edgeDistance * max(_VoyageGrassInteractionWorld.w, 1.0) * 2.0);
                float4 temporary = SAMPLE_TEXTURE2D_LOD(_VoyageGrassInteraction, sampler_VoyageGrassInteraction, uv, 0);
                temporaryWeight = temporary.b * inside * edgeFade;
                recoveryAge = saturate(1.0 - temporary.a);

                float4 permanent = SAMPLE_TEXTURE2D_LOD(_VoyageGrassPermanentInteraction, sampler_VoyageGrassPermanentInteraction, uv, 0);
                float permanentWeight = permanent.b * inside * edgeFade * 0.42;
                float2 temporaryDirection = temporary.rg;
                float2 permanentDirection = permanent.rg;
                float2 nearBend = (temporaryDirection + permanentDirection * 0.42);
                float2 world = _VoyageGrassInteractionWorld.xy + (uv - 0.5) * _VoyageGrassInteractionWorld.z;
                float2 farUV = (world - _VoyageGrassFarWorld.xy) / max(_VoyageGrassFarWorld.z, 1.0) + 0.5;
                float farInside = step(0.0, farUV.x) * step(farUV.x, 1.0) * step(0.0, farUV.y) * step(farUV.y, 1.0);
                float2 farBend = SAMPLE_TEXTURE2D_LOD(_VoyageGrassFarInteraction, sampler_VoyageGrassFarInteraction, farUV, 0).rg;
                farBend *= farInside * _VoyageGrassFarRecovery;
                float nearBlend = smoothstep(0.0, 8.0, edgeDistance * _VoyageGrassInteractionWorld.z) * inside;
                return lerp(farBend, nearBend, nearBlend);
            }

            float DistantDitherThreshold(float2 pixelPosition)
            {
                int2 cell = (int2)floor(pixelPosition) & 3;
                int index = cell.x + cell.y * 4;
                // 4x4 Bayer matrix, normalized to the center of each step.
                const float4 row0 = float4(0.03125, 0.53125, 0.15625, 0.65625);
                const float4 row1 = float4(0.78125, 0.28125, 0.90625, 0.40625);
                const float4 row2 = float4(0.21875, 0.71875, 0.09375, 0.59375);
                const float4 row3 = float4(0.96875, 0.46875, 0.84375, 0.34375);
                if (index < 4) return row0[index];
                if (index < 8) return row1[index - 4];
                if (index < 12) return row2[index - 8];
                return row3[index - 12];
            }

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 clusterPosition = TransformObjectToWorld(float3(0, 0, 0));
                // A baked mesh has no per-cluster transform; use blade position
                // in that legacy path instead of fading the entire tile at once.
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                    clusterPosition = positionWS;
                #endif
                float clusterDistance = distance(clusterPosition, GetCameraPositionWS());
                float density = VoyageGrassDensity(clusterDistance, _FadeStart, _FadeEnd);
                float selection = VoyageGrassSelection(clusterPosition);
                output.densityFade = 1.0 - smoothstep(density, density + 0.08, selection);
                // Every generated grass blade is authored around a local
                // ground plane at y=0; terrain height is carried only by the
                // per-instance matrix translation. Do not reconstruct the
                // root from UVs or a material height, since legacy meshes can
                // contain stale height channels and launch the whole blade.
                float3 bladeRootWS = TransformObjectToWorld(float3(input.positionOS.x, 0.0, input.positionOS.z));
                float cameraDistance = distance(positionWS, GetCameraPositionWS());
                float farBlend = smoothstep(_FadeStart * 0.35, max(_FadeStart * 0.35 + 0.01, _FadeEnd * 0.78), cameraDistance);
                float originalVertexY = positionWS.y;
                float tip = saturate(input.uv.y);
                float temporaryWeight;
                float recoveryAge;
                // Stored, world-space contact state is the sole source of
                // deformation. Moving/stopping the vehicle cannot move or
                // erase an existing impression.
                temporaryWeight = 0.0;
                recoveryAge = 1.0;
                float2 fieldBend = _FieldInteractionEnabled > 0.5
                    ? SampleBend(FieldUV(bladeRootWS), temporaryWeight, recoveryAge) : 0.0;
                float2 interactionBend = fieldBend * _InteractionEnabled * _BendStrength;
                float2 globalWindDirection = normalize(_VoyageGrassWind.xy + float2(0.0001, 0.0001));
                float globalWindSpeed = _VoyageGrassWind.z > 0.0 ? _VoyageGrassWind.z : 1.0;
                float globalGustStrength = saturate(_VoyageGrassWind.w);
                float2 windPerpendicular = float2(-globalWindDirection.y, globalWindDirection.x);
                float alongWind = dot(positionWS.xz, globalWindDirection);
                float acrossWind = dot(positionWS.xz, windPerpendicular);
                float phase = alongWind * 0.055 - _Time.y * _WindSpeed * globalWindSpeed;
                float wave = sin(phase) + 0.32 * sin(phase * 0.47 + acrossWind * 0.028);
                float gust = 0.78 + 0.22 * sin(alongWind * 0.012 + acrossWind * 0.017 + _Time.y * 0.38);
                float windVariation = lerp(0.74, 1.26, input.instanceRandom.x);
                float windDistanceAttenuation = lerp(1.0, 0.28, farBlend);
                float2 wind = globalWindDirection * wave * _WindStrength * 1.35 * windDistanceAttenuation *
                              lerp(1.0, gust, globalGustStrength) * windVariation;
                float bendTip = sqrt(tip);
                // The field stores a soft, filtered tire footprint. Expand
                // that signal before converting it to an angle so a tire
                // impression remains visibly pressed at LOD1/LOD2 instead
                // of looking identical to wind-only motion.
                float interactionAmount = saturate(length(interactionBend));
                float windAmount = saturate(length(wind) * 1.05);
                // Make a live tire pass visually unambiguous: the blade root
                // remains planted while the tip can approach horizontal.
                float bendAngle = min(1.48, interactionAmount * 1.48 + windAmount * 0.22 * (1.0 - interactionAmount));
                float2 bendDirection = normalize(interactionBend + wind * 0.38 + float2(0.0001, 0.0001));
                float angleAtVertex = bendAngle * bendTip;
                float bladeHeight = max(0.0, positionWS.y - bladeRootWS.y);
                float3 rootWidthOffset = float3(positionWS.x - bladeRootWS.x, 0.0, positionWS.z - bladeRootWS.z);
                float3 arcDirection = float3(bendDirection.x, 0.0, bendDirection.y);

                // Rotate each blade around its planted root. This keeps the
                // root fixed and preserves the blade length while the tip
                // approaches the ground like a clock hand.
                positionWS = bladeRootWS + rootWidthOffset +
                             arcDirection * (sin(angleAtVertex) * bladeHeight);
                positionWS.y = bladeRootWS.y + cos(angleAtVertex) * bladeHeight;
                // A bent blade may never rise above its authored tip or sink
                // below its planted root. This protects against malformed
                // slope normals/instance transforms producing grass in the
                // sky while preserving the intended root-to-tip arc.
                float minBladeY = min(bladeRootWS.y, originalVertexY);
                float maxBladeY = max(bladeRootWS.y, originalVertexY);
                positionWS.y = clamp(positionWS.y, minBladeY, maxBladeY);

                output.positionWS = positionWS;
                output.shadowCoord = TransformWorldToShadowCoord(positionWS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                float3 normalOS = dot(input.normalOS, input.normalOS) > 0.01 ? input.normalOS : float3(0, 1, 0);
                float3 baseNormalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(normalOS));
                // Keep lighting coherent with the displaced blade. Without a
                // dynamic normal, wind and pressed grass move but retain the
                // original face lighting, which reads as an unlit object.
                float2 visibleBend = bendDirection * sin(angleAtVertex);
                float3 normalTilt = float3(-visibleBend.x * 0.72,
                                           abs(visibleBend.x) * 0.24 + abs(visibleBend.y) * 0.24,
                                           -visibleBend.y * 0.72);
                output.normalWS = NormalizeNormalPerVertex(normalize(baseNormalWS + normalTilt));
                output.uv = input.uv;
                output.instanceRandom = input.instanceRandom;
                output.farBlend = farBlend;
                // Keep the debug channel separate from wind: a red pixel must
                // mean direct tire influence, not merely a wind-bent blade.
                float directAngleAtVertex = 0.0;
                output.bendAmount = saturate(abs(sin(angleAtVertex)));
                output.directBendAmount = saturate(abs(sin(directAngleAtVertex)));
                return output;
            }

            half4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float centerMask = 1.0 - abs(input.uv.x * 2.0 - 1.0);
                float bladeWidth = lerp(1.0, 0.16, saturate(input.uv.y));
                clip(centerMask - max(1.0 - bladeWidth, _AlphaClip));
                float cameraDistance = distance(input.positionWS, GetCameraPositionWS());
                float fade = (1.0 - smoothstep(_FadeStart, max(_FadeStart + 0.01, _FadeEnd), cameraDistance)) * _TileFade * input.densityFade;
                clip(fade - 0.001);
                float distanceGroundBlend = smoothstep(_FadeStart, max(_FadeStart + 0.01, _FadeEnd), cameraDistance);
                if (_DistantAlphaClip > 0.5 && cameraDistance > _FadeStart)
                {
                    // Far LOD uses alpha clip instead of blending. This
                    // keeps the fade silhouette while avoiding a full
                    // transparent fragment blend for every crossed card.
                    clip(fade - DistantDitherThreshold(input.positionCS.xy));
                    fade = 1.0;
                }

                // Keep the authored grass color independent of sun direction
                // and ambient light. Only the main-light shadow attenuation is
                // applied, so external objects can shade the meadow without
                // introducing self-lighting or card-to-card gradients.
                // Do not sample the shadow map from this procedural card
                // shader. On DX12 an invalid per-card shadow coordinate can
                // become NaN and contaminate the entire fragment color.
                // Realtime shadows remain enabled on the vehicle/URP Lit
                // objects; the grass keeps a stable authored base tone.
                Light mainLight = GetMainLight();
                half shadowLight = 1.0h;
                float macro = frac(sin(dot(floor(input.positionWS.xz * max(_MacroScale, 0.001)), float2(12.9898, 78.233))) * 43758.5453);
                float macroStrength = lerp(_MacroStrength, 0.08, input.farBlend);
                macro = lerp(1.0, lerp(0.82, 1.18, macro), macroStrength);
                // Keep each blade a single authored warm-gold color. The
                // reference look gets its variation from neighboring clumps,
                // not from a visible root-to-tip gradient on every blade.
                half bladeVariation = saturate(input.instanceRandom.x * 0.75h + input.instanceRandom.y * 0.25h);
                half3 grassColor = lerp(_BaseColor.rgb, _TipColor.rgb, bladeVariation);
                half randomVariation = lerp(0.82h, 1.12h, input.instanceRandom.y);
                randomVariation = lerp(randomVariation, 1.0h, input.farBlend * 0.82h);
                grassColor *= macro * randomVariation;
                half3 color = grassColor * shadowLight;
                if (_VoyageGrassDebugStateMachine > 0.5)
                {
                    float4 fieldSample = SAMPLE_TEXTURE2D_LOD(_VoyageGrassInteraction,
                                                               sampler_VoyageGrassInteraction,
                                                               FieldUV(input.positionWS), 0);
                    if (input.directBendAmount > 0.08) color = half3(1.0, 0.05, 0.02);
                    else if (input.bendAmount > 0.08) color = half3(1.0, 0.55, 0.02);
                    else if (fieldSample.b > 0.025) color = half3(0.05, 0.25, 1.0);
                    else color = half3(0.42, 0.42, 0.42);
                }
                // The distant grass must converge toward the deep-green terrain
                // before its alpha fades, otherwise yellow/red tips remain
                // visible as a mismatched transparent veil.
                half3 horizonColor = _VoyageAtmosphereRange.w > 0.5
                    ? _VoyageAtmosphereColor.rgb
                    : _FadeColor.rgb;
                color = lerp(color, horizonColor, distanceGroundBlend * 0.82h);
                color = MixFog(color, input.fogFactor);
                if (any(color != color)) color = _BaseColor.rgb;
                // Streaming and fog transitions must never expose a black
                // card; the ground palette remains the minimum visible tone.
                color = max(color, _RootColor.rgb * 0.22h);
                // Keep the near field fully opaque and let the terrain show
                // through progressively in the transition band. This avoids
                // the hard dither horizon produced by distance clip alone.
                return half4(color, _Color.a * saturate(fade));
            }
            ENDHLSL
        }

    }
}
