using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

// Validate an existing package in small idle slices. Never build on entering Play.
[InitializeOnLoad]
public static class VoyageTerrainPlayCache
{
    const string ValidatedVersionKey = "Voyage.TerrainBundleValidatedVersion";
    const string ValidatedTargetKey = "Voyage.TerrainBundleValidatedTarget";
    const string ValidatedStampKey = "Voyage.TerrainBundleValidatedStamp";
    static IEnumerator validation;
    static ulong version;
    static BuildTarget target;
    static string packageStamp;
    static bool checkedInputs;
    static double nextCheck;

    static VoyageTerrainPlayCache()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update += UpdateValidation;
        packageStamp = SessionState.GetString(ValidatedStampKey, "");
        checkedInputs = ulong.TryParse(SessionState.GetString(ValidatedVersionKey, ""), out version)
            && !string.IsNullOrEmpty(packageStamp);
        target = (BuildTarget)SessionState.GetInt(ValidatedTargetKey, (int)BuildTarget.NoTarget);
        // Static enumerators do not survive the domain reload entering Play.
        // A pending validation is restarted there; a completed one stays usable.
        if (SessionState.GetBool(TerrainPrefabStore.EditorBundlePendingKey, false)) BeginValidation();
    }

    static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "missing";
    }

    static string ReadStamp()
    {
        string directory = "Library/VoyageTerrainBuildCache/" + EditorUserBuildSettings.activeBuildTarget;
        // ProjectSettings are not imported assets. Track them explicitly too.
        return FileStamp(Path.Combine(directory, "fingerprint.txt")) + "|" +
            FileStamp(Path.Combine(directory, TerrainPrefabStore.BundleName)) + "|" +
            FileStamp("ProjectSettings/GraphicsSettings.asset") + "|" +
            FileStamp("ProjectSettings/QualitySettings.asset") + "|" + PlayerSettings.colorSpace + "|" +
            string.Join(",", PlayerSettings.GetGraphicsAPIs(EditorUserBuildSettings.activeBuildTarget));
    }

    static void BeginValidation()
    {
        (validation as IDisposable)?.Dispose();
        version = AssetDatabase.GlobalArtifactDependencyVersion;
        target = EditorUserBuildSettings.activeBuildTarget;
        packageStamp = ReadStamp();
        checkedInputs = true;
        SessionState.EraseString(TerrainPrefabStore.EditorBundleSessionKey);
        SessionState.SetBool(TerrainPrefabStore.EditorBundlePendingKey, true);
        validation = VoyageTerrainBuildCache.ValidatePreparedEditorPlayBundle(path =>
        {
            if (version != AssetDatabase.GlobalArtifactDependencyVersion ||
                target != EditorUserBuildSettings.activeBuildTarget || packageStamp != ReadStamp())
            {
                checkedInputs = false;
                path = null;
            }
            if (path != null) SessionState.SetString(TerrainPrefabStore.EditorBundleSessionKey, path);
            SessionState.SetBool(TerrainPrefabStore.EditorBundlePendingKey, false);
            if (checkedInputs)
            {
                SessionState.SetString(ValidatedVersionKey, version.ToString());
                SessionState.SetString(ValidatedStampKey, packageStamp);
                SessionState.SetInt(ValidatedTargetKey, (int)target);
            }
        });
    }

    static void UpdateValidation()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        // Do not swap the streaming backend or invalidate an open bundle mid-run.
        if (!EditorApplication.isPlaying && EditorApplication.timeSinceStartup >= nextCheck)
        {
            nextCheck = EditorApplication.timeSinceStartup + 1;
            if (!checkedInputs || version != AssetDatabase.GlobalArtifactDependencyVersion ||
                target != EditorUserBuildSettings.activeBuildTarget || packageStamp != ReadStamp()) BeginValidation();
        }
        if (validation == null) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            do
            {
                if (!validation.MoveNext())
                {
                    (validation as IDisposable)?.Dispose();
                    validation = null;
                    break;
                }
            } while ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < 2.0);
        }
        catch (Exception error)
        {
            (validation as IDisposable)?.Dispose();
            validation = null;
            SessionState.EraseString(TerrainPrefabStore.EditorBundleSessionKey);
            SessionState.SetBool(TerrainPrefabStore.EditorBundlePendingKey, false);
            UnityEngine.Debug.LogWarning("VOYAGE TERRAIN CACHE // validation skipped: " + error.Message);
        }
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode) return;
        // Utility/test scenes without the driving loop do not need the world package.
        if (UnityEngine.Object.FindAnyObjectByType<DrivingCore>(FindObjectsInactive.Include) == null) return;

        if (!checkedInputs || version != AssetDatabase.GlobalArtifactDependencyVersion ||
            target != EditorUserBuildSettings.activeBuildTarget || packageStamp != ReadStamp()) BeginValidation();
        // Incremental validation continues on editor updates if not already ready.
        // Missing/stale packages fall back to current project assets, never stale terrain.
    }
}
