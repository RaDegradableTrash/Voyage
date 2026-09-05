using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voyage.Lighting
{
    [Serializable]
    public struct LightingSnapshot
    {
        public float time;
        public float sunHeight;
        public float sunIntensity;
        public float moonIntensity;
        public float ambientIntensity;
    }

    /// <summary>Single owner for the scene's sun, moon, ambient light and sky.</summary>
    [DisallowMultipleComponent]
    public sealed class DayNightSystem : MonoBehaviour
    {
        public static DayNightSystem Instance { get; private set; }

        [Header("Clock")]
        [Range(0f, 24f)] public float currentTime = 12f;
        public bool advanceTime = true;
        [Min(1f)] public float dayDuration = 60f;
        public float timeScale = 1f;
        [Range(0f, 24f)] public float sunriseTime = 6f;
        [Range(0f, 24f)] public float sunsetTime = 18f;

        [Header("Managed lights")]
        public Light sun;
        public Light moon;
        [Range(0f, 2f)] public float daySunIntensity = .95f;
        [Range(0f, .2f)] public float nightSunIntensity = .035f;
        [Range(0f, .2f)] public float moonIntensity = .10f;
        public Color daySunColor = new Color(1f, .91f, .78f);
        public Color sunsetSunColor = new Color(.95f, .5f, .3f);
        public Color moonColor = new Color(.42f, .5f, .72f);
        [Range(0f, 1f)] public float sunShadowStrength = .72f;
        [Range(0f, 1f)] public float moonShadowStrength = .3f;

        [Header("Environment")]
        [Range(0f, 1f)] public float dayAmbientIntensity = .34f;
        [Range(0f, 1f)] public float nightAmbientIntensity = .10f;
        [Range(0f, 1f)] public float dayReflectionIntensity = .28f;
        [Range(0f, 1f)] public float nightReflectionIntensity = .015f;
        public Color daySkyColor = new Color(.42f, .52f, .62f);
        public Color horizonSkyColor = new Color(.82f, .34f, .22f);
        public Color nightSkyColor = new Color(.018f, .028f, .06f);
        public bool manageOtherDirectionalLights = true;

        public LightingSnapshot Snapshot { get; private set; }
        public bool IsNight => Snapshot.sunHeight <= 0f;
        public event Action<LightingSnapshot> Changed;
        private Material runtimeSkybox;
        private Material previousSkybox;
        [NonSerialized] GameObject sunVisual;
        [NonSerialized] GameObject moonVisual;
        [NonSerialized] Material sunVisualMaterial;
        [NonSerialized] Material moonVisualMaterial;
        private readonly System.Collections.Generic.List<Light> disabledDirectionalLights = new System.Collections.Generic.List<Light>();
        static readonly int GrassEnvironmentColorId = Shader.PropertyToID("_VoyageGrassEnvironmentColor");
        static readonly int GrassEnvironmentLightId = Shader.PropertyToID("_VoyageGrassEnvironmentLight");
        static readonly int SkySunDirectionId = Shader.PropertyToID("_SunDirection");
        static readonly int SkyMoonDirectionId = Shader.PropertyToID("_MoonDirection");

        void OnEnable()
        {
            Instance = this;
            EnsureLights();
            EnsureSkybox();
            EnsureCamera();
            ConfigureCameras();
            EnsureCelestialVisuals();
            Apply();
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (RenderSettings.skybox == runtimeSkybox) RenderSettings.skybox = previousSkybox;
            if (runtimeSkybox != null)
            {
                if (Application.isPlaying) Destroy(runtimeSkybox);
                else DestroyImmediate(runtimeSkybox);
            }
            runtimeSkybox = null;
            previousSkybox = null;
            if (sunVisual != null) DestroyObject(sunVisual);
            if (moonVisual != null) DestroyObject(moonVisual);
            if (sunVisualMaterial != null) DestroyObject(sunVisualMaterial);
            if (moonVisualMaterial != null) DestroyObject(moonVisualMaterial);
            sunVisual = moonVisual = null;
            sunVisualMaterial = moonVisualMaterial = null;
            for (int i = 0; i < disabledDirectionalLights.Count; i++)
                if (disabledDirectionalLights[i] != null) disabledDirectionalLights[i].enabled = true;
            disabledDirectionalLights.Clear();
        }

        void Update()
        {
            if (Application.isPlaying && advanceTime)
                currentTime = Mathf.Repeat(currentTime + Time.deltaTime * timeScale * 24f / dayDuration, 24f);
            Apply();
        }

        public void SetTime(float value) { currentTime = Mathf.Repeat(value, 24f); Apply(); }

        void EnsureLights()
        {
            if (sun == null)
            {
                Light[] found = FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < found.Length; i++)
                    if (found[i].type == LightType.Directional && found[i].name.IndexOf("Sun", StringComparison.OrdinalIgnoreCase) >= 0) { sun = found[i]; break; }
                if (sun == null) for (int i = 0; i < found.Length; i++) if (found[i].type == LightType.Directional) { sun = found[i]; break; }
            }
            if (sun == null) sun = CreateLight("Voyage Sun", Color.white);
            if (moon == null) moon = CreateLight("Voyage Moon", moonColor);
            Configure(sun, sunShadowStrength);
            Configure(moon, moonShadowStrength);
            if (manageOtherDirectionalLights)
                foreach (Light light in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (light.type == LightType.Directional && light != sun && light != moon && light.enabled) { light.enabled = false; if (!disabledDirectionalLights.Contains(light)) disabledDirectionalLights.Add(light); }
        }

        static Light CreateLight(string name, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(Instance != null ? Instance.transform : null);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            return light;
        }

        static void Configure(Light light, float strength)
        {
            if (light == null) return;
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = strength;
            light.shadowBias = .035f;
            light.shadowNormalBias = .3f;
            light.cullingMask = ~0;
            light.renderMode = LightRenderMode.Auto;
        }

        void Apply()
        {
            Vector3 sunDirection = CalculateSunDirection(currentTime);
            float height = sunDirection.y;
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .18f, height));
            float sunset = Mathf.Clamp01(1f - ClockDistance(currentTime, sunsetTime) / 1.8f);
            float sunrise = Mathf.Clamp01(1f - ClockDistance(currentTime, sunriseTime) / 1.5f);
            float horizonGlow = Mathf.Max(sunrise, sunset);
            Vector3 moonDirection = -sunDirection;
            float sunValue = height <= 0f ? 0f : Mathf.Lerp(nightSunIntensity, daySunIntensity, day);
            float moonValue = Mathf.Lerp(moonIntensity, .006f, day);
            float ambient = Mathf.Lerp(nightAmbientIntensity, dayAmbientIntensity, day);
            float reflection = Mathf.Lerp(nightReflectionIntensity, dayReflectionIntensity, day);
            if (sun != null) { sun.transform.rotation = Quaternion.LookRotation(-sunDirection, Vector3.up); sun.intensity = sunValue; sun.color = Color.Lerp(daySunColor, sunsetSunColor, horizonGlow); sun.enabled = sunValue > .001f; RenderSettings.sun = sun; }
            if (moon != null) { moon.transform.rotation = Quaternion.LookRotation(-moonDirection, Vector3.up); moon.intensity = moonValue; moon.color = moonColor; moon.enabled = moonValue > .001f; }
            Snapshot = new LightingSnapshot { time = currentTime, sunHeight = height, sunIntensity = sunValue, moonIntensity = moonValue, ambientIntensity = ambient };
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Color.Lerp(nightSkyColor, daySkyColor, day);
            RenderSettings.ambientEquatorColor = Color.Lerp(RenderSettings.ambientSkyColor, Color.gray, .45f);
            // Never use a pure-black environment ground. URP Lit surfaces
            // can otherwise become silhouettes whenever the realtime key
            // light is shadowed, even though the material itself is valid.
            RenderSettings.ambientGroundColor = Color.Lerp(
                new Color(.075f, .055f, .025f), new Color(.28f, .27f, .25f), day);
            RenderSettings.ambientIntensity = ambient;
            RenderSettings.reflectionIntensity = reflection;
            ApplySky(day, horizonGlow, sunDirection);
            UpdateCelestialVisuals(sunDirection, moonDirection, sunValue, moonValue);
            ApplyCameraFallbackColor(day, horizonGlow);
            PublishGrassEnvironment(day);
            Changed?.Invoke(Snapshot);
        }

        void PublishGrassEnvironment(float day)
        {
            // Grass never receives or casts realtime shadows. Its appearance
            // follows the independent day/night environment smoothly instead.
            Color nightGrass = new Color(.46f, .54f, .72f, 1f);
            Color dayGrass = new Color(1f, .94f, .80f, 1f);
            Shader.SetGlobalColor(GrassEnvironmentColorId, Color.Lerp(nightGrass, dayGrass, day));
            Shader.SetGlobalFloat(GrassEnvironmentLightId, Mathf.Lerp(.48f, 1f, day));
        }

        void EnsureSkybox()
        {
            if (runtimeSkybox != null) return;
            previousSkybox = RenderSettings.skybox;
            // Use the project shader first. Built-in procedural sky can resolve
            // by name in a player while still being stripped from the active
            // URP renderer, which produces a black background.
            Shader shader = Shader.Find("Voyage/Sky/Gradient");
            if (shader == null) shader = Shader.Find("Skybox/Procedural");
            if (shader == null) return;
            runtimeSkybox = previousSkybox != null && previousSkybox.shader == shader
                ? new Material(previousSkybox)
                : new Material(shader);
            runtimeSkybox.name = "Voyage Runtime Skybox";
        }

        static void ConfigureCameras()
        {
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // A solid fallback keeps the environment visible on URP/DX12
                // paths where DrawSkybox is skipped for a target texture.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
            }
        }

        static void EnsureCamera()
        {
            if (FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length > 0) return;
            GameObject go = new GameObject("Voyage Runtime Camera");
            go.tag = "MainCamera";
            go.transform.SetPositionAndRotation(new Vector3(0f, 4f, -10f), Quaternion.identity);
            Camera camera = go.AddComponent<Camera>();
            camera.fieldOfView = 67f;
            camera.nearClipPlane = .3f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            go.AddComponent<AudioListener>();
            // DrivingCore binds its follow target to Camera.main during startup.
            System.Type followType = System.Type.GetType("FollowCamera, Assembly-CSharp");
            if (followType != null && go.GetComponent(followType) == null) go.AddComponent(followType);
        }

        void EnsureCelestialVisuals()
        {
            if (sunVisual != null && moonVisual != null) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return;
            sunVisualMaterial = new Material(shader) { name = "Voyage Sun Visual" };
            moonVisualMaterial = new Material(shader) { name = "Voyage Moon Visual" };
            SetMaterialColor(sunVisualMaterial, new Color(1f, .72f, .25f, 1f));
            SetMaterialColor(moonVisualMaterial, new Color(.62f, .75f, 1f, 1f));
            sunVisual = CreateCelestialVisual("Voyage Sun Disc", sunVisualMaterial, 42f);
            moonVisual = CreateCelestialVisual("Voyage Moon Disc", moonVisualMaterial, 30f);
        }

        static GameObject CreateCelestialVisual(string name, Material material, float size)
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = name;
            body.transform.localScale = Vector3.one * size;
            body.layer = 0;
            Collider collider = body.GetComponent<Collider>();
            if (collider != null) DestroyObject(collider);
            MeshRenderer renderer = body.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return body;
        }

        static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        void UpdateCelestialVisuals(Vector3 sunDirection, Vector3 moonDirection, float sunIntensity, float moonIntensity)
        {
            Camera camera = Camera.main;
            if (camera == null) camera = FindFirstObjectByType<Camera>();
            if (camera == null || sunVisual == null || moonVisual == null) return;
            Vector3 origin = camera.transform.position;
            // Keep the celestial bodies in the camera's sky hemisphere. The
            // physical direction still drives their elevation and day/night
            // visibility, but a chase camera must not lose them behind its
            // limited horizontal view or behind the terrain.
            sunVisual.transform.position = SkyPosition(camera, sunDirection, 650f);
            moonVisual.transform.position = SkyPosition(camera, moonDirection, 640f);
            sunVisual.SetActive(sunIntensity > .001f);
            moonVisual.SetActive(moonIntensity > .001f);
            float sunScale = Mathf.Lerp(28f, 52f, Mathf.Clamp01(sunIntensity / daySunIntensity));
            sunVisual.transform.localScale = Vector3.one * sunScale;
            moonVisual.transform.localScale = Vector3.one * 30f;
        }

        static Vector3 SkyPosition(Camera camera, Vector3 direction, float distance)
        {
            float horizontal = Vector3.Dot(direction, camera.transform.right);
            float vertical = Vector3.Dot(direction, camera.transform.up);
            horizontal = Mathf.Clamp(horizontal, -.72f, .72f);
            vertical = Mathf.Clamp(vertical, -.18f, .62f);
            return camera.transform.position + camera.transform.forward * distance +
                camera.transform.right * (horizontal * distance * .45f) +
                camera.transform.up * (vertical * distance * .42f);
        }

        void ApplyCameraFallbackColor(float day, float horizonGlow)
        {
            Color color = Color.Lerp(nightSkyColor, daySkyColor, day);
            color = Color.Lerp(color, horizonSkyColor, horizonGlow * .45f);
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = color;
            }
        }

        static void DestroyObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        void ApplySky(float day, float sunset, Vector3 sunDirection)
        {
            if (runtimeSkybox == null) return;
            Color skyColor = Color.Lerp(nightSkyColor, daySkyColor, day);
            skyColor = Color.Lerp(skyColor, horizonSkyColor, sunset * .58f);
            Color groundColor = Color.Lerp(new Color(.075f, .055f, .025f), new Color(.32f, .34f, .35f), day);
            groundColor = Color.Lerp(groundColor, new Color(.48f, .22f, .13f), sunset * .35f);
            if (runtimeSkybox.HasProperty("_SkyTint")) runtimeSkybox.SetColor("_SkyTint", skyColor);
            if (runtimeSkybox.HasProperty("_GroundColor")) runtimeSkybox.SetColor("_GroundColor", groundColor);
            if (runtimeSkybox.HasProperty("_Exposure")) runtimeSkybox.SetFloat("_Exposure", Mathf.Lerp(.08f, .78f, day) + sunset * .08f);
            if (runtimeSkybox.HasProperty(SkySunDirectionId)) runtimeSkybox.SetVector(SkySunDirectionId, new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, 0f));
            if (runtimeSkybox.HasProperty(SkyMoonDirectionId)) runtimeSkybox.SetVector(SkyMoonDirectionId, new Vector4((-sunDirection).x, (-sunDirection).y, (-sunDirection).z, 0f));
            RenderSettings.skybox = runtimeSkybox;
        }

        Vector3 CalculateSunDirection(float time)
        {
            float dayDuration = Mathf.Repeat(sunsetTime - sunriseTime, 24f);
            if (dayDuration < .01f) dayDuration = 12f;
            float nightDuration = Mathf.Max(.01f, 24f - dayDuration);
            float sinceSunrise = Mathf.Repeat(time - sunriseTime, 24f);
            float angle = sinceSunrise <= dayDuration
                ? sinceSunrise / dayDuration * Mathf.PI
                : Mathf.PI + (sinceSunrise - dayDuration) / nightDuration * Mathf.PI;
            return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), .18f).normalized;
        }

        static float ClockDistance(float a, float b) =>
            Mathf.Abs(Mathf.DeltaAngle(a * 15f, b * 15f)) / 15f;
    }
}
