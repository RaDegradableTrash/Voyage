#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

namespace Voyage.TerrainSystem.Editor
{
    internal static class TerrainTileValidation
    {
        public static string Validate(TerrainTileIndex index)
        {
            StringBuilder report = new StringBuilder();
            int duplicateCoordinates = 0;
            int missingPrefabs = 0;
            HashSet<Vector2Int> coordinates = new HashSet<Vector2Int>();
            for (int i = 0; i < index.tiles.Count; i++)
            {
                TerrainTileRecord record = index.tiles[i];
                if (!coordinates.Add(record.coordinate)) duplicateCoordinates++;
                string assetPath = FindPrefabPath(record.resourcePath);
                if (string.IsNullOrEmpty(assetPath)) { missingPrefabs++; continue; }
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null || prefab.GetComponentInChildren<MeshFilter>(true) == null) missingPrefabs++;
            }
            int missingNeighbors = 0;
            for (int i = 0; i < index.tiles.Count; i++)
            {
                Vector2Int c = index.tiles[i].coordinate;
                if (!coordinates.Contains(c + Vector2Int.right)) missingNeighbors++;
                if (!coordinates.Contains(c + Vector2Int.up)) missingNeighbors++;
            }
            report.AppendLine("TerrainSystem validation report");
            report.AppendLine("Tiles: " + index.tiles.Count);
            report.AppendLine("Duplicate coordinates: " + duplicateCoordinates);
            report.AppendLine("Missing or invalid prefabs: " + missingPrefabs);
            report.AppendLine("Open top/right neighbors (expected at outer boundary): " + missingNeighbors);
            report.AppendLine("Boundary policy: " + (index.settings != null ? index.settings.triangleBoundaryPolicy.ToString() : "Unknown"));
            TerrainBoundaryValidator.Result seam = TerrainBoundaryValidator.Validate(index);
            report.AppendLine("Shared edges checked: " + seam.checkedEdges);
            report.AppendLine("Shared edges failed: " + seam.failedEdges);
            report.AppendLine("Maximum boundary error: " + seam.maximumError.ToString("F######") + " m");
            string path = "Assets/TerrainSystem/Source/TerrainValidationReport.txt";
            File.WriteAllText(Path.GetFullPath(path), report.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
            return report.ToString();
        }

        private static string FindPrefabPath(string resourcePath)
        {
            string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(resourcePath) + " t:Prefab", new[] { "Assets/TerrainSystem/GeneratedTiles" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (path.EndsWith(Path.GetFileName(resourcePath) + ".prefab")) return path;
            }
            return null;
        }
    }

    internal static class TerrainBoundaryValidator
    {
        public struct Result
        {
            public int checkedEdges;
            public int failedEdges;
            public float maximumError;
        }

        public static Result Validate(TerrainTileIndex index)
        {
            Result result = new Result();
            Dictionary<Vector2Int, TerrainTileRecord> records = new Dictionary<Vector2Int, TerrainTileRecord>();
            for (int i = 0; i < index.tiles.Count; i++) records[index.tiles[i].coordinate] = index.tiles[i];
            foreach (KeyValuePair<Vector2Int, TerrainTileRecord> pair in records)
            {
                CheckNeighbor(pair.Key, Vector2Int.right, records, index.settings, ref result);
                CheckNeighbor(pair.Key, Vector2Int.up, records, index.settings, ref result);
            }
            return result;
        }

        private static void CheckNeighbor(Vector2Int coordinate, Vector2Int direction, Dictionary<Vector2Int, TerrainTileRecord> records, TerrainChunkSettings settings, ref Result result)
        {
            Vector2Int neighborCoordinate = coordinate + direction;
            if (!records.TryGetValue(neighborCoordinate, out TerrainTileRecord neighbor)) return;
            if (!records.TryGetValue(coordinate, out TerrainTileRecord current)) return;
            List<Vector3> a = LoadBoundaryVertices(current, coordinate, direction, settings);
            List<Vector3> b = LoadBoundaryVertices(neighbor, neighborCoordinate, -direction, settings);
            result.checkedEdges++;
            float error = Compare(a, b, settings);
            result.maximumError = Mathf.Max(result.maximumError, error);
            float tolerance = settings != null ? settings.boundaryPositionTolerance : 0.0001f;
            if (a.Count == 0 || b.Count == 0 || error > tolerance || a.Count != b.Count) result.failedEdges++;
        }

        private static List<Vector3> LoadBoundaryVertices(TerrainTileRecord record, Vector2Int coordinate, Vector2Int direction, TerrainChunkSettings settings)
        {
            List<Vector3> result = new List<Vector3>();
            string path = FindPrefabPath(record.resourcePath);
            GameObject prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            MeshFilter filter = prefab == null ? null : prefab.transform.Find("LOD0")?.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return result;
            Vector3 origin = settings.GetTileBounds(coordinate).center;
            Vector3[] vertices = filter.sharedMesh.vertices;
            float edge = settings.tileSize * 0.5f;
            float tolerance = settings != null ? settings.boundaryPositionTolerance : 0.0001f;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 local = vertices[i];
                bool onEdge = direction == Vector2Int.right ? Mathf.Abs(local.x - edge) <= tolerance : direction == Vector2Int.left ? Mathf.Abs(local.x + edge) <= tolerance : settings.horizontalAxes == TerrainHorizontalAxes.XZ ? (direction == Vector2Int.up ? Mathf.Abs(local.z - edge) <= tolerance : Mathf.Abs(local.z + edge) <= tolerance) : (direction == Vector2Int.up ? Mathf.Abs(local.y - edge) <= tolerance : Mathf.Abs(local.y + edge) <= tolerance);
                if (onEdge) result.Add(origin + local);
            }
            return result;
        }

        private static float Compare(List<Vector3> a, List<Vector3> b, TerrainChunkSettings settings)
        {
            float maxError = 0f;
            for (int i = 0; i < a.Count; i++)
            {
                float best = float.PositiveInfinity;
                for (int j = 0; j < b.Count; j++)
                {
                    Vector3 delta = a[i] - b[j];
                    float horizontal = settings.horizontalAxes == TerrainHorizontalAxes.XZ ? new Vector2(delta.z, delta.y).sqrMagnitude : new Vector2(delta.y, delta.z).sqrMagnitude;
                    best = Mathf.Min(best, horizontal);
                }
                if (!float.IsPositiveInfinity(best)) maxError = Mathf.Max(maxError, Mathf.Sqrt(best));
            }
            return maxError;
        }

        private static string FindPrefabPath(string resourcePath)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(resourcePath);
            string[] guids = AssetDatabase.FindAssets(file + " t:Prefab", new[] { "Assets/TerrainSystem/GeneratedTiles" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (path.EndsWith(file + ".prefab", StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }
    }
}
#endif
