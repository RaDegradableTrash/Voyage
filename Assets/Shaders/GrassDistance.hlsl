#ifndef VOYAGE_GRASS_DISTANCE_INCLUDED
#define VOYAGE_GRASS_DISTANCE_INCLUDED

// Selection depends on world position, never on the owning tile or its LOD.
float VoyageGrassSelection(float3 position)
{
    int2 cell = (int2)floor(position.xz * 16.0);
    uint hash = (uint)cell.x * 73856093u ^ (uint)cell.y * 19349663u;
    hash ^= hash >> 13;
    return (hash & 65535u) / 65536.0;
}

float VoyageGrassDensity(float viewDistance, float nearDistance, float farDistance)
{
    float middle = max(nearDistance + 0.01, farDistance * 0.5);
    float nearBlend = smoothstep(nearDistance, middle, viewDistance);
    float farBlend = smoothstep(middle, max(middle + 0.01, farDistance), viewDistance);
    return lerp(lerp(1.0, 0.42, nearBlend), 0.04, farBlend);
}

#endif
