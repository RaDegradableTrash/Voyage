Shader "Voyage/Sky/Gradient"
{
    Properties
    {
        _SkyTint ("Sky Tint", Color) = (0.42, 0.52, 0.62, 1)
        _GroundColor ("Ground Color", Color) = (0.32, 0.34, 0.35, 1)
        _Exposure ("Exposure", Float) = 0.8
        _SunDirection ("Sun Direction", Vector) = (0, 1, 0, 0)
        _MoonDirection ("Moon Direction", Vector) = (0, -1, 0, 0)
        _HorizonTint ("Horizon tint", Color) = (.7,.76,.8,1)
        _SunColor ("Sun color", Color) = (1,.95,.8,1)
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

            struct Attributes { float4 vertex : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.vertex);
                output.direction = mul((float3x3)unity_ObjectToWorld, input.vertex.xyz);
                return output;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float height = normalize(input.direction).y;
                float horizon = saturate(1.0 - abs(height) * 2.0);
                float upper = saturate(height * 0.5 + 0.5);
                fixed3 sky = lerp(_HorizonTint.rgb, _SkyTint.rgb, smoothstep(0,.8,max(height,0)));
                if(height<0) sky=lerp(_HorizonTint.rgb,_GroundColor.rgb,saturate(-height*3));
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
                float night = 1-smoothstep(-.15,.05,_SunDirection.y);
                float2 starCell=floor(viewDir.xz/max(.1,viewDir.y)*550);
                float star=pow(frac(sin(dot(starCell,float2(12.9898,78.233)))*43758.5453),1800);
                sky += star * night * smoothstep(.08,.3,height) * .28;
                sky = lerp(sky, float3(0.82, 0.88, 1.0), moonDisk);
                return fixed4(sky * exp2(_Exposure), 1.0);
            }
            ENDHLSL
        }
    }
}
