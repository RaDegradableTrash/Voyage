using UnityEngine;

namespace Voyage.Lighting
{
    /// <summary>Restores lighting and fog for scenes that do not serialize the systems.</summary>
    public static class LightingSystemBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            GameObject root = GameObject.Find("VOYAGE // LIGHTING SYSTEMS");
            if (root == null) { root = new GameObject("VOYAGE // LIGHTING SYSTEMS"); Object.DontDestroyOnLoad(root); }
            if (root.GetComponent<DayNightSystem>() == null) root.AddComponent<DayNightSystem>();
            if (root.GetComponent<FogSystem>() == null) root.AddComponent<FogSystem>();
            if (root.GetComponent<CloudLightingBridge>() == null) root.AddComponent<CloudLightingBridge>();
        }
    }
}
