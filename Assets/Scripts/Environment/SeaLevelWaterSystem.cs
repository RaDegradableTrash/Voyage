using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voyage.Wind;

namespace Voyage.Environment
{
    /// <summary>
    /// A world-anchored ocean at sea level. A GPU height field carries collision
    /// ripples near the player; the radial mesh becomes coarser toward the horizon.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SeaLevelWaterSystem : MonoBehaviour
    {
        const int MaxRippleImpulses = 16;
        const int RippleTextureSize = 512;
        const float RippleFieldHalfSize = 64f;
        const float RippleFieldRecenterStep = 2f;

        static readonly int SeaLevelId = Shader.PropertyToID("_SeaLevel");
        static readonly int WaterWindId = Shader.PropertyToID("_WaterWind");
        static readonly int WaterRippleTextureId = Shader.PropertyToID("_WaterRippleTex");
        static readonly int WaterRippleCenterId = Shader.PropertyToID("_WaterRippleCenter");
        static readonly int RipplePreviousId = Shader.PropertyToID("_PreviousTex");
        static readonly int RippleCurrentId = Shader.PropertyToID("_CurrentTex");
        static readonly int RippleDampingId = Shader.PropertyToID("_Damping");
        static readonly int RippleScrollOffsetId = Shader.PropertyToID("_ScrollOffset");
        static readonly int RippleImpulseArrayId = Shader.PropertyToID("_RippleImpulses");
        static readonly int RippleImpulseCountId = Shader.PropertyToID("_RippleImpulseCount");
        static readonly int WaterCloudWindId = Shader.PropertyToID("_WaterCloudWind");
        static readonly int WaterCloudParametersId = Shader.PropertyToID("_WaterCloudParameters");
        static readonly int WaterCloudDensityId = Shader.PropertyToID("_WaterCloudDensity");
        static readonly int SkyTintId = Shader.PropertyToID("_WaterSkyTint");
        static readonly int HorizonTintId = Shader.PropertyToID("_WaterHorizonTint");
        static readonly int GroundColorId = Shader.PropertyToID("_WaterGroundColor");
        static readonly int SunColorId = Shader.PropertyToID("_WaterSunColor");
        static readonly int SunDirectionId = Shader.PropertyToID("_WaterSunDirection");
        static readonly int MoonDirectionId = Shader.PropertyToID("_WaterMoonDirection");
        static readonly int ExposureId = Shader.PropertyToID("_WaterExposure");
        static readonly int StarIntensityId = Shader.PropertyToID("_WaterStarIntensity");

        static readonly float[] FarRingRadii =
        {
            80f, 96f, 128f, 192f, 256f, 384f, 512f, 768f, 1024f,
            2048f, 4096f, 8192f, 16384f, 24000f
        };

        static readonly Color DefaultSky = new Color(.42f, .52f, .62f, 1f);
        static readonly Color DefaultHorizon = new Color(.65f, .74f, .80f, 1f);
        static readonly Color DefaultGround = new Color(.32f, .34f, .35f, 1f);

        public static SeaLevelWaterSystem Instance { get; private set; }

        [Tooltip("World-space sea surface height relative to the terrain's Y=0 datum.")]
        public float seaLevel = -600f;
        [Tooltip("Water shader assigned by the scene prefab; Resources/Shader.Find remain fallbacks.")]
        public Shader waterShader;
        [Tooltip("Far ocean extent. Distant vertices are projected inside the camera far plane to reach the horizon.")]
        [Min(1000f)] public float renderRadius = 1000000f;
        [Tooltip("Cloud altitude range copied by the reflected sky. Keep this in sync with the PC cloud renderer.")]
        public Vector2 reflectedCloudHeights = new Vector2(180f, 980f);
        [Min(.0001f)] public float reflectedCloudNoiseScale = .00125f;
        [Range(0f, 1f)] public float reflectedCloudCoverage = .64f;
        [Range(0f, 4f)] public float reflectedCloudDensity = 1.6f;
        [Header("Collision Ripples")]
        [Tooltip("The trigger follows the player and listens for rigidbodies entering the water surface.")]
        [Min(8f)] public float collisionInteractionRadius = 64f;
        [Min(.25f)] public float collisionTriggerDepth = 4f;
        [Range(.90f, .999f)] public float rippleDamping = .992f;
        [Min(.05f)] public float collisionStampInterval = .14f;

        GameObject surfaceObject;
        Transform surfaceTransform;
        Mesh surfaceMesh;
        Material waterMaterial;
        Material rippleStepMaterial;
        Material rippleImpulseMaterial;
        Material rippleScrollMaterial;
        RenderTexture ripplePrevious;
        RenderTexture rippleCurrent;
        RenderTexture rippleScratch;
        BoxCollider collisionTrigger;
        WaterCollisionRelay collisionRelay;
        Transform followTarget;
        readonly List<Vector4> pendingImpulses = new List<Vector4>(MaxRippleImpulses);
        readonly Dictionary<EntityId, float> nextStampTimeByBody = new Dictionary<EntityId, float>();
        readonly Vector4[] rippleImpulses = new Vector4[MaxRippleImpulses];
        Vector2 rippleCenter;
        float nextTargetSearch;
        bool rippleFieldInitialized;

        void OnEnable()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }

            Instance = this;
            CreateSurface();
            CreateRippleSimulation();
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (surfaceObject != null) Destroy(surfaceObject);
            if (surfaceMesh != null) Destroy(surfaceMesh);
            if (waterMaterial != null) Destroy(waterMaterial);
            if (rippleStepMaterial != null) Destroy(rippleStepMaterial);
            if (rippleImpulseMaterial != null) Destroy(rippleImpulseMaterial);
            if (rippleScrollMaterial != null) Destroy(rippleScrollMaterial);
            ReleaseRippleTexture(ref ripplePrevious);
            ReleaseRippleTexture(ref rippleCurrent);
            ReleaseRippleTexture(ref rippleScratch);
            surfaceObject = null;
            surfaceTransform = null;
            surfaceMesh = null;
            waterMaterial = null;
            rippleStepMaterial = null;
            rippleImpulseMaterial = null;
            rippleScrollMaterial = null;
            collisionTrigger = null;
            collisionRelay = null;
            pendingImpulses.Clear();
            nextStampTimeByBody.Clear();
            rippleFieldInitialized = false;
        }

        void LateUpdate()
        {
            if (surfaceTransform == null || waterMaterial == null) return;

            ResolveFollowTarget();
            Vector3 center = followTarget != null ? followTarget.position : Vector3.zero;
            surfaceTransform.position = new Vector3(center.x, seaLevel, center.z);
            waterMaterial.SetFloat(SeaLevelId, seaLevel);

            UpdateRippleSimulation(center);
            SyncSkyAndWind();
        }

        void CreateSurface()
        {
            Shader shader = waterShader;
            if (shader == null) shader = Resources.Load<Shader>("SeaLevelWater");
            if (shader == null) shader = Shader.Find("Voyage/Environment/SeaLevelWater");
            if (shader == null)
            {
                Debug.LogError("WATER // SeaLevelWater shader is missing.", this);
                return;
            }

            waterMaterial = new Material(shader)
            {
                name = "Voyage Sea Level Water (Runtime)",
                hideFlags = HideFlags.HideAndDontSave
            };
            waterMaterial.SetFloat(SeaLevelId, seaLevel);

            surfaceObject = new GameObject("VOYAGE // SEA LEVEL WATER");
            surfaceObject.transform.SetParent(transform, false);
            surfaceTransform = surfaceObject.transform;
            surfaceTransform.position = new Vector3(0f, seaLevel, 0f);

            surfaceMesh = BuildRadialMesh(Mathf.Max(1000f, renderRadius), BuildRingRadii(renderRadius), 192);
            MeshFilter filter = surfaceObject.AddComponent<MeshFilter>();
            filter.sharedMesh = surfaceMesh;
            MeshRenderer waterRenderer = surfaceObject.AddComponent<MeshRenderer>();
            waterRenderer.sharedMaterial = waterMaterial;
            waterRenderer.shadowCastingMode = ShadowCastingMode.Off;
            waterRenderer.receiveShadows = false;
            waterRenderer.allowOcclusionWhenDynamic = false;
            waterRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            collisionTrigger = surfaceObject.AddComponent<BoxCollider>();
            collisionTrigger.isTrigger = true;
            collisionTrigger.size = new Vector3(collisionInteractionRadius * 2f,
                Mathf.Max(.25f, collisionTriggerDepth), collisionInteractionRadius * 2f);
            collisionRelay = surfaceObject.AddComponent<WaterCollisionRelay>();
            collisionRelay.Owner = this;
            Debug.Log($"WATER // Sea surface ready at y={seaLevel:0.##}, radius={renderRadius:0}.", this);
        }

        void CreateRippleSimulation()
        {
            if (waterMaterial == null) return;

            Shader stepShader = Resources.Load<Shader>("WaterRippleStep");
            Shader impulseShader = Resources.Load<Shader>("WaterRippleImpulse");
            Shader scrollShader = Resources.Load<Shader>("WaterRippleScroll");
            if (stepShader == null || impulseShader == null || scrollShader == null)
            {
                Debug.LogError("WATER // Ripple simulation shaders are missing from Resources.", this);
                return;
            }

            rippleStepMaterial = new Material(stepShader) { hideFlags = HideFlags.HideAndDontSave };
            rippleImpulseMaterial = new Material(impulseShader) { hideFlags = HideFlags.HideAndDontSave };
            rippleScrollMaterial = new Material(scrollShader) { hideFlags = HideFlags.HideAndDontSave };
            ripplePrevious = CreateRippleTexture("Voyage Water Previous Height");
            rippleCurrent = CreateRippleTexture("Voyage Water Current Height");
            rippleScratch = CreateRippleTexture("Voyage Water Height Scratch");
            if (ripplePrevious == null || rippleCurrent == null || rippleScratch == null)
            {
                Debug.LogError("WATER // Unable to allocate the ripple height field.", this);
                return;
            }
            ClearRippleFields();
        }

        RenderTexture CreateRippleTexture(string textureName)
        {
            var texture = new RenderTexture(RippleTextureSize, RippleTextureSize, 0,
                RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = textureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!texture.Create())
            {
                Destroy(texture);
                return null;
            }
            return texture;
        }

        void ResolveFollowTarget()
        {
            if (followTarget != null && followTarget.GetComponent<CarControl>() != null) return;
            if (Time.unscaledTime < nextTargetSearch) return;
            nextTargetSearch = Time.unscaledTime + 1f;

            CarControl car = FindAnyObjectByType<CarControl>();
            if (car != null) followTarget = car.transform;
            else if (followTarget == null && Camera.main != null) followTarget = Camera.main.transform;
        }

        void SyncSkyAndWind()
        {
            Material sky = RenderSettings.skybox;
            if (sky != null)
            {
                CopyColor(sky, "_SkyTint", SkyTintId, DefaultSky);
                CopyColor(sky, "_HorizonTint", HorizonTintId, DefaultHorizon);
                CopyColor(sky, "_GroundColor", GroundColorId, DefaultGround);
                CopyColor(sky, "_SunColor", SunColorId, Color.white);
                CopyVector(sky, "_SunDirection", SunDirectionId, new Vector4(0f, 1f, 0f, 0f));
                CopyVector(sky, "_MoonDirection", MoonDirectionId, new Vector4(0f, -1f, 0f, 0f));
                if (sky.HasProperty("_Exposure"))
                    waterMaterial.SetFloat(ExposureId, sky.GetFloat("_Exposure"));
                if (sky.HasProperty("_StarIntensity"))
                    waterMaterial.SetFloat(StarIntensityId, sky.GetFloat("_StarIntensity"));
            }

            WindSystem wind = WindSystem.Instance;
            Vector2 direction = wind != null ? wind.Direction : Vector2.right;
            float speed = wind != null ? wind.speed : 1f;
            waterMaterial.SetVector(WaterWindId, new Vector4(direction.x, direction.y, speed, 0f));
            Vector4 cloudWind = wind != null
                ? wind.GetCloudWind(new Vector2(.65f, .76f), 14f)
                : new Vector4(.65f, .76f, 14f, 0f);
            waterMaterial.SetVector(WaterCloudWindId, cloudWind);
            waterMaterial.SetVector(WaterCloudParametersId,
                new Vector4(reflectedCloudHeights.x, Mathf.Max(reflectedCloudHeights.x + 1f, reflectedCloudHeights.y),
                    reflectedCloudNoiseScale, reflectedCloudCoverage));
            waterMaterial.SetFloat(WaterCloudDensityId, reflectedCloudDensity);
        }

        void UpdateRippleSimulation(Vector3 targetPosition)
        {
            if (rippleCurrent == null || ripplePrevious == null || rippleScratch == null ||
                rippleStepMaterial == null || rippleImpulseMaterial == null || rippleScrollMaterial == null)
                return;

            Vector2 desiredCenter = new Vector2(
                Mathf.Round(targetPosition.x / RippleFieldRecenterStep) * RippleFieldRecenterStep,
                Mathf.Round(targetPosition.z / RippleFieldRecenterStep) * RippleFieldRecenterStep);
            if (!rippleFieldInitialized)
            {
                rippleCenter = desiredCenter;
                rippleFieldInitialized = true;
            }
            else
            {
                Vector2 offset = desiredCenter - rippleCenter;
                if (offset.sqrMagnitude > .0001f)
                {
                    if (Mathf.Abs(offset.x) >= RippleFieldHalfSize * 2f ||
                        Mathf.Abs(offset.y) >= RippleFieldHalfSize * 2f)
                        ClearRippleFields();
                    else
                        ScrollRippleFields(offset);
                    rippleCenter = desiredCenter;
                }
            }

            // Follow the reference project's previous/current RenderTexture
            // wave step. Its mouse-written height stamp is replaced by the
            // rigidbody impulses queued by WaterCollisionRelay.
            if (Time.deltaTime > 0f)
            {
                rippleStepMaterial.SetTexture(RipplePreviousId, ripplePrevious);
                rippleStepMaterial.SetTexture(RippleCurrentId, rippleCurrent);
                rippleStepMaterial.SetFloat(RippleDampingId, rippleDamping);
                Graphics.Blit(rippleCurrent, rippleScratch, rippleStepMaterial);

                RenderTexture oldPrevious = ripplePrevious;
                ripplePrevious = rippleCurrent;
                rippleCurrent = rippleScratch;
                rippleScratch = oldPrevious;

                ApplyPendingImpulses();
            }

            waterMaterial.SetTexture(WaterRippleTextureId, rippleCurrent);
            float texelWorldSize = RippleFieldHalfSize * 2f / RippleTextureSize;
            waterMaterial.SetVector(WaterRippleCenterId,
                new Vector4(rippleCenter.x, rippleCenter.y, RippleFieldHalfSize, texelWorldSize));
        }

        void ScrollRippleFields(Vector2 worldOffset)
        {
            rippleScrollMaterial.SetVector(RippleScrollOffsetId,
                new Vector4(worldOffset.x / (RippleFieldHalfSize * 2f),
                    worldOffset.y / (RippleFieldHalfSize * 2f), 0f, 0f));

            Graphics.Blit(rippleCurrent, rippleScratch, rippleScrollMaterial);
            Swap(ref rippleCurrent, ref rippleScratch);
            Graphics.Blit(ripplePrevious, rippleScratch, rippleScrollMaterial);
            Swap(ref ripplePrevious, ref rippleScratch);
        }

        void ApplyPendingImpulses()
        {
            if (pendingImpulses.Count == 0) return;

            Array.Clear(rippleImpulses, 0, rippleImpulses.Length);
            int count = 0;
            for (int i = 0; i < pendingImpulses.Count && count < MaxRippleImpulses; i++)
            {
                Vector4 impact = pendingImpulses[i];
                float u = (impact.x - rippleCenter.x) / (RippleFieldHalfSize * 2f) + .5f;
                float v = (impact.y - rippleCenter.y) / (RippleFieldHalfSize * 2f) + .5f;
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                float radiusUv = impact.w / (RippleFieldHalfSize * 2f);
                rippleImpulses[count++] = new Vector4(u, v, radiusUv, impact.z);
            }
            pendingImpulses.Clear();
            if (count == 0) return;

            rippleImpulseMaterial.SetTexture("_MainTex", rippleCurrent);
            rippleImpulseMaterial.SetVectorArray(RippleImpulseArrayId, rippleImpulses);
            rippleImpulseMaterial.SetInt(RippleImpulseCountId, count);
            Graphics.Blit(rippleCurrent, rippleScratch, rippleImpulseMaterial);
            Swap(ref rippleCurrent, ref rippleScratch);
        }

        void ClearRippleFields()
        {
            ClearRippleTexture(ripplePrevious);
            ClearRippleTexture(rippleCurrent);
            ClearRippleTexture(rippleScratch);
        }

        static void ClearRippleTexture(RenderTexture texture)
        {
            if (texture == null) return;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        static void Swap(ref RenderTexture first, ref RenderTexture second)
        {
            RenderTexture value = first;
            first = second;
            second = value;
        }

        static void ReleaseRippleTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            Destroy(texture);
            texture = null;
        }

        public void RegisterCollision(Collider other, bool staying)
        {
            if (other == null || !other.gameObject.activeInHierarchy) return;
            Rigidbody body = other.attachedRigidbody;
            if (body == null || body.isKinematic) return;

            EntityId id = body.GetEntityId();
            float now = Time.fixedTime;
            if (nextStampTimeByBody.TryGetValue(id, out float nextTime) && now < nextTime) return;

            Vector3 contactPoint = new Vector3(other.bounds.center.x, seaLevel, other.bounds.center.z);
            contactPoint = other.ClosestPoint(contactPoint);
            contactPoint.y = seaLevel;
            float impactSpeed = body.GetPointVelocity(contactPoint).magnitude;
            float threshold = staying ? .8f : .45f;
            if (impactSpeed < threshold) return;

            nextStampTimeByBody[id] = now + collisionStampInterval;
            if (nextStampTimeByBody.Count > 256)
            {
                var staleIds = new List<EntityId>();
                foreach (KeyValuePair<EntityId, float> item in nextStampTimeByBody)
                    if (item.Value < now) staleIds.Add(item.Key);
                for (int i = 0; i < staleIds.Count; i++) nextStampTimeByBody.Remove(staleIds[i]);
            }

            float strength = staying
                ? Mathf.Lerp(.06f, .32f, Mathf.Clamp01((impactSpeed - threshold) / 10f))
                : Mathf.Lerp(.12f, .9f, Mathf.Clamp01((impactSpeed - threshold) / 14f));
            float radius = Mathf.Clamp(.55f + impactSpeed * .035f, .55f, 2.6f);
            pendingImpulses.Add(new Vector4(contactPoint.x, contactPoint.z, strength, radius));
            if (pendingImpulses.Count > 64) pendingImpulses.RemoveAt(0);
        }

        void CopyColor(Material source, string sourceName, int destinationId, Color fallback)
        {
            waterMaterial.SetColor(destinationId,
                source.HasProperty(sourceName) ? source.GetColor(sourceName) : fallback);
        }

        void CopyVector(Material source, string sourceName, int destinationId, Vector4 fallback)
        {
            waterMaterial.SetVector(destinationId,
                source.HasProperty(sourceName) ? source.GetVector(sourceName) : fallback);
        }

        static Mesh BuildRadialMesh(float radius, float[] ringRadii, int segments)
        {
            int ringCount = ringRadii.Length;
            Vector3[] vertices = new Vector3[1 + ringCount * segments];
            int[] triangles = new int[(segments + (ringCount - 1) * segments * 2) * 3];
            int triangle = 0;

            for (int ring = 0; ring < ringCount; ring++)
            {
                float ringRadius = Mathf.Min(radius, ringRadii[ring]);
                for (int segment = 0; segment < segments; segment++)
                {
                    float angle = segment * (Mathf.PI * 2f / segments);
                    vertices[1 + ring * segments + segment] =
                        new Vector3(Mathf.Cos(angle) * ringRadius, 0f, Mathf.Sin(angle) * ringRadius);
                }
            }

            // Center fan; winding faces upward.
            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;
                triangles[triangle++] = 0;
                triangles[triangle++] = 1 + next;
                triangles[triangle++] = 1 + segment;
            }

            for (int ring = 0; ring < ringCount - 1; ring++)
            {
                int inner = 1 + ring * segments;
                int outer = inner + segments;
                for (int segment = 0; segment < segments; segment++)
                {
                    int next = (segment + 1) % segments;
                    int a = inner + segment;
                    int b = outer + segment;
                    int c = outer + next;
                    int d = inner + next;
                    triangles[triangle++] = a;
                    triangles[triangle++] = d;
                    triangles[triangle++] = c;
                    triangles[triangle++] = a;
                    triangles[triangle++] = c;
                    triangles[triangle++] = b;
                }
            }

            Mesh mesh = new Mesh { name = "Voyage Sea Level Radial LOD" };
            mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(radius * 2f + 100f, 100f, radius * 2f + 100f));
            return mesh;
        }

        static float[] BuildRingRadii(float radius)
        {
            radius = Mathf.Max(1000f, radius);
            var radii = new List<float>(FarRingRadii.Length + 72);
            for (int r = 1; r <= 72; r++) radii.Add(r);
            foreach (float r in FarRingRadii) if (r < radius) radii.Add(r);
            // Extend the geometry too, not just its culling bounds. Sparse outer
            // rings are enough for a flat sea and keep the near ripple mesh intact.
            for (float r = 48000f; r < radius; r *= 2f) radii.Add(r);
            radii.Add(radius);
            return radii.ToArray();
        }
    }
}
