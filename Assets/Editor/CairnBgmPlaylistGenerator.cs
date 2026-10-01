#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using Voyage.Audio;

static class CairnBgmPlaylistGenerator
{
    const string SourceFolder = "Assets/SFX/CairnOST";
    const string ResourceFolder = "Assets/Resources/Audio";
    const string AssetPath = ResourceFolder + "/CairnBgmPlaylist.asset";

    [MenuItem("NightRunner/Refresh Cairn BGM Playlist")]
    public static void Refresh()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(ResourceFolder)) AssetDatabase.CreateFolder("Assets/Resources", "Audio");
        var playlist = AssetDatabase.LoadAssetAtPath<CairnBgmPlaylist>(AssetPath);
        if (playlist == null)
        {
            playlist = ScriptableObject.CreateInstance<CairnBgmPlaylist>();
            AssetDatabase.CreateAsset(playlist, AssetPath);
        }

        string[] paths = Directory.Exists(SourceFolder)
            ? Directory.GetFiles(SourceFolder, "*.mp3")
            : new string[0];
        System.Array.Sort(paths, System.StringComparer.OrdinalIgnoreCase);
        var tracks = new AudioClip[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            string assetPath = paths[i].Replace('\\', '/');
            tracks[i] = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }
        playlist.tracks = tracks;
        EditorUtility.SetDirty(playlist);
        AssetDatabase.SaveAssets();
        Debug.Log("Cairn BGM playlist refreshed with " + tracks.Length + " tracks.");
    }
}
#endif
