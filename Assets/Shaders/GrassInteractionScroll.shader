Shader "Hidden/Voyage/GrassInteractionScroll"
{
    SubShader { Pass { ZTest Always ZWrite Off Cull Off HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex); float4 _ScrollOffset;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=float4(i.p.xy,0,1);o.uv=i.uv;return o;}
        half4 frag(V i):SV_Target
        {
            float2 sourceUV = i.uv + _ScrollOffset.xy;
            if (sourceUV.x < 0.0 || sourceUV.x > 1.0 || sourceUV.y < 0.0 || sourceUV.y > 1.0) return half4(0,0,0,0);
            return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sourceUV);
        }
        ENDHLSL } }
}
