using System;
using UnityEditor;
using UnityEngine;
using Voyage.TerrainSystem;

// Validate before the domain reload; no synchronous AssetDatabase loads during driving.
[InitializeOnLoad]
public static class VoyageTerrainPlayCache
{
    static VoyageTerrainPlayCache()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode) return;
        SessionState.EraseString(TerrainPrefabStore.EditorBundleSessionKey);
        // Utility/test scenes without the driving loop do not need the world package.
        if (UnityEngine.Object.FindAnyObjectByType<DrivingCore>(FindObjectsInactive.Include) == null) return;
        try
        {
            string path = VoyageTerrainBuildCache.PrepareForEditorPlay();
            SessionState.SetString(TerrainPrefabStore.EditorBundleSessionKey, path);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.isPlaying = false;
        }
    }
}
