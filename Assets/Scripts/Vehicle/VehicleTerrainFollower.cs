using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(1000)]
[RequireComponent(typeof(Rigidbody))]
public sealed class VehicleTerrainFollower : MonoBehaviour
{
    public enum VehicleState { Grounded, PartialGrounded, Airborne, Landing, Flipped, Stuck }

    [Header("Vehicle")]
    public float vehicleMass = 1000f;
    public Vector3 centerOfMass = new Vector3(0f, -1.10f, 0f);
    public float maxSpeed = 32f;
    public float acceleration = 18000f;
    public float brakeForce = 22000f;
    public float steeringAngle = 24f;
    public float highSpeedSteeringLimit = 0.75f;
    public float airDrag = 0.12f;
    public float angularDamping = 0.35f;
    public float steeringResponse = 8f;
    public float lateralVelocityResponse = 18f;
    public float lowSpeedSteeringSpeed = 2.5f;

    [Header("Suspension")]
    public float suspensionLength = 0.62f;
    public float maxCompression = 0.20f;
    public float maxExtension = 0.95f;
    public float springStrength = 42000f;
    public float damper = 11000f;
    // Raycast suspension already supplies the landing support. Extra bounce
    // makes a turning car launch when contact changes between terrain tiles.
    public float landingBounceStrength = 0f;
    public float tireRadius = 0.38f;
    public float wheelGroundClearance = 0.045f;

    [Header("Tires")]
    public float wheelBase = 2.84f;
    public float trackWidth = 1.84f;
    public float tireGrip = 1.1f;
    // Strong lateral response is intentional: this is an arcade-style
    // raycast tire, so it must cancel body side velocity before the vehicle
    // feels like it is skating across the ground.
    public float lateralFriction = 8f;
    public LayerMask groundLayers = ~0;
    public float groundDetectionDistance = 32f;

    [Header("Recovery")]
    public float flippedAngle = 0.35f;

    [Header("Debug")]
    public bool debugDraw;

    [System.Serializable]
    public sealed class WheelData
    {
        public Vector3 localHardpoint;
        public bool isFront;
        public bool grounded;
        [System.NonSerialized] public RaycastHit hit;
        public float suspensionTravel;
        public float suspensionForce;
        public Vector3 tireForce;
        public Vector3 contactPoint;
        public Vector3 groundNormal;
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
    bool wasGrounded;
    float stuckTimer;
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
        get
        {
            int count = 0;
            for (int i = 0; i < wheels.Length; i++)
                if (wheels[i] != null && wheels[i].grounded) count++;
            return count;
        }
    }

    public Vector3 AverageNormal
    {
        get
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < wheels.Length; i++)
                if (wheels[i] != null && wheels[i].grounded) normal += wheels[i].groundNormal;
            return normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
        }
    }

    public void SetDriveVelocity(Vector3 velocity) { requestedVelocity = velocity; }
    public void SetSteering(float value) { requestedSteer = Mathf.Clamp(value, -1f, 1f); }
    public void SetBrake(float value) { requestedBrake = Mathf.Clamp01(value); }
    public void SetHandbrake(bool value) { handbrake = value; }
    public void SetTraction(float value) { requestedTraction = Mathf.Clamp(value, 0.15f, 1.25f); }

    public void ConfigureWheelTransforms(Transform[] sourceWheels)
    {
        ConfigureWheelTransforms(sourceWheels, true, false);
    }

    public void ConfigureWheelTransforms(Transform[] sourceWheels, bool frontIsPositiveZ)
    {
        ConfigureWheelTransforms(sourceWheels, frontIsPositiveZ, false);
    }

    public void ConfigureWheelTransforms(Transform[] sourceWheels, bool frontIsPositiveAxis, bool frontAxisIsX)
    {
        if (sourceWheels == null || sourceWheels.Length != 4) return;
        var sorted = new List<Transform>(sourceWheels);
        float midpoint = 0f;
        for (int i = 0; i < sorted.Count; i++) midpoint += Axis(sorted[i], frontAxisIsX);
        midpoint /= sorted.Count;
        sorted.Sort((a, b) =>
        {
            float aa = Axis(a, frontAxisIsX);
            float bb = Axis(b, frontAxisIsX);
            bool af = frontIsPositiveAxis ? aa >= midpoint : aa <= midpoint;
            bool bf = frontIsPositiveAxis ? bb >= midpoint : bb <= midpoint;
            if (af != bf) return af ? -1 : 1;
            return LateralAxis(a, frontAxisIsX).CompareTo(LateralAxis(b, frontAxisIsX));
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
        if (sourceWheels.Count != 4) return;
        float frontAxis = Axis(sourceWheels[0], frontAxisIsX);
        float rearAxis = Axis(sourceWheels[2], frontAxisIsX);
        if (Mathf.Abs(frontAxis - rearAxis) > 0.01f)
            forwardSign = Mathf.Sign(frontAxis - rearAxis);

        float radiusSum = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (sourceWheels[i] == null) return;
            Vector3 local = transform.InverseTransformPoint(sourceWheels[i].position);
            wheels[i].localHardpoint = local + Vector3.up * suspensionLength;
            wheels[i].isFront = frontFlags == null ? i < 2 : frontFlags[i];
            Renderer renderer = sourceWheels[i].GetComponentInChildren<Renderer>();
            if (renderer != null)
                radiusSum += Mathf.Max(renderer.bounds.extents.x,
                    Mathf.Max(renderer.bounds.extents.y, renderer.bounds.extents.z));
        }
        if (radiusSum > 0.01f) tireRadius = Mathf.Clamp(radiusSum / 4f, 0.15f, 2.5f);
        wheelBase = Mathf.Abs(wheels[0].localHardpoint.z - wheels[2].localHardpoint.z);
        trackWidth = Mathf.Abs(wheels[0].localHardpoint.x - wheels[1].localHardpoint.x);
        if (frontAxisIsX)
        {
            wheelBase = Mathf.Abs(wheels[0].localHardpoint.x - wheels[2].localHardpoint.x);
            trackWidth = Mathf.Abs(wheels[0].localHardpoint.z - wheels[1].localHardpoint.z);
        }
        if (body != null) body.centerOfMass = centerOfMass;
    }

    float Axis(Transform value, bool axisX)
    {
        Vector3 local = transform.InverseTransformPoint(value.position);
        return axisX ? local.x : local.z;
    }

    float LateralAxis(Transform value, bool axisX)
    {
        Vector3 local = transform.InverseTransformPoint(value.position);
        return axisX ? local.z : local.x;
    }

    void Awake()
    {
        if (groundLayers.value == 0) groundLayers = Physics.DefaultRaycastLayers;
        body = GetComponent<Rigidbody>();
        body.isKinematic = false;
        body.useGravity = true;
        body.detectCollisions = true;
        body.mass = vehicleMass;
        body.centerOfMass = centerOfMass;
        body.linearDamping = 0.15f;
        body.angularDamping = angularDamping;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.solverIterations = 12;
        body.solverVelocityIterations = 8;
        body.sleepThreshold = 0.005f;
        body.maxAngularVelocity = 100f;
        EnsureWheelData();
    }

    WheelData NewWheel(float lateral, float longitudinal, bool front)
    {
        return new WheelData
        {
            localHardpoint = new Vector3(longitudinal, 0.15f, lateral),
            isFront = front,
            groundNormal = Vector3.up
        };
    }

    void EnsureWheelData()
    {
        if (wheels[0] == null) wheels[0] = NewWheel(-trackWidth * 0.5f, wheelBase * 0.5f, true);
        if (wheels[1] == null) wheels[1] = NewWheel(trackWidth * 0.5f, wheelBase * 0.5f, true);
        if (wheels[2] == null) wheels[2] = NewWheel(-trackWidth * 0.5f, -wheelBase * 0.5f, false);
        if (wheels[3] == null) wheels[3] = NewWheel(trackWidth * 0.5f, -wheelBase * 0.5f, false);
    }

    public void BindTerrain(Terrain terrain)
    {
        runtimeTerrain = terrain;
        EnsureWheelData();
        SnapToTerrainNow();
    }

    // Keep compatibility with older bootstrap/prefab assemblies while all
    // current callers use BindTerrain.
    public void SetTerrain(Terrain terrain)
    {
        BindTerrain(terrain);
    }

    void FixedUpdate()
    {
        SampleWheels();
        ResolveWheelPenetration();
        ApplyWheelForces();
        ApplyDamping();
        UpdateState();
    }

    void ResolveWheelPenetration()
    {
        // This vehicle intentionally has no chassis Collider. Forces alone
        // cannot undo a deep penetration after a fast terrain step, so keep
        // the tire center above the maximum suspension-compression plane.
        float deepest = 0f;
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            if (wheel == null || !wheel.grounded) continue;
            Vector3 origin = transform.TransformPoint(wheel.localHardpoint);
            float distance = origin.y - wheel.contactPoint.y;
            float minimumDistance = suspensionLength - maxCompression + tireRadius;
            deepest = Mathf.Max(deepest, minimumDistance - distance);
        }

        if (deepest <= 0f) return;
        float correction = Mathf.Min(deepest, 0.35f);
        body.position += Vector3.up * correction;
        if (body.linearVelocity.y < 0f) body.linearVelocity = new Vector3(
            body.linearVelocity.x, 0f, body.linearVelocity.z);
        Physics.SyncTransforms();
    }

    void SampleWheels()
    {
        EnsureWheelData();
        float castLength = Mathf.Max(groundDetectionDistance, suspensionLength + maxExtension + tireRadius);
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            Vector3 origin = transform.TransformPoint(wheel.localHardpoint);
            bool wasWheelGrounded = wheel.grounded;
            wheel.grounded = false;
            wheel.contactFresh = false;
            wheel.landingContact = false;
            wheel.suspensionForce = 0f;
            wheel.tireForce = Vector3.zero;

            Vector3 castOrigin = origin + Vector3.up * groundDetectionDistance;
            // Sweep a small tire area so a generated-mesh seam cannot make a
            // wheel lose contact for one frame.
            int hitCount = Physics.SphereCastNonAlloc(castOrigin, tireRadius * 0.35f,
                Vector3.down, raycastBuffer, castLength + groundDetectionDistance,
                groundLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int h = 0; h < hitCount; h++)
            {
                RaycastHit hit = raycastBuffer[h];
                if (hit.collider == null || hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    wheel.hit = hit;
                }
            }

            Vector3 contactPoint;
            Vector3 normal;
            bool found = nearest < float.MaxValue;
            if (found)
            {
                contactPoint = wheel.hit.point;
                normal = wheel.hit.normal.sqrMagnitude > 0.01f ? wheel.hit.normal.normalized : Vector3.up;
            }
            else
            {
                found = TrySampleRuntimeTerrain(origin, out contactPoint, out normal);
            }

            if (found)
            {
                float distance = origin.y - contactPoint.y;
                float travel = Mathf.Clamp(suspensionLength - (distance - tireRadius), -maxExtension, maxCompression);
                if (distance - tireRadius <= suspensionLength + maxExtension)
                {
                    wheel.grounded = true;
                    wheel.contactFresh = true;
                    wheel.landingContact = !wasWheelGrounded;
                    wheel.contactPoint = contactPoint;
                    wheel.groundNormal = normal;
                    wheel.suspensionTravel = travel;
                }
            }

            if (debugDraw) Debug.DrawLine(origin, origin + Vector3.down * castLength, wheel.grounded ? Color.green : Color.red);
        }
    }

    bool TrySampleRuntimeTerrain(Vector3 worldPoint, out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        if (runtimeTerrain == null)
            runtimeTerrain = Terrain.activeTerrains != null && Terrain.activeTerrains.Length > 0
                ? Terrain.activeTerrains[0] : FindAnyObjectByType<Terrain>();
        if (runtimeTerrain == null || runtimeTerrain.terrainData == null) return false;
        Vector3 origin = runtimeTerrain.GetPosition();
        Vector3 size = runtimeTerrain.terrainData.size;
        float u = (worldPoint.x - origin.x) / Mathf.Max(0.001f, size.x);
        float v = (worldPoint.z - origin.z) / Mathf.Max(0.001f, size.z);
        if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
        point = new Vector3(worldPoint.x, runtimeTerrain.SampleHeight(worldPoint) + origin.y, worldPoint.z);
        normal = runtimeTerrain.terrainData.GetInterpolatedNormal(Mathf.Clamp01(u), Mathf.Clamp01(v));
        if (normal.sqrMagnitude < 0.001f) normal = Vector3.up;
        return true;
    }

    void ApplyWheelForces()
    {
        int grounded = GroundedCount;
        if (grounded == 0) return;

        float speed = Mathf.Abs(Vector3.Dot(body.linearVelocity, ForwardDirection));
        float steerAngle = Mathf.Lerp(steeringAngle, steeringAngle * highSpeedSteeringLimit,
            Mathf.InverseLerp(4f, maxSpeed, speed));
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelData wheel = wheels[i];
            if (!wheel.grounded || !wheel.contactFresh) continue;
            Vector3 up = wheel.groundNormal;
            Vector3 pointVelocity = body.GetPointVelocity(wheel.contactPoint);
            float spring = Mathf.Max(0f, wheel.suspensionTravel) * springStrength;
            float damping = spring > 0f ? -Vector3.Dot(pointVelocity, up) * damper : 0f;
            // On a slope, a force equal to the vehicle weight has a smaller
            // vertical component because it follows the surface normal. Scale
            // the per-wheel load by the average normal's vertical component
            // so gravity cannot slowly push the body through the terrain.
            // The flat-ground limit remains exactly one vehicle weight.
            float slopeSupportScale = 1f / Mathf.Max(0.35f, Vector3.Dot(AverageNormal, Vector3.up));
            float supportLimit = body.mass * Physics.gravity.magnitude * slopeSupportScale / grounded;
            // A raycast wheel has no Collider contact normal. At full
            // extension the spring term is zero even though the ray has a
            // valid ground contact, which previously made both drive and
            // steering traction collapse to zero while GroundedCount still
            // reported four wheels. Give every live wheel its share of the
            // vehicle weight, then add measured suspension load on top.
            float staticLoad = body.mass * Physics.gravity.magnitude * slopeSupportScale / grounded;
            wheel.suspensionForce = Mathf.Clamp(
                Mathf.Max(staticLoad, spring + damping), 0f, supportLimit);
            // Apply the arcade support at the rigidbody center. Contact-point
            // application creates roll torque when one ray changes state,
            // which makes the other wheels alternately lose and regain grip.
            body.AddForce(up * wheel.suspensionForce, ForceMode.Force);

            if (wheel.landingContact && landingBounceStrength > 0f)
            {
                float impactSpeed = Mathf.Max(0f, -Vector3.Dot(pointVelocity, up));
                if (impactSpeed > 0.5f)
                    body.AddForce(up * body.mass * impactSpeed * landingBounceStrength,
                        ForceMode.Impulse);
            }

            Vector3 forward = Vector3.ProjectOnPlane(ForwardDirection, up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.ProjectOnPlane(transform.right, up);
            forward.Normalize();
            if (wheel.isFront)
                forward = Quaternion.AngleAxis(requestedSteer * steerAngle * forwardSign, up) * forward;
            Vector3 side = Vector3.Cross(up, forward).normalized;
            float forwardSpeed = Vector3.Dot(pointVelocity, forward);
            float sideSpeed = Vector3.Dot(pointVelocity, side);
            float targetSpeed = Vector3.Dot(requestedVelocity, forward);
            float drive = Mathf.Clamp((targetSpeed - forwardSpeed) * body.mass * 3.5f,
                -acceleration, acceleration) * requestedTraction;
            // During a turn, reserve the front tires for lateral force. Keep
            // the total requested drive approximately unchanged by shifting
            // the unused front share to the rear tires.
            float steeringLoad = Mathf.Abs(requestedSteer);
            float frontDriveScale = Mathf.Lerp(1f, 0.15f, steeringLoad);
            drive *= wheel.isFront ? frontDriveScale : 2f - frontDriveScale;
            float brakeInput = handbrake ? 1f : requestedBrake;
            float brake = Mathf.Clamp(-forwardSpeed * brakeForce * brakeInput,
                -brakeForce * brakeInput, brakeForce * brakeInput);
            float normalLoad = Mathf.Max(0f, wheel.suspensionForce);
            float tractionLimit = normalLoad * tireGrip * requestedTraction;
            // Reserve the tire budget for correcting side slip first. The
            // previous order let W consume nearly all traction, leaving the
            // front tires with no cornering force during a turn. Do not apply
            // the old per-wheel rollover limit here: it divided the available
            // lateral force by loadShare once more (about 0.25 per wheel on a
            // flat four-wheel stance), making steering almost ineffective.
            float lateralLimit = tractionLimit;
            float lateral = Mathf.Clamp(-sideSpeed * body.mass * lateralFriction,
                -lateralLimit, lateralLimit);
            // Keep forward acceleration independent from lateral correction.
            // A friction-circle clamp here made minor side velocity consume
            // nearly all of the available W drive.
            float longitudinal = Mathf.Clamp(drive + brake, -acceleration, acceleration);
            wheel.tireForce = forward * longitudinal + side * lateral;
            // Keep tire forces torque-free as well. Steering yaw is controlled
            // explicitly below, so wheel contact offsets must not lift a
            // single corner of this raycast-only vehicle.
            body.AddForce(wheel.tireForce, ForceMode.Force);

            if (debugDraw)
            {
                Debug.DrawRay(wheel.contactPoint, up * (wheel.suspensionForce / Mathf.Max(1f, springStrength)), Color.yellow);
                Debug.DrawRay(wheel.contactPoint, wheel.tireForce / Mathf.Max(1f, body.mass) * 0.02f, Color.cyan);
            }
        }
    }

    void ApplyDamping()
    {
        body.linearDamping = GroundedCount >= 2 ? 0.15f : airDrag;
        body.angularDamping = angularDamping;

        if (GroundedCount < 2) return;

        Vector3 up = AverageNormal;
        float yawRate = Vector3.Dot(body.angularVelocity, up);
        // Dampen yaw only through the vehicle's vertical axis. Roll and pitch
        // are left to suspension forces and the Rigidbody's normal damping.
        body.AddTorque(-up * yawRate * body.mass * 0.08f, ForceMode.Force);

        Vector3 forward = Vector3.ProjectOnPlane(ForwardDirection, up);
        Vector3 velocity = Vector3.ProjectOnPlane(body.linearVelocity, up);
        if (forward.sqrMagnitude > 0.001f)
        {
            forward.Normalize();
            // This is an arcade raycast vehicle, not a WheelCollider setup.
            // Explicitly remove the component of velocity perpendicular to
            // the car's current heading so the body cannot keep skating after
            // the steering input is released or while only two tires are
            // temporarily touching an uneven terrain tile.
            Vector3 lateralVelocity = velocity - forward * Vector3.Dot(velocity, forward);
            float lateralBlend = 1f - Mathf.Exp(-lateralVelocityResponse * Time.fixedDeltaTime);
            body.linearVelocity -= lateralVelocity * lateralBlend;
            velocity = Vector3.ProjectOnPlane(body.linearVelocity, up);
            float forwardSpeed = Mathf.Abs(Vector3.Dot(velocity, forward));
            // Steering authority must not disappear when the vehicle is
            // crawling or is temporarily supported by only two raycast
            // wheels. Without a floor, desiredYawRate is almost zero and a
            // valid A/D input looks broken until the car reaches speed.
            float steeringSpeed = Mathf.Max(forwardSpeed, lowSpeedSteeringSpeed);
            float effectiveAngle = Mathf.Lerp(steeringAngle,
                steeringAngle * highSpeedSteeringLimit,
                Mathf.InverseLerp(4f, maxSpeed, forwardSpeed)) * Mathf.Deg2Rad;
            float desiredYawRate = requestedSteer * steeringSpeed
                * Mathf.Tan(effectiveAngle) / Mathf.Max(0.5f, wheelBase);
            desiredYawRate = Mathf.Clamp(desiredYawRate, -3.5f, 3.5f);

            // Tire forces remove lateral slip. Follow the requested yaw rate
            // directly because this raycast vehicle has no native wheel
            // contact constraint whose steering torque can be relied on.
            // Using MoveTowards here avoids requiring visible side slip before
            // the body can begin turning, while preserving roll and pitch.
            float yawBlend = 1f - Mathf.Exp(-steeringResponse * Time.fixedDeltaTime);
            Vector3 nonYawAngularVelocity = body.angularVelocity - up * yawRate;
            float correctedYawRate = Mathf.Lerp(yawRate, desiredYawRate, yawBlend);
            body.angularVelocity = nonYawAngularVelocity + up * correctedYawRate;
        }
        if (Mathf.Abs(requestedSteer) < 0.01f && velocity.sqrMagnitude > 4f
            && forward.sqrMagnitude > 0.001f
            && Vector3.Dot(velocity.normalized, forward.normalized) > 0.96f)
        {
            float headingError = Vector3.SignedAngle(forward, velocity, up) * Mathf.Deg2Rad;
            body.AddTorque(up * headingError * body.mass * 0.45f, ForceMode.Force);
        }
    }

    void UpdateState()
    {
        bool grounded = GroundedCount > 0;
        bool landing = grounded && !wasGrounded;
        if (Vector3.Dot(transform.up, Vector3.up) < -flippedAngle)
        {
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

    public bool TryGetWheelContact(int index, out Vector3 point, out Vector3 normal,
        out bool grounded, out string surfaceName)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        grounded = false;
        surfaceName = string.Empty;
        if (index < 0 || index >= wheels.Length || wheels[index] == null) return false;
        WheelData wheel = wheels[index];
        grounded = wheel.grounded;
        if (!grounded) return false;
        point = wheel.contactPoint;
        normal = wheel.groundNormal;
        surfaceName = wheel.hit.collider != null ? wheel.hit.collider.name : string.Empty;
        return true;
    }

    public void StopImmediately()
    {
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
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
        stuckTimer = 0f;
    }

    public void SnapToTerrainNow()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        EnsureWheelData();
        float highestGround = float.MinValue;
        bool found = false;
        for (int i = 0; i < wheels.Length; i++)
        {
            Vector3 origin = transform.TransformPoint(wheels[i].localHardpoint);
            Vector3 castOrigin = origin + Vector3.up * groundDetectionDistance;
            int hitCount = Physics.SphereCastNonAlloc(castOrigin, tireRadius * 0.35f,
                Vector3.down, raycastBuffer, groundDetectionDistance * 2f,
                groundLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            Vector3 hitPoint = Vector3.zero;
            for (int h = 0; h < hitCount; h++)
            {
                RaycastHit hit = raycastBuffer[h];
                if (hit.collider == null || hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    hitPoint = hit.point;
                }
            }
            if (nearest < float.MaxValue)
            {
                highestGround = Mathf.Max(highestGround, hitPoint.y + wheelGroundClearance);
                found = true;
            }
            else if (TrySampleRuntimeTerrain(origin, out Vector3 point, out Vector3 normal))
            {
                highestGround = Mathf.Max(highestGround, point.y + wheelGroundClearance);
                found = true;
            }
        }
        if (!found) return;
        float lowestBottom = float.MaxValue;
        for (int i = 0; i < wheels.Length; i++)
        {
            Vector3 wheel = transform.TransformPoint(wheels[i].localHardpoint - Vector3.up * suspensionLength);
            lowestBottom = Mathf.Min(lowestBottom, wheel.y - tireRadius);
        }
        body.position += Vector3.up * (highestGround - lowestBottom);
        StopImmediately();
        Physics.SyncTransforms();
    }

    void OnDrawGizmosSelected()
    {
        if (!debugDraw) return;
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(transform.TransformPoint(centerOfMass), 0.08f);
        for (int i = 0; i < wheels.Length; i++)
            if (wheels[i] != null) Gizmos.DrawWireSphere(transform.TransformPoint(wheels[i].localHardpoint), 0.08f);
    }
}
