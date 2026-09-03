Shader "Hidden/Voyage/GrassInteractionDecay"
{
    SubShader { Pass { ZTest Always ZWrite Off Cull Off HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex); float _Decay;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=float4(i.p.xy,0,1);o.uv=i.uv;return o;} half4 frag(V i):SV_Target { half4 c=SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv); half pressure=saturate(c.b); half recoveryDecay=lerp(_Decay,pow(_Decay,0.22),pressure); c.rg*=_Decay; c.b*=recoveryDecay; c.a*=recoveryDecay; return c; }
        ENDHLSL } }
}
