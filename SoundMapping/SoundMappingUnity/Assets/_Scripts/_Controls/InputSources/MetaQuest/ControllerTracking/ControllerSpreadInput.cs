using UnityEngine;

/// <summary>
/// Spread control via Meta Quest Touch controllers, in either rate or absolute mode.
/// Distance between left/right controller anchors is mapped using captured
/// min/neutral/max bounds (set by the V-key calibration flow):
///   • Rate mode    → -1..+1 (closer than neutral = contract, farther = expand)
///   • Absolute mode → target swarm separation in meters
///
/// Sibling of HandSpreadInput. Reads OVRCameraRig anchors and gates on
/// OVRInput.IsControllerConnected for both controllers.
/// </summary>
public class ControllerSpreadInput : MonoBehaviour
{
    [Header("OVR References")]
    [Tooltip("OVRCameraRig in the scene — auto-found if left empty")]
    public OVRCameraRig cameraRig;

    [Header("Output Mode")]
    [Tooltip("Rate (-1..+1) or Absolute (target meters). Switchable per-trial.")]
    public SpreadMode mode = SpreadMode.Absolute;

    [Header("Calibrated Bounds (meters between controllers)")]
    [Tooltip("Inter-controller distance at MIN spread. Set by calibration.")]
    public float minDistance = 0.1f;

    [Tooltip("Inter-controller distance at NEUTRAL (rate-mode deadzone center). Set by calibration.")]
    public float neutralDistance = 0.35f;

    [Tooltip("Inter-controller distance at MAX spread. Set by calibration.")]
    public float maxDistance = 0.8f;

    [Header("Absolute Mode — Target Range (meters between drones)")]
    [Tooltip("Swarm separation when distance == minDistance.")]
    public float minSwarmSeparation = 1.0f;

    [Tooltip("Swarm separation when distance == maxDistance.")]
    public float maxSwarmSeparation = 5.0f;

    [Header("Response (rate mode only)")]
    [Tooltip("Linear or exponential mapping from delta-from-neutral to rate.")]
    public ResponseCurve responseCurve = ResponseCurve.Linear;

    [Tooltip("Exponent used when responseCurve == Exponential.")]
    [Range(1f, 3f)]
    public float curveExponent = 2.0f;

    [Header("Deadzone (rate mode only)")]
    [Tooltip("Half-width of the no-change band around neutral (meters)")]
    [Range(0f, 0.2f)]
    public float deadzone = 0.1f;

    [Header("Smoothing")]
    [Tooltip("SmoothDamp time on output. 0 = no smoothing.")]
    [Range(0f, 0.5f)]
    public float smoothTime = 0.1f;

    [Header("Local Test Calibration")]
    [Tooltip("Optional standalone re-center key — official flow is V (multi-step).")]
    public KeyCode calibrateKey = KeyCode.K;

    [Header("Debug")]
    public bool showDebugInfo = false;

    // ============================================
    // PUBLIC OUTPUTS (read by InputFusionManager)
    // ============================================

    /// <summary>
    /// Spread output. Meaning depends on mode:
    ///   • Rate     → -1..+1 rate (use IsAbsoluteMode == false)
    ///   • Absolute → target swarm separation in meters (use IsAbsoluteMode == true)
    /// </summary>
    public float SpreadControl { get; private set; }

    /// <summary>True when the source is currently in Absolute mode.</summary>
    public bool IsAbsoluteMode => mode == SpreadMode.Absolute;

    /// <summary>Current measured distance between the two controller anchors (meters).</summary>
    public float CurrentDistance { get; private set; }

    /// <summary>True when both Touch controllers are connected and anchors are valid.</summary>
    public bool IsAvailable
    {
        get
        {
            if (cameraRig == null
                || cameraRig.leftHandAnchor == null
                || cameraRig.rightHandAnchor == null) return false;
            return OVRInput.IsControllerConnected(OVRInput.Controller.LTouch)
                && OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
        }
    }

    // ============================================
    // PRIVATE STATE
    // ============================================

    private float _smoothed = 0f;
    private float _smoothVelocity = 0f;

    void Start()
    {
        if (cameraRig == null) cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null)
            Debug.LogWarning("ControllerSpreadInput: OVRCameraRig not found — assign it in the Inspector.");
    }

    void Update()
    {
        if (Input.GetKeyDown(calibrateKey)) CaptureNeutral();

        if (!IsAvailable)
        {
            SpreadControl = 0f;
            return;
        }

        CurrentDistance = GetCurrentDistance();
        float target;

        if (mode == SpreadMode.Absolute)
        {
            float t = InputCurves.ToAbsolute01(CurrentDistance, minDistance, maxDistance);
            target = Mathf.Lerp(minSwarmSeparation, maxSwarmSeparation, t);
        }
        else
        {
            float rawRate = InputCurves.ToRate(CurrentDistance, minDistance, neutralDistance, maxDistance, deadzone);
            target = InputCurves.ApplyCurve(rawRate, responseCurve, curveExponent);
        }

        if (smoothTime > 0f)
        {
            _smoothed = Mathf.SmoothDamp(_smoothed, target, ref _smoothVelocity, smoothTime);
            SpreadControl = _smoothed;
        }
        else
        {
            SpreadControl = target;
        }
    }

    /// <summary>
    /// Inter-controller distance. Public so the V-key calibration flow can read it
    /// without depending on Update() having run this frame.
    /// </summary>
    public float GetCurrentDistance()
    {
        if (!IsAvailable) return 0f;
        return Vector3.Distance(
            cameraRig.leftHandAnchor.position,
            cameraRig.rightHandAnchor.position);
    }

    // ============================================
    // CALIBRATION HOOKS — used by MetaQuestCalibrationFlow
    // ============================================

    public void CaptureMin()     { if (IsAvailable) minDistance     = GetCurrentDistance(); }
    public void CaptureNeutral() { if (IsAvailable) { neutralDistance = GetCurrentDistance(); _smoothed = mode == SpreadMode.Rate ? 0f : _smoothed; _smoothVelocity = 0f; } }
    public void CaptureMax()     { if (IsAvailable) maxDistance     = GetCurrentDistance(); }

    /// <summary>Legacy single-pose neutral capture preserved so existing button flows still work.</summary>
    public void CalibrateNeutral() => CaptureNeutral();

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;
        GUILayout.BeginArea(new Rect(10, 720, 380, 130));
        GUILayout.Label("<b>=== CONTROLLER SPREAD ===</b>");
        GUILayout.Label($"Available: {IsAvailable}  mode: {mode}");
        if (IsAvailable)
        {
            GUILayout.Label($"d: {CurrentDistance:F2}m  bounds [{minDistance:F2}, {neutralDistance:F2}, {maxDistance:F2}]");
            string label = mode == SpreadMode.Absolute ? $"Target: {SpreadControl:F2}m" : $"Rate: {SpreadControl:F2}";
            GUILayout.Label($"{label}  curve: {responseCurve}");
        }
        GUILayout.EndArea();
    }

    void OnDrawGizmos()
    {
        if (!showDebugInfo || !Application.isPlaying || !IsAvailable) return;
        Gizmos.color = mode == SpreadMode.Rate
            ? (Mathf.Abs(SpreadControl) > 0.01f ? Color.green : Color.yellow)
            : Color.cyan;
        Gizmos.DrawLine(cameraRig.leftHandAnchor.position, cameraRig.rightHandAnchor.position);
    }
}
