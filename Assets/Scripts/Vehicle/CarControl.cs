using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

[RequireComponent(typeof(AudioSource))]
public class CarControl : MonoBehaviour
{
    public enum GearMode
    {
        Park,
        Reverse,
        Neutral,
        Drive,
        Sport,
        H6,
        L6
    }

    [Header("Camera Bindings")]
    [SerializeField] private GameObject cockpitCam; // 拖入你的第一人称/座舱相机物体

    [Header("Gear")]
    [SerializeField] private GearMode startGear = GearMode.Park;
    [SerializeField] private GearMode currentGear = GearMode.Park;
    public GearMode CurrentGear => currentGear;
    public event System.Action<GearMode> OnGearChanged;
    [SerializeField] private bool engineOn = false;
    public bool EngineOn => engineOn;
    public event System.Action<bool> OnEngineStateChanged;
    [SerializeField] private bool electricalPowerOn = true;
    public bool ElectricalPowerOn => electricalPowerOn;
    public event System.Action<bool> OnElectricalPowerChanged;

    [Header("Start Procedure")]
    [SerializeField] private StartProcedure startProcedure;
    [SerializeField] private bool startWithEngineRunning = false;

    [Header("Modes")]
    [SerializeField] private float sportTorque = 50000f;
    [SerializeField] private float sixLockTorque = 50000f;
    [SerializeField] private float h6MaxSpeedKmh = 75f;
    [SerializeField] private float l6MaxSpeedKmh = 35f;
    [SerializeField] private float driveMaxSpeedKmh = 160f;
    [SerializeField] private float reverseMaxSpeedKmh = 30f;
    [SerializeField] private float sixLockSwitchMaxWheelRpm = 0.01f;
    [SerializeField] private float speedLimiterBrake = 0.2f;
    [SerializeField] private WheelControl[] sixLockWheels = new WheelControl[6];

    public float motorTorque = 35000;
    public float brakeTorque = 400000;
    public float eBrakeTorque = 10000000f;
    public float maxSpeed = 20;
    public float steeringRange = 30;
    public float steeringRangeAtMaxSpeed = 10;
    public float centreOfGravityOffset = -1f;

    [Header("Transmission (Auto)")]
    [SerializeField] private float finalDriveRatio = 3.42f;
    [SerializeField] private float[] forwardGearRatios = new float[] { 3.5f, 2.0f, 1.4f, 1.0f, 0.75f, 0.6f };
    [SerializeField] private float reverseGearRatio = 3.0f;
    [SerializeField] private float engineMinRpm = 500f;
    [SerializeField] private float upshiftRpm = 2200f;
    [SerializeField] private float downshiftRpm = 1200f;
    [SerializeField] private float shiftDuration = 0.5f;

    private int currentTransmissionGear = 0;
    private float shiftTimer = 0f;
    private float smoothEngineRpm = 500f;
    public float SmoothEngineRpm => smoothEngineRpm;

    [Header("Fuel & Regeneration System")]
    [Tooltip("燃油消耗速率倍率。1为默认，数值越小越慢（如0.5），数值越大越快（如2）")]
    [SerializeField] private float fuelConsumptionMultiplier = 1f;
    
    [Tooltip("滑行或怠速（未踩油门）时的基础轻微消耗百分比（占最大油门消耗的比例，0.02表示2%）")]
    [SerializeField] private float idleConsumptionFactor = 0.02f;
    
    [Header("Regenerative Braking")]
    [Tooltip("动能回收效率 (0-1)。0=无回收，1=100%动能转化为电量")]
    [SerializeField] private float regenEfficiency = 0.3f;
    
    [Tooltip("动能回收最大功率（每秒回收多少燃油当量）")]
    [SerializeField] private float maxRegenPower = 2f;
    
    [Tooltip("动能回收生效的最小速度 (km/h)")]
    [SerializeField] private float regenMinSpeed = 5f;
    
    [Tooltip("动能回收生效的最大刹车力度阈值 (0-1)")]
    [SerializeField] private float regenBrakeThreshold = 0.1f;

    [Header("Control Override")]
    [SerializeField] private bool activeControl = false;
    public bool ActiveControl { get => activeControl; set => activeControl = value; }
    
    [SerializeField] private TextMeshProUGUI speedDisplay;
    [SerializeField] private TextMeshProUGUI rpmDisplay;
    [SerializeField] private TextMeshProUGUI gearDisplay;
    [SerializeField] private float speedMultiplier = 1f;
    [SerializeField] private Transform steeringWheel;
    [SerializeField] private Vector3 steeringWheelLocalAxis = new Vector3(0, 0, 1);
    [SerializeField] private float steeringWheelMaxTurn = 540f;
    [SerializeField] private bool invertSteeringWheel = false;
    [SerializeField] private float steeringResponseSpeed = 45f;
    [SerializeField] private float steeringReturnSpeed = 120f;
    [SerializeField] private float steeringReturnMinSpeedKmh = 1f;
    [SerializeField] private float innerSteerAngle = 37f;
    [SerializeField] private float outerSteerAngle = 25f;
    [SerializeField] private float l6ThrottleRise = 0.4f;
    [SerializeField] private float l6ThrottleFall = 0.8f;
    [SerializeField] private AnimationCurve l6TorqueBySpeed = new AnimationCurve(
        new Keyframe(0f, 1f),
        new Keyframe(10f, 1f),
        new Keyframe(25f, 0.85f),
        new Keyframe(35f, 0.7f)
    );
    [SerializeField] private AnimationCurve steeringReturnBySpeed = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(10f, 0.3f),
        new Keyframe(30f, 0.7f),
        new Keyframe(80f, 1f)
    );
    private Quaternion steeringWheelInitialLocalRotation;
    [SerializeField] private AnimationCurve steeringLimitBySpeed = new AnimationCurve(
        new Keyframe(0f, 1f),
        new Keyframe(30f, 0.27f),
        new Keyframe(60f, 0.11f),
        new Keyframe(80f, 0.065f),
        new Keyframe(100f, 0.045f),
        new Keyframe(120f, 0.0335f),
        new Keyframe(140f, 0.0205f),
        new Keyframe(160f, 0.0205f)
    );

    // ========== 引擎声音参数 ==========
    [Header("Engine Audio")]
    [Range(0f, 0.8f)] public float engineMasterVolume = 0.5f;
    [Range(0.05f, 0.25f)] public float pulseWidth = 0.12f;
    [Range(0f, 1f)] public float pulseSharpness = 0.6f;
    [Range(0f, 1f)] public float exhaustResonance = 0.7f;
    [Range(0f, 1f)] public float exhaustDrone = 0.4f;
    [Range(0f, 1f)] public float intakeSound = 0.5f;
    [Range(0f, 1f)] public float turboWhine = 0.6f;
    [Range(0f, 0.15f)] public float mechanicalNoise = 0.07f;
    [Range(0f, 0.3f)] public float cylinderImbalance = 0.15f;

    WheelControl[] wheels;
    Rigidbody rigidBody;
    private float currentSteerAngle;
    private float currentSpeedKmh;
    public float CurrentSpeedKmh => currentSpeedKmh;
    private float l6ThrottleCurrent;
    private readonly HashSet<WheelControl> sixLockWheelSet = new HashSet<WheelControl>();
    private static readonly string[] ForwardGearDisplay = { "D1", "D2", "D3", "D4", "D5", "D6" };
    private int lastDisplayedSpeedKmh = int.MinValue;
    private int lastDisplayedRpm = int.MinValue;
    private string lastDisplayedGear;
    
    // 引擎声音相关变量
    private float engineLoad = 0f;
    private double phase;
    private double exhaustPhase;
    private double intakePhase;
    private double turboPhase;
    private double samplingRate;
    private uint noiseSeed = 123456789u;
    
    // 动能回收相关
    private float lastFrameVelocity = 0f;
    private Vector3 lastFramePosition;

    public void SetGear(GearMode gear)
    {
        SetGearInternal(gear, false);
    }

    public void SetEngineOn(bool value)
    {
        if (engineOn == value)
        {
            return;
        }

        engineOn = value;
        OnEngineStateChanged?.Invoke(engineOn);
    }

    public void SetElectricalPower(bool value)
    {
        if (electricalPowerOn == value)
        {
            return;
        }

        electricalPowerOn = value;
        OnElectricalPowerChanged?.Invoke(electricalPowerOn);
    }

    private void SetGearInternal(GearMode gear, bool force)
    {
        if (!force && currentGear == gear)
        {
            return;
        }

        if (!force && !CanSwitchGear(gear))
        {
            return;
        }

        currentGear = gear;
        OnGearChanged?.Invoke(currentGear);
    }

    private bool CanSwitchGear(GearMode targetGear)
    {
        bool currentSix = IsSixLockGear(currentGear);
        bool targetSix = IsSixLockGear(targetGear);
        if (currentSix || targetSix)
        {
            bool isHandBraking = activeControl && Input.GetKey(KeyCode.Space);
            return GetMaxWheelRpm() <= sixLockSwitchMaxWheelRpm || isHandBraking;
        }
        return true;
    }

    private static bool IsSixLockGear(GearMode gear)
    {
        return gear == GearMode.H6 || gear == GearMode.L6;
    }

    private void BuildSixLockWheelSet()
    {
        sixLockWheelSet.Clear();
        if (sixLockWheels == null)
        {
            return;
        }
        foreach (WheelControl wheel in sixLockWheels)
        {
            if (wheel != null)
            {
                sixLockWheelSet.Add(wheel);
            }
        }
    }

    private float GetMaxWheelRpm()
    {
        float maxRpm = 0f;
        if (sixLockWheelSet.Count > 0)
        {
            foreach (WheelControl wheel in sixLockWheelSet)
            {
                if (wheel == null || wheel.WheelCollider == null)
                {
                    continue;
                }
                float rpm = Mathf.Abs(wheel.WheelCollider.rpm);
                if (rpm > maxRpm)
                {
                    maxRpm = rpm;
                }
            }
            return maxRpm;
        }

        if (wheels == null)
        {
            return maxRpm;
        }

        foreach (WheelControl wheel in wheels)
        {
            if (wheel == null || wheel.WheelCollider == null)
            {
                continue;
            }
            float rpm = Mathf.Abs(wheel.WheelCollider.rpm);
            if (rpm > maxRpm)
            {
                maxRpm = rpm;
            }
        }
        return maxRpm;
    }

    private float GetStableWheelRpm(float forwardSpeed)
    {
        float radius = 0.35f;
        if (wheels != null && wheels.Length > 0 && wheels[0] != null && wheels[0].WheelCollider != null)
        {
            radius = wheels[0].WheelCollider.radius;
        }
        return (Mathf.Abs(forwardSpeed) * 60f) / (2f * Mathf.PI * radius);
    }

    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();

        if (startProcedure == null)
        {
            startProcedure = FindObjectOfType<StartProcedure>();
        }

        rigidBody.centerOfMass += Vector3.up * centreOfGravityOffset;

        wheels = GetComponentsInChildren<WheelControl>();
        BuildSixLockWheelSet();
        
        // 初始化 AudioSource
        AudioSource audioSource = GetComponent<AudioSource>();
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.spatialBlend = 0f;
            audioSource.Play();
        }
        
        samplingRate = AudioSettings.outputSampleRate;
        
        if (steeringWheel != null)
        {
            steeringWheelInitialLocalRotation = steeringWheel.localRotation;
        }

        SetGearInternal(startGear, true);
        
        // 初始化位置记录用于动能回收
        lastFramePosition = transform.position;

        if (startWithEngineRunning)
        {
            SetEngineOn(true);
            SetElectricalPower(true);
            
            if (startProcedure != null)
            {
                startProcedure.ForceStartVehicle();
            }
        }
        else
        {
            SetEngineOn(engineOn);
            if (startProcedure != null)
            {
                SetElectricalPower(startProcedure.HasAnyBatteryOn());
            }
            else
            {
                SetElectricalPower(true);
            }
        }

    }

    void Update()
    {
        if (startProcedure != null)
        {
            SetElectricalPower(startProcedure.HasAnyBatteryOn());
            if (engineOn != startProcedure.EngineOn)
            {
                SetEngineOn(startProcedure.EngineOn);
            }
        }
        
        float rawVertical = activeControl ? Input.GetAxis("Vertical") : 0f;
        float hInputRaw = activeControl ? Input.GetAxisRaw("Horizontal") : 0f;
        // Keep the reference legacy axes as the primary path. Unity 6 can
        // still have the Input System package active while the old axis
        // backend returns zero, so use the keyboard device only as a fallback.
        if (activeControl && Keyboard.current != null)
        {
            if (Mathf.Abs(rawVertical) < 0.01f)
            {
                rawVertical = (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed ? 1f : 0f)
                    - (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed ? 1f : 0f);
            }
            if (Mathf.Abs(hInputRaw) < 0.01f)
            {
                hInputRaw = (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed ? 1f : 0f)
                    - (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed ? 1f : 0f);
            }
        }
        float forwardSpeed = Vector3.Dot(transform.forward, rigidBody.linearVelocity);

        float displaySpeed = Mathf.Abs(forwardSpeed) * 3.6f * speedMultiplier;
        currentSpeedKmh = displaySpeed;
        UpdateSpeedDisplay(displaySpeed);

        float speedFactorMotor = Mathf.InverseLerp(0, maxSpeed, forwardSpeed);

        float requestedMotorTorque = motorTorque;
        if (currentGear == GearMode.Sport)
        {
            requestedMotorTorque = sportTorque;
        }
        else if (currentGear == GearMode.H6 || currentGear == GearMode.L6)
        {
            requestedMotorTorque = sixLockTorque;
        }

        if (currentGear == GearMode.L6)
        {
            requestedMotorTorque *= Mathf.Clamp01(l6TorqueBySpeed.Evaluate(displaySpeed));
        }

        float currentMotorTorque = Mathf.Lerp(requestedMotorTorque, 0, speedFactorMotor);

        float steeringLimitMultiplier = Mathf.Clamp01(steeringLimitBySpeed.Evaluate(displaySpeed));
        bool steeringLocked = currentGear == GearMode.Park;
        float outerMaxAngle = outerSteerAngle * steeringLimitMultiplier;
        float innerMaxAngle = innerSteerAngle * steeringLimitMultiplier;
        float currentMaxWheelSteerAngle = outerMaxAngle;

        float sumSteerAngles = 0f;
        int steerCount = 0;

        bool wantsForward = rawVertical > 0.01f;
        bool wantsBackward = rawVertical < -0.01f;

        float throttleInput = 0f;
        float brakeInput = 0f;

        switch (currentGear)
        {
            case GearMode.Park:
                throttleInput = 0f;
                brakeInput = 1f;
                steeringLocked = true;
                break;
            case GearMode.Neutral:
                throttleInput = 0f;
                brakeInput = wantsBackward ? Mathf.Abs(rawVertical) : 0f;
                break;
            case GearMode.Drive:
            case GearMode.Sport:
            case GearMode.H6:
            case GearMode.L6:
                if (wantsForward)
                {
                    // The RV1.0 WheelColliders are authored with the
                    // opposite spin direction to the chassis forward axis.
                    // This negative sign is present in the reference
                    // Cementery CarControl and is required for W to produce
                    // forward motion.
                    throttleInput = -rawVertical;
                }
                if (wantsBackward)
                {
                    brakeInput = Mathf.Abs(rawVertical);
                }
                break;
            case GearMode.Reverse:
                if (wantsForward)
                {
                    throttleInput = rawVertical;
                }
                if (wantsBackward)
                {
                    brakeInput = Mathf.Abs(rawVertical);
                }
                break;
        }

        float appliedThrottleInput = throttleInput;
        if (currentGear == GearMode.L6)
        {
            float rate = Mathf.Abs(throttleInput) > Mathf.Abs(l6ThrottleCurrent) ? l6ThrottleRise : l6ThrottleFall;
            l6ThrottleCurrent = Mathf.MoveTowards(l6ThrottleCurrent, throttleInput, rate * Time.deltaTime);
            appliedThrottleInput = l6ThrottleCurrent;
        }
        else
        {
            l6ThrottleCurrent = throttleInput;
        }

        float speedLimitKmh = 0f;
        switch (currentGear)
        {
            case GearMode.H6:
                speedLimitKmh = h6MaxSpeedKmh;
                break;
            case GearMode.L6:
                speedLimitKmh = l6MaxSpeedKmh;
                break;
            case GearMode.Drive:
            case GearMode.Sport:
                speedLimitKmh = driveMaxSpeedKmh;
                break;
            case GearMode.Reverse:
                speedLimitKmh = reverseMaxSpeedKmh;
                break;
        }

        if (speedLimitKmh > 0f && displaySpeed > speedLimitKmh)
        {
            appliedThrottleInput = 0f;
            if (currentGear == GearMode.L6)
            {
                l6ThrottleCurrent = Mathf.MoveTowards(l6ThrottleCurrent, 0f, l6ThrottleFall * Time.deltaTime);
            }
        }

        // --- Transmission & Engine RPM Calculation ---
        float currentGearRatio = 0f;

        if (currentGear == GearMode.Reverse) {
            currentGearRatio = reverseGearRatio;
        } else if (currentGear == GearMode.Drive || currentGear == GearMode.Sport || IsSixLockGear(currentGear)) {
            if (currentTransmissionGear < 0) currentTransmissionGear = 0;
            if (currentTransmissionGear >= forwardGearRatios.Length) currentTransmissionGear = forwardGearRatios.Length - 1;
            currentGearRatio = forwardGearRatios[currentTransmissionGear];
        }
        
        string gearString = GetGearDisplayString();
        UpdateGearDisplay(gearString);

        // ========== 用转速限制车速 ==========
        float maxEngineRpm = 2800f;

        float wheelRadius = 0.35f;
        if (wheels != null && wheels.Length > 0 && wheels[0] != null && wheels[0].WheelCollider != null)
        {
            wheelRadius = wheels[0].WheelCollider.radius;
        }

        if (currentGear == GearMode.Drive || currentGear == GearMode.Sport || IsSixLockGear(currentGear))
        {
            float maxWheelRpmForCurrentGear = maxEngineRpm / (currentGearRatio * finalDriveRatio);
            float maxForwardSpeedForCurrentGear = maxWheelRpmForCurrentGear * (2f * Mathf.PI * wheelRadius) / 60f;
            
            float absForwardSpeed = Mathf.Abs(forwardSpeed);
            if (absForwardSpeed > maxForwardSpeedForCurrentGear)
            {
                float limitedSpeed = Mathf.Sign(forwardSpeed) * maxForwardSpeedForCurrentGear;
                Vector3 currentVel = rigidBody.linearVelocity;
                float currentSideways = Vector3.Dot(transform.right, currentVel);
                float currentUp = Vector3.Dot(transform.up, currentVel);
                rigidBody.linearVelocity = transform.forward * limitedSpeed + transform.right * currentSideways + transform.up * currentUp;
                forwardSpeed = limitedSpeed;
            }
        }

        float absWheelRpm = GetStableWheelRpm(forwardSpeed);
        float calculatedEngineRpm = absWheelRpm * currentGearRatio * finalDriveRatio;
        float targetEngineRpm = Mathf.Max(engineMinRpm, Mathf.Min(calculatedEngineRpm, maxEngineRpm));

        // Auto-Shift Logic
        if (shiftTimer <= 0f && (currentGear == GearMode.Drive || currentGear == GearMode.Sport || IsSixLockGear(currentGear)))
        {
            if (targetEngineRpm > upshiftRpm && currentTransmissionGear < forwardGearRatios.Length - 1)
            {
                currentTransmissionGear++;
                shiftTimer = shiftDuration;
            }
            else if (targetEngineRpm < downshiftRpm && currentTransmissionGear > 0)
            {
                currentTransmissionGear--;
                shiftTimer = shiftDuration;
            }
        }

        if (shiftTimer > 0f)
        {
            shiftTimer -= Time.deltaTime;
            appliedThrottleInput = 0f;
            targetEngineRpm = engineMinRpm;
        }
        else if (currentGear == GearMode.Park || currentGear == GearMode.Neutral)
        {
            targetEngineRpm = engineMinRpm + Mathf.Abs(throttleInput) * (upshiftRpm - engineMinRpm);
        }

        // ★★★ 关键修改：引擎熄火时强制 throttle 为 0 ★★★
        if (!engineOn)
        {
            targetEngineRpm = 0f;
            appliedThrottleInput = 0f;
        }

        float rpmLerpSpeed = (targetEngineRpm > smoothEngineRpm) ? 5f : 3f;
        
        if (engineOn)
        {
            smoothEngineRpm = Mathf.Lerp(smoothEngineRpm, targetEngineRpm, Time.deltaTime * rpmLerpSpeed);
        }
        else
        {
            smoothEngineRpm = Mathf.Lerp(smoothEngineRpm, 0f, Time.deltaTime * 3f);
        }

        float targetLoad = engineOn ? Mathf.Abs(appliedThrottleInput) : 0f;
        engineLoad = Mathf.Lerp(engineLoad, targetLoad, Time.deltaTime * 8f);

        UpdateRpmDisplay(smoothEngineRpm);

        bool steerInputActive = Mathf.Abs(hInputRaw) > 0.01f;
        float targetSteerAngle = hInputRaw * currentMaxWheelSteerAngle;
        if (!steeringLocked && steerInputActive)
        {
            currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetSteerAngle, steeringResponseSpeed * Time.deltaTime);
        }
        else if (!steeringLocked && displaySpeed > steeringReturnMinSpeedKmh)
        {
            float returnSpeed = steeringReturnSpeed * Mathf.Clamp01(steeringReturnBySpeed.Evaluate(displaySpeed));
            currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, 0f, returnSpeed * Time.deltaTime);
        }

        bool isHandBraking = activeControl && Input.GetKey(KeyCode.Space);
        
        // ★★★ 动能回收处理 ★★★
        HandleRegenerativeBraking(brakeInput, isHandBraking);

        foreach (var wheel in wheels)
        {
            if (wheel.steerable)
            {
                float steerAngleForWheel = currentSteerAngle;
                if (wheel.isFrontLeft || wheel.isFrontRight)
                {
                    float absOuter = Mathf.Abs(currentSteerAngle);
                    if (absOuter > 0.0001f)
                    {
                        float ratio = outerMaxAngle > 0.001f ? (innerMaxAngle / outerMaxAngle) : 1f;
                        float absInner = absOuter * ratio;
                        bool turningRight = currentSteerAngle > 0f;
                        bool isInner = (turningRight && wheel.isFrontRight) || (!turningRight && wheel.isFrontLeft);
                        steerAngleForWheel = Mathf.Sign(currentSteerAngle) * (isInner ? absInner : absOuter);
                    }
                }

                wheel.WheelCollider.steerAngle = steerAngleForWheel;
                sumSteerAngles += wheel.WheelCollider.steerAngle;
                steerCount++;
            }
            
            if (currentGear == GearMode.Park)
            {
                wheel.WheelCollider.brakeTorque = brakeTorque;
                wheel.WheelCollider.motorTorque = 0f;
                continue;
            }

            if (isHandBraking)
            {
                wheel.WheelCollider.brakeTorque = eBrakeTorque;
                wheel.WheelCollider.motorTorque = 0f;
            }
            else
            {
                wheel.WheelCollider.brakeTorque = brakeInput * brakeTorque;
                bool isSixLock = IsSixLockGear(currentGear);
                bool allowSixLockDrive = isSixLock && (sixLockWheelSet.Count == 0 || sixLockWheelSet.Contains(wheel));
                bool isMotorized = isSixLock ? allowSixLockDrive : wheel.motorized;
                
                // ★★★ 引擎熄火时完全不输出动力 ★★★
                if (isMotorized && engineOn)
                {
                    wheel.WheelCollider.motorTorque = appliedThrottleInput * currentMotorTorque;
                }
                else
                {
                    wheel.WheelCollider.motorTorque = 0f;
                }
            }
        }
        
        if (steeringWheel != null)
        {
            float avgWheelSteerAngle = steerCount > 0 ? (sumSteerAngles / steerCount) : 0f;
            float denom = outerSteerAngle != 0f ? outerSteerAngle : 1f;
            float steeringNormalized = Mathf.Clamp(avgWheelSteerAngle / denom, -1f, 1f);
            float dir = invertSteeringWheel ? -1f : 1f;
            float targetAngle = steeringNormalized * steeringWheelMaxTurn * dir;
            steeringWheel.localRotation = steeringWheelInitialLocalRotation * Quaternion.AngleAxis(targetAngle, steeringWheelLocalAxis);
        }

        // Reference RVcode uses the legacy Vertical axis for fuel load.
        throttleInput = activeControl ? Mathf.Clamp01(Input.GetAxis("Vertical")) : 0f;

        // ★★★ 燃油消耗管理（会自动熄火）★★★
        HandleFuelConsumption(throttleInput);
    }

    private void UpdateSpeedDisplay(float displaySpeed)
    {
        if (speedDisplay == null)
        {
            return;
        }

        int roundedSpeed = Mathf.RoundToInt(displaySpeed);
        if (roundedSpeed == lastDisplayedSpeedKmh)
        {
            return;
        }

        lastDisplayedSpeedKmh = roundedSpeed;
        speedDisplay.text = roundedSpeed.ToString();
    }

    private void UpdateRpmDisplay(float rpm)
    {
        if (rpmDisplay == null)
        {
            return;
        }

        int roundedRpm = Mathf.RoundToInt(rpm);
        if (roundedRpm == lastDisplayedRpm)
        {
            return;
        }

        lastDisplayedRpm = roundedRpm;
        rpmDisplay.text = roundedRpm.ToString();
    }

    private void UpdateGearDisplay(string gearString)
    {
        if (gearDisplay == null || lastDisplayedGear == gearString)
        {
            return;
        }

        lastDisplayedGear = gearString;
        gearDisplay.text = gearString;
    }

    private string GetGearDisplayString()
    {
        switch (currentGear)
        {
            case GearMode.Park:
                return "P";
            case GearMode.Reverse:
                return "R";
            case GearMode.Neutral:
                return "N";
            case GearMode.Drive:
            case GearMode.Sport:
            case GearMode.H6:
            case GearMode.L6:
                int index = Mathf.Clamp(currentTransmissionGear, 0, ForwardGearDisplay.Length - 1);
                return ForwardGearDisplay[index];
            default:
                return string.Empty;
        }
    }

    // ★★★ 燃油系统（新增自动熄火逻辑）★★★
//--燃油系统（完全基于转速重构版）--
private void HandleFuelConsumption(float throttle)
{
    // 1. 安全边界判断：如果油箱已经没油了，确保强制熄火并退出
    if (FuelTank.SharedFuel <= 0f)
    {
        if (engineOn)
        {
            engineOn = false;
            OnEngineStateChanged?.Invoke(false);
            
            if (startProcedure != null && startProcedure.EngineOn)
            {
                startProcedure.ForceShutdownEngine();
            }
        }
        return;
    }
    
    // 2. 状态拦截：如果引擎没发动、没通电，或者当前平滑转速接近0，立刻退出不扣油
    // 这一步直接杀死了“下车按W扣油”的Bug，因为下车/熄火时转速为0
    if (!engineOn || !electricalPowerOn || smoothEngineRpm <= 10f) return;

    // 3. 计算燃油消耗率 (基于当前真实的转速 RPM)
    // 设定：当引擎达到最大转速 2800 RPM 且踩满油门时，达到最大消耗率
    float maxEngineRpm = 2800f;
    float maxCapacity = 100f;
    
    // 满载最大每秒消耗率 = 50% / 60秒 = 0.8333f (即 2800转且满油门时的消耗)
    float maxRatePerSecond = (maxCapacity * 0.5f) / 60f; 

    // 计算当前的转速比例 (0f 到 1f)
    float rpmRatio = Mathf.Clamp01(smoothEngineRpm / maxEngineRpm);

    float currentRate = 0f;

    if (throttle > 0.05f)
    {
        // 【有油门状态】：消耗由 “当前转速” 和 “油门深度” 共同决定（高转速大油门狂扣油）
        currentRate = maxRatePerSecond * rpmRatio * throttle;
    }
    else
    {
        // 【无油门/怠速/滑行状态】：玩家没踩油门，消耗纯粹由 “当前转速” 决定
        // 挂空挡轰油门放开后，转速回落的过程中，油会随着转速变低而越扣越少，直到降回怠速线
        currentRate = maxRatePerSecond * rpmRatio * idleConsumptionFactor;
    }

    // 4. 应用 Inspector 里的全局速度控制系数与帧时间
    float finalConsumption = currentRate * fuelConsumptionMultiplier * Time.deltaTime;
    
    // 5. 扣除共享燃油，做防负数保护
    if (finalConsumption >= FuelTank.SharedFuel)
    {
        FuelTank.SharedFuel = 0f;
        engineOn = false;
        OnEngineStateChanged?.Invoke(false);
        
        if (startProcedure != null && startProcedure.EngineOn)
        {
            startProcedure.ForceShutdownEngine();
        }
    }
    else
    {
        FuelTank.SharedFuel -= finalConsumption;
    }
}
    
    // ★★★ 动能回收系统 ★★★
    private void HandleRegenerativeBraking(float brakeInput, bool isHandBraking)
    {
        // 条件检查：引擎必须运行（或者有电气系统），车速足够，不在空档或倒车？可以根据需要调整
        if (!electricalPowerOn || isHandBraking || currentSpeedKmh < regenMinSpeed)
        {
            return;
        }
        
        // 检查是否在刹车
// 检查是否在刹车
bool isBraking = brakeInput > regenBrakeThreshold;
if (!isBraking)
{
    return;
}
        
        // 计算动能回收量
        // 基于减速度计算回收能量
        Vector3 currentVelocity = rigidBody.linearVelocity;
        float forwardVelocity = Vector3.Dot(transform.forward, currentVelocity);
        
        // 简单的物理计算：动能变化 = 0.5 * m * (v1^2 - v2^2)
        // 我们使用上一帧速度来估算动能损失
        Vector3 lastVel = (lastFramePosition - transform.position) / Time.deltaTime;
        float lastForwardVel = Vector3.Dot(transform.forward, lastVel);
        
        float deltaVelocity = Mathf.Max(0, lastForwardVel - forwardVelocity);
        
        if (deltaVelocity > 0.1f)
        {
            float mass = rigidBody.mass;
            float kineticEnergyLost = 0.5f * mass * (lastForwardVel * lastForwardVel - forwardVelocity * forwardVelocity);
            
            // 转换能量为燃油当量（假设1单位燃油 = 1000焦耳，可根据需要调整）
            float energyToFuel = kineticEnergyLost / 1000f;
            
            // 应用效率和最大功率限制
            float regenAmount = energyToFuel * regenEfficiency;
            regenAmount = Mathf.Min(regenAmount, maxRegenPower * Time.deltaTime);
            
            // 回收燃油到油箱
            if (regenAmount > 0)
            {
                AddFuel(regenAmount);
                
                // 可选：添加UI提示（如显示"再生制动 +X"）
                // Debug.Log($"Regen: +{regenAmount:F3} fuel");
            }
        }
    }
    
    // ★★★ 公共接口：添加燃油 ★★★
    public void AddFuel(float amount)
    {
        if (amount <= 0) return;
        
        float oldFuel = FuelTank.SharedFuel;
        FuelTank.SharedFuel = Mathf.Min(100f, FuelTank.SharedFuel + amount);
        
        float added = FuelTank.SharedFuel - oldFuel;
        if (added > 0)
        {
            Debug.Log($"Added {added:F2} fuel. Total: {FuelTank.SharedFuel:F2}/100");
            
            // 如果有加油动画或UI事件可以在这里触发
            // OnFuelAdded?.Invoke(added);
                    if (oldFuel <= 0f && FuelTank.SharedFuel > 0f && startProcedure != null)
        {
            startProcedure.TryAutoRestartEngine();
        }
        }
    }
    
    // ★★★ 获取当前油量（方便外部显示）★★★
    public float GetCurrentFuel()
    {
        return FuelTank.SharedFuel;
    }

    // ========== 引擎声音生成 ==========
    private float GetDeterministicNoise()
    {
        noiseSeed ^= noiseSeed << 13;
        noiseSeed ^= noiseSeed >> 17;
        noiseSeed ^= noiseSeed << 5;
        return (noiseSeed / (float)uint.MaxValue) * 2f - 1f;
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        float rpm = Mathf.Max(0, smoothEngineRpm);
        float vol = engineMasterVolume;
        if (data == null || data.Length == 0 || channels <= 0)
        {
            return;
        }

        if (vol <= 0.0001f || (rpm <= 1f && engineLoad <= 0.0001f))
        {
            System.Array.Clear(data, 0, data.Length);
            return;
        }

        float rpmRatio = Mathf.Clamp01(rpm / 2800f);
        
        double freqIncrement = (rpm / 60.0) * 4.0 / samplingRate;
        double exhaustIncrement = (rpm / 60.0) * 2.0 / samplingRate;
        double intakeIncrement = (rpm / 60.0) * 8.0 / samplingRate;
        double turboIncrement = (rpm / 60.0) * 24.0 / samplingRate;
        
        float pWidth = pulseWidth;
        float sharpness = pulseSharpness;
        float exhaustRes = exhaustResonance;
        float exhaustDroneVal = exhaustDrone;
        float intake = intakeSound;
        float turbo = turboWhine;
        float mechanical = mechanicalNoise;
        float imbalance = cylinderImbalance;
        
        float idleBias = Mathf.Clamp01(1f - rpmRatio * 2f);
        float highRpmBias = rpmRatio * rpmRatio;
        
        double localPhase = phase;
        double localExhaustPhase = exhaustPhase;
        double localIntakePhase = intakePhase;
        double localTurboPhase = turboPhase;
        
        for (int i = 0; i < data.Length; i += channels)
        {
            localPhase += freqIncrement;
            if (localPhase > 1.0) localPhase -= 1.0;
            localExhaustPhase += exhaustIncrement;
            if (localExhaustPhase > 1.0) localExhaustPhase -= 1.0;
            localIntakePhase += intakeIncrement;
            if (localIntakePhase > 1.0) localIntakePhase -= 1.0;
            localTurboPhase += turboIncrement;
            if (localTurboPhase > 1.0) localTurboPhase -= 1.0;
            
            float signal = 0f;
            float phaseAngle = (float)(localPhase * Mathf.PI * 2f);
            
            float pulse = 0f;
            if (localPhase < pWidth)
            {
                float t = (float)(localPhase / pWidth);
                float curve = Mathf.Lerp(1f - t, Mathf.Exp(-sharpness * 5f * t), sharpness);
                pulse = curve * (1f + Mathf.Sin(phaseAngle * 0.5f) * 0.3f);
            }
            
            float cylinderVar = 1f + Mathf.Sin((float)(localPhase * Mathf.PI * 32f)) * imbalance * 0.5f;
            pulse *= cylinderVar;
            signal += pulse * 0.7f;
            
            float rumble = Mathf.Sin(phaseAngle) * 0.25f;
            rumble += Mathf.Sin(phaseAngle * 2f) * 0.12f * rpmRatio;
            rumble += Mathf.Sin(phaseAngle * 3f) * 0.06f * highRpmBias;
            signal += rumble;
            
            float exhaustAngle = (float)(localExhaustPhase * Mathf.PI * 2f);
            float exhaust = 0f;
            exhaust += Mathf.Sin(exhaustAngle * 2f) * 0.4f * exhaustRes;
            exhaust += Mathf.Sin((float)(localPhase * Mathf.PI * 0.8f)) * 0.3f * exhaustDroneVal * idleBias;
            exhaust += Mathf.Exp(-Mathf.Abs(Mathf.Sin(exhaustAngle))) * 0.2f;
            signal += exhaust * 0.3f;
            
            float intakeAngle = (float)(localIntakePhase * Mathf.PI * 2f);
            float intakeSig = 0f;
            float intakeBias = Mathf.Sin(rpmRatio * Mathf.PI) * 0.8f;
            intakeSig += Mathf.Sin(intakeAngle) * 0.4f * intakeBias;
            intakeSig += Mathf.Sin(intakeAngle * 3f) * 0.15f * highRpmBias;
            signal += intakeSig * intake;
            
            float turboAngle = (float)(localTurboPhase * Mathf.PI * 2f);
            float turbosound = 0f;
            if (rpmRatio > 0.4f)
            {
                float turboStrength = Mathf.Clamp01((rpmRatio - 0.4f) / 0.6f);
                turbosound += Mathf.Sin(turboAngle) * 0.25f * turboStrength;
                turbosound += Mathf.Sin(turboAngle * 2.3f) * 0.12f * turboStrength;
                if (highRpmBias > 0.6f)
                {
                    turbosound += Mathf.Sin(turboAngle * 4.7f) * 0.08f;
                }
            }
            signal += turbosound * turbo;
            
            float mechNoise = GetDeterministicNoise() * mechanical;
            mechNoise *= (0.5f + rpmRatio * 0.5f);
            signal += mechNoise;
            
            float loadPulse = Mathf.Sin(phaseAngle * 4f) * engineLoad * 0.2f;
            signal += loadPulse;
            
            float volumeEnvelope = Mathf.Lerp(0.65f, 1.0f, rpmRatio);
            volumeEnvelope += engineLoad * 0.2f;
            volumeEnvelope = Mathf.Clamp01(volumeEnvelope);
            
            float filteredSignal = signal;
            if (rpmRatio < 0.3f)
            {
                filteredSignal = signal * 0.7f + mechNoise * 0.3f;
            }
            
            float finalSample = filteredSignal * vol * volumeEnvelope;
            finalSample = Mathf.Clamp(finalSample, -0.95f, 0.95f);
            
            for (int ch = 0; ch < channels; ch++)
            {
                data[i + ch] = finalSample;
            }
        }
        
        phase = localPhase;
        exhaustPhase = localExhaustPhase;
        intakePhase = localIntakePhase;
        turboPhase = localTurboPhase;
    }
}
