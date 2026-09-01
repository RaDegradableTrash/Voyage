Shader "Voyage/Grass/InteractiveUnlit"
{
    Properties { _Color ("Color", Color) = (0.18,0.42,0.12,1) _WindStrength ("Wind Strength", Float) = 0.08 _BendStrength ("Interaction Bend", Float) = 0.8 _RecoverySpeed ("Recovery Speed", Float) = 0.9 _InteractionEnabled ("Interaction Enabled", Float) = 1 _Density ("Density", Range(0,1)) = 1 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_VoyageGrassInteraction); SAMPLER(sampler_VoyageGrassInteraction);
            float4 _VoyageGrassInteractionWorld;
            float4 _Color; float _WindStrength; float _BendStrength; float _RecoverySpeed; float _InteractionEnabled; float _Density;
            struct Attributes { float4 positionOS:POSITION; float2 uv:TEXCOORD0; float2 instanceRandom:TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float3 positionWS:TEXCOORD1; float2 instanceRandom:TEXCOORD2; };
            float2 fieldUV(float3 p) { return (p.xz - (_VoyageGrassInteractionWorld.xy - _VoyageGrassInteractionWorld.zz * 0.5)) / max(_VoyageGrassInteractionWorld.zz, 1.0); }
            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o; float3 p = TransformObjectToWorld(input.positionOS.xyz); float tip = saturate(input.uv.y);
                float2 uv = fieldUV(p);
                float4 temporary = SAMPLE_TEXTURE2D_LOD(_VoyageGrassInteraction, sampler_VoyageGrassInteraction, uv, 0);
                float2 bendDir = temporary.rg * 2.0 - 1.0;
                float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0) * step(1.0, _VoyageGrassInteractionWorld.z);
                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeFade = saturate(edgeDistance * max(_VoyageGrassInteractionWorld.w, 1.0) * 2.0);
                float temporaryWeight = temporary.b * inside * edgeFade;
                float recoveryAge = saturate(1.0 - temporary.a);
                // A damped oscillator gives the blades a short, subtle
                // counter-sway while recovering instead of a purely monotonic
                // time fade. The field remains the only stateful input, so
                // this does not add another simulation texture or CPU work.
                float angularFrequency = 12.0;
                float damping = max(_RecoverySpeed, 0.01) * 1.35;
                float spring = exp(-damping * recoveryAge) *
                               (cos(angularFrequency * recoveryAge) +
                                damping / angularFrequency * sin(angularFrequency * recoveryAge));
                float strength = temporaryWeight * spring * _InteractionEnabled;
                float seed = input.instanceRandom.x * 19.37 + input.instanceRandom.y * 7.11;
                float2 wind = float2(sin(p.x * 0.07 + _Time.y * (0.8 + input.instanceRandom.y * 0.35) + seed), cos(p.z * 0.06 + _Time.y * 1.17 + seed * 1.7)) * _WindStrength * tip;
                float2 bend = (bendDir * strength * _BendStrength + wind) * tip * tip;
                p.xz += bend;
                o.positionWS = p; o.positionCS = TransformWorldToHClip(p); o.uv = input.uv; o.instanceRandom = input.instanceRandom; return o;
            }
            half4 frag(Varyings input):SV_Target
            {
                float centerMask = 1.0 - abs(input.uv.x * 2.0 - 1.0);
                // The blade is wide at its root and narrows toward the tip.
                // centerMask is 1 in the middle and 0 at the quad edges, so
                // the clip threshold must be the inverse of the desired width.
                // This keeps the broad base and trims the top to a point.
                float bladeWidth = lerp(1.0, 0.16, saturate(input.uv.y));
                clip(_Density - input.instanceRandom.x);
                clip(centerMask - (1.0 - bladeWidth));
                // Keep the debug phase untextured, but vary each blade subtly
                // so a cluster does not read as one flat white/green card.
                float shade = lerp(0.72, 1.12, input.instanceRandom.y);
                return half4(_Color.rgb * shade, _Color.a);
            }
            ENDHLSL
        }
    }
}
