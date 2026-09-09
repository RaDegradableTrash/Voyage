Shader "Hidden/Voyage/GrassInteractionDecay"
{
    Properties { _MainTex ("Source state", 2D) = "black" {} }
    SubShader { Pass { ZTest Always ZWrite Off Cull Off CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex; float _Decay;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=UnityObjectToClipPos(i.p);o.uv=i.uv;return o;} half4 frag(V i):SV_Target { return tex2D(_MainTex,i.uv) * _Decay; }
ENDCG } }
}
