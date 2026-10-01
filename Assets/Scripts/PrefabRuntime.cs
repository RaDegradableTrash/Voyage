using UnityEngine;
using System;
using System.Collections;

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
        GameObject instance = UnityEngine.Object.Instantiate(template, position, rotation);
        instance.name = instanceName;
        instance.transform.position = position;
        instance.transform.rotation = rotation;
        return instance;
    }

    public static IEnumerator SpawnAsync(string resourceName, string instanceName, Vector3 position,
        Quaternion rotation, Action<GameObject> completed)
    {
        GameObject template;
        if (!prefabCache.TryGetValue(resourceName, out template) || template == null)
        {
            ResourceRequest request = Resources.LoadAsync<GameObject>("Prefabs/" + resourceName);
            yield return request;
            template = request.asset as GameObject;
            if (template != null) prefabCache[resourceName] = template;
        }
        if (template == null)
        {
            Debug.LogError("VOYAGE PREFAB MISSING // Prefabs/" + resourceName + " // requested by " + instanceName);
            completed?.Invoke(null);
            yield break;
        }

        AsyncInstantiateOperation<GameObject> operation = UnityEngine.Object.InstantiateAsync(template, position, rotation);
        // This one-time player spawn is holding up the initial playable state.
        // Give its hierarchy integration a modest per-frame budget so large
        // vehicle prefabs do not leave the world appearing idle for seconds.
        AsyncInstantiateOperation.SetIntegrationTimeMS(4f);
        yield return operation;
        GameObject instance = operation.isDone && operation.Result != null && operation.Result.Length > 0
            ? operation.Result[0]
            : null;
        if (instance == null)
        {
            Debug.LogError("VOYAGE PREFAB FAILED // " + resourceName + " could not be instantiated.");
            completed?.Invoke(null);
            yield break;
        }
        instance.name = instanceName;
        instance.transform.position = position;
        instance.transform.rotation = rotation;
        completed?.Invoke(instance);
    }
}
