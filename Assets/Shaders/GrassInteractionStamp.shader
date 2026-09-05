Shader "Hidden/Voyage/GrassInteractionStamp"
{
    Properties { _MainTex ("Source state", 2D) = "black" {} }
    SubShader { Pass { Blend One Zero ZTest Always ZWrite Off Cull Off CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex; float4 _StampA,_StampB,_StampDirection; float _StampRadius,_StampStrength;
        struct A { float4 p:POSITION; float2 uv:TEXCOORD0; }; struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i){V o;o.p=UnityObjectToClipPos(i.p);o.uv=i.uv;return o;}
        half4 frag(V i):SV_Target
        {
            half4 old = tex2D(_MainTex,i.uv);
            float2 ab = _StampB.xy - _StampA.xy;
            float h = saturate(dot(i.uv-_StampA.xy,ab)/max(dot(ab,ab),1e-10));
            float coverage = 1.0-smoothstep(0.35,1.0,distance(i.uv,_StampA.xy+ab*h)/max(_StampRadius,1e-6));
            float pressure = coverage * _StampStrength;
            // Max pressure is invariant under frame rate and repeated axles.
            if (pressure <= old.b) return old;
            return half4(_StampDirection.xy * pressure, pressure, pressure);
        }
        ENDCG } }
}
