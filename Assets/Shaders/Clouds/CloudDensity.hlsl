#ifndef VOYAGE_CLOUD_DENSITY_INCLUDED
#define VOYAGE_CLOUD_DENSITY_INCLUDED

float VoyageCloudHash(float3 p)
{
    p = frac(p * .1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float VoyageCloudNoise(float3 p)
{
    float3 c = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(VoyageCloudHash(c), VoyageCloudHash(c + float3(1,0,0)), f.x),
                     lerp(VoyageCloudHash(c + float3(0,1,0)), VoyageCloudHash(c + float3(1,1,0)), f.x), f.y),
                lerp(lerp(VoyageCloudHash(c + float3(0,0,1)), VoyageCloudHash(c + float3(1,0,1)), f.x),
                     lerp(VoyageCloudHash(c + float3(0,1,1)), VoyageCloudHash(c + 1.0), f.x), f.y), f.z);
}

// Connected, wind-stretched banks with flat lower decks and uneven towers.
// Shape is a warped height field eroded by 3D turbulence, never a lattice of spheres.
// layer = bottom, top, noise scale, coverage.
float VoyageCloudDensity(float3 positionWS, float4 layer, float2 offset, float density, float erosion, float filterWidth)
{
    float height = (positionWS.y - layer.x) / max(1.0, layer.y - layer.x);
    if (height <= 0.0 || height >= 1.0 || layer.w <= 0.0 || density <= 0.0) return 0.0;
    float2 horizontal = (positionWS.xz - offset) * max(layer.z, .0001);
    horizontal = float2(horizontal.x * .8 - horizontal.y * .6,
                        horizontal.x * .6 + horizontal.y * .8) * float2(.55, 1.0);
    float2 warp = float2(VoyageCloudNoise(float3(horizontal * .32, 11.4)),
                        VoyageCloudNoise(float3(horizontal * .32 + 8.7, 21.2))) - .5;
    horizontal += warp * 1.6;
    float weather = VoyageCloudNoise(float3(horizontal * .17, 4.7));
    float footprintSize = filterWidth * max(layer.z, .0001);
    float fineDetail = 1.0 - smoothstep(.04, .4, footprintSize);
    float broadDetail = 1.0 - smoothstep(.4, 1.6, footprintSize);
    // Different horizontal sections shear with altitude: cloud edges must not
    // become vertical extruded walls when viewed near the horizon.
    float bank = lerp(.5, VoyageCloudNoise(float3(horizontal, 7.2 + height * 1.6)), broadDetail) * .62
               + lerp(.5, VoyageCloudNoise(float3(horizontal * 2.13, 19.1 + height * 2.5)), fineDetail) * .26
               + lerp(.5, VoyageCloudNoise(float3(horizontal * 4.37, 31.8 + height * 4.0)), fineDetail) * .12;
    float threshold = lerp(.86, .26, saturate(layer.w));
    float footprint = smoothstep(threshold - .08, threshold + .14, bank + (weather - .5) * .38);
    if (footprint <= 0.0) return 0.0;

    float towers = lerp(.5, VoyageCloudNoise(float3(horizontal * 1.25, 43.6)), broadDetail);
    float cap = .25 + towers * .62 + (VoyageCloudNoise(float3(horizontal * 5.7, 52.4)) - .5) * .12 * fineDetail;
    float profile = smoothstep(.02, .10, height) * (1.0 - smoothstep(cap * .52, cap, height));
    float3 p = float3(horizontal.x * 3.0, height * 4.5, horizontal.y * 3.0);
    float curls = VoyageCloudNoise(p + float3(warp.x, 0, warp.y)) * .5
                + VoyageCloudNoise(p * 2.07 + 17.8) * .3
                + VoyageCloudNoise(p * 4.31 - 8.4) * .2;
    curls = lerp(.5, curls, fineDetail);
    float body = footprint * profile;
    // Feathered perimeter and broken undersides; dense banks stay connected.
    body = saturate(body - (1.0 - curls) * (.16 + saturate(erosion) * .6) * (1.0 - body * .65));
    float wisps = exp(-abs(height - (.72 + (bank - .5) * .14)) * 42.0);
    wisps *= smoothstep(threshold + .03, threshold + .18, bank) * .16;
    // A narrow density transition keeps the silhouette painted and crisp;
    // translucent wisps remain separate from the solid bank interiors.
    return (smoothstep(.035, .22, body) + wisps) * density;
}

float VoyageCloudDensity(float3 positionWS, float4 layer, float2 offset, float density, float erosion)
{
    return VoyageCloudDensity(positionWS, layer, offset, density, erosion, 0.0);
}
#endif
