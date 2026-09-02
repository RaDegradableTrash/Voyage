using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>
    /// Camera-independent world-space field for temporary grass deformation.
    /// RG stores bend direction, B stores intensity. Permanent tracks use a
    /// second field so temporary recovery never erases persistent data.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class GrassInteractionSystem : MonoBehaviour
    {
        public static GrassInteractionSystem Instance { get; private set; }

        [Header("World-space field")]
        [Min(16)] public int resolution = 512;
        [Min(16f)] public float worldSize = 160f;
        [Min(0.01f)] public float decayPerSecond = 0.28f;
        [Min(0.01f)] public float maxStampDistance = 12f;
        [Min(1f)] public float maxTeleportDistance = 80f;
        [Min(0.1f)] public float speedForFullBend = 12f;
        [Min(1)] public int liveStampsPerFrame = 96;
        [Min(64)] public int maxPendingStamps = 2048;
        [Min(1)] public int permanentRebuildStampsPerFrame = 96;
        public bool recordPermanentTracks = true;
        public Transform followTarget;

        readonly List<Transform> vehicles = new List<Transform>();
        readonly List<WheelState> wheelStates = new List<WheelState>();
        readonly List<EmitterState> emitters = new List<EmitterState>();
        readonly Queue<PendingStamp> pendingStamps = new Queue<PendingStamp>();
        RenderTexture field;
        RenderTexture scratch;
        RenderTexture permanentField;
        RenderTexture permanentScratch;
        Material decayMaterial;
        Material stampMaterial;
        Material scrollMaterial;
        GrassPermanentTrackStore permanentTrackStore;
        Vector3 fieldCenter;
        Vector3 lastFieldCenter;
        bool hasFieldCenter;
        bool initialized;
        readonly List<GrassPermanentTrackStore.TrackSample> permanentRebuildSamples = new List<GrassPermanentTrackStore.TrackSample>();
        int permanentRebuildCursor;
        bool permanentRebuildPending;

        sealed class WheelState
        {
            public Transform wheel;
            public Vector3 previous;
            public bool valid;
        }

        sealed class EmitterState
        {
            public Transform target;
            public Vector3 previous;
            public float radius;
            public float minimumTravel;
            public bool valid;
        }

        struct PendingStamp
        {
            public Vector3 from;
            public Vector3 to;
            public float radius;
            public float speed;
            public Transform source;
        }

        void OnValidate()
        {
            resolution = Mathf.Max(16, resolution);
            worldSize = Mathf.Max(16f, worldSize);
            decayPerSecond = Mathf.Max(0.01f, decayPerSecond);
            maxStampDistance = Mathf.Max(0.01f, maxStampDistance);
            maxTeleportDistance = Mathf.Max(maxStampDistance, maxTeleportDistance);
            speedForFullBend = Mathf.Max(0.1f, speedForFullBend);
            liveStampsPerFrame = Mathf.Max(1, liveStampsPerFrame);
            maxPendingStamps = Mathf.Max(64, maxPendingStamps);
            permanentRebuildStampsPerFrame = Mathf.Max(1, permanentRebuildStampsPerFrame);
        }

        public RenderTexture Field => field;
        public RenderTexture PermanentField => permanentField;
        public bool IsReady => initialized;
        public int RegisteredWheelCount => wheelStates.Count;
        public int PendingStampCount => pendingStamps.Count;
        public Vector4 WorldToUv => new Vector4(fieldCenter.x, fieldCenter.z, worldSize, resolution);

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            permanentTrackStore = GetComponent<GrassPermanentTrackStore>();
            if (permanentTrackStore == null) permanentTrackStore = gameObject.AddComponent<GrassPermanentTrackStore>();
            Initialize();
        }

        public void Initialize()
        {
            if (initialized) return;
            OnValidate();
            field = CreateField("Grass Interaction Field");
            scratch = CreateField("Grass Interaction Scratch");
            permanentField = CreateField("Grass Permanent Track Field");
            permanentScratch = CreateField("Grass Permanent Track Scratch");
            decayMaterial = CreateMaterial("Hidden/Voyage/GrassInteractionDecay");
            stampMaterial = CreateMaterial("Hidden/Voyage/GrassInteractionStamp");
            scrollMaterial = CreateMaterial("Hidden/Voyage/GrassInteractionScroll");
            initialized = field != null && scratch != null && permanentField != null && permanentScratch != null && decayMaterial != null && stampMaterial != null && scrollMaterial != null;
            if (initialized)
            {
                Clear();
                lastFieldCenter = fieldCenter;
                PublishGlobals();
            }
        }

        void OnEnable()
        {
            // OnDisable clears global shader state. Republish it immediately
            // when this component is re-enabled instead of waiting for the
            // first LateUpdate, which prevents a transient interaction gap.
            if (initialized) PublishGlobals();
        }

        public void SetTarget(Transform target)
        {
            if (followTarget == target) return;
            followTarget = target;
            // A new target means the old wheel contact baselines are not
            // spatially continuous. Force a fresh world-space anchor and let
            // the next valid WheelCollider hit establish new baselines.
            hasFieldCenter = false;
            for (int i = 0; i < wheelStates.Count; i++) wheelStates[i].valid = false;
        }

        public void RegisterVehicle(GameObject vehicle)
        {
            if (vehicle == null || vehicles.Contains(vehicle.transform)) return;
            vehicles.Add(vehicle.transform);
            WheelCollider[] colliders = vehicle.GetComponentsInChildren<WheelCollider>(true);
            for (int i = 0; i < colliders.Length; i++)
                wheelStates.Add(new WheelState { wheel = colliders[i].transform });
        }

        public void RegisterEmitter(Transform target, float radius, float minimumTravel)
        {
            if (target == null) return;
            for (int i = 0; i < emitters.Count; i++)
                if (emitters[i].target == target)
                {
                    emitters[i].radius = Mathf.Max(0.05f, radius);
                    emitters[i].minimumTravel = Mathf.Max(0f, minimumTravel);
                    emitters[i].valid = false;
                    return;
                }
            emitters.Add(new EmitterState
            {
                target = target,
                radius = Mathf.Max(0.05f, radius),
                minimumTravel = Mathf.Max(0f, minimumTravel)
            });
        }

        public void UnregisterEmitter(Transform target)
        {
            for (int i = emitters.Count - 1; i >= 0; i--)
                if (emitters[i].target == null || emitters[i].target == target) emitters.RemoveAt(i);
        }

        public void UnregisterVehicle(GameObject vehicle)
        {
            if (vehicle == null) return;
            Transform root = vehicle.transform;
            vehicles.Remove(root);
            for (int i = wheelStates.Count - 1; i >= 0; i--)
            {
                Transform wheel = wheelStates[i].wheel;
                if (wheel == null || (wheel != root && wheel.IsChildOf(root)))
                {
                    if (permanentTrackStore != null) permanentTrackStore.ForgetSource(wheel);
                    wheelStates.RemoveAt(i);
                }
            }
        }

        void LateUpdate()
        {
            if (!initialized) return;
            if (followTarget != null)
            {
                Vector3 targetCenter = followTarget.position;
                targetCenter.y = 0f;
                float texelWorldSize = worldSize / Mathf.Max(1, resolution);
                targetCenter.x = Mathf.Round(targetCenter.x / texelWorldSize) * texelWorldSize;
                targetCenter.z = Mathf.Round(targetCenter.z / texelWorldSize) * texelWorldSize;
                Vector2 shift = new Vector2(targetCenter.x - lastFieldCenter.x, targetCenter.z - lastFieldCenter.z);
                // The texture is already in world space. Do not move its
                // coordinate frame every frame without reprojecting the
                // pixels, or existing tracks would slide under the vehicle.
                // Re-anchor only after the vehicle leaves the stable window;
                // the existing pixels are reprojected before the coordinate
                // frame changes, while the newly exposed area is cleared.
                if (!hasFieldCenter || shift.magnitude > worldSize * 0.25f)
                {
                    bool wasInitialized = hasFieldCenter;
                    Vector3 previousFieldCenter = fieldCenter;
                    bool previousRebuildComplete = !permanentRebuildPending;
                    if (wasInitialized)
                    {
                        Vector2 uvOffset = new Vector2(shift.x / worldSize, shift.y / worldSize);
                        ScrollField(field, scratch, uvOffset);
                        Swap();
                        ScrollField(permanentField, permanentScratch, uvOffset);
                        SwapPermanent();
                    }
                    fieldCenter = targetCenter;
                    lastFieldCenter = fieldCenter;
                    if (!wasInitialized)
                    {
                        Clear();
                    }
                    BeginPermanentFieldRebuild(previousFieldCenter, wasInitialized && previousRebuildComplete);
                    for (int i = 0; i < wheelStates.Count; i++) wheelStates[i].valid = false;
                    hasFieldCenter = true;
                }
            }
            fieldCenter.y = 0f;
            decayMaterial.SetFloat("_Decay", Mathf.Exp(-decayPerSecond * Time.deltaTime));
            Graphics.Blit(field, scratch, decayMaterial);
            Swap();
            ProcessPermanentFieldRebuild();

            for (int i = vehicles.Count - 1; i >= 0; i--)
                if (vehicles[i] == null) vehicles.RemoveAt(i);

            for (int i = wheelStates.Count - 1; i >= 0; i--)
            {
                WheelState state = wheelStates[i];
                if (state.wheel == null)
                {
                    if (permanentTrackStore != null) permanentTrackStore.ForgetSource(state.wheel);
                    wheelStates.RemoveAt(i);
                    continue;
                }
                WheelCollider collider = state.wheel.GetComponent<WheelCollider>();
                if (collider == null)
                {
                    if (permanentTrackStore != null) permanentTrackStore.ForgetSource(state.wheel);
                    wheelStates.RemoveAt(i);
                    continue;
                }
                if (!collider.GetGroundHit(out WheelHit hit)) { state.valid = false; continue; }
                Vector3 current = hit.point;
                if (!state.valid) { state.previous = current; state.valid = true; continue; }
                bool currentOutside = IsOutsideField(current, collider.radius);
                bool previousOutside = IsOutsideField(state.previous, collider.radius);
                // Do not queue or persist tracks for entities that are wholly
                // outside the active world-space window. Keeping the latest
                // contact as the baseline still lets a distant wheel enter
                // the window without producing a teleport-length segment.
                if (currentOutside && previousOutside)
                {
                    state.previous = current;
                    continue;
                }
                Vector3 delta = current - state.previous;
                float distance = new Vector2(delta.x, delta.z).magnitude;
                if (distance > maxTeleportDistance) state.previous = current;
                else if (distance > 0.001f)
                {
                    Transform wheelSource = state.wheel;
                    QueueSegment(state.previous, current, collider.radius, wheelSource);
                }
                state.previous = current;
            }
            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                EmitterState emitter = emitters[i];
                if (emitter.target == null) { emitters.RemoveAt(i); continue; }
                Vector3 current = emitter.target.position;
                if (!emitter.valid)
                {
                    emitter.previous = current;
                    emitter.valid = true;
                    continue;
                }
                Vector3 delta = current - emitter.previous;
                float distance = new Vector2(delta.x, delta.z).magnitude;
                if (distance > maxTeleportDistance)
                {
                    emitter.previous = current;
                    continue;
                }
                if (distance >= Mathf.Max(0f, emitter.minimumTravel))
                    QueueSegment(emitter.previous, current, emitter.radius, emitter.target);
                emitter.previous = current;
            }
            ProcessPendingStamps();
            PublishGlobals();
        }

        bool IsOutsideField(Vector3 position, float radius)
        {
            float halfWorld = worldSize * 0.5f + Mathf.Max(0f, radius);
            return Mathf.Abs(position.x - fieldCenter.x) > halfWorld ||
                   Mathf.Abs(position.z - fieldCenter.z) > halfWorld;
        }

        void QueueSegment(Vector3 from, Vector3 to, float radius, Transform source)
        {
            float distance = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            float speed = distance / Mathf.Max(Time.deltaTime, 0.0001f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(0.01f, maxStampDistance)));
            for (int i = 0; i < steps; i++)
            {
                float start = i / (float)steps;
                float end = (i + 1) / (float)steps;
                int pendingLimit = Mathf.Max(1, maxPendingStamps);
                if (pendingStamps.Count >= pendingLimit) pendingStamps.Dequeue();
                pendingStamps.Enqueue(new PendingStamp
                {
                    from = Vector3.Lerp(from, to, start),
                    to = Vector3.Lerp(from, to, end),
                    radius = radius,
                    speed = speed,
                    source = source
                });
            }
        }

        void ProcessPendingStamps()
        {
            int budget = Mathf.Max(1, liveStampsPerFrame);
            while (budget-- > 0 && pendingStamps.Count > 0)
            {
                PendingStamp stamp = pendingStamps.Dequeue();
                Stamp(stamp.from, stamp.to, stamp.radius, stamp.speed, stamp.source);
            }
        }

        void Stamp(Vector3 from, Vector3 to, float radius, float speed, Transform source)
        {
            Vector2 center = new Vector2((fieldCenter.x - worldSize * 0.5f), (fieldCenter.z - worldSize * 0.5f));
            Vector2 a = (new Vector2(from.x, from.z) - center) / worldSize;
            Vector2 b = (new Vector2(to.x, to.z) - center) / worldSize;
            Vector2 dir = (b - a).sqrMagnitude > 0.000001f ? (b - a).normalized : Vector2.up;
            float strength = Mathf.Lerp(0.28f, 1f, Mathf.Clamp01(speed / speedForFullBend));
            if (recordPermanentTracks && permanentTrackStore != null)
                permanentTrackStore.RecordSegment(from, to, radius, strength, source);
            float margin = Mathf.Max(radius * 1.25f, worldSize / resolution);
            float minX = Mathf.Min(from.x, to.x) - margin;
            float maxX = Mathf.Max(from.x, to.x) + margin;
            float minZ = Mathf.Min(from.z, to.z) - margin;
            float maxZ = Mathf.Max(from.z, to.z) + margin;
            float halfWorld = worldSize * 0.5f;
            if (maxX < fieldCenter.x - halfWorld || minX > fieldCenter.x + halfWorld ||
                maxZ < fieldCenter.z - halfWorld || minZ > fieldCenter.z + halfWorld) return;
            StampInto(field, scratch, a, b, dir, radius, strength);
            Swap();
            if (recordPermanentTracks && permanentTrackStore != null)
            {
                StampInto(permanentField, permanentScratch, a, b, dir, radius, strength);
                SwapPermanent();
            }
        }

        void StampInto(RenderTexture source, RenderTexture destination, Vector2 a, Vector2 b, Vector2 dir, float radius, float strength)
        {
            stampMaterial.SetVector("_StampA", new Vector4(a.x, a.y, 0f, 0f));
            stampMaterial.SetVector("_StampB", new Vector4(b.x, b.y, 0f, 0f));
            stampMaterial.SetVector("_StampDirection", new Vector4(dir.x, dir.y, 0f, 0f));
            stampMaterial.SetFloat("_StampRadius", Mathf.Max(radius * 1.25f / worldSize, 1f / resolution));
            stampMaterial.SetFloat("_StampStrength", strength);
            Graphics.Blit(source, destination, stampMaterial);
        }

        void ScrollField(RenderTexture source, RenderTexture destination, Vector2 uvOffset)
        {
            scrollMaterial.SetVector("_ScrollOffset", new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
            Graphics.Blit(source, destination, scrollMaterial);
        }

        void PublishGlobals()
        {
            Shader.SetGlobalTexture("_VoyageGrassInteraction", field);
            Shader.SetGlobalVector("_VoyageGrassInteractionWorld", WorldToUv);
            Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", permanentField);
        }

        void BeginPermanentFieldRebuild(Vector3 previousCenter, bool onlyNewArea)
        {
            permanentRebuildSamples.Clear();
            permanentRebuildCursor = 0;
            permanentRebuildPending = false;
            if (permanentTrackStore == null) return;
            // If the previous rebuild was interrupted, the scrolled field can
            // contain samples from an older coordinate window. A full replay
            // must start from a blank target or those stale pixels survive
            // indefinitely and no longer represent the persistent store.
            if (!onlyNewArea)
                ClearPermanentFields();
            IReadOnlyList<GrassPermanentTrackStore.TrackSample> samples = permanentTrackStore.Samples;
            float half = worldSize * 0.5f;
            float previousHalf = worldSize * 0.5f;
            for (int i = 0; i < samples.Count; i++)
            {
                GrassPermanentTrackStore.TrackSample sample = samples[i];
                if (Mathf.Abs(sample.position.x - fieldCenter.x) > half + sample.radius ||
                    Mathf.Abs(sample.position.z - fieldCenter.z) > half + sample.radius) continue;
                if (onlyNewArea &&
                    Mathf.Abs(sample.position.x - previousCenter.x) <= previousHalf + sample.radius &&
                    Mathf.Abs(sample.position.z - previousCenter.z) <= previousHalf + sample.radius) continue;
                permanentRebuildSamples.Add(sample);
            }
            permanentRebuildPending = permanentRebuildSamples.Count > 0;
        }

        void ClearPermanentFields()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = permanentField;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = permanentScratch;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        void ProcessPermanentFieldRebuild()
        {
            if (!permanentRebuildPending) return;
            int budget = Mathf.Max(1, permanentRebuildStampsPerFrame);
            float half = worldSize * 0.5f;
            while (budget-- > 0 && permanentRebuildCursor < permanentRebuildSamples.Count)
            {
                GrassPermanentTrackStore.TrackSample sample = permanentRebuildSamples[permanentRebuildCursor++];
                Vector2 p = new Vector2((sample.position.x - (fieldCenter.x - half)) / worldSize,
                                        (sample.position.z - (fieldCenter.z - half)) / worldSize);
                StampInto(permanentField, permanentScratch, p, p, sample.direction, sample.radius, sample.strength);
                SwapPermanent();
            }
            if (permanentRebuildCursor >= permanentRebuildSamples.Count)
            {
                permanentRebuildSamples.Clear();
                permanentRebuildPending = false;
            }
        }

        void Swap()
        {
            RenderTexture temp = field; field = scratch; scratch = temp;
        }

        void SwapPermanent()
        {
            RenderTexture temp = permanentField; permanentField = permanentScratch; permanentScratch = temp;
        }

        void Clear()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = field; GL.Clear(false, true, Color.clear);
            RenderTexture.active = scratch; GL.Clear(false, true, Color.clear);
            RenderTexture.active = permanentField; GL.Clear(false, true, Color.clear);
            RenderTexture.active = permanentScratch; GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        RenderTexture CreateField(string name)
        {
            var result = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false };
            result.Create();
            return result;
        }

        Material CreateMaterial(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            return shader == null ? null : new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void OnDisable()
        {
            if (Instance != this) return;
            pendingStamps.Clear();
            permanentRebuildSamples.Clear();
            permanentRebuildCursor = 0;
            permanentRebuildPending = false;
            for (int i = 0; i < wheelStates.Count; i++) wheelStates[i].valid = false;
            for (int i = 0; i < emitters.Count; i++) emitters[i].valid = false;
            Shader.SetGlobalTexture("_VoyageGrassInteraction", null);
            Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", null);
            Shader.SetGlobalVector("_VoyageGrassInteractionWorld", Vector4.zero);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalTexture("_VoyageGrassInteraction", null);
            Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", null);
            Shader.SetGlobalVector("_VoyageGrassInteractionWorld", Vector4.zero);
            if (field != null) field.Release();
            if (scratch != null) scratch.Release();
            if (permanentField != null) permanentField.Release();
            if (permanentScratch != null) permanentScratch.Release();
            if (field != null) Destroy(field);
            if (scratch != null) Destroy(scratch);
            if (permanentField != null) Destroy(permanentField);
            if (permanentScratch != null) Destroy(permanentScratch);
            if (decayMaterial != null) Destroy(decayMaterial);
            if (stampMaterial != null) Destroy(stampMaterial);
            if (scrollMaterial != null) Destroy(scrollMaterial);
        }
    }
}
