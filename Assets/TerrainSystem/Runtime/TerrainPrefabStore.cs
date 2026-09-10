using System;
using System.Collections;
using System.IO;
using UnityEngine;

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

        public static string AssetPath(string resourcePath) => PrefabFolder + "/" + Path.GetFileName(resourcePath) + ".prefab";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            if (bundle != null) bundle.Unload(false);
            bundle = null;
            opening = null;
            prefabCache.Clear();
        }

#if UNITY_EDITOR
        public static GameObject LoadInEditor(string resourcePath) =>
            UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(AssetPath(resourcePath));
#endif

        public static IEnumerator LoadAsync(string resourcePath, Action<GameObject> completed)
        {
#if UNITY_EDITOR
            // Painting and play mode use the editable source assets directly.
            string editorPath = AssetPath(resourcePath);
            GameObject editorCached;
            if (prefabCache.TryGetValue(editorPath, out editorCached) && editorCached != null)
            {
                completed(editorCached);
                yield break;
            }
            yield return null;
            editorCached = LoadInEditor(resourcePath);
            if (editorCached != null) prefabCache[editorPath] = editorCached;
            completed(editorCached);
#else
            if (bundle == null)
            {
                string path = Path.Combine(Application.streamingAssetsPath, BundleRelativePath);
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
            string assetPath = AssetPath(resourcePath);
            GameObject cached;
            if (prefabCache.TryGetValue(assetPath, out cached) && cached != null)
            {
                completed(cached);
                yield break;
            }
            var request = bundle.LoadAssetAsync<GameObject>(assetPath);
            yield return request;
            var prefab = request.asset as GameObject;
            if (prefab == null) Debug.LogError("Terrain tile is missing from package: " + resourcePath);
            else prefabCache[assetPath] = prefab;
            completed(prefab);
#endif
        }
    }
}
