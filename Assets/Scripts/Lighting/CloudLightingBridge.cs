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
        static readonly int GrassEnvironmentColorId = Shader.PropertyToID("_VoyageGrassEnvironmentColor");
        static readonly int GrassEnvironmentLightId = Shader.PropertyToID("_VoyageGrassEnvironmentLight");

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
            if (dayNight == null) Subscribe();
        }

        void OnDisable()
        {
            if (dayNight != null) dayNight.Changed -= Publish;
            dayNight = null;
            Shader.SetGlobalVector(SunDirectionId, new Vector4(0f, 1f, 0f, 0f));
            Shader.SetGlobalColor(SunColorId, Color.white);
            Shader.SetGlobalColor(AmbientColorId, nightAmbient);
            Shader.SetGlobalFloat(LightId, 0f);
            Shader.SetGlobalColor(GrassEnvironmentColorId, Color.white);
            Shader.SetGlobalFloat(GrassEnvironmentLightId, 1f);
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
            float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.04f, .2f, snapshot.sunHeight));
            Light sun = dayNight != null ? dayNight.sun : RenderSettings.sun;
            Vector3 direction = sun != null ? -sun.transform.forward : Vector3.up;
            Color sunColor = sun != null ? sun.color : Color.white;
            Shader.SetGlobalVector(SunDirectionId, new Vector4(direction.x, direction.y, direction.z, 0f));
            Shader.SetGlobalColor(SunColorId, sunColor);
            Shader.SetGlobalColor(AmbientColorId, Color.Lerp(nightAmbient, dayAmbient, daylight));
            Shader.SetGlobalFloat(LightId, Mathf.Lerp(nightLight, dayLight, daylight));

            // Grass keeps shadow casting and receiving disabled. Instead, it
            // follows the same smooth day/night environment as the rest of
            // the world through a soft colour and brightness adjustment.
            Color nightGrass = new Color(.46f, .54f, .72f, 1f);
            Color dayGrass = new Color(1f, .94f, .80f, 1f);
            Shader.SetGlobalColor(GrassEnvironmentColorId, Color.Lerp(nightGrass, dayGrass, daylight));
            Shader.SetGlobalFloat(GrassEnvironmentLightId, Mathf.Lerp(.48f, 1f, daylight));
        }
    }
}
