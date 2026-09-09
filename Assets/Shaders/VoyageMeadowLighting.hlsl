#ifndef VOYAGE_MEADOW_LIGHTING
#define VOYAGE_MEADOW_LIGHTING
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
float4 _VoyageGrassEnvironmentColor;
float _VoyageGrassEnvironmentLight;
float3 VoyageGroundAlbedo(float3 position)
{
    float variation = sin(position.x*.035 + sin(position.z*.026))* .035;
    return float3(.43,.39,.29) * (1+variation);
}
float3 VoyageSurfaceLight(float3 albedo, float3 position, float3 normal)
{
    Light key = GetMainLight(TransformWorldToShadowCoord(position));
    // Indirect grass draws do not receive the per-renderer SH probe state used
    // by mesh renderers. Use the shared scene's tri-light environment for both.
    float3 ambient = lerp(unity_AmbientEquator.rgb,
        normal.y>=0 ? unity_AmbientSky.rgb : unity_AmbientGround.rgb,abs(normal.y));
    ambient = max(ambient,float3(.045,.045,.045));
    float3 light = ambient + key.color * saturate(dot(normal,key.direction)*.65+.35) * key.shadowAttenuation;
    #if defined(_ADDITIONAL_LIGHTS)
    uint count=GetAdditionalLightsCount();
    for(uint i=0;i<count;i++)
    {
        Light lamp=GetAdditionalLight(i,position);
        light+=lamp.color*lamp.distanceAttenuation*lamp.shadowAttenuation*saturate(dot(normal,lamp.direction));
    }
    #endif
    // One multiplier for both surfaces; no independent unlit grass brightness floor.
    return albedo * lerp(.78,1,saturate(normal.y)) * light * max(_VoyageGrassEnvironmentColor.rgb,.01) * max(_VoyageGrassEnvironmentLight,.01);
}
#endif
