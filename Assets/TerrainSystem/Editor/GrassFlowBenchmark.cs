using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using GrassFlow;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Voyage.TerrainSystem;
using Object = UnityEngine.Object;

// Controlled render comparison; includes GPU synchronization/readback and is not gameplay FPS.
public static class GrassFlowBenchmark
{
    public static void Run()
    {
        var window = ScriptableObject.CreateInstance<GrassFlow.Editor.GrassFlowPainterWindow>();
        var cameraObject = new GameObject("Grass benchmark camera");
        var camera = cameraObject.AddComponent<Camera>(); camera.tag = "MainCamera";
        cameraObject.AddComponent<FollowCamera>(); // Match the gameplay camera's edge antialiasing.
        var target = new RenderTexture(960, 540, 24);
        var image = new Texture2D(960, 540, TextureFormat.RGB24, false);
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
            sky.SendMessage("OnEnable"); sky.SetTime(10);
            var patch = Resources.Load<GrassFlowPatch>("GrassFlow/Tiles/Grass_-1_-1");
            Vector3 point = new Vector3(-24, 0, -24);
            Vector2 uv = new Vector2((point.x - patch.bounds.min.x) / patch.bounds.size.x, (point.z - patch.bounds.min.z) / patch.bounds.size.z);
            point.y = patch.bounds.min.y + patch.surface.GetPixelBilinear(uv.x, uv.y).r * patch.bounds.size.y;
            camera.transform.SetPositionAndRotation(point + Vector3.up * 2.5f, Quaternion.LookRotation(new Vector3(.3f, -.12f, .8f)));
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
                sky.SendMessage("OnDisable"); Object.DestroyImmediate(sky.gameObject);
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
                    File.WriteAllBytes("Logs/GrassFlowValidation/actual-terrain-" + name + ".png", image.EncodeToPNG());
                    if (phase == 0) { paintedMs = median; foreach (var renderer in renderers) renderer.enabled = false; }
                    if (phase == 1) { groundMs = median; RenderPipelineManager.beginCameraRendering += legacyDraw; }
                    if (phase == 2)
                    {
                        File.WriteAllText("Logs/GrassFlowValidation/render-comparison.txt",
                            $"GPU: {SystemInfo.graphicsDeviceName}\n960x540, 15 samples on separate editor updates after 12 warmup frames per phase. Includes synchronous RGB readback.\nTerrain only: {groundMs:F2} ms\nPainted GrassFlow: {paintedMs:F2} ms\nLegacy reconstruction: {median:F2} ms\nDifferent configured density/draw distances, not equal-density or full gameplay FPS.\n");
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
}
