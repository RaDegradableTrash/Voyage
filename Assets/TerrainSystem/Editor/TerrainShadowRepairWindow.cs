#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.TerrainSystem.Editor
{
    public static class TerrainShadowRepairWindow
    {
        [MenuItem("Tools/Voyage/Terrain System/Repair Generated Terrain Shadows")]
        public static void Repair()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/TerrainSystem/GeneratedTiles" });
            int renderers = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (MeshRenderer renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        renderer.shadowCastingMode = ShadowCastingMode.On;
                        renderer.receiveShadows = true;
                        renderer.renderingLayerMask = 1u;
                        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                        renderers++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(prefab, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(prefab); }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Terrain shadow renderer settings applied. Prefabs: {guids.Length}, renderers configured: {renderers}. Original materials were preserved.");
        }
    }
}
#endif
