Shader "Hidden/Voyage/WindSlope"
{
    SubShader { Pass { ZTest Always ZWrite Off Cull Off
        HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        float4 _DirectionForce, _Phase;
        struct A { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
        struct V { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
        V vert(A i) { V o; o.vertex=UnityObjectToClipPos(i.vertex); o.uv=i.uv; return o; }
        float4 frag(V i):SV_Target
        {
            // Integer spatial frequencies make the world-space tile seamless.
            float2 uv = i.uv - _Phase.xy;
            float wave = .65 + .22*sin(dot(uv,float2(19,11))*6.283185)
                              + .13*sin(dot(uv,float2(-7,13))*6.283185);
            float side = .12*sin(dot(uv,float2(9,-17))*6.283185);
            float2 dir = _DirectionForce.xy;
            float2 slope = (dir*wave + float2(-dir.y,dir.x)*side)*_DirectionForce.z;
            return float4(slope,length(slope),0);
        }
        ENDHLSL
    } }
}
