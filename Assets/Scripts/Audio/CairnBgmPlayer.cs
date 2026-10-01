using System.Collections;
using UnityEngine;

namespace Voyage.Audio
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class CairnBgmPlayer : MonoBehaviour
    {
        const string PlaylistResourcePath = "Audio/CairnBgmPlaylist";
        const float FadeInSeconds = 2f;
        const float FadeOutSeconds = 5f;
        const float MinimumGapSeconds = 5f;
        const float MaximumGapSeconds = 20f;

        [SerializeField, Range(0f, 1f)] float volume = 0.55f;
        AudioSource source;
        CairnBgmPlaylist playlist;
        Coroutine playback;
        int previousTrack = -1;
        float volumeScale = 1f;
        bool paused;

        public bool IsPaused => paused;
        public bool IsPlaying => source != null && source.isPlaying;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CreateAtRuntime()
        {
            if (FindAnyObjectByType<CairnBgmPlayer>(FindObjectsInactive.Include) != null) return;
            var go = new GameObject("Cairn BGM");
            DontDestroyOnLoad(go);
            go.AddComponent<CairnBgmPlayer>();
        }

        void Awake()
        {
            source = GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            playlist = Resources.Load<CairnBgmPlaylist>(PlaylistResourcePath);
            if (playlist == null || playlist.tracks == null || playlist.tracks.Length == 0)
            {
                Debug.LogWarning("Cairn BGM playlist is missing or empty.");
                return;
            }
            playback = StartCoroutine(PlayPlaylist());
        }

        public void SetVolumePercent(float percent)
        {
            if (float.IsNaN(percent) || float.IsInfinity(percent)) return;
            volumeScale = Mathf.Clamp01(percent / 100f);
            if (source != null && (source.isPlaying || paused))
                source.volume = volume * volumeScale;
        }

        public void PauseMusic()
        {
            if (source == null || !source.isPlaying) return;
            paused = true;
            source.Pause();
        }

        public void ContinueMusic()
        {
            if (!paused || source == null) return;
            paused = false;
            source.UnPause();
        }

        public void NextTrack()
        {
            if (playlist == null || playlist.tracks == null || playlist.tracks.Length == 0) return;
            if (playback != null) StopCoroutine(playback);
            paused = false;
            source.Stop();
            source.clip = null;
            playback = StartCoroutine(PlayPlaylist());
        }

        IEnumerator PlayPlaylist()
        {
            while (true)
            {
                int index = PickNextTrack();
                AudioClip clip = playlist.tracks[index];
                previousTrack = index;
                if (clip == null) continue;

                source.clip = clip;
                source.volume = 0f;
                source.Play();
                yield return Fade(0f, volume, FadeInSeconds);

                float holdSeconds = Mathf.Max(0f, clip.length - FadeInSeconds - FadeOutSeconds);
                yield return WaitWhilePlaying(holdSeconds, false);
                yield return Fade(volume, 0f, Mathf.Min(FadeOutSeconds, clip.length));
                source.Stop();
                source.clip = null;
                yield return WaitWhilePlaying(Random.Range(MinimumGapSeconds, MaximumGapSeconds), true);
            }
        }

        IEnumerator WaitWhilePlaying(float seconds, bool allowPause)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                if (!paused || allowPause) elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        int PickNextTrack()
        {
            int count = playlist.tracks.Length;
            if (count < 2) return 0;
            int index;
            do index = Random.Range(0, count);
            while (index == previousTrack);
            return index;
        }

        IEnumerator Fade(float from, float to, float duration)
        {
            if (duration <= 0f)
            {
                source.volume = to * volumeScale;
                yield break;
            }
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (!paused) elapsed += Time.unscaledDeltaTime;
                source.volume = Mathf.Lerp(from * volumeScale, to * volumeScale, Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            source.volume = to * volumeScale;
        }

        void OnDestroy()
        {
            if (playback != null) StopCoroutine(playback);
        }
    }
}
