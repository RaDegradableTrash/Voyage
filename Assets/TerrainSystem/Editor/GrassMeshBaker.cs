using System;
using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem.Editor
{
    internal static class GrassMeshBaker
    {
        public static GrassPrototypeAsset BuildPrototype(TerrainChunkSettings settings, string assetName)
        {
            if (settings == null || settings.grassDensity <= 0f) return null;
            System.Random random = new System.Random(73856093 ^ settings.grassBladesPerCluster * 19349663);
            List<Vector3> vertices = new List<Vector3>(settings.grassBladesPerCluster * 6);
            List<Vector2> uvs = new List<Vector2>(vertices.Capacity);
            List<Vector2> randoms = new List<Vector2>(vertices.Capacity);
            List<int> indices = new List<int>(settings.grassBladesPerCluster * 12);
            for (int blade = 0; blade < settings.grassBladesPerCluster; blade++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = Mathf.Sqrt((float)random.NextDouble()) * Mathf.Max(0.05f, settings.grassClusterRadius);
                Vector3 local = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                float height = settings.grassBladeHeight * (0.72f + (float)random.NextDouble() * 0.56f);
                float width = height * (0.10f + (float)random.NextDouble() * 0.05f);
                float yaw = (float)random.NextDouble() * Mathf.PI;
                float variation = (float)random.NextDouble();
                AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw, variation);
                AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw + Mathf.PI * 0.5f, variation * 0.73f + 0.11f);
            }
            Mesh clusterMesh = new Mesh { name = assetName + " Cluster Mesh" };
            clusterMesh.SetVertices(vertices);
            clusterMesh.SetUVs(0, uvs);
            clusterMesh.SetUVs(1, randoms);
            clusterMesh.SetTriangles(indices, 0, true);
            clusterMesh.RecalculateBounds();
            GrassPrototypeAsset asset = ScriptableObject.CreateInstance<GrassPrototypeAsset>();
            asset.name = assetName;
            asset.clusterMesh = clusterMesh;
            asset.bladesPerCluster = settings.grassBladesPerCluster;
            asset.clusterRadius = settings.grassClusterRadius;
            asset.bladeHeight = settings.grassBladeHeight;
            return asset;
        }

        public static GrassChunkAsset BuildAsset(List<TerrainMeshBaker.TriangleSource> triangles, Vector3 origin, Vector2Int coordinate, TerrainChunkSettings settings, string assetName)
        {
            if (triangles == null || triangles.Count == 0 || settings.grassDensity <= 0f) return null;
            float spacing = Mathf.Max(1f, settings.grassClusterSpacing);
            int sideCount = Mathf.Max(1, Mathf.CeilToInt(settings.tileSize / spacing));
            int clusterCount = Mathf.Min(settings.grassClusterBudget, Mathf.CeilToInt(sideCount * sideCount * settings.grassDensity));
            if (clusterCount <= 0) return null;

            float[] cumulativeAreas = new float[triangles.Count];
            float totalArea = 0f;
            for (int i = 0; i < triangles.Count; i++)
            {
                TerrainMeshBaker.TriangleSource t = triangles[i];
                totalArea += Vector3.Cross(t.b - t.a, t.c - t.a).magnitude * 0.5f;
                cumulativeAreas[i] = totalArea;
            }
            if (totalArea <= 0.0001f) return null;

            List<Vector3> positions = new List<Vector3>(clusterCount);
            List<Vector4> parameters = new List<Vector4>(clusterCount);
            List<float> scales = new List<float>(clusterCount);
            System.Random random = new System.Random(unchecked(coordinate.x * 73856093 ^ coordinate.y * 19349663));
            int attempts = 0;
            int maxAttempts = Mathf.Max(clusterCount * 8, 64);
            while (positions.Count < clusterCount && attempts++ < maxAttempts)
            {
                float pick = (float)random.NextDouble() * totalArea;
                int triangleIndex = Array.BinarySearch(cumulativeAreas, pick);
                if (triangleIndex < 0) triangleIndex = ~triangleIndex;
                triangleIndex = Mathf.Clamp(triangleIndex, 0, triangles.Count - 1);
                TerrainMeshBaker.TriangleSource t = triangles[triangleIndex];
                float r1 = Mathf.Sqrt((float)random.NextDouble());
                float r2 = (float)random.NextDouble();
                Vector3 position = t.a * (1f - r1) + t.b * (r1 * (1f - r2)) + t.c * (r1 * r2);
                float densityNoise = Mathf.Lerp(0.55f, 1.15f, Mathf.PerlinNoise(position.x * 0.035f, position.z * 0.035f));
                if (random.NextDouble() > settings.grassDensity * densityNoise) continue;
                Vector3 normal = (t.na * (1f - r1) + t.nb * (r1 * (1f - r2)) + t.nc * (r1 * r2)).normalized;
                float slope = Vector3.Angle(normal, Vector3.up);
                float slopeDensity = 1f - Mathf.InverseLerp(settings.grassFullDensityBelowSlope, settings.grassNoGrassAboveSlope, slope);
                if (slopeDensity <= 0f || random.NextDouble() > slopeDensity) continue;
                positions.Add(position - origin);
                Quaternion groundRotation = Quaternion.FromToRotation(Vector3.up, normal);
                Quaternion yawRotation = Quaternion.AngleAxis((float)random.NextDouble() * 360f, normal);
                Quaternion rotation = yawRotation * groundRotation;
                parameters.Add(new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
                scales.Add(0.82f + (float)random.NextDouble() * 0.36f);
            }
            if (positions.Count == 0) return null;

            List<Vector3> vertices = new List<Vector3>(settings.grassBladesPerCluster * 8);
            List<Vector2> uvs = new List<Vector2>(vertices.Capacity);
            List<Vector2> randoms = new List<Vector2>(vertices.Capacity);
            List<int> indices = new List<int>(settings.grassBladesPerCluster * 12);
            for (int blade = 0; blade < settings.grassBladesPerCluster; blade++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = Mathf.Sqrt((float)random.NextDouble()) * Mathf.Max(0.05f, settings.grassClusterRadius);
                Vector3 local = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                float height = settings.grassBladeHeight * (0.72f + (float)random.NextDouble() * 0.56f);
                float width = height * (0.10f + (float)random.NextDouble() * 0.05f);
                float yaw = (float)random.NextDouble() * Mathf.PI;
                float variation = (float)random.NextDouble();
                AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw, variation);
                AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw + Mathf.PI * 0.5f, variation * 0.73f + 0.11f);
            }
            Mesh clusterMesh = new Mesh { name = assetName + " Cluster Mesh" };
            clusterMesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            clusterMesh.SetVertices(vertices); clusterMesh.SetUVs(0, uvs); clusterMesh.SetUVs(1, randoms); clusterMesh.SetTriangles(indices, 0, true); clusterMesh.RecalculateBounds();
            GrassChunkAsset asset = ScriptableObject.CreateInstance<GrassChunkAsset>();
            asset.name = assetName;
            asset.clusterMesh = clusterMesh;
            asset.positions = positions.ToArray();
            asset.parameters = parameters.ToArray();
            asset.scales = scales.ToArray();
            return asset;
        }

        public static Mesh Build(List<TerrainMeshBaker.TriangleSource> triangles, Vector3 origin, Vector2Int coordinate, TerrainChunkSettings settings, string meshName)
        {
            if (triangles == null || triangles.Count == 0 || settings.grassDensity <= 0f) return null;

            float spacing = Mathf.Max(1f, settings.grassClusterSpacing);
            int sideCount = Mathf.Max(1, Mathf.CeilToInt(settings.tileSize / spacing));
            int desiredClusters = Mathf.CeilToInt(sideCount * sideCount * settings.grassDensity);
            int clusterCount = Mathf.Min(settings.grassClusterBudget, desiredClusters);
            if (clusterCount <= 0) return null;

            float[] cumulativeAreas = new float[triangles.Count];
            float totalArea = 0f;
            for (int i = 0; i < triangles.Count; i++)
            {
                TerrainMeshBaker.TriangleSource t = triangles[i];
                totalArea += Vector3.Cross(t.b - t.a, t.c - t.a).magnitude * 0.5f;
                cumulativeAreas[i] = totalArea;
            }
            if (totalArea <= 0.0001f) return null;

            var vertices = new List<Vector3>(clusterCount * settings.grassBladesPerCluster * 8);
            var uvs = new List<Vector2>(vertices.Capacity);
            var randoms = new List<Vector2>(vertices.Capacity);
            var indices = new List<int>(clusterCount * settings.grassBladesPerCluster * 12);
            System.Random random = new System.Random(unchecked(coordinate.x * 73856093 ^ coordinate.y * 19349663));
            int accepted = 0;
            int attempts = 0;
            int maxAttempts = Mathf.Max(clusterCount * 8, 64);
            while (accepted < clusterCount && attempts++ < maxAttempts)
            {
                float pick = (float)random.NextDouble() * totalArea;
                int triangleIndex = Array.BinarySearch(cumulativeAreas, pick);
                if (triangleIndex < 0) triangleIndex = ~triangleIndex;
                triangleIndex = Mathf.Clamp(triangleIndex, 0, triangles.Count - 1);
                TerrainMeshBaker.TriangleSource triangle = triangles[triangleIndex];
                float r1 = Mathf.Sqrt((float)random.NextDouble());
                float r2 = (float)random.NextDouble();
                Vector3 position = triangle.a * (1f - r1) + triangle.b * (r1 * (1f - r2)) + triangle.c * (r1 * r2);
                Vector3 normal = (triangle.na * (1f - r1) + triangle.nb * (r1 * (1f - r2)) + triangle.nc * (r1 * r2)).normalized;
                float slope = Vector3.Angle(normal, Vector3.up);
                float slopeDensity = 1f - Mathf.InverseLerp(settings.grassFullDensityBelowSlope, settings.grassNoGrassAboveSlope, slope);
                if (slopeDensity <= 0f || random.NextDouble() > slopeDensity) continue;

                accepted++;
                float radius = Mathf.Max(0.05f, settings.grassClusterRadius);
                for (int blade = 0; blade < settings.grassBladesPerCluster; blade++)
                {
                    float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                    float distance = Mathf.Sqrt((float)random.NextDouble()) * radius;
                    Vector3 local = position - origin + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                    float height = settings.grassBladeHeight * (0.72f + (float)random.NextDouble() * 0.56f);
                    float width = height * (0.10f + (float)random.NextDouble() * 0.05f);
                    float yaw = (float)random.NextDouble() * Mathf.PI;
                    float variation = (float)random.NextDouble();
                    AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw, variation);
                    AddBlade(vertices, uvs, randoms, indices, local, height, width, yaw + Mathf.PI * 0.5f, variation * 0.73f + 0.11f);
                }
            }

            if (vertices.Count == 0) return null;
            Mesh mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, randoms);
            mesh.SetTriangles(indices, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddBlade(List<Vector3> vertices, List<Vector2> uvs, List<Vector2> randoms, List<int> indices, Vector3 position, float height, float width, float yaw, float variation)
        {
            Vector3 side = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw)) * width;
            int start = vertices.Count;
            vertices.Add(position - side); vertices.Add(position + side);
            vertices.Add(position + Vector3.up * height);
            uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f)); uvs.Add(new Vector2(0.5f, 1f));
            Vector2 random = new Vector2(Mathf.Repeat(variation, 1f), Mathf.Repeat(variation * 2.17f + 0.37f, 1f));
            randoms.Add(random); randoms.Add(random); randoms.Add(random);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start + 2); indices.Add(start + 1); indices.Add(start);
        }
    }
}
