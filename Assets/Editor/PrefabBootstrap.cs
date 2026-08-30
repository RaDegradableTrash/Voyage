#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class PrefabBootstrap
{
    // Generates reusable runtime assets and keeps scene prefab paths valid on Windows.
    // Keep this editor bootstrap reloadable so newly added gameplay prefabs are created in-place.
    static PrefabBootstrap()
    {
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            EditorApplication.delayCall += EnsurePrefabs;
    }

    [MenuItem("NightRunner/Generate Prefabs")]
    public static void EnsurePrefabs()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying) return;
        EditorApplication.delayCall -= EnsurePrefabs;
        const string folder = "Assets/Resources/Prefabs";
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Resources", "Prefabs");
        CreatePrimitivePrefab("Cube", PrimitiveType.Cube);
        CreatePrimitivePrefab("Cylinder", PrimitiveType.Cylinder);
        CreatePrimitivePrefab("Sphere", PrimitiveType.Sphere);
        CreatePrimitivePrefab("Capsule", PrimitiveType.Capsule);
        CreateRootPrefab("PlayerCar", true);
        CreateTerrainPrefab();
        CreateVehicleManualPrefab();
        CreateGameRootPrefab();
        ConvertOpenSceneRoots();
        EnsureGameRootInScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void CreatePrimitivePrefab(string name, PrimitiveType type)
    {
        string path = "Assets/Resources/Prefabs/" + name + ".prefab";
        if (File.Exists(path)) return;
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    static void CreateRootPrefab(string name, bool player)
    {
        string path = "Assets/Resources/Prefabs/" + name + ".prefab";
        if (File.Exists(path)) return;
        GameObject go = new GameObject(name);
        if (player)
        {
            var box = go.AddComponent<BoxCollider>(); box.size = new Vector3(2.1f, 0.8f, 4.3f); box.center = new Vector3(0, 0.15f, 0);
            var rb = go.AddComponent<Rigidbody>(); rb.mass = 1250; rb.linearDamping = 0.15f; rb.angularDamping = 3f;
            go.AddComponent<PlayerCar>();
        }
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    static void CreateGameRootPrefab()
    {
        string path = "Assets/Resources/Prefabs/GameRoot.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("VOYAGE // GAME ROOT");
        root.AddComponent<DrivingCore>();
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
    }

    static void CreateTerrainPrefab()
    {
        string path = "Assets/Resources/Prefabs/TerrainTile.prefab";
        if (File.Exists(path)) return;
        GameObject go = new GameObject("TerrainTile");
        go.AddComponent<Terrain>();
        go.AddComponent<TerrainCollider>();
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    static void CreateExplorerPrefab()
    {
        string path = "Assets/Resources/Prefabs/Explorer.prefab";
        if (File.Exists(path)) return;
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "Explorer";
        Object.DestroyImmediate(go.GetComponent<Collider>());
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    static void CreateSupplyCachePrefab()
    {
        string path = "Assets/Resources/Prefabs/SupplyCache.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("SupplyCache");
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = "CacheCrate"; box.transform.SetParent(root.transform); box.transform.localPosition = new Vector3(0, 0.45f, 0); box.transform.localScale = new Vector3(0.85f, 0.65f, 0.85f);
        var beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere); beacon.name = "CacheBeacon"; beacon.transform.SetParent(root.transform); beacon.transform.localPosition = new Vector3(0, 1.2f, 0); beacon.transform.localScale = Vector3.one * 0.22f;
        foreach (var c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        PrefabUtility.SaveAsPrefabAsset(root, path); Object.DestroyImmediate(root);
    }

    static void CreatePinePrefab()
    {
        string path = "Assets/Resources/Prefabs/PineTree.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("PineTree");
        var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder); trunk.name = "Trunk"; trunk.transform.SetParent(root.transform); trunk.transform.localPosition = new Vector3(0, 1.2f, 0); trunk.transform.localScale = new Vector3(0.22f, 1.2f, 0.22f);
        var crown = GameObject.CreatePrimitive(PrimitiveType.Capsule); crown.name = "Needles"; crown.transform.SetParent(root.transform); crown.transform.localPosition = new Vector3(0, 3f, 0); crown.transform.localScale = new Vector3(1.3f, 2.1f, 1.3f);
        foreach (var c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        PrefabUtility.SaveAsPrefabAsset(root, path); Object.DestroyImmediate(root);
    }

    static void CreateCampPrefab()
    {
        string path = "Assets/Resources/Prefabs/Camp.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("Camp");
        var basePart = GameObject.CreatePrimitive(PrimitiveType.Cube); basePart.transform.SetParent(root.transform); basePart.transform.localPosition = new Vector3(0, 0.35f, 0); basePart.transform.localScale = new Vector3(5, 0.7f, 4);
        var tent = GameObject.CreatePrimitive(PrimitiveType.Cube); tent.transform.SetParent(root.transform); tent.transform.localPosition = new Vector3(-0.8f, 1.3f, 0); tent.transform.localScale = new Vector3(2.4f, 1.5f, 2.2f); tent.transform.localRotation = Quaternion.Euler(0, 0, 12);
        foreach (var c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        PrefabUtility.SaveAsPrefabAsset(root, path); Object.DestroyImmediate(root);
    }

    static void CreateBeaconPrefab()
    {
        string path = "Assets/Resources/Prefabs/SummitBeacon.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("SummitBeacon");
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder); pole.transform.SetParent(root.transform); pole.transform.localPosition = new Vector3(0, 3, 0); pole.transform.localScale = new Vector3(0.18f, 3f, 0.18f);
        var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere); cap.transform.SetParent(root.transform); cap.transform.localPosition = new Vector3(0, 6.2f, 0); cap.transform.localScale = Vector3.one * 0.7f;
        foreach (var c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        PrefabUtility.SaveAsPrefabAsset(root, path); Object.DestroyImmediate(root);
    }

    static void CreateRainPrefab()
    {
        string path = "Assets/Resources/Prefabs/RainFX.prefab";
        if (File.Exists(path)) return;
        GameObject root = new GameObject("RainFX");
        var particle = root.AddComponent<ParticleSystem>();
        var main = particle.main;
        main.loop = true; main.playOnAwake = true; main.startLifetime = 1.4f; main.startSpeed = 22f; main.startSize = 0.035f; main.startColor = new Color(0.65f, 0.78f, 1f, 0.7f); main.maxParticles = 900;
        var emission = particle.emission; emission.rateOverTime = 300f;
        var shape = particle.shape; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = new Vector3(28f, 0.1f, 28f);
        var renderer = root.GetComponent<ParticleSystemRenderer>();
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader != null) renderer.material = new Material(shader);
        PrefabUtility.SaveAsPrefabAsset(root, path); Object.DestroyImmediate(root);
    }

    static void CreateFallenLogPrefab()
    {
        string path = "Assets/Resources/Prefabs/FallenLog.prefab";
        if (File.Exists(path)) return;
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        root.name = "FallenLog";
        root.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        root.transform.localScale = new Vector3(0.62f, 3.2f, 0.62f);
        var renderer = root.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader != null)
        {
            var material = new Material(shader);
            material.color = new Color(0.24f, 0.10f, 0.045f);
            material.SetFloat("_Smoothness", 0.28f);
            renderer.material = material;
        }
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
    }

    static void CreateVehicleManualPrefab()
    {
        const string sourcePath = "Assets/Vehicle.fbx";
        const string prefabPath = "Assets/Resources/Prefabs/VehicleManual.prefab";
        if (File.Exists(prefabPath)) return;

        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        if (source == null)
        {
            Debug.LogWarning("PLAYER CAR MODEL // Vehicle.fbx was not found; VehicleManual.prefab was not created.");
            return;
        }

        GameObject root = new GameObject("VehicleManual");
        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(source);
        model.name = "Vehicle FBX";
        model.transform.SetParent(root.transform, false);

        // The FBX contains its own Camera/Light. They must not become active
        // gameplay cameras when this model is used by the vehicle system.
        Camera[] embeddedCameras = model.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < embeddedCameras.Length; i++)
            Object.DestroyImmediate(embeddedCameras[i].gameObject);
        Light[] embeddedLights = model.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < embeddedLights.Length; i++)
            Object.DestroyImmediate(embeddedLights[i].gameObject);

        GameObject markerRoot = new GameObject("WheelMarkers");
        markerRoot.transform.SetParent(root.transform, false);

        Transform frontLeft = CreateWheelMarker(markerRoot.transform, "FrontLeft");
        Transform frontRight = CreateWheelMarker(markerRoot.transform, "FrontRight");
        Transform rearLeft = CreateWheelMarker(markerRoot.transform, "RearLeft");
        Transform rearRight = CreateWheelMarker(markerRoot.transform, "RearRight");

        // Store references to the actual wheel transforms. The old generator
        // left four zero-position marker objects here, which made the runtime
        // suspension rays collapse into one point after regeneration.
        List<Transform> sourceWheels = FindVehicleWheelTransforms(model.transform);
        if (sourceWheels.Count >= 4)
        {
            sourceWheels.Sort((a, b) =>
            {
                Vector3 pa = model.transform.InverseTransformPoint(a.position);
                Vector3 pb = model.transform.InverseTransformPoint(b.position);
                int front = pa.x.CompareTo(pb.x); // Vehicle.fbx front is -X.
                return front != 0 ? front : pa.z.CompareTo(pb.z);
            });
            frontLeft = sourceWheels[0].position.z <= sourceWheels[1].position.z ? sourceWheels[0] : sourceWheels[1];
            frontRight = sourceWheels[0].position.z <= sourceWheels[1].position.z ? sourceWheels[1] : sourceWheels[0];
            rearLeft = sourceWheels[2].position.z <= sourceWheels[3].position.z ? sourceWheels[2] : sourceWheels[3];
            rearRight = sourceWheels[2].position.z <= sourceWheels[3].position.z ? sourceWheels[3] : sourceWheels[2];
        }

        VehicleWheelMarkers layout = root.AddComponent<VehicleWheelMarkers>();
        layout.modelRoot = model.transform;
        layout.frontLeft = frontLeft;
        layout.frontRight = frontRight;
        layout.rearLeft = rearLeft;
        layout.rearRight = rearRight;

        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        Debug.Log("PLAYER CAR MODEL // created manual wheel prefab at " + prefabPath + " with explicit wheel references.");
    }

    static List<Transform> FindVehicleWheelTransforms(Transform modelRoot)
    {
        var result = new List<Transform>();
        Transform[] all = modelRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == modelRoot) continue;
            string name = all[i].name;
            if (!name.StartsWith("Cylinder", System.StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf("Wheel", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (all[i].GetComponentInChildren<Renderer>(true) == null) continue;
            result.Add(all[i]);
        }
        return result;
    }

    static Transform CreateWheelMarker(Transform parent, string name)
    {
        GameObject marker = new GameObject(name);
        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one;
        return marker.transform;
    }

    static void ConvertOpenSceneRoots()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying) return;
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || !scene.path.EndsWith("SampleScene.unity")) return;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (PrefabUtility.IsPartOfPrefabInstance(root)) continue;
            string safeName = SanitizeAssetName(root.name);
            string path = "Assets/Resources/Prefabs/Scene_" + safeName + ".prefab";
            if (!File.Exists(path)) PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.AutomatedAction);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    static void EnsureGameRootInScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying) return;
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || !scene.path.EndsWith("SampleScene.unity")) return;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.GetComponent<DrivingCore>() != null) return;
        }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Prefabs/GameRoot.prefab");
        if (prefab == null) return;
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.name = "VOYAGE // GAME ROOT";
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    static string SanitizeAssetName(string value)
    {
        if (string.IsNullOrEmpty(value)) return "SceneRoot";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') builder.Append(c);
            else builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }
}
#endif
