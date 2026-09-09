using UnityEngine;
using UnityEngine.Scripting;

namespace Voyage.Lighting
{
    /// <summary>Restores lighting and fog for scenes that do not serialize the systems.</summary>
    [Preserve]
    public static class LightingSystemBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallBeforeSceneLoad() => EnsureInstalled();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallAfterSceneLoad() => EnsureInstalled();

        static void EnsureInstalled()
        {
            GameObject root = GameObject.Find("VOYAGE // LIGHTING SYSTEMS");
            DayNightSystem[] systems = Object.FindObjectsByType<DayNightSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (root == null && systems.Length > 0) root = systems[0].gameObject;
            if (root == null)
            {
                root = new GameObject("VOYAGE // LIGHTING SYSTEMS");
                Object.DontDestroyOnLoad(root);
            }
            for (int i = 0; i < systems.Length; i++)
                if (systems[i] != null && systems[i].gameObject != root)
                    Object.Destroy(systems[i].gameObject);
            if (root.GetComponent<DayNightSystem>() == null) root.AddComponent<DayNightSystem>();
            if (root.GetComponent<FogSystem>() == null) root.AddComponent<FogSystem>();
            if (root.GetComponent<CloudLightingBridge>() == null) root.AddComponent<CloudLightingBridge>();
        }
    }
}
