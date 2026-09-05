using UnityEngine;
using UnityEngine.Scripting;

namespace Voyage.Lighting
{
    /// <summary>Restores lighting and fog for scenes that do not serialize the systems.</summary>
    [Preserve]
    public static class LightingSystemBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallAfterSceneLoad() => EnsureInstalled();

        static void EnsureInstalled()
        {
            GameObject root = GameObject.Find("VOYAGE // LIGHTING SYSTEMS");
            if (root == null)
            {
                root = new GameObject("VOYAGE // LIGHTING SYSTEMS");
                Object.DontDestroyOnLoad(root);
            }
            if (root.GetComponent<DayNightSystem>() == null) root.AddComponent<DayNightSystem>();
            if (root.GetComponent<FogSystem>() == null) root.AddComponent<FogSystem>();
            if (root.GetComponent<CloudLightingBridge>() == null) root.AddComponent<CloudLightingBridge>();
        }
    }
}
