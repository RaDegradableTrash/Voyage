using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Voyage.TerrainSystem.Editor
{
    public static class TerrainTileIndexRecovery
    {
        private const string IndexPath = "Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/TerrainTileIndex.asset";
        private const string PrefabFolder = "Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/GeneratedTiles";
        private const string SourcePath = "Assets/TerrainSystem/Source/TerrainSource.asset";
        private const string SettingsPath = "Assets/TerrainSystem/Source/TerrainChunkSettings.asset";

        [MenuItem("Tools/Voyage/Terrain System/Rebuild Tile Index From Generated Prefabs")]
        public static void Rebuild()
        {
            TerrainTileIndex index = AssetDatabase.LoadAssetAtPath<TerrainTileIndex>(IndexPath);
            if (index == null)
            {
                index = ScriptableObject.CreateInstance<TerrainTileIndex>();
                AssetDatabase.CreateAsset(index, IndexPath);
            }

            index.source = AssetDatabase.LoadAssetAtPath<TerrainSourceAsset>(SourcePath);
            index.settings = AssetDatabase.LoadAssetAtPath<TerrainChunkSettings>(SettingsPath);
            index.tiles.Clear();

            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
            for (int i = 0; i < paths.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(paths[i]);
                string[] parts = name.Split('_');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y))
                    continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                MeshFilter filter = prefab == null ? null : prefab.transform.Find("LOD0")?.GetComponent<MeshFilter>();
                MeshRenderer renderer = prefab == null ? null : prefab.transform.Find("LOD0")?.GetComponent<MeshRenderer>();
                if (filter == null || filter.sharedMesh == null) continue;
                // The generated mesh is intentionally stored relative to the tile
                // center, so renderer.bounds on the prefab asset is local (usually
                // centered at zero).  The index must carry the tile's world bounds;
                // otherwise ScenePreview places every tile on the same vertical
                // line.  Reconstruct it from the authoritative chunk settings.
                Bounds bounds = index.settings != null
                    ? index.settings.GetTileBounds(new Vector2Int(x, y))
                    : (renderer == null ? new Bounds(prefab.transform.position, Vector3.zero) : renderer.bounds);
                index.tiles.Add(new TerrainTileRecord
                {
                    coordinate = new Vector2Int(x, y),
                    bounds = bounds,
                    resourcePath = "TerrainSystem/GeneratedTiles/" + name,
                    vertexCount = filter.sharedMesh.vertexCount,
                    triangleCount = (int)(filter.sharedMesh.GetIndexCount(0) / 3),
                    materialCount = renderer == null ? 0 : renderer.sharedMaterials.Length,
                    estimatedBytes = 0,
                    hasCollision = prefab.GetComponent<TerrainTileRuntime>() != null,
                    hasHlod = prefab.GetComponent<TerrainTileRuntime>() != null
                });
                if (!Application.isBatchMode && (i & 127) == 0)
                    EditorUtility.DisplayProgressBar("Terrain Tile Index Recovery", "Scanning generated prefabs", (float)i / Mathf.Max(1, paths.Length));
            }

            if (!Application.isBatchMode) EditorUtility.ClearProgressBar();
            index.RebuildLookup();
            EditorUtility.SetDirty(index);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("FBX TERRAIN // recovered tile index with " + index.tiles.Count + " generated prefab records.");
        }
    }
}
