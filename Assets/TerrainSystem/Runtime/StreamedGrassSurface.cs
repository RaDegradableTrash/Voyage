using System;
using System.Collections;
using UnityEngine;
using GrassFlow;
namespace Voyage.TerrainSystem
{
    // Shared frame budget across every streaming tile; no placement raycasts or saved-asset writes.
    public static class StreamedGrassSurface
    {
        static int frame = -1;
        static double frameStart;
        static bool HasBudget()
        {
            if (!Application.isPlaying) return true;
            if(frame!=Time.frameCount) { frame=Time.frameCount; frameStart=Time.realtimeSinceStartupAsDouble; }
            return Time.realtimeSinceStartupAsDouble-frameStart<.00075;
        }
        public static IEnumerator Build(MeshFilter[] filters, Bounds bounds, Action<GrassFlowPatch> ready)
        {
            if (Application.isPlaying)
            {
                // Runtime fallback tiles are generated while the vehicle is
                // moving. Triangle rasterization allocates the complete LOD
                // vertex/index arrays and can spike when a chunk enters the
                // streaming ring. A coarse height sample has the same visual
                // purpose for fallback grass, but keeps work bounded and
                // independent of terrain mesh triangle count.
                yield return BuildRuntimeSampled(bounds, ready);
                yield break;
            }
            const int n = 128;
            var pixels = new Color[n*n];
            var min = bounds.min; var size = bounds.size; size.y = Mathf.Max(.1f,size.y);
            foreach (var filter in filters)
            {
                if (filter == null || filter.sharedMesh == null || filter.name.IndexOf("skirt",StringComparison.OrdinalIgnoreCase)>=0) continue;
                var mesh = filter.sharedMesh;
                var vertices = mesh.vertices; var triangles = mesh.triangles; var matrix = filter.transform.localToWorldMatrix;
                for (int t=0;t<triangles.Length;t+=3)
                {
                    while(!HasBudget()) yield return null;
                    Vector3 a=matrix.MultiplyPoint3x4(vertices[triangles[t]]), b=matrix.MultiplyPoint3x4(vertices[triangles[t+1]]), c=matrix.MultiplyPoint3x4(vertices[triangles[t+2]]);
                    if (Mathf.Abs(Vector3.Cross(b-a,c-a).normalized.y)<.57f) continue;
                    float d=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z); if (Mathf.Abs(d)<.000001f) continue;
                    int x0=Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x,Mathf.Min(b.x,c.x))-min.x)/size.x*n),0,n-1), x1=Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x,Mathf.Max(b.x,c.x))-min.x)/size.x*n),0,n-1);
                    int z0=Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.z,Mathf.Min(b.z,c.z))-min.z)/size.z*n),0,n-1), z1=Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.z,Mathf.Max(b.z,c.z))-min.z)/size.z*n),0,n-1);
                    for(int z=z0;z<=z1;z++)
                    {
                    while(!HasBudget()) yield return null;
                    for(int x=x0;x<=x1;x++)
                    {
                        float px=min.x+(x+.5f)/n*size.x,pz=min.z+(z+.5f)/n*size.z;
                        float u=((b.z-c.z)*(px-c.x)+(c.x-b.x)*(pz-c.z))/d, v=((c.z-a.z)*(px-c.x)+(a.x-c.x)*(pz-c.z))/d;
                        if(u<0||v<0||u+v>1) continue;
                        float h=(u*a.y+v*b.y+(1-u-v)*c.y-min.y)/size.y; int i=z*n+x;
                        if(pixels[i].g==0||h>pixels[i].r) pixels[i]=new Color(h,1,0,1);
                    }
                    }
                }
            }
            var patch=ScriptableObject.CreateInstance<GrassFlowPatch>(); patch.name="Streamed meadow"; patch.bounds=new Bounds(min+size*.5f,size);
            patch.surface=new Texture2D(n,n,TextureFormat.RGFloat,false,true) { wrapMode=TextureWrapMode.Clamp };
            patch.surface.SetPixels(pixels); patch.surface.Apply();
            for(int i=0;i<pixels.Length;i++) pixels[i]=Color.white*(pixels[i].g>.5f?.78f:0);
            patch.density=new Texture2D(n,n,TextureFormat.RGBA32,false,true) { wrapMode=TextureWrapMode.Clamp };
            patch.density.SetPixels(pixels); patch.density.Apply(); ready(patch);
        }

        static IEnumerator BuildRuntimeSampled(Bounds bounds, Action<GrassFlowPatch> ready)
        {
            const int n = 16;
            Color[] surfacePixels = new Color[n * n];
            Color[] densityPixels = new Color[n * n];
            Vector3 min = bounds.min;
            Vector3 size = bounds.size;
            float rayTop = bounds.center.y + 2000f;
            float rayDistance = 4000f;
            // Keep a small height field inside each tile. A single center
            // sample makes every streamed tile a flat slab of grass, which is
            // visible as staircase-like patches on slopes. Sixteen-by-sixteen
            // samples are enough for bilinear filtering while remaining far
            // cheaper than rasterizing the source mesh triangles.
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    while (!HasBudget()) yield return null;
                    float px = min.x + (x + 0.5f) / n * size.x;
                    float pz = min.z + (z + 0.5f) / n * size.z;
                    RaycastHit hit;
                    bool found = Physics.Raycast(new Vector3(px, rayTop, pz), Vector3.down,
                        out hit, rayDistance, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    int index = z * n + x;
                    if (!found)
                    {
                        surfacePixels[index] = new Color(0.5f, 1f, 0f, 1f);
                        densityPixels[index] = Color.clear;
                        continue;
                    }
                    float normalizedHeight = Mathf.InverseLerp(min.y, min.y + size.y, hit.point.y);
                    surfacePixels[index] = new Color(normalizedHeight, 1f, 0f, 1f);
                    float slope = Vector3.Angle(hit.normal, Vector3.up);
                    float density = 1f - Mathf.InverseLerp(28f, 58f, slope);
                    densityPixels[index] = Color.white * Mathf.Clamp01(density * 0.78f);
                }
            }
            yield return null;
            GrassFlowPatch patch = ScriptableObject.CreateInstance<GrassFlowPatch>();
            patch.name = "Streamed meadow";
            patch.bounds = new Bounds(min + size * 0.5f, size);
            patch.surface = new Texture2D(n, n, TextureFormat.RGFloat, false, true)
            { wrapMode = TextureWrapMode.Clamp };
            patch.surface.filterMode = FilterMode.Bilinear;
            patch.surface.SetPixels(surfacePixels);
            patch.surface.Apply();
            patch.density = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
            { wrapMode = TextureWrapMode.Clamp };
            patch.density.filterMode = FilterMode.Bilinear;
            patch.density.SetPixels(densityPixels);
            patch.density.Apply();
            ready(patch);
        }
    }
}
