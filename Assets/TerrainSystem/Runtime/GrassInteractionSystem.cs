using System.Collections.Generic;
using UnityEngine;

namespace Voyage.TerrainSystem
{
    /// <summary>
    /// Camera-independent world-space field for temporary grass deformation.
    /// RG stores the signed, pressure-weighted bend, B stores intensity. Permanent tracks use a
    /// second field so temporary recovery never erases persistent data.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class GrassInteractionSystem : MonoBehaviour
    {
        public enum GrassDebugState
        {
            Outside,
            WindOnly,
            NearbyIdle,
            Pressing,
            Recovering
        }

        [Header("Debug")]
        [Tooltip("Color grass by its current wheel interaction state and show wheel state text in Game view.")]
        public bool debugGrassStateMachine;
        [Tooltip("Draw the streamed grass-tile state and wheel influence bounds in the Scene view.")]
        public bool debugDrawTileStates;
        [Tooltip("Runtime key used to toggle the debug overlay and shader colors.")]
        public KeyCode debugToggleKey = KeyCode.F10;

        public static GrassInteractionSystem Instance { get; private set; }

        [Header("World-space field")]
        [Min(16)] public int resolution = 512;
        [Min(16f)] public float worldSize = 160f;
        [Tooltip("Exponential recovery speed. At 0.06, impressions retain 55% after 10 seconds and 5% after 50 seconds. Recovery depends only on elapsed time.")]
        [Min(0.01f)] public float decayPerSecond = 0.06f;
        [Min(0.01f)] public float maxStampDistance = 12f;
        [Min(1f)] public float maxTeleportDistance = 80f;
        [Min(0.1f)] public float speedForFullBend = 12f;
        [Tooltip("Rigidbody mass at which a tire applies maximum grass pressure.")]
        [Min(1f)] public float massForFullPressure = 2500f;
        [Tooltip("Minimum horizontal vehicle speed used for rolling pressure. Stationary ground contacts are refreshed at a reduced rate.")]
        [Min(0f)] public float minimumVehicleSpeed = 0.08f;
        [Tooltip("Minimum horizontal wheel/contact movement used to create a tire segment.")]
        [Min(0f)] public float minimumWheelTravel = 0.02f;
        [Min(1)] public int liveStampsPerFrame = 96;
        [Min(64)] public int maxPendingStamps = 2048;
        [Min(1)] public int permanentRebuildStampsPerFrame = 96;
        // Optional permanent damage is separate from the time-based recovery
        // history, which always survives movement of the GPU window.
        public bool recordPermanentTracks = false;
        public Transform followTarget;

        readonly List<Transform> vehicles = new List<Transform>();
        readonly List<WheelState> wheelStates = new List<WheelState>();
        readonly List<EmitterState> emitters = new List<EmitterState>();
        readonly Queue<PendingStamp> pendingStamps = new Queue<PendingStamp>();
        RenderTexture field;
        RenderTexture scratch;
        const int FarResolution = 1024;
        const float FarWorldSize = 1200f;
        RenderTexture farField, farScratch;
        Vector3 farCenter;
        bool hasFarCenter;
        float farDecayTime;
        readonly Queue<ContactHistory> farReplay = new Queue<ContactHistory>();
        RenderTexture permanentField;
        RenderTexture permanentScratch;
        Material decayMaterial;
        Material stampMaterial;
        ComputeShader contactCompute;
        readonly int[] stampPixelMin = new int[2], stampPixelMax = new int[2];
        static readonly Unity.Profiling.ProfilerMarker ContactUpdate = new Unity.Profiling.ProfilerMarker("Voyage.Grass.ContactUpdate");
        // Bounded world-space event history survives movement of the GPU
        // window. Entries expire when their bend is below visible precision.
        const int HistoryCapacity = 65536;
        readonly ContactHistory[] contactHistory = new ContactHistory[HistoryCapacity];
        int historyStart, historyCount;
        readonly Queue<ContactHistory> historyReplay = new Queue<ContactHistory>();
        struct ContactHistory
        {
            public Vector3 from, to;
            public Vector2 direction;
            public float radius, strength, time;
        }
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
            public Rigidbody body;
            public VehicleTerrainFollower terrainFollower;
            // -1 means this is a real WheelCollider.  Runtime raycast cars
            // use the follower's contact array instead of a WheelCollider.
            public int followerWheelIndex = -1;
            public float radius;
            public Vector3 previous;
            public bool valid;
            public GrassDebugState debugState;
            public float lastPressedTime = -1000f;
            public bool pressingThisFrame;
            public float lastContactStamp = -1000f;
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
            massForFullPressure = Mathf.Max(1f, massForFullPressure);
            minimumVehicleSpeed = Mathf.Max(0f, minimumVehicleSpeed);
            minimumWheelTravel = Mathf.Max(0f, minimumWheelTravel);
            liveStampsPerFrame = Mathf.Max(1, liveStampsPerFrame);
            maxPendingStamps = Mathf.Max(64, maxPendingStamps);
            permanentRebuildStampsPerFrame = Mathf.Max(1, permanentRebuildStampsPerFrame);
        }

        public RenderTexture Field => field;
        public RenderTexture PermanentField => permanentField;
        public RenderTexture FarField => farField;
        public Vector4 FarWorldToUv => new Vector4(farCenter.x, farCenter.z, FarWorldSize, FarResolution);
        public float FarRecovery => Mathf.Exp(-decayPerSecond * (Time.time - farDecayTime));
        public bool IsReady => initialized;
        public int RegisteredWheelCount => wheelStates.Count;
        public int PendingStampCount => pendingStamps.Count;
        public Vector4 WorldToUv => new Vector4(fieldCenter.x, fieldCenter.z, worldSize, resolution);

        public void BindShaderProperties(Material target)
        {
            if (target == null || !initialized) return;
            target.SetTexture("_VoyageGrassInteraction", field);
            target.SetTexture("_VoyageGrassPermanentInteraction", permanentField);
            target.SetTexture("_VoyageGrassFarInteraction", farField);
            target.SetVector("_VoyageGrassFarWorld", FarWorldToUv);
            target.SetFloat("_VoyageGrassFarRecovery", FarRecovery);
            target.SetVector("_VoyageGrassInteractionWorld", WorldToUv);
            target.SetFloat("_VoyageGrassDebugStateMachine", debugGrassStateMachine ? 1f : 0f);
        }

        public void BindShaderProperties(MaterialPropertyBlock target)
        {
            if (target == null || !initialized) return;
            target.SetTexture("_VoyageGrassInteraction", field);
            target.SetTexture("_VoyageGrassPermanentInteraction", permanentField);
            target.SetTexture("_VoyageGrassFarInteraction", farField);
            target.SetVector("_VoyageGrassFarWorld", FarWorldToUv);
            target.SetFloat("_VoyageGrassFarRecovery", FarRecovery);
            target.SetVector("_VoyageGrassInteractionWorld", WorldToUv);
            target.SetFloat("_VoyageGrassDebugStateMachine", debugGrassStateMachine ? 1f : 0f);
        }

        public GrassDebugState GetDebugState(Vector3 position)
        {
            float nearestDistance = float.MaxValue;
            bool hasMovingWheel = false;
            for (int i = 0; i < wheelStates.Count; i++)
            {
                WheelState wheel = wheelStates[i];
                if (!wheel.valid || wheel.wheel == null) continue;
                Vector3 anchor = GetWheelAnchor(wheel);
                float distance = Vector2.Distance(new Vector2(position.x, position.z),
                                                  new Vector2(anchor.x, anchor.z));
                nearestDistance = Mathf.Min(nearestDistance, distance);
                if (wheel.debugState == GrassDebugState.Pressing) hasMovingWheel = true;
            }

            if (nearestDistance == float.MaxValue) return GrassDebugState.Outside;
            if (hasMovingWheel && nearestDistance <= 2.4f) return GrassDebugState.Pressing;
            if (nearestDistance <= 2.4f) return GrassDebugState.NearbyIdle;
            return GrassDebugState.WindOnly;
        }

        public GrassDebugState GetDebugState(Bounds bounds, out float nearestWheelDistance, out int pressingWheelCount)
        {
            nearestWheelDistance = float.MaxValue;
            pressingWheelCount = 0;
            bool recovering = false;
            for (int i = 0; i < wheelStates.Count; i++)
            {
                WheelState wheel = wheelStates[i];
                if (!wheel.valid || wheel.wheel == null) continue;
                Vector3 anchor = GetWheelAnchor(wheel);
                Vector3 closest = bounds.ClosestPoint(anchor);
                float distance = Vector2.Distance(new Vector2(anchor.x, anchor.z), new Vector2(closest.x, closest.z));
                nearestWheelDistance = Mathf.Min(nearestWheelDistance, distance);
                float influenceRadius = Mathf.Max(2.4f, wheel.radius * 3f);
                if (distance <= influenceRadius && wheel.debugState == GrassDebugState.Pressing) pressingWheelCount++;
                if (distance <= influenceRadius && wheel.debugState == GrassDebugState.Recovering) recovering = true;
            }
            if (pressingWheelCount > 0) return GrassDebugState.Pressing;
            if (recovering) return GrassDebugState.Recovering;
            if (nearestWheelDistance <= 2.4f) return GrassDebugState.NearbyIdle;
            return nearestWheelDistance == float.MaxValue ? GrassDebugState.Outside : GrassDebugState.WindOnly;
        }

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
            if (SystemInfo.supportsComputeShaders) contactCompute = Resources.Load<ComputeShader>("TerrainSystem/GrassContact");
            field = CreateField("Grass Interaction Field");
            scratch = CreateField("Grass Interaction Scratch");
            farField = CreateField("Grass Distant History", FarResolution);
            farScratch = CreateField("Grass Distant Scratch", FarResolution);
            farDecayTime = Time.time;
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

        void Update()
        {
            if (debugToggleKey != KeyCode.None && Input.GetKeyDown(debugToggleKey))
            {
                debugGrassStateMachine = !debugGrassStateMachine;
                PublishGlobals();
            }
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
            {
                Transform wheel = colliders[i].transform;
                Rigidbody body = vehicle.GetComponent<Rigidbody>();
                if (body == null) body = wheel.GetComponentInParent<Rigidbody>();
                VehicleTerrainFollower terrainFollower = vehicle.GetComponent<VehicleTerrainFollower>();
                if (terrainFollower == null) terrainFollower = wheel.GetComponentInParent<VehicleTerrainFollower>();
                wheelStates.Add(new WheelState
                {
                    wheel = wheel,
                    body = body,
                    terrainFollower = terrainFollower,
                    radius = Mathf.Max(0.2f, colliders[i].radius * Mathf.Abs(colliders[i].transform.lossyScale.y) * 0.45f)
                });
            }

            if (colliders.Length == 0)
            {
                PlayerCar playerCar = vehicle.GetComponent<PlayerCar>();
                VehicleTerrainFollower follower = vehicle.GetComponent<VehicleTerrainFollower>();
                IReadOnlyList<Transform> runtimeWheels = playerCar != null ? playerCar.GrassInteractionWheelTransforms : null;
                if (runtimeWheels != null)
                {
                    for (int i = 0; i < runtimeWheels.Count; i++)
                    {
                        Transform wheel = runtimeWheels[i];
                        if (wheel == null) continue;
                        wheelStates.Add(new WheelState
                        {
                        wheel = wheel,
                        body = vehicle.GetComponent<Rigidbody>(),
                        terrainFollower = follower,
                        followerWheelIndex = i,
                        radius = follower != null ? Mathf.Max(0.35f, follower.tireRadius) : 0.45f
                        });
                    }
                }
            }
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
            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                Transform emitter = emitters[i].target;
                if (emitter == null || emitter == root || emitter.IsChildOf(root))
                    emitters.RemoveAt(i);
            }
        }

        void LateUpdate()
        {
            using var contactScope = ContactUpdate.Auto();
            if (!initialized) return;

            // The vehicle is spawned asynchronously and may be recreated
            // after a domain/scene reload. Recover the live target and its
            // interaction registration instead of depending on one bootstrap
            // callback to run in the right order.
            if (followTarget == null)
            {
                PlayerCar playerCar = FindAnyObjectByType<PlayerCar>();
                if (playerCar != null) SetTarget(playerCar.transform);
            }
            if (followTarget != null && !HasRegisteredVehicle(followTarget))
                RegisterVehicle(followTarget.gameObject);

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
                    QueueHistoryReplay();
                    BeginPermanentFieldRebuild(previousFieldCenter, wasInitialized && previousRebuildComplete);
                    for (int i = 0; i < wheelStates.Count; i++) wheelStates[i].valid = false;
                    hasFieldCenter = true;
                }
            }
            fieldCenter.y = 0f;
            decayMaterial.SetFloat("_Decay", Mathf.Exp(-decayPerSecond * Time.deltaTime));
            Graphics.Blit(field, scratch, decayMaterial);
            Swap();
            UpdateFarField();
            ProcessHistoryReplay();
            ProcessPermanentFieldRebuild();

            EnsureRuntimeVehicleWheels();

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
                // The project vehicle is a raycast vehicle: its four visual
                // wheel transforms are the interaction sources, but they do
                // not have WheelCollider components. Do not delete those
                // states just because they are not physics WheelColliders.
                if (collider == null && state.terrainFollower == null)
                {
                    if (permanentTrackStore != null) permanentTrackStore.ForgetSource(state.wheel);
                    wheelStates.RemoveAt(i);
                    continue;
                }
                // Use the wheel pivot's XZ as the footprint anchor. WheelCollider
                // contact points are solver results and can remain fixed (or
                // report no hit) while the chassis is being driven over a
                // streamed mesh. The pivot follows every axle, so all wheels
                // produce a continuous world-space tire path.
                // Airborne wheels cannot press grass. A new contact begins a
                // new path rather than drawing a segment across the jump.
                if (collider != null && !collider.GetGroundHit(out _))
                {
                    state.valid = false;
                    state.pressingThisFrame = false;
                    continue;
                }
                Vector3 current = GetWheelAnchor(state);
                if (!state.valid) { state.previous = current; state.valid = true; }
                state.pressingThisFrame = false;
                bool currentOutside = IsOutsideField(current, state.radius);
                bool previousOutside = IsOutsideField(state.previous, state.radius);
                // Do not queue or persist tracks for entities that are wholly
                // outside the active world-space window. Keeping the latest
                // contact as the baseline still lets a distant wheel enter
                // the window without producing a teleport-length segment.
                if (currentOutside && previousOutside)
                {
                    state.debugState = GrassDebugState.Outside;
                    state.previous = current;
                    continue;
                }
                Vector3 delta = current - state.previous;
                float distance = new Vector2(delta.x, delta.z).magnitude;
                if (distance > maxTeleportDistance) state.previous = current;
                else if (IsVehicleMoving(state, distance, out Vector3 motionVelocity))
                {
                    // Do not restamp the same physics pose on every render
                    // frame. Accumulate small movements until a useful span.
                    if (distance < minimumWheelTravel && Time.time - state.lastContactStamp < 0.12f) continue;
                    if (motionVelocity.sqrMagnitude < 0.0001f && distance > 0.0001f)
                        motionVelocity = (current - state.previous) / Mathf.Max(Time.deltaTime, 0.0001f);
                    motionVelocity.y = 0f;
                    state.pressingThisFrame = true;
                    state.debugState = GrassDebugState.Pressing;
                    state.lastPressedTime = Time.time;
                    Transform wheelSource = state.wheel;
                    // On a WheelCollider the reported contact point can stay
                    // almost fixed while the rigidbody travels over a smooth
                    // triangle. Use the contact point as the footprint anchor
                    // and the body velocity to reconstruct the tire path in
                    // that case. This also keeps the path directional instead
                    // of degenerating into an upward-facing point stamp.
                    Vector3 from = state.previous;
                    if (distance <= minimumWheelTravel)
                        from = current - motionVelocity * Time.deltaTime;
                    QueueSegment(from, current, state.radius, wheelSource);
                    state.lastContactStamp = Time.time;
                }
                else if (Time.time - state.lastContactStamp >= 0.25f)
                {
                    // Weight still presses grass while parked. Only the
                    // actual contact remains held; the trail recovers freely.
                    QueueSegment(current, current, state.radius, state.wheel);
                    state.lastContactStamp = Time.time;
                }
                else if (Time.time - state.lastPressedTime < 10f)
                {
                    state.debugState = GrassDebugState.Recovering;
                }
                else
                {
                    state.debugState = GrassDebugState.NearbyIdle;
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

        bool HasRegisteredVehicle(Transform root)
        {
            for (int i = 0; i < vehicles.Count; i++)
                if (vehicles[i] == root) return true;
            return false;
        }

        void EnsureRuntimeVehicleWheels()
        {
            for (int vehicleIndex = 0; vehicleIndex < vehicles.Count; vehicleIndex++)
            {
                Transform root = vehicles[vehicleIndex];
                if (root == null) continue;
                PlayerCar playerCar = root.GetComponent<PlayerCar>();
                if (playerCar == null) continue;
                VehicleTerrainFollower follower = root.GetComponent<VehicleTerrainFollower>();
                IReadOnlyList<Transform> runtimeWheels = playerCar.GrassInteractionWheelTransforms;
                if (runtimeWheels == null) continue;
                for (int wheelIndex = 0; wheelIndex < runtimeWheels.Count; wheelIndex++)
                {
                    Transform wheel = runtimeWheels[wheelIndex];
                    if (wheel == null || HasWheelState(wheel)) continue;
                    wheelStates.Add(new WheelState
                    {
                    wheel = wheel,
                        body = root.GetComponent<Rigidbody>(),
                        terrainFollower = follower,
                        followerWheelIndex = wheelIndex,
                        radius = follower != null ? Mathf.Max(0.35f, follower.tireRadius) : 0.45f
                    });
                }
            }
        }

        bool HasWheelState(Transform wheel)
        {
            for (int i = 0; i < wheelStates.Count; i++)
                if (wheelStates[i].wheel == wheel) return true;
            return false;
        }

        Vector3 GetWheelAnchor(WheelState state)
        {
            if (state != null && state.terrainFollower != null && state.followerWheelIndex >= 0)
            {
                // PlayerCar can expose six visual wheels while the raycast
                // follower owns only four suspension records. Never index the
                // follower with the extra visual wheels: invalid indices fall
                // back to the vehicle root and make unrelated grass respond
                // as if it were under a tire. The visual wheel pivot is the
                // authoritative XZ footprint for every axle; Y is irrelevant
                // to the world-space grass field and shader footprint.
                Vector3 visualWheelPosition = state.wheel != null ? state.wheel.position : state.terrainFollower.transform.position;
                visualWheelPosition.y -= state.terrainFollower.tireRadius;
                return visualWheelPosition;
            }
            if (state != null && state.wheel != null)
            {
                WheelCollider collider = state.wheel.GetComponent<WheelCollider>();
                if (collider != null && collider.GetGroundHit(out WheelHit groundHit))
                    return groundHit.point;
            }
            if (state != null && state.wheel != null)
            {
                WheelCollider collider = state.wheel.GetComponent<WheelCollider>();
                if (collider != null && collider.GetGroundHit(out WheelHit hit)) return hit.point;
                return state.wheel.position;
            }
            return Vector3.zero;
        }

        bool IsVehicleMoving(WheelState state, float observedDistance, out Vector3 motionVelocity)
        {
            // WheelCollider contact points can move while a parked vehicle's
            // suspension settles. Those changes must not keep refreshing the
            // grass recovery timer. Prefer the same velocity source that
            // drives the vehicle. The raycast vehicle can be advanced by
            // VehicleTerrainFollower while its Rigidbody velocity is near
            // zero, so consulting Rigidbody alone silently disables all
            // wheel interaction for that vehicle.
            motionVelocity = Vector3.zero;
            if (state.terrainFollower != null && state.terrainFollower.enabled)
            {
                motionVelocity = state.terrainFollower.CurrentVelocity;
            }
            else if (state.body != null)
            {
                motionVelocity = state.body.linearVelocity;
            }

            motionVelocity.y = 0f;
            if (motionVelocity.sqrMagnitude >= minimumVehicleSpeed * minimumVehicleSpeed)
                return true;

            // A WheelCollider can lag one or more render frames behind the
            // chassis while suspension/mesh streaming settles. In that
            // interval the wheel displacement is still authoritative: it is
            // the actual footprint change that must refresh the grass.
            if (observedDistance >= minimumWheelTravel)
                return true;

            // Keep transform-driven rigs supported when they have no velocity
            // provider, but never use this fallback for a parked physical car.
            if (state.terrainFollower != null || state.body != null) return false;
            return observedDistance / Mathf.Max(Time.deltaTime, 0.0001f) >= 0.2f;
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
            // Grass is displaced away from the moving object, not in front of
            // it. This also makes permanent tire tracks use the same physical
            // orientation as the temporary pressed-grass field.
            // Grass falls in the vehicle's travel direction, matching the
            // wake behind each tire rather than bending away from the tire.
            Vector2 fallbackDirection = source != null ? new Vector2(-source.forward.x, -source.forward.z).normalized : Vector2.up;
            Vector2 dir = (b - a).sqrMagnitude > 1e-12f ? (b - a).normalized : fallbackDirection;
            float speedPressure = Mathf.Lerp(0.8f, 0.95f, Mathf.Clamp01(speed / speedForFullBend));
            Rigidbody body = source != null ? source.GetComponentInParent<Rigidbody>() : null;
            float massPressure = body != null ? Mathf.InverseLerp(250f, massForFullPressure, body.mass) : 0.55f;
            // A heavy vehicle leaves a stronger stored impression than a light
            // prop at the same speed. The resulting B channel also drives the
            // pressure-dependent decay in GrassInteractionField.shader.
            float strength = Mathf.Clamp01(speedPressure * Mathf.Lerp(0.5f, 1f, massPressure));
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
            ApplyContact(field, scratch, a, b, dir, radius, strength, false);
            RememberContact(from, to, dir, radius, strength);
            StampFar(from, to, dir, radius, strength);
            if (recordPermanentTracks && permanentTrackStore != null)
            {
                StampInto(permanentField, permanentScratch, a, b, dir, radius, strength);
                SwapPermanent();
            }
        }

        void UpdateFarField()
        {
            Vector3 target = followTarget != null ? followTarget.position : fieldCenter;
            float texel = FarWorldSize / FarResolution;
            target = new Vector3(Mathf.Round(target.x / texel) * texel, 0, Mathf.Round(target.z / texel) * texel);
            const float recenterDistance = FarWorldSize * 0.5f - 550f;
            if (!hasFarCenter || (target - farCenter).sqrMagnitude > recenterDistance * recenterDistance)
            {
                Vector3 previousCenter = farCenter;
                bool preserved = hasFarCenter && farReplay.Count == 0;
                if (hasFarCenter)
                {
                    ScrollField(farField, farScratch, new Vector2(target.x - farCenter.x, target.z - farCenter.z) / FarWorldSize);
                    SwapFar();
                }
                farCenter = target;
                hasFarCenter = true;
                farReplay.Clear();
                for (int i = 0; i < historyCount; i++)
                {
                    ContactHistory entry = contactHistory[(historyStart + i) % HistoryCapacity];
                    if (Time.time - entry.time > 6.22f / decayPerSecond) continue;
                    Vector3 midpoint = (entry.from + entry.to) * 0.5f;
                    float margin = FarWorldSize * 0.5f + entry.radius + maxStampDistance;
                    if (Mathf.Abs(midpoint.x - farCenter.x) <= margin && Mathf.Abs(midpoint.z - farCenter.z) <= margin)
                    {
                        float radius = Mathf.Max(entry.radius, texel);
                        float safeHalf = FarWorldSize * 0.5f - radius;
                        if (preserved &&
                            Mathf.Abs(entry.from.x - previousCenter.x) < safeHalf && Mathf.Abs(entry.to.x - previousCenter.x) < safeHalf &&
                            Mathf.Abs(entry.from.z - previousCenter.z) < safeHalf && Mathf.Abs(entry.to.z - previousCenter.z) < safeHalf) continue;
                        farReplay.Enqueue(entry);
                    }
                }
            }
            // The distant field covers every visible grass LOD. Decay its
            // pixels at 10 Hz; the shader interpolates elapsed recovery so
            // neither animation nor render cost scales with wheel count.
            if (Time.time - farDecayTime >= 0.1f)
            {
                decayMaterial.SetFloat("_Decay", FarRecovery);
                Graphics.Blit(farField, farScratch, decayMaterial);
                SwapFar();
                farDecayTime = Time.time;
            }
            int budget = Mathf.Max(1, permanentRebuildStampsPerFrame);
            while (budget-- > 0 && farReplay.Count > 0)
            {
                ContactHistory entry = farReplay.Dequeue();
                float strength = entry.strength * Mathf.Exp(-decayPerSecond * (Time.time - entry.time));
                if (strength >= 0.002f) StampFar(entry.from, entry.to, entry.direction, entry.radius, strength);
            }
        }

        void StampFar(Vector3 from, Vector3 to, Vector2 direction, float radius, float strength)
        {
            Vector2 origin = new Vector2(farCenter.x, farCenter.z) - Vector2.one * FarWorldSize * 0.5f;
            Vector2 a = (new Vector2(from.x, from.z) - origin) / FarWorldSize;
            Vector2 b = (new Vector2(to.x, to.z) - origin) / FarWorldSize;
            ApplyContact(farField, farScratch, a, b, direction, radius, strength / Mathf.Max(FarRecovery, 0.001f), false, true);
        }

        void RememberContact(Vector3 from, Vector3 to, Vector2 direction, float radius, float strength)
        {
            while (historyCount > 0 && Time.time - contactHistory[historyStart].time > 6.22f / decayPerSecond)
            {
                historyStart = (historyStart + 1) % HistoryCapacity;
                historyCount--;
            }
            if (historyCount == HistoryCapacity)
            {
                historyStart = (historyStart + 1) % HistoryCapacity;
                historyCount--;
            }
            contactHistory[(historyStart + historyCount++) % HistoryCapacity] = new ContactHistory
            { from = from, to = to, direction = direction, radius = radius, strength = strength, time = Time.time };
        }

        void QueueHistoryReplay()
        {
            historyReplay.Clear();
            float half = worldSize * 0.5f;
            for (int i = 0; i < historyCount; i++)
            {
                ContactHistory entry = contactHistory[(historyStart + i) % HistoryCapacity];
                if (Time.time - entry.time > 6.22f / decayPerSecond) continue;
                if (Mathf.Max(entry.from.x, entry.to.x) + entry.radius < fieldCenter.x - half ||
                    Mathf.Min(entry.from.x, entry.to.x) - entry.radius > fieldCenter.x + half ||
                    Mathf.Max(entry.from.z, entry.to.z) + entry.radius < fieldCenter.z - half ||
                    Mathf.Min(entry.from.z, entry.to.z) - entry.radius > fieldCenter.z + half) continue;
                historyReplay.Enqueue(entry);
            }
        }

        void ProcessHistoryReplay()
        {
            Vector2 origin = new Vector2(fieldCenter.x, fieldCenter.z) - Vector2.one * worldSize * 0.5f;
            int budget = Mathf.Max(1, permanentRebuildStampsPerFrame);
            while (budget-- > 0 && historyReplay.Count > 0)
            {
                ContactHistory entry = historyReplay.Dequeue();
                float strength = entry.strength * Mathf.Exp(-decayPerSecond * (Time.time - entry.time));
                if (strength < 0.002f) continue;
                Vector2 a = (new Vector2(entry.from.x, entry.from.z) - origin) / worldSize;
                Vector2 b = (new Vector2(entry.to.x, entry.to.z) - origin) / worldSize;
                ApplyContact(field, scratch, a, b, entry.direction, entry.radius, strength, false);
            }
        }

        void ApplyContact(RenderTexture source, RenderTexture destination, Vector2 a, Vector2 b,
            Vector2 direction, float radius, float strength, bool permanent, bool far = false)
        {
            int resolution = source.width;
            float size = far ? FarWorldSize : worldSize;
            float uvRadius = Mathf.Max(radius / size, 1f / resolution);
            if (contactCompute != null)
            {
                int minX = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x, b.x) - uvRadius) * resolution), 0, resolution);
                int minY = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.y, b.y) - uvRadius) * resolution), 0, resolution);
                int maxX = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x, b.x) + uvRadius) * resolution), 0, resolution);
                int maxY = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.y, b.y) + uvRadius) * resolution), 0, resolution);
                if (maxX <= minX || maxY <= minY) return;
                contactCompute.SetTexture(0, "_Field", source);
                contactCompute.SetVector("_StampA", new Vector4(a.x, a.y, 0, 0));
                contactCompute.SetVector("_StampB", new Vector4(b.x, b.y, 0, 0));
                contactCompute.SetVector("_StampDirection", new Vector4(direction.x, direction.y, 0, 0));
                contactCompute.SetFloat("_StampRadius", uvRadius);
                contactCompute.SetFloat("_StampStrength", strength);
                contactCompute.SetInt("_Resolution", resolution);
                stampPixelMin[0] = minX; stampPixelMin[1] = minY;
                contactCompute.SetInts("_PixelMin", stampPixelMin);
                stampPixelMax[0] = maxX; stampPixelMax[1] = maxY;
                contactCompute.SetInts("_PixelMax", stampPixelMax);
                contactCompute.Dispatch(0, (maxX-minX+7)/8, (maxY-minY+7)/8, 1);
            }
            else
            {
                StampInto(source, destination, a, b, direction, radius, strength, size);
                if (far) SwapFar(); else if (permanent) SwapPermanent(); else Swap();
            }
        }

        void StampInto(RenderTexture source, RenderTexture destination, Vector2 a, Vector2 b, Vector2 dir, float radius, float strength, float size = 0f)
        {
            stampMaterial.SetVector("_StampA", new Vector4(a.x, a.y, 0f, 0f));
            stampMaterial.SetVector("_StampB", new Vector4(b.x, b.y, 0f, 0f));
            stampMaterial.SetVector("_StampDirection", new Vector4(dir.x, dir.y, 0f, 0f));
            // A wheel contact is smaller than the visible crushed-grass band.
            // Use a wider stamp so the entire tire footprint reads as bent
            // grass instead of a nearly invisible one-pixel line.
            stampMaterial.SetFloat("_StampRadius", Mathf.Max(radius / (size > 0 ? size : worldSize), 1f / source.width));
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
            Shader.SetGlobalTexture("_VoyageGrassFarInteraction", farField);
            Shader.SetGlobalVector("_VoyageGrassFarWorld", FarWorldToUv);
            Shader.SetGlobalFloat("_VoyageGrassFarRecovery", FarRecovery);

            Shader.SetGlobalFloat("_VoyageGrassDebugStateMachine", debugGrassStateMachine ? 1f : 0f);
        }

        void OnGUI()
        {
            if (!debugGrassStateMachine || !Application.isPlaying) return;
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(12f, 72f, 620f, 360f), GUI.skin.box);
            GUILayout.Label("GRASS INTERACTION STATE MACHINE");
            GUILayout.Label($"Wheel sources: {wheelStates.Count} | pending stamps: {pendingStamps.Count}");
            for (int i = 0; i < wheelStates.Count; i++)
            {
                WheelState wheel = wheelStates[i];
                if (wheel.wheel == null) continue;
                Vector3 p = GetWheelAnchor(wheel);
                GUILayout.Label($"Wheel {i}: {wheel.debugState}  anchor=({p.x:0.0}, {p.y:0.0}, {p.z:0.0})  pressed={wheel.pressingThisFrame}");
            }
            InteractiveGrassTile[] tiles = FindObjectsByType<InteractiveGrassTile>(FindObjectsSortMode.None);
            GUILayout.Label($"Visible tile states: {tiles.Length}");
            for (int i = 0; i < tiles.Length && i < 18; i++)
                GUILayout.Label($"Tile {tiles[i].tileCoordinate}: {tiles[i].DebugState}  nearest={tiles[i].DebugNearestWheelDistance:0.0}m  pressing wheels={tiles[i].DebugPressingWheelCount}");
            GUILayout.Label("Shader: red=direct wheel, blue=field, gray=wind only; tile gizmos: red=pressing, blue=recovering, yellow=nearby idle, gray=wind/outside");
            GUILayout.EndArea();
        }

        void BeginPermanentFieldRebuild(Vector3 previousCenter, bool onlyNewArea)
        {
            permanentRebuildSamples.Clear();
            permanentRebuildCursor = 0;
            permanentRebuildPending = false;
            if (!recordPermanentTracks) { ClearPermanentFields(); return; }
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

        void SwapFar() { RenderTexture temp = farField; farField = farScratch; farScratch = temp; }

        void Clear()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = field; GL.Clear(false, true, Color.clear);
            RenderTexture.active = scratch; GL.Clear(false, true, Color.clear);
            RenderTexture.active = permanentField; GL.Clear(false, true, Color.clear);
            RenderTexture.active = permanentScratch; GL.Clear(false, true, Color.clear);
            if (farField != null) { RenderTexture.active = farField; GL.Clear(false, true, Color.clear); }
            if (farScratch != null) { RenderTexture.active = farScratch; GL.Clear(false, true, Color.clear); }
            RenderTexture.active = previous;
        }

        RenderTexture CreateField(string name, int size = 0)
        {
            if (size <= 0) size = resolution;
            var result = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false, enableRandomWrite = contactCompute != null };
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
            historyReplay.Clear();
            farReplay.Clear();
            permanentRebuildSamples.Clear();
            permanentRebuildCursor = 0;
            permanentRebuildPending = false;
            for (int i = 0; i < wheelStates.Count; i++) wheelStates[i].valid = false;
            for (int i = 0; i < emitters.Count; i++) emitters[i].valid = false;
            Shader.SetGlobalTexture("_VoyageGrassInteraction", null);
            Shader.SetGlobalTexture("_VoyageGrassPermanentInteraction", null);
            Shader.SetGlobalTexture("_VoyageGrassFarInteraction", null);
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
            if (farField != null) farField.Release();
            if (farScratch != null) farScratch.Release();
            if (permanentField != null) permanentField.Release();
            if (permanentScratch != null) permanentScratch.Release();
            if (field != null) Destroy(field);
            if (scratch != null) Destroy(scratch);
            if (farField != null) Destroy(farField);
            if (farScratch != null) Destroy(farScratch);
            if (permanentField != null) Destroy(permanentField);
            if (permanentScratch != null) Destroy(permanentScratch);
            if (decayMaterial != null) Destroy(decayMaterial);
            if (stampMaterial != null) Destroy(stampMaterial);
            if (scrollMaterial != null) Destroy(scrollMaterial);
        }
    }
}
