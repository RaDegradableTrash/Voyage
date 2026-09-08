using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using GrassFlow;
using UnityEngine;
using Voyage.Lighting;
using Voyage.TerrainSystem;

namespace Voyage.Tests
{
    // Opt-in test of the real game scene/player, including the shipped terrain package.
    // No object or work is created during ordinary gameplay.
    public sealed class BuildSmokeProbe : MonoBehaviour
    {
        readonly StringBuilder report = new StringBuilder();
        bool passed = true;
        string directory;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.isEditor || !Environment.GetCommandLineArgs().Contains("-voyage-validate")) return;
            Application.runInBackground = true;
            DontDestroyOnLoad(new GameObject("Build verification").AddComponent<BuildSmokeProbe>().gameObject);
        }
        void Check(string label, bool valid)
        {
            report.AppendLine(label + ": " + valid);
            passed &= valid;
        }
        IEnumerator Start()
        {
            directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Validation"));
            Directory.CreateDirectory(directory);
            foreach (var tracks in FindObjectsByType<GrassPermanentTrackStore>(FindObjectsSortMode.None))
                tracks.fileName = Path.Combine(directory, "grass-tracks.json");
            float until = Time.realtimeSinceStartup + 120;
            while ((DrivingCore.Instance == null || DrivingCore.Instance.Player == null) && Time.realtimeSinceStartup < until) yield return null;
            var car = DrivingCore.Instance != null ? DrivingCore.Instance.Player : null;
            Check("Vehicle spawned", car != null);
            Check("Terrain package shipped", File.Exists(Path.Combine(Application.streamingAssetsPath, TerrainPrefabStore.BundleRelativePath)));
            var index = Resources.Load<TerrainTileIndex>("TerrainSystem/TerrainTileIndex");
            var bundle = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b => b.name == TerrainPrefabStore.BundleName);
            var names = bundle != null ? bundle.GetAllAssetNames().ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            Check("Every indexed tile packaged", index != null && names != null && index.tiles.All(t => names.Contains(TerrainPrefabStore.AssetPath(t.resourcePath))));
            if (car != null)
            {
                yield return new WaitForSecondsRealtime(5);
                Check("Spawn ground collision", Physics.RaycastAll(car.transform.position + Vector3.up * 5, Vector3.down, 100)
                    .Any(h => h.collider.GetComponentInParent<TerrainTileRuntime>() != null));
                Check("Spawn grass ready", FindObjectsByType<GrassFlowRenderer>(FindObjectsSortMode.None).Any(g => g.Ready));
                var day = DayNightSystem.Instance;
                Check("Day/night bootstrap", day != null);
                if (day != null) { day.advanceTime = false; day.SetTime(12); }
                Check("Fog active", RenderSettings.fog);
                yield return Capture("noon.png");
                if (day != null) day.SetTime(0);
                yield return Capture("night.png");
                if (day != null) day.SetTime(12);
                var far = index.tiles.FirstOrDefault(t => t.coordinate == new Vector2Int(8, 8));
                Check("Distant test tile exists", far != null);
                if (far != null)
                {
                    var control = car.GetComponent<CarControl>();
                    if (control != null) control.enabled = false;
                    var body = car.GetComponent<Rigidbody>();
                    if (body != null) body.isKinematic = true;
                    car.transform.position = new Vector3(far.bounds.center.x, 100, far.bounds.center.z);
                    Physics.SyncTransforms();
                    until = Time.realtimeSinceStartup + 90;
                    TerrainTileRuntime tile = null;
                    while (Time.realtimeSinceStartup < until)
                    {
                        tile = FindObjectsByType<TerrainTileRuntime>(FindObjectsSortMode.None).FirstOrDefault(t => t.Coordinate == far.coordinate && t.CollisionEnabled);
                        if (tile != null) break;
                        yield return null;
                    }
                    Check("Terrain streams after 3km relocation", tile != null);
                    var ground = Physics.RaycastAll(new Vector3(far.bounds.center.x, 1000, far.bounds.center.z), Vector3.down, 2000)
                        .Where(h => h.collider.GetComponentInParent<TerrainTileRuntime>() != null).OrderBy(h => h.distance).ToArray();
                    if (ground.Length > 0)
                    {
                        car.transform.position = ground[0].point + Vector3.up * 3.2f;
                        Physics.SyncTransforms();
                    }
                    else Check("Distant ground collision", false);
                    yield return new WaitForSecondsRealtime(8);
                    Check("Distant grass generated", tile != null && tile.GrassBuildFinished && tile.GetComponentsInChildren<GrassFlowRenderer>().Any(g => g.Ready));
                    yield return Capture("distant.png");
                }
            }
            report.AppendLine("PASS=" + passed);
            File.WriteAllText(Path.Combine(directory, "result.txt"), report.ToString());
            Debug.Log(report.ToString());
            Application.Quit(passed ? 0 : 1);
        }
        IEnumerator Capture(string name)
        {
            yield return new WaitForSecondsRealtime(1);
            var camera = Camera.main;
            if (camera == null) { Check(name + " camera", false); yield break; }
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var target = new RenderTexture(1280, 720, 24);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                // Batch/occluded players have no reliable swap-chain screenshot.
                // Render the actual game camera, renderer features and grass to a texture.
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                pixels.Apply();
                Check(name + " rendered", pixels.GetPixels32().Any(c => c.r > 8 || c.g > 8 || c.b > 8));
                File.WriteAllBytes(Path.Combine(directory, name), pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                Destroy(target); Destroy(pixels);
            }
        }
    }
}
