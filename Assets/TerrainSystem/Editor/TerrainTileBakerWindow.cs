#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

namespace Voyage.TerrainSystem.Editor
{
    public sealed class TerrainTileBakerWindow : EditorWindow
    {
        private GameObject sourceObject;
        private TerrainChunkSettings settings;
        private TerrainSourceAsset descriptor;
        private TerrainTileIndex index;
        private Vector2Int selectedCoordinate;
        private string status = "等待 FBX 输入。";
        private Vector2 scroll;

        private const string SettingsPath = "Assets/TerrainSystem/Source/TerrainChunkSettings.asset";
        private const string DescriptorPath = "Assets/TerrainSystem/Source/TerrainSource.asset";
        private const string IndexPath = "Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/TerrainTileIndex.asset";

        [MenuItem("Tools/Voyage/Terrain System/FBX Tile Baker")]
        public static void Open() => GetWindow<TerrainTileBakerWindow>("Terrain Tile Baker");

        private void OnEnable()
        {
            EnsureToolAssets();
            if (index != null && index.tiles != null && index.tiles.Count > 0)
                EnsureScenePreview();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("非破坏性 FBX 地形分块工具。源 FBX 只读，所有 Mesh、LOD 和 Prefab 都写入 TerrainSystem 派生目录。", MessageType.Info);
            sourceObject = (GameObject)EditorGUILayout.ObjectField("FBX / Prefab", sourceObject, typeof(GameObject), false);
            settings = (TerrainChunkSettings)EditorGUILayout.ObjectField("Chunk Settings", settings, typeof(TerrainChunkSettings), false);
            if (settings != null)
                settings.triangleBoundaryPolicy = (TerrainTriangleBoundaryPolicy)EditorGUILayout.EnumPopup("Boundary Cut", settings.triangleBoundaryPolicy);
            descriptor = (TerrainSourceAsset)EditorGUILayout.ObjectField("Source Descriptor", descriptor, typeof(TerrainSourceAsset), false);
            index = (TerrainTileIndex)EditorGUILayout.ObjectField("Tile Index", index, typeof(TerrainTileIndex), false);
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox("配置文件由工具自动维护，不需要手动创建。", MessageType.None);
            using (new EditorGUI.DisabledScope(sourceObject == null || settings == null || descriptor == null || index == null))
            {
                if (GUILayout.Button("Analyze FBX", GUILayout.Height(28f))) RunAnalyze();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Generate All Tiles", GUILayout.Height(28f))) RunGenerate(null);
                    if (GUILayout.Button("Regenerate Tiles", GUILayout.Height(28f))) RunRegenerate();
                }
            }
            using (new EditorGUI.DisabledScope(index == null))
            {
                if (GUILayout.Button("Validate Generated Tiles")) status = TerrainTileValidation.Validate(index);
            }
            if (GUILayout.Button("Clear Generated Assets")) ClearGeneratedAssets();
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(status, MessageType.None);
            if (descriptor != null)
            {
                EditorGUILayout.LabelField("Source bounds", descriptor.sourceBounds.ToString());
                EditorGUILayout.LabelField("Meshes / Renderers / Materials", descriptor.meshCount + " / " + descriptor.rendererCount + " / " + descriptor.materialCount);
            }
            if (index != null)
            {
                EditorGUILayout.LabelField("Generated tiles", index.tiles.Count.ToString());
                for (int i = 0; i < Mathf.Min(index.tiles.Count, 32); i++)
                {
                    TerrainTileRecord tile = index.tiles[i];
                    EditorGUILayout.LabelField(tile.coordinate.ToString(), tile.vertexCount + " verts, " + tile.triangleCount + " tris, " + tile.materialCount + " mats");
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunAnalyze()
        {
            try { TerrainMeshBaker.Analyze(sourceObject, descriptor); status = "分析完成：源 FBX 未被修改。"; Repaint(); }
            catch (Exception exception) { status = exception.Message; Debug.LogException(exception); }
        }

        private void EnsureToolAssets()
        {
            settings = LoadOrCreate<TerrainChunkSettings>(SettingsPath);
            descriptor = LoadOrCreate<TerrainSourceAsset>(DescriptorPath);
            index = LoadOrCreate<TerrainTileIndex>(IndexPath);

            if (sourceObject == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets" });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceObject = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        break;
                    }
                }
            }
            if (sourceObject != null && descriptor != null && descriptor.sourceObject == null)
            {
                descriptor.sourceObject = sourceObject;
                EditorUtility.SetDirty(descriptor);
                AssetDatabase.SaveAssets();
            }
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            asset = CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return asset;
        }

        private void RunGenerate(Vector2Int? coordinate)
        {
            try
            {
                EnsureToolAssets();
                settings.triangleBoundaryPolicy = TerrainTriangleBoundaryPolicy.ClipDerivedMesh;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                TerrainMeshBaker.Generate(sourceObject, settings, descriptor, index, coordinate);
                status = coordinate.HasValue ? "指定地块生成完成。" : "全部地块生成完成。";
                EnsureScenePreview();
                Repaint();
            }
            catch (Exception exception) { status = exception.Message; Debug.LogException(exception); }
        }

        private void RunRegenerate()
        {
            // Regeneration is intentionally a complete, deterministic rebuild. This avoids
            // leaving stale LOD/collision assets behind after source or settings changes.
            RunGenerate(null);
            status = "全部地块已重新生成，Scene 预览已刷新。";
        }

        private void EnsureScenePreview()
        {
            TerrainTileScenePreview preview = FindAnyObjectByType<TerrainTileScenePreview>(FindObjectsInactive.Include);
            if (preview == null)
            {
                GameObject root = new GameObject("Terrain Tile Scene Preview");
                preview = root.AddComponent<TerrainTileScenePreview>();
                Undo.RegisterCreatedObjectUndo(root, "Create Terrain Tile Scene Preview");
            }
            preview.index = index;
            preview.settings = settings;
            preview.showGeneratedTiles = true;
            EditorUtility.SetDirty(preview);
            preview.Sync();
        }

        private void ClearGeneratedAssets()
        {
            if (!EditorUtility.DisplayDialog("Clear TerrainSystem generated assets", "仅删除 TerrainSystem/GeneratedTiles 和 GeneratedLOD，源 FBX 与旧系统不受影响。继续？", "Delete", "Cancel")) return;
            AssetDatabase.DeleteAsset("Assets/TerrainSystem/GeneratedTiles");
            AssetDatabase.DeleteAsset("Assets/TerrainSystem/GeneratedLOD");
            AssetDatabase.Refresh();
            status = "已清理 TerrainSystem 生成目录；源 FBX 未被修改。";
        }

        private static T CreateAsset<T>(string path) where T : ScriptableObject
        {
            string folder = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/TerrainSystem", folder.Substring("Assets/TerrainSystem/".Length));
            T asset = CreateInstance<T>(); AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(path)); AssetDatabase.SaveAssets(); Selection.activeObject = asset; return asset;
        }

        private static void EnsureFolder(string folder)
        {
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
