Shader "Voyage/WindLine"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 position:POSITION; float4 color:COLOR; };
            struct V { float4 position:SV_POSITION; float4 color:COLOR; float fog:TEXCOORD0; };
            V vert(A i) { V o; o.position=TransformObjectToHClip(i.position.xyz); o.color=i.color; o.fog=ComputeFogFactor(o.position.z); return o; }
            half4 frag(V i):SV_Target { return half4(MixFog(i.color.rgb,i.fog),i.color.a); }
            ENDHLSL
        }
    }
}
