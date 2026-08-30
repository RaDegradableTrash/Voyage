using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(0)]
public class PlayerCar : MonoBehaviour
{
    // Runtime wheel-axis audit marker: keep this file importable after each
    // geometry change so the open Unity editor reloads the latest assembly.
    [Header("Vehicle Model")]
    public GameObject vehicleModel;
    [Tooltip("Vehicle.fbx uses its negative local Z side as the front axle.")]
    public bool modelFrontIsPositiveZ = false;
    [Tooltip("Used when the FBX wheelbase is along X. The current Vehicle.fbx uses negative X as its front axle.")]
    public bool modelFrontIsPositiveX = true;
    [Tooltip("The current modeled vehicle faces negative local Z. Rotate the visual model so vehicle +Z is its front.")]
    public bool modelFrontFacesNegativeZ = true;
    Rigidbody rb;
    Light leftHeadlight;
    Light rightHeadlight;
    AudioSource engineAudio;
    AudioSource hornAudio;
    AudioSource mudAudio;
    AudioSource waterAudio;
    AudioSource skidAudio;
    VehicleTerrainFollower terrainFollower;
    float steer;
    bool controlEnabled = true;
    bool fuelStarved;
    float damage;
    float damageSteerBias;
    float throttleLoad;
    bool lowRange;
    bool differentialLock;
    bool controlStateLogged;
    bool tirePunctured;
    readonly List<Transform> wheels = new List<Transform>();
    readonly List<bool> wheelIsFront = new List<bool>();
    readonly List<Quaternion> wheelRestRotations = new List<Quaternion>();
    readonly List<Vector3> wheelSpinAxes = new List<Vector3>();
    readonly List<Transform> terrainEffects = new List<Transform>();
    readonly List<Renderer> tailLightRenderers = new List<Renderer>();
    readonly List<Material> tailLightMaterials = new List<Material>();
    readonly List<Material> terrainEffectMaterials = new List<Material>();
    float modelAxleMidZ;
    bool hasModelAxleLayout;
    bool modelForwardAlongX;
    bool modelFrontIsPositiveAxis;
    bool steeringMappingLogged;
    bool driveMotionLogged;
    Material bodyMaterial;
    PhysicsMaterial vehicleBodyPhysicsMaterial;
    Transform visualBody;
    Transform visualCabin;
    Transform spareTireVisual;
    Color pristineBodyColor = new Color(0.03f, 0.34f, 0.8f);
    float mudVisualLevel;
    float wheelSpinDegrees;
    float wheelSpinRateDegrees;
    float wheelSpinRateVelocity;
    int engineUpgradeLevel;
    int suspensionUpgradeLevel;
    int fuelTankUpgradeLevel;
    bool isInMud;
    bool isInWater;
    bool usingVehicleModel;
    bool visualsBuilt;
    bool wheelRuntimeGeometryLogged;
    bool driveCommandLogged;
    [Header("Diagnostics")]
    public bool diagnosticLogging = false;
    string surfaceType = "GROUND";
    float surfaceGripValue = 1f;
    float externalSpeedMultiplier = 1f;
    float surfaceProbeClock;
    bool grounded;
    Vector3 groundNormal = Vector3.up;
    bool cachedAirborne;
    float nextAirborneProbe;
    readonly RaycastHit[] airborneHits = new RaycastHit[16];
    readonly Vector3[] surfaceSampleOffsets =
    {
        new Vector3(0f, 0f, 0f),
        new Vector3(-0.78f, 0f, 1.35f),
        new Vector3(0.78f, 0f, 1.35f),
        new Vector3(-0.78f, 0f, -1.35f),
        new Vector3(0.78f, 0f, -1.35f)
    };
    public float Damage { get { return damage; } }
    public float SteeringBias { get { return damageSteerBias; } }
    public float ThrottleLoad { get { return throttleLoad; } }
    public float FuelUseMultiplier { get { return (lowRange ? 1.3f : 1f) * (isInMud ? 1.18f : (isInWater ? 1.1f : 1f)) * (tirePunctured ? 1.2f : 1f); } }
    public int EngineUpgradeLevel { get { return engineUpgradeLevel; } }
    public int SuspensionUpgradeLevel { get { return suspensionUpgradeLevel; } }
    public int FuelTankUpgradeLevel { get { return fuelTankUpgradeLevel; } }
    public float FuelCapacity { get { return 100f + fuelTankUpgradeLevel * 15f; } }
    public bool LowRangeEnabled { get { return lowRange; } }
    public bool DifferentialLockEnabled { get { return differentialLock; } }
    public bool TirePunctured { get { return tirePunctured; } }
    public bool IsUpsideDown { get { return Vector3.Dot(transform.up, Vector3.up) < -0.25f; } }
    public bool IsInMud { get { return isInMud; } }
    public bool IsInWater { get { return isInWater; } }
    public string SurfaceType { get { return surfaceType; } }
    public float SurfaceGrip { get { return surfaceGripValue; } }
    public string DriveMode { get { return (lowRange ? "LOW" : "HIGH") + (differentialLock ? " / LOCK" : " / OPEN"); } }
    public float speedKmh { get { return terrainFollower != null ? terrainFollower.CurrentVelocity.magnitude * 3.6f : (rb == null ? 0 : rb.linearVelocity.magnitude * 3.6f); } }
    public float LateralSpeedKmh { get { Vector3 velocity = terrainFollower != null ? terrainFollower.CurrentVelocity : (rb == null ? Vector3.zero : rb.linearVelocity); return Mathf.Abs(transform.InverseTransformDirection(velocity).z) * 3.6f; } }
    public float SteeringAmount { get { return Mathf.Abs(steer); } }
    public void EnsureVehiclePhysics()
    {
        BoxCollider body = GetComponent<BoxCollider>();
        if (body == null) body = gameObject.AddComponent<BoxCollider>();
        // VehicleManual and the wheel solver use local X as forward.
        body.size = new Vector3(4.3f, 0.8f, 2.1f);
        body.center = new Vector3(0f, 0.15f, 0f);
        // Keep the chassis as a real collider. The wheel solver provides the
        // suspension forces, but a trigger chassis has no collision response;
        // when the raycast suspension briefly misses, the whole car sinks
        // through the terrain and cannot recover.
        body.isTrigger = false;

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 1000f;
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.detectCollisions = true;
        rb.linearDamping = 0.15f;
        rb.angularDamping = 1.2f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.solverIterations = 12;
        rb.solverVelocityIterations = 8;
        rb.sleepThreshold = 0.005f;
        rb.constraints = RigidbodyConstraints.None;
    }
    public void BindTerrain(Terrain terrain)
    {
        if (terrainFollower == null) terrainFollower = GetComponent<VehicleTerrainFollower>();
        if (terrainFollower == null) terrainFollower = gameObject.AddComponent<VehicleTerrainFollower>();
        terrainFollower.BindTerrain(terrain);
    }
    public bool IsAirborne
    {
        get
        {
            if (rb == null) return false;
            if (Time.time < nextAirborneProbe) return cachedAirborne;
            nextAirborneProbe = Time.time + 0.08f;
            Vector3 origin = transform.position + Vector3.up * 1.1f;
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, airborneHits, 4.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            cachedAirborne = true;
            for (int i = 0; i < hitCount; i++)
            {
                Transform hitTransform = airborneHits[i].collider != null ? airborneHits[i].collider.transform : null;
                if (hitTransform != null && hitTransform != transform && !hitTransform.IsChildOf(transform)) { cachedAirborne = false; break; }
            }
            return cachedAirborne;
        }
    }
    public void BuildVisuals(Material body, Material glass, Material tail, Material head)
    {
        if (visualsBuilt) return;
        EnsureVehiclePhysics();
        terrainFollower = GetComponent<VehicleTerrainFollower>();
        if (terrainFollower == null) terrainFollower = gameObject.AddComponent<VehicleTerrainFollower>();
        terrainFollower.enabled = true;
        ResolveVehicleModelReference();
        wheels.Clear();
        wheelIsFront.Clear();
        hasModelAxleLayout = false;
        steeringMappingLogged = false;
        wheelRestRotations.Clear();
        wheelSpinAxes.Clear();
        terrainEffects.Clear();
        tailLightRenderers.Clear();
        tailLightMaterials.Clear();
        terrainEffectMaterials.Clear();
        if (vehicleModel != null)
        {
            try
            {
                usingVehicleModel = true;
                if (diagnosticLogging) Debug.Log("PLAYER CAR MODEL // using " + vehicleModel.name + " on " + name);
                BuildVehicleModelVisuals();
                InitializeEngineAudio();
                visualsBuilt = true;
                return;
            }
            catch (System.InvalidCastException)
            {
                // A model sub-asset can survive prefab reimports as a native
                // Prefab reference even though this field requires GameObject.
                // Fall back to the procedural vehicle instead of aborting boot.
                Debug.LogWarning("PlayerCar: vehicleModel reference is invalid; using the procedural vehicle model.");
                vehicleModel = null;
                usingVehicleModel = false;
            }
        }
        // The runtime vehicle convention is local X forward, local Z across
        // the car. Keep the procedural fallback on the same convention as
        // VehicleManual so driving still works when the art prefab is absent.
        visualBody = CreatePart(PrimitiveType.Cube, "Body", new Vector3(0, 0.15f, 0), new Vector3(4.2f, 0.72f, 2f), body).transform;
        var bodyRenderer = visualBody.GetComponent<Renderer>();
        if (bodyRenderer != null)
        {
            bodyMaterial = bodyRenderer.material;
            pristineBodyColor = bodyMaterial.color;
        }
        visualCabin = CreatePart(PrimitiveType.Cube, "Cabin", new Vector3(-0.15f, 0.66f, 0), new Vector3(1.75f, 0.6f, 1.55f), glass).transform;
        for (int side = -1; side <= 1; side += 2) for (int z = -1; z <= 1; z += 2)
        { var wheel = CreatePart(PrimitiveType.Cylinder, "Wheel", new Vector3(z * 1.35f, -0.2f, side * 1.02f), new Vector3(0.38f, 0.18f, 0.38f), Color.black); wheel.transform.localRotation = Quaternion.Euler(0, 0, 90); wheels.Add(wheel.transform); wheelRestRotations.Add(wheel.transform.localRotation); wheelSpinAxes.Add(Vector3.up); }
        for (int side = -1; side <= 1; side += 2) for (int z = -1; z <= 1; z += 2)
        {
            var effect = CreatePart(PrimitiveType.Sphere, "Terrain Feedback", new Vector3(z * 1.35f, -0.24f, side * 1.02f), new Vector3(0.12f, 0.06f, 0.12f), new Color(0.38f, 0.2f, 0.07f));
            effect.gameObject.SetActive(false);
            terrainEffects.Add(effect.transform);
            var effectRenderer = effect.GetComponent<Renderer>();
            terrainEffectMaterials.Add(effectRenderer != null ? effectRenderer.material : null);
        }
        spareTireVisual = CreatePart(PrimitiveType.Cylinder, "Spare Tire", new Vector3(-2.18f, 0.35f, 0f), new Vector3(0.48f, 0.2f, 0.48f), Color.black).transform;
        spareTireVisual.localRotation = Quaternion.Euler(0f, 0f, 90f);
        CreatePart(PrimitiveType.Cube, "Headlights", new Vector3(2.12f, 0.2f, 0), new Vector3(0.08f, 0.16f, 1.2f), head);
        var tailLights = CreatePart(PrimitiveType.Cube, "Tail Lights", new Vector3(-2.12f, 0.2f, 0), new Vector3(0.08f, 0.16f, 1.2f), tail);
        var tailRenderer = tailLights.GetComponent<Renderer>();
        if (tailRenderer != null)
        {
            tailLightRenderers.Add(tailRenderer);
            tailLightMaterials.Add(tailRenderer.material);
        }
        leftHeadlight = CreateHeadlight("Left Headlight", new Vector3(2.16f, 0.22f, -0.62f));
        rightHeadlight = CreateHeadlight("Right Headlight", new Vector3(2.16f, 0.22f, 0.62f));
        if (terrainFollower != null && wheels.Count >= 4)
            terrainFollower.ConfigureWheelTransforms(wheels.ToArray(), true, true);
        InitializeEngineAudio();
        visualsBuilt = true;
    }

    void ResolveVehicleModelReference()
    {
        // VehicleManual is the single runtime visual source. Do not trust the
        // serialized vehicleModel field: old prefabs may contain an FBX
        // sub-asset reference that is not a valid GameObject prefab reference.
        GameObject markedVehiclePrefab = Resources.Load<GameObject>("Prefabs/VehicleManual");
        if (markedVehiclePrefab != null)
        {
            vehicleModel = markedVehiclePrefab;
            if (diagnosticLogging) Debug.Log("PLAYER CAR MODEL // using marked prefab " + vehicleModel.name + " for " + name);
            return;
        }

        vehicleModel = null;
        Debug.LogWarning("PLAYER CAR MODEL // Resources/Prefabs/VehicleManual is missing; procedural fallback will be used.");
    }

    void BuildVehicleModelVisuals()
    {
        GameObject model = Instantiate(vehicleModel, transform);
        model.name = "Vehicle FBX // BODY AND WHEELS";
        // Preserve the prefab-authored root transform. The prefab's wheel
        // names and authored facing direction are the source of truth.
        visualBody = model.transform;
        DisableEmbeddedCameras(model);

        // VehicleManual is the authoritative wheel layout. Its component
        // contains the four transforms that were marked in the prefab, so
        // front/rear and left/right never depend on FBX naming or axis guesses.
        VehicleWheelMarkers markedLayout = model.GetComponent<VehicleWheelMarkers>();
        if (markedLayout != null && TryBuildMarkedWheelLayout(model, markedLayout))
        {
            BuildModelBodyColliders(model);
            return;
        }

        Transform[] cylinders = model.GetComponentsInChildren<Transform>(true);
        var wheelTransforms = new List<Transform>();
        for (int i = 0; i < cylinders.Length; i++)
        {
            if (cylinders[i] == model.transform || !cylinders[i].name.StartsWith("Cylinder", System.StringComparison.OrdinalIgnoreCase)) continue;
            wheelTransforms.Add(cylinders[i]);
        }
        if (wheelTransforms.Count >= 4)
        {
            float wheelZMid = 0f;
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            for (int i = 0; i < wheelTransforms.Count; i++)
            {
                Vector3 p = transform.InverseTransformPoint(wheelTransforms[i].position);
                wheelZMid += p.z;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
            }
            wheelZMid /= wheelTransforms.Count;
            // VehicleManual is authored with local X+ as the front. Do not
            // infer the longitudinal axis from the model's overall bounds:
            // body proportions and imported child rotations can make Z look
            // longer and silently swap front/rear behavior.
            modelForwardAlongX = true;
            modelFrontIsPositiveAxis = modelFrontIsPositiveX;
            float wheelAxisMid = modelForwardAlongX ? (minX + maxX) * 0.5f : wheelZMid;
            wheelTransforms.Sort((a, b) =>
            {
                Vector3 pa = transform.InverseTransformPoint(a.position);
                Vector3 pb = transform.InverseTransformPoint(b.position);
                float aAxis = modelForwardAlongX ? pa.x : pa.z;
                float bAxis = modelForwardAlongX ? pb.x : pb.z;
                int frontOrder = modelFrontIsPositiveAxis ? bAxis.CompareTo(aAxis) : aAxis.CompareTo(bAxis);
                if (frontOrder != 0) return frontOrder;
                float aLateral = modelForwardAlongX ? pa.z : pa.x;
                float bLateral = modelForwardAlongX ? pb.z : pb.x;
                return aLateral.CompareTo(bLateral);
            });
            for (int i = 0; i < 4; i++)
            {
                Transform sourceWheel = wheelTransforms[i];
                Vector3 localWheelPosition = transform.InverseTransformPoint(sourceWheel.position);
                float wheelAxis = modelForwardAlongX ? localWheelPosition.x : localWheelPosition.z;
                bool isFrontWheel = modelFrontIsPositiveAxis ? wheelAxis >= wheelAxisMid : wheelAxis <= wheelAxisMid;
                // Keep the prefab wheel transform exactly where it was
                // authored. Only its position along the suspension axis may
                // change later at runtime.
                Transform wheelPivot = CreateWheelPivot(sourceWheel, "Wheel Pivot // " + i);
                Vector3 spinAxis = GetSpinAxisInPivotSpace(wheelPivot);
                wheels.Add(wheelPivot);
                wheelIsFront.Add(isFrontWheel);
                wheelRestRotations.Add(wheelPivot.localRotation);
                wheelSpinAxes.Add(spinAxis);
                if (diagnosticLogging) Debug.Log("PLAYER CAR WHEEL // " + sourceWheel.name + " pivot=" + wheelPivot.position + " axis=" + spinAxis);
                if (diagnosticLogging) Debug.Log("PLAYER CAR AXLE // " + sourceWheel.name + " = " + (isFrontWheel ? "FRONT" : "REAR") + " side=" + (localWheelPosition.x < 0f ? "LEFT" : "RIGHT"));
            }
            modelAxleMidZ = 0f;
            for (int i = 0; i < wheels.Count; i++)
                modelAxleMidZ += modelForwardAlongX
                    ? transform.InverseTransformPoint(wheels[i].position).x
                    : transform.InverseTransformPoint(wheels[i].position).z;
            modelAxleMidZ /= wheels.Count;
            hasModelAxleLayout = true;
            if (diagnosticLogging) Debug.Log("PLAYER CAR LAYOUT // forwardAxis=" + (modelForwardAlongX ? "X" : "Z") + " front=" + (modelFrontIsPositiveAxis ? "+" : "-") + " axis");
            if (terrainFollower != null) terrainFollower.ConfigureWheelTransforms(wheels.ToArray(), modelFrontIsPositiveAxis, modelForwardAlongX);
        }
        else
        {
            Debug.LogError("PLAYER CAR MODEL // Vehicle.fbx contains " + wheelTransforms.Count + " Cylinder wheel objects; expected 4.");
        }

        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length > 0)
        {
            bodyMaterial = renderers[0].material;
            pristineBodyColor = bodyMaterial.color;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            BoxCollider rootBox = GetComponent<BoxCollider>();
            if (rootBox != null)
            {
                Bounds chassisBounds = new Bounds();
                bool hasChassisBounds = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (IsWheelPart(renderers[i].transform)) continue;
                    if (!hasChassisBounds) { chassisBounds = renderers[i].bounds; hasChassisBounds = true; }
                    else chassisBounds.Encapsulate(renderers[i].bounds);
                }
                if (hasChassisBounds)
                {
                    rootBox.enabled = true;
                    rootBox.center = transform.InverseTransformPoint(chassisBounds.center);
                    rootBox.size = chassisBounds.size + Vector3.one * 0.04f;
                }
            }
            KeepBodyColliderAboveTires();
            AddModelMeshColliders(model);
        }
    }

    bool TryBuildMarkedWheelLayout(GameObject model, VehicleWheelMarkers markedLayout)
    {
        Transform[] markedWheels = markedLayout.GetOrderedWheels();
        if (markedWheels == null || markedWheels.Length != 4) return false;

        for (int i = 0; i < markedWheels.Length; i++)
        {
            if (markedWheels[i] == null || markedWheels[i] == model.transform || !markedWheels[i].IsChildOf(model.transform))
            {
                Debug.LogWarning("PLAYER CAR MODEL // VehicleManual wheel marker " + i + " is missing or outside the model.");
                return false;
            }
            // A marker is useful only when it owns the rendered wheel. Some
            // imported Vehicle.fbx hierarchies contain empty WheelFront*/
            // WheelBack* locator objects while the actual tire meshes are
            // separate Cylinder objects. Treating an empty locator as a
            // wheel pivot guarantees a visibly wrong rotation center.
            MeshFilter markerMesh = markedWheels[i].GetComponentInChildren<MeshFilter>(true);
            Renderer markerRenderer = markedWheels[i].GetComponentInChildren<Renderer>(true);
            if (markerMesh == null || markerMesh.sharedMesh == null || markerRenderer == null)
            {
                Debug.LogWarning("PLAYER CAR MODEL // wheel marker " + markedWheels[i].name + " has no rendered mesh; using discovered wheel meshes.");
                return false;
            }
            for (int j = 0; j < i; j++)
                if (markedWheels[i] == markedWheels[j])
                {
                    Debug.LogWarning("PLAYER CAR MODEL // VehicleManual contains duplicate wheel markers.");
                    return false;
                }
        }

        // A generated/manual prefab with all four references at the marker
        // root is technically non-null but physically unusable. Reject it so
        // the named-cylinder fallback can recover instead of putting all
        // suspension rays at one point.
        Vector3 min = markedWheels[0].position;
        Vector3 max = min;
        for (int i = 1; i < markedWheels.Length; i++)
        {
            min = Vector3.Min(min, markedWheels[i].position);
            max = Vector3.Max(max, markedWheels[i].position);
        }
        if ((max - min).sqrMagnitude < 0.05f * 0.05f)
        {
            Debug.LogWarning("PLAYER CAR MODEL // VehicleManual wheel markers are collapsed; using model wheel discovery.");
            return false;
        }

        // The prefab names are authoritative. This keeps the runtime order
        // stable even if the marker fields were assigned in another order.
        Array.Sort(markedWheels, (a, b) =>
        {
            int frontCompare = (IsFrontWheelName(a.name) ? 0 : 1).CompareTo(IsFrontWheelName(b.name) ? 0 : 1);
            if (frontCompare != 0) return frontCompare;
            bool aLeft = a.name.EndsWith("L", StringComparison.OrdinalIgnoreCase);
            bool bLeft = b.name.EndsWith("L", StringComparison.OrdinalIgnoreCase);
            return bLeft.CompareTo(aLeft);
        });

        for (int i = 0; i < markedWheels.Length; i++)
        {
            Transform sourceWheel = markedWheels[i];
            Vector3 markerPosition = sourceWheel.position;
            bool isFrontWheel = IsFrontWheelName(sourceWheel.name);
            Transform wheelPivot = CreateWheelPivot(sourceWheel, "Wheel Pivot // " + i);
            Vector3 spinAxis = GetSpinAxisInPivotSpace(wheelPivot);
            wheels.Add(wheelPivot);
            wheelIsFront.Add(isFrontWheel);
            wheelRestRotations.Add(wheelPivot.localRotation);
            wheelSpinAxes.Add(spinAxis);
            if (diagnosticLogging) Debug.Log("PLAYER CAR MARKED WHEEL // " + sourceWheel.name + " = " + (isFrontWheel ? "FRONT" : "REAR") + " marker=" + markerPosition.ToString("F3") + " pivot=" + wheelPivot.position.ToString("F3") + " spinAxis=" + spinAxis.ToString("F3"));
        }

        hasModelAxleLayout = true;
        modelForwardAlongX = true;
        modelFrontIsPositiveAxis = true;
        modelAxleMidZ = 0f;
        if (terrainFollower != null)
            terrainFollower.ConfigureWheelTransforms(wheels.ToArray(), wheelIsFront.ToArray());
        if (diagnosticLogging) Debug.Log("PLAYER CAR LAYOUT // using explicit VehicleWheelMarkers order: FrontLeft, FrontRight, RearLeft, RearRight");
        return true;
    }

    void BuildModelBodyColliders(GameObject model)
    {
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        bodyMaterial = renderers[0].material;
        pristineBodyColor = bodyMaterial.color;
        BoxCollider rootBox = GetComponent<BoxCollider>();
        if (rootBox != null)
        {
            Bounds chassisBounds = new Bounds();
            bool hasChassisBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (IsWheelPart(renderers[i].transform)) continue;
                if (!hasChassisBounds) { chassisBounds = renderers[i].bounds; hasChassisBounds = true; }
                else chassisBounds.Encapsulate(renderers[i].bounds);
            }
            if (hasChassisBounds)
            {
                rootBox.enabled = true;
                rootBox.center = transform.InverseTransformPoint(chassisBounds.center);
                rootBox.size = chassisBounds.size + Vector3.one * 0.04f;
            }
        }
        KeepBodyColliderAboveTires();
        AddModelMeshColliders(model);
    }

    void KeepBodyColliderAboveTires()
    {
        BoxCollider body = GetComponent<BoxCollider>();
        if (body == null || terrainFollower == null || wheels.Count < 4) return;

        float highestTireBottom = float.MinValue;
        for (int i = 0; i < wheels.Count; i++)
        {
            if (wheels[i] == null) continue;
            float wheelCenterY = transform.InverseTransformPoint(wheels[i].position).y;
            highestTireBottom = Mathf.Max(highestTireBottom, wheelCenterY - Mathf.Max(0.05f, terrainFollower.tireRadius));
        }
        if (highestTireBottom == float.MinValue) return;

        // The chassis must not become a second support surface below the
        // tires. Raise only the bottom of the root body box; the suspension
        // then supports the car through the tires as intended.
        // Use the highest tire bottom, not the lowest one. On a slope this
        // keeps the solid chassis above every wheel contact and prevents it
        // from becoming an unintended second support point.
        float safeBottom = highestTireBottom + 0.22f;
        float currentBottom = body.center.y - body.size.y * 0.5f;
        if (currentBottom < safeBottom)
        {
            float top = body.center.y + body.size.y * 0.5f;
            body.center = new Vector3(body.center.x, (safeBottom + top) * 0.5f, body.center.z);
            body.size = new Vector3(body.size.x, Mathf.Max(0.1f, top - safeBottom), body.size.z);
        }
    }

    void DisableEmbeddedCameras(GameObject model)
    {
        Camera[] embeddedCameras = model.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < embeddedCameras.Length; i++)
        {
            Camera embeddedCamera = embeddedCameras[i];
            if (embeddedCamera == null) continue;
            embeddedCamera.enabled = false;
            if (embeddedCamera.CompareTag("MainCamera")) embeddedCamera.tag = "Untagged";
        }

        if (embeddedCameras.Length > 0)
            if (diagnosticLogging) Debug.Log("PLAYER CAR MODEL // disabled " + embeddedCameras.Length + " embedded FBX camera(s); using scene vehicle camera");
    }

    Vector3 GetSpinAxisInPivotSpace(Transform wheelPivot)
    {
        // Imported wheel meshes do not all use the same axle axis. First find
        // the thinnest mesh direction, then construct the axis from the two
        // opposite end-face centers on that direction. Using the line between
        // those actual face centers is more reliable than transforming a
        // guessed unit vector through an offset/rotated FBX hierarchy.
        Vector3 negativeFaceCenter;
        Vector3 positiveFaceCenter;
        if (TryGetWheelFaceCenters(wheelPivot, out negativeFaceCenter, out positiveFaceCenter))
        {
            Vector3 negativeInPivot = wheelPivot.InverseTransformPoint(negativeFaceCenter);
            Vector3 positiveInPivot = wheelPivot.InverseTransformPoint(positiveFaceCenter);
            // InverseTransformPoint already returns coordinates in the
            // wheel pivot's local/rest space. Do not apply localRotation a
            // second time: that rotated the axle away from the face-center
            // line and made the tire appear to orbit while rolling.
            Vector3 faceCenterLine = positiveInPivot - negativeInPivot;
            if (faceCenterLine.sqrMagnitude > 0.000001f)
            {
                // `rest * spin` expects the rolling axis in the authored
                // wheel-local space, which is exactly the space above.
                return faceCenterLine.normalized;
            }
        }
        return Vector3.forward;
    }

    bool TryGetWheelFaceCenters(Transform wheelRoot, out Vector3 negativeFaceCenter, out Vector3 positiveFaceCenter)
    {
        negativeFaceCenter = wheelRoot.position;
        positiveFaceCenter = wheelRoot.position + wheelRoot.forward;
        MeshFilter meshFilter = wheelRoot.GetComponentInChildren<MeshFilter>(true);
        Mesh mesh = meshFilter == null ? null : meshFilter.sharedMesh;
        if (mesh == null) return false;

        // Imported FBX meshes may still be non-readable in an already-open
        // Unity session. Mesh.bounds is available in that case, while
        // mesh.vertices throws and silently forced the old code to use the
        // marker origin. For a tire, the midpoint of the two bounds faces on
        // its thinnest axis is the axle center and is a stable fallback.
        if (!mesh.isReadable)
        {
            Bounds bounds = mesh.bounds;
            Vector3[] boundsAxes = { Vector3.right, Vector3.up, Vector3.forward };
            float shortestBoundsLength = float.MaxValue;
            Vector3 boundsNegative = Vector3.zero;
            Vector3 boundsPositive = Vector3.zero;
            for (int axisIndex = 0; axisIndex < boundsAxes.Length; axisIndex++)
            {
                Vector3 axis = boundsAxes[axisIndex];
                float halfExtent = Vector3.Dot(bounds.extents, axis);
                if (halfExtent <= 0.00001f) continue;
                Vector3 localCenter = bounds.center;
                Vector3 localNegative = localCenter - axis * halfExtent;
                Vector3 localPositive = localCenter + axis * halfExtent;
                Vector3 worldNegative = meshFilter.transform.TransformPoint(localNegative);
                Vector3 worldPositive = meshFilter.transform.TransformPoint(localPositive);
                float length = (worldPositive - worldNegative).sqrMagnitude;
                if (length < shortestBoundsLength)
                {
                    shortestBoundsLength = length;
                    boundsNegative = worldNegative;
                    boundsPositive = worldPositive;
                }
            }
            if (shortestBoundsLength < float.MaxValue)
            {
                negativeFaceCenter = boundsNegative;
                positiveFaceCenter = boundsPositive;
                return true;
            }
            return false;
        }

        Vector3[] readableVertices = mesh.vertices;
        if (readableVertices == null || readableVertices.Length == 0) return false;

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        float shortestLength = float.MaxValue;
        Vector3 bestNegative = Vector3.zero;
        Vector3 bestPositive = Vector3.zero;
        for (int axisIndex = 0; axisIndex < axes.Length; axisIndex++)
        {
            Vector3 axis = axes[axisIndex];
            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int i = 0; i < vertices.Length; i++)
            {
                float coordinate = Vector3.Dot(vertices[i], axis);
                minimum = Mathf.Min(minimum, coordinate);
                maximum = Mathf.Max(maximum, coordinate);
            }
            float tolerance = Mathf.Max(0.00001f, (maximum - minimum) * 0.001f);
            Vector3 minimumCenterSum = Vector3.zero;
            Vector3 maximumCenterSum = Vector3.zero;
            float minimumAreaSum = 0f;
            float maximumAreaSum = 0f;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]];
                Vector3 b = vertices[triangles[i + 1]];
                Vector3 c = vertices[triangles[i + 2]];
                float aCoordinate = Vector3.Dot(a, axis);
                float bCoordinate = Vector3.Dot(b, axis);
                float cCoordinate = Vector3.Dot(c, axis);
                float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                if (area <= 0.0000001f) continue;
                Vector3 faceCenter = (a + b + c) / 3f;
                if (Mathf.Abs(aCoordinate - minimum) <= tolerance &&
                    Mathf.Abs(bCoordinate - minimum) <= tolerance &&
                    Mathf.Abs(cCoordinate - minimum) <= tolerance)
                {
                    minimumCenterSum += faceCenter * area;
                    minimumAreaSum += area;
                }
                if (Mathf.Abs(aCoordinate - maximum) <= tolerance &&
                    Mathf.Abs(bCoordinate - maximum) <= tolerance &&
                    Mathf.Abs(cCoordinate - maximum) <= tolerance)
                {
                    maximumCenterSum += faceCenter * area;
                    maximumAreaSum += area;
                }
            }

            // A beveled mesh may have no triangle completely on the extreme
            // plane. Fall back to the extreme vertex groups only in that
            // case; the normal path above uses actual end-face geometry.
            if (minimumAreaSum <= 0.0000001f || maximumAreaSum <= 0.0000001f)
            {
                Vector3 minimumSum = Vector3.zero;
                Vector3 maximumSum = Vector3.zero;
                int minimumCount = 0;
                int maximumCount = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    float coordinate = Vector3.Dot(vertices[i], axis);
                    if (Mathf.Abs(coordinate - minimum) <= tolerance) { minimumSum += vertices[i]; minimumCount++; }
                    if (Mathf.Abs(coordinate - maximum) <= tolerance) { maximumSum += vertices[i]; maximumCount++; }
                }
                if (minimumCount == 0 || maximumCount == 0) continue;
                minimumCenterSum = minimumSum;
                maximumCenterSum = maximumSum;
                minimumAreaSum = minimumCount;
                maximumAreaSum = maximumCount;
            }

            Vector3 localMinimumCenter = minimumCenterSum / minimumAreaSum;
            Vector3 localMaximumCenter = maximumCenterSum / maximumAreaSum;
            Vector3 worldMinimumCenter = meshFilter.transform.TransformPoint(localMinimumCenter);
            Vector3 worldMaximumCenter = meshFilter.transform.TransformPoint(localMaximumCenter);
            float length = (worldMaximumCenter - worldMinimumCenter).sqrMagnitude;
            if (length < shortestLength)
            {
                shortestLength = length;
                bestNegative = worldMinimumCenter;
                bestPositive = worldMaximumCenter;
            }
        }
        if (shortestLength == float.MaxValue || shortestLength < 0.000001f) return false;
        negativeFaceCenter = bestNegative;
        positiveFaceCenter = bestPositive;
        return true;
    }

    Transform CreateWheelPivot(Transform sourceWheel, string pivotName)
    {
        Vector3 pivotPosition;
        Vector3 negativeFaceCenter;
        Vector3 positiveFaceCenter;
        bool hasFaceCenters = TryGetWheelFaceCenters(sourceWheel, out negativeFaceCenter, out positiveFaceCenter);
        if (hasFaceCenters)
            pivotPosition = (negativeFaceCenter + positiveFaceCenter) * 0.5f;
        else
            pivotPosition = sourceWheel.position;

        GameObject pivotObject = new GameObject(pivotName);
        Transform pivot = pivotObject.transform;
        // Keep the runtime wheel pivot directly under the Rigidbody root and
        // place it at the wheel Transform's own origin. Renderer.bounds.center
        // is a world-space visual bound and can be offset by mesh rotation,
        // scale, or an asymmetric tire; using it makes the tire orbit a point
        // that is not the authored axle.
        pivot.SetParent(transform, true);
        pivot.position = pivotPosition;
        pivot.rotation = sourceWheel.rotation;
        sourceWheel.SetParent(pivot, true);
        if (hasFaceCenters)
        {
            // The marker/source transform can be offset from the actual mesh
            // center. Leaving that offset below a rotating pivot makes the
            // tire travel in an orbit even when the pivot itself is correct.
            // Re-center the source under the pivot while preserving the
            // mesh center at the pivot origin.
            Vector3 meshCenterInSource = sourceWheel.InverseTransformPoint(pivot.position);
            Vector3 scaledCenter = Vector3.Scale(sourceWheel.localScale, meshCenterInSource);
            sourceWheel.localPosition = -(sourceWheel.localRotation * scaledCenter);
        }
        Collider[] wheelColliders = pivot.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < wheelColliders.Length; i++) wheelColliders[i].enabled = false;
        Vector3 sourceOriginOffset = sourceWheel.position - pivot.position;
        Renderer pivotRenderer = pivot.GetComponentInChildren<Renderer>(true);
        if (diagnosticLogging) Debug.Log("PLAYER CAR WHEEL GEOMETRY // " + sourceWheel.name
            + " faceCenters=" + hasFaceCenters
            + " center=" + pivot.position.ToString("F4")
            + " sourceOriginOffset=" + sourceOriginOffset.ToString("F4")
            + " faceLine=" + (positiveFaceCenter - negativeFaceCenter).ToString("F4")
            + " rendererCenter=" + (pivotRenderer == null ? "NONE" : pivotRenderer.bounds.center.ToString("F4"))
            + " rendererOffset=" + (pivotRenderer == null ? "NONE" : (pivotRenderer.bounds.center - pivot.position).ToString("F4")));
        return pivot;
    }

    static bool IsFrontWheelName(string wheelName)
    {
        return !string.IsNullOrEmpty(wheelName) && wheelName.IndexOf("WheelFront", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    Vector3 wheelBoundsForLog(Transform sourceWheel)
    {
        Renderer renderer = sourceWheel.GetComponentInChildren<Renderer>(true);
        return renderer != null ? renderer.bounds.center : sourceWheel.position;
    }

    void AddModelMeshColliders(GameObject model)
    {
        if (vehicleBodyPhysicsMaterial == null)
        {
            vehicleBodyPhysicsMaterial = new PhysicsMaterial("Vehicle Body Low Friction")
            {
                dynamicFriction = 0.05f,
                staticFriction = 0.05f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
        }
        BoxCollider rootBox = GetComponent<BoxCollider>();
        if (rootBox != null) rootBox.sharedMaterial = vehicleBodyPhysicsMaterial;
        MeshFilter[] meshes = model.GetComponentsInChildren<MeshFilter>(true);
        int colliderCount = 0;
        int wheelColliderCount = 0;
        for (int i = 0; i < meshes.Length; i++)
        {
            MeshFilter meshFilter = meshes[i];
            if (meshFilter.sharedMesh == null) continue;
            MeshCollider meshCollider = meshFilter.GetComponent<MeshCollider>();
            if (meshCollider == null) meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
            // Use only the root chassis box for body collision. Imported mesh
            // colliders can touch the terrain before the tire suspension does,
            // making the body press the tires below the surface.
            meshCollider.enabled = false;
            if (IsWheelPart(meshFilter.transform)) wheelColliderCount++;
            else colliderCount++;
        }
        if (diagnosticLogging) Debug.Log("PLAYER CAR COLLIDERS // FBX body colliders: " + colliderCount + ", wheel visual colliders disabled: " + wheelColliderCount);
    }

    bool IsWheelPart(Transform candidate)
    {
        Transform current = candidate;
        while (current != null && current != transform)
        {
            if (current.name.StartsWith("Cylinder", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (current.name.StartsWith("Wheel", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (current.name.IndexOf("Wheel Pivot", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            current = current.parent;
        }
        return false;
    }

    void InitializeEngineAudio()
    {
        if (!TryGetComponent<AudioSource>(out engineAudio))
        {
            engineAudio = null;
            return;
        }
        engineAudio.playOnAwake = false;
        engineAudio.loop = true;
        engineAudio.spatialBlend = 0f;
        engineAudio.volume = 0.08f;
        engineAudio.pitch = 0.9f;
        const int sampleRate = 22050;
        const int sampleCount = sampleRate;
        var clip = AudioClip.Create("VOYAGE // ENGINE LOOP", sampleCount, 1, sampleRate, false);
        float[] samples = new float[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            float time = i / (float)sampleRate;
            samples[i] = (Mathf.Sin(time * Mathf.PI * 2f * 110f) * 0.55f + Mathf.Sin(time * Mathf.PI * 2f * 220f) * 0.25f + Mathf.Sin(time * Mathf.PI * 2f * 330f) * 0.12f) * 0.18f;
        }
        clip.SetData(samples, 0);
        engineAudio.clip = clip;
        engineAudio.Play();
        if (hornAudio == null)
        {
            hornAudio = gameObject.AddComponent<AudioSource>();
            hornAudio.playOnAwake = false;
            hornAudio.loop = false;
            hornAudio.spatialBlend = 0f;
            hornAudio.volume = 0.22f;
            const int hornSamples = 11025;
            var hornClip = AudioClip.Create("VOYAGE // HORN", hornSamples, 1, sampleRate, false);
            float[] hornData = new float[hornSamples];
            for (int i = 0; i < hornData.Length; i++)
            {
                float time = i / (float)sampleRate;
                float envelope = Mathf.Clamp01(1f - time * 2.2f);
                hornData[i] = (Mathf.Sin(time * Mathf.PI * 2f * 390f) * 0.55f + Mathf.Sin(time * Mathf.PI * 2f * 520f) * 0.35f) * envelope * 0.42f;
            }
            hornClip.SetData(hornData, 0);
            hornAudio.clip = hornClip;
        }
        if (mudAudio == null) mudAudio = CreateTerrainAudio("VOYAGE // MUD ROAR", 78f, 0.19f);
        if (waterAudio == null) waterAudio = CreateTerrainAudio("VOYAGE // WATER SPLASH", 142f, 0.14f);
        if (skidAudio == null) skidAudio = CreateTerrainAudio("VOYAGE // TIRE SCRUB", 310f, 0.1f);
    }

    AudioSource CreateTerrainAudio(string clipName, float frequency, float volume)
    {
        var source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        source.volume = 0f;
        const int sampleRate = 22050;
        int sampleCount = sampleRate;
        var clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
        float[] data = new float[sampleCount];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)sampleRate;
            float grit = Mathf.Sin(t * Mathf.PI * 2f * frequency) * 0.58f + Mathf.Sin(t * Mathf.PI * 2f * frequency * 1.91f) * 0.22f;
            float pulse = 0.72f + Mathf.Sin(t * Mathf.PI * 2f * 3.2f) * 0.28f;
            data[i] = grit * pulse * volume;
        }
        clip.SetData(data, 0);
        source.clip = clip;
        return source;
    }

    public void Honk()
    {
        if (hornAudio != null) hornAudio.PlayOneShot(hornAudio.clip);
    }

    void UpdateEngineAudio(float throttle, float forwardSpeed)
    {
        if (engineAudio == null) return;
        float speedLoad = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / 22f);
        float throttleLoad01 = Mathf.Clamp01(Mathf.Abs(throttle));
        float terrainLoad = isInMud || isInWater ? 0.18f : 0f;
        engineAudio.pitch = Mathf.Lerp(0.78f, 1.32f, Mathf.Clamp01(speedLoad * 0.62f + throttleLoad01 * 0.42f + terrainLoad));
        engineAudio.volume = Mathf.Lerp(0.035f, 0.16f, Mathf.Clamp01(0.18f + speedLoad * 0.45f + throttleLoad01 * 0.45f + terrainLoad));
    }
    GameObject CreatePart(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        string prefabName = type == PrimitiveType.Cylinder ? "Cylinder" : type == PrimitiveType.Sphere ? "Sphere" : "Cube";
        var p = PrefabRuntime.Spawn(prefabName, name, transform.position, transform.rotation);
        p.transform.SetParent(transform);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        p.GetComponent<Renderer>().sharedMaterial = mat;
        Destroy(p.GetComponent<Collider>());
        return p;
    }
    GameObject CreatePart(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Color color) { var m = new Material(Shader.Find("Universal Render Pipeline/Lit")); m.color = color; return CreatePart(type, name, pos, scale, m); }
    Light CreateHeadlight(string name, Vector3 localPosition)
    {
        var go = PrefabRuntime.Spawn("Cube", name, transform.position, transform.rotation);
        go.transform.SetParent(transform);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.Euler(7f, 90f, 0f);
        var renderer = go.GetComponent<Renderer>(); if (renderer != null) renderer.enabled = false;
        var collider = go.GetComponent<Collider>(); if (collider != null) collider.enabled = false;
        var light = go.AddComponent<Light>();
        light.type = LightType.Spot;
        light.color = new Color(1f, 0.82f, 0.5f);
        light.intensity = 4f;
        light.range = 28f;
        light.spotAngle = 38f;
        light.innerSpotAngle = 20f;
        light.shadows = LightShadows.None;
        light.enabled = false;
        return light;
    }
    public void SetHeadlights(bool enabled)
    {
        if (leftHeadlight != null) leftHeadlight.enabled = enabled;
        if (rightHeadlight != null) rightHeadlight.enabled = enabled;
    }
    void FixedUpdate()
    {
        RunDrivingControl();
        return;
    }

    void LateUpdate()
    {
        // Physics runs at a fixed rate, but wheel visuals must be sampled at
        // the render rate. Updating them only in FixedUpdate makes the tires
        // visibly snap from one suspension sample to the next.
        if (visualsBuilt && terrainFollower != null)
            UpdateWheelVisuals();
    }

    void RunDrivingControl()
    {
        if (terrainFollower == null) return;


        Vector2 pad = Gamepad.current != null ? Gamepad.current.leftStick.ReadValue() : Vector2.zero;
        float keyboardThrottle = (ReadKey(KeyCode.W) || ReadKey(KeyCode.UpArrow) ? 1f : 0f) - (ReadKey(KeyCode.S) || ReadKey(KeyCode.DownArrow) ? 1f : 0f);
        float keyboardSteer = (ReadKey(KeyCode.D) || ReadKey(KeyCode.RightArrow) ? 1f : 0f) - (ReadKey(KeyCode.A) || ReadKey(KeyCode.LeftArrow) ? 1f : 0f);
        float throttle = Mathf.Abs(pad.y) > 0.12f ? pad.y : keyboardThrottle;
        float inputSteer = Mathf.Abs(pad.x) > 0.12f ? pad.x : keyboardSteer;
        bool boost = ReadKey(KeyCode.LeftShift) || ReadKey(KeyCode.RightShift) || (Gamepad.current != null && Gamepad.current.rightTrigger.isPressed);
        bool handbrake = ReadKey(KeyCode.Space) || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);

        if (!controlEnabled)
        {
            throttle = 0f;
            inputSteer = 0f;
            handbrake = true;
        }

        // Shift is a sustained accelerator, not an instant speed multiplier.
        // It can also drive the vehicle by itself when W is not pressed.
        if (boost && throttle >= 0f)
            throttle = 1f;

        if (!controlStateLogged)
        {
            controlStateLogged = true;
            Debug.Log("VEHICLE CONTROL STATE // enabled=" + controlEnabled
                + " keyboardThrottle=" + keyboardThrottle.ToString("F1")
                + " grounded=" + terrainFollower.GroundedCount);
        }

        if (surfaceProbeClock <= 0f)
        {
            GetSurfaceGrip();
            surfaceProbeClock = 0.08f;
        }
        surfaceProbeClock -= Time.fixedDeltaTime;

        Vector3 velocity = terrainFollower.CurrentVelocity;
        if (!driveMotionLogged && Mathf.Abs(throttle) > 0.01f && velocity.magnitude > 0.25f)
        {
            driveMotionLogged = true;
            Debug.Log("VEHICLE DRIVE MOTION // speed=" + velocity.magnitude.ToString("F2")
                + " position=" + transform.position.ToString("F2")
                + " grounded=" + terrainFollower.GroundedCount);
        }
        // This vehicle prefab is authored with local X+ as forward.
        Vector3 vehicleForward = terrainFollower.ForwardDirection;
        float forwardSpeed = Vector3.Dot(velocity, vehicleForward);
        bool directionChange = (throttle < -0.1f && forwardSpeed > 1.5f) || (throttle > 0.1f && forwardSpeed < -1.5f);
        bool braking = handbrake || directionChange;
        if (directionChange) throttle = 0f;

        float traction = surfaceGripValue;
        // Normal road speed is 32 m/s (~115 km/h). Shift progressively adds
        // a small high-speed reserve instead of applying a fixed 1.2x cap.
        float maxTargetSpeed = 32f * (lowRange ? 0.72f : 1f)
            * (boost ? 1.5f : 1f) * externalSpeedMultiplier;
        if (fuelStarved) maxTargetSpeed *= 0.35f;
        float suspensionSteer = Mathf.Lerp(1f, 0.55f, suspensionUpgradeLevel / 3f);
        // Steering is an input direction, not stored momentum. Releasing A/D
        // must immediately leave the front wheels straight; only actual tire
        // slip may then change the vehicle heading.
        steer = Mathf.Clamp(inputSteer + damageSteerBias * suspensionSteer, -1f, 1f);
        throttleLoad = Mathf.Abs(throttle) * (boost ? 1.5f : 1f);
        grounded = terrainFollower.IsGrounded;
        groundNormal = terrainFollower.GroundNormal;
        // Keep the driver's requested heading stable across terrain triangles.
        // Projecting onto the instantaneous contact normal made the target
        // velocity rotate whenever a wheel found a new triangle, causing
        // passive direction changes and weak WASD control on slopes.
        Vector3 driveForward = Vector3.ProjectOnPlane(vehicleForward, Vector3.up);
        if (driveForward.sqrMagnitude < 0.01f) driveForward = vehicleForward;
        driveForward.Normalize();
        Vector3 targetVelocity = driveForward * (throttle * maxTargetSpeed);
        if (!driveCommandLogged && Mathf.Abs(throttle) > 0.01f)
        {
            driveCommandLogged = true;
            Debug.Log("VEHICLE DRIVE COMMAND // throttle=" + throttle.ToString("F2")
                + " targetSpeed=" + targetVelocity.magnitude.ToString("F2")
                + " grounded=" + terrainFollower.GroundedCount);
        }

        terrainFollower.SetTraction(traction * (differentialLock ? 1.08f : 1f));
        terrainFollower.SetDriveVelocity(targetVelocity);
        terrainFollower.SetSteering(steer);
        terrainFollower.SetBrake(braking ? 1f : 0f);
        terrainFollower.SetHandbrake(handbrake);

        float lateralSpeed = transform.InverseTransformDirection(velocity).z;
        UpdateTailLights(throttle, forwardSpeed);
        UpdateBodyVisuals(throttle, inputSteer, lateralSpeed);
        UpdateEngineAudio(throttle, forwardSpeed);
        UpdateTerrainAudio();
    }


    void UpdateTerrainAudio()
    {
        float speedMix = Mathf.Clamp01(speedKmh / 65f);
        float throttleMix = Mathf.Clamp01(Mathf.Abs(throttleLoad) * 0.55f);
        float load = Mathf.Clamp01(0.12f + speedMix * 0.62f + throttleMix * 0.42f);
        float mudVolume = isInMud ? load * 0.22f : 0f;
        float waterVolume = isInWater ? load * 0.28f : 0f;
        if (mudAudio != null)
        {
            mudAudio.volume = Mathf.MoveTowards(mudAudio.volume, mudVolume, Time.fixedDeltaTime * 1.8f);
            mudAudio.pitch = Mathf.Lerp(0.72f, 1.22f, speedMix);
            if (mudAudio.volume > 0.005f && !mudAudio.isPlaying) mudAudio.Play();
            if (mudAudio.volume <= 0.005f && mudAudio.isPlaying) mudAudio.Stop();
        }
        if (waterAudio != null)
        {
            waterAudio.volume = Mathf.MoveTowards(waterAudio.volume, waterVolume, Time.fixedDeltaTime * 2.2f);
            waterAudio.pitch = Mathf.Lerp(0.78f, 1.35f, speedMix);
            if (waterAudio.volume > 0.005f && !waterAudio.isPlaying) waterAudio.Play();
            if (waterAudio.volume <= 0.005f && waterAudio.isPlaying) waterAudio.Stop();
        }
        bool handbrake = ReadKey(KeyCode.Space) || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);
        float skidLoad = handbrake && speedKmh > 7f ? Mathf.Clamp01((LateralSpeedKmh - 0.8f) / 8f) * Mathf.Clamp01(speedKmh / 24f) : 0f;
        if (skidAudio != null)
        {
            float target = skidLoad * 0.2f;
            skidAudio.volume = Mathf.MoveTowards(skidAudio.volume, target, Time.fixedDeltaTime * 3.5f);
            skidAudio.pitch = Mathf.Lerp(0.78f, 1.38f, Mathf.Clamp01(speedKmh / 30f));
            if (skidAudio.volume > 0.005f && !skidAudio.isPlaying) skidAudio.Play();
            if (skidAudio.volume <= 0.005f && skidAudio.isPlaying) skidAudio.Stop();
        }
    }

    void UpdateBodyVisuals(float throttle, float inputSteer, float lateralSpeed)
    {
        if (visualBody == null) return;
        float targetMud = isInMud ? 1f : 0f;
        float cleanRate = isInWater ? 0.55f : 0.09f;
        mudVisualLevel = Mathf.MoveTowards(mudVisualLevel, targetMud, Time.fixedDeltaTime * (targetMud > mudVisualLevel ? 0.22f : cleanRate));
        if (bodyMaterial != null) bodyMaterial.color = Color.Lerp(pristineBodyColor, new Color(0.19f, 0.095f, 0.035f), mudVisualLevel);
        UpdateTerrainFeedbackVisuals(throttle, lateralSpeed);
        // Calculate visual pitch and roll from terrain normal and driving forces.
        float pitchAngle = 0f;
        float rollAngle = 0f;
        if (terrainFollower != null && terrainFollower.IsGrounded)
        {
            Vector3 localNormal = transform.InverseTransformDirection(groundNormal);
            pitchAngle = -localNormal.x * 22f - throttle * 3.5f;
            rollAngle = localNormal.z * 22f + inputSteer * 4.5f;
        }
        Quaternion bodyTarget = Quaternion.Euler(rollAngle, 0f, pitchAngle);
        visualBody.localRotation = Quaternion.Slerp(visualBody.localRotation, bodyTarget, Time.fixedDeltaTime * 8f);
        if (visualCabin != null)
        {
            Quaternion cabinTarget = Quaternion.Euler(rollAngle * 1.15f, 0f, pitchAngle * 1.15f);
            visualCabin.localRotation = Quaternion.Slerp(visualCabin.localRotation, cabinTarget, Time.fixedDeltaTime * 5f);
        }
    }

    void UpdateTerrainFeedbackVisuals(float throttle, float lateralSpeed)
    {
        bool active = isInMud || isInWater;
        float speedMix = Mathf.Clamp01(speedKmh / 65f);
        float load = Mathf.Clamp01(Mathf.Abs(throttle) * 0.7f + Mathf.Abs(lateralSpeed) * 0.08f + speedMix * 0.35f);
        Color effectColor = isInWater ? new Color(0.18f, 0.75f, 0.95f) : new Color(0.42f, 0.22f, 0.07f);
        float pulse = 1f + Mathf.Sin(Time.fixedTime * (isInWater ? 15f : 10f)) * 0.2f * load;
        for (int i = 0; i < terrainEffects.Count; i++)
        {
            var effect = terrainEffects[i];
            if (effect == null) continue;
            effect.gameObject.SetActive(active && load > 0.08f);
            if (!effect.gameObject.activeSelf) continue;
            var effectMaterial = i < terrainEffectMaterials.Count ? terrainEffectMaterials[i] : null;
            if (effectMaterial != null) effectMaterial.color = effectColor;
            float side = effect.localPosition.x < 0f ? -1f : 1f;
            float spread = Mathf.Lerp(0.12f, 0.34f, load) * (isInWater ? 1.35f : 1f);
            effect.localPosition = new Vector3(side * (1.02f + spread * 0.18f), -0.2f - load * 0.03f, effect.localPosition.z);
            effect.localScale = new Vector3(0.12f + spread, 0.06f + spread * 0.35f, 0.12f + spread * pulse);
        }
    }

    void UpdateTailLights(float throttle, float forwardSpeed)
    {
        bool reversing = throttle < -0.1f;
        bool braking = ReadKey(KeyCode.Space) || (Mathf.Abs(forwardSpeed) > 1.2f && Mathf.Abs(throttle) < 0.01f);
        Color lightColor = reversing ? new Color(0.92f, 0.95f, 1f) : (braking ? new Color(1f, 0.02f, 0.01f) : new Color(0.42f, 0.01f, 0.01f));
        for (int i = 0; i < tailLightRenderers.Count; i++)
        {
            var material = i < tailLightMaterials.Count ? tailLightMaterials[i] : null;
            if (material == null) continue;
            material.color = lightColor;
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", lightColor * (braking || reversing ? 2.5f : 0.7f));
        }
    }

    void UpdateWheelVisuals()
    {
        // Keep each wheel at its authored local position. The wheel transform
        // is its own pivot, so only rotate that pivot; never overwrite its
        // world position from the suspension solver.
        Vector3 vehicleForward = terrainFollower != null ? terrainFollower.ForwardDirection : transform.right;
        // Integrate a smoothed angular speed instead of accumulating the
        // difference between two rendered Rigidbody positions. The latter
        // inherits every fixed-step/interpolation boundary and makes the
        // tire visibly advance in small steps while the car is moving.
        float radius = Mathf.Max(0.05f, terrainFollower.tireRadius);
        Vector3 velocity = rb != null ? rb.linearVelocity : terrainFollower.CurrentVelocity;
        float forwardSpeed = Vector3.Dot(velocity, vehicleForward);
        // The authored tire mesh rolls in the positive local axle direction
        // when the vehicle's forward speed is positive. The previous minus
        // sign made W visibly spin the tire backward.
        float targetSpinRate = forwardSpeed / (Mathf.PI * 2f * radius) * 360f;
        wheelSpinRateDegrees = Mathf.SmoothDamp(
            wheelSpinRateDegrees,
            targetSpinRate,
            ref wheelSpinRateVelocity,
            0.12f,
            Mathf.Infinity,
            Mathf.Max(0.0001f, Time.deltaTime));
        wheelSpinDegrees = Mathf.Repeat(
            wheelSpinDegrees + wheelSpinRateDegrees * Mathf.Max(0.0001f, Time.deltaTime),
            360f);
        for (int i = 0; i < wheels.Count; i++)
        {
            var wheel = wheels[i];
            if (wheel == null) continue;
            // Apply only the filtered suspension travel along the chassis up
            // axis. The pivot remains centered on the wheel Transform origin;
            // raw terrain contact points never move it sideways.
            if (terrainFollower != null && i < 4)
                wheel.position = terrainFollower.GetWheelVisualPosition(i);
            if (!wheelRuntimeGeometryLogged && usingVehicleModel)
            {
                Renderer wheelRenderer = wheel.GetComponentInChildren<Renderer>(true);
                if (wheelRenderer != null)
                {
                    if (diagnosticLogging) Debug.Log("PLAYER CAR WHEEL RUNTIME // " + wheel.name
                        + " pivot=" + wheel.position.ToString("F4")
                        + " rendererCenter=" + wheelRenderer.bounds.center.ToString("F4")
                        + " rendererExtents=" + wheelRenderer.bounds.extents.ToString("F4"));
                    wheelRuntimeGeometryLogged = true;
                }
            }
            Vector3 wheelLocalPosition = usingVehicleModel ? transform.InverseTransformPoint(wheel.position) : wheel.localPosition;
            bool isFrontWheel = usingVehicleModel && wheelIsFront.Count == wheels.Count
                ? wheelIsFront[i]
                : (usingVehicleModel && hasModelAxleLayout
                    ? (modelFrontIsPositiveAxis
                        ? (modelForwardAlongX ? wheelLocalPosition.x : wheelLocalPosition.z) >= modelAxleMidZ
                        : (modelForwardAlongX ? wheelLocalPosition.x : wheelLocalPosition.z) <= modelAxleMidZ)
                    : wheel.localPosition.x > 0f);
            float steeringAngle = isFrontWheel ? steer * 22f : 0f;
            Quaternion rest = i < wheelRestRotations.Count ? wheelRestRotations[i] : wheel.localRotation;
            // Both steering and rolling stay on the wheel's own pivot. The
            // rolling axis is measured from that wheel mesh (or explicitly
            // supplied by the procedural fallback); never assume every FBX
            // tire uses the same local axis.
            Vector3 spinAxis = i < wheelSpinAxes.Count && wheelSpinAxes[i].sqrMagnitude > 0.001f
                ? wheelSpinAxes[i].normalized
                : Vector3.forward;
            // Keep the visual wheel at its authored suspension pivot. The
            // physics suspension still supports the body, but visual wheel
            // placement is not rewritten from raycast contacts every frame;
            // that competing transform update was the source of the visible
            // one-step tire hitching.
            wheel.localRotation = Quaternion.AngleAxis(steeringAngle, Vector3.up)
                * rest
                * Quaternion.AngleAxis(wheelSpinDegrees, spinAxis);
            if (usingVehicleModel && Mathf.Abs(steer) > 0.1f && !steeringMappingLogged && i == wheels.Count - 1)
            {
                steeringMappingLogged = true;
                if (diagnosticLogging) Debug.Log("PLAYER CAR STEERING // front axle uses local " + (modelForwardAlongX ? "X" : "Z") + " " + (modelFrontIsPositiveAxis ? ">= " : "<= ") + modelAxleMidZ.ToString("0.00"));
            }
        }
    }

    float GetSurfaceGrip()
    {
        float weatherGrip = 1f;
        float grip = 1f;
        bool foundSurface = false;
        bool mud = false;
        bool water = false;
        bool trail = false;
        for (int i = 0; i < surfaceSampleOffsets.Length; i++)
        {
            Vector3 sampleOrigin = transform.TransformPoint(surfaceSampleOffsets[i]) + Vector3.up * 0.8f;
            if (!Physics.Raycast(sampleOrigin, Vector3.down, out RaycastHit hit, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)) continue;
            if (hit.collider == null || hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) continue;
            foundSurface = true;
            string surfaceName = hit.collider.gameObject.name;
            if (surfaceName.Contains("MUD PATCH"))
            {
                mud = true;
                grip = Mathf.Min(grip, 0.42f);
                continue;
            }
            if (surfaceName.Contains("MOUNTAIN STREAM"))
            {
                water = true;
                grip = Mathf.Min(grip, 0.52f);
                continue;
            }
            if (hit.collider is TerrainCollider) grip = Mathf.Min(grip, 0.68f);
            else if (surfaceName.Contains("TrailSegment"))
            {
                trail = true;
                grip = Mathf.Min(grip, 0.94f);
            }
        }
        isInMud = mud;
        isInWater = water;
        surfaceType = !foundSurface ? "UNKNOWN" : (mud && water ? "MIXED" : (mud ? "MUD" : (water ? "WATER" : (trail ? "TRAIL" : "GROUND"))));
        if (!foundSurface)
        {
            surfaceGripValue = 0.8f * weatherGrip;
            return surfaceGripValue;
        }
        surfaceGripValue = grip * weatherGrip;
        return surfaceGripValue;
    }
    public void SetFuelStarved(bool value) { fuelStarved = value; }
    public void SetExternalSpeedMultiplier(float value) { externalSpeedMultiplier = Mathf.Clamp(value, 0.1f, 1f); }
    public void SetControlEnabled(bool value)
    {
        controlEnabled = value;
        if (!value) throttleLoad = 0f;
        if (engineAudio != null)
        {
            if (value && !engineAudio.isPlaying) engineAudio.Play();
            else if (!value && engineAudio.isPlaying) engineAudio.Pause();
        }
    }
    bool ReadKey(KeyCode key)
    {
        bool legacy = Input.GetKey(key);
        if (Keyboard.current == null)
        {
            if (key == KeyCode.Space) return legacy || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);
            return legacy;
        }
        switch (key)
        {
            case KeyCode.W: return legacy || Keyboard.current.wKey.isPressed;
            case KeyCode.A: return legacy || Keyboard.current.aKey.isPressed;
            case KeyCode.S: return legacy || Keyboard.current.sKey.isPressed;
            case KeyCode.D: return legacy || Keyboard.current.dKey.isPressed;
            case KeyCode.UpArrow: return legacy || Keyboard.current.upArrowKey.isPressed;
            case KeyCode.DownArrow: return legacy || Keyboard.current.downArrowKey.isPressed;
            case KeyCode.LeftArrow: return legacy || Keyboard.current.leftArrowKey.isPressed;
            case KeyCode.RightArrow: return legacy || Keyboard.current.rightArrowKey.isPressed;
            case KeyCode.LeftShift: return legacy || Keyboard.current.leftShiftKey.isPressed;
            case KeyCode.RightShift: return legacy || Keyboard.current.rightShiftKey.isPressed;
            case KeyCode.Space: return legacy || Keyboard.current.spaceKey.isPressed || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);
            case KeyCode.K: return legacy || Keyboard.current.kKey.isPressed;
            case KeyCode.L: return legacy || Keyboard.current.lKey.isPressed;
            default: return legacy;
        }
    }
    bool ReadKeyDownLocal(KeyCode key)
    {
        if (Input.GetKeyDown(key)) return true;
        if (Keyboard.current == null && Gamepad.current == null) return false;
        if (key == KeyCode.K) return (Keyboard.current != null && Keyboard.current.kKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.rightShoulder.wasPressedThisFrame);
        if (key == KeyCode.L) return (Keyboard.current != null && Keyboard.current.lKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.leftShoulder.wasPressedThisFrame);
        return false;
    }
    public void ResetCar(Vector3 pos)
    {
        if (terrainFollower != null) terrainFollower.StopImmediately();
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        damage = Mathf.Min(100f, damage + 2f);
        // Reset the Rigidbody itself, not only the Transform. Writing only
        // transform.position can leave PhysX at the old pose for one step,
        // which makes the car appear airborne or rotate without propulsion.
        rb.position = pos;
        rb.rotation = Quaternion.identity;
        Physics.SyncTransforms();
        if (terrainFollower != null) terrainFollower.SnapToTerrainNow();
    }
}
