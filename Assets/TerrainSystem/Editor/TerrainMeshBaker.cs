#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

namespace Voyage.TerrainSystem.Editor
{
    internal static class TerrainMeshBaker
    {
        internal sealed class TriangleSource
        {
            public Vector3 a, b, c;
            public Vector3 na, nb, nc;
            public Vector2 uva, uvb, uvc;
            public Material material;
        }

        private struct ClipVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
            public ClipVertex(Vector3 position, Vector3 normal, Vector2 uv) { this.position = position; this.normal = normal; this.uv = uv; }
        }

        internal sealed class TileData
        {
            public readonly List<TriangleSource> triangles = new List<TriangleSource>();
        }

        public static TerrainSourceAsset Analyze(GameObject source, TerrainSourceAsset descriptor)
        {
            if (source == null) throw new InvalidOperationException("请先拖入 FBX 模型或 FBX Prefab。");
            GameObject copy = InstantiateSource(source);
            try
            {
                MeshFilter[] filters = copy.GetComponentsInChildren<MeshFilter>(true);
                MeshRenderer[] renderers = copy.GetComponentsInChildren<MeshRenderer>(true);
                Bounds bounds = new Bounds(copy.transform.position, Vector3.zero);
                int materials = 0;
                for (int i = 0; i < filters.Length; i++)
                {
                    if (filters[i].sharedMesh == null) continue;
                    Matrix4x4 matrix = filters[i].transform.localToWorldMatrix;
                    Vector3[] vertices = filters[i].sharedMesh.vertices;
                    for (int v = 0; v < vertices.Length; v++) bounds.Encapsulate(matrix.MultiplyPoint3x4(vertices[v]));
                    MeshRenderer renderer = filters[i].GetComponent<MeshRenderer>();
                    if (renderer != null) materials += renderer.sharedMaterials.Length;
                }
                descriptor.sourceObject = source;
                descriptor.sourceAssetPath = AssetDatabase.GetAssetPath(source);
                descriptor.sourceGuid = AssetDatabase.AssetPathToGUID(descriptor.sourceAssetPath);
                descriptor.sourcePosition = source.transform.position;
                descriptor.sourceEulerAngles = source.transform.eulerAngles;
                descriptor.sourceScale = source.transform.lossyScale;
                descriptor.sourceBounds = bounds;
                descriptor.meshCount = filters.Length;
                descriptor.rendererCount = renderers.Length;
                descriptor.materialCount = materials;
                descriptor.importSettingsSnapshot = "Source asset is read through shared Mesh data. Original FBX is never modified.";
                EditorUtility.SetDirty(descriptor);
                AssetDatabase.SaveAssets();
                return descriptor;
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }

        public static TerrainTileIndex Generate(GameObject source, TerrainChunkSettings settings, TerrainSourceAsset descriptor, TerrainTileIndex index, Vector2Int? onlyCoordinate = null)
        {
            Analyze(source, descriptor);
            GameObject copy = InstantiateSource(source);
            try
            {
                Dictionary<Vector2Int, TileData> tiles = CollectTriangles(copy, settings);
                if (!onlyCoordinate.HasValue)
                {
                    AssetDatabase.DeleteAsset("Assets/TerrainSystem/GeneratedLOD");
                    AssetDatabase.DeleteAsset("Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/GeneratedTiles");
                }
                EnsureDirectories();
                GrassPrototypeAsset grassPrototype = settings.bakeGrass ? BuildSharedGrassPrototype(settings) : null;
                index.source = descriptor;
                index.settings = settings;
                if (onlyCoordinate == null) index.tiles.Clear();
                int tileNumber = 0;
                foreach (KeyValuePair<Vector2Int, TileData> pair in tiles)
                {
                    if (onlyCoordinate.HasValue && pair.Key != onlyCoordinate.Value) continue;
                    tileNumber++;
                    if (EditorUtility.DisplayCancelableProgressBar("Terrain Tile Baker", "Generating " + pair.Key, (float)tileNumber / Mathf.Max(1, tiles.Count)))
                        throw new OperationCanceledException("Terrain tile generation cancelled. Existing derived assets are preserved; source FBX was not modified.");
/*
                    if (EditorUtility.DisplayCancelableProgressBar("Terrain Tile Baker", "Generating " + pair.Key, (float)tileNumber / Mathf.Max(1, tiles.Count)))
                        throw new OperationCanceledException("地块生成已取消，已生成的派生资源保留，源 FBX 未被修改。");
                    // disabled duplicate progress block
*/
                    TerrainTileRecord record = BuildTile(pair.Key, pair.Value, settings, grassPrototype);
                    ReplaceRecord(index, record);
                }
                EditorUtility.ClearProgressBar();
                index.RebuildLookup();
                Debug.Log(TerrainTileValidation.Validate(index));
                EditorUtility.SetDirty(index);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return index;
            }
            finally { EditorUtility.ClearProgressBar(); UnityEngine.Object.DestroyImmediate(copy); }
        }

        private static Dictionary<Vector2Int, TileData> CollectTriangles(GameObject root, TerrainChunkSettings settings)
        {
            Dictionary<Vector2Int, TileData> result = new Dictionary<Vector2Int, TileData>();
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int f = 0; f < filters.Length; f++)
            {
                Mesh mesh = filters[f].sharedMesh;
                if (mesh == null) continue;
                MeshRenderer renderer = filters[f].GetComponent<MeshRenderer>();
                Material[] materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
                Matrix4x4 matrix = filters[f].transform.localToWorldMatrix;
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                Vector3[] v = mesh.vertices;
                Vector3[] n = mesh.normals;
                Vector2[] uv = mesh.uv;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    int[] tris = mesh.GetTriangles(sub);
                    Material material = sub < materials.Length ? materials[sub] : null;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        Vector3 a = matrix.MultiplyPoint3x4(v[tris[t]]);
                        Vector3 b = matrix.MultiplyPoint3x4(v[tris[t + 1]]);
                        Vector3 c = matrix.MultiplyPoint3x4(v[tris[t + 2]]);
                        Vector3 centroid = (a + b + c) / 3f;
                        Vector2Int tile = settings.WorldToTile(centroid);
                        if (settings.triangleBoundaryPolicy == TerrainTriangleBoundaryPolicy.ClipDerivedMesh)
                        {
                            Bounds triangleBounds = new Bounds(a, Vector3.zero); triangleBounds.Encapsulate(b); triangleBounds.Encapsulate(c);
                            GetHorizontalTileRange(triangleBounds, settings, out int minX, out int maxX, out int minZ, out int maxZ);
                            for (int x = minX; x <= maxX; x++)
                            for (int z = minZ; z <= maxZ; z++)
                                ClipTriangleIntoTile(result, new Vector2Int(x, z), settings, a, b, c, n, uv, tris, t, normalMatrix, material);
                        }
                        else
                        {
                            AddTriangle(result, tile, a, b, c, n, uv, tris, t, normalMatrix, material);
                        }
                        if (settings.triangleBoundaryPolicy == TerrainTriangleBoundaryPolicy.DuplicateToAdjacentTiles)
                        {
                            Bounds triangleBounds = new Bounds(a, Vector3.zero); triangleBounds.Encapsulate(b); triangleBounds.Encapsulate(c);
                            GetHorizontalTileRange(triangleBounds, settings, out int minX, out int maxX, out int minZ, out int maxZ);
                            for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
                                if (x != tile.x || z != tile.y) AddTriangle(result, new Vector2Int(x, z), a, b, c, n, uv, tris, t, normalMatrix, material);
                        }
                    }
                }
            }
            return result;
        }

        private static void GetHorizontalTileRange(Bounds bounds, TerrainChunkSettings settings, out int minX, out int maxX, out int minZ, out int maxZ)
        {
            float originDepth = settings.horizontalAxes == TerrainHorizontalAxes.XZ ? settings.worldOrigin.z : settings.worldOrigin.y;
            float minDepth = settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.min.z : bounds.min.y;
            float maxDepth = settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.max.z : bounds.max.y;
            minX = Mathf.FloorToInt((bounds.min.x - settings.worldOrigin.x) / settings.tileSize);
            maxX = Mathf.FloorToInt((bounds.max.x - settings.worldOrigin.x) / settings.tileSize);
            minZ = Mathf.FloorToInt((minDepth - originDepth) / settings.tileSize);
            maxZ = Mathf.FloorToInt((maxDepth - originDepth) / settings.tileSize);
        }

        private static void ClipTriangleIntoTile(Dictionary<Vector2Int, TileData> result, Vector2Int tile, TerrainChunkSettings settings, Vector3 a, Vector3 b, Vector3 c, Vector3[] normals, Vector2[] uvs, int[] tris, int offset, Matrix4x4 normalMatrix, Material material)
        {
            ClipVertex va = new ClipVertex(a, normals.Length > tris[offset] ? normalMatrix.MultiplyVector(normals[tris[offset]]).normalized : Vector3.up, uvs.Length > tris[offset] ? uvs[tris[offset]] : Vector2.zero);
            ClipVertex vb = new ClipVertex(b, normals.Length > tris[offset + 1] ? normalMatrix.MultiplyVector(normals[tris[offset + 1]]).normalized : Vector3.up, uvs.Length > tris[offset + 1] ? uvs[tris[offset + 1]] : Vector2.zero);
            ClipVertex vc = new ClipVertex(c, normals.Length > tris[offset + 2] ? normalMatrix.MultiplyVector(normals[tris[offset + 2]]).normalized : Vector3.up, uvs.Length > tris[offset + 2] ? uvs[tris[offset + 2]] : Vector2.zero);
            List<ClipVertex> polygon = new List<ClipVertex> { va, vb, vc };
            Bounds bounds = settings.GetTileBounds(tile, settings.boundaryOverlap);
            polygon = ClipPolygon(polygon, settings, bounds, 0, true);
            polygon = ClipPolygon(polygon, settings, bounds, 0, false);
            polygon = ClipPolygon(polygon, settings, bounds, 1, true);
            polygon = ClipPolygon(polygon, settings, bounds, 1, false);
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                AddTriangle(result, tile, polygon[0].position, polygon[i].position, polygon[i + 1].position, new[] { polygon[0].normal, polygon[i].normal, polygon[i + 1].normal }, new[] { polygon[0].uv, polygon[i].uv, polygon[i + 1].uv }, new[] { 0, 1, 2 }, 0, Matrix4x4.identity, material);
            }
        }

        private static List<ClipVertex> ClipPolygon(List<ClipVertex> input, TerrainChunkSettings settings, Bounds bounds, int axis, bool minimum)
        {
            List<ClipVertex> output = new List<ClipVertex>();
            if (input.Count == 0) return output;
            for (int i = 0; i < input.Count; i++)
            {
                ClipVertex current = input[i];
                ClipVertex previous = input[(i + input.Count - 1) % input.Count];
                float currentValue = HorizontalValue(current.position, settings, axis);
                float previousValue = HorizontalValue(previous.position, settings, axis);
                float boundary = axis == 0 ? (minimum ? (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.min.x : bounds.min.x) : (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.max.x : bounds.max.x)) : (minimum ? (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.min.z : bounds.min.y) : (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? bounds.max.z : bounds.max.y));
                bool currentInside = minimum ? currentValue >= boundary : currentValue <= boundary;
                bool previousInside = minimum ? previousValue >= boundary : previousValue <= boundary;
                if (currentInside != previousInside)
                {
                    float denominator = currentValue - previousValue;
                    float amount = Mathf.Abs(denominator) < 0.000001f ? 0f : (boundary - previousValue) / denominator;
                    output.Add(Interpolate(previous, current, Mathf.Clamp01(amount)));
                }
                if (currentInside) output.Add(current);
            }
            return output;
        }

        private static float HorizontalValue(Vector3 value, TerrainChunkSettings settings, int axis)
        {
            if (axis == 0) return value.x;
            return settings.horizontalAxes == TerrainHorizontalAxes.XZ ? value.z : value.y;
        }

        private static ClipVertex Interpolate(ClipVertex a, ClipVertex b, float t)
        {
            return new ClipVertex(Vector3.Lerp(a.position, b.position, t), Vector3.Slerp(a.normal, b.normal, t).normalized, Vector2.Lerp(a.uv, b.uv, t));
        }

        private static void AddTriangle(Dictionary<Vector2Int, TileData> result, Vector2Int tile, Vector3 a, Vector3 b, Vector3 c, Vector3[] normals, Vector2[] uvs, int[] tris, int offset, Matrix4x4 normalMatrix, Material material)
        {
            if (!result.TryGetValue(tile, out TileData data)) { data = new TileData(); result.Add(tile, data); }
            TriangleSource triangle = new TriangleSource { a = a, b = b, c = c, material = material };
            triangle.na = normals.Length > tris[offset] ? normalMatrix.MultiplyVector(normals[tris[offset]]).normalized : Vector3.up;
            triangle.nb = normals.Length > tris[offset + 1] ? normalMatrix.MultiplyVector(normals[tris[offset + 1]]).normalized : Vector3.up;
            triangle.nc = normals.Length > tris[offset + 2] ? normalMatrix.MultiplyVector(normals[tris[offset + 2]]).normalized : Vector3.up;
            triangle.uva = uvs.Length > tris[offset] ? uvs[tris[offset]] : Vector2.zero;
            triangle.uvb = uvs.Length > tris[offset + 1] ? uvs[tris[offset + 1]] : Vector2.zero;
            triangle.uvc = uvs.Length > tris[offset + 2] ? uvs[tris[offset + 2]] : Vector2.zero;
            data.triangles.Add(triangle);
        }

        private static TerrainTileRecord BuildTile(Vector2Int coordinate, TileData data, TerrainChunkSettings settings, GrassPrototypeAsset grassPrototype)
        {
            string tileName = string.Format(settings.tileNameFormat, coordinate.x, coordinate.y);
            Vector3 tileOrigin = settings.GetTileBounds(coordinate).center;
            Mesh lod0 = BuildMesh(data.triangles, tileOrigin, coordinate, settings, tileName + "_LOD0");
            Mesh lod1 = TerrainLodBuilder.Build(lod0, settings.lod1Quality, coordinate, settings, tileName + "_LOD1");
            Mesh lod2 = TerrainLodBuilder.Build(lod0, settings.lod2Quality, coordinate, settings, tileName + "_LOD2");
            Mesh lod3 = TerrainLodBuilder.Build(lod0, settings.lod3Quality, coordinate, settings, tileName + "_LOD3");
            Mesh[] skirts = settings.generateSkirts ? new Mesh[]
            {
                TerrainSkirtBuilder.Build(lod0, coordinate, settings, settings.skirtDepth, tileName + "_Skirt0"),
                TerrainSkirtBuilder.Build(lod1, coordinate, settings, settings.skirtDepth, tileName + "_Skirt1"),
                TerrainSkirtBuilder.Build(lod2, coordinate, settings, settings.skirtDepth, tileName + "_Skirt2"),
                TerrainSkirtBuilder.Build(lod3, coordinate, settings, settings.skirtDepth, tileName + "_Skirt3")
            } : new Mesh[4];
            string lodFolder = "Assets/TerrainSystem/GeneratedLOD/" + tileName + "/";
            AssetDatabase.DeleteAsset(lodFolder);
            AssetDatabase.DeleteAsset("Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/GeneratedTiles/" + tileName + ".prefab");
            EnsureAssetFolder(lodFolder);
            AssetDatabase.CreateAsset(lod0, lodFolder + tileName + "_LOD0.asset");
            AssetDatabase.CreateAsset(lod1, lodFolder + tileName + "_LOD1.asset");
            AssetDatabase.CreateAsset(lod2, lodFolder + tileName + "_LOD2.asset");
            AssetDatabase.CreateAsset(lod3, lodFolder + tileName + "_LOD3.asset");
            for (int i = 0; i < skirts.Length; i++) if (skirts[i] != null) AssetDatabase.CreateAsset(skirts[i], lodFolder + skirts[i].name + ".asset");
            AssetDatabase.SaveAssets();

            GameObject prefabRoot = new GameObject(tileName);
            TerrainTileRuntime runtime = prefabRoot.AddComponent<TerrainTileRuntime>();
            InteractiveGrassTile grassRuntime = prefabRoot.AddComponent<InteractiveGrassTile>();
            grassRuntime.prototype = grassPrototype;
            grassRuntime.tileCoordinate = coordinate;
            grassRuntime.clusterSpacing = settings.grassClusterSpacing;
            grassRuntime.bladesPerCluster = settings.grassBladesPerCluster;
            grassRuntime.clusterRadius = settings.grassClusterRadius;
            grassRuntime.bladeHeight = settings.grassBladeHeight;
            grassRuntime.density = settings.grassDensity;
            grassRuntime.runtimeClusterBudget = settings.grassClusterBudget;
            grassRuntime.fullDensityBelowSlope = settings.grassFullDensityBelowSlope;
            grassRuntime.noGrassAboveSlope = settings.grassNoGrassAboveSlope;
            List<GameObject> roots = new List<GameObject>();
            Material[] materials = CollectMaterials(data.triangles);
            Mesh[] lods = { lod0, lod1, lod2, lod3 };
            for (int i = 0; i < lods.Length; i++)
            {
                GameObject lodRoot = new GameObject("LOD" + i); lodRoot.transform.SetParent(prefabRoot.transform, false);
                MeshFilter filter = lodRoot.AddComponent<MeshFilter>(); filter.sharedMesh = lods[i];
                MeshRenderer renderer = lodRoot.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials; ConfigureRendererLighting(renderer);
                if (skirts[i] != null)
                {
                    GameObject skirtRoot = new GameObject("Skirt");
                    skirtRoot.transform.SetParent(lodRoot.transform, false);
                    MeshFilter skirtFilter = skirtRoot.AddComponent<MeshFilter>(); skirtFilter.sharedMesh = skirts[i];
                    MeshRenderer skirtRenderer = skirtRoot.AddComponent<MeshRenderer>(); skirtRenderer.sharedMaterials = materials; ConfigureRendererLighting(skirtRenderer);
                }
                roots.Add(lodRoot);
            }
            GameObject collisionRoot = new GameObject("Collision");
            collisionRoot.transform.SetParent(prefabRoot.transform, false);
            MeshCollider collision = collisionRoot.AddComponent<MeshCollider>();
            collision.sharedMesh = lod0;
            collision.convex = false;
            collision.isTrigger = false;
            collision.cookingOptions = MeshColliderCookingOptions.EnableMeshCleaning |
                                        MeshColliderCookingOptions.WeldColocatedVertices |
                                        MeshColliderCookingOptions.CookForFasterSimulation;
            collision.enabled = false;
            SerializedObject serialized = new SerializedObject(runtime);
            SerializedProperty rootsProperty = serialized.FindProperty("lodRoots");
            rootsProperty.arraySize = roots.Count;
            for (int i = 0; i < roots.Count; i++) rootsProperty.GetArrayElementAtIndex(i).objectReferenceValue = roots[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            string prefabPath = "Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/GeneratedTiles/" + tileName + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            UnityEngine.Object.DestroyImmediate(prefabRoot);
            AssetDatabase.SaveAssets();

            Bounds bounds = settings.GetTileBounds(coordinate);
            return new TerrainTileRecord { coordinate = coordinate, bounds = bounds, resourcePath = "TerrainSystem/GeneratedTiles/" + tileName, vertexCount = lod0.vertexCount, triangleCount = (int)lod0.GetIndexCount(0) / 3, materialCount = materials.Length, estimatedBytes = lod0.vertexCount * 32L + lod0.triangles.Length * 4L, hasHlod = settings.generateHlod };
        }

        private static void ConfigureRendererLighting(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
            renderer.allowOcclusionWhenDynamic = true;
        }

        private static Mesh BuildMesh(List<TriangleSource> triangles, Vector3 origin, Vector2Int coordinate, TerrainChunkSettings settings, string name)
        {
            Mesh mesh = new Mesh { name = name };
            List<Vector3> vertices = new List<Vector3>(); List<Vector3> normals = new List<Vector3>(); List<Vector2> uvs = new List<Vector2>(); List<int> indices = new List<int>();
            List<List<int>> submeshIndices = new List<List<int>>();
            List<Material> materials = new List<Material>();
            Dictionary<Vector3Int, int> dedup = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < triangles.Count; i++)
            {
                TriangleSource t = triangles[i];
                Vector3[] worldPositions = { SnapBoundary(t.a, coordinate, settings), SnapBoundary(t.b, coordinate, settings), SnapBoundary(t.c, coordinate, settings) };
                Vector3[] ps = { worldPositions[0] - origin, worldPositions[1] - origin, worldPositions[2] - origin }; Vector3[] ns = { t.na, t.nb, t.nc }; Vector2[] tex = { t.uva, t.uvb, t.uvc };
                int materialIndex = materials.IndexOf(t.material);
                if (materialIndex < 0) { materialIndex = materials.Count; materials.Add(t.material); submeshIndices.Add(new List<int>()); }
                int[] triangleIndices = new int[3];
                for (int v = 0; v < 3; v++)
                {
                    Vector3Int key = new Vector3Int(Mathf.RoundToInt(ps[v].x * 10000f), Mathf.RoundToInt(ps[v].y * 10000f), Mathf.RoundToInt(ps[v].z * 10000f));
                    if (!dedup.TryGetValue(key, out int index)) { index = vertices.Count; dedup[key] = index; vertices.Add(ps[v]); normals.Add(ns[v]); uvs.Add(tex[v]); }
                    triangleIndices[v] = index;
                }
                submeshIndices[materialIndex].Add(triangleIndices[0]); submeshIndices[materialIndex].Add(triangleIndices[1]); submeshIndices[materialIndex].Add(triangleIndices[2]);
            }
            if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.subMeshCount = Mathf.Max(1, submeshIndices.Count);
            for (int sub = 0; sub < submeshIndices.Count; sub++) mesh.SetTriangles(submeshIndices[sub], sub, true);
            if (settings.recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 SnapBoundary(Vector3 world, Vector2Int coordinate, TerrainChunkSettings settings)
        {
            float minX = settings.worldOrigin.x + coordinate.x * settings.tileSize;
            float maxX = minX + settings.tileSize;
            float minDepth = (settings.horizontalAxes == TerrainHorizontalAxes.XZ ? settings.worldOrigin.z : settings.worldOrigin.y) + coordinate.y * settings.tileSize;
            float maxDepth = minDepth + settings.tileSize;
            float epsilon = Mathf.Max(settings.boundaryPositionTolerance, settings.tileSize * 0.0000001f);
            if (Mathf.Abs(world.x - minX) <= epsilon) world.x = minX;
            if (Mathf.Abs(world.x - maxX) <= epsilon) world.x = maxX;
            if (settings.horizontalAxes == TerrainHorizontalAxes.XZ)
            {
                if (Mathf.Abs(world.z - minDepth) <= epsilon) world.z = minDepth;
                if (Mathf.Abs(world.z - maxDepth) <= epsilon) world.z = maxDepth;
                if (Mathf.Abs(world.x - minX) <= epsilon || Mathf.Abs(world.x - maxX) <= epsilon || Mathf.Abs(world.z - minDepth) <= epsilon || Mathf.Abs(world.z - maxDepth) <= epsilon)
                    world.y = Mathf.Round(world.y / settings.boundaryHeightPrecision) * settings.boundaryHeightPrecision;
            }
            else
            {
                if (Mathf.Abs(world.y - minDepth) <= epsilon) world.y = minDepth;
                if (Mathf.Abs(world.y - maxDepth) <= epsilon) world.y = maxDepth;
                if (Mathf.Abs(world.x - minX) <= epsilon || Mathf.Abs(world.x - maxX) <= epsilon || Mathf.Abs(world.y - minDepth) <= epsilon || Mathf.Abs(world.y - maxDepth) <= epsilon)
                    world.z = Mathf.Round(world.z / settings.boundaryHeightPrecision) * settings.boundaryHeightPrecision;
            }
            return world;
        }

        private static Material[] CollectMaterials(List<TriangleSource> triangles)
        {
            List<Material> result = new List<Material>();
            Material fallback = GetFallbackTerrainMaterial();
            for (int i = 0; i < triangles.Count; i++)
            {
                // This baker produces grassland terrain tiles, not a material
                // archive for the source FBX. Use one shared Lit material for
                // every surface so embedded/importer-generated gray materials
                // cannot reintroduce the white bare patches at runtime.
                triangles[i].material = fallback;
                if (triangles[i].material != null && !result.Contains(triangles[i].material)) result.Add(triangles[i].material);
            }
            return result.ToArray();
        }

        private static bool IsDiagnosticTerrainMaterial(Material material)
        {
            if (material == null) return false;
            if (string.Equals(material.name, "TerrainDiagnosticGray", StringComparison.OrdinalIgnoreCase)) return true;
            // The imported source FBX uses TerrainColor for the same neutral
            // placeholder. Treat it as diagnostic too, otherwise white terrain
            // islands survive the bake and fight the grass palette.
            if (string.Equals(material.name, "TerrainColor", StringComparison.OrdinalIgnoreCase)) return true;
            return material.shader != null && string.Equals(material.shader.name, "Hidden/Voyage/LightingDiagnosticWhite", StringComparison.OrdinalIgnoreCase);
        }

        private static Material GetFallbackTerrainMaterial()
        {
            const string path = "Assets/TerrainSystem/Source/TerrainFallbackMaterial.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;
            material = new Material(shader) { name = "Terrain Fallback Material", color = new Color(0.25f, 0.30f, 0.16f, 1f) };
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.15f);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static GameObject InstantiateSource(GameObject source)
        {
            GameObject copy = UnityEngine.Object.Instantiate(source);
            copy.name = "__TerrainSourceAnalysisCopy"; copy.hideFlags = HideFlags.HideAndDontSave;
            return copy;
        }

        private static void ReplaceRecord(TerrainTileIndex index, TerrainTileRecord record)
        {
            for (int i = 0; i < index.tiles.Count; i++) if (index.tiles[i].coordinate == record.coordinate) { index.tiles[i] = record; return; }
            index.tiles.Add(record);
        }

        private static void EnsureDirectories()
        {
            EnsureAssetFolder("Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/GeneratedTiles/");
            EnsureAssetFolder("Assets/TerrainSystem/GeneratedLOD/");
        }

        private static GrassPrototypeAsset BuildSharedGrassPrototype(TerrainChunkSettings settings)
        {
            const string path = "Assets/TerrainSystem/Source/GrassPrototype.asset";
            GrassPrototypeAsset previous = AssetDatabase.LoadAssetAtPath<GrassPrototypeAsset>(path);
            if (previous != null) AssetDatabase.DeleteAsset(path);
            GrassPrototypeAsset prototype = GrassMeshBaker.BuildPrototype(settings, "GrassPrototype");
            if (prototype == null) return null;
            prototype.material = GetGrassMaterial();
            AssetDatabase.CreateAsset(prototype, path);
            if (prototype.clusterMesh != null) AssetDatabase.AddObjectToAsset(prototype.clusterMesh, prototype);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<GrassPrototypeAsset>(path);
        }

        public static void RebuildGrassPrototypeOnly(TerrainChunkSettings settings)
        {
            const string path = "Assets/TerrainSystem/Source/GrassPrototype.asset";
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            GrassPrototypeAsset generated = GrassMeshBaker.BuildPrototype(settings, "GrassPrototype");
            if (generated == null) throw new InvalidOperationException("Grass prototype could not be generated from the current settings.");
            generated.material = GetGrassMaterial();

            GrassPrototypeAsset existing = AssetDatabase.LoadAssetAtPath<GrassPrototypeAsset>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(generated, path);
                if (generated.clusterMesh != null) AssetDatabase.AddObjectToAsset(generated.clusterMesh, generated);
            }
            else
            {
                // Update the existing main asset in place so every generated
                // prefab keeps its serialized reference/GUID.
                Mesh oldMesh = existing.clusterMesh;
                if (oldMesh != null)
                {
                    AssetDatabase.RemoveObjectFromAsset(oldMesh);
                    UnityEngine.Object.DestroyImmediate(oldMesh, true);
                }
                existing.clusterMesh = generated.clusterMesh;
                existing.material = generated.material;
                existing.bladesPerCluster = generated.bladesPerCluster;
                existing.clusterRadius = generated.clusterRadius;
                existing.bladeHeight = generated.bladeHeight;
                if (existing.clusterMesh != null) AssetDatabase.AddObjectToAsset(existing.clusterMesh, existing);
                EditorUtility.SetDirty(existing);
                generated.clusterMesh = null;
                UnityEngine.Object.DestroyImmediate(generated);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static Material GetGrassMaterial()
        {
            const string path = "Assets/TerrainSystem/Source/GrassMaterial.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Voyage/Grass/InteractiveLit");
            if (shader == null) shader = Shader.Find("Voyage/Grass/InteractiveUnlit");
            if (material == null && shader != null)
            {
                material = new Material(shader) { name = "Grass Material" };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material != null)
            {
                if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.20f, 0.28f, 0.105f, 1f));
                if (material.HasProperty("_WindStrength")) material.SetFloat("_WindStrength", 0.32f);
                if (material.HasProperty("_WindSpeed")) material.SetFloat("_WindSpeed", 1.15f);
                if (material.HasProperty("_BendStrength")) material.SetFloat("_BendStrength", 1.15f);
                if (material.HasProperty("_RecoverySpeed")) material.SetFloat("_RecoverySpeed", 1.2f);
                if (material.HasProperty("_AmbientStrength")) material.SetFloat("_AmbientStrength", 0.75f);
                if (material.HasProperty("_DirectLightStrength")) material.SetFloat("_DirectLightStrength", 1f);
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        private static void EnsureAssetFolder(string path)
        {
            string normalized = path.TrimEnd('/'); string[] parts = normalized.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++) { string next = current + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]); current = next; }
        }
    }
}
#endif
