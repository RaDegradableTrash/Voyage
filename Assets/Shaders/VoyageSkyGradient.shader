Shader "Voyage/Sky/Gradient"
{
    Properties
    {
        _SkyTint ("Sky Tint", Color) = (0.18, 0.36, 0.56, 1)
        _GroundColor ("Ground Color", Color) = (0.32, 0.34, 0.35, 1)
        _Exposure ("Exposure", Float) = 0
        _SunDirection ("Sun Direction", Vector) = (0, 1, 0, 0)
        _MoonDirection ("Moon Direction", Vector) = (0, -1, 0, 0)
        _HorizonTint ("Horizon tint", Color) = (.24,.42,.62,1)
        _SunColor ("Sun color", Color) = (1,.95,.8,1)
        _StarIntensity ("Star brightness", Range(0,2)) = 0.85
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _SkyTint;
            fixed4 _GroundColor;
            float _Exposure;
            float4 _SunDirection;
            float4 _MoonDirection;
            float4 _HorizonTint, _SunColor;
            float _StarIntensity;

            float StarLayer(float3 direction, float scale, float seed)
            {
                // Stereographic projection is continuous across the visible
                // hemisphere, including the horizon and zenith.
                float2 p = direction.xz / max(.1, 1.0 + direction.y) * scale;
                float2 cell = floor(p);
                float3 hash = frac(sin(float3(dot(cell,float2(127.1,311.7)),
                    dot(cell,float2(269.5,183.3)),dot(cell,float2(419.2,371.9))) + seed) * 43758.5453);
                float2 center = .2 + .6 * hash.xy;
                float radius = lerp(.055,.11,hash.z);
                float footprint = max(length(fwidth(p)), .001);
                float disc = 1.0 - smoothstep(max(0.0,radius-footprint*.5),radius+footprint*.5,length(frac(p)-center));
                return disc * step(.99,hash.z) * lerp(.65,1,hash.x);
            }

            struct Attributes { float4 vertex : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.direction = mul((float3x3)unity_ObjectToWorld, input.vertex.xyz);
                // Skybox vertices must be camera-centered. Project only the
                // world-space ray through the view rotation, never the camera
                // translation, so distant stars and discs cannot parallax.
                float3 viewDirection = mul((float3x3)UNITY_MATRIX_V, output.direction);
                output.positionCS = mul(UNITY_MATRIX_P, float4(viewDirection, 1.0));
                // Keep the sky at the actual far clip plane on both depth
                // conventions. Writing z=w unconditionally puts it at the
                // near plane with reversed-Z, allowing the sun disc (and sky)
                // to draw over mountain depth instead of being occluded by it.
                #if UNITY_REVERSED_Z
                    output.positionCS.z = 0.0;
                #else
                    output.positionCS.z = output.positionCS.w;
                #endif
                return output;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float height = normalize(input.direction).y;
                // Visible sky below the horizontal plane is still atmosphere;
                // terrain depth, rather than a hemisphere mask, hides it.
                float3 poleTint = height >= 0 ? _SkyTint.rgb : lerp(_SkyTint.rgb, _GroundColor.rgb, .15);
                fixed3 sky = lerp(_HorizonTint.rgb, poleTint, smoothstep(0,.8,abs(height)));
                float3 viewDir = normalize(input.direction);
                float sunDot = dot(viewDir, normalize(_SunDirection.xyz));
                float moonDot = dot(viewDir, normalize(_MoonDirection.xyz));
                // World-space angular discs: camera translation cannot move them.
                float aboveHorizon = smoothstep(-0.01, 0.015, height);
                float sunVisible = smoothstep(-0.035, 0.01, _SunDirection.y) * aboveHorizon;
                float moonVisible = smoothstep(-0.035, 0.01, _MoonDirection.y) * aboveHorizon;
                float sunDisk = smoothstep(0.99965, 0.99985, sunDot) * sunVisible;
                float moonDisk = smoothstep(0.99965, 0.99985, moonDot) * moonVisible;
                // Broad atmospheric glow follows the same direction as the disc.
                sky += float3(1.0, 0.48, 0.15) * pow(saturate(sunDot), 128.0) * 0.16 * sunVisible;
                sky = lerp(sky, _SunColor.rgb, sunDisk);
                // Fade stars in after twilight so white pinpoints do not show
                // across the warm dusk sky. Fewer, slightly larger stars read
                // as a night sky instead of subpixel snow.
                float night = 1-smoothstep(-.32,-.14,_SunDirection.y);
                // Overlapping hemisphere projections avoid a singularity at
                // either pole and crossfade below the horizon without a seam.
                float3 upperDir = viewDir;
                float3 lowerDir = float3(viewDir.x, -viewDir.y, viewDir.z);
                float upperStars = StarLayer(upperDir,100,0) + StarLayer(upperDir,180,19.1)*.45;
                float lowerStars = StarLayer(lowerDir,100,37.2) + StarLayer(lowerDir,180,56.3)*.45;
                float stars = lerp(lowerStars, upperStars, smoothstep(-.25, .05, height));
                float horizonVisibility = lerp(.65, 1.0, smoothstep(0,.2,abs(height)));
                sky += stars * night * horizonVisibility * _StarIntensity * float3(.88,.94,1);
                sky = lerp(sky, float3(0.82, 0.88, 1.0), moonDisk);
                return fixed4(sky * exp2(_Exposure), 1.0);
            }
            ENDHLSL
        }
    }
}
