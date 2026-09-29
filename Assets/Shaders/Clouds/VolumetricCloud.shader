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

        #include "CloudDensity.hlsl"

        float4 _CloudHeights;
        float4 _CloudOffset;
        float4 _CloudTexelSize;
        float _Erosion;
        float _MaxCloudDistance;
        float _Extinction;
        float4 _VoyageCloudSunDirection;
        float4 _VoyageCloudSunColor;
        float4 _VoyageCloudAmbientColor;
        float4 _VoyageCloudTwilight;
        float4 _VoyageAtmosphereColor;
        float4 _VoyageAtmosphereRange;
        float _VoyageCloudLight;
        float _NoiseScale;
        float _Coverage;
        float _Density;
        float _PrimarySteps;
        float _LightSteps;
        float _ReflectionSteps;
        float4 _CloudReflectionOrigin;

        Varyings CloudVert(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            // This pass uses DrawProcedural, not Blitter; it must not inherit
            // a previous blit's _BlitScaleBias when reconstructing sky rays.
            output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
            return output;
        }

        float CloudShape(float3 positionWS)
        {
            return VoyageCloudDensity(positionWS, float4(_CloudHeights.xy, _NoiseScale, _Coverage),
                _CloudOffset.xy, _Density, _Erosion);
        }

        float LightTransmittance(float3 positionWS)
        {
            float3 lightDirection = normalize(_VoyageCloudSunDirection.xyz + float3(.0001, .0001, .0001));
            float thickness = _CloudHeights.y - _CloudHeights.x;
            // Independent of view-ray length: turning the camera cannot change self-shadowing.
            float exitDistance = lightDirection.y >= 0.0
                ? (_CloudHeights.y - positionWS.y) / max(.05, lightDirection.y)
                : (_CloudHeights.x - positionWS.y) / min(-.05, lightDirection.y);
            float lightDistance = min(max(0.0, exitDistance), thickness * 3.0);
            int steps = clamp((int)_LightSteps, 1, 8);
            float stepLength = lightDistance / steps;
            float opticalDepth = 0.0;
            [loop] for (int i = 0; i < steps; i++)
                opticalDepth += CloudShape(positionWS + lightDirection * ((i + .5) * stepLength)) * stepLength;
            return exp(-opticalDepth * _Extinction);
        }
        float CloudFogFactor(float distanceToSample)
        {
            if (_VoyageAtmosphereRange.w < .5 || _VoyageAtmosphereRange.z <= .001)
                return 0.0;

            float fogRange = max(1.0, _VoyageAtmosphereRange.y - _VoyageAtmosphereRange.x);
            float distanceFog = saturate((distanceToSample - _VoyageAtmosphereRange.x) / fogRange);
            // A soft ramp avoids a visible boundary while retaining the
            // distance haze used by the terrain and its silhouettes.
            distanceFog = distanceFog * distanceFog * (3.0 - 2.0 * distanceFog);
            return distanceFog * saturate(_VoyageAtmosphereRange.z);
        }

        half4 EmptyCloud()
        {
            return 0;
        }

        half4 TraceCloud(float3 rayOrigin, float3 rayDirection, float sceneDistance, int steps)
        {
            float startDistance;
            float endDistance;
            if (abs(rayDirection.y) < .0001)
            {
                // A near-horizontal ray can remain inside a cloud layer for
                // many kilometres; only march it when the camera is in-band.
                if (rayOrigin.y < _CloudHeights.x || rayOrigin.y > _CloudHeights.y)
                    return EmptyCloud();
                startDistance = 0.0;
                endDistance = sceneDistance;
            }
            else
            {
                float nearIntersection = (_CloudHeights.x - rayOrigin.y) / rayDirection.y;
                float farIntersection = (_CloudHeights.y - rayOrigin.y) / rayDirection.y;
                startDistance = max(0.0, min(nearIntersection, farIntersection));
                endDistance = min(sceneDistance, max(nearIntersection, farIntersection));
            }
            if (endDistance <= startDistance) return EmptyCloud();

            // Visibility distance and traversal length are different budgets.
            // A horizon ray may reach a cloud bank 100 km away, but marching
            // all 100 km at 64 samples produces box-shaped slices. Resolve the
            // first 10 km of the intersected bank at consistent spatial detail.
            endDistance = min(endDistance, startDistance + 10000.0);


            // Use regular centered samples. Per-pixel random ray jitter showed
            // up as white stippling across the sky at the reduced cloud scale.
            float transmittance = 1.0;
            float3 accumulated = 0.0;
            float3 sunDirection = normalize(_VoyageCloudSunDirection.xyz + float3(.0001, .0001, .0001));
            float sunPhase = saturate(dot(rayDirection, sunDirection) * .5 + .5);
            float phase = pow(sunPhase, 5.0);

            [loop]
            for (int i = 0; i < steps; i++)
            {
                float u0 = (float)i / steps;
                float u1 = (float)(i + 1) / steps;
                float intervalStart = lerp(startDistance, endDistance, u0);
                float intervalEnd = lerp(startDistance, endDistance, u1);
                float stepLength = intervalEnd - intervalStart;
                float distanceAlongRay = (intervalStart + intervalEnd) * .5;
                float3 samplePosition = rayOrigin + rayDirection * distanceAlongRay;
                float density = VoyageCloudDensity(samplePosition, float4(_CloudHeights.xy, _NoiseScale, _Coverage),
                    _CloudOffset.xy, _Density, _Erosion, max(stepLength * .5, distanceAlongRay * .003));
                density *= 1.0 - smoothstep(_MaxCloudDistance * .8, _MaxCloudDistance, distanceAlongRay);
                if (density > .002)
                {
                    // A deliberately soft extinction keeps the sky and fog
                    // visible through the cloud banks.
                    float extinction = density * stepLength * _Extinction;
                    float alpha = 1.0 - exp(-extinction);
                    float light = LightTransmittance(samplePosition);
                    float height = saturate((samplePosition.y - _CloudHeights.x) / (_CloudHeights.y - _CloudHeights.x));
                    float3 ambient = _VoyageCloudAmbientColor.rgb * lerp(.48, 1.12, height);
                    ambient *= lerp(float3(1,1,1), float3(1.15,.65,.9), _VoyageCloudTwilight.w);
                    ambient += _VoyageCloudTwilight.rgb * _VoyageCloudTwilight.w * .13;
                    // Broad multiple scattering keeps shadowed cloud bottoms soft.
                    light = light * .8 + .2;
                    float3 lighting = ambient + _VoyageCloudSunColor.rgb *
                                      (light * (.48 + phase * .85) * _VoyageCloudLight);
                    lighting += _VoyageCloudTwilight.rgb * _VoyageCloudTwilight.w *
                        (.06 + light * .45) * (.4 + phase * .6);

                    // RenderSettings fog does not affect a fullscreen raymarch,
                    // so explicitly haze each world-space cloud sample using
                    // the same palette and distance range as FogSystem.
                    float fogFactor = CloudFogFactor(distanceAlongRay);
                    float3 fogLitCloud = _VoyageAtmosphereColor.rgb * (.78 + phase * .38);
                    // Keep distant clouds tinted by the atmosphere without
                    // washing their shape into the sky's fog color.
                    lighting = lerp(lighting, fogLitCloud, fogFactor * .30);
                    accumulated += lighting * alpha * transmittance;
                    transmittance *= 1.0 - alpha;
                    if (transmittance < .025) break;
                }
            }
            return half4(accumulated, 1.0 - transmittance);
        }

        half4 FragCloud(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float rawDepth = SampleSceneDepth(uv);
            bool skyPixel;
#if UNITY_REVERSED_Z
            skyPixel = rawDepth <= 0.000001;
#else
            skyPixel = rawDepth >= 0.999999;
#endif
            float3 rayOrigin = GetCameraPositionWS();
            // Reconstruct the ray with the same inverse VP transform used for
            // opaque depth below. That keeps cloud samples and scene depth in
            // exactly the same world-space camera basis while the view turns.
#if UNITY_REVERSED_Z
            float farDepth = .0001;
            float nearDepth = 1.0;
#else
            float farDepth = .9999;
            float nearDepth = UNITY_NEAR_CLIP_VALUE;
            rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
#endif
            float3 farPositionWS = ComputeWorldSpacePosition(uv, farDepth, UNITY_MATRIX_I_VP);
            float3 rayDirection = normalize(farPositionWS - rayOrigin);
            if (unity_OrthoParams.w > .5)
            {
                rayOrigin = ComputeWorldSpacePosition(uv, nearDepth, UNITY_MATRIX_I_VP);
                rayDirection = normalize(farPositionWS - rayOrigin);
            }

            // Far horizon rays otherwise accumulate density through tens of
            // kilometres and turn the whole sky opaque. Fog handles the
            // remaining distance beyond this cloud detail range.
            float maximumDistance = _MaxCloudDistance;
            float sceneDistance = maximumDistance;
            if (!skyPixel)
            {
                float3 scenePositionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                sceneDistance = min(maximumDistance, distance(scenePositionWS, rayOrigin));
            }

            return TraceCloud(rayOrigin, rayDirection, sceneDistance, clamp((int)_PrimarySteps, 8, 96));
        }

        half4 FragReflection(Varyings input) : SV_Target
        {
            float longitude = (input.texcoord.x - .5) * TWO_PI;
            float elevation = input.texcoord.y * HALF_PI;
            float3 direction = float3(sin(longitude) * cos(elevation), sin(elevation), cos(longitude) * cos(elevation));
            return TraceCloud(_CloudReflectionOrigin.xyz, direction, _MaxCloudDistance, clamp((int)_ReflectionSteps, 8, 64));
        }
        TEXTURE2D_X(_VoyageCloudTexture);

        float CloudEyeDepth(float rawDepth)
        {
            if (unity_OrthoParams.w < .5) return LinearEyeDepth(rawDepth, _ZBufferParams);
#if UNITY_REVERSED_Z
            rawDepth = 1.0 - rawDepth;
#endif
            return lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
        }

        half4 FragComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
            // Bilateral upsample: never blend a sky texel across a nearer silhouette.
            float depth = SampleSceneDepth(input.texcoord);
            float eyeDepth = CloudEyeDepth(depth);
            float2 pixel = input.texcoord * _CloudTexelSize.zw - .5;
            float2 basePixel = floor(pixel);
            float2 fraction = frac(pixel);
            half4 cloud = 0;
            float totalWeight = 0;
            [unroll] for (int y = 0; y < 2; y++)
            {
                [unroll] for (int x = 0; x < 2; x++)
                {
                    float2 tapUv = (basePixel + float2(x, y) + .5) * _CloudTexelSize.xy;
                    float tapDepth = CloudEyeDepth(SampleSceneDepth(tapUv));
                    float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
                                 * (y == 0 ? 1.0 - fraction.y : fraction.y);
                    weight *= exp(-abs(tapDepth - eyeDepth) / max(1.0, eyeDepth * .01));
                    cloud += SAMPLE_TEXTURE2D_X(_VoyageCloudTexture, sampler_PointClamp, tapUv) * weight;
                    totalWeight += weight;
                }
            }
            // At thin foreground geometry no low-resolution sample may match.
            // Re-evaluate just these edge pixels at their actual scene depth.
            [branch] if (totalWeight > .001) cloud /= totalWeight;
            else cloud = FragCloud(input);
            // Empty or invalid cloud data must preserve the source image.
            if (cloud.a != cloud.a || cloud.a <= 0.0001h) return scene;
            // Radiance is premultiplied during integration. The cloud depth
            // clipping in the raymarch keeps clouds behind foreground objects.
            return half4(scene.rgb * (1.0h - cloud.a) + cloud.rgb, scene.a);
        }
        ENDHLSL

        Pass
        {
            Name "CloudRaymarch"
            HLSLPROGRAM
            #pragma vertex CloudVert
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
        Pass
        {
            Name "CloudReflectionPanorama"
            HLSLPROGRAM
            #pragma vertex CloudVert
            #pragma fragment FragReflection
            ENDHLSL
        }
    }
}
