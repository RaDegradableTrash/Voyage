Shader "Voyage/Grass/InteractiveLit"
{
    Properties
    {
        _Color ("Grass Color", Color) = (0.28, 0.38, 0.14, 1)
        _BaseColor ("Base Color", Color) = (0.34, 0.43, 0.08, 1)
        _RootColor ("Root Ground Color", Color) = (0.20, 0.28, 0.105, 1)
        _ShadowColor ("Shadow Color", Color) = (0.16, 0.24, 0.045, 1)
        _TipColor ("Tip Color", Color) = (0.58, 0.48, 0.10, 1)
        _BacksideColor ("Backside Warm Color", Color) = (0.43, 0.36, 0.07, 1)
        _FadeColor ("Distance Ground Color", Color) = (0.055, 0.16, 0.045, 1)
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
        _Density ("Density", Range(0,1)) = 1
        _AmbientStrength ("Ambient Strength", Range(0,2)) = 0.75
        _DirectLightStrength ("Direct Light Strength", Range(0,2)) = 1.0
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_VoyageGrassInteraction); SAMPLER(sampler_VoyageGrassInteraction);
            TEXTURE2D(_VoyageGrassPermanentInteraction); SAMPLER(sampler_VoyageGrassPermanentInteraction);
            float4 _VoyageGrassInteractionWorld;
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
            float _Density;
            float _AmbientStrength;
            float _DirectLightStrength;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 instanceRandom : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float2 instanceRandom : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                float farBlend : TEXCOORD5;
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
                float2 temporaryDirection = temporary.rg * 2.0 - 1.0;
                float2 permanentDirection = permanent.rg * 2.0 - 1.0;
                return temporaryDirection * temporaryWeight + permanentDirection * permanentWeight;
            }

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 instanceOriginWS = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 bladeRootWS = TransformObjectToWorld(float3(input.positionOS.x, 0.0, input.positionOS.z));
                float cameraDistance = distance(instanceOriginWS, GetCameraPositionWS());
                float farBlend = smoothstep(_FadeStart * 0.35, max(_FadeStart * 0.35 + 0.01, _FadeEnd * 0.78), cameraDistance);
                // Replace distant tiny cards with visually broader clumps.
                float farClusterScale = lerp(1.0, 2.25, farBlend);
                float3 localFromOrigin = positionWS - instanceOriginWS;
                positionWS = instanceOriginWS + float3(localFromOrigin.x * farClusterScale,
                                                        localFromOrigin.y * lerp(1.0, 1.18, farBlend),
                                                        localFromOrigin.z * farClusterScale);
                float3 rootFromOrigin = bladeRootWS - instanceOriginWS;
                bladeRootWS = instanceOriginWS + float3(rootFromOrigin.x * farClusterScale,
                                                         rootFromOrigin.y * lerp(1.0, 1.18, farBlend),
                                                         rootFromOrigin.z * farClusterScale);
                float tip = saturate(input.uv.y);
                float temporaryWeight;
                float recoveryAge;
                float2 interactionBend = SampleBend(FieldUV(positionWS), temporaryWeight, recoveryAge);

                // The interaction texture alpha is the recovery timer. Follow
                // it directly so pressed grass stands back up smoothly.
                float recoveryVariation = lerp(0.86, 1.14, input.instanceRandom.y);
                float recoveryStrength = pow(saturate(1.0 - recoveryAge), recoveryVariation);
                interactionBend *= recoveryStrength * _InteractionEnabled * _BendStrength * 1.8;

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
                float bendTip = tip * tip * (0.35 + 0.65 * tip);
                float interactionAmount = saturate(length(interactionBend) * 0.86);
                float windAmount = saturate(length(wind) * 1.05);
                float bendAngle = saturate(interactionAmount * 1.52 + windAmount * 0.28);
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

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
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
                output.shadowCoord = TransformWorldToShadowCoord(positionWS);
                output.farBlend = farBlend;
                return output;
            }

            half4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float centerMask = 1.0 - abs(input.uv.x * 2.0 - 1.0);
                float bladeWidth = lerp(1.0, 0.16, saturate(input.uv.y));
                clip(centerMask - max(1.0 - bladeWidth, _AlphaClip));
                float cameraDistance = distance(input.positionWS, GetCameraPositionWS());
                float fade = 1.0 - smoothstep(_FadeStart, max(_FadeStart + 0.01, _FadeEnd), cameraDistance);
                float distanceGroundBlend = smoothstep(_FadeStart, max(_FadeStart + 0.01, _FadeEnd), cameraDistance);

                // Grass is deliberately not part of the shadow-map pass. Use
                // direct light only here as well, so shadow-map precision does
                // not make dense cards flicker against one another.
                Light mainLight = GetMainLight();
                half3 normalWS = normalize(input.normalWS);
                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                half3 direct = mainLight.color * ndotl * _DirectLightStrength;
                float macro = frac(sin(dot(floor(input.positionWS.xz * max(_MacroScale, 0.001)), float2(12.9898, 78.233))) * 43758.5453);
                float macroStrength = lerp(_MacroStrength, 0.08, input.farBlend);
                macro = lerp(1.0, lerp(0.82, 1.18, macro), macroStrength);
                half heightBlend = saturate(input.uv.y * 1.35);
                half3 grassColor = lerp(_ShadowColor.rgb, _BaseColor.rgb, heightBlend);
                grassColor = lerp(grassColor, _TipColor.rgb, saturate((input.uv.y - 0.55) * 1.8));
                // Fade the lowest part of every blade into the terrain tone.
                // This hides the artificial card/ground seam without making
                // the whole blade dark.
                grassColor = lerp(_RootColor.rgb, grassColor, smoothstep(0.02, 0.34, input.uv.y));
                half randomVariation = lerp(0.82h, 1.12h, input.instanceRandom.y);
                randomVariation = lerp(randomVariation, 1.0h, input.farBlend * 0.82h);
                grassColor *= macro * randomVariation;
                half3 litColor = grassColor * (ambient + direct + 0.12h);
                half3 color = isFrontFace ? litColor : lerp(litColor, _BacksideColor.rgb, 0.38h);
                // The distant grass must converge toward the deep-green terrain
                // before its alpha fades, otherwise yellow/red tips remain
                // visible as a mismatched transparent veil.
                color = lerp(color, _FadeColor.rgb, distanceGroundBlend * 0.82h);
                // Keep the near field fully opaque and let the terrain show
                // through progressively in the transition band. This avoids
                // the hard dither horizon produced by distance clip alone.
                return half4(color, _Color.a * saturate(fade));
            }
            ENDHLSL
        }

    }
}
