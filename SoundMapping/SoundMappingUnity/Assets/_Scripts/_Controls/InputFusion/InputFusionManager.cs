using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Central hub that fuses all input sources (traditional, IMU, VR, etc.)
/// and provides a unified API for swarm control.
/// This is the ONLY class that MigrationPointController and CameraMovement should read from.
/// </summary>
public class InputFusionManager : MonoBehaviour
{
    // ============================================
    // INPUT SOURCE REFERENCES
    // ============================================
    [Header("Input Sources")]
    [Tooltip("Keyboard and game controller input")]
    public TraditionalInput traditionalInput;

    [Tooltip("Chest IMU rate-based movement (forward/back tilt = forward speed; left/right tilt = strafe)")]
    public IMUMovementInput imuMovementInput;

    [Tooltip("Meta Quest headset — rate-based camera yaw")]
    [FormerlySerializedAs("metaQuestInput")]
    public HeadsetYawInput headsetYawInput;

    [Tooltip("IMU yaw input for camera rotation from OpenZen sensor")]
    public IMUYawInput imuYawInput;

    [Tooltip("Pose-tracking spread input from Python webcam tracking (MediaPipe or RTMPose)")]
    [FormerlySerializedAs("mediaPipeSpreadInput")]
    public PoseSpreadInput poseSpreadInput;

    [Tooltip("Pose-tracking height input from Python webcam tracking (MediaPipe or RTMPose)")]
    [FormerlySerializedAs("mediaPipeHeightInput")]
    public PoseHeightInput poseHeightInput;

    [Tooltip("Pose-tracking head-yaw input — non-VR camera rotation fallback (RTMPose Wholebody only)")]
    public PoseYawInput poseYawInput;

    [Tooltip("Arm IMU input — forearm sensors for spread and height (primary wearable source)")]
    public ArmIMUSpreadHeightInput armIMUInput;

    [Tooltip("Optional Hand-IMU fusion sitting in front of armIMUInput — leave empty to use raw IMU")]
    [FormerlySerializedAs("driftCorrector")]
    public HandIMUFusion handIMUFusion;

    [Tooltip("Meta Quest hand tracking — rate-based height")]
    public HandHeightInput handHeightInput;

    [Tooltip("Meta Quest hand tracking — rate-based spread")]
    public HandSpreadInput handSpreadInput;

    [Tooltip("Meta Quest Touch controllers — rate-based height")]
    public ControllerHeightInput controllerHeightInput;

    [Tooltip("Meta Quest Touch controllers — rate-based spread")]
    public ControllerSpreadInput controllerSpreadInput;

    [Tooltip("6-step Meta Quest calibration flow (V key by default). Auto-found if left empty.")]
    public MetaQuestCalibrationFlow metaQuestCalibration;

    // ============================================
    // INPUT PRIORITY TOGGLES
    // ============================================
    [Header("Input Priority Settings")]
    [Tooltip("Use IMU for swarm movement (if false, uses traditional input)")]
    public bool useIMUForMovement = false; // Start with false until IMU is connected

    [Tooltip("Use Meta Quest headset yaw for camera rotation (if false, uses traditional input)")]
    public bool useMetaQuestForRotation = false; // Will enable when MetaQuest is added

    [Tooltip("Use IMU yaw for camera rotation (if false, falls back to MetaQuest or traditional)")]
    public bool useIMUForRotation = false;

    [Tooltip("Use webcam pose tracking for spread control (if false, uses traditional input)")]
    [FormerlySerializedAs("useMediaPipeForSpread")]
    public bool usePoseForSpread = false;

    [Tooltip("Use webcam pose tracking for height control (if false, uses traditional input)")]
    [FormerlySerializedAs("useMediaPipeForHeight")]
    public bool usePoseForHeight = false;

    [Tooltip("Use webcam pose tracking for camera rotation (non-VR fallback for participants without a headset)")]
    public bool usePoseForRotation = false;

    [Tooltip("Use forearm IMUs for spread and height (takes priority over pose tracking when enabled)")]
    public bool useArmIMUForSpreadHeight = false;

    [Tooltip("Use Meta Quest hand tracking for height (priority below ArmIMU)")]
    public bool useMetaHandsForHeight = false;

    [Tooltip("Use Meta Quest hand tracking for spread (priority below ArmIMU)")]
    public bool useMetaHandsForSpread = false;

    [Tooltip("Use Meta Quest Touch controllers for height (priority below MetaHands)")]
    public bool useControllersForHeight = false;

    [Tooltip("Use Meta Quest Touch controllers for spread (priority below MetaHands)")]
    public bool useControllersForSpread = false;

    [Tooltip("Always allow traditional input as fallback when primary sources aren't moving")]
    public bool enableTraditionalFallback = true;

    // ============================================
    // PER-AXIS MAX SPEEDS (absolute, applied to rate-based inputs)
    // Each value is the migration-vector / rate magnitude produced when the
    // corresponding input axis is at full ±1. Downstream MigrationPointController
    // multiplies by `radius` (default 1) and swarmModel clamps drone velocity at
    // `maxSpeed`. Keep `radius = 1` for these values to read literally.
    // ============================================
    [Header("Per-Axis Max Speeds (at full ±1 input)")]
    [Tooltip("Forward (positive Z) max migration speed at full input")]
    [Range(0f, 5f)]
    public float forwardMaxSpeed = 1f;

    [Tooltip("Backward (negative Z) max migration speed at full input")]
    [Range(0f, 5f)]
    public float backwardMaxSpeed = 1f;

    [Tooltip("Lateral (left/right, X axis) max migration speed at full input")]
    [Range(0f, 5f)]
    public float lateralMaxSpeed = 1f;

    [Tooltip("Height (Y axis, up and down) max migration speed at full input")]
    [Range(0f, 5f)]
    public float heightMaxSpeed = 1f;

    [Tooltip("Camera rotation max yaw rate at full input (compounded with CameraMovement.rotationSpeed)")]
    [Range(0f, 5f)]
    public float cameraRotationMaxSpeed = 1f;

    [Tooltip("Spread max rate at full input — m/s of separation change (rate-based modes only)")]
    [Range(0f, 5f)]
    public float spreadMaxSpeed = 1f;

    // ============================================
    // SWARM SPEED + SEPARATION BOUNDS (forwarded into MigrationPointController / swarmModel)
    // ============================================
    [Header("Swarm Speed & Spread Bounds")]
    [Tooltip("Hard cap on drone velocity (m/s). Mirrors swarmModel.maxSpeed — set at runtime in Start().")]
    [Range(0.5f, 20f)]
    public float swarmMaxSpeed = 5f;

    [Tooltip("Reference to MigrationPointController — auto-found if left empty. Used to push spread bounds.")]
    public MigrationPointController migrationPointController;

    [Tooltip("Reference to swarmModel — auto-found if left empty. Used to push swarmMaxSpeed.")]
    public swarmModel swarmModelRef;

    [Tooltip("Minimum allowed separation distance (m) — clamps lower bound on spread")]
    [Range(0.1f, 5f)]
    public float minSeparationDistance = 1f;

    [Tooltip("Maximum allowed separation distance (m) — clamps upper bound on spread")]
    [Range(1f, 20f)]
    public float maxSeparationDistance = 5f;

    [Header("Run Start")]
    [Tooltip("Keyboard key that releases the frozen swarm. Also handled here as a backup in case swarmModel misses the key.")]
    public KeyCode startRunKey = KeyCode.P;

    [Tooltip("When true, pressing the start key also starts the run timer/trajectory window. The start trigger still works and duplicate starts are ignored.")]
    public bool startTimerOnStartKey = true;

    [Header("Calibration Profiles")]
    [Tooltip("Filename used when saving after the V-key calibration flow. .json is added automatically.")]
    public string calibrationProfileName = "default";

    [Tooltip("Filename used when loading. Leave empty to load Calibration Profile Name.")]
    [CalibrationProfileDropdown]
    public string calibrationProfileToLoad = "default";

    [Tooltip("Automatically save IMU and MetaQuest calibration to the named profile when the V-key flow completes.")]
    public bool saveCalibrationAfterVFlow = true;

    [Tooltip("Load a calibration profile on Start.")]
    public bool loadCalibrationOnStart = true;

    [Tooltip("When loading on Start, load the newest calibration JSON file instead of Calibration Profile To Load.")]
    public bool loadLatestCalibrationOnStart = true;

    [Tooltip("Press this during Play to save the current calibration profile.")]
    public KeyCode saveCalibrationProfileKey = KeyCode.F8;

    [Tooltip("Press this during Play to load Calibration Profile To Load.")]
    public KeyCode loadCalibrationProfileKey = KeyCode.F9;

    // ============================================
    // CALIBRATION STATE
    // ============================================
    private bool isCalibrating = false;
    private float calibrationCountdown = 0f;

    // ============================================
    // FUSED OUTPUT PROPERTIES (Read by other systems)
    // ============================================
    // Fused Outputs (Read-Only) - these are computed from all input sources
    public Vector3 SwarmMovement { get; private set; }      // Final movement vector for swarm (XZ plane + Y height)
    public float SwarmSpread { get; private set; }          // Final spread control value (rate or target depending on mode)
    public float CameraRotation { get; private set; }       // Final camera rotation input
    public string CameraRotationSource { get; private set; } = "None";
    public string CameraRotationBlockReason { get; private set; } = "";
    
    /// <summary>
    /// Returns true if SwarmSpread is an absolute target (not a rate)
    /// MigrationPointController needs to know this to apply correctly
    /// </summary>
    public bool IsSpreadAbsolute
    {
        get
        {
            if (useArmIMUForSpreadHeight && ArmIMUAvailable())
                return armIMUInput.IsAbsoluteMode;
            if (useMetaHandsForSpread && MetaHandSpreadAvailable())
                return handSpreadInput.IsAbsoluteMode;
            if (useControllersForSpread && controllerSpreadInput != null && controllerSpreadInput.IsAvailable)
                return controllerSpreadInput.IsAbsoluteMode;
            if (usePoseForSpread && poseSpreadInput != null && poseSpreadInput.IsAvailable)
                return poseSpreadInput.IsAbsoluteMode;
            return traditionalInput != null && traditionalInput.IsSpreadAbsolute;
        }
    }

    // Button states (pass-through from traditional for now)
    public bool SelectionNextPressed { get; private set; }
    public bool SelectionPrevPressed { get; private set; }
    public bool EmbodimentPressed { get; private set; }
    public bool DisembodimentPressed { get; private set; }
    public bool ToggleDummyForcesPressed { get; private set; }
    public bool CalibratePressed { get; private set; }

    // ============================================
    // INITIALIZATION
    // ============================================
    void Start()
    {
        ValidateReferences();
        ResolveSwarmReferences();
        ApplyCalibrationProfileSettings();
        if (loadCalibrationOnStart && metaQuestCalibration != null)
        {
            if (loadLatestCalibrationOnStart)
            {
                metaQuestCalibration.LoadLatestCalibrationProfile();
                calibrationProfileToLoad = metaQuestCalibration.calibrationProfileToLoad;
            }
            else
            {
                metaQuestCalibration.LoadCalibrationProfile();
            }
        }
        ApplySwarmBounds();
    }

    void ResolveSwarmReferences()
    {
        if (migrationPointController == null) migrationPointController = FindObjectOfType<MigrationPointController>();
        if (swarmModelRef == null) swarmModelRef = FindObjectOfType<swarmModel>();
        if (metaQuestCalibration == null) metaQuestCalibration = FindObjectOfType<MetaQuestCalibrationFlow>();
    }

    /// <summary>
    /// True when any calibration is in progress: either the legacy 2-second neutral-capture
    /// or the V-key Meta Quest 6-step flow. Used to hold the swarm steady mid-capture.
    /// </summary>
    private bool IsAnyCalibrationActive =>
        isCalibrating || (metaQuestCalibration != null && metaQuestCalibration.IsRunning);

    /// <summary>
    /// Pushes swarmMaxSpeed and min/max separation bounds into the actual swarm components.
    /// Called every frame so Inspector tweaks take effect live.
    /// </summary>
    void ApplySwarmBounds()
    {
        if (swarmModelRef != null) swarmModelRef.maxSpeed = swarmMaxSpeed;
        if (migrationPointController != null)
        {
            migrationPointController.minSpreadnessRuntime = minSeparationDistance;
            migrationPointController.maxSpreadnessRuntime = maxSeparationDistance;
        }

        ApplyMetaQuestSpreadTargetRange();
    }

    void ApplyMetaQuestSpreadTargetRange()
    {
        if (handSpreadInput != null)
        {
            handSpreadInput.minSwarmSeparation = minSeparationDistance;
            handSpreadInput.maxSwarmSeparation = maxSeparationDistance;
        }

        if (controllerSpreadInput != null)
        {
            controllerSpreadInput.minSwarmSeparation = minSeparationDistance;
            controllerSpreadInput.maxSwarmSeparation = maxSeparationDistance;
        }
    }

    void ApplyCalibrationProfileSettings()
    {
        if (metaQuestCalibration == null) return;

        metaQuestCalibration.calibrationProfileName = calibrationProfileName;
        metaQuestCalibration.calibrationProfileToLoad = calibrationProfileToLoad;
        metaQuestCalibration.saveProfileWhenFlowCompletes = saveCalibrationAfterVFlow;
        metaQuestCalibration.loadProfileOnStart = loadCalibrationOnStart;
        metaQuestCalibration.loadLatestProfileOnStart = loadLatestCalibrationOnStart;
    }

    void HandleStartRunKey()
    {
        if (!Input.GetKeyDown(startRunKey)) return;

        bool released = swarmModel.ReleaseSwarm("InputFusionManager start key");

        if (startTimerOnStartKey)
        {
            SwarmTrajectoryRecorder.MarkTrialStart("Run");
            Timer timer = swarmModelRef != null ? swarmModelRef.GetComponent<Timer>() : FindObjectOfType<Timer>();
            if (timer != null) timer.StartTimer();
            else Debug.LogWarning("InputFusionManager: Start key pressed, but no Timer was found.");
        }

        if (!released)
            Debug.Log("InputFusionManager: Start key pressed; swarm was already active.");
    }

    void HandleCalibrationProfileHotkeys()
    {
        if (metaQuestCalibration == null) return;

        if (Input.GetKeyDown(saveCalibrationProfileKey))
            SaveCalibrationProfile();

        if (Input.GetKeyDown(loadCalibrationProfileKey))
            LoadSelectedCalibrationProfile();
    }

    [ContextMenu("Save Calibration Profile")]
    public void SaveCalibrationProfile()
    {
        ResolveSwarmReferences();
        ApplyCalibrationProfileSettings();
        if (metaQuestCalibration != null)
            metaQuestCalibration.SaveCalibrationProfile();
    }

    [ContextMenu("Load Selected Calibration Profile")]
    public void LoadSelectedCalibrationProfile()
    {
        ResolveSwarmReferences();
        ApplyCalibrationProfileSettings();
        if (metaQuestCalibration != null)
            metaQuestCalibration.LoadCalibrationProfile();
    }

    [ContextMenu("Load Latest Calibration Profile")]
    public void LoadLatestCalibrationProfile()
    {
        ResolveSwarmReferences();
        ApplyCalibrationProfileSettings();
        if (metaQuestCalibration != null)
            metaQuestCalibration.LoadLatestCalibrationProfile();
    }

    void ValidateReferences()
    {
        if (traditionalInput == null)
        {
            Debug.LogError("InputFusionManager: TraditionalInput reference is missing! Assign it in the Inspector.");
        }

        if (imuMovementInput == null && useIMUForMovement)
        {
            Debug.LogWarning("InputFusionManager: IMUMovementInput is enabled but reference is missing. Falling back to traditional input.");
            useIMUForMovement = false;
        }

        if (headsetYawInput == null && useMetaQuestForRotation)
        {
            Debug.LogWarning("InputFusionManager: HeadsetYawInput is enabled but reference is missing. Falling back to traditional input.");
            useMetaQuestForRotation = false;
        }

        if (poseSpreadInput == null && usePoseForSpread)
        {
            Debug.LogWarning("InputFusionManager: PoseSpreadInput is enabled but reference is missing. Falling back to traditional input.");
            usePoseForSpread = false;
        }

        if (poseYawInput == null && usePoseForRotation)
        {
            Debug.LogWarning("InputFusionManager: PoseYawInput is enabled but reference is missing. Falling back to MetaQuest/traditional rotation.");
            usePoseForRotation = false;
        }

        if (poseHeightInput == null && usePoseForHeight)
        {
            Debug.LogWarning("InputFusionManager: PoseHeightInput is enabled but reference is missing. Falling back to traditional input.");
            usePoseForHeight = false;
        }

        if (armIMUInput == null && useArmIMUForSpreadHeight)
        {
            Debug.LogWarning("InputFusionManager: ArmIMUSpreadHeightInput is enabled but reference is missing. Falling back to pose tracking/traditional.");
            useArmIMUForSpreadHeight = false;
        }

        if (handHeightInput == null && useMetaHandsForHeight)
        {
            Debug.LogWarning("InputFusionManager: HandHeightInput is enabled but reference is missing. Disabling MetaHands height.");
            useMetaHandsForHeight = false;
        }

        if (handSpreadInput == null && useMetaHandsForSpread)
        {
            Debug.LogWarning("InputFusionManager: HandSpreadInput is enabled but reference is missing. Disabling MetaHands spread.");
            useMetaHandsForSpread = false;
        }

        if (controllerHeightInput == null && useControllersForHeight)
        {
            Debug.LogWarning("InputFusionManager: ControllerHeightInput is enabled but reference is missing. Disabling controller height.");
            useControllersForHeight = false;
        }

        if (controllerSpreadInput == null && useControllersForSpread)
        {
            Debug.LogWarning("InputFusionManager: ControllerSpreadInput is enabled but reference is missing. Disabling controller spread.");
            useControllersForSpread = false;
        }
    }

    // ============================================
    // UPDATE LOOP - FUSION LOGIC
    // ============================================
    void Update()
    {
        ApplySwarmBounds();
        HandleStartRunKey();
        HandleCalibrationProfileHotkeys();
        FuseMovementInputs();
        FuseHeightInputs();
        FuseSpreadInputs();
        FuseRotationInputs();
        FuseButtonInputs();

        // Check for calibration button press - calibrate immediately
        // Start 3-second countdown on button press (only if not already calibrating)
        if (CalibratePressed && !isCalibrating)
        {
            isCalibrating = true;
            calibrationCountdown = 2f;
            Debug.Log("Calibration in 2 seconds — hold steady!");
        }

        // Tick countdown and fire when it reaches zero
        if (isCalibrating)
        {
            calibrationCountdown -= Time.deltaTime;
            if (calibrationCountdown <= 0f)
            {
                PerformCalibration();
                isCalibrating = false;
                calibrationCountdown = 0f;
            }
        }
    }

    /// <summary>
    /// Combines horizontal movement (XZ plane) from IMU and/or traditional input
    /// Height (Y) is handled separately in FuseHeightInputs()
    /// </summary>
    void FuseMovementInputs()
    {
        Vector3 movement = Vector3.zero;

        // STABILIZE during calibration - provide minimal forward velocity to maintain swarm cohesion
        if (IsAnyCalibrationActive)
        {
            // Small forward velocity prevents alignmentVector from becoming zero (which causes disconnection)
            SwarmMovement = new Vector3(0f, 0f, 0.05f); // Gentle forward drift maintains formation
            return;
        }

        // PRIMARY: Use IMU if enabled and available
        if (useIMUForMovement && imuMovementInput != null && imuMovementInput.IsAvailable)
        {
            movement = imuMovementInput.MovementVector; // Already has Y=0
        }
        // FALLBACK: Use traditional input (horizontal only)
        if (movement == Vector3.zero && traditionalInput != null)
        {
            Vector2 moveInput = traditionalInput.MovementInput;
            movement = new Vector3(moveInput.x, 0f, moveInput.y); // Y is handled in FuseHeightInputs
        }

        movement.x *= lateralMaxSpeed;
        movement.z *= movement.z >= 0f ? forwardMaxSpeed : backwardMaxSpeed;

        SwarmMovement = movement;
    }

    /// <summary>
    /// Combines height control from pose tracking and/or traditional input
    /// Updates the Y component of SwarmMovement
    /// </summary>
    void FuseHeightInputs()
    {
        float height = 0f;

        // PRIMARY: Arm IMU forearm sensors
        if (useArmIMUForSpreadHeight && ArmIMUAvailable())
        {
            height = handIMUFusion != null && handIMUFusion.IsAvailable
                ? handIMUFusion.HeightControl
                : armIMUInput.HeightControl;
        }
        // SECONDARY: Meta Quest hand tracking
        else if (useMetaHandsForHeight && MetaHandHeightAvailable())
        {
            height = handHeightInput.HeightControl;
        }
        // TERTIARY: Meta Quest Touch controllers
        else if (useControllersForHeight && controllerHeightInput != null && controllerHeightInput.IsAvailable)
        {
            height = controllerHeightInput.HeightControl;
        }
        // QUATERNARY: webcam pose tracking (MediaPipe or RTMPose)
        else if (usePoseForHeight && poseHeightInput != null && poseHeightInput.IsAvailable)
        {
            height = poseHeightInput.HeightControl;
        }
        // FALLBACK: traditional input (Taranis)
        else if (traditionalInput != null)
        {
            height = traditionalInput.HeightInput;
        }

        height *= heightMaxSpeed;

        SwarmMovement = new Vector3(SwarmMovement.x, height, SwarmMovement.z);
    }

    /// <summary>
    /// Combines spread control from pose tracking and/or traditional input
    /// Note: Output meaning depends on mode:
    /// - Traditional: SwarmSpread is a rate (-1 to +1)
    /// - Pose tracking: SwarmSpread is target separation distance (meters)
    /// </summary>
    void FuseSpreadInputs()
    {
        float spread = 0f;

        // FREEZE SPREAD during calibration - no spreading/shrinking
        if (IsAnyCalibrationActive)
        {
            SwarmSpread = 0f; // Zero spread change during calibration
            return;
        }

        // PRIMARY: Arm IMU forearm sensors
        if (useArmIMUForSpreadHeight && ArmIMUAvailable())
        {
            spread = handIMUFusion != null && handIMUFusion.IsAvailable
                ? handIMUFusion.SpreadControl
                : armIMUInput.SpreadControl;
        }
        // SECONDARY: Meta Quest hand tracking
        else if (useMetaHandsForSpread && MetaHandSpreadAvailable())
        {
            spread = handSpreadInput.SpreadControl;
        }
        // TERTIARY: Meta Quest Touch controllers
        else if (useControllersForSpread && controllerSpreadInput != null && controllerSpreadInput.IsAvailable)
        {
            spread = controllerSpreadInput.SpreadControl;
        }
        // QUATERNARY: webcam pose tracking (MediaPipe or RTMPose)
        else if (usePoseForSpread && poseSpreadInput != null && poseSpreadInput.IsAvailable)
        {
            spread = poseSpreadInput.SpreadControl;
        }
        // FALLBACK: traditional input (Taranis, rate-based)
        else if (traditionalInput != null)
        {
            spread = traditionalInput.SpreadInput;
        }

        // Per-axis max speed only meaningful for rate-based spread; absolute (target distance)
        // sources should pass through unchanged.
        if (!IsSpreadAbsolute) spread *= spreadMaxSpeed;

        SwarmSpread = spread;
    }

    /// <summary>
    /// Combines rotation from IMU, Meta Quest headset, and/or traditional input
    /// Priority: IMU > MetaQuest > Traditional
    /// Rotation source is selected only by the inspector toggles below.
    /// </summary>
    void FuseRotationInputs()
    {
        float rotation = 0f;
        CameraRotationSource = "None";
        CameraRotationBlockReason = "";

        // STOP rotation during calibration (but allow swarm to hover)
        if (IsAnyCalibrationActive)
        {
            CameraRotation = 0f;
            CameraRotationBlockReason = "Calibration active";
            return;
        }

        // PRIORITY 1: IMU yaw if enabled and available
        if (useIMUForRotation && imuYawInput != null && imuYawInput.IsAvailable)
        {
            rotation = imuYawInput.YawRotationRate;
            CameraRotationSource = "IMU yaw";
        }
        // PRIORITY 2: Meta Quest headset yaw if enabled and available
        else if (useMetaQuestForRotation && headsetYawInput != null)
        {
            rotation = headsetYawInput.RotationControl;
            CameraRotationSource = headsetYawInput.IsAvailable ? "Meta Quest headset yaw" : "Meta Quest headset yaw unavailable";
            if (!headsetYawInput.IsAvailable)
                CameraRotationBlockReason = "HeadsetYawInput has no OVRCameraRig/centerEyeAnchor";
        }
        // PRIORITY 3: Webcam pose yaw — for non-VR participants who can't wear the headset
        else if (usePoseForRotation && poseYawInput != null && poseYawInput.IsAvailable)
        {
            rotation = poseYawInput.YawRate;
            CameraRotationSource = "Pose yaw";
        }
        // FALLBACK: Use traditional input (right stick / controller)
        else if (traditionalInput != null)
        {
            rotation = traditionalInput.RotationInput;
            CameraRotationSource = "Traditional";
        }

        rotation *= cameraRotationMaxSpeed;

        CameraRotation = rotation;
    }

    /// <summary>
    /// Pass through button states from traditional input
    /// (Buttons don't need fusion, they come from controller only)
    /// </summary>
    void FuseButtonInputs()
    {
        if (traditionalInput != null)
        {
            SelectionNextPressed = traditionalInput.SelectionNextPressed;
            SelectionPrevPressed = traditionalInput.SelectionPrevPressed;
            EmbodimentPressed = traditionalInput.EmbodimentPressed;
            DisembodimentPressed = traditionalInput.DisembodimentPressed;
            ToggleDummyForcesPressed = traditionalInput.ToggleDummyForcesPressed;
            CalibratePressed = traditionalInput.CalibratePressed;
        }
    }

    // ============================================
    // HELPER METHODS
    // ============================================

    bool ArmIMUAvailable() => armIMUInput != null && armIMUInput.IsAvailable;

    bool MetaHandHeightAvailable() => handHeightInput != null && handHeightInput.IsAvailable;

    bool MetaHandSpreadAvailable() => handSpreadInput != null && handSpreadInput.IsAvailable;

    /// <summary>
    /// Returns true if any movement input is active (from any source)
    /// </summary>
    public bool IsMoving()
    {
        return SwarmMovement.sqrMagnitude > 0.01f;
    }

    /// <summary>
    /// Returns selection direction: +1 for next, -1 for previous, 0 for none
    /// </summary>
    public int GetSelectionDirection()
    {
        if (SelectionNextPressed) return 1;
        if (SelectionPrevPressed) return -1;
        return 0;
    }

    // ============================================
    // CALIBRATION METHODS
    // ============================================

    // Calibration is now instant - no countdown needed

    /// <summary>
    /// Performs the actual calibration of IMU and headset after countdown
    /// </summary>
    void PerformCalibration()
    {
        Debug.Log("=== PERFORMING CALIBRATION ===");

        // Calibrate IMU movement (all modes)
        if (imuMovementInput != null && imuMovementInput.IsAvailable)
        {
            imuMovementInput.CalibrateNeutral();
            Debug.Log("✓ IMU movement calibrated");
        }

        // Calibrate Meta Quest headset yaw
        if (headsetYawInput != null && headsetYawInput.IsAvailable)
        {
            headsetYawInput.CalibrateNeutral();
            Debug.Log("✓ Meta Quest headset yaw calibrated");
        }

        // Calibrate IMU yaw input
        if (imuYawInput != null && imuYawInput.IsAvailable)
        {
            imuYawInput.CalibrateNeutral();
            Debug.Log("✓ IMU yaw calibrated");
        }

        // Calibrate arm IMU spread/height — route through HandIMUFusion when present
        // (it just forwards to armIMU, but keeps the call site uniform with the read path)
        if (handIMUFusion != null && handIMUFusion.IsAvailable)
        {
            handIMUFusion.CalibrateNeutral();
            Debug.Log("✓ Arm IMU spread/height calibrated (via HandIMUFusion)");
        }
        else if (armIMUInput != null && armIMUInput.IsAvailable)
        {
            armIMUInput.CalibrateNeutral();
            Debug.Log("✓ Arm IMU spread/height calibrated");
        }

        // Calibrate Meta Quest hand height/spread
        if (handHeightInput != null && handHeightInput.IsAvailable)
        {
            handHeightInput.CalibrateNeutral();
            Debug.Log("✓ Meta hand height calibrated");
        }
        if (handSpreadInput != null && handSpreadInput.IsAvailable)
        {
            handSpreadInput.CalibrateNeutral();
            Debug.Log("✓ Meta hand spread calibrated");
        }

        // Calibrate Meta Quest Touch controller height/spread
        if (controllerHeightInput != null && controllerHeightInput.IsAvailable)
        {
            controllerHeightInput.CalibrateNeutral();
            Debug.Log("✓ Controller height calibrated");
        }
        if (controllerSpreadInput != null && controllerSpreadInput.IsAvailable)
        {
            controllerSpreadInput.CalibrateNeutral();
            Debug.Log("✓ Controller spread calibrated");
        }

        Debug.Log("=== CALIBRATION COMPLETE ===");
        Debug.Log("Sensors calibrated. Input sources remain in their current state (not auto-activated).");
    }

    // ============================================
    // DEBUG VISUALIZATION
    // ============================================
    [Header("Debug")]
    [Tooltip("Show the top-left input-fusion status overlay during play. Off by default to keep the screen clean during study sessions.")]
    public bool showInputStatusOverlay = false;

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        if (!showInputStatusOverlay) return;

        // Display current input state in top-left corner (for debugging)
        GUILayout.BeginArea(new Rect(10, 10, 400, 300));
        GUILayout.Label($"<b>=== INPUT FUSION STATUS ===</b>");
        GUILayout.Label($"Run Timer: {Timer.elapsedTime:F2}s");
        
        // Show calibration status prominently
        if (isCalibrating)
        {
            GUILayout.Label($"<color=yellow><b>⏱ CALIBRATING in {calibrationCountdown:F1}s - HOLD STEADY!</b></color>");
        }
        
        GUILayout.Label($"Movement: {SwarmMovement}");
        GUILayout.Label($"Spread: {SwarmSpread:F2}  Height: {SwarmMovement.y:F2}");
        GUILayout.Label($"Camera Rotation OUTPUT: {CameraRotation:F2}");
        GUILayout.Label($"Rotation Source: {CameraRotationSource}");
        if (!string.IsNullOrEmpty(CameraRotationBlockReason))
            GUILayout.Label($"<color=orange>Rotation Blocked: {CameraRotationBlockReason}</color>");
        GUILayout.Label($"Rotation Enabled: {LevelConfiguration._control_rotation}");
        if (LevelConfiguration._CollectibleNumber > 0)
        {
            GUILayout.Label($"Stars Collected: {SwarmTrajectoryRecorder.CollectiblesPickedUp}/{LevelConfiguration._CollectibleNumber}");
        }
        GUILayout.Label($"ArmIMU: {(useArmIMUForSpreadHeight ? (ArmIMUAvailable() ? "<color=lime>ON</color>" : "<color=red>MISSING</color>") : "off")}");
        GUILayout.Label($"MetaHands H/S: {(useMetaHandsForHeight ? (MetaHandHeightAvailable() ? "<color=lime>H-ON</color>" : "<color=red>H-MISS</color>") : "h-off")} / {(useMetaHandsForSpread ? (MetaHandSpreadAvailable() ? "<color=lime>S-ON</color>" : "<color=red>S-MISS</color>") : "s-off")}");
        GUILayout.Label($"Controllers H/S: {(useControllersForHeight ? (controllerHeightInput != null && controllerHeightInput.IsAvailable ? "<color=lime>H-ON</color>" : "<color=red>H-MISS</color>") : "h-off")} / {(useControllersForSpread ? (controllerSpreadInput != null && controllerSpreadInput.IsAvailable ? "<color=lime>S-ON</color>" : "<color=red>S-MISS</color>") : "s-off")}");
       // GUILayout.Label($"---");
       // GUILayout.Label($"IMU Movement Active: {useIMUForMovement}");
       // GUILayout.Label($"<color=cyan>MetaQuest Rotation Active: {useMetaQuestForRotation}</color>");
        //GUILayout.Label($"IMU Rotation Active: {useIMUForRotation}");
       // GUILayout.Label($"Pose Spread Active: {usePoseForSpread}");
        //GUILayout.Label($"Pose Height Active: {usePoseForHeight}");
        //GUILayout.Label($"Traditional Fallback: {enableTraditionalFallback}");
        
        // Show pitch active status (left/right movement)
        bool pitchActive = useIMUForMovement &&
                         imuMovementInput != null &&
                         imuMovementInput.IsPitchActive;
        GUILayout.Label($"IMU Pitch Active: {pitchActive}");
        
        if (useMetaQuestForRotation && headsetYawInput != null)
        {
            GUILayout.Label($"Headset Available: {headsetYawInput.IsAvailable}");
            GUILayout.Label($"Headset Yaw Rate: {headsetYawInput.RotationControl:F2}");
            GUILayout.Label($"Headset Rel Yaw: {headsetYawInput.GetRelativeYaw():+0.0;-0.0;0.0} deg");
        }
        GUILayout.EndArea();
    }
}
