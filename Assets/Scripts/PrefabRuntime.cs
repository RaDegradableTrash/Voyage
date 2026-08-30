using UnityEngine;

public static class PrefabRuntime
{
    static readonly System.Collections.Generic.Dictionary<string, GameObject> prefabCache = new System.Collections.Generic.Dictionary<string, GameObject>();

    public static GameObject Spawn(string resourceName, string instanceName, Vector3 position, Quaternion rotation)
    {
        GameObject template;
        if (!prefabCache.TryGetValue(resourceName, out template) || template == null)
        {
            template = Resources.Load<GameObject>("Prefabs/" + resourceName);
            if (template != null) prefabCache[resourceName] = template;
        }
        if (template == null)
        {
            Debug.LogError("VOYAGE PREFAB MISSING // Prefabs/" + resourceName + " // requested by " + instanceName);
            return null;
        }
        GameObject instance = Object.Instantiate(template, position, rotation);
        instance.name = instanceName;
        instance.transform.position = position;
        instance.transform.rotation = rotation;
        return instance;
    }
}
