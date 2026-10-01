using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Unity.Profiling;

namespace Voyage.TerrainSystem
{
    public static class TerrainPrefabStore
    {
        public const string PrefabFolder = "Assets/TerrainSystem/GeneratedTiles/RuntimeTiles";
        public const string BundleName = "voyage-terrain";
        public const string BundleRelativePath = "VoyageTerrain/" + BundleName;
        static AssetBundle bundle;
        static AssetBundleCreateRequest opening;
        static readonly System.Collections.Generic.Dictionary<string, GameObject> prefabCache =
            new System.Collections.Generic.Dictionary<string, GameObject>();
        static readonly ProfilerMarker EditorAssetLoadMarker = new ProfilerMarker("Voyage.Terrain.LoadPrefabAsset");

        public static string AssetPath(string resourcePath) => PrefabFolder + "/" + Path.GetFileName(resourcePath) + ".prefab";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            TerrainCollisionPreparation.Reset();
            if (bundle != null) bundle.Unload(false);
            bundle = null;
            opening = null;
            prefabCache.Clear();
        }

#if UNITY_EDITOR
        public const string EditorBundleSessionKey = "Voyage.ValidatedTerrainBundle";
        public const string EditorBundlePendingKey = "Voyage.TerrainBundleValidationPending";
        public static GameObject LoadInEditor(string resourcePath) =>
            UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(AssetPath(resourcePath));
#endif

        public static IEnumerator LoadAsync(string resourcePath, Action<GameObject> completed)
        {
            string assetPath = AssetPath(resourcePath);
            GameObject cached;
            if (prefabCache.TryGetValue(assetPath, out cached) && cached != null)
            {
                completed(cached);
                yield break;
            }

            if (bundle == null)
            {
#if UNITY_EDITOR
                // Play Mode must not synchronously rebuild thousands of terrain
                // prefabs. Use a validated cache when present; otherwise load
                // individual assets as the world streams them.
                // If Play began before the idle validator finished, keep the
                // loading screen responsive instead of reading terrain assets
                // synchronously on every subsequent streaming boundary.
                while (UnityEditor.SessionState.GetBool(EditorBundlePendingKey, false)) yield return null;
                string path = UnityEditor.SessionState.GetString(EditorBundleSessionKey, "");
                if (string.IsNullOrEmpty(path))
                {
                    GameObject editorPrefab;
                    using (EditorAssetLoadMarker.Auto()) editorPrefab = LoadInEditor(resourcePath);
                    if (editorPrefab == null)
                    {
                        Debug.LogError("Terrain tile is missing from the project: " + resourcePath);
                        completed(null);
                        yield break;
                    }
                    prefabCache[assetPath] = editorPrefab;
                    completed(editorPrefab);
                    yield break;
                }
#else
                string path = Path.Combine(Application.streamingAssetsPath, BundleRelativePath);
#endif
                if (!File.Exists(path))
                {
                    Debug.LogError("Terrain package is missing: " + path);
                    completed(null);
                    yield break;
                }
                if (opening == null) opening = AssetBundle.LoadFromFileAsync(path);
                yield return opening;
                bundle = opening.assetBundle;
                if (bundle == null)
                {
                    Debug.LogError("Could not open terrain package: " + path);
                    completed(null);
                    yield break;
                }
            }
            var request = bundle.LoadAssetAsync<GameObject>(assetPath);
            yield return request;
            var prefab = request.asset as GameObject;
            if (prefab == null) Debug.LogError("Terrain tile is missing from package: " + resourcePath);
            else prefabCache[assetPath] = prefab;
            completed(prefab);
        }
    }
}
