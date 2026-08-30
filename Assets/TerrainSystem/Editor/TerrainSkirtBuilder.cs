#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using Voyage.TerrainSystem;

namespace Voyage.TerrainSystem.Editor
{
    internal static class TerrainSkirtBuilder
    {
        public static Mesh Build(Mesh source, Vector2Int coordinate, TerrainChunkSettings settings, float depth, string name)
        {
            Vector3[] sourceVertices = source.vertices;
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            AddEdge(sourceVertices, coordinate, settings, depth, 0, vertices, triangles);
            AddEdge(sourceVertices, coordinate, settings, depth, 1, vertices, triangles);
            AddEdge(sourceVertices, coordinate, settings, depth, 2, vertices, triangles);
            AddEdge(sourceVertices, coordinate, settings, depth, 3, vertices, triangles);
            if (vertices.Count == 0) return null;
            Mesh skirt = new Mesh { name = name };
            if (vertices.Count > 65535) skirt.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            skirt.SetVertices(vertices);
            skirt.SetTriangles(triangles, 0, true);
            skirt.RecalculateNormals();
            skirt.RecalculateBounds();
            return skirt;
        }

        private static void AddEdge(Vector3[] source, Vector2Int coordinate, TerrainChunkSettings settings, float depth, int edge, List<Vector3> vertices, List<int> triangles)
        {
            float half = settings.tileSize * 0.5f;
            float epsilon = Mathf.Max(0.0001f, settings.tileSize * 0.000001f);
            List<Vector3> points = new List<Vector3>();
            for (int i = 0; i < source.Length; i++)
            {
                Vector3 p = source[i];
                bool onEdge = edge == 0 ? Mathf.Abs(p.x + half) <= epsilon : edge == 1 ? Mathf.Abs(p.x - half) <= epsilon : settings.horizontalAxes == TerrainHorizontalAxes.XZ ? (edge == 2 ? Mathf.Abs(p.z + half) <= epsilon : Mathf.Abs(p.z - half) <= epsilon) : (edge == 2 ? Mathf.Abs(p.y + half) <= epsilon : Mathf.Abs(p.y - half) <= epsilon);
                if (onEdge && !ContainsPoint(points, p)) points.Add(p);
            }
            if (points.Count < 2) return;
            points.Sort((a, b) =>
            {
                if (edge <= 1) return settings.horizontalAxes == TerrainHorizontalAxes.XZ ? a.z.CompareTo(b.z) : a.y.CompareTo(b.y);
                return a.x.CompareTo(b.x);
            });
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[i + 1];
                // Do not bridge unrelated boundary islands. Such a bridge creates
                // huge folded faces which show up as underground layers and also
                // makes MeshCollider cooking unstable.
                float alongGap = edge <= 1
                    ? (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? Mathf.Abs(a.z - b.z) : Mathf.Abs(a.y - b.y))
                    : Mathf.Abs(a.x - b.x);
                if (alongGap > settings.tileSize * 0.5f) continue;
                int start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(Down(a, settings, depth)); vertices.Add(Down(b, settings, depth));
                if (edge == 0 || edge == 2)
                {
                    AddQuad(triangles, start, start + 2, start + 1, start + 3);
                }
                else
                {
                    AddQuad(triangles, start, start + 1, start + 2, start + 3);
                }
            }
        }

        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            // One winding is enough. Emitting both windings creates coplanar
            // duplicate faces and severe z-fighting (the black bands in Game view).
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(c); triangles.Add(b); triangles.Add(d);
        }

        private static Vector3 Down(Vector3 value, TerrainChunkSettings settings, float depth)
        {
            if (settings.horizontalAxes == TerrainHorizontalAxes.XZ) value.y -= depth; else value.z -= depth;
            return value;
        }

        private static bool ContainsPoint(List<Vector3> points, Vector3 value)
        {
            for (int i = 0; i < points.Count; i++) if ((points[i] - value).sqrMagnitude < 0.00000001f) return true;
            return false;
        }
    }
}
#endif
