Shader "Hidden/Voyage/GrassInteractionStamp"
{
    SubShader { Pass { Blend One Zero ZTest Always ZWrite Off Cull Off HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex); float4 _StampA,_StampB,_StampDirection; float _StampRadius,_StampStrength;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=float4(i.p.xy,0,1);o.uv=i.uv;return o;}
        half4 frag(V i):SV_Target { half4 old=SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv); float2 ab=_StampB.xy-_StampA.xy; float h=saturate(dot(i.uv-_StampA.xy,ab)/max(dot(ab,ab),1e-5)); float2 q=_StampA.xy+ab*h; float a=saturate(1-distance(i.uv,q)/max(_StampRadius,1e-5))*_StampStrength; half4 stamp=half4(_StampDirection.xy*0.5+0.5,1,1); half4 result=lerp(old,stamp,a); float refreshedRecovery=saturate(a*2.5); result.a=max(old.a,refreshedRecovery); result.b=max(result.b,a); return result; }
        ENDHLSL } }
}
