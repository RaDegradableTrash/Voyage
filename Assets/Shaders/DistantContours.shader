Shader "Hidden/Voyage/DistantContours"
{
 SubShader
 {
  Tags { "RenderPipeline"="UniversalPipeline" }
  Pass
  {
   ZWrite Off ZTest Always Cull Off
   HLSLPROGRAM
   #pragma vertex Vert
   #pragma fragment Frag
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
   TEXTURE2D_X_FLOAT(_VoyageContourDepth);
   float4 _VoyageTerrainView;
   float ContourDepth(float2 uv) { return SAMPLE_TEXTURE2D_X(_VoyageContourDepth,sampler_PointClamp,uv).r; }
   half4 Frag(Varyings i):SV_Target
   {
    float2 uv=i.texcoord, pixel=1.5/_ScaledScreenParams.xy;
    half4 color=SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,uv);
    float depth=LinearEyeDepth(ContourDepth(uv),_ZBufferParams);
    float a=LinearEyeDepth(ContourDepth(uv+float2(pixel.x,0)),_ZBufferParams);
    float b=LinearEyeDepth(ContourDepth(uv-float2(pixel.x,0)),_ZBufferParams);
    float c=LinearEyeDepth(ContourDepth(uv+float2(0,pixel.y)),_ZBufferParams);
    float d=LinearEyeDepth(ContourDepth(uv-float2(0,pixel.y)),_ZBufferParams);
    // Reciprocal depth is affine across a perspective-projected plane. Reject
    // its first derivative; the old one-sided depth difference outlined slopes
    // in horizontal bands, even on a perfectly continuous flat surface.
    float2 curvature=abs(float2(rcp(a)+rcp(b),rcp(c)+rcp(d))-2*rcp(depth));
    float2 gradient=abs(float2(rcp(a)-rcp(b),rcp(c)-rcp(d)));
    float2 breaks=max(0,curvature-gradient*.5)*depth;
    float edge=smoothstep(.015,.055,max(breaks.x,breaks.y));
    // Suppress small grass-blade depth steps rather than suppressing all near
    // geometry. Apply the same contour after fog, including fully fogged ridges.
    float jump=max(max(abs(a-depth),abs(b-depth)),max(abs(c-depth),abs(d-depth)));
    edge*=smoothstep(1.5,3.0,jump)*(1-step(_ProjectionParams.z*.999,depth));
    // The junction between local and coarse meshes is a streaming boundary,
    // not a ridge. Avoid drawing an artificial circular contour there.
    if(_VoyageTerrainView.w>0)
    {
     float raw=ContourDepth(uv);
     #if !UNITY_REVERSED_Z
      raw=lerp(UNITY_NEAR_CLIP_VALUE,1,raw);
     #endif
     float3 world=ComputeWorldSpacePosition(uv,raw,UNITY_MATRIX_I_VP);
     edge*=smoothstep(4,16,abs(distance(world.xz,_VoyageTerrainView.xy)-_VoyageTerrainView.w));
    }
    // Draw on the nearer silhouette after fog, retaining distant ridge separation.
    color.rgb=lerp(color.rgb,color.rgb*float3(.60,.65,.72),edge*.72);
    return color;
   }
   ENDHLSL
  }
 }
}
