using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 6-step calibration capture for Meta Quest spread + height inputs.
/// Triggered by the V key (configurable). Walks the participant through min/max/neutral
/// poses for spread (controllers/hands distance) and height (average hand world Y),
/// firing CaptureMin/Neutral/Max on whichever input source is currently available.
///
/// Design notes:
///   • Controller and hand sources are mutually exclusive at the hardware level
///     (OVRInput won't report both connected). Each step writes to whichever pair
///     is live at the moment of capture.
///   • Steps are advanced by a per-step countdown (default 3 s). Pressing the
///     advance key (Space) skips the remaining countdown.
///   • Pressing the cancel key (Escape) aborts mid-flow and rolls back the values
///     captured so far so the participant doesn't end up with a half-calibrated state.
/// </summary>
public class MetaQuestCalibrationFlow : MonoBehaviour
{
    [Header("Targets")]
    [Tooltip("Optional. Auto-found if left empty. Used as the source of truth for profile names/settings.")]
    public InputFusionManager inputFusionManager;

    [Tooltip("Optional. Auto-found if left empty. Captures chest IMU zero plus movement extrema.")]
    public IMUMovementInput imuMovement;

    [Tooltip("Optional. Auto-found if left empty. Captures headset yaw zero plus left/right extrema.")]
    public HeadsetYawInput headsetYaw;

    [Tooltip("Optional. Auto-found if left empty.")]
    public ControllerSpreadInput controllerSpread;
    [Tooltip("Optional. Auto-found if left empty.")]
    public HandSpreadInput        handSpread;
    [Tooltip("Optional. Auto-found if left empty.")]
    public ControllerHeightInput  controllerHeight;
    [Tooltip("Optional. Auto-found if left empty.")]
    public HandHeightInput        handHeight;

    [Header("Keybinds")]
    [Tooltip("Press to start the 6-step flow.")]
    public KeyCode startKey = KeyCode.V;

    [Tooltip("Press to skip the remaining countdown and capture immediately.")]
    public KeyCode advanceKey = KeyCode.Space;

    [Tooltip("Press to cancel mid-flow and roll back changes.")]
    public KeyCode cancelKey = KeyCode.Escape;

    [Header("Timing")]
    [Tooltip("Seconds the participant has to assume the pose (e.g. raise hands to MIN HEIGHT) before the capture countdown starts. Skippable with the advance key.")]
    [Range(0f, 15f)]
    public float getReadyPerStep = 4f;

    [Tooltip("Seconds to hold the pose steady while the capture countdown elapses. Skippable with the advance key.")]
    [Range(1f, 10f)]
    public float countdownPerStep = 3f;

    [Header("Debug")]
    public bool verboseLogging = true;

    [Header("Headset Prompt")]
    [Tooltip("Show the calibration instructions inside the Meta Quest headset.")]
    public bool showPromptInHeadset = true;

    [Tooltip("Optional headset/camera transform. Leave empty to use the OVRCameraRig center eye or Camera.main.")]
    public Transform headsetPromptAnchor;

    [Tooltip("Optional OVRCameraRig used for headset prompt placement. Leave empty to auto-find.")]
    public OVRCameraRig headsetPromptRig;

    [Tooltip("Local position from the headset camera for the world-space/fallback mesh prompt. Screen-space camera mode uses Screen Space Prompt Plane Distance instead.")]
    public Vector3 headsetPromptOffset = new Vector3(0f, 0f, 0.55f);

    [Tooltip("Minimum forward distance for camera-attached prompt meshes. Values closer than the camera near clip can disappear in VR.")]
    [Range(0.05f, 1f)]
    public float headsetPromptMinimumDistance = 0.32f;

    [Tooltip("World-space canvas scale. This only affects the fallback canvas path.")]
    [Range(0.0005f, 0.005f)]
    public float headsetPromptScale = 0.002f;

    [Tooltip("Scale of the visible camera-attached mesh prompt used by Quest. Increase this if the prompt looks too small.")]
    [Range(0.005f, 0.06f)]
    public float meshPromptScale = 0.02f;

    [Tooltip("Font size of the visible camera-attached mesh prompt used by Quest.")]
    [Range(6f, 24f)]
    public float meshPromptFontSize = 11.5f;

    [Tooltip("Local background panel size of the visible camera-attached mesh prompt.")]
    public Vector2 meshPromptPanelSize = new Vector2(190f, 74f);

    [Tooltip("Local text box size of the visible camera-attached mesh prompt.")]
    public Vector2 meshPromptTextSize = new Vector2(172f, 64f);

    [Tooltip("Distance from the camera for the screen-space headset prompt. Used when a headset camera is found.")]
    [Range(0.35f, 2f)]
    public float screenSpacePromptPlaneDistance = 0.62f;

    [Tooltip("Font size for the screen-space headset prompt. Used when a headset camera is found.")]
    [Range(32f, 96f)]
    public float screenSpacePromptFontSize = 56f;

    [Tooltip("Pixel size of the screen-space headset prompt panel. Used when a headset camera is found.")]
    public Vector2 screenSpacePromptPanelSize = new Vector2(1100f, 390f);

    [Tooltip("Pixel offset of the screen-space headset prompt panel from the center of the headset view.")]
    public Vector2 screenSpacePromptPanelOffset = new Vector2(0f, -80f);

    [Header("Calibration Profiles")]
    [Tooltip("Filename used when saving after the V-key flow completes. .json is added automatically.")]
    public string calibrationProfileName = "default";

    [Tooltip("Filename used when loading a saved profile. Leave empty to use Calibration Profile Name.")]
    public string calibrationProfileToLoad = "default";

    [Tooltip("Save IMU and MetaQuest calibration bounds automatically when the V-key flow completes.")]
    public bool saveProfileWhenFlowCompletes = true;

    [Tooltip("Load the selected profile when this component starts.")]
    public bool loadProfileOnStart = false;

    [Tooltip("When loading on start, ignore the selected name and load the newest profile file.")]
    public bool loadLatestProfileOnStart = true;

    [Tooltip("Store profiles under Assets/CalibrationProfiles while in the Unity project. Disable to use Application.persistentDataPath.")]
    public bool storeProfilesInProjectAssets = true;

    [Tooltip("Subfolder for calibration profile JSON files.")]
    public string calibrationProfileFolder = "CalibrationProfiles";

    [Tooltip("Last profile path saved or loaded.")]
    public string lastProfilePath = "";

    // ============================================
    // STATE
    // ============================================

    private enum Step
    {
        Idle,
        MovementNeutral,
        MovementForwardMax,
        MovementBackwardMax,
        MovementLeftMax,
        MovementRightMax,
        HeadsetNeutral,
        HeadsetLeftMax,
        HeadsetRightMax,
        SpreadMin,
        SpreadMax,
        SpreadNeutral,
        HeightMin,
        HeightMax,
        HeightNeutral,
        Done,
    }

    private static readonly Dictionary<Step, string> Prompts = new()
    {
        { Step.MovementNeutral,     "IMU ZERO - stand relaxed, facing forward" },
        { Step.MovementForwardMax,  "IMU FORWARD MAX - lean forward to comfortable maximum" },
        { Step.MovementBackwardMax, "IMU BACKWARD MAX - lean backward to comfortable maximum" },
        { Step.MovementLeftMax,     "IMU LEFT MAX - lean left to comfortable maximum" },
        { Step.MovementRightMax,    "IMU RIGHT MAX - lean right to comfortable maximum" },
        { Step.HeadsetNeutral,      "HEADSET ZERO - face straight ahead" },
        { Step.HeadsetLeftMax,      "HEADSET LEFT MAX - turn head left to comfortable maximum" },
        { Step.HeadsetRightMax,     "HEADSET RIGHT MAX - turn head right to comfortable maximum" },
        { Step.SpreadMin,     "MIN SPREAD — bring hands as close together as comfortable" },
        { Step.SpreadMax,     "MAX SPREAD — extend hands as wide as comfortable" },
        { Step.SpreadNeutral, "NEUTRAL SPREAD — relaxed, comfortable middle distance" },
        { Step.HeightMin,     "MIN HEIGHT — lower hands to the bottom of your range" },
        { Step.HeightMax,     "MAX HEIGHT — raise hands to the top of your range" },
        { Step.HeightNeutral, "NEUTRAL HEIGHT — relaxed, comfortable middle height" },
    };

    private Step _step = Step.Idle;
    private float _countdown = 0f;
    // Each step has two phases: get-ready (assume the pose) → capture countdown (hold steady).
    private bool _isGettingReady = false;
    private string _screenMessage = "";
    private float _screenMessageUntil = 0f;
    private Canvas _headsetPromptCanvas;
    private RectTransform _headsetPromptPanel;
    private TextMeshProUGUI _headsetPromptText;
    private Transform _headsetPromptCurrentAnchor;
    private Camera _headsetPromptCamera;
    private bool _headsetPromptLogged = false;
    private GameObject _meshPromptRoot;
    private Transform _meshPromptBackground;
    private TextMeshPro _meshPromptText;
    private TextMeshPro _meshPromptTextBack;
    private readonly List<MeshPromptInstance> _meshPromptInstances = new();

    private class MeshPromptInstance
    {
        public Transform anchor;
        public int layer;
        public GameObject root;
        public Transform background;
        public TextMeshPro frontText;
        public TextMeshPro backText;
    }

    // Snapshots of the prior calibration so we can roll back on cancel.
    private Vector3 _imuOffsetBackup = Vector3.zero;
    private bool _imuHasNeutralBackup = false;
    private bool _imuUseDirectionalBackup = false;
    private float _imuForwardRollBackup, _imuBackwardRollBackup, _imuLeftPitchBackup, _imuRightPitchBackup;
    private bool _headsetHasNeutralBackup = false;
    private bool _headsetUseDirectionalBackup = false;
    private float _headsetNeutralYawBackup, _headsetLeftYawBackup, _headsetRightYawBackup;
    private float _spreadMinBackup,    _spreadNeutralBackup,    _spreadMaxBackup;
    private float _heightMinBackup,    _heightNeutralBackup,    _heightMaxBackup;
    private bool _backupsTaken = false;

    public bool IsRunning => _step != Step.Idle && _step != Step.Done;

    [System.Serializable]
    public class CalibrationProfileData
    {
        public int version = 1;
        public string savedAt;
        public IMUMovementCalibration imuMovement = new();
        public HeadsetYawCalibration headsetYaw = new();
        public MetaSpreadCalibration controllerSpread = new();
        public MetaHeightCalibration controllerHeight = new();
        public MetaSpreadCalibration handSpread = new();
        public MetaHeightCalibration handHeight = new();
    }

    [System.Serializable]
    public class IMUMovementCalibration
    {
        public bool present;
        public bool hasNeutralCalibration;
        public bool useDirectionalAngleCalibration;
        public Vector3 calibrationOffset;
        public float forwardRollAngle;
        public float backwardRollAngle;
        public float leftPitchAngle;
        public float rightPitchAngle;
    }

    [System.Serializable]
    public class HeadsetYawCalibration
    {
        public bool present;
        public bool hasNeutralCalibration;
        public bool useDirectionalYawCalibration;
        public float neutralYaw;
        public float leftYawAngle;
        public float rightYawAngle;
    }

    [System.Serializable]
    public class MetaSpreadCalibration
    {
        public bool present;
        public float minDistance;
        public float neutralDistance;
        public float maxDistance;
    }

    [System.Serializable]
    public class MetaHeightCalibration
    {
        public bool present;
        public float minHeight;
        public float neutralHeight;
        public float maxHeight;
    }

    // ============================================
    // LIFECYCLE
    // ============================================

    void Start()
    {
        ResolveTargets();

        if (loadProfileOnStart)
        {
            if (loadLatestProfileOnStart)
                LoadLatestCalibrationProfile();
            else
                LoadCalibrationProfile();
        }
    }

    void Update()
    {
        if (!IsRunning)
        {
            if (Input.GetKeyDown(startKey)) BeginFlow();
            return;
        }

        if (Input.GetKeyDown(cancelKey)) { Cancel(); return; }
        if (Input.GetKeyDown(startKey))
        {
            _headsetPromptLogged = false;
            ReactivateHeadsetPrompt();
            UpdateHeadsetPrompt();
        }

        _countdown -= Time.deltaTime;
        bool skip = Input.GetKeyDown(advanceKey);

        if (_countdown <= 0f || skip)
        {
            if (_isGettingReady)
            {
                // Get-ready phase done: start the capture countdown for the same step.
                _isGettingReady = false;
                _countdown = countdownPerStep;
                if (verboseLogging) Debug.Log($"  [{_step}] get-ready done, capturing in {countdownPerStep:F1}s");
            }
            else
            {
                CaptureCurrentStep();
                AdvanceStep();
            }
        }
    }

    void LateUpdate()
    {
        UpdateHeadsetPrompt();
    }

    void OnDestroy()
    {
        if (_headsetPromptCanvas != null)
            Destroy(_headsetPromptCanvas.gameObject);
        if (_meshPromptRoot != null)
            Destroy(_meshPromptRoot);
        foreach (MeshPromptInstance prompt in _meshPromptInstances)
        {
            if (prompt.root != null)
                Destroy(prompt.root);
        }
        _meshPromptInstances.Clear();
    }

    // ============================================
    // FLOW CONTROL
    // ============================================

    public void BeginFlow()
    {
        ResolveTargets();
        SyncProfileSettingsFromInputFusionManager();
        _screenMessage = "";
        _screenMessageUntil = 0f;
        _headsetPromptLogged = false;
        TakeBackups();
        _step = GetFirstCalibrationStep();
        _isGettingReady = true;
        _countdown = getReadyPerStep;
        ReactivateHeadsetPrompt();
        UpdateHeadsetPrompt();
        if (verboseLogging) Debug.Log("=== Input calibration: starting (press Esc to cancel) ===");
    }

    private void ResolveTargets()
    {
        if (inputFusionManager == null) inputFusionManager = FindObjectOfType<InputFusionManager>();
        if (imuMovement       == null) imuMovement       = FindObjectOfType<IMUMovementInput>();
        if (headsetYaw        == null) headsetYaw        = FindObjectOfType<HeadsetYawInput>();
        if (controllerSpread  == null) controllerSpread  = FindObjectOfType<ControllerSpreadInput>();
        if (handSpread        == null) handSpread        = FindObjectOfType<HandSpreadInput>();
        if (controllerHeight  == null) controllerHeight  = FindObjectOfType<ControllerHeightInput>();
        if (handHeight        == null) handHeight        = FindObjectOfType<HandHeightInput>();
    }

    private void SyncProfileSettingsFromInputFusionManager()
    {
        if (inputFusionManager == null) return;

        calibrationProfileName = inputFusionManager.calibrationProfileName;
        calibrationProfileToLoad = inputFusionManager.calibrationProfileToLoad;
        saveProfileWhenFlowCompletes = inputFusionManager.saveCalibrationAfterVFlow;
        loadProfileOnStart = inputFusionManager.loadCalibrationOnStart;
        loadLatestProfileOnStart = inputFusionManager.loadLatestCalibrationOnStart;
    }

    private void AdvanceStep()
    {
        _step = _step switch
        {
            Step.MovementNeutral     => Step.MovementForwardMax,
            Step.MovementForwardMax  => Step.MovementBackwardMax,
            Step.MovementBackwardMax => Step.MovementLeftMax,
            Step.MovementLeftMax     => Step.MovementRightMax,
            Step.MovementRightMax    => HasHeadsetYawTarget() ? Step.HeadsetNeutral : Step.SpreadMin,
            Step.HeadsetNeutral      => Step.HeadsetLeftMax,
            Step.HeadsetLeftMax      => Step.HeadsetRightMax,
            Step.HeadsetRightMax     => Step.SpreadMin,
            Step.SpreadMin     => Step.SpreadMax,
            Step.SpreadMax     => Step.SpreadNeutral,
            Step.SpreadNeutral => Step.HeightMin,
            Step.HeightMin     => Step.HeightMax,
            Step.HeightMax     => Step.HeightNeutral,
            Step.HeightNeutral => Step.Done,
            _                  => Step.Idle,
        };

        if (_step == Step.Done)
        {
            if (verboseLogging) Debug.Log("=== Input calibration: complete ===");
            if (saveProfileWhenFlowCompletes)
                SaveCalibrationProfile();
            _step = Step.Idle;
            _backupsTaken = false;
            _isGettingReady = false;
        }
        else
        {
            _isGettingReady = true;
            _countdown = getReadyPerStep;
        }
    }

    private void CaptureCurrentStep()
    {
        switch (_step)
        {
            case Step.MovementNeutral:     CaptureIMUMovement(m => m.CalibrateNeutral());     break;
            case Step.MovementForwardMax:  CaptureIMUMovement(m => m.CaptureForwardMax());   break;
            case Step.MovementBackwardMax: CaptureIMUMovement(m => m.CaptureBackwardMax());  break;
            case Step.MovementLeftMax:     CaptureIMUMovement(m => m.CaptureLeftMax());      break;
            case Step.MovementRightMax:    CaptureIMUMovement(m => m.CaptureRightMax());     break;
            case Step.HeadsetNeutral:      CaptureHeadsetYaw(h => h.CalibrateNeutral());     break;
            case Step.HeadsetLeftMax:      CaptureHeadsetYaw(h => h.CaptureLeftMax());       break;
            case Step.HeadsetRightMax:     CaptureHeadsetYaw(h => h.CaptureRightMax());      break;
            case Step.SpreadMin:     CaptureSpread((s, h) => { s?.CaptureMin();     h?.CaptureMin();     }); break;
            case Step.SpreadMax:     CaptureSpread((s, h) => { s?.CaptureMax();     h?.CaptureMax();     }); break;
            case Step.SpreadNeutral: CaptureSpread((s, h) => { s?.CaptureNeutral(); h?.CaptureNeutral(); }); break;
            case Step.HeightMin:     CaptureHeight((s, h) => { s?.CaptureMin();     h?.CaptureMin();     }); break;
            case Step.HeightMax:     CaptureHeight((s, h) => { s?.CaptureMax();     h?.CaptureMax();     }); break;
            case Step.HeightNeutral: CaptureHeight((s, h) => { s?.CaptureNeutral(); h?.CaptureNeutral(); }); break;
        }
    }

    private bool HasIMUMovementTarget()
    {
        return imuMovement != null && imuMovement.IsAvailable;
    }

    private bool HasHeadsetYawTarget()
    {
        return headsetYaw != null && headsetYaw.IsAvailable;
    }

    private Step GetFirstCalibrationStep()
    {
        if (HasIMUMovementTarget()) return Step.MovementNeutral;
        if (HasHeadsetYawTarget()) return Step.HeadsetNeutral;
        return Step.SpreadMin;
    }

    private void CaptureIMUMovement(System.Action<IMUMovementInput> apply)
    {
        if (!HasIMUMovementTarget())
        {
            if (verboseLogging) Debug.LogWarning($"  [{_step}] IMU movement n/a");
            return;
        }

        apply(imuMovement);
        if (verboseLogging)
        {
            Debug.Log(
                $"  [{_step}] pitch={imuMovement.GetRawPitchAngle():F1}° roll={imuMovement.GetRawRollAngle():F1}° " +
                $"bounds F/B={imuMovement.forwardRollAngle:F1}/{imuMovement.backwardRollAngle:F1} " +
                $"L/R={imuMovement.leftPitchAngle:F1}/{imuMovement.rightPitchAngle:F1}");
        }
    }

    private void CaptureHeadsetYaw(System.Action<HeadsetYawInput> apply)
    {
        if (!HasHeadsetYawTarget())
        {
            if (verboseLogging) Debug.LogWarning($"  [{_step}] headset yaw n/a");
            return;
        }

        apply(headsetYaw);
        if (verboseLogging)
        {
            Debug.Log(
                $"  [{_step}] yaw={headsetYaw.CurrentYaw:F1}° rel={headsetYaw.GetRelativeYaw():F1}° " +
                $"neutral={headsetYaw.NeutralYaw:F1}° bounds L/R={headsetYaw.leftYawAngle:F1}/{headsetYaw.rightYawAngle:F1}");
        }
    }

    private void CaptureSpread(System.Action<ControllerSpreadInput, HandSpreadInput> apply)
    {
        apply(controllerSpread, handSpread);
        if (verboseLogging)
        {
            string ctlr = controllerSpread != null && controllerSpread.IsAvailable ? $"controller d={controllerSpread.GetCurrentDistance():F2}" : "controller n/a";
            string hand = handSpread != null && handSpread.IsAvailable ? $"hand d={handSpread.GetCurrentDistance():F2}" : "hand n/a";
            Debug.Log($"  [{_step}] {ctlr}  {hand}");
        }
    }

    private void CaptureHeight(System.Action<ControllerHeightInput, HandHeightInput> apply)
    {
        apply(controllerHeight, handHeight);
        if (verboseLogging)
        {
            string ctlr = controllerHeight != null && controllerHeight.IsAvailable ? $"controller y={controllerHeight.GetAverageControllerHeight():F2}" : "controller n/a";
            string hand = handHeight != null && handHeight.IsAvailable ? $"hand y={handHeight.GetAverageHandHeight():F2}" : "hand n/a";
            Debug.Log($"  [{_step}] {ctlr}  {hand}");
        }
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        if (verboseLogging) Debug.LogWarning("=== Input calibration: cancelled, rolling back ===");
        RestoreBackups();
        _step = Step.Idle;
        _isGettingReady = false;
    }

    // ============================================
    // BACKUPS — single shared snapshot for whichever sibling is live.
    // We back up from controller* if connected, otherwise from hand*; on restore
    // we write the same snapshot back to whichever is still live. That's a tradeoff:
    // if the participant swaps controllers↔hands mid-flow, the rollback uses the
    // first source's values. In practice this never happens within a 20-second flow.
    // ============================================

    private void TakeBackups()
    {
        if (imuMovement != null)
        {
            _imuOffsetBackup = imuMovement.CalibrationOffset;
            _imuHasNeutralBackup = imuMovement.HasNeutralCalibration;
            _imuUseDirectionalBackup = imuMovement.useDirectionalAngleCalibration;
            _imuForwardRollBackup = imuMovement.forwardRollAngle;
            _imuBackwardRollBackup = imuMovement.backwardRollAngle;
            _imuLeftPitchBackup = imuMovement.leftPitchAngle;
            _imuRightPitchBackup = imuMovement.rightPitchAngle;
        }

        if (headsetYaw != null)
        {
            _headsetNeutralYawBackup = headsetYaw.NeutralYaw;
            _headsetHasNeutralBackup = headsetYaw.HasNeutralCalibration;
            _headsetUseDirectionalBackup = headsetYaw.useDirectionalYawCalibration;
            _headsetLeftYawBackup = headsetYaw.leftYawAngle;
            _headsetRightYawBackup = headsetYaw.rightYawAngle;
        }

        if (controllerSpread != null)
        {
            _spreadMinBackup     = controllerSpread.minDistance;
            _spreadNeutralBackup = controllerSpread.neutralDistance;
            _spreadMaxBackup     = controllerSpread.maxDistance;
        }
        else if (handSpread != null)
        {
            _spreadMinBackup     = handSpread.minDistance;
            _spreadNeutralBackup = handSpread.neutralDistance;
            _spreadMaxBackup     = handSpread.maxDistance;
        }

        if (controllerHeight != null)
        {
            _heightMinBackup     = controllerHeight.minHeight;
            _heightNeutralBackup = controllerHeight.neutralHeight;
            _heightMaxBackup     = controllerHeight.maxHeight;
        }
        else if (handHeight != null)
        {
            _heightMinBackup     = handHeight.minHeight;
            _heightNeutralBackup = handHeight.neutralHeight;
            _heightMaxBackup     = handHeight.maxHeight;
        }

        _backupsTaken = true;
    }

    private void RestoreBackups()
    {
        if (!_backupsTaken) return;

        if (imuMovement != null)
        {
            imuMovement.RestoreCalibration(
                _imuOffsetBackup,
                _imuHasNeutralBackup,
                _imuUseDirectionalBackup,
                _imuForwardRollBackup,
                _imuBackwardRollBackup,
                _imuLeftPitchBackup,
                _imuRightPitchBackup);
        }

        if (headsetYaw != null)
        {
            headsetYaw.RestoreCalibration(
                _headsetNeutralYawBackup,
                _headsetHasNeutralBackup,
                _headsetUseDirectionalBackup,
                _headsetLeftYawBackup,
                _headsetRightYawBackup);
        }

        if (controllerSpread != null)
        {
            controllerSpread.minDistance     = _spreadMinBackup;
            controllerSpread.neutralDistance = _spreadNeutralBackup;
            controllerSpread.maxDistance     = _spreadMaxBackup;
        }
        if (handSpread != null)
        {
            handSpread.minDistance     = _spreadMinBackup;
            handSpread.neutralDistance = _spreadNeutralBackup;
            handSpread.maxDistance     = _spreadMaxBackup;
        }
        if (controllerHeight != null)
        {
            controllerHeight.minHeight     = _heightMinBackup;
            controllerHeight.neutralHeight = _heightNeutralBackup;
            controllerHeight.maxHeight     = _heightMaxBackup;
        }
        if (handHeight != null)
        {
            handHeight.minHeight     = _heightMinBackup;
            handHeight.neutralHeight = _heightNeutralBackup;
            handHeight.maxHeight     = _heightMaxBackup;
        }

        _backupsTaken = false;
    }

    [ContextMenu("Save Calibration Profile Now")]
    public void SaveCalibrationProfile()
    {
        ResolveTargets();
        SyncProfileSettingsFromInputFusionManager();
        CalibrationProfileData profile = CaptureProfileData();
        profile.savedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        string path = GetProfilePath(calibrationProfileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(profile, true));
        lastProfilePath = path;
        Debug.Log($"[CalibrationProfile] Saved '{calibrationProfileName}' to {path}");
        ShowCalibrationMessage($"Calibration saved: {Path.GetFileName(path)}", 4f);
    }

    [ContextMenu("Load Selected Calibration Profile")]
    public void LoadCalibrationProfile()
    {
        ResolveTargets();
        string profileName = string.IsNullOrWhiteSpace(calibrationProfileToLoad)
            ? calibrationProfileName
            : calibrationProfileToLoad;
        string path = GetProfilePath(profileName);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[CalibrationProfile] No calibration profile found at {path}");
            return;
        }

        string json = File.ReadAllText(path);
        CalibrationProfileData profile = JsonUtility.FromJson<CalibrationProfileData>(json);
        if (profile == null)
        {
            Debug.LogWarning($"[CalibrationProfile] Could not parse calibration profile at {path}");
            return;
        }

        ApplyProfileData(profile);
        lastProfilePath = path;
        string valuesSummary = BuildImportedValuesSummary(profile);
        string message = $"Calibration loaded: {Path.GetFileName(path)}";
        Debug.Log($"[CalibrationProfile] Successfully imported '{profileName}' from {path}. {valuesSummary}");
        ShowCalibrationMessage($"{message}\nValues imported correctly.", 4f);
    }

    [ContextMenu("Load Latest Calibration Profile")]
    public void LoadLatestCalibrationProfile()
    {
        string folder = GetProfileFolderPath();
        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[CalibrationProfile] Calibration profile folder does not exist: {folder}");
            return;
        }

        DirectoryInfo dir = new DirectoryInfo(folder);
        FileInfo latest = null;
        foreach (FileInfo file in dir.GetFiles("*.json"))
        {
            if (latest == null || file.LastWriteTimeUtc > latest.LastWriteTimeUtc)
                latest = file;
        }

        if (latest == null)
        {
            Debug.LogWarning($"[CalibrationProfile] No calibration profile JSON files found in {folder}");
            return;
        }

        calibrationProfileToLoad = Path.GetFileNameWithoutExtension(latest.Name);
        LoadCalibrationProfile();
    }

    private string BuildImportedValuesSummary(CalibrationProfileData profile)
    {
        List<string> parts = new List<string>();

        if (profile.imuMovement.present)
        {
            parts.Add(
                $"IMU neutral={profile.imuMovement.hasNeutralCalibration}, " +
                $"directional={profile.imuMovement.useDirectionalAngleCalibration}, " +
                $"F/B roll={profile.imuMovement.forwardRollAngle:F1}/{profile.imuMovement.backwardRollAngle:F1}, " +
                $"L/R pitch={profile.imuMovement.leftPitchAngle:F1}/{profile.imuMovement.rightPitchAngle:F1}");
        }

        if (profile.headsetYaw.present)
        {
            parts.Add(
                $"Headset yaw neutral={profile.headsetYaw.neutralYaw:F1}, " +
                $"hasNeutral={profile.headsetYaw.hasNeutralCalibration}, " +
                $"directional={profile.headsetYaw.useDirectionalYawCalibration}, " +
                $"L/R={profile.headsetYaw.leftYawAngle:F1}/{profile.headsetYaw.rightYawAngle:F1}");
        }

        if (profile.controllerSpread.present)
            parts.Add($"Controller spread=[{profile.controllerSpread.minDistance:F2}, {profile.controllerSpread.neutralDistance:F2}, {profile.controllerSpread.maxDistance:F2}]");

        if (profile.controllerHeight.present)
            parts.Add($"Controller height=[{profile.controllerHeight.minHeight:F2}, {profile.controllerHeight.neutralHeight:F2}, {profile.controllerHeight.maxHeight:F2}]");

        if (profile.handSpread.present)
            parts.Add($"Hand spread=[{profile.handSpread.minDistance:F2}, {profile.handSpread.neutralDistance:F2}, {profile.handSpread.maxDistance:F2}]");

        if (profile.handHeight.present)
            parts.Add($"Hand height=[{profile.handHeight.minHeight:F2}, {profile.handHeight.neutralHeight:F2}, {profile.handHeight.maxHeight:F2}]");

        return parts.Count > 0 ? string.Join("; ", parts) : "No calibration values were marked present in this file.";
    }

    private void ShowCalibrationMessage(string message, float seconds)
    {
        _screenMessage = message;
        _screenMessageUntil = Time.unscaledTime + seconds;

        try
        {
            textInfo.setTextErrorStatic(message, seconds);
        }
        catch (System.Exception)
        {
            // Some scenes intentionally run without the study HUD.
        }
    }

    private CalibrationProfileData CaptureProfileData()
    {
        CalibrationProfileData profile = new CalibrationProfileData();

        if (imuMovement != null && IsValidIMUMovementCalibration(
                imuMovement.HasNeutralCalibration,
                imuMovement.useDirectionalAngleCalibration,
                imuMovement.forwardRollAngle,
                imuMovement.backwardRollAngle,
                imuMovement.leftPitchAngle,
                imuMovement.rightPitchAngle))
        {
            profile.imuMovement.present = true;
            profile.imuMovement.calibrationOffset = imuMovement.CalibrationOffset;
            profile.imuMovement.hasNeutralCalibration = imuMovement.HasNeutralCalibration;
            profile.imuMovement.useDirectionalAngleCalibration = imuMovement.useDirectionalAngleCalibration;
            profile.imuMovement.forwardRollAngle = imuMovement.forwardRollAngle;
            profile.imuMovement.backwardRollAngle = imuMovement.backwardRollAngle;
            profile.imuMovement.leftPitchAngle = imuMovement.leftPitchAngle;
            profile.imuMovement.rightPitchAngle = imuMovement.rightPitchAngle;
        }

        if (headsetYaw != null && IsValidHeadsetYawCalibration(
                headsetYaw.HasNeutralCalibration,
                headsetYaw.useDirectionalYawCalibration,
                headsetYaw.leftYawAngle,
                headsetYaw.rightYawAngle))
        {
            profile.headsetYaw.present = true;
            profile.headsetYaw.neutralYaw = headsetYaw.NeutralYaw;
            profile.headsetYaw.hasNeutralCalibration = headsetYaw.HasNeutralCalibration;
            profile.headsetYaw.useDirectionalYawCalibration = headsetYaw.useDirectionalYawCalibration;
            profile.headsetYaw.leftYawAngle = headsetYaw.leftYawAngle;
            profile.headsetYaw.rightYawAngle = headsetYaw.rightYawAngle;
        }

        if (controllerSpread != null && IsValidRange(
                controllerSpread.minDistance,
                controllerSpread.neutralDistance,
                controllerSpread.maxDistance))
        {
            profile.controllerSpread.present = true;
            profile.controllerSpread.minDistance = controllerSpread.minDistance;
            profile.controllerSpread.neutralDistance = controllerSpread.neutralDistance;
            profile.controllerSpread.maxDistance = controllerSpread.maxDistance;
        }

        if (controllerHeight != null && IsValidRange(
                controllerHeight.minHeight,
                controllerHeight.neutralHeight,
                controllerHeight.maxHeight))
        {
            profile.controllerHeight.present = true;
            profile.controllerHeight.minHeight = controllerHeight.minHeight;
            profile.controllerHeight.neutralHeight = controllerHeight.neutralHeight;
            profile.controllerHeight.maxHeight = controllerHeight.maxHeight;
        }

        if (handSpread != null && IsValidRange(
                handSpread.minDistance,
                handSpread.neutralDistance,
                handSpread.maxDistance))
        {
            profile.handSpread.present = true;
            profile.handSpread.minDistance = handSpread.minDistance;
            profile.handSpread.neutralDistance = handSpread.neutralDistance;
            profile.handSpread.maxDistance = handSpread.maxDistance;
        }

        if (handHeight != null && IsValidRange(
                handHeight.minHeight,
                handHeight.neutralHeight,
                handHeight.maxHeight))
        {
            profile.handHeight.present = true;
            profile.handHeight.minHeight = handHeight.minHeight;
            profile.handHeight.neutralHeight = handHeight.neutralHeight;
            profile.handHeight.maxHeight = handHeight.maxHeight;
        }

        return profile;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsValidRange(float min, float neutral, float max)
    {
        return IsFinite(min) && IsFinite(neutral) && IsFinite(max)
            && max > min
            && neutral >= min
            && neutral <= max;
    }

    private static bool IsValidIMUMovementCalibration(
        bool hasNeutral,
        bool useDirectional,
        float forwardRoll,
        float backwardRoll,
        float leftPitch,
        float rightPitch)
    {
        if (!hasNeutral) return false;
        if (!useDirectional) return true;
        return IsFinite(forwardRoll) && IsFinite(backwardRoll)
            && IsFinite(leftPitch) && IsFinite(rightPitch)
            && Mathf.Abs(forwardRoll) > 0.001f
            && Mathf.Abs(backwardRoll) > 0.001f
            && Mathf.Abs(leftPitch) > 0.001f
            && Mathf.Abs(rightPitch) > 0.001f
            && Mathf.Sign(forwardRoll) != Mathf.Sign(backwardRoll)
            && Mathf.Sign(leftPitch) != Mathf.Sign(rightPitch);
    }

    private static bool IsValidHeadsetYawCalibration(
        bool hasNeutral,
        bool useDirectional,
        float leftYaw,
        float rightYaw)
    {
        if (!hasNeutral) return false;
        if (!useDirectional) return true;
        return IsFinite(leftYaw) && IsFinite(rightYaw)
            && Mathf.Abs(leftYaw) > 0.001f
            && Mathf.Abs(rightYaw) > 0.001f
            && Mathf.Sign(leftYaw) != Mathf.Sign(rightYaw);
    }

    private void ApplyProfileData(CalibrationProfileData profile)
    {
        if (imuMovement != null && profile.imuMovement.present
            && IsValidIMUMovementCalibration(
                profile.imuMovement.hasNeutralCalibration,
                profile.imuMovement.useDirectionalAngleCalibration,
                profile.imuMovement.forwardRollAngle,
                profile.imuMovement.backwardRollAngle,
                profile.imuMovement.leftPitchAngle,
                profile.imuMovement.rightPitchAngle))
        {
            imuMovement.RestoreCalibration(
                profile.imuMovement.calibrationOffset,
                profile.imuMovement.hasNeutralCalibration,
                profile.imuMovement.useDirectionalAngleCalibration,
                profile.imuMovement.forwardRollAngle,
                profile.imuMovement.backwardRollAngle,
                profile.imuMovement.leftPitchAngle,
                profile.imuMovement.rightPitchAngle);
        }

        if (headsetYaw != null && profile.headsetYaw.present
            && IsValidHeadsetYawCalibration(
                profile.headsetYaw.hasNeutralCalibration,
                profile.headsetYaw.useDirectionalYawCalibration,
                profile.headsetYaw.leftYawAngle,
                profile.headsetYaw.rightYawAngle))
        {
            headsetYaw.RestoreCalibration(
                profile.headsetYaw.neutralYaw,
                profile.headsetYaw.hasNeutralCalibration,
                profile.headsetYaw.useDirectionalYawCalibration,
                profile.headsetYaw.leftYawAngle,
                profile.headsetYaw.rightYawAngle);
        }

        if (controllerSpread != null && profile.controllerSpread.present
            && IsValidRange(
                profile.controllerSpread.minDistance,
                profile.controllerSpread.neutralDistance,
                profile.controllerSpread.maxDistance))
        {
            controllerSpread.minDistance = profile.controllerSpread.minDistance;
            controllerSpread.neutralDistance = profile.controllerSpread.neutralDistance;
            controllerSpread.maxDistance = profile.controllerSpread.maxDistance;
        }

        if (controllerHeight != null && profile.controllerHeight.present
            && IsValidRange(
                profile.controllerHeight.minHeight,
                profile.controllerHeight.neutralHeight,
                profile.controllerHeight.maxHeight))
        {
            controllerHeight.minHeight = profile.controllerHeight.minHeight;
            controllerHeight.neutralHeight = profile.controllerHeight.neutralHeight;
            controllerHeight.maxHeight = profile.controllerHeight.maxHeight;
        }

        if (handSpread != null && profile.handSpread.present
            && IsValidRange(
                profile.handSpread.minDistance,
                profile.handSpread.neutralDistance,
                profile.handSpread.maxDistance))
        {
            handSpread.minDistance = profile.handSpread.minDistance;
            handSpread.neutralDistance = profile.handSpread.neutralDistance;
            handSpread.maxDistance = profile.handSpread.maxDistance;
        }

        if (handHeight != null && profile.handHeight.present
            && IsValidRange(
                profile.handHeight.minHeight,
                profile.handHeight.neutralHeight,
                profile.handHeight.maxHeight))
        {
            handHeight.minHeight = profile.handHeight.minHeight;
            handHeight.neutralHeight = profile.handHeight.neutralHeight;
            handHeight.maxHeight = profile.handHeight.maxHeight;
        }
    }

    public string GetProfilePath(string profileName)
    {
        string safeName = MakeFileSafe(string.IsNullOrWhiteSpace(profileName) ? "default" : profileName);
        if (!safeName.EndsWith(".json")) safeName += ".json";

        string root = storeProfilesInProjectAssets
            ? Path.Combine(Application.dataPath, calibrationProfileFolder)
            : Path.Combine(Application.persistentDataPath, calibrationProfileFolder);
        return Path.Combine(root, safeName);
    }

    public string GetProfileFolderPath()
    {
        return storeProfilesInProjectAssets
            ? Path.Combine(Application.dataPath, calibrationProfileFolder)
            : Path.Combine(Application.persistentDataPath, calibrationProfileFolder);
    }

    private static string MakeFileSafe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
    }

    // ============================================
    // HEADSET PROMPT
    // ============================================

    private void UpdateHeadsetPrompt()
    {
        bool showTimedMessage = !string.IsNullOrEmpty(_screenMessage) && Time.unscaledTime <= _screenMessageUntil;
        bool shouldShow = Application.isPlaying && showPromptInHeadset && (IsRunning || showTimedMessage);

        if (!shouldShow)
        {
            SetHeadsetPromptVisible(false);
            SetMeshPromptVisible(false);
            SetMeshPromptsVisible(false);
            return;
        }

        Transform meshAnchor = ResolveHeadsetPromptAnchor();
        string promptText = IsRunning ? BuildPromptText() : _screenMessage;
        UpdateMeshPrompt(meshAnchor, promptText);
        UpdateMeshPrompts(promptText);

        Camera promptCamera = ResolveHeadsetPromptCamera();
        Transform anchor = promptCamera != null ? promptCamera.transform : meshAnchor;
        if (anchor == null)
        {
            SetHeadsetPromptVisible(false);
            return;
        }

        EnsureHeadsetPrompt(anchor, promptCamera);
        SetHeadsetPromptVisible(true);

        _headsetPromptText.text = IsRunning ? BuildPromptText() : _screenMessage;
        RefreshPromptText(_headsetPromptText);
        if (_headsetPromptCanvas.renderMode == RenderMode.ScreenSpaceCamera)
        {
            RectTransform rect = _headsetPromptCanvas.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _headsetPromptPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _headsetPromptPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _headsetPromptPanel.pivot = new Vector2(0.5f, 0.5f);
            _headsetPromptPanel.sizeDelta = screenSpacePromptPanelSize;
            _headsetPromptPanel.anchoredPosition = screenSpacePromptPanelOffset;
            _headsetPromptText.fontSize = screenSpacePromptFontSize;
            float minPlaneDistance = promptCamera != null ? promptCamera.nearClipPlane + 0.05f : 0.1f;
            _headsetPromptCanvas.planeDistance = Mathf.Max(screenSpacePromptPlaneDistance, minPlaneDistance);
        }
        else
        {
            _headsetPromptPanel.anchorMin = Vector2.zero;
            _headsetPromptPanel.anchorMax = Vector2.one;
            _headsetPromptPanel.offsetMin = Vector2.zero;
            _headsetPromptPanel.offsetMax = Vector2.zero;
            _headsetPromptCanvas.transform.localPosition = headsetPromptOffset;
            _headsetPromptCanvas.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _headsetPromptCanvas.transform.localScale = Vector3.one * headsetPromptScale;
        }

        if (verboseLogging && !_headsetPromptLogged)
        {
            string cameraName = promptCamera != null ? promptCamera.name : "none";
            Debug.Log($"MetaQuestCalibrationFlow: headset prompt visible. Camera={cameraName}, Anchor={anchor.name}, RenderMode={_headsetPromptCanvas.renderMode}");
            _headsetPromptLogged = true;
        }
    }

    private Camera ResolveHeadsetPromptCamera()
    {
        if (headsetPromptAnchor != null)
        {
            Camera explicitCamera = headsetPromptAnchor.GetComponent<Camera>();
            if (explicitCamera != null) return explicitCamera;
        }

        OVRCameraRig rig = ResolveHeadsetPromptRig();
        if (rig != null && rig.centerEyeAnchor != null)
        {
            Camera rigCenterEyeCamera = rig.centerEyeAnchor.GetComponent<Camera>();
            if (rigCenterEyeCamera != null) return rigCenterEyeCamera;
        }

        if (headsetYaw != null && headsetYaw.cameraRig != null && headsetYaw.cameraRig.centerEyeAnchor != null)
        {
            Camera centerEyeCamera = headsetYaw.cameraRig.centerEyeAnchor.GetComponent<Camera>();
            if (centerEyeCamera != null) return centerEyeCamera;
        }

        Camera[] cameras = Camera.allCameras;
        foreach (Camera cam in cameras)
        {
            if (cam != null && cam.enabled && cam.stereoTargetEye != StereoTargetEyeMask.None)
                return cam;
        }

        return Camera.main;
    }

    private Transform ResolveHeadsetPromptAnchor()
    {
        if (headsetPromptAnchor != null) return headsetPromptAnchor;
        OVRCameraRig rig = ResolveHeadsetPromptRig();
        if (rig != null && rig.centerEyeAnchor != null) return rig.centerEyeAnchor;
        if (headsetYaw != null && headsetYaw.cameraRig != null && headsetYaw.cameraRig.centerEyeAnchor != null)
            return headsetYaw.cameraRig.centerEyeAnchor;
        Camera mainCamera = Camera.main;
        return mainCamera != null ? mainCamera.transform : null;
    }

    private OVRCameraRig ResolveHeadsetPromptRig()
    {
        if (headsetPromptRig != null) return headsetPromptRig;
        if (headsetYaw != null && headsetYaw.cameraRig != null) return headsetYaw.cameraRig;

        headsetPromptRig = FindObjectOfType<OVRCameraRig>();
        return headsetPromptRig;
    }

    private void EnsureHeadsetPrompt(Transform anchor, Camera promptCamera)
    {
        if (_headsetPromptCanvas != null && _headsetPromptCurrentAnchor == anchor && _headsetPromptCamera == promptCamera)
            return;

        if (_headsetPromptCanvas == null)
            CreateHeadsetPrompt();

        _headsetPromptCurrentAnchor = anchor;
        _headsetPromptCamera = promptCamera;

        if (promptCamera != null)
        {
            _headsetPromptCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _headsetPromptCanvas.worldCamera = promptCamera;
            _headsetPromptCanvas.targetDisplay = promptCamera.targetDisplay;
            _headsetPromptCanvas.transform.SetParent(null, false);
        }
        else
        {
            _headsetPromptCanvas.renderMode = RenderMode.WorldSpace;
            _headsetPromptCanvas.worldCamera = null;
            _headsetPromptCanvas.transform.SetParent(anchor, false);
        }
    }

    private void CreateHeadsetPrompt()
    {
        GameObject canvasObject = new GameObject("Meta Quest Calibration Prompt");
        _headsetPromptCanvas = canvasObject.AddComponent<Canvas>();
        _headsetPromptCanvas.renderMode = RenderMode.WorldSpace;
        _headsetPromptCanvas.sortingOrder = 5000;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = screenSpacePromptPanelSize;

        GameObject panelObject = new GameObject("Panel");
        panelObject.transform.SetParent(canvasObject.transform, false);
        Image background = panelObject.AddComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.82f);
        _headsetPromptPanel = panelObject.GetComponent<RectTransform>();
        _headsetPromptPanel.anchorMin = Vector2.zero;
        _headsetPromptPanel.anchorMax = Vector2.one;
        _headsetPromptPanel.offsetMin = Vector2.zero;
        _headsetPromptPanel.offsetMax = Vector2.zero;

        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(panelObject.transform, false);
        _headsetPromptText = textObject.AddComponent<TextMeshProUGUI>();
        _headsetPromptText.alignment = TextAlignmentOptions.Center;
        _headsetPromptText.enableWordWrapping = true;
        _headsetPromptText.fontSize = screenSpacePromptFontSize;
        _headsetPromptText.color = Color.white;
        _headsetPromptText.richText = true;
        _headsetPromptText.raycastTarget = false;

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(36f, 24f);
        textRect.offsetMax = new Vector2(-36f, -24f);
    }

    private void SetHeadsetPromptVisible(bool visible)
    {
        if (_headsetPromptCanvas != null && _headsetPromptCanvas.gameObject.activeSelf != visible)
            _headsetPromptCanvas.gameObject.SetActive(visible);
    }

    private void ReactivateHeadsetPrompt()
    {
        SetHeadsetPromptVisible(true);
        SetMeshPromptVisible(true);
        SetMeshPromptsVisible(true);
    }

    private void UpdateMeshPrompt(Transform anchor, string message)
    {
        if (anchor == null)
        {
            SetMeshPromptVisible(false);
            return;
        }

        EnsureMeshPrompt(anchor);
        SetMeshPromptVisible(true);

        _meshPromptRoot.transform.localPosition = GetMeshPromptOffset(anchor);
        _meshPromptRoot.transform.localRotation = Quaternion.identity;
        ApplyMeshPromptLayout(_meshPromptRoot.transform, _meshPromptBackground, _meshPromptText, _meshPromptTextBack);

        _meshPromptText.text = message;
        _meshPromptTextBack.text = message;
        _meshPromptText.ForceMeshUpdate();
        _meshPromptTextBack.ForceMeshUpdate();
        RefreshPromptText(_meshPromptText);
        RefreshPromptText(_meshPromptTextBack);
    }

    private void EnsureMeshPrompt(Transform anchor)
    {
        if (_meshPromptRoot == null)
            CreateMeshPrompt();

        if (_meshPromptRoot.transform.parent != anchor)
            _meshPromptRoot.transform.SetParent(anchor, false);
    }

    private void CreateMeshPrompt()
    {
        _meshPromptRoot = new GameObject("Meta Quest Calibration Prompt Mesh");
        _meshPromptRoot.layer = 0;

        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        background.layer = 0;
        background.transform.SetParent(_meshPromptRoot.transform, false);
        background.transform.localPosition = new Vector3(0f, 0f, 0.05f);
        background.transform.localRotation = Quaternion.identity;
        _meshPromptBackground = background.transform;
        Collider backgroundCollider = background.GetComponent<Collider>();
        if (backgroundCollider != null) Destroy(backgroundCollider);
        Renderer backgroundRenderer = background.GetComponent<Renderer>();
        Shader backgroundShader = Shader.Find("Unlit/Color");
        if (backgroundShader == null) backgroundShader = Shader.Find("Standard");
        Material backgroundMaterial = new Material(backgroundShader);
        backgroundMaterial.color = new Color(0f, 0f, 0f, 0.92f);
        backgroundRenderer.material = backgroundMaterial;

        _meshPromptText = CreateMeshPromptText("Text", Quaternion.identity, -0.02f);
        _meshPromptTextBack = CreateMeshPromptText("Text Back", Quaternion.Euler(0f, 180f, 0f), 0.08f);
        ApplyMeshPromptLayout(_meshPromptRoot.transform, _meshPromptBackground, _meshPromptText, _meshPromptTextBack);
    }

    private TextMeshPro CreateMeshPromptText(string name, Quaternion localRotation, float localZ)
    {
        GameObject textObject = new GameObject(name);
        textObject.layer = 0;
        textObject.transform.SetParent(_meshPromptRoot.transform, false);
        textObject.transform.localPosition = new Vector3(0f, 0f, localZ);
        textObject.transform.localRotation = localRotation;
        textObject.transform.localScale = Vector3.one;

        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = true;
        text.fontSize = meshPromptFontSize;
        text.color = Color.white;
        text.rectTransform.sizeDelta = meshPromptTextSize;
        text.richText = true;
        Renderer renderer = text.GetComponent<Renderer>();
        if (renderer != null) renderer.sortingOrder = 5001;
        return text;
    }

    private Vector3 GetMeshPromptOffset(Transform anchor)
    {
        Vector3 offset = headsetPromptOffset;
        float minimumDistance = headsetPromptMinimumDistance;
        Camera anchorCamera = anchor != null ? anchor.GetComponent<Camera>() : null;
        if (anchorCamera != null)
            minimumDistance = Mathf.Max(minimumDistance, anchorCamera.nearClipPlane + 0.02f);

        offset.z = Mathf.Max(offset.z, minimumDistance);
        return offset;
    }

    private void ApplyMeshPromptLayout(Transform root, Transform background, TextMeshPro frontText, TextMeshPro backText)
    {
        if (root != null)
            root.localScale = Vector3.one * Mathf.Max(0.001f, meshPromptScale);

        if (background != null)
        {
            float width = Mathf.Max(1f, meshPromptPanelSize.x);
            float height = Mathf.Max(1f, meshPromptPanelSize.y);
            background.localScale = new Vector3(width, height, 1f);
        }

        ApplyMeshPromptTextLayout(frontText);
        ApplyMeshPromptTextLayout(backText);
    }

    private void ApplyMeshPromptTextLayout(TextMeshPro text)
    {
        if (text == null) return;

        text.fontSize = Mathf.Max(1f, meshPromptFontSize);
        float width = Mathf.Max(1f, meshPromptTextSize.x);
        float height = Mathf.Max(1f, meshPromptTextSize.y);
        text.rectTransform.sizeDelta = new Vector2(width, height);
    }

    private void SetMeshPromptVisible(bool visible)
    {
        if (_meshPromptRoot != null && _meshPromptRoot.activeSelf != visible)
            _meshPromptRoot.SetActive(visible);
    }

    private void UpdateMeshPrompts(string message)
    {
        List<Transform> anchors = GetHeadsetPromptAnchors();
        if (anchors.Count == 0)
        {
            SetMeshPromptsVisible(false);
            return;
        }

        foreach (MeshPromptInstance prompt in _meshPromptInstances)
        {
            bool stillUsed = anchors.Contains(prompt.anchor);
            if (prompt.root != null && prompt.root.activeSelf != stillUsed)
                prompt.root.SetActive(stillUsed);
        }

        foreach (Transform anchor in anchors)
        {
            UpdateMeshPromptInstance(anchor, 0, message);
            UpdateMeshPromptInstance(anchor, 5, message);
        }
    }

    private List<Transform> GetHeadsetPromptAnchors()
    {
        List<Transform> anchors = new List<Transform>();
        AddPromptAnchor(anchors, headsetPromptAnchor);

        OVRCameraRig rig = ResolveHeadsetPromptRig();
        if (rig != null)
        {
            AddPromptAnchor(anchors, rig.centerEyeAnchor);
            AddPromptAnchor(anchors, FindChildByName(rig.transform, "CenterEyeAnchor"));
            AddPromptAnchor(anchors, FindChildByName(rig.transform, "LeftEyeAnchor"));
            AddPromptAnchor(anchors, FindChildByName(rig.transform, "RightEyeAnchor"));
        }

        if (headsetYaw != null && headsetYaw.cameraRig != null)
        {
            AddPromptAnchor(anchors, headsetYaw.cameraRig.centerEyeAnchor);
            AddPromptAnchor(anchors, FindChildByName(headsetYaw.cameraRig.transform, "CenterEyeAnchor"));
            AddPromptAnchor(anchors, FindChildByName(headsetYaw.cameraRig.transform, "LeftEyeAnchor"));
            AddPromptAnchor(anchors, FindChildByName(headsetYaw.cameraRig.transform, "RightEyeAnchor"));
        }

        foreach (Camera cam in Camera.allCameras)
        {
            if (cam != null && cam.enabled)
                AddPromptAnchor(anchors, cam.transform);
        }

        Camera mainCamera = Camera.main;
        if (mainCamera != null) AddPromptAnchor(anchors, mainCamera.transform);

        return anchors;
    }

    private static void AddPromptAnchor(List<Transform> anchors, Transform anchor)
    {
        if (anchor != null && !anchors.Contains(anchor))
            anchors.Add(anchor);
    }

    private static Transform FindChildByName(Transform root, string childName)
    {
        if (root == null) return null;
        if (root.name == childName) return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindChildByName(root.GetChild(i), childName);
            if (found != null) return found;
        }

        return null;
    }

    private void UpdateMeshPromptInstance(Transform anchor, int layer, string message)
    {
        MeshPromptInstance prompt = GetOrCreateMeshPromptInstance(anchor, layer);
        prompt.root.SetActive(true);
        prompt.root.transform.localPosition = GetMeshPromptOffset(anchor);
        prompt.root.transform.localRotation = Quaternion.identity;
        ApplyMeshPromptLayout(prompt.root.transform, prompt.background, prompt.frontText, prompt.backText);
        prompt.frontText.text = message;
        prompt.backText.text = message;
        prompt.frontText.ForceMeshUpdate();
        prompt.backText.ForceMeshUpdate();
        RefreshPromptText(prompt.frontText);
        RefreshPromptText(prompt.backText);
    }

    private MeshPromptInstance GetOrCreateMeshPromptInstance(Transform anchor, int layer)
    {
        foreach (MeshPromptInstance prompt in _meshPromptInstances)
        {
            if (prompt.anchor == anchor && prompt.layer == layer)
                return prompt;
        }

        MeshPromptInstance created = CreateMeshPromptInstance(anchor, layer);
        _meshPromptInstances.Add(created);
        return created;
    }

    private MeshPromptInstance CreateMeshPromptInstance(Transform anchor, int layer)
    {
        MeshPromptInstance prompt = new MeshPromptInstance
        {
            anchor = anchor,
            layer = layer,
            root = new GameObject($"Meta Quest Calibration Prompt Mesh ({anchor.name}, layer {layer})")
        };
        prompt.root.layer = layer;
        prompt.root.transform.SetParent(anchor, false);

        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        background.layer = layer;
        background.transform.SetParent(prompt.root.transform, false);
        background.transform.localPosition = new Vector3(0f, 0f, 0.06f);
        background.transform.localRotation = Quaternion.identity;
        prompt.background = background.transform;
        Collider backgroundCollider = background.GetComponent<Collider>();
        if (backgroundCollider != null) Destroy(backgroundCollider);
        Renderer backgroundRenderer = background.GetComponent<Renderer>();
        Shader backgroundShader = Shader.Find("Unlit/Color");
        if (backgroundShader == null) backgroundShader = Shader.Find("Standard");
        Material backgroundMaterial = new Material(backgroundShader);
        backgroundMaterial.color = new Color(0f, 0f, 0f, 0.92f);
        backgroundRenderer.material = backgroundMaterial;

        prompt.frontText = CreateMeshPromptText(prompt.root.transform, "Text", layer, Quaternion.identity, -0.02f);
        prompt.backText = CreateMeshPromptText(prompt.root.transform, "Text Back", layer, Quaternion.Euler(0f, 180f, 0f), 0.10f);
        ApplyMeshPromptLayout(prompt.root.transform, prompt.background, prompt.frontText, prompt.backText);
        return prompt;
    }

    private TextMeshPro CreateMeshPromptText(Transform parent, string name, int layer, Quaternion localRotation, float localZ)
    {
        GameObject textObject = new GameObject(name);
        textObject.layer = layer;
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = new Vector3(0f, 0f, localZ);
        textObject.transform.localRotation = localRotation;
        textObject.transform.localScale = Vector3.one;

        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = true;
        text.fontSize = meshPromptFontSize;
        text.color = Color.white;
        text.rectTransform.sizeDelta = meshPromptTextSize;
        text.richText = true;
        Renderer renderer = text.GetComponent<Renderer>();
        if (renderer != null) renderer.sortingOrder = 5001;
        return text;
    }

    private void SetMeshPromptsVisible(bool visible)
    {
        foreach (MeshPromptInstance prompt in _meshPromptInstances)
        {
            if (prompt.root != null && prompt.root.activeSelf != visible)
                prompt.root.SetActive(visible);
        }
    }

    private string BuildPromptText()
    {
        if (!IsRunning) return _screenMessage;

        return
            $"<size=125%><b>Input Calibration</b></size>\n" +
            $"<size=95%>Step: {Prompts[_step]}</size>\n" +
            $"<size=125%>{GetPromptPhaseText()}</size>\n" +
            $"<size=75%>{advanceKey} = skip phase    {cancelKey} = cancel</size>";
    }

    private string GetPromptPhaseText()
    {
        return _isGettingReady
            ? $"Get ready - capturing in {_countdown:F1}s"
            : $"Hold steady - capture in {_countdown:F1}s";
    }

    private void RefreshPromptText(TMP_Text text)
    {
        if (text == null) return;

        text.ForceMeshUpdate();
        if (!IsRunning) return;

        TMP_TextInfo textInfo = text.textInfo;
        string phaseText = GetPromptPhaseText();
        string displayedText = "";
        for (int i = 0; i < textInfo.characterCount; i++)
            displayedText += textInfo.characterInfo[i].character;

        int phaseStart = displayedText.IndexOf(phaseText, System.StringComparison.Ordinal);
        int phaseEnd = phaseStart + phaseText.Length;
        Color32 normalColor = Color.white;
        Color32 phaseColor = _isGettingReady ? Color.cyan : Color.yellow;

        for (int characterIndex = 0; characterIndex < textInfo.characterCount; characterIndex++)
        {
            TMP_CharacterInfo character = textInfo.characterInfo[characterIndex];
            if (!character.isVisible) continue;

            Color32 color = phaseStart >= 0 && characterIndex >= phaseStart && characterIndex < phaseEnd
                ? phaseColor
                : normalColor;
            Color32[] vertexColors = textInfo.meshInfo[character.materialReferenceIndex].colors32;
            int vertexIndex = character.vertexIndex;
            vertexColors[vertexIndex] = color;
            vertexColors[vertexIndex + 1] = color;
            vertexColors[vertexIndex + 2] = color;
            vertexColors[vertexIndex + 3] = color;
        }

        text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }

    // ============================================
    // ON-SCREEN PROMPT
    // ============================================

    void OnGUI()
    {
        if (!Application.isPlaying) return;

        DrawScreenMessage();

        if (!IsRunning) return;

        GUI.color = Color.white;
        GUILayout.BeginArea(new Rect(Screen.width / 2 - 320, Screen.height / 2 - 80, 640, 160), GUI.skin.box);
        GUILayout.Label($"<size=22><b>Input Calibration</b></size>");
        GUILayout.Label($"<size=18>Step: {Prompts[_step]}</size>");
        if (_isGettingReady)
            GUILayout.Label($"<size=24><color=cyan>Get ready — capturing in {_countdown:F1}s</color></size>");
        else
            GUILayout.Label($"<size=24><color=yellow>Hold steady — capture in {_countdown:F1}s</color></size>");
        GUILayout.Label($"<size=14>{advanceKey} = skip phase    {cancelKey} = cancel</size>");
        GUILayout.EndArea();
    }

    private void DrawScreenMessage()
    {
        if (string.IsNullOrEmpty(_screenMessage) || Time.unscaledTime > _screenMessageUntil)
            return;

        GUIStyle style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 22,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };
        Rect rect = new Rect(Screen.width * 0.5f - 300f, 28f, 600f, 82f);
        GUI.Box(rect, _screenMessage, style);
    }
}
