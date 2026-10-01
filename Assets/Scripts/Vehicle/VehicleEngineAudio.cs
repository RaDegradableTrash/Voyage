using UnityEngine;

/// <summary>Dedicated continuous engine carrier, isolated from vehicle effects.</summary>
[DisallowMultipleComponent]
public sealed class VehicleEngineAudio : MonoBehaviour
{
    AudioSource source;
    float targetPitch = 0.78f;
    float targetVolume = 0.5f;

    public AudioSource Source => source;

    public void SetState(bool running, float targetRpm, float targetLoad)
    {
        float rpm01 = Mathf.Clamp01(Mathf.Max(0f, targetRpm) / 2800f);
        targetPitch = Mathf.Lerp(0.72f, 1.38f, rpm01);
        // Keep running loudness fixed. Load and RPM are expressed by pitch;
        // feeding the shifting/traction load into volume caused the audible
        // swell that persisted while W was held.
        targetVolume = running ? 0.5f : 0f;
        if (source == null) return;
        source.pitch = Mathf.Lerp(source.pitch, targetPitch, 0.12f);
        source.volume = Mathf.MoveTowards(source.volume, targetVolume,
            Time.unscaledDeltaTime * (running ? 2.5f : 0.7f));
    }

    void Awake()
    {
        int sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        source.priority = 0;
        source.bypassEffects = true;
        source.bypassListenerEffects = true;
        source.bypassReverbZones = true;
        source.volume = 0.5f;
        source.pitch = targetPitch;
        // Keep a normal one-second loop. Some Unity backends virtualize or
        // report a one-sample looping clip as stopped, which makes the caller
        // restart it and produces audible gaps.
        int sampleCount = (int)sampleRate;
        source.clip = AudioClip.Create("Continuous engine carrier", sampleCount, 1, (int)sampleRate, false);
        float[] carrier = new float[sampleCount];
        for (int i = 0; i < carrier.Length; i++)
        {
            float angle = i / (float)sampleRate * Mathf.PI * 2f;
            carrier[i] = (Mathf.Sin(angle * 110f) * 0.52f
                + Mathf.Sin(angle * 220f) * 0.22f
                + Mathf.Sin(angle * 330f) * 0.10f) * 0.32f;
        }
        source.clip.SetData(carrier, 0);
        source.Play();
    }
}
