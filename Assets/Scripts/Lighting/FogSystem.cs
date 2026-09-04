using UnityEngine;

namespace Voyage.Lighting
{
    /// <summary>Vibrant distance atmosphere driven by the shared day/night clock.</summary>
    [DisallowMultipleComponent]
    public sealed class FogSystem : MonoBehaviour
    {
        public static FogSystem Instance { get; private set; }
        public bool enableFog = true;
        [Header("Palette")]
        public Color dayColor = new Color(.43f, .57f, .66f);
        public Color horizonColor = new Color(.95f, .46f, .25f);
        public Color dawnColor = new Color(.72f, .40f, .46f);
        public Color nightColor = new Color(.018f, .028f, .06f);
        [Header("Linear distance range")]
        public float dayStart = 52f;
        public float nightStart = 36f;
        public float dayEnd = 720f;
        public float nightEnd = 560f;
        [Range(0f, 1f)] public float horizonIntensity = .72f;
        [Range(0f, 1f)] public float contribution = 1f;

        DayNightSystem subscribedDayNight;
        bool previousFogEnabled;
        FogMode previousFogMode;
        Color previousFogColor;
        float previousFogStart;
        float previousFogEnd;
        float previousFogDensity;

        public Color CurrentColor { get; private set; }
        public float CurrentStartDistance { get; private set; }
        public float CurrentEndDistance { get; private set; }

        void OnEnable()
        {
            Instance = this;
            previousFogEnabled = RenderSettings.fog;
            previousFogMode = RenderSettings.fogMode;
            previousFogColor = RenderSettings.fogColor;
            previousFogStart = RenderSettings.fogStartDistance;
            previousFogEnd = RenderSettings.fogEndDistance;
            previousFogDensity = RenderSettings.fogDensity;
            TrySubscribe();
            Apply();
        }

        void OnDisable()
        {
            if (subscribedDayNight != null) subscribedDayNight.Changed -= OnLightingChanged;
            subscribedDayNight = null;
            RenderSettings.fog = previousFogEnabled;
            RenderSettings.fogMode = previousFogMode;
            RenderSettings.fogColor = previousFogColor;
            RenderSettings.fogStartDistance = previousFogStart;
            RenderSettings.fogEndDistance = previousFogEnd;
            RenderSettings.fogDensity = previousFogDensity;
            Shader.SetGlobalColor("_VoyageAtmosphereColor", previousFogColor);
            Shader.SetGlobalVector("_VoyageAtmosphereRange", Vector4.zero);
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            // Lighting systems are bootstrapped before scene load, but this
            // also recovers cleanly after domain reloads or component toggles.
            if (subscribedDayNight == null) TrySubscribe();
        }

        void TrySubscribe()
        {
            DayNightSystem dayNight = DayNightSystem.Instance;
            if (dayNight == subscribedDayNight) return;
            if (subscribedDayNight != null) subscribedDayNight.Changed -= OnLightingChanged;
            subscribedDayNight = dayNight;
            if (subscribedDayNight != null) subscribedDayNight.Changed += OnLightingChanged;
        }

        void OnLightingChanged(LightingSnapshot _) => Apply();

        public void Apply()
        {
            DayNightSystem day = DayNightSystem.Instance;
            LightingSnapshot snapshot = day == null
                ? new LightingSnapshot { time = 12f, sunHeight = 1f }
                : day.Snapshot;
            float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.04f, .24f, snapshot.sunHeight));
            float sunsetTime = day != null ? day.sunsetTime : 18f;
            float sunriseTime = day != null ? day.sunriseTime : 6f;
            float sunset = Mathf.Clamp01(1f - ClockDistance(snapshot.time, sunsetTime) / 1.8f);
            float sunrise = Mathf.Clamp01(1f - ClockDistance(snapshot.time, sunriseTime) / 1.5f);
            Color baseColor = Color.Lerp(nightColor, dayColor, daylight);
            Color horizon = Color.Lerp(dawnColor, horizonColor, sunset / Mathf.Max(sunrise + sunset, .0001f));
            float horizonWeight = Mathf.Max(sunrise, sunset) * horizonIntensity * contribution;

            CurrentColor = Color.Lerp(baseColor, horizon, horizonWeight);
            float authoredStart = Mathf.Lerp(nightStart, dayStart, daylight);
            float authoredEnd = Mathf.Lerp(nightEnd, dayEnd, daylight);
            CurrentStartDistance = Mathf.Lerp(authoredEnd, authoredStart, contribution);
            CurrentEndDistance = Mathf.Lerp(100000f, authoredEnd, contribution);
            bool fogActive = enableFog && contribution > .001f;
            RenderSettings.fog = fogActive;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = CurrentColor;
            RenderSettings.fogStartDistance = CurrentStartDistance;
            RenderSettings.fogEndDistance = Mathf.Max(CurrentStartDistance + 1f, CurrentEndDistance);
            Shader.SetGlobalColor("_VoyageAtmosphereColor", CurrentColor);
            Shader.SetGlobalVector("_VoyageAtmosphereRange",
                new Vector4(CurrentStartDistance, CurrentEndDistance, contribution, fogActive ? 1f : 0f));
        }

        public string BuildStatus() => $"Fog enabled={enableFog} contribution={contribution:0.00} color=#{ColorUtility.ToHtmlStringRGB(CurrentColor)} start={RenderSettings.fogStartDistance:0} end={RenderSettings.fogEndDistance:0}";

        static float ClockDistance(float a, float b) =>
            Mathf.Abs(Mathf.DeltaAngle(a * 15f, b * 15f)) / 15f;
    }
}
