using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Original vehicle camera framing with an independent orbit around the vehicle.
/// Camera yaw/pitch never feeds back into vehicle input or vehicle rotation.
/// </summary>
[DefaultExecutionOrder(1000)]
public class FollowCamera : MonoBehaviour
{
    [Header("Target and distance")]
    public Transform target;
    public float distance = 10.5f;
    public float targetHeight = 1.05f;
    public float vehicleHeight = 1.15f;
    public float minDistance = 4f;
    [Tooltip("Absolute maximum distance reached by mouse-wheel zoom-out.")]
    public float maxDistance = 160f;
    public float wheelZoomStep = 0.0015f;
    public float zoomSmooth = 12f;
    public float defaultYaw = 0f;
    public float defaultPitch = 65f;

    [Header("On-foot overhead view")]
    public float onFootPitch = 68f;

    [Header("Vehicle tracking")]
    [Tooltip("How far ahead of the vehicle root the chase camera looks.")]
    public float vehicleFocusForward = 3.2f;
    [Tooltip("Additional height of the vehicle focus point.")]
    public float vehicleFocusHeight = 1.9f;
    [Tooltip("Seconds of forward motion used to create a subtle delayed follow.")]
    public float vehicleTrackingLead = 0.14f;
    [Tooltip("How quickly the inertial tracking offset settles.")]
    public float vehicleTrackingSmooth = 1.67f;
    [Tooltip("How quickly the camera position catches up to the vehicle.")]
    public float vehiclePivotFollowSpeed = 1.6f;
    [Tooltip("How quickly the camera heading catches up while the vehicle is turning.")]
    public float vehicleTurnFollowSpeed = 1.17f;
    [Tooltip("How quickly the camera heading catches up while driving straight.")]
    public float vehicleStraightFollowSpeed = 2.33f;
    [Tooltip("Vehicle turn rate in degrees per second that reaches the maximum camera lag.")]
    public float vehicleTurnRateForMaxLag = 90f;

    [Header("Look")]
    public float mouseSensitivity = 0.11f;
    public float gamepadSensitivity = 115f;
    public bool invertY;
    public float rotationDamping = 18f;
    public float followDamping = 8f;
    public bool autoRecenter;
    public float autoRecenterSpeed = 3f;
    public bool diagnosticLogging;

    [Header("Collision")]
    public LayerMask cameraCollisionLayers = ~0;
    public float cameraCollisionRadius = 0.22f;
    public float cameraCollisionPadding = 0.12f;

    bool onFoot;
    bool hoodView;
    bool cursorOwned;
    bool hadFocus = true;
    float shakeTime;
    float shakeStrength;
    float currentYaw;
    float currentPitch;
    float desiredYaw;
    float desiredPitch;
    float zoom = 1f;
    float zoomTarget = 1f;
    Vector3 smoothedPivot;
    Vector3 smoothedCameraPivot;
    bool pivotInitialized;
    bool vehicleTarget;
    Vector3 vehicleTrackingOffset;
    Vector3 smoothedVehicleForward;
    Vector3 previousVehicleForward;
    bool vehicleHeadingInitialized;
    Camera cameraComponent;
    float baseFov = 67f;
    bool reportedPose;
    float currentCollisionDistance = -1f;
    readonly RaycastHit[] cameraCollisionHits = new RaycastHit[32];

    public bool HoodView { get { return hoodView; } }
    public float CameraYaw { get { return desiredYaw; } }
    public float CameraPitch { get { return desiredPitch; } }

    public void ApplyOriginalVehicleFraming()
    {
        distance = 10.5f;
        targetHeight = 1.05f;
        maxDistance = Mathf.Max(maxDistance, 160f);
        defaultYaw = 0f;
        defaultPitch = 65f;
        autoRecenter = false;
        zoom = 1f;
        zoomTarget = 1f;
        ResetOrbitIfNeeded();
    }

    public void SetOnFoot(bool value)
    {
        onFoot = value;
        ResetOrbitIfNeeded();
        UpdateCursorState();
    }

    public void SetTarget(Transform value)
    {
        target = value;
        vehicleTarget = target != null && (target.GetComponent<ReferenceVehicleRuntimeBinder>() != null
            || target.GetComponent<CarControl>() != null
            || target.GetComponent<PlayerCar>() != null);
        if (vehicleTarget)
        {
            // FollowCamera runs in LateUpdate while the RV1.0 is moved by
            // WheelColliders in FixedUpdate. Interpolation prevents the
            // third-person camera from sampling the same physics pose for
            // several render frames, which appears as camera judder.
            Rigidbody targetBody = target.GetComponent<Rigidbody>();
            if (targetBody != null)
                targetBody.interpolation = RigidbodyInterpolation.Interpolate;

            // RV1.0 is the original ~11.5m chassis; the old Voyage framing
            // was tuned for the small replacement car and clipped the RV roof.
            distance = 18f;
            targetHeight = 2.35f;
            minDistance = 8f;
            maxDistance = 200f;
            defaultPitch = 65f;
        }
        else if (onFoot)
        {
            distance = 6.5f;
            minDistance = 1.5f;
            maxDistance = 14f;
            zoom = zoomTarget = 1f;
        }
        currentCollisionDistance = -1f;
        pivotInitialized = false;
        smoothedCameraPivot = Vector3.zero;
        vehicleTrackingOffset = Vector3.zero;
        vehicleHeadingInitialized = false;
        smoothedVehicleForward = Vector3.forward;
        previousVehicleForward = Vector3.forward;
        ResetOrbitIfNeeded();
        if (target != null && !onFoot)
            if (diagnosticLogging) Debug.Log("CAMERA SYSTEM // original FollowCamera active on " + name + " target=" + target.name);
    }

    public void ToggleVehicleView()
    {
        if (onFoot) return;
        hoodView = !hoodView;
    }

    public void Shake(float strength)
    {
        shakeTime = Mathf.Max(shakeTime, 0.16f);
        shakeStrength = Mathf.Max(shakeStrength, strength);
    }

    void Awake()
    {
        cameraComponent = GetComponent<Camera>();
        if (cameraComponent != null)
        {
            cameraComponent.allowDynamicResolution = false;
            var additional = cameraComponent.GetUniversalAdditionalCameraData();
            additional.renderPostProcessing = true;
            additional.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            additional.antialiasingQuality = AntialiasingQuality.High;
        }
        ResetOrbitIfNeeded();
    }

    void OnEnable()
    {
        hadFocus = Application.isFocused;
        UpdateCursorState();
    }

    void OnDisable()
    {
        ReleaseCursor();
    }

    void OnApplicationFocus(bool focus)
    {
        hadFocus = focus;
        if (!focus) ReleaseCursor();
        else UpdateCursorState();
    }

    void ResetOrbitIfNeeded()
    {
        if (target == null) return;
        desiredYaw = defaultYaw;
        desiredPitch = onFoot ? onFootPitch : defaultPitch;
        currentYaw = desiredYaw;
        currentPitch = desiredPitch;
    }

    bool LookActive()
    {
        return hadFocus && Time.timeScale > 0.001f && target != null;
    }

    void UpdateCursorState()
    {
        bool shouldOwn = LookActive() && !VoyageCommandConsole.IsOpen;
        if (shouldOwn)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            cursorOwned = true;
        }
        else if (cursorOwned)
        {
            ReleaseCursor();
        }
    }

    void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        cursorOwned = false;
    }

    Vector3 SmoothVehicleHeading(Vector3 vehicleForward, float deltaTime)
    {
        if (!vehicleHeadingInitialized)
        {
            smoothedVehicleForward = vehicleForward;
            previousVehicleForward = vehicleForward;
            vehicleHeadingInitialized = true;
        }

        deltaTime = Mathf.Max(0.001f, deltaTime);
        float turnRate = Vector3.Angle(previousVehicleForward, vehicleForward) / deltaTime;
        previousVehicleForward = vehicleForward;
        float turnAmount = Mathf.InverseLerp(5f, Mathf.Max(5f, vehicleTurnRateForMaxLag), turnRate);
        float headingFollowSpeed = Mathf.Lerp(
            Mathf.Max(0.01f, vehicleStraightFollowSpeed),
            Mathf.Max(0.01f, vehicleTurnFollowSpeed),
            turnAmount);
        float headingBlend = 1f - Mathf.Exp(-headingFollowSpeed * deltaTime);
        smoothedVehicleForward = Vector3.Slerp(smoothedVehicleForward, vehicleForward, headingBlend).normalized;
        return smoothedVehicleForward;
    }

    void LateUpdate()
    {
        if (target == null)
        {
            ReleaseCursor();
            return;
        }

        if (cameraComponent == null) cameraComponent = GetComponent<Camera>();
        UpdateCursorState();
        if (!VoyageCommandConsole.IsOpen) ReadLookInput();


        Vector3 rawPivot;
        Vector3 rawCameraPivot;
        Vector3 cameraReferenceForward = Vector3.forward;
        if (vehicleTarget && !onFoot)
        {
            Vector3 vehicleForward = target.forward;
            CarControl car = target.GetComponent<CarControl>();
            if (car != null && car.DriveForward.sqrMagnitude > 0.001f)
                vehicleForward = car.DriveForward;
            else
            {
                VehicleTerrainFollower follower = target.GetComponent<VehicleTerrainFollower>();
                if (follower != null && follower.ForwardDirection.sqrMagnitude > 0.001f)
                    vehicleForward = follower.ForwardDirection;
                else if (target.GetComponent<PlayerCar>() != null)
                    vehicleForward = target.right;
            }
            vehicleForward = Vector3.ProjectOnPlane(vehicleForward, Vector3.up);
            if (vehicleForward.sqrMagnitude < 0.001f) vehicleForward = Vector3.forward;
            vehicleForward.Normalize();
            cameraReferenceForward = SmoothVehicleHeading(vehicleForward, Time.unscaledDeltaTime);

            Rigidbody body = target.GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up) : Vector3.zero;
            Vector3 desiredTrackingOffset = velocity * Mathf.Max(0f, vehicleTrackingLead);
            float trackingBlend = 1f - Mathf.Exp(-Mathf.Max(0.01f, vehicleTrackingSmooth) * Time.unscaledDeltaTime);
            vehicleTrackingOffset = Vector3.Lerp(vehicleTrackingOffset, desiredTrackingOffset, trackingBlend);

            // RV1.0's authored forward axis is exposed by CarControl.DriveForward.
            // Looking ahead of the root keeps the camera on the bonnet/road
            // direction instead of framing the rear body and spare tyre.
            rawCameraPivot = target.position
                + vehicleTrackingOffset
                + Vector3.up * targetHeight;
            rawPivot = target.position
                + cameraReferenceForward * vehicleFocusForward
                + vehicleTrackingOffset
                + Vector3.up * vehicleFocusHeight;
        }
        else
        {
            rawPivot = target.position + Vector3.up * (onFoot ? 1.1f : (hoodView ? 0.85f : targetHeight));
            rawCameraPivot = rawPivot;
        }
        if (!pivotInitialized)
        {
            smoothedPivot = rawPivot;
            smoothedCameraPivot = rawCameraPivot;
            pivotInitialized = true;
        }
        // Filter suspension and steering impulses before using the vehicle
        // position for both the camera orbit and its look direction. A simple
        // exponential filter avoids SmoothDamp overshoot when a WheelCollider
        // corrects its contact at a streamed tile seam.
        float pivotFollowSpeed = onFoot ? 24f : (vehicleTarget ? Mathf.Max(0.01f, vehiclePivotFollowSpeed) : 6f);
        float pivotBlend = 1f - Mathf.Exp(-pivotFollowSpeed * Time.unscaledDeltaTime);
        Vector3 pivot = Vector3.Lerp(smoothedPivot, rawPivot, pivotBlend);
        Vector3 cameraPivot = Vector3.Lerp(smoothedCameraPivot, rawCameraPivot, pivotBlend);
        smoothedPivot = pivot;
        smoothedCameraPivot = cameraPivot;
        float actualDistance = (onFoot ? distance : (hoodView ? 1.8f : distance)) * zoom;
        Quaternion orbit = Quaternion.Euler(currentPitch, currentYaw, 0f);
        if (vehicleTarget && !onFoot)
        {
            // Keep the zero-yaw chase position behind the vehicle's current
            // heading instead of behind a fixed world axis. Mouse yaw remains
            // a relative orbit around that heading.
            Quaternion vehicleHeading = Quaternion.LookRotation(cameraReferenceForward, Vector3.up);
            orbit = vehicleHeading * orbit;
        }
        Vector3 desiredPosition = cameraPivot + orbit * Vector3.back * actualDistance;
        // Vehicle and walking cameras both remain outside colliders. The
        // collision distance is smoothed in ResolveCollision so streamed
        // terrain seams cannot create an instant zoom, while the vehicle
        // camera still respects ground and solid world geometry.
        desiredPosition = ResolveCollision(cameraPivot, desiredPosition);

        if (!reportedPose)
        {
            if (diagnosticLogging) Debug.Log("CAMERA SYSTEM // pose=" + desiredPosition + " pivot=" + pivot + " distance=" + Vector3.Distance(desiredPosition, pivot));
            reportedPose = true;
        }

        float followBlend = 1f - Mathf.Exp(-followDamping * Time.unscaledDeltaTime);
        if (vehicleTarget)
        {
            // The vehicle anchor is already delayed and filtered above. Apply
            // the collision-safe result directly so a camera that was pushed
            // into the ground cannot remain there while a second Lerp catches
            // up over several frames.
            transform.position = desiredPosition;
        }
        else
        {
            transform.position = Vector3.Lerp(transform.position, desiredPosition, followBlend);
        }

        Quaternion lookRotation = Quaternion.LookRotation(pivot - transform.position, Vector3.up);
        float rotationBlend = 1f - Mathf.Exp(-rotationDamping * Time.unscaledDeltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, lookRotation, rotationBlend);

        if (shakeTime > 0f)
        {
            float fade = Mathf.Clamp01(shakeTime / 0.16f);
            transform.position += new Vector3(Random.Range(-1f, 1f), Random.Range(-0.7f, 0.7f), Random.Range(-1f, 1f)) * shakeStrength * fade;
            shakeTime -= Time.unscaledDeltaTime;
            shakeStrength = Mathf.MoveTowards(shakeStrength, 0f, Time.unscaledDeltaTime * 1.8f);
        }

        // Follow smoothing and camera shake happen after the first orbit cast.
        // Re-check the final pose so neither can leave the camera inside terrain.
        Vector3 beforeCollisionCorrection = transform.position;
        transform.position = ResolveCollision(cameraPivot, transform.position);
        if ((transform.position - beforeCollisionCorrection).sqrMagnitude > 0.000001f)
            transform.rotation = Quaternion.LookRotation(pivot - transform.position, Vector3.up);

        if (cameraComponent != null)
        {
            // Keep vehicle framing fixed. Speed-dependent FOV makes the
            // camera appear to zoom whenever steering changes forward speed.
            float targetFov = baseFov;
            cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, targetFov, 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime));
        }
    }

    void ReadLookInput()
    {
        if (!LookActive() || VoyageCommandConsole.IsOpen || VoyageCommandConsole.ConsumedInputThisFrame) return;

        Vector2 look = Vector2.zero;
        if (Mouse.current != null)
            look += Mouse.current.delta.ReadValue() * mouseSensitivity;

        if (Gamepad.current != null)
            look += Gamepad.current.rightStick.ReadValue() * gamepadSensitivity * Time.unscaledDeltaTime;

        if (look.sqrMagnitude > 0.000001f)
        {
            desiredYaw = Mathf.Repeat(desiredYaw + look.x, 360f);
            float pitchDelta = invertY ? look.y : -look.y;
            desiredPitch += pitchDelta;
        }

        if (autoRecenter && look.sqrMagnitude < 0.000001f && !onFoot)
            desiredYaw = Mathf.MoveTowardsAngle(desiredYaw, defaultYaw, autoRecenterSpeed * Time.unscaledDeltaTime * 30f);

        currentYaw = Mathf.LerpAngle(currentYaw, desiredYaw, 1f - Mathf.Exp(-rotationDamping * Time.unscaledDeltaTime));
        currentPitch = Mathf.Lerp(currentPitch, desiredPitch, 1f - Mathf.Exp(-rotationDamping * Time.unscaledDeltaTime));

        {
            float scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
            float legacyScroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(legacyScroll) > 0.01f) scroll = legacyScroll;
            // Keep the legacy axis as a fallback for projects using the Both
            // input backend when the Game view does not publish a Mouse device.
            float legacyScrollAxis = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(legacyScrollAxis) > 0.0001f) scroll = legacyScrollAxis * 120f;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                float minZoom = minDistance / Mathf.Max(0.01f, distance);
                float maxZoom = Mathf.Max(minZoom, maxDistance / Mathf.Max(0.01f, distance));
                zoomTarget = Mathf.Clamp(zoomTarget - scroll * wheelZoomStep, minZoom, maxZoom);
                if (diagnosticLogging) Debug.Log("CAMERA ZOOM // scroll=" + scroll.ToString("0.0") + " targetDistance=" + (distance * zoomTarget).ToString("0.00"));
            }
        }

        if (Gamepad.current != null && Gamepad.current.rightStickButton.wasPressedThisFrame)
            zoomTarget = zoomTarget > 1f ? 0.78f : 1.2f;

        zoom = Mathf.Lerp(zoom, zoomTarget, 1f - Mathf.Exp(-zoomSmooth * Time.unscaledDeltaTime));
    }

    Vector3 ResolveCollision(Vector3 pivot, Vector3 desired)
    {
        Vector3 ray = desired - pivot;
        float length = ray.magnitude;
        if (length < 0.01f) return desired;

        float radius = cameraCollisionRadius;
        if (cameraComponent != null)
        {
            float near = cameraComponent.nearClipPlane;
            float halfHeight = near * Mathf.Tan(cameraComponent.fieldOfView * Mathf.Deg2Rad * .5f);
            radius = Mathf.Max(radius, Mathf.Sqrt(near * near + halfHeight * halfHeight * (1f + cameraComponent.aspect * cameraComponent.aspect)));
        }
        int hitCount = Physics.SphereCastNonAlloc(
            pivot,
            radius,
            ray / length,
            cameraCollisionHits,
            length,
            cameraCollisionLayers,
            QueryTriggerInteraction.Ignore);
        float nearest = length;
        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = cameraCollisionHits[i].collider;
            Transform hitTransform = hitCollider != null ? hitCollider.transform : null;
            if (hitTransform == target || (hitTransform != null && hitTransform.IsChildOf(target))) continue;
            nearest = Mathf.Min(nearest, cameraCollisionHits[i].distance);
        }

        // NonAlloc hits are unordered; a full buffer may omit the closest wall.
        if (hitCount == cameraCollisionHits.Length)
        {
            foreach (var hit in Physics.SphereCastAll(pivot, radius, ray / length, length, cameraCollisionLayers, QueryTriggerInteraction.Ignore))
                if (hit.transform != target && !hit.transform.IsChildOf(target))
                    nearest = Mathf.Min(nearest, hit.distance);
        }
        float targetDistance = nearest < length ? Mathf.Max(0.05f, nearest - cameraCollisionPadding) : length;
        if (currentCollisionDistance < 0f || targetDistance < currentCollisionDistance)
        {
            // Move inward immediately. Interpolating this side of the
            // correction lets a fast mouse pitch carry the camera through the
            // ground for several frames before it catches up.
            currentCollisionDistance = targetDistance;
        }
        else
        {
            // Recover outward smoothly after the view is clear.
            const float response = 8f;
            currentCollisionDistance = Mathf.Lerp(currentCollisionDistance, targetDistance,
                1f - Mathf.Exp(-response * Time.unscaledDeltaTime));
        }
        float safeDistance = currentCollisionDistance;
        return pivot + ray.normalized * safeDistance;
    }

}
