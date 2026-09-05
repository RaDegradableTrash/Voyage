Shader "Hidden/Voyage/GrassInteractionScroll"
{
    Properties { _MainTex ("Source state", 2D) = "black" {} }
    SubShader { Pass { ZTest Always ZWrite Off Cull Off CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex; float4 _ScrollOffset;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=UnityObjectToClipPos(i.p);o.uv=i.uv;return o;}
        half4 frag(V i):SV_Target
        {
            float2 sourceUV = i.uv + _ScrollOffset.xy;
            if (sourceUV.x < 0.0 || sourceUV.x > 1.0 || sourceUV.y < 0.0 || sourceUV.y > 1.0) return half4(0,0,0,0);
            return tex2D(_MainTex, sourceUV);
        }
        ENDCG } }
}
