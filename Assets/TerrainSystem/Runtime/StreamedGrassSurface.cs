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
            return Time.realtimeSinceStartupAsDouble-frameStart<.0015;
        }
        public static IEnumerator Build(MeshFilter[] filters, Bounds bounds, Action<GrassFlowPatch> ready)
        {
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
    }
}
