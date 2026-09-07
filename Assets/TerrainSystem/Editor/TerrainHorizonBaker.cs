#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.TerrainSystem.Editor
{
    /// <summary>Build a collider-free backdrop from existing lowest-detail tiles.</summary>
    public static class TerrainHorizonBaker
    {
        const string Output = "Assets/TerrainSystem/Horizon/Resources/TerrainSystem/Horizon";
        [MenuItem("Voyage/Terrain/Rebuild distant horizon")]
        public static void Bake()
        {
            var index = Resources.Load<TerrainTileIndex>("TerrainSystem/TerrainTileIndex");
            if (index == null) throw new System.InvalidOperationException("Missing terrain index");
            Directory.CreateDirectory(Output);
            AssetDatabase.Refresh();
            var material = new Material(Shader.Find("Voyage/Terrain/Stylized"));
            material.SetFloat("_Horizon", 1);
            material = Save(material, Output + "/Horizon.mat");
            var groups = new Dictionary<Vector2Int, List<CombineInstance>>();
            foreach (var tile in index.tiles)
            {
                string name = $"Terrain_{tile.coordinate.x}_{tile.coordinate.y}";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"Assets/TerrainSystem/GeneratedLOD/{name}/{name}_LOD3.asset");
                if (mesh == null) throw new System.InvalidOperationException("Missing horizon mesh: " + name);
                var cell = new Vector2Int(Mathf.FloorToInt(tile.coordinate.x / 8f), Mathf.FloorToInt(tile.coordinate.y / 8f));
                if (!groups.TryGetValue(cell, out var list)) groups[cell] = list = new List<CombineInstance>();
                // Slightly below detailed terrain: no z-fighting, and distance
                // dithering reveals terrain underneath instead of holes in the sky.
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    list.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub,
                        transform = Matrix4x4.Translate(tile.bounds.center + Vector3.down * 2f) });
            }
            var root = new GameObject("Persistent distant terrain");
            long triangles = 0;
            try
            {
                foreach (var group in groups)
                {
                    var mesh = new Mesh { name = $"Horizon_{group.Key.x}_{group.Key.y}", indexFormat = IndexFormat.UInt32 };
                    mesh.CombineMeshes(group.Value.ToArray(), true, true);
                    triangles += mesh.GetIndexCount(0) / 3;
                    mesh = Save(mesh, Output + "/" + mesh.name + ".asset");
                    var child = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                    child.transform.SetParent(root.transform, false);
                    child.GetComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = child.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                PrefabUtility.SaveAsPrefabAsset(root, Output + "/TerrainHorizon.prefab");
                AssetDatabase.SaveAssets();
                Debug.Log($"HORIZON // {groups.Count} culled regions, {triangles} triangles, {index.tiles.Count} source tiles");
            }
            finally { Object.DestroyImmediate(root); }
        }
        static T Save<T>(T asset, string path) where T : Object
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null) { AssetDatabase.CreateAsset(asset, path); return asset; }
            EditorUtility.CopySerialized(asset, existing); Object.DestroyImmediate(asset);
            return (T)existing;
        }
    }
}
#endif
