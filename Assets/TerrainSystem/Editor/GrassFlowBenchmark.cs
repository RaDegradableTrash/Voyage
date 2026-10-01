using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GrassFlow;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Voyage.TerrainSystem;
using Object = UnityEngine.Object;

// Controlled render comparison; includes GPU synchronization/readback and is not gameplay FPS.
public static class GrassFlowBenchmark
{
    public static void Run() => RunAtResolution(960, 540, "960x540");

    public static void RunHighResolution() => RunAtResolution(1600, 900, "1600x900");

    public static void RunHighResolutionOverhead() => RunAtResolution(1600, 900, "1600x900-overhead", 60f);

    public static void RunNightHighResolutionOverhead() => RunAtResolution(1600, 900, "1600x900-overhead-night", 60f, 22f);

    static void RunAtResolution(int width, int height, string suffix, float drivingPitch = 34f, float timeOfDay = 10f)
    {
        var window = ScriptableObject.CreateInstance<GrassFlow.Editor.GrassFlowPainterWindow>();
        var cameraObject = new GameObject("Grass benchmark camera");
        var camera = cameraObject.AddComponent<Camera>(); camera.tag = "MainCamera";
        cameraObject.AddComponent<FollowCamera>(); // Match the gameplay camera's edge antialiasing.
        var target = new RenderTexture(width, height, 24);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        var sky = new GameObject("Benchmark sky").AddComponent<Voyage.Lighting.DayNightSystem>();
        var legacyObjects = new List<GameObject>(); var legacyAssets = new List<GrassChunkAsset>();
        var legacy = new List<InteractiveGrassTile>();
        var horizonPrefab = Resources.Load<GameObject>("TerrainSystem/Horizon/TerrainHorizon");
        var horizon = horizonPrefab == null ? null : Object.Instantiate(horizonPrefab);
        Action<ScriptableRenderContext, Camera> legacyDraw = null;
        bool scheduled = false;
        try
        {
            window.LoadArea(new Vector3(-24, 0, -24));
            // The fixture contains nine detailed tiles, unlike the live 1 km circle.
            Shader.SetGlobalVector("_VoyageTerrainView",new Vector4(-24,-24,192,256));
            // AddComponent invokes OnEnable automatically on this active object.
            sky.SetTime(timeOfDay);
            var patch = Resources.Load<GrassFlowPatch>("GrassFlow/Tiles/Grass_-1_-1");
            Vector3 point = new Vector3(-24, 0, -24);
            Vector2 uv = new Vector2((point.x - patch.bounds.min.x) / patch.bounds.size.x, (point.z - patch.bounds.min.z) / patch.bounds.size.z);
            point.y = patch.bounds.min.y + patch.surface.GetPixelBilinear(uv.x, uv.y).r * patch.bounds.size.y;
            // Default pitch measures the normal driving LOD; the overhead run
            // exercises the broader canopy used when the player looks down.
            const float cameraDistance = 18f;
            camera.transform.SetPositionAndRotation(
                point + new Vector3(0f, Mathf.Sin(drivingPitch * Mathf.Deg2Rad) * cameraDistance,
                    -Mathf.Cos(drivingPitch * Mathf.Deg2Rad) * cameraDistance),
                Quaternion.Euler(drivingPitch, 0f, 0f));
            camera.targetTexture = target; camera.fieldOfView = 67;
            Directory.CreateDirectory("Logs/GrassFlowValidation");
            double paintedMs = 0;

            // Painter previews use DontSave flags and are intentionally omitted by FindObjectsByType.
            var renderers = Resources.FindObjectsOfTypeAll<GrassFlowRenderer>();
            foreach (var renderer in renderers) renderer.enabled = false;
            double groundMs = 0;


            // Same terrain/camera; reconstruct the former 80k-cluster tile workload from cached surfaces.
            foreach (var renderer in renderers)
            {
                var p = renderer.patch; var go = new GameObject("Legacy comparison grass"); legacyObjects.Add(go);
                var grass = go.AddComponent<InteractiveGrassTile>(); grass.renderInAdditionalCameras = false;
                var asset = ScriptableObject.CreateInstance<GrassChunkAsset>(); legacyAssets.Add(asset);
                asset.clusterMesh = GrassFlowRenderer.BladeMesh(1);
                var positions = new List<Vector3>();
                const int side = 283;
                for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
                {
                    float u = (x + .5f) / side, v = (z + .5f) / side;
                    Color surface = p.surface.GetPixelBilinear(u, v);
                    if (surface.g < .99f) continue;
                    positions.Add(p.bounds.min + new Vector3(u * p.bounds.size.x, surface.r * p.bounds.size.y, v * p.bounds.size.z));
                }
                asset.positions = positions.ToArray(); grass.bakedClusters = asset;
                grass.Initialize(p.bounds); grass.SetLod(0);
                typeof(InteractiveGrassTile).GetField("tileFade", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(grass, 1f);
                typeof(InteractiveGrassTile).GetMethod("ApplyMaterialState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(grass, null);
                legacy.Add(grass);
            }
            var draw = typeof(InteractiveGrassTile).GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            legacyDraw = (_, c) => { if (c == camera) foreach (var grass in legacy) draw.Invoke(grass, null); };
            Action cleanup = () =>
            {
                RenderPipelineManager.beginCameraRendering -= legacyDraw;
                if (horizon != null) Object.DestroyImmediate(horizon);
                Shader.SetGlobalVector("_VoyageTerrainView",Vector4.zero);
                foreach (var go in legacyObjects) Object.DestroyImmediate(go);
                foreach (var asset in legacyAssets) { Object.DestroyImmediate(asset.clusterMesh); Object.DestroyImmediate(asset); }
                camera.targetTexture = null; RenderTexture.active = null;
                Object.DestroyImmediate(sky.gameObject);
                Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(target); Object.DestroyImmediate(image); Object.DestroyImmediate(window);
            };
            int phase = 0, frame = 0;
            var samples = new List<double>();
            foreach (var renderer in renderers) renderer.enabled = true;
            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                try
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    var watch = Stopwatch.StartNew(); camera.Render(); RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); watch.Stop();
                    if (++frame > 12) samples.Add(watch.Elapsed.TotalMilliseconds);
                    if (frame < 27) return;
                    samples.Sort(); double median = samples[samples.Count / 2];
                    string name = phase == 0 ? "painted" : phase == 1 ? "no-grass" : "legacy";
                    File.WriteAllBytes($"Logs/GrassFlowValidation/actual-terrain-{name}-{suffix}.png", image.EncodeToPNG());
                    if (phase == 0) { paintedMs = median; foreach (var renderer in renderers) renderer.enabled = false; }
                    if (phase == 1) { groundMs = median; RenderPipelineManager.beginCameraRendering += legacyDraw; }
                    if (phase == 2)
                    {
                        File.WriteAllText($"Logs/GrassFlowValidation/render-comparison-{suffix}.txt",
                            $"GPU: {SystemInfo.graphicsDeviceName}\n{width}x{height}, {drivingPitch:0}-degree driving camera, 15 samples on separate editor updates after 12 warmup frames per phase. Includes synchronous RGB readback.\nTerrain only: {groundMs:F2} ms\nPainted GrassFlow: {paintedMs:F2} ms\nLegacy reconstruction: {median:F2} ms\nDifferent configured density/draw distances, not equal-density or full gameplay FPS.\n");
                        EditorApplication.update -= update; cleanup(); EditorApplication.Exit(0); return;
                    }
                    phase++; frame = 0; samples.Clear();
                }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); EditorApplication.update -= update; cleanup(); EditorApplication.Exit(1); }
            };
            EditorApplication.update += update;
            scheduled = true;
        }
        finally
        {
            if (!scheduled)
            {
            if (legacyDraw != null) RenderPipelineManager.beginCameraRendering -= legacyDraw;
            foreach (var go in legacyObjects) Object.DestroyImmediate(go);
            foreach (var asset in legacyAssets) { Object.DestroyImmediate(asset.clusterMesh); Object.DestroyImmediate(asset); }
            camera.targetTexture = null; RenderTexture.active = null;
            sky.SendMessage("OnDisable"); Object.DestroyImmediate(sky.gameObject);
            Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(target); Object.DestroyImmediate(image); Object.DestroyImmediate(window);
        }
        }
    }

    // Separate sky-facing test: the ground-facing grass benchmark does not
    // contain enough sky pixels to measure the volumetric cloud pass.
    public static void RunCloudQualityComparison()
    {
        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>("Assets/Settings/PC_Renderer.asset");
        var entries = rendererData == null ? null : new SerializedObject(rendererData).FindProperty("m_RendererFeatures");
        ScriptableRendererFeature clouds = null, contours = null;
        if (entries != null)
        {
            for (int i = 0; i < entries.arraySize; i++)
            {
                var feature = entries.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererFeature;
                if (feature == null) continue;
                if (feature.GetType().Name == "VolumetricCloudFeature") clouds = feature;
                if (feature.GetType().Name == "DistantContourFeature") contours = feature;
            }
        }
        if (clouds == null) throw new InvalidOperationException("PC renderer has no VolumetricCloudFeature.");

        bool cloudWasActive = clouds.isActive;
        bool contourWasActive = contours != null && contours.isActive;
        var cameraObject = new GameObject("Cloud quality comparison camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 67f;
        camera.transform.SetPositionAndRotation(new Vector3(0f, 8f, 0f), Quaternion.Euler(-25f, 0f, 0f));
        var target = new RenderTexture(1600, 900, 24);
        var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        var sky = new GameObject("Cloud comparison sky").AddComponent<Voyage.Lighting.DayNightSystem>();
        var previousTarget = RenderTexture.active;
        Action cleanup = () =>
        {
            clouds.SetActive(cloudWasActive);
            if (contours != null) contours.SetActive(contourWasActive);
            camera.targetTexture = null; RenderTexture.active = previousTarget;
            Object.DestroyImmediate(sky.gameObject); Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(target); Object.DestroyImmediate(image);
        };
        try
        {
            sky.SetTime(10f);
            if (contours != null) contours.SetActive(false);
            clouds.SetActive(cloudWasActive);
            Directory.CreateDirectory("Logs/GrassFlowValidation");
            int phase = 0, frame = 0;
            var samples = new List<double>();
            var cloudTimes = new double[2];
            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                try
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    var watch = Stopwatch.StartNew(); camera.Render(); RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); watch.Stop();
                    if (++frame > 8) samples.Add(watch.Elapsed.TotalMilliseconds);
                    if (frame < 24) return;
                    samples.Sort(); cloudTimes[phase] = samples[samples.Count / 2];
                    File.WriteAllBytes("Logs/GrassFlowValidation/sky-" + (phase == 0 ? "with-clouds" : "without-clouds") + ".png", image.EncodeToPNG());
                    if (phase == 0) clouds.SetActive(false);
                    else
                    {
                        int changed = 0; long sum = 0;
                        Color32[] pixels = image.GetPixels32();
                        var withClouds = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                        try
                        {
                            var bytes = File.ReadAllBytes("Logs/GrassFlowValidation/sky-with-clouds.png");
                            withClouds.LoadImage(bytes);
                            Color32[] cloudPixels = withClouds.GetPixels32();
                            for (int i = 0; i < pixels.Length; i++)
                            {
                                var a = pixels[i]; var b = cloudPixels[i];
                                int difference = Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                                if (difference >= 8) changed++;
                                sum += difference;
                            }
                            var cloudFeature = clouds as Voyage.Rendering.Clouds.VolumetricCloudFeature;
                            if (cloudFeature == null) throw new InvalidOperationException("Cloud renderer feature has an unexpected type.");
                            object cloudSettings = cloudFeature.settings;
                            string settingsSummary = string.Format("scale={0}, primarySteps={1}, lightSteps={2}",
                                cloudSettings.GetType().GetField("resolutionScale").GetValue(cloudSettings),
                                cloudSettings.GetType().GetField("primarySteps").GetValue(cloudSettings),
                                cloudSettings.GetType().GetField("lightSteps").GetValue(cloudSettings));
                            string report = string.Format(
                                "GPU: {0}\nSky-facing camera at 1600x900, 16 samples per state after 8 warmups, synchronous RGB readback.\nCloud settings: {1}.\nClouds enabled: {2:F2} ms\nClouds disabled: {3:F2} ms\nPixels changed by at least 8/255: {4:P1}; mean channel difference: {5:F2}/255.\n",
                                SystemInfo.graphicsDeviceName, settingsSummary, cloudTimes[0], cloudTimes[1],
                                changed / (float)pixels.Length, sum / (3f * pixels.Length));
                            File.WriteAllText("Logs/GrassFlowValidation/cloud-quality-comparison.txt", report);
                        }
                        finally { Object.DestroyImmediate(withClouds); }
                        EditorApplication.update -= update; cleanup(); EditorApplication.Exit(0); return;
                    }
                    phase++; frame = 0; samples.Clear();
                }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); EditorApplication.update -= update; cleanup(); EditorApplication.Exit(1); }
            };
            EditorApplication.update += update;
        }
        catch
        {
            cleanup();
            throw;
        }
    }
}
