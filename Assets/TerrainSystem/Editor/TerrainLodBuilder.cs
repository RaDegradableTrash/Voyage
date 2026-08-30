#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem.Editor
{
    internal static class TerrainLodBuilder
    {
        public static Mesh Build(Mesh source, float quality, Vector2Int coordinate, TerrainChunkSettings settings, string name)
        {
            if (quality >= 0.999f)
            {
                Mesh exact = Object.Instantiate(source);
                exact.name = name;
                return exact;
            }

            Vector3[] positions = source.vertices;
            Vector3 size = source.bounds.size;
            float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * Mathf.Max(0.01f, 1f - quality) * 0.08f;
            cell = Mathf.Max(cell, 0.001f);
            Dictionary<Vector3Int, int> clusters = new Dictionary<Vector3Int, int>();
            List<Vector3> newPositions = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector2> newUvs = new List<Vector2>();
            int[] remap = new int[positions.Length];
            Vector3[] normals = source.normals;
            Vector2[] uvs = source.uv;
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 p = positions[i];
                bool boundary = IsBoundaryVertex(p, coordinate, settings);
                Vector3Int key = new Vector3Int(Mathf.RoundToInt(p.x / cell), Mathf.RoundToInt(p.y / cell), Mathf.RoundToInt(p.z / cell));
                if (boundary) key = new Vector3Int(i, int.MinValue, int.MinValue);
                if (!clusters.TryGetValue(key, out int index))
                {
                    index = newPositions.Count;
                    clusters.Add(key, index);
                    newPositions.Add(p);
                    newNormals.Add(normals.Length == positions.Length ? normals[i] : Vector3.up);
                    newUvs.Add(uvs.Length == positions.Length ? uvs[i] : Vector2.zero);
                }
                remap[i] = index;
            }

            Mesh result = new Mesh { name = name };
            if (newPositions.Count > 65535) result.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            result.subMeshCount = source.subMeshCount;
            List<List<int>> simplifiedSubmeshes = new List<List<int>>(source.subMeshCount);
            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                int[] triangles = source.GetTriangles(sub);
                List<int> simplified = new List<int>(triangles.Length);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = remap[triangles[i]];
                    int b = remap[triangles[i + 1]];
                    int c = remap[triangles[i + 2]];
                    // Never remove a source triangle just because clustering made it
                    // degenerate. Removing it creates pin-hole cracks in irregular
                    // mountain meshes. Preserve that triangle with private vertices;
                    // the LOD becomes slightly heavier, but remains watertight.
                    if (a == b || b == c || c == a)
                    {
                        a = AppendPrivateVertex(triangles[i], positions, normals, uvs, newPositions, newNormals, newUvs);
                        b = AppendPrivateVertex(triangles[i + 1], positions, normals, uvs, newPositions, newNormals, newUvs);
                        c = AppendPrivateVertex(triangles[i + 2], positions, normals, uvs, newPositions, newNormals, newUvs);
                    }
                    simplified.Add(a); simplified.Add(b); simplified.Add(c);
                }
                simplifiedSubmeshes.Add(simplified);
            }
            // Degenerate source triangles may append private vertices above, so
            // assign vertex buffers only after all indices have been finalized.
            result.SetVertices(newPositions);
            result.SetNormals(newNormals);
            result.SetUVs(0, newUvs);
            for (int sub = 0; sub < simplifiedSubmeshes.Count; sub++)
                result.SetTriangles(simplifiedSubmeshes[sub], sub, true);
            result.RecalculateBounds();
            result.RecalculateNormals();
            return result;
        }

        private static int AppendPrivateVertex(int sourceIndex, Vector3[] positions, Vector3[] normals, Vector2[] uvs,
            List<Vector3> newPositions, List<Vector3> newNormals, List<Vector2> newUvs)
        {
            int index = newPositions.Count;
            newPositions.Add(positions[sourceIndex]);
            newNormals.Add(normals.Length == positions.Length ? normals[sourceIndex] : Vector3.up);
            newUvs.Add(uvs.Length == positions.Length ? uvs[sourceIndex] : Vector2.zero);
            return index;
        }

        private static bool IsBoundaryVertex(Vector3 localPosition, Vector2Int coordinate, TerrainChunkSettings settings)
        {
            float epsilon = Mathf.Max(0.0001f, settings.tileSize * 0.000001f);
            float half = settings.tileSize * 0.5f;
            if (Mathf.Abs(localPosition.x + half) <= epsilon || Mathf.Abs(localPosition.x - half) <= epsilon) return true;
            if (settings.horizontalAxes == TerrainHorizontalAxes.XZ)
                return Mathf.Abs(localPosition.z + half) <= epsilon || Mathf.Abs(localPosition.z - half) <= epsilon;
            return Mathf.Abs(localPosition.y + half) <= epsilon || Mathf.Abs(localPosition.y - half) <= epsilon;
        }
    }
}
#endif
