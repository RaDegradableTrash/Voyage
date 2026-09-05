Shader "Hidden/Voyage/VolumetricClouds"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float4 _CloudHeights;
        float4 _CloudWind;
        float4 _VoyageCloudSunDirection;
        float4 _VoyageCloudSunColor;
        float4 _VoyageCloudAmbientColor;
        float4 _VoyageAtmosphereColor;
        float _VoyageCloudLight;
        float _NoiseScale;
        float _Coverage;
        float _Density;
        float _PrimarySteps;
        float _LightSteps;

        float Hash31(float3 p)
        {
            p = frac(p * .1031);
            p += dot(p, p.yzx + 33.33);
            return frac((p.x + p.y) * p.z);
        }

        float Noise3D(float3 p)
        {
            float3 cell = floor(p);
            float3 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = Hash31(cell + float3(0, 0, 0));
            float n100 = Hash31(cell + float3(1, 0, 0));
            float n010 = Hash31(cell + float3(0, 1, 0));
            float n110 = Hash31(cell + float3(1, 1, 0));
            float n001 = Hash31(cell + float3(0, 0, 1));
            float n101 = Hash31(cell + float3(1, 0, 1));
            float n011 = Hash31(cell + float3(0, 1, 1));
            float n111 = Hash31(cell + float3(1, 1, 1));
            return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                        lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
        }

        float CloudShape(float3 worldPosition)
        {
            float height01 = saturate((worldPosition.y - _CloudHeights.x) /
                                      max(1.0, _CloudHeights.y - _CloudHeights.x));
            float verticalProfile = smoothstep(0.0, .16, height01) *
                                    (1.0 - smoothstep(.68, 1.0, height01));
            float2 windOffset = _CloudWind.xy * (_Time.y * _CloudWind.z);
            float3 p = float3(worldPosition.x + windOffset.x, worldPosition.y * .72,
                              worldPosition.z + windOffset.y) * _NoiseScale;
            float baseNoise = Noise3D(p);
            baseNoise += Noise3D(p * 2.03 + 13.7) * .5;
            baseNoise += Noise3D(p * 4.11 - 8.2) * .25;
            baseNoise /= 1.75;
            float threshold = lerp(.72, .30, saturate(_Coverage));
            return saturate((baseNoise - threshold) * 3.2) * verticalProfile * _Density;
        }

        float LightTransmittance(float3 positionWS, float stepLength)
        {
            float opticalDepth = 0.0;
            float3 sunDirection = normalize(_VoyageCloudSunDirection.xyz + float3(.0001, .0001, .0001));
            int steps = clamp((int)_LightSteps, 1, 8);
            [loop]
            for (int i = 0; i < steps; i++)
            {
                positionWS += sunDirection * stepLength;
                opticalDepth += CloudShape(positionWS) * stepLength * .0011;
            }
            return exp(-opticalDepth);
        }

        half4 FragCloud(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float rawDepth = SampleSceneDepth(uv);
            float3 rayOrigin = GetCameraPositionWS();
            // Sky pixels have no scene intersection. Reconstruct a finite
            // far point so clouds can still render against the skybox.
            bool skyPixel = rawDepth <= 0.00001 || rawDepth >= 0.99999;
            float skyDepth;
#if UNITY_REVERSED_Z
            skyDepth = 0.00001;
#else
            skyDepth = 0.99999;
#endif
            float depthForPosition = skyPixel ? skyDepth : rawDepth;
            float3 scenePositionWS = ComputeWorldSpacePosition(uv, depthForPosition, UNITY_MATRIX_I_VP);
            float3 rayDirection = normalize(scenePositionWS - rayOrigin);
            float sceneDistance = distance(scenePositionWS, rayOrigin);
            if (any(scenePositionWS != scenePositionWS) || sceneDistance != sceneDistance || sceneDistance <= 0.0)
                return 0;

            if (abs(rayDirection.y) < .0001) return 0;
            float nearIntersection = (_CloudHeights.x - rayOrigin.y) / rayDirection.y;
            float farIntersection = (_CloudHeights.y - rayOrigin.y) / rayDirection.y;
            float startDistance = max(0.0, min(nearIntersection, farIntersection));
            float endDistance = min(sceneDistance, max(nearIntersection, farIntersection));
            if (endDistance <= startDistance) return 0;

            int steps = clamp((int)_PrimarySteps, 8, 64);
            float stepLength = (endDistance - startDistance) / steps;
            float jitter = Hash31(float3(input.positionCS.xy, _Time.y)) - .5;
            float distanceAlongRay = startDistance + stepLength * (.5 + jitter * .65);
            float transmittance = 1.0;
            float3 accumulated = 0.0;
            float3 sunDirection = normalize(_VoyageCloudSunDirection.xyz + float3(.0001, .0001, .0001));
            float phase = pow(saturate(dot(rayDirection, sunDirection) * .5 + .5), 5.0);

            [loop]
            for (int i = 0; i < steps; i++)
            {
                float3 samplePosition = rayOrigin + rayDirection * distanceAlongRay;
                float density = CloudShape(samplePosition);
                if (density > .002)
                {
                    float extinction = density * stepLength * .0016;
                    float alpha = 1.0 - exp(-extinction);
                    float light = LightTransmittance(samplePosition, stepLength * 1.8);
                    float3 ambient = lerp(_VoyageCloudAmbientColor.rgb, _VoyageAtmosphereColor.rgb, .25);
                    float3 lighting = ambient + _VoyageCloudSunColor.rgb *
                                      (light * (.48 + phase * .85) * _VoyageCloudLight);
                    accumulated += lighting * alpha * transmittance;
                    transmittance *= 1.0 - alpha;
                    if (transmittance < .025) break;
                }
                distanceAlongRay += stepLength;
            }
            return half4(accumulated, 1.0 - transmittance);
        }

        TEXTURE2D_X(_VoyageCloudTexture);

        half4 FragComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
            // The cloud buffer is half resolution and filtered. Without a
            // depth guard, its edge pixels can bleed over nearby terrain or
            // vehicle silhouettes and turn those opaque pixels black/dim.
            float rawSceneDepth = SampleSceneDepth(input.texcoord);
            bool scenePixel = rawSceneDepth > 0.00001 && rawSceneDepth < 0.99999;
#if UNITY_REVERSED_Z
            scenePixel = rawSceneDepth < 0.99999 && rawSceneDepth > 0.00001;
#endif
            if (scenePixel) return scene;
            half4 cloud = SAMPLE_TEXTURE2D_X(_VoyageCloudTexture, sampler_LinearClamp, input.texcoord);
            // A driver/render-graph edge case must never replace a valid
            // scene pixel with undefined cloud data.
            if (cloud.a != cloud.a || cloud.a <= 0.0001h) return scene;
            return half4(scene.rgb * (1.0h - cloud.a) + cloud.rgb, scene.a);
        }
        ENDHLSL

        Pass
        {
            Name "CloudRaymarch"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCloud
            ENDHLSL
        }

        Pass
        {
            Name "CloudComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
