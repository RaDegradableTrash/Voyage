using UnityEngine;

namespace Voyage.Audio
{
    [CreateAssetMenu(menuName = "Voyage/Audio/Cairn BGM Playlist")]
    public sealed class CairnBgmPlaylist : ScriptableObject
    {
        [Tooltip("Tracks are played in random order without repeating the previous track.")]
        public AudioClip[] tracks;
    }
}
