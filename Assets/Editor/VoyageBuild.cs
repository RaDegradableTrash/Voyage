using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Keeps the normal editor/import cache alive between builds. A request file is
// consumed once; importing this script alone never starts a build.
[InitializeOnLoad]
public static class VoyageBuild
{
    const string RequestPath = "Library/VoyageBuildRequest.json";
    [Serializable] public class Request { public string output; public string report; public bool clean; }
    [Serializable] public class Step { public string name; public int depth; public double seconds; }
    [Serializable] public class Result
    {
        public string result, output, started;
        public double seconds;
        public double wallSeconds;
        public ulong bytes;
        public int errors, warnings;
        public Step[] steps;
    }
    static VoyageBuild()
    {
        EditorApplication.update += Poll;
        BuildPlayerWindow.RegisterBuildPlayerHandler(options => {
            VoyageTerrainBuildCache.Ensure(options.target);
            BuildPlayerWindow.DefaultBuildMethods.BuildPlayer(options);
        });
    }
    static void Poll()
    {
        if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) { EditorApplication.isPlaying = false; return; }
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
        File.Delete(RequestPath);
        Build(request);
    }
    [MenuItem("Tools/Voyage/Build/Windows prealpha v0.0.4")]
    public static void BuildRelease()
    {
        string previousVersion = PlayerSettings.bundleVersion;
        try
        {
            PlayerSettings.bundleVersion = "0.0.4";
            Build(new Request { output = "Builds/Voyage_prealpha_v0.0.4/Voyage.exe", report = "Logs/prealpha-v0.0.4-build.json" });
        }
        finally { PlayerSettings.bundleVersion = previousVersion; }
    }

    public static void Build(Request request)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.output));
            Directory.CreateDirectory(Path.GetDirectoryName(request.report));
            VoyageTerrainBuildCache.Ensure(BuildTarget.StandaloneWindows64);
            Debug.Log("VOYAGE BUILD START " + DateTime.UtcNow.ToString("O") + " " + request.output);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = request.output, target = BuildTarget.StandaloneWindows64,
                options = request.clean ? BuildOptions.CleanBuildCache : BuildOptions.None });
            var s = report.summary;
            File.WriteAllText(request.report, JsonUtility.ToJson(new Result {
                result = s.result.ToString(), output = s.outputPath, started = s.buildStartedAt.ToString("O"),
                seconds = s.totalTime.TotalSeconds, wallSeconds = timer.Elapsed.TotalSeconds, bytes = s.totalSize, errors = s.totalErrors, warnings = s.totalWarnings,
                steps = report.steps.Select(step => new Step { name = step.name, depth = step.depth, seconds = step.duration.TotalSeconds }).ToArray()
            }, true));
            Debug.Log("VOYAGE BUILD END " + s.result + " " + s.totalTime);
            if (s.result != BuildResult.Succeeded && Application.isBatchMode)
                throw new UnityEditor.Build.BuildFailedException("Player build failed: " + s.result);
        }
        catch (Exception exception)
        {
            File.WriteAllText(request.report, JsonUtility.ToJson(new Result { result = "Failed", output = request.output, started = exception.ToString() }, true));
            Debug.LogException(exception);
            if (Application.isBatchMode) throw;
        }
    }
}
