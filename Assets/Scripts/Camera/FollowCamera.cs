using UnityEngine;
using UnityEngine.InputSystem;

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
    public float maxDistance = 32f;
    public float wheelZoomStep = 0.0015f;
    public float zoomSmooth = 12f;
    public float defaultYaw = 0f;
    public float defaultPitch = 34f;
    public float minPitch = -20f;
    public float maxPitch = 60f;

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
    Camera cameraComponent;
    PlayerCar targetCar;
    float baseFov = 67f;
    bool reportedPose;
    readonly RaycastHit[] cameraCollisionHits = new RaycastHit[32];

    public bool HoodView { get { return hoodView; } }
    public float CameraYaw { get { return desiredYaw; } }
    public float CameraPitch { get { return desiredPitch; } }

    public void ApplyOriginalVehicleFraming()
    {
        distance = 10.5f;
        targetHeight = 1.05f;
        maxDistance = Mathf.Max(maxDistance, 32f);
        defaultYaw = 0f;
        defaultPitch = 34f;
        minPitch = -20f;
        maxPitch = 60f;
        autoRecenter = false;
        zoom = 1f;
        zoomTarget = 1f;
        ResetOrbitIfNeeded();
    }

    public void SetOnFoot(bool value)
    {
        onFoot = value;
        if (onFoot) ReleaseCursor();
        else ResetOrbitIfNeeded();
    }

    public void SetTarget(Transform value)
    {
        target = value;
        targetCar = target != null ? target.GetComponent<PlayerCar>() : null;
        if (target != null && target.GetComponent<ReferenceVehicleRuntimeBinder>() != null)
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
            maxDistance = 40f;
            defaultPitch = 24f;
        }
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
        desiredPitch = Mathf.Clamp(defaultPitch, minPitch, maxPitch);
        currentYaw = desiredYaw;
        currentPitch = desiredPitch;
    }

    bool DrivingActive()
    {
        return !onFoot && hadFocus && Time.timeScale > 0.001f && target != null;
    }

    void UpdateCursorState()
    {
        bool shouldOwn = DrivingActive();
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

    void LateUpdate()
    {
        if (target == null)
        {
            ReleaseCursor();
            return;
        }

        if (cameraComponent == null) cameraComponent = GetComponent<Camera>();
        UpdateCursorState();
        ReadLookInput();

        float vehicleSpeedMix = 0f;
        if (targetCar == null) targetCar = target.GetComponent<PlayerCar>();
        if (targetCar != null) vehicleSpeedMix = Mathf.Clamp01(targetCar.speedKmh / 100f);

        Vector3 pivot = target.position + Vector3.up * (onFoot ? 1.1f : (hoodView ? 0.85f : targetHeight));
        float actualDistance = (onFoot ? 6.5f : (hoodView ? 1.8f : distance)) * zoom;
        Quaternion orbit = Quaternion.Euler(currentPitch, currentYaw, 0f);
        Vector3 desiredPosition = pivot + orbit * Vector3.back * actualDistance;
        desiredPosition = ResolveCollision(pivot, desiredPosition);

        if (!reportedPose)
        {
            if (diagnosticLogging) Debug.Log("CAMERA SYSTEM // pose=" + desiredPosition + " pivot=" + pivot + " distance=" + Vector3.Distance(desiredPosition, pivot));
            reportedPose = true;
        }

        float followBlend = 1f - Mathf.Exp(-followDamping * Time.unscaledDeltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, followBlend);

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

        if (cameraComponent != null)
        {
            float targetFov = onFoot ? baseFov : baseFov + vehicleSpeedMix * 5f;
            cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, targetFov, 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime));
        }
    }

    void ReadLookInput()
    {
        Vector2 look = Vector2.zero;
        bool freeMouseLook = onFoot && Mouse.current != null && Mouse.current.rightButton.isPressed;
        if ((DrivingActive() || freeMouseLook) && Mouse.current != null)
            look += Mouse.current.delta.ReadValue() * mouseSensitivity;

        if (Gamepad.current != null)
            look += Gamepad.current.rightStick.ReadValue() * gamepadSensitivity * Time.unscaledDeltaTime;

        if (look.sqrMagnitude > 0.000001f)
        {
            desiredYaw = Mathf.Repeat(desiredYaw + look.x, 360f);
            desiredPitch = Mathf.Clamp(desiredPitch + (invertY ? look.y : -look.y), minPitch, maxPitch);
        }

        if (autoRecenter && look.sqrMagnitude < 0.000001f && !onFoot)
            desiredYaw = Mathf.MoveTowardsAngle(desiredYaw, defaultYaw, autoRecenterSpeed * Time.unscaledDeltaTime * 30f);

        currentYaw = Mathf.LerpAngle(currentYaw, desiredYaw, 1f - Mathf.Exp(-rotationDamping * Time.unscaledDeltaTime));
        currentPitch = Mathf.Lerp(currentPitch, desiredPitch, 1f - Mathf.Exp(-rotationDamping * Time.unscaledDeltaTime));

        if (!onFoot)
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

    float currentCollisionDistance = -1f;

    Vector3 ResolveCollision(Vector3 pivot, Vector3 desired)
    {
        Vector3 ray = desired - pivot;
        float length = ray.magnitude;
        if (length < 0.01f) return desired;

        int hitCount = Physics.SphereCastNonAlloc(
            pivot,
            cameraCollisionRadius,
            ray / length,
            cameraCollisionHits,
            length,
            cameraCollisionLayers,
            QueryTriggerInteraction.Ignore);
        float nearest = length;
        for (int i = 0; i < hitCount; i++)
        {
            Transform hitTransform = cameraCollisionHits[i].collider != null ? cameraCollisionHits[i].collider.transform : null;
            if (hitTransform == target || (hitTransform != null && hitTransform.IsChildOf(target))) continue;

            // A ground triangle below the vehicle is not a camera obstruction. Treating
            // it as one collapses the original rear camera into the vehicle or terrain.
            if (cameraCollisionHits[i].normal.y > 0.55f && cameraCollisionHits[i].point.y <= pivot.y + 0.2f) continue;
            nearest = Mathf.Min(nearest, cameraCollisionHits[i].distance);
        }

        float targetCollisionDistance = nearest < length ? Mathf.Max(1.5f, nearest - cameraCollisionPadding) : length;
        if (currentCollisionDistance < 0f) currentCollisionDistance = targetCollisionDistance;
        else
        {
            float speed = targetCollisionDistance < currentCollisionDistance ? 24f : 8f;
            currentCollisionDistance = Mathf.Lerp(currentCollisionDistance, targetCollisionDistance, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
        }

        return pivot + ray.normalized * currentCollisionDistance;
    }
}
