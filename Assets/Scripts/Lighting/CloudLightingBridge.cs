using UnityEngine;

namespace Voyage.Lighting
{
    /// <summary>Publishes the shared day/night lighting state for renderer features.</summary>
    [DisallowMultipleComponent]
    public sealed class CloudLightingBridge : MonoBehaviour
    {
        static readonly int SunDirectionId = Shader.PropertyToID("_VoyageCloudSunDirection");
        static readonly int SunColorId = Shader.PropertyToID("_VoyageCloudSunColor");
        static readonly int AmbientColorId = Shader.PropertyToID("_VoyageCloudAmbientColor");
        static readonly int LightId = Shader.PropertyToID("_VoyageCloudLight");
        static readonly int TwilightId = Shader.PropertyToID("_VoyageCloudTwilight");

        public Color dayAmbient = new Color(.42f, .52f, .62f);
        public Color nightAmbient = new Color(.025f, .035f, .08f);
        [Range(0f, 2f)] public float dayLight = 1f;
        [Range(0f, 1f)] public float nightLight = .08f;

        DayNightSystem dayNight;

        void OnEnable()
        {
            Subscribe();
            Publish(dayNight != null ? dayNight.Snapshot : default);
        }

        void Update()
        {
            if (dayNight != DayNightSystem.Instance)
            {
                Subscribe();
                Publish(dayNight != null ? dayNight.Snapshot : default);
            }
            else if (dayNight == null) Publish(default);
        }

        void OnDisable()
        {
            if (dayNight != null) dayNight.Changed -= Publish;
            dayNight = null;
            Shader.SetGlobalVector(SunDirectionId, new Vector4(0f, 1f, 0f, 0f));
            Shader.SetGlobalColor(SunColorId, Color.white);
            Shader.SetGlobalColor(AmbientColorId, nightAmbient);
            Shader.SetGlobalFloat(LightId, 0f);
            Shader.SetGlobalVector(TwilightId, Vector4.zero);
        }

        void Subscribe()
        {
            DayNightSystem current = DayNightSystem.Instance;
            if (current == dayNight) return;
            if (dayNight != null) dayNight.Changed -= Publish;
            dayNight = current;
            if (dayNight != null) dayNight.Changed += Publish;
        }

        void Publish(LightingSnapshot snapshot)
        {
            Light sun = dayNight != null ? dayNight.sun : RenderSettings.sun;
            float sunHeight = dayNight != null ? snapshot.sunHeight :
                (sun != null ? -sun.transform.forward.y : -1f);
            float daylight = DayNightSystem.EvaluateDaylight(sunHeight);
            Light moon = dayNight != null ? dayNight.moon : null;
            float sunlight = sun != null && sun.enabled ? sun.intensity * dayLight : 0f;
            float moonlight = moon != null && moon.enabled ? moon.intensity * nightLight : 0f;
            bool useSun = sun != null && sunlight >= moonlight;
            Light key = useSun ? sun : moon;
            Vector3 direction = key != null ? -key.transform.forward : Vector3.up;
            Color sunColor = key != null ? key.color : Color.white;
            Shader.SetGlobalVector(SunDirectionId, new Vector4(direction.x, direction.y, direction.z, 0f));
            Shader.SetGlobalColor(SunColorId, sunColor);
            Shader.SetGlobalColor(AmbientColorId, Color.Lerp(nightAmbient, dayAmbient, daylight));
            Shader.SetGlobalFloat(LightId, Mathf.Max(sunlight, moonlight));
            float twilight = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.03f, .35f, sunHeight)))
                * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.18f, .02f, sunHeight));
            Color twilightColor = sun != null ? sun.color : new Color(1f, .36f, .16f);
            Shader.SetGlobalVector(TwilightId, new Vector4(twilightColor.r, twilightColor.g, twilightColor.b, twilight));

        }
    }
}
