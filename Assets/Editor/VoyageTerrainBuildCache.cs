using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Voyage.TerrainSystem;
using Debug = UnityEngine.Debug;

public sealed class VoyageTerrainBuildCache : BuildPlayerProcessor
{
    const string IndexPath = "Assets/TerrainSystem/GeneratedTiles/Resources/TerrainSystem/TerrainTileIndex.asset";
    public override int callbackOrder => -1000;
    static string CacheDirectory(BuildTarget target) => "Library/VoyageTerrainBuildCache/" + target;

    // Run from EditorApplication.update, not as one blocking Play Mode callback.
    // The caller budgets MoveNext calls and discards the result if assets change.
    public static IEnumerator ValidatePreparedEditorPlayBundle(Action<string> completed)
    {
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        string directory = CacheDirectory(target);
        string bundle = Path.Combine(directory, TerrainPrefabStore.BundleName);
        string stamp = Path.Combine(directory, "fingerprint.txt");
        if (!File.Exists(bundle) || !File.Exists(stamp)) { completed(null); yield break; }
        string expected = File.ReadAllText(stamp);
        var signature = BeginFingerprint(target, out string[] paths);
        yield return null;
        foreach (string path in paths)
        {
            if (!File.Exists(path)) { completed(null); yield break; }
            signature.Append(path).Append(AssetDatabase.GetAssetDependencyHash(path));
            yield return null;
        }
        string actual = FinishFingerprint(signature, target);
        // A build may have replaced the package while validation was pending.
        completed(actual == expected && File.Exists(bundle) && File.ReadAllText(stamp) == expected
            ? Path.GetFullPath(bundle) : null);
    }

    public static bool TryGetPreparedEditorPlayBundle(out string path)
    {
        path = null;
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        string directory = CacheDirectory(target);
        string bundle = Path.Combine(directory, TerrainPrefabStore.BundleName);
        string stamp = Path.Combine(directory, "fingerprint.txt");
        if (!File.Exists(bundle) || !File.Exists(stamp)) return false;

        try
        {
            if (File.ReadAllText(stamp) != ComputeFingerprint(target, out _)) return false;
            path = Path.GetFullPath(bundle);
            return true;
        }
        catch (Exception error)
        {
            Debug.LogWarning("VOYAGE TERRAIN CACHE // skipping editor bundle validation: " + error.Message);
            return false;
        }
    }

    public static void Ensure(BuildTarget target)
    {
        var timer = Stopwatch.StartNew();
        string fingerprint = ComputeFingerprint(target, out string[] paths);
        string directory = CacheDirectory(target);
        string stamp = Path.Combine(directory, "fingerprint.txt");
        string bundle = Path.Combine(directory, TerrainPrefabStore.BundleName);
        if (File.Exists(bundle) && File.Exists(stamp) && File.ReadAllText(stamp) == fingerprint)
        {
            Debug.Log($"VOYAGE TERRAIN CACHE HIT // {paths.Length} tiles, {timer.Elapsed.TotalSeconds:F2}s");
            return;
        }
        Directory.CreateDirectory(directory);
        // Never mark a failed or interrupted package as reusable.
        if (File.Exists(stamp)) File.Delete(stamp);
        var manifest = BuildPipeline.BuildAssetBundles(directory, new[] {
            new AssetBundleBuild { assetBundleName = TerrainPrefabStore.BundleName, assetNames = paths }
        }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, target);
        if (manifest == null || !File.Exists(bundle)) throw new BuildFailedException("Terrain package build failed.");
        File.WriteAllText(stamp, fingerprint);
        Debug.Log($"VOYAGE TERRAIN CACHE BUILT // {paths.Length} tiles, {timer.Elapsed.TotalSeconds:F2}s, {new FileInfo(bundle).Length} bytes");
    }

    static string ComputeFingerprint(BuildTarget target, out string[] paths)
    {
        var signature = BeginFingerprint(target, out paths);
        foreach (string path in paths)
        {
            if (!File.Exists(path)) throw new BuildFailedException("Terrain prefab is missing: " + path);
            signature.Append(path).Append(AssetDatabase.GetAssetDependencyHash(path));
        }
        return FinishFingerprint(signature, target);
    }

    static StringBuilder BeginFingerprint(BuildTarget target, out string[] paths)
    {
        var index = AssetDatabase.LoadAssetAtPath<TerrainTileIndex>(IndexPath);
        if (index == null || index.tiles == null || index.tiles.Count == 0)
            throw new BuildFailedException("Terrain index is missing or empty.");
        paths = index.tiles.Select(t => TerrainPrefabStore.AssetPath(t.resourcePath)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return new StringBuilder(Application.unityVersion + "|" + target + "|terrain-cache-v1");
    }

    static string FinishFingerprint(StringBuilder signature, BuildTarget target)
    {
        // Shader build rules also affect bundles even when the materials are unchanged.
        signature.Append(File.ReadAllText("ProjectSettings/GraphicsSettings.asset"));
        signature.Append(File.ReadAllText("ProjectSettings/QualitySettings.asset"));
        signature.Append(PlayerSettings.colorSpace);
        signature.Append(string.Join(",", PlayerSettings.GetGraphicsAPIs(target)));
        foreach (string path in Directory.GetFiles("Assets/Settings", "*.asset").OrderBy(p => p, StringComparer.Ordinal))
            signature.Append(path).Append(AssetDatabase.GetAssetDependencyHash(path.Replace('\\', '/')));
        string fingerprint;
        using (var sha = SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(signature.ToString())));
        return fingerprint;
    }

    public override void PrepareForBuild(BuildPlayerContext context)
    {
        string package = Path.Combine(CacheDirectory(context.BuildPlayerOptions.target), TerrainPrefabStore.BundleName);
        if (!File.Exists(package)) throw new BuildFailedException("Use Tools > Voyage > Build to prepare the terrain package before building.");
        // Copy straight into the player: no generated binary in Assets, no reimport.
        context.AddAdditionalPathToStreamingAssets(package, TerrainPrefabStore.BundleRelativePath);
    }
}
