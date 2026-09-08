using System.Text;
using GrassFlow;
using UnityEngine;
using Voyage.Lighting;

namespace Voyage.Tests
{
    // Invoked only by the isolated standalone validation scene.
    public static class RuntimeFeatureProbe
    {
        public static bool Run(Camera camera, RenderTexture target, Texture2D pixels, out string report)
        {
            var lines = new StringBuilder();
            bool passed = true;
            void Check(string name, bool valid)
            {
                lines.AppendLine(name + ": " + valid);
                passed &= valid;
            }
            foreach (string name in new[] { "Voyage/Sky/Gradient", "Hidden/Voyage/VolumetricClouds", "Hidden/Voyage/DistantContours" })
            {
                var shader = Shader.Find(name);
                Check("Player shader " + name, shader != null && shader.isSupported);
            }
            var day = DayNightSystem.Instance;
            day.SetTime(12);
            float sunlight = day.sun.intensity;
            Color ambient = RenderSettings.ambientSkyColor;
            day.SetTime(0);
            Check("Day/night sun and ambient change", day.sun.intensity < sunlight && RenderSettings.ambientSkyColor != ambient);
            Check("Runtime sky material", RenderSettings.skybox != null && RenderSettings.skybox.shader.isSupported);
            day.SetTime(12);
            Check("Terrain index packaged", Resources.Load("TerrainSystem/TerrainTileIndex") != null);
            Check("Distant horizon packaged", Resources.Load<GameObject>("TerrainSystem/Horizon/TerrainHorizon") != null);
            Check("GPU compute available", SystemInfo.supportsComputeShaders);
            var compute = Resources.Load<ComputeShader>("GrassFlow/GrassCompute");
            Check("Grass generation and culling kernels", compute != null && compute.HasKernel("CSMain") && compute.HasKernel("CSCull"));

            camera.transform.SetPositionAndRotation(new Vector3(0, 2, -8), Quaternion.LookRotation(new Vector3(0, -.1f, 1)));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
            var before = pixels.GetPixels32();
            var patch = ScriptableObject.CreateInstance<GrassFlowPatch>();
            patch.bounds = new Bounds(new Vector3(0, .5f, 8), new Vector3(16, 1, 16));
            patch.bladesPerRow = 64;
            patch.surface = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            patch.surface.SetPixels(new[] { Color.green, Color.green, Color.green, Color.green }); patch.surface.Apply();
            patch.density = Texture2D.whiteTexture;
            var host = new GameObject("Standalone grass probe");
            var grass = host.AddComponent<GrassFlowRenderer>(); grass.patch = patch;
            Check("GPU grass buffers generated", grass.PrepareForCamera(camera));
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
            var after = pixels.GetPixels32();
            int changed = 0;
            for (int i = 0; i < after.Length; i++)
                if (Mathf.Abs(after[i].r - before[i].r) + Mathf.Abs(after[i].g - before[i].g) + Mathf.Abs(after[i].b - before[i].b) > 12) changed++;
            Check("Indirect grass visible pixels=" + changed, changed > 100);
            grass.enabled = false;
            Object.Destroy(host); Object.Destroy(patch.surface); Object.Destroy(patch);
            report = lines.ToString();
            return passed;
        }
    }
}
