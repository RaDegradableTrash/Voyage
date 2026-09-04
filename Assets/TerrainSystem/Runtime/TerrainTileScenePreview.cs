using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>
    /// Editor-only preview of generated tile prefabs. It never participates in builds or runtime streaming.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class TerrainTileScenePreview : MonoBehaviour
    {
        public TerrainTileIndex index;
        public TerrainChunkSettings settings;
        // Loading thousands of prefab instances in the editor is intentionally
        // opt-in. Runtime streaming remains unchanged and is the correct way to
        // inspect the complete world at scale.
        public bool showGeneratedTiles = false;
        [Min(0)] public int maxTiles = 0;

        private readonly Dictionary<Vector2Int, GameObject> instances = new Dictionary<Vector2Int, GameObject>();
        private Transform previewRoot;
        private bool syncing;

        private void OnEnable()
        {
            if (!Application.isPlaying) Sync();
        }

        private void OnDisable()
        {
            if (!Application.isPlaying) Clear();
        }

        public void Sync()
        {
#if UNITY_EDITOR
            if (Application.isPlaying || syncing) return;
            syncing = true;
            try
            {
                if (!showGeneratedTiles || index == null || index.tiles == null)
                {
                    Clear();
                    return;
                }

                EnsureRoot();
                HashSet<Vector2Int> wanted = new HashSet<Vector2Int>();
                int count = 0;
                for (int i = 0; i < index.tiles.Count; i++)
                {
                    TerrainTileRecord record = index.tiles[i];
                    if (record == null || string.IsNullOrEmpty(record.resourcePath)) continue;
                    if (maxTiles > 0 && count >= maxTiles) break;
                    wanted.Add(record.coordinate);
                    if (!instances.TryGetValue(record.coordinate, out GameObject instance) || instance == null)
                    {
                        GameObject prefab = Resources.Load<GameObject>(record.resourcePath);
                        if (prefab == null) continue;
                        instance = UnityEditor.PrefabUtility.InstantiatePrefab(prefab, previewRoot) as GameObject;
                        if (instance == null) continue;
                        instance.name = prefab.name + " [Scene Preview]";
                        instance.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                        instances[record.coordinate] = instance;
                    }

                    instance.transform.SetParent(previewRoot, false);
                    // BuildMesh stores vertices relative to the tile center, so the
                    // prefab root supplies the tile's world-space translation.
                    instance.transform.position = record.bounds.center;
                    instance.transform.rotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;
                    TerrainTileRuntime runtime = instance.GetComponent<TerrainTileRuntime>();
                    if (runtime != null)
                    {
                        runtime.Initialize(record, settings != null ? settings : index.settings, false);
                        runtime.SetCollisionEnabled(false);
                    }
                    count++;
                }

                List<Vector2Int> stale = new List<Vector2Int>();
                foreach (KeyValuePair<Vector2Int, GameObject> pair in instances)
                    if (!wanted.Contains(pair.Key)) stale.Add(pair.Key);
                for (int i = 0; i < stale.Count; i++)
                {
                    if (instances[stale[i]] != null) Object.DestroyImmediate(instances[stale[i]]);
                    instances.Remove(stale[i]);
                }
            }
            finally { syncing = false; }
#endif
        }

        public void Clear()
        {
#if UNITY_EDITOR
            List<GameObject> objects = new List<GameObject>(instances.Values);
            instances.Clear();
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            if (previewRoot != null) Object.DestroyImmediate(previewRoot.gameObject);
            previewRoot = null;
#endif
        }

#if UNITY_EDITOR
        private void EnsureRoot()
        {
            if (previewRoot != null) return;
            Transform existing = transform.Find("__TerrainTileScenePreview");
            previewRoot = existing != null ? existing : new GameObject("__TerrainTileScenePreview").transform;
            previewRoot.SetParent(transform, false);
            previewRoot.gameObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            // Domain reloads clear the runtime dictionary but leave the
            // non-saved preview objects alive. Remove those orphaned children
            // before rebuilding from the authoritative tile index.
            if (instances.Count == 0)
            {
                for (int i = previewRoot.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(previewRoot.GetChild(i).gameObject);
            }
        }
#endif
    }
}
