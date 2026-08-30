using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(1000)]
[RequireComponent(typeof(Rigidbody))]
public sealed class VehicleTerrainFollower : MonoBehaviour
{
    public enum VehicleState { Grounded, PartialGrounded, Airborne, Landing, Flipped, Stuck }

    [Header("Vehicle")]
    public float vehicleMass = 1000f;
    // Keep the physical center of mass below the chassis origin so lateral
    // tire forces have a smaller rollover moment during hard cornering.
    public Vector3 centerOfMass = new Vector3(0f, -1.10f, 0f);
    public float maxSpeed = 22f;
    public float acceleration = 18000f;
    public float brakeForce = 22000f;
    public float steeringAngle = 24f;
    public float highSpeedSteeringLimit = 0.5f;
    public float airControlStrength = 0.35f;
    [Tooltip("Aerodynamic drag while airborne. This reduces excessive flight inertia without applying any roll correction.")]
    public float airDrag = 0.12f;
    [Tooltip("Extra gravity while all four tires are off the ground. It shortens flight time without changing normal driving.")]
    public float airGravityMultiplier = 1f;
    public float boostAcceleration = 10f;

    [Header("Suspension")]
    public float suspensionLength = 0.62f;
    public float maxCompression = 0.20f;
    // The streamed off-road terrain can change height by more than a metre
    // between wheel samples. Keep enough extension to preserve four contacts
    // over crests and seams instead of dropping a wheel into airborne mode.
    public float maxExtension = 0.95f;
    public float springStrength = 42000f;
    public float damper = 11000f;
    [Range(0.1f, 1f)]
    [Tooltip("Suspension damping relative to critical damping. Lower values make landings feel heavier and less cushioned.")]
    public float suspensionDampingRatio = 1f;
    public float maxSuspensionForce = 70000f;
    [Tooltip("Small one-shot rebound impulse on wheel landing.")]
    public float landingBounceStrength = 0.10f;
    public float tireRadius = 0.38f;
    [Tooltip("Small gap that keeps the visible tire below surface contact from penetrating the terrain.")]
    public float wheelGroundClearance = 0.045f;
    public float suspensionVisualSmoothTime = 0.14f;
    public float contactSmoothing = 10f;
    [Tooltip("Smooths the physical spring input so terrain triangle seams do not shake the chassis.")]
    public float suspensionTravelSmoothing = 18f;
    [Tooltip("Keeps a wheel supported for a very short gap when a terrain triangle seam misses one raycast.")]
    public float contactGraceTime = 0.12f;
    [Tooltip("Extra extension required before a contact is visually released.")]
    public float contactReleaseMargin = 0.25f;
    [Tooltip("Small spherical probe that bridges terrain triangle seams without changing the tire radius.")]
    public float wheelContactProbeRadius = 0.08f;
    public float wheelBase = 2.84f;
    public float trackWidth = 1.84f;
    public float tireGrip = 1f;
    // A little extra lateral grip keeps the default vehicle readable with a
    // keyboard. Players should be able to correct a slide without mastering
    // a detailed tire model first.
    public float lateralFriction = 1.5f;
    public LayerMask groundLayers = ~0;
    // VehicleManual's wheel centers sit roughly two metres below the root.
    // The previous 1.35m ray ended above the terrain at the spawn pose, so
    // all four wheels were considered airborne and no suspension/drive force
    // was ever applied.
    public float groundDetectionDistance = 32f;

    [Header("Stability")]
    [Tooltip("Selective stability damping around the ground-up axis only. It does not damp roll or pitch.")]
    public float stabilityYawDamping = 4f;
    // Retained for serialized compatibility; the controller no longer caps
    // angular velocity as a stability aid.
    public float maxAngularVelocity = 100f;

    [Header("Recovery")]
    public float flippedAngle = 0.35f;
    public float recoveryTime = 2.5f;

    [Header("Debug")]
    public bool debugDraw;
    public bool diagnosticLogging;

    [System.Serializable]
    public sealed class WheelData
    {
        public Vector3 localHardpoint;
        public bool isFront;
        public bool grounded;
        [System.NonSerialized] public RaycastHit hit;
        public float compression;
        public float suspensionTravel;
        public float suspensionForce;
        public Vector3 tireForce;
        public Vector3 contactPoint;
        public Vector3 groundNormal;
        [System.NonSerialized] public Vector3 visualPosition;
        [System.NonSerialized] public Vector3 visualVelocity;
        [System.NonSerialized] public bool hasVisualPosition;
        [System.NonSerialized] public float visualTravel;
        [System.NonSerialized] public bool hasVisualTravel;
        [System.NonSerialized] public bool visualTravelLogged;
        [System.NonSerialized] public float filteredSuspensionTravel;
        [System.NonSerialized] public bool hasFilteredSuspensionTravel;
        [System.NonSerialized] public bool groundDiagnosticLogged;
        [System.NonSerialized] public Vector3 filteredContactPoint;
        [System.NonSerialized] public Vector3 filteredGroundNormal;
        [System.NonSerialized] public bool hasFilteredContact;
        [System.NonSerialized] public float contactLossTime;
        [System.NonSerialized] public bool contactFresh;
        [System.NonSerialized] public bool landingContact;
    }

    readonly WheelData[] wheels = new WheelData[4];
    readonly RaycastHit[] raycastBuffer = new RaycastHit[16];
    Rigidbody body;
    Terrain runtimeTerrain;
    Vector3 requestedVelocity;
    float requestedSteer;
    float requestedBrake;
    float requestedTraction = 1f;
    bool handbrake;
    bool boostActive;
    bool wasGrounded;
    float flippedTimer;
    float stuckTimer;
    float diagnosticClock;
    bool groundStateDiagnosticLogged;
    int lastGroundedDiagnosticCount = -1;
    bool terrainFallbackLogged;
    bool terrainBindingLogged;
    bool terrainSampleLogged;
    bool terrainSnapLogged;
    float forwardSign = 1f;
    VehicleState state;

    public bool IsGrounded { get { return GroundedCount > 0; } }
    public bool HasTerrain { get { return true; } }
    public Vector3 GroundNormal { get { return AverageNormal; } }
    public float GroundHeight { get { return transform.position.y - tireRadius; } }
    public Vector3 CurrentVelocity { get { return body != null ? body.linearVelocity : Vector3.zero; } }
    public Vector3 ForwardDirection { get { return transform.right * forwardSign; } }
    public VehicleState State { get { return state; } }
    public int GroundedCount
    {
        get { int count = 0; for (int i = 0; i < wheels.Length; i++) if (wheels[i] != null && wheels[i].grounded) count++; return count; }
    }
    int FreshGroundedCount
    {
        get { int count = 0; for (int i = 0; i < wheels.Length; i++) if (wheels[i] != null && wheels[i].grounded && wheels[i].contactFresh) count++; return count; }
    }
    public Vector3 AverageNormal
    {
        get
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < wheels.Length; i++) if (wheels[i] != null && wheels[i].grounded) normal += wheels[i].groundNormal;
            return normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
        }
    }

    public void SetDriveVelocity(Vector3 velocity) { requestedVelocity = velocity; }
    public void SetSteering(float value) { requestedSteer = Mathf.Clamp(value, -1f, 1f); }
    public void SetBrake(float value) { requestedBrake = Mathf.Clamp01(value); }
    public void SetHandbrake(bool value) { handbrake = value; }
    public void SetBoost(bool value) { boostActive = value; }
    public void SetTraction(float value) { requestedTraction = Mathf.Clamp(value, 0.15f, 1.25f); }

    public void ConfigureWheelTransforms(Transform[] sourceWheels) { ConfigureWheelTransforms(sourceWheels, true, false); }
    public void ConfigureWheelTransforms(Transform[] sourceWheels, bool frontIsPositiveZ) { ConfigureWheelTransforms(sourceWheels, frontIsPositiveZ, false); }

    public void ConfigureWheelTransforms(Transform[] sourceWheels, bool frontIsPositiveAxis, bool frontAxisIsX)
    {
        if (sourceWheels == null || sourceWheels.Length != 4) return;
        var sorted = new List<Transform>(sourceWheels);
        float axisMid = 0f;
        for (int i = 0; i < sorted.Count; i++) axisMid += GetLocalAxis(sorted[i], frontAxisIsX);
        axisMid /= sorted.Count;
        sorted.Sort((a, b) =>
        {
            float aa = GetLocalAxis(a, frontAxisIsX);
            float bb = GetLocalAxis(b, frontAxisIsX);
            bool af = frontIsPositiveAxis ? aa >= axisMid : aa <= axisMid;
            bool bf = frontIsPositiveAxis ? bb >= axisMid : bb <= axisMid;
            if (af != bf) return af ? -1 : 1;
            return GetLateralAxis(a, frontAxisIsX).CompareTo(GetLateralAxis(b, frontAxisIsX));
        });
        ConfigureWheelData(sorted, null, frontAxisIsX);
    }

    public void ConfigureWheelTransforms(Transform[] sourceWheels, bool[] frontFlags)
    {
        if (sourceWheels == null || sourceWheels.Length != 4 || frontFlags == null || frontFlags.Length != 4) return;
        ConfigureWheelData(new List<Transform>(sourceWheels), frontFlags, true);
    }

    void ConfigureWheelData(List<Transform> sourceWheels, bool[] frontFlags, bool frontAxisIsX)
    {
        EnsureWheelData();
        // The authored VehicleManual places its labelled front axle on -X,
        // while the procedural fallback uses +X. Derive the sign from the
        // actual front/rear markers instead of assuming a global convention.
        float frontAxis = GetLocalAxis(sourceWheels[0], frontAxisIsX);
        float rearAxis = GetLocalAxis(sourceWheels[2], frontAxisIsX);
        if (Mathf.Abs(frontAxis - rearAxis) > 0.01f)
            forwardSign = Mathf.Sign(frontAxis - rearAxis);
        float radiusSum = 0f;
        float wheelCenterY = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (sourceWheels[i] == null) return;
            Vector3 local = transform.InverseTransformPoint(sourceWheels[i].position);
            wheels[i].localHardpoint = local + Vector3.up * suspensionLength;
            wheels[i].isFront = frontFlags == null ? i < 2 : frontFlags[i];
            wheelCenterY += local.y;
            Renderer renderer = sourceWheels[i].GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                Vector3 extents = renderer.bounds.extents;
                radiusSum += Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            }
        }
        // Vehicle.fbx tires are large: their measured radius is about 1.48m.
        // The old 1.25m ceiling made every ray terminate too high above the
        // terrain, so all four wheels became airborne and none of the custom
        // suspension, tire grip, slope hold, drive force, or trail emission
        // could operate. Keep a generous safety ceiling instead of silently
        // shrinking the imported tire.
        if (radiusSum > 0.01f) tireRadius = Mathf.Clamp(radiusSum / 4f, 0.15f, 2.5f);
        wheelBase = Mathf.Abs(wheels[0].localHardpoint.z - wheels[2].localHardpoint.z);
        trackWidth = Mathf.Abs(wheels[0].localHardpoint.x - wheels[1].localHardpoint.x);
        if (frontAxisIsX) wheelBase = Mathf.Abs(wheels[0].localHardpoint.x - wheels[2].localHardpoint.x);
        centerOfMass.y = Mathf.Min(centerOfMass.y, wheelCenterY / 4f + 0.45f);
        if (body != null) body.centerOfMass = centerOfMass;
    }

    float GetLocalAxis(Transform wheel, bool axisX) { Vector3 p = transform.InverseTransformPoint(wheel.position); return axisX ? p.x : p.z; }
    float GetLateralAxis(Transform wheel, bool axisX) { Vector3 p = transform.InverseTransformPoint(wheel.position); return axisX ? p.z : p.x; }

    public void SetTerrain(Terrain terrain)
    {
        runtimeTerrain = terrain;
        EnsureWheelData();
        if (runtimeTerrain != null && !terrainBindingLogged)
        {
            terrainBindingLogged = true;
            Debug.Log("VEHICLE TERRAIN BIND // " + runtimeTerrain.name
                + " position=" + runtimeTerrain.GetPosition().ToString("F2")
                + " size=" + (runtimeTerrain.terrainData == null ? "NO DATA" : runtimeTerrain.terrainData.size.ToString("F2")));
        }
        // BindTerrain is called after the vehicle has been built. Snap here
        // as part of the binding itself so startup cannot leave the body at
        // its pre-terrain spawn height if the caller's one-shot snap happens
        // before wheel layout initialization has completed.
        // The streamed scene may expose the ground through mesh colliders
        // before a Terrain component is available (DrivingCore intentionally
        // passes null here). Snap from the wheel raycasts regardless, so the
        // first physics step never starts with the body buried or floating.
        SnapToTerrainNow();
    }

    public void SnapToTerrainNow()
    {
        if (body == null || wheels[0] == null) return;
        Physics.SyncTransforms();
        float targetGroundY = float.MinValue;
        bool foundGround = false;
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            if (wheel == null) continue;
            Vector3 wheelCenter = transform.TransformPoint(wheel.localHardpoint - Vector3.up * suspensionLength);
            Vector3 origin = wheelCenter + Vector3.up * 64f;
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, raycastBuffer, 128f, groundLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            RaycastHit selected = default(RaycastHit);
            for (int h = 0; h < hitCount; h++)
            {
                RaycastHit hit = raycastBuffer[h];
                if (!IsExternalCollider(hit.collider) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                selected = hit;
            }
            if (nearest == float.MaxValue && TrySampleRuntimeTerrain(wheelCenter, out Vector3 terrainPoint, out Vector3 terrainNormal))
            {
                if (!terrainSnapLogged)
                {
                    terrainSnapLogged = true;
                    Debug.Log("VEHICLE TERRAIN SNAP SAMPLE // wheelCenter=" + wheelCenter.ToString("F2")
                        + " terrainPoint=" + terrainPoint.ToString("F2"));
                }
                foundGround = true;
                // targetGroundY is compared with the wheel *bottom* below,
                // so it must be the terrain height plus only the visual
                // clearance. Adding tireRadius here lifted the whole car by
                // one complete tire and left every suspension ray extended.
                targetGroundY = Mathf.Max(targetGroundY, terrainPoint.y + wheelGroundClearance);
                continue;
            }
            if (nearest == float.MaxValue) continue;
            foundGround = true;
            float wheelTargetY = selected.point.y + wheelGroundClearance;
            targetGroundY = Mathf.Max(targetGroundY, wheelTargetY);
        }
        if (!foundGround) return;

        float currentLowestWheelY = float.MaxValue;
        for (int i = 0; i < wheels.Length; i++)
        {
            if (wheels[i] == null) continue;
            Vector3 wheelCenter = transform.TransformPoint(wheels[i].localHardpoint - Vector3.up * suspensionLength);
            currentLowestWheelY = Mathf.Min(currentLowestWheelY, wheelCenter.y - tireRadius);
        }
        float deltaY = targetGroundY - currentLowestWheelY;
        if (terrainSnapLogged)
            Debug.Log("VEHICLE TERRAIN SNAP // targetGroundY=" + targetGroundY.ToString("F2")
                + " lowestWheelBottom=" + currentLowestWheelY.ToString("F2")
                + " deltaY=" + deltaY.ToString("F2"));
        body.position += Vector3.up * deltaY;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();
    }

    bool TrySampleRuntimeTerrain(Vector3 worldPoint, out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        if (runtimeTerrain == null)
        {
            runtimeTerrain = Terrain.activeTerrains != null && Terrain.activeTerrains.Length > 0
                ? Terrain.activeTerrains[0]
                : FindAnyObjectByType<Terrain>();
        }
        if (runtimeTerrain == null || runtimeTerrain.terrainData == null) return false;
        Vector3 terrainOrigin = runtimeTerrain.GetPosition();
        Vector3 size = runtimeTerrain.terrainData.size;
        float u = (worldPoint.x - terrainOrigin.x) / Mathf.Max(0.001f, size.x);
        float v = (worldPoint.z - terrainOrigin.z) / Mathf.Max(0.001f, size.z);
        if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
        point = new Vector3(worldPoint.x, runtimeTerrain.SampleHeight(worldPoint) + terrainOrigin.y, worldPoint.z);
        normal = runtimeTerrain.terrainData.GetInterpolatedNormal(Mathf.Clamp01(u), Mathf.Clamp01(v));
        if (normal.sqrMagnitude < 0.001f) normal = Vector3.up;
        return true;
    }

    void Awake()
    {
        // Older PlayerCar prefab instances can contain an empty LayerMask.
        // A zero mask makes every wheel probe miss the terrain, which removes
        // spring support, tire grip, and drive force while steering visuals
        // continue to respond. Zero therefore means the normal physics mask.
        if (groundLayers.value == 0) groundLayers = Physics.DefaultRaycastLayers;
        // Upgrade old serialized vehicle instances that still contain the
        // pre-off-road 0.28m extension value.
        // Older serialized vehicle instances contain the experimental 1.6m
        // and 2.4m rebound values. Those values made an unsupported tire look
        // grounded for metres while the chassis was actually falling, so the
        // spring could not carry the vehicle or drive it. Keep the physical
        // rebound travel in the real suspension range.
        // The authored terrain has real metre-scale height changes across one
        // wheelbase. Keep enough rebound travel to retain the rear axle over
        // a crest; a short rebound range turns ordinary slope driving into a
        // permanent two-wheel pivot.
        if (maxExtension > 1.2f || maxExtension < 0.35f) maxExtension = 0.95f;
        body = GetComponent<Rigidbody>();
        body.isKinematic = false;
        body.useGravity = true;
        body.detectCollisions = true;
        body.mass = vehicleMass;
        body.centerOfMass = centerOfMass;
        body.linearDamping = 0.15f;
        body.angularDamping = 0f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.solverIterations = 12;
        body.solverVelocityIterations = 8;
        body.sleepThreshold = 0.005f;
        body.maxAngularVelocity = maxAngularVelocity;
        wheels[0] = NewWheel(-trackWidth * 0.5f, wheelBase * 0.5f, true);
        wheels[1] = NewWheel(trackWidth * 0.5f, wheelBase * 0.5f, true);
        wheels[2] = NewWheel(-trackWidth * 0.5f, -wheelBase * 0.5f, false);
        wheels[3] = NewWheel(trackWidth * 0.5f, -wheelBase * 0.5f, false);
        EnsureWheelData();
    }

    // The vehicle prefab uses local X as longitudinal/forward and local Z as
    // lateral/left-right. Keep the fallback layout consistent with the model.
    WheelData NewWheel(float lateral, float forward, bool front) { return new WheelData { localHardpoint = new Vector3(forward, 0.15f, lateral), isFront = front, groundNormal = Vector3.up }; }

    void EnsureWheelData()
    {
        if (wheels[0] == null) wheels[0] = NewWheel(-trackWidth * 0.5f, wheelBase * 0.5f, true);
        if (wheels[1] == null) wheels[1] = NewWheel(trackWidth * 0.5f, wheelBase * 0.5f, true);
        if (wheels[2] == null) wheels[2] = NewWheel(-trackWidth * 0.5f, -wheelBase * 0.5f, false);
        if (wheels[3] == null) wheels[3] = NewWheel(trackWidth * 0.5f, -wheelBase * 0.5f, false);
    }

    void FixedUpdate()
    {
        SampleWheels();
        int groundedNow = GroundedCount;
        if (diagnosticLogging && groundedNow != lastGroundedDiagnosticCount)
        {
            lastGroundedDiagnosticCount = groundedNow;
            Debug.Log("VEHICLE CONTACT CHANGE // grounded=" + groundedNow + "/4"
                + " travels=" + wheels[0].suspensionTravel.ToString("F2") + ","
                + wheels[1].suspensionTravel.ToString("F2") + ","
                + wheels[2].suspensionTravel.ToString("F2") + ","
                + wheels[3].suspensionTravel.ToString("F2")
                + " position=" + transform.position.ToString("F1"));
        }
        if (diagnosticLogging && !groundStateDiagnosticLogged)
        {
            groundStateDiagnosticLogged = true;
            Debug.Log("VEHICLE CONTACT STATE // grounded=" + GroundedCount + "/4"
                + " travels=" + wheels[0].suspensionTravel.ToString("F3") + ","
                + wheels[1].suspensionTravel.ToString("F3") + ","
                + wheels[2].suspensionTravel.ToString("F3") + ","
                + wheels[3].suspensionTravel.ToString("F3")
                + " maxExtension=" + maxExtension.ToString("F3"));
        }
        ApplyWheelForces();
        ApplyStabilityDamping();
        ApplyAirControl();
        UpdateState();
        if (diagnosticLogging)
        {
            diagnosticClock -= Time.fixedDeltaTime;
            if (diagnosticClock <= 0f && requestedVelocity.sqrMagnitude > 1f)
            {
                diagnosticClock = 0.5f;
                Debug.Log("VEHICLE PHYSICS // target=" + requestedVelocity.ToString("F1") + " velocity=" + body.linearVelocity.ToString("F1") + " grounded=" + GroundedCount + "/4 state=" + state);
            }
        }
    }

    void SampleWheels()
    {
        EnsureWheelData();
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            if (wheel == null) continue;
            Vector3 origin = transform.TransformPoint(wheel.localHardpoint);
            float castLength = Mathf.Max(groundDetectionDistance, suspensionLength + maxExtension + tireRadius);
            bool wasGrounded = wheel.grounded;
            wheel.grounded = false;
            wheel.contactFresh = false;
            wheel.landingContact = false;
            wheel.compression = 0f;
            wheel.suspensionForce = 0f;
            wheel.tireForce = Vector3.zero;
            // Ground contact is sampled vertically. Casting along the current
            // chassis up vector can miss the terrain as soon as the rigidbody
            // has a small pitch/roll error, which makes all four wheels
            // alternate between grounded and airborne on slopes.
            // A thin sphere probe is still a point-like suspension sample, but
            // it does not disappear for one fixed step when the ray crosses a
            // triangle edge. That one-frame miss was feeding the contact
            // release hysteresis and looked like the wheel was popping.
            // The chassis collider is now a real safety collider. Start the
            // probe above it and collect all hits, then explicitly select the
            // first external collider; otherwise the vehicle's own box would
            // hide the terrain and every wheel would become airborne.
            // Start well above the wheel instead of only 2.5m above it. If a
            // transient missed sample lets the body pass below the terrain,
            // a short downward ray also starts below the ground and can never
            // recover the vehicle. The long upper probe makes suspension
            // recovery possible without using a solid chassis collider as a
            // second, launch-prone support surface.
            float probeHeight = Mathf.Max(2.5f, groundDetectionDistance);
            Vector3 probeOrigin = origin + Vector3.up * probeHeight;
            int hitCount = Physics.RaycastNonAlloc(probeOrigin, Vector3.down, raycastBuffer, castLength + probeHeight, groundLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int h = 0; h < hitCount; h++)
            {
                RaycastHit hit = raycastBuffer[h];
                if (!IsExternalCollider(hit.collider) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                wheel.hit = hit;
            }
            if (!wheel.groundDiagnosticLogged && nearest == float.MaxValue)
            {
                wheel.groundDiagnosticLogged = true;
                Debug.Log("VEHICLE WHEEL RAY // index=" + i
                    + " origin=" + origin.ToString("F3")
                    + " hitCount=" + hitCount
                    + " externalHit=false");
            }
            // TerrainCollider cooking can complete after the runtime
            // heightfield is assigned. Sample TerrainData directly during
            // that window so the vehicle still receives suspension and drive
            // forces instead of remaining permanently airborne.
            if (nearest == float.MaxValue && TrySampleRuntimeTerrain(origin, out Vector3 terrainPoint, out Vector3 terrainNormal))
            {
                float distanceAlongSuspension = origin.y - terrainPoint.y;
                float terrainTravel = suspensionLength - (distanceAlongSuspension - tireRadius);
                if (!terrainSampleLogged)
                {
                    terrainSampleLogged = true;
                    Debug.Log("VEHICLE TERRAIN SAMPLE // wheelOrigin=" + origin.ToString("F2")
                        + " terrainPoint=" + terrainPoint.ToString("F2")
                        + " travel=" + terrainTravel.ToString("F2"));
                }
                // The imported VehicleManual wheel hardpoints can be many
                // metres above the Rigidbody root. If the first terrain
                // sample is outside the physical suspension range, gravity
                // cannot reach the ground reliably before the driving solver
                // starts. Correct that one-time spawn offset immediately;
                // subsequent motion is handled by the spring normally.
                if (terrainTravel >= -maxExtension)
                {
                    if (!terrainFallbackLogged)
                    {
                        terrainFallbackLogged = true;
                        Debug.Log("VEHICLE TERRAIN FALLBACK // using TerrainData directly at "
                            + terrainPoint.ToString("F2") + " travel=" + terrainTravel.ToString("F2"));
                    }
                    wheel.grounded = true;
                    wheel.contactFresh = true;
                    wheel.landingContact = !wasGrounded;
                    wheel.contactLossTime = 0f;
                    wheel.contactPoint = terrainPoint;
                    wheel.groundNormal = terrainNormal;
                    UpdateSuspensionTravel(wheel, terrainTravel);
                    wheel.compression = SuspensionCompression(wheel.suspensionTravel);
                    if (debugDraw) Debug.DrawLine(origin, terrainPoint, Color.green);
                    continue;
                }
            }
            if (nearest < float.MaxValue)
            {
                // The probe travels in world-up. Measure travel in the same
                // stable axis; projecting onto a rolling chassis up vector
                // makes the spring length change merely because the body
                // rolled, which creates the oscillating support impulse.
                float distanceAlongSuspension = Vector3.Dot(origin - wheel.hit.point, Vector3.up);
                float travel = suspensionLength - (distanceAlongSuspension - tireRadius);
                if (!wheel.groundDiagnosticLogged)
                {
                    wheel.groundDiagnosticLogged = true;
                    Debug.Log("VEHICLE WHEEL RAY // index=" + i
                        + " origin=" + origin.ToString("F3")
                        + " hit=" + wheel.hit.collider.name
                        + " distance=" + wheel.hit.distance.ToString("F3")
                        + " rawTravel=" + travel.ToString("F3")
                        + " maxExtension=" + maxExtension.ToString("F3"));
                }
                // Over-compression is still contact. Do not flip the wheel to
                // airborne when the body settles onto a ledge or terrain
                // seam; that discontinuity is what made the visual tire jump
                // from the surface into the ground on the next frame.
                if (travel >= -maxExtension)
                {
                    wheel.grounded = true;
                    wheel.contactFresh = true;
                    wheel.landingContact = !wasGrounded;
                    wheel.contactLossTime = 0f;
                    Vector3 rawContactPoint = wheel.hit.point;
                    Vector3 rawGroundNormal = wheel.hit.normal.sqrMagnitude > 0.1f ? wheel.hit.normal.normalized : Vector3.up;
                    float contactBlend = 1f - Mathf.Exp(-contactSmoothing * Time.fixedDeltaTime);
                    if (!wheel.hasFilteredContact)
                    {
                        wheel.filteredContactPoint = rawContactPoint;
                        wheel.filteredGroundNormal = rawGroundNormal;
                        wheel.hasFilteredContact = true;
                    }
                    else
                    {
                        wheel.filteredContactPoint = Vector3.Lerp(wheel.filteredContactPoint, rawContactPoint, contactBlend);
                        wheel.filteredGroundNormal = Vector3.Slerp(wheel.filteredGroundNormal, rawGroundNormal, contactBlend).normalized;
                    }
                    // Keep the physical force point on the current terrain
                    // sample. The filtered point is only for presentation;
                    // applying spring/tire forces at a lagging point makes a
                    // moving car inject torque after it has already crossed
                    // a height change, which is the source of the jump.
                    wheel.contactPoint = rawContactPoint;
                    wheel.groundNormal = wheel.filteredGroundNormal;
                    float filteredDistance = Vector3.Dot(origin - wheel.filteredContactPoint, Vector3.up);
                    UpdateSuspensionTravel(wheel, suspensionLength - (filteredDistance - tireRadius));
                    wheel.compression = SuspensionCompression(wheel.suspensionTravel);
                }
                else
                {
                    wheel.contactLossTime += Time.fixedDeltaTime;
                    bool withinReleaseHysteresis = travel >= -(maxExtension + contactReleaseMargin);
                    wheel.grounded = wasGrounded && wheel.hasFilteredContact
                        && (withinReleaseHysteresis || wheel.contactLossTime <= contactGraceTime);
                    wheel.contactFresh = false;
                    if (!wheel.grounded) wheel.hasFilteredContact = false;
                }
            }
            else
            {
                // Terrain meshes can briefly miss exactly at a triangle seam
                // while the car advances. Do not drop the spring force for a
                // single missed sample; retain the already filtered contact
                // for a bounded grace period, then return to airborne state.
                wheel.contactLossTime += Time.fixedDeltaTime;
                wheel.grounded = wasGrounded && wheel.hasFilteredContact && wheel.contactLossTime <= contactGraceTime;
                if (!wheel.grounded) wheel.hasFilteredContact = false;
            }
            if (wheel.grounded && wheel.hasFilteredContact)
            {
                // Never use the delayed visual contact as a physical support
                // point. A delayed point can sit above or below the current
                // triangle while the vehicle is moving.
                wheel.contactPoint = wheel.hit.point;
                wheel.groundNormal = wheel.filteredGroundNormal;
                float filteredDistance = Vector3.Dot(origin - wheel.contactPoint, Vector3.up);
                UpdateSuspensionTravel(wheel, suspensionLength - (filteredDistance - tireRadius));
                wheel.compression = SuspensionCompression(wheel.suspensionTravel);
            }
            if (debugDraw) Debug.DrawLine(origin, origin + Vector3.down * castLength, wheel.grounded ? Color.green : Color.red);
        }
    }

    void UpdateSuspensionTravel(WheelData wheel, float targetTravel)
    {
        targetTravel = Mathf.Clamp(targetTravel, -maxExtension, maxCompression);
        // The spring must use the current physical compression. Filtering
        // this value adds phase lag: the body falls first, the spring reacts
        // one or more fixed steps later, and the delayed force launches it
        // back up. Visual wheel motion has its own smoothing in
        // GetWheelVisualPosition, so the physical state does not need this
        // second lagging filter.
        wheel.filteredSuspensionTravel = targetTravel;
        wheel.hasFilteredSuspensionTravel = true;
        wheel.suspensionTravel = targetTravel;
    }

    float SuspensionCompression(float travel)
    {
        // Negative travel is extension, not compression. Treating it as a
        // non-zero compression was the impulse that launched the chassis.
        return Mathf.Clamp01(Mathf.InverseLerp(0f, maxCompression, Mathf.Max(0f, travel)));
    }

    bool IsExternalCollider(Collider collider) { return collider != null && collider.transform != transform && !collider.transform.IsChildOf(transform); }

    void ApplyWheelForces()
    {
        // Do not discard a real contact just because the other wheels are in
        // the air. A wheel is a point support: its spring force is applied at
        // the contact point, so the Rigidbody receives the correct tipping
        // torque from an uneven load. The old early return removed that force
        // and left the chassis box with excessive angular damping, which made
        // a one-wheel vehicle appear unnaturally balanced.
        float steerAngle = EffectiveSteeringAngle();
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            // A grace-period contact may keep the visual tire from popping,
            // but it is not allowed to create physical support or propulsion.
            if (!wheel.grounded || !wheel.contactFresh) continue;
            Vector3 up = wheel.groundNormal;
            Vector3 pointVelocity = body.GetPointVelocity(wheel.contactPoint);
            float travel = Mathf.Clamp(wheel.suspensionTravel, -maxExtension, maxCompression);
            float springVelocity = Vector3.Dot(pointVelocity, up);
            float springForce = Mathf.Max(0f, travel) * springStrength;
            // A damper cannot push a fully extended wheel away from the
            // terrain. Only apply damping while the spring has compression.
            // Use the critical-damping value for the load carried by the
            // currently live tires. With only two tires down, the effective
            // mass per tire is larger than the four-tire value; using the
            // inspector value alone made the chassis bounce indefinitely.
            float effectiveMassPerWheel = body.mass / Mathf.Max(1f, FreshGroundedCount);
            float criticalDamper = 2f * Mathf.Sqrt(Mathf.Max(0f, springStrength * effectiveMassPerWheel));
            float dampingCoefficient = Mathf.Max(damper, criticalDamper * suspensionDampingRatio);
            float dampingForce = springForce > 0f ? -springVelocity * dampingCoefficient : 0f;
            wheel.suspensionForce = Mathf.Clamp(
                springForce + dampingForce,
                0f, maxSuspensionForce);
            body.AddForceAtPosition(up * wheel.suspensionForce, wheel.contactPoint, ForceMode.Force);
            if (wheel.landingContact && landingBounceStrength > 0f)
            {
                float incomingSpeed = Mathf.Max(0f, -Vector3.Dot(pointVelocity, up));
                if (incomingSpeed > 0.5f)
                    body.AddForceAtPosition(up * body.mass * incomingSpeed * landingBounceStrength, wheel.contactPoint, ForceMode.Impulse);
            }

            Vector3 forward = Vector3.ProjectOnPlane(ForwardDirection, up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            forward.Normalize();
            if (wheel.isFront) forward = Quaternion.AngleAxis(requestedSteer * steerAngle * forwardSign, up) * forward;
            Vector3 side = Vector3.Cross(up, forward).normalized;
            float forwardSpeed = Vector3.Dot(pointVelocity, forward);
            float sideSpeed = Vector3.Dot(pointVelocity, side);
            float targetSpeed = Vector3.Dot(requestedVelocity, forward);
            // The old extra 0.5 multiplier made the vehicle lose to mild
            // grades. This is already the force of one of four wheels.
            // Use a softer velocity controller so the chassis does not
            // repeatedly overshoot its target speed and feed that oscillation
            // back into the wheel suspension.
            float driveAcceleration = acceleration + (boostActive ? boostAcceleration * body.mass : 0f);
            float drive = Mathf.Clamp((targetSpeed - forwardSpeed) * body.mass * 3.5f, -driveAcceleration, driveAcceleration);
            drive *= requestedTraction;
            bool brakingActive = handbrake || requestedBrake > 0.05f;
            float brakeInput = handbrake ? 1f : requestedBrake;
            float dynamicBrake = -forwardSpeed * brakeForce * brakeInput;
            float staticBrakeCap = brakeForce * brakeInput;
            float brake = Mathf.Clamp(dynamicBrake, -staticBrakeCap, staticBrakeCap);
            // A raycast wheel has no collider normal force. Its only physical
            // load is the spring force, so an extended wheel cannot magically
            // generate static friction or drive the chassis uphill.
            // A raycast tire has no PhysX contact normal force. Immediately
            // after a positional ground correction the spring can briefly
            // report zero even though this wheel is genuinely in contact;
            // using zero here makes W do nothing while air-control still
            // allows steering. Keep a conservative static-load floor for
            // loaded tires, while retaining the measured spring load when it
            // is available.
            float normalLoad = Mathf.Max(0f, wheel.suspensionForce);
            float lateralLimit = normalLoad * tireGrip * requestedTraction;
            float lateral = Mathf.Clamp(-sideSpeed * body.mass * lateralFriction, -lateralLimit, lateralLimit);
            float tractionLimit = normalLoad * tireGrip * requestedTraction;
            drive = Mathf.Clamp(drive, -tractionLimit, tractionLimit);
            float longitudinalForce = drive + brake;
            wheel.tireForce = forward * longitudinalForce + side * lateral;
            body.AddForceAtPosition(wheel.tireForce, wheel.contactPoint, ForceMode.Force);
            if (debugDraw)
            {
                Debug.DrawRay(wheel.contactPoint, up * (wheel.suspensionForce / Mathf.Max(1f, springStrength)), Color.yellow);
                Debug.DrawRay(wheel.contactPoint, wheel.tireForce / body.mass * 0.02f, Color.cyan);
            }
        }

    }

    float EffectiveSteeringAngle()
    {
        float speed = Mathf.Abs(Vector3.Dot(body.linearVelocity, ForwardDirection));
        return Mathf.Lerp(steeringAngle, steeringAngle * highSpeedSteeringLimit, Mathf.InverseLerp(4f, maxSpeed, speed));
    }

    void ApplyStabilityDamping()
    {
        // Rigidbody.angularDamping affects every rotation axis. Keep it at
        // zero and damp only unwanted yaw after steering is released, so
        // rollovers and recovery retain their physical angular speed.
        if (stabilityYawDamping <= 0f || FreshGroundedCount < 2 || Mathf.Abs(requestedSteer) > 0.04f)
            return;

        Vector3 yawAxis = AverageNormal.sqrMagnitude > 0.001f
            ? AverageNormal.normalized
            : Vector3.up;
        float lateralSpeed = Mathf.Abs(Vector3.Dot(body.linearVelocity, transform.forward));
        float slipFactor = Mathf.InverseLerp(0.35f, 1.5f, lateralSpeed);
        float yawRate = Vector3.Dot(body.angularVelocity, yawAxis);
        body.AddTorque(-yawAxis * yawRate * body.mass * stabilityYawDamping * (1f - slipFactor), ForceMode.Force);
    }

    void ApplyAirControl()
    {
        // Moderate damping reduces endless drift and spin, but remains far
        // below the old anti-roll value that prevented the vehicle flipping.
        body.linearDamping = FreshGroundedCount >= 2 ? 0.15f : airDrag;
        // Anti-roll resistance acts on angle; damping stays low so a real flip
        // is not merely slowed down.
        body.angularDamping = 0f;
        // A one-wheel or two-wheel contact is still a grounded vehicle. Do not
        // add airborne steering torque on top of tire contact forces, otherwise
        // a transient raycast miss turns a normal corner into a spin.
        if (GroundedCount == 0)
        {
            body.AddTorque(transform.up * requestedSteer * airControlStrength * body.mass, ForceMode.Force);
        }
    }

    void UpdateState()
    {
        bool grounded = GroundedCount > 0;
        bool landing = grounded && !wasGrounded;
        if (Vector3.Dot(transform.up, Vector3.up) < -flippedAngle)
        {
            flippedTimer += Time.fixedDeltaTime;
            state = VehicleState.Flipped;
        }
        else if (landing) state = VehicleState.Landing;
        else if (grounded && body.linearVelocity.magnitude < 0.35f && requestedVelocity.sqrMagnitude > 4f)
        {
            stuckTimer += Time.fixedDeltaTime;
            state = stuckTimer > 1.5f ? VehicleState.Stuck : VehicleState.PartialGrounded;
        }
        else if (GroundedCount == 4) { stuckTimer = 0f; state = VehicleState.Grounded; }
        else if (grounded) state = VehicleState.PartialGrounded;
        else { stuckTimer = 0f; state = VehicleState.Airborne; }
        wasGrounded = grounded;
    }

    public bool TryGetWheelContact(int index, out Vector3 point, out Vector3 normal, out bool grounded, out string surfaceName)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        grounded = false;
        surfaceName = string.Empty;
        if (index < 0 || index >= wheels.Length) return false;
        WheelData wheel = wheels[index];
        if (wheel == null) return false;
        grounded = wheel.grounded;
        if (!grounded) return false;
        point = wheel.contactPoint;
        normal = wheel.groundNormal;
        surfaceName = wheel.hit.collider != null ? wheel.hit.collider.name : string.Empty;
        return true;
    }

    public Vector3 GetWheelVisualPosition(int index)
    {
        if (index < 0 || index >= wheels.Length || wheels[index] == null)
            return transform.position;
        WheelData wheel = wheels[index];
        float targetTravel = wheel.grounded
            ? Mathf.Clamp(wheel.suspensionTravel, -maxExtension, maxCompression)
            : -maxExtension;
        float visualBlend;
        if (!wheel.hasVisualTravel)
        {
            wheel.visualTravel = targetTravel;
            wheel.hasVisualTravel = true;
            visualBlend = 1f;
        }
        else
        {
            visualBlend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, suspensionVisualSmoothTime));
            wheel.visualTravel = Mathf.Lerp(wheel.visualTravel, targetTravel, visualBlend);
        }

        Vector3 position = transform.TransformPoint(wheel.localHardpoint - Vector3.up *
            (suspensionLength - Mathf.Clamp(wheel.visualTravel, -maxExtension, maxCompression)));
        if (!wheel.visualTravelLogged)
        {
            wheel.visualTravelLogged = true;
            Debug.Log("VEHICLE WHEEL VISUAL // index=" + index
                + " grounded=" + wheel.grounded
                + " targetTravel=" + targetTravel.ToString("F4")
                + " visualTravel=" + wheel.visualTravel.ToString("F4")
                + " tireRadius=" + tireRadius.ToString("F4")
                + " position=" + position.ToString("F4"));
        }
        return position;
    }

    public void StopImmediately()
    {
        if (body != null) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        requestedVelocity = Vector3.zero;
        requestedBrake = 1f;
        handbrake = true;
    }

    public void ResetCar(Vector3 position)
    {
        if (body == null) return;
        body.position = position;
        body.rotation = Quaternion.identity;
        StopImmediately();
        flippedTimer = 0f;
        stuckTimer = 0f;
    }

    void OnDrawGizmosSelected()
    {
        if (!debugDraw) return;
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(transform.TransformPoint(centerOfMass), 0.08f);
        for (int i = 0; i < wheels.Length; i++) if (wheels[i] != null) Gizmos.DrawWireSphere(transform.TransformPoint(wheels[i].localHardpoint), 0.08f);
    }
}
