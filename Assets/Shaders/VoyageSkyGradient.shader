Shader "Voyage/Sky/Gradient"
{
    Properties
    {
        _SkyTint ("Sky Tint", Color) = (0.42, 0.52, 0.62, 1)
        _GroundColor ("Ground Color", Color) = (0.32, 0.34, 0.35, 1)
        _Exposure ("Exposure", Float) = 0.8
        _SunDirection ("Sun Direction", Vector) = (0, 1, 0, 0)
        _MoonDirection ("Moon Direction", Vector) = (0, -1, 0, 0)
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
                fixed3 sky = lerp(_GroundColor.rgb, _SkyTint.rgb, upper);
                sky = lerp(sky, (_SkyTint.rgb + _GroundColor.rgb) * 0.5, horizon * 0.2);
                float3 viewDir = normalize(input.direction);
                float sunDot = dot(viewDir, normalize(_SunDirection.xyz));
                float moonDot = dot(viewDir, normalize(_MoonDirection.xyz));
                float sunDisk = smoothstep(0.9992, 0.9998, sunDot);
                float moonDisk = smoothstep(0.9990, 0.9997, moonDot);
                sky = lerp(sky, float3(1.0, 0.82, 0.42), sunDisk);
                sky = lerp(sky, float3(0.82, 0.88, 1.0), moonDisk);
                return fixed4(sky * exp2(_Exposure), 1.0);
            }
            ENDHLSL
        }
    }
}
