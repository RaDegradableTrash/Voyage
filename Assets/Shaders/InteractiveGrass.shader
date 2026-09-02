Shader "Voyage/Grass/InteractiveLit"
{
    Properties
    {
        _Color ("Grass Color", Color) = (0.28, 0.38, 0.14, 1)
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
        Tags { "RenderType"="Opaque" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_VoyageGrassInteraction); SAMPLER(sampler_VoyageGrassInteraction);
            TEXTURE2D(_VoyageGrassPermanentInteraction); SAMPLER(sampler_VoyageGrassPermanentInteraction);
            float4 _VoyageGrassInteractionWorld;
            float4 _Color;
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
                float tip = saturate(input.uv.y);
                float temporaryWeight;
                float recoveryAge;
                float2 interactionBend = SampleBend(FieldUV(positionWS), temporaryWeight, recoveryAge);

                float angularFrequency = 10.0;
                float damping = max(_RecoverySpeed, 0.01) * 1.35;
                float spring = exp(-damping * recoveryAge) *
                               (cos(angularFrequency * recoveryAge) +
                                damping / angularFrequency * sin(angularFrequency * recoveryAge));
                interactionBend *= spring * _InteractionEnabled * _BendStrength;

                float seed = input.instanceRandom.x * 19.37 + input.instanceRandom.y * 7.11;
                float phase = _Time.y * _WindSpeed + seed;
                float2 broadWave = float2(
                    sin(positionWS.x * 0.045 + phase) + 0.5 * sin(positionWS.z * 0.091 - phase * 0.73),
                    cos(positionWS.z * 0.052 + phase * 0.87) + 0.5 * cos(positionWS.x * 0.083 - phase * 1.21));
                float gust = 0.72 + 0.28 * sin(positionWS.x * 0.012 + positionWS.z * 0.017 + _Time.y * 0.38);
                float2 wind = normalize(broadWave + float2(0.001, 0.001)) * _WindStrength * gust;
                float bendTip = tip * tip * (0.35 + 0.65 * tip);
                positionWS.xz += (interactionBend + wind) * bendTip;

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                float3 normalOS = dot(input.normalOS, input.normalOS) > 0.01 ? input.normalOS : float3(0, 1, 0);
                output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(normalOS));
                output.uv = input.uv;
                output.instanceRandom = input.instanceRandom;
                output.shadowCoord = TransformWorldToShadowCoord(positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float centerMask = 1.0 - abs(input.uv.x * 2.0 - 1.0);
                float bladeWidth = lerp(1.0, 0.16, saturate(input.uv.y));
                clip(centerMask - (1.0 - bladeWidth));

                Light mainLight = GetMainLight(input.shadowCoord);
                half3 normalWS = normalize(input.normalWS);
                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                half3 direct = mainLight.color * (ndotl * mainLight.shadowAttenuation) * _DirectLightStrength;
                half variation = lerp(0.78, 1.12, input.instanceRandom.y);
                half3 color = _Color.rgb * variation * (ambient + direct + 0.12h);
                return half4(color, _Color.a);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }
}
