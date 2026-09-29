Shader "Voyage/Environment/SeaLevelWater"
{
    Properties
    {
        _SeaLevel ("Sea Level", Float) = -600
        _WaterWind ("Wind", Vector) = (1, 0, 1, 0)
        _WaterRippleTex ("Collision Ripple Height", 2D) = "black" {}
        _WaterRippleCenter ("Ripple Field Center", Vector) = (0, 0, 64, 0.25)
        _WaterCloudWind ("Cloud Wind", Vector) = (0.65, 0.76, 14, 0)
        _WaterCloudParameters ("Cloud Parameters", Vector) = (180, 980, 0.00125, 0.64)
        _WaterCloudDensity ("Cloud Density", Float) = 1.6
        _WaterSkyTint ("Sky Tint", Color) = (0.42, 0.52, 0.62, 1)
        _WaterHorizonTint ("Horizon Tint", Color) = (0.65, 0.74, 0.8, 1)
        _WaterGroundColor ("Ground Color", Color) = (0.32, 0.34, 0.35, 1)
        _WaterSunColor ("Sun Color", Color) = (1, 0.95, 0.8, 1)
        _WaterSunDirection ("Sun Direction", Vector) = (0, 1, 0, 0)
        _WaterMoonDirection ("Moon Direction", Vector) = (0, -1, 0, 0)
        _WaterExposure ("Exposure", Float) = 0.8
        _WaterStarIntensity ("Star Intensity", Float) = 0.85
        _ReflectionDistortion ("Reflection Wave Distortion", Range(0, 2)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "RenderType"="Transparent" }
        Pass
        {
            Name "SeaLevelWater"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVert
            #pragma fragment WaterFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"





            float _VoyageCloudReflectionEnabled;
            TEXTURE2D(_VoyageCloudReflectionTexture);
            TEXTURE2D(_WaterRippleTex);
            SAMPLER(sampler_WaterRippleTex);
            float4 _VoyageAtmosphereColor;
            float4 _VoyageAtmosphereRange;
            float4 _VoyageCloudAmbientColor;
            float _VoyageCloudLight;

            CBUFFER_START(UnityPerMaterial)
                float _SeaLevel;
                float4 _WaterWind;
                float4 _WaterRippleCenter;
                float4 _WaterCloudWind;
                float4 _WaterCloudParameters;
                float _WaterCloudDensity;
                float4 _WaterSkyTint;
                float4 _WaterHorizonTint;
                float4 _WaterGroundColor;
                float4 _WaterSunColor;
                float4 _WaterSunDirection;
                float4 _WaterMoonDirection;
                float _WaterExposure;
                float _WaterStarIntensity;
                float _ReflectionDistortion;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float waveFade : TEXCOORD1;
            };

            float SampleCollisionRipple(float2 worldXZ, out float2 rippleSlope, out float edgeFade)
            {
                float2 fieldUv = (worldXZ - _WaterRippleCenter.xy) /
                    max(.01, _WaterRippleCenter.z * 2.0) + .5;
                float2 edgeDistance = min(fieldUv, 1.0 - fieldUv);
                edgeFade = smoothstep(0.0, .12, min(edgeDistance.x, edgeDistance.y));
                float2 texel = max(_WaterRippleCenter.w / max(.01, _WaterRippleCenter.z * 2.0), .00001);
                float centerHeight = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_WaterRippleTex,
                    fieldUv, 0).r;
                float left = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_WaterRippleTex,
                    fieldUv - float2(texel.x, 0), 0).r;
                float right = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_WaterRippleTex,
                    fieldUv + float2(texel.x, 0), 0).r;
                float down = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_WaterRippleTex,
                    fieldUv - float2(0, texel.y), 0).r;
                float up = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_WaterRippleTex,
                    fieldUv + float2(0, texel.y), 0).r;
                float worldTexel = max(_WaterRippleCenter.w, .001);
                rippleSlope = float2((right - left), (up - down)) /
                    (2.0 * worldTexel) * edgeFade;
                return centerHeight * edgeFade;
            }

            float4 SampleReflectedCloud(float2 uv)
            {
                // Longitude wraps through 360 degrees; latitude stops at the pole.
                uv.x = frac(uv.x);
                uv.y = clamp(uv.y, .5 / 256.0, 1.0 - .5 / 256.0);
                return SAMPLE_TEXTURE2D_LOD(_VoyageCloudReflectionTexture, sampler_LinearRepeat, uv, 0);
            }

            float4 ReflectedCloud(float3 direction, float roughness)
            {
                float2 uv = float2(atan2(direction.x, direction.z) / TWO_PI + .5,
                                   asin(saturate(direction.y)) / HALF_PI);
                float2 radius = float2(1.0 / 512.0, 1.0 / 256.0) * (.35 + roughness * 1.5);
                float4 cloud = SampleReflectedCloud(uv) * .4;
                cloud += SampleReflectedCloud(uv + float2(radius.x, 0)) * .15;
                cloud += SampleReflectedCloud(uv - float2(radius.x, 0)) * .15;
                cloud += SampleReflectedCloud(uv + float2(0, radius.y)) * .15;
                cloud += SampleReflectedCloud(uv - float2(0, radius.y)) * .15;
                return cloud * smoothstep(0.0, .012, direction.y);
            }
            float FogFactor(float distanceToSample)
            {
                if (_VoyageAtmosphereRange.w < .5 || _VoyageAtmosphereRange.z <= .001)
                    return 0.0;
                float range = max(1.0, _VoyageAtmosphereRange.y - _VoyageAtmosphereRange.x);
                float fog = saturate((distanceToSample - _VoyageAtmosphereRange.x) / range);
                fog = fog * fog * (3.0 - 2.0 * fog);
                return fog * saturate(_VoyageAtmosphereRange.z);
            }

            float WaterHeightAndSlope(float2 worldXZ, float filterWidth, out float2 slope, out float foam)
            {
                float2 p = worldXZ - _WaterWind.xy * (_Time.y * _WaterWind.z * .18);
                float height = 0.0;
                slope = 0.0;
                foam = 0.0;

                const float2 direction0 = float2(.96, .28);
                const float2 direction1 = float2(-.42, .91);
                const float2 direction2 = float2(.23, -.97);
                const float2 direction3 = float2(-.82, -.57);
                float phase0 = dot(p, direction0) * .085 + _Time.y * 1.15;
                float phase1 = dot(p, direction1) * .19 - _Time.y * 1.72 + 1.7;
                float phase2 = dot(p, direction2) * .43 + _Time.y * 2.05 + 3.2;
                float phase3 = dot(p, direction3) * .92 - _Time.y * 2.85 + .8;
                float4 visible = 1.0 - smoothstep(.6, 2.8, filterWidth * float4(.085, .19, .43, .92));
                height += sin(phase0) * .34 * visible.x + sin(phase1) * .16 * visible.y +
                    sin(phase2) * .065 * visible.z + sin(phase3) * .022 * visible.w;
                slope += direction0 * (cos(phase0) * (.34 * .085)) * visible.x;
                slope += direction1 * (cos(phase1) * (.16 * .19)) * visible.y;
                slope += direction2 * (cos(phase2) * (.065 * .43)) * visible.z;
                slope += direction3 * (cos(phase3) * (.022 * .92)) * visible.w;

                // Short wind ripples fragment the reflection itself. Their
                // positions are anchored in metres, never in screen UVs.
                float2 windDirection = normalize(_WaterWind.xy + float2(.0001, .0001));
                float2 crossWind = float2(-windDirection.y, windDirection.x);
                float crossPhase = dot(p, crossWind) * .35;
                float ripplePhase = dot(p, windDirection) * 1.7 + sin(crossPhase) * .55 - _Time.y * 1.9;
                float rippleVisibility = 1.0 - smoothstep(.6, 2.8, filterWidth * 1.7);
                height += sin(ripplePhase) * .024 * rippleVisibility;
                slope += cos(ripplePhase) * .024 *
                    (windDirection * 1.7 + crossWind * (.35 * .55 * cos(crossPhase))) * rippleVisibility;
                float2 collisionSlope;
                float collisionEdgeFade;
                height += SampleCollisionRipple(worldXZ, collisionSlope, collisionEdgeFade);
                slope += collisionSlope;
                foam += saturate(length(collisionSlope) * 1.8) * collisionEdgeFade;

                return height;
            }

            Varyings WaterVert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
                float fade = 1.0 - smoothstep(450.0, 6500.0, distanceToCamera);
                float2 slope;
                float foam;
                float height = WaterHeightAndSlope(positionWS.xz, 0.0, slope, foam);
                positionWS.y = _SeaLevel + height * fade;
                output.positionWS = positionWS;
                output.waveFade = fade;
                output.positionCS = TransformWorldToHClip(positionWS);
                // Keep the true sea-plane direction and world position, but
                // allow the distant ocean to survive the terrain far clip.
                // Its depth stays behind all opaque geometry.
                if (output.positionCS.w > 0.0)
                {
#if UNITY_REVERSED_Z
                    output.positionCS.z = max(output.positionCS.z, output.positionCS.w * .000001);
#else
                    output.positionCS.z = min(output.positionCS.z, output.positionCS.w * .999999);
#endif
                }
                return output;
            }

            float StarLayer(float3 direction, float scale, float seed)
            {
                float2 p = direction.xz / max(.1, 1.0 + direction.y) * scale;
                float2 cell = floor(p);
                float3 hash = frac(sin(float3(dot(cell, float2(127.1, 311.7)),
                    dot(cell, float2(269.5, 183.3)), dot(cell, float2(419.2, 371.9))) + seed) * 43758.5453);
                float2 center = .2 + .6 * hash.xy;
                float radius = lerp(.035, .085, hash.z);
                float footprint = max(length(fwidth(p)), .001);
                float disc = 1.0 - smoothstep(max(0.0, radius - footprint * .5),
                    radius + footprint * .5, length(frac(p) - center));
                return disc * step(.97, hash.z) * lerp(.55, 1.0, hash.x);
            }

            float3 ReflectedSky(float3 direction, float roughness)
            {
                direction = normalize(direction);
                float height = direction.y;
                float3 poleTint = height >= 0.0
                    ? _WaterSkyTint.rgb
                    : lerp(_WaterSkyTint.rgb, _WaterGroundColor.rgb, .15);
                float3 sky = lerp(_WaterHorizonTint.rgb, poleTint, smoothstep(0.0, .8, abs(height)));

                float3 sunDirection = normalize(_WaterSunDirection.xyz + float3(.0001, .0001, .0001));
                float3 moonDirection = normalize(_WaterMoonDirection.xyz + float3(.0001, .0001, .0001));
                float sunDot = dot(direction, sunDirection);
                float moonDot = dot(direction, moonDirection);
                float sunVisible = smoothstep(-.035, .01, _WaterSunDirection.y) * smoothstep(-.01, .015, height);
                float moonVisible = smoothstep(-.035, .01, _WaterMoonDirection.y) * smoothstep(-.01, .015, height);
                float sunDisk = smoothstep(.99965, .99985, sunDot) * sunVisible;
                float moonDisk = smoothstep(.99965, .99985, moonDot) * moonVisible;
                sky += _WaterSunColor.rgb * pow(saturate(sunDot), 128.0) * .16 * sunVisible;
                sky = lerp(sky, _WaterSunColor.rgb, sunDisk);

                float night = 1.0 - smoothstep(-.22, -.06, _WaterSunDirection.y);
                float stars = StarLayer(direction, 140.0, 0.0) + StarLayer(direction, 260.0, 19.1) * .45;
                sky += stars * night * _WaterStarIntensity * float3(.88, .94, 1.0);
                sky = lerp(sky, float3(.82, .88, 1.0), moonDisk);

                // The same full-direction reflection is used at every pixel,
                // including the screen edges and directions behind the camera.
                // No view-projection matrix or screen-space visibility test.
                if (_VoyageCloudReflectionEnabled > .5 && direction.y > 0.0)
                {
                    float4 cloud = ReflectedCloud(direction, roughness);
                    sky = sky * (1.0 - cloud.a) + cloud.rgb;
                }
                float atmosphereDistance = min(50000.0, 850.0 / max(.035, max(height, 0.0)));
                sky = lerp(sky, _VoyageAtmosphereColor.rgb, FogFactor(atmosphereDistance) * .55);
                return sky * exp2(_WaterExposure);
            }

            half4 WaterFrag(Varyings input, out float waterDepth : SV_Depth) : SV_Target
            {
                // Reconstruct depth per fragment: interpolating depth from
                // clamped far vertices would pull whole triangles forward over
                // mountains near the terrain far plane. Keep ZWrite Off so later
                // transparent effects retain their existing behavior.
                float4 trueClip = TransformWorldToHClip(input.positionWS);
                float trueDepth = trueClip.z / max(.0001, trueClip.w);
#if UNITY_REVERSED_Z
                waterDepth = max(.000001, trueDepth);
#else
                waterDepth = min(.999999, (trueDepth - UNITY_NEAR_CLIP_VALUE) / (1.0 - UNITY_NEAR_CLIP_VALUE));
#endif
                float2 slope;
                float foam;
                WaterHeightAndSlope(input.positionWS.xz, length(fwidth(input.positionWS.xz)), slope, foam);
                slope *= input.waveFade * _ReflectionDistortion;
                float3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));
                float3 viewDirection = normalize(_WorldSpaceCameraPos - input.positionWS);
                float3 reflectionDirection = reflect(-viewDirection, normalWS);
                float roughness = .18 + saturate(length(slope) * 2.0) * .35;
                float3 reflectedSky = ReflectedSky(reflectionDirection, roughness);

                float viewFacing = saturate(dot(normalWS, viewDirection));
                float fresnel = .04 + .96 * pow(1.0 - viewFacing, 5.0);
                float reflectionWeight = lerp(.43, .97, fresnel);
                float rippleShade = saturate(length(slope) * 1.8);
                float3 deepWater = lerp(float3(.018, .075, .09), float3(.035, .105, .14), rippleShade);
                float3 color = lerp(deepWater, reflectedSky, reflectionWeight);

                float3 sunDirection = normalize(_WaterSunDirection.xyz + float3(.0001, .0001, .0001));
                float3 halfDirection = normalize(viewDirection + sunDirection);
                float specularFacing = saturate(dot(normalWS, halfDirection));
                float sunVisibility = smoothstep(-.12, .03, _WaterSunDirection.y);
                // Broad sun sheen supplements the distorted sky reflection;
                // no noise-driven high-energy glitter replaces it nearby.
                float broadGlint = pow(specularFacing, 38.0) * .06;
                float sharpGlint = pow(specularFacing, 96.0) * .22;
                float sunGlint = (broadGlint + sharpGlint) * sunVisibility * max(_VoyageCloudLight, 0.0);
                color += _WaterSunColor.rgb * sunGlint;

                float foamAmount = saturate(foam * .78);
                color = lerp(color, lerp(float3(.33, .43, .43), _WaterHorizonTint.rgb, .18), foamAmount);
                float distanceToCamera = distance(_WorldSpaceCameraPos, input.positionWS);
                color = lerp(color, _VoyageAtmosphereColor.rgb,
                // Keep the distant ocean recognizable through the horizon
                // haze instead of blending almost completely into the sky.
                FogFactor(distanceToCamera) * .5);
                return half4(color, .97h);
            }
            ENDHLSL
        }
    }
}
