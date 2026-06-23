using UnityEngine;

/// <summary>
/// Reads two forearm IMUs relative to the chest IMU to derive swarm spread and height.
/// Height  = average relative pitch of both arms (arms up = swarm rises)
/// Spread  = yaw angular difference between arms (arms apart = swarm expands)
///
/// The chest IMU is used as the reference frame via quaternion math, NOT Euler subtraction.
/// This means tilting the chest forward/backward has zero effect on height or spread readings,
/// even for large angles or combined rotations.
///
/// Spread modes:
///   Absolute  — arm angle directly maps to target separation in meters (hold arm = hold spread)
///   RateBased — arm angle maps to rate of change (-1..+1), release to neutral = hold spread
/// </summary>
public enum SpreadControlMode { Absolute, RateBased }

public class ArmIMUSpreadHeightInput : MonoBehaviour
{
    // ============================================
    // IMU REFERENCES
    // ============================================
    [Header("IMU Sources")]
    [Tooltip("Chest IMU — used as the reference frame for both arm sensors")]
    public OpenZenMoveObject chestIMU;

    [Tooltip("Left forearm IMU")]
    public OpenZenMoveObject leftArmIMU;

    [Tooltip("Right forearm IMU")]
    public OpenZenMoveObject rightArmIMU;

    // ============================================
    // HEIGHT SETTINGS
    // ============================================
    [Header("Height Mapping")]
    [Tooltip("Arm pitch angle (degrees) that produces maximum height output (+1 or -1)")]
    public float pitchMaxAngle = 60f;

    [Tooltip("Ignore pitch angles smaller than this (degrees)")]
    public float pitchDeadzone = 5f;

    [Tooltip("Response curve exponent for height (1 = linear, 2 = squared)")]
    [Range(1f, 3f)]
    public float pitchResponseCurve = 1.5f;

    [Tooltip("Height smoothing factor (0 = instant, 1 = maximum smoothing)")]
    [Range(0f, 0.95f)]
    public float heightSmoothing = 0.3f;

    // ============================================
    // SPREAD SETTINGS
    // ============================================
    [Header("Spread Mapping")]
    [Tooltip("Absolute: arm angle maps to target meters. RateBased: arm angle maps to rate of change (-1..+1).")]
    public SpreadControlMode spreadMode = SpreadControlMode.RateBased;

    [Tooltip(
        "PitchDiff — left arm up / right arm down (orthogonal to height by construction)\n" +
        "Y         — arms swing sideways (shoulder abduction, yaw twist)\n" +
        "Z         — forearms twist outward (roll twist)\n" +
        "Check the debug overlay: perform your spread gesture and see which row changes.")]
    public enum SpreadAxis { PitchDiff, Y, Z, X }
    public SpreadAxis spreadAxis = SpreadAxis.Y;

    [Tooltip("Yaw angle difference (degrees) between arms from neutral that maps to maximum spread rate (+1.0). Arms neutral-forward = 0 rate. Arms wide apart = max expand. Arms crossed = max shrink.")]
    public float yawMaxSpreadAngle = 90f;

    [Tooltip("Ignore yaw differences smaller than this (degrees)")]
    public float yawDeadzone = 5f;

    [Tooltip("Response curve exponent for spread (1 = linear, 2 = squared)")]
    [Range(1f, 3f)]
    public float spreadResponseCurve = 1.5f;

    [Tooltip("Spread smoothing factor (0 = instant, 1 = maximum smoothing)")]
    [Range(0f, 0.95f)]
    public float spreadSmoothing = 0.3f;

    [Header("Spread → Separation Mapping")]
    [Tooltip("Swarm separation (meters) when spread = 0")]
    public float minSwarmSeparation = 1.0f;

    [Tooltip("Swarm separation (meters) when spread = 1")]
    public float maxSwarmSeparation = 10.0f;

    // ============================================
    // CALIBRATION
    // ============================================
    [Header("Calibration")]
    [Tooltip("Press this key to set current arm pose as neutral")]
    public KeyCode calibrateKey = KeyCode.C;

    [Tooltip("Automatically calibrate neutral on start (after a few frames)")]
    public bool autoCalibrateOnStart = true;

    // Neutral relative rotations captured at calibration time.
    // Each stores: Inverse(chest) * arm, so the reference pose is "arm relative to chest when calibrated".
    private Quaternion _leftNeutralRel  = Quaternion.identity;
    private Quaternion _rightNeutralRel = Quaternion.identity;
    private bool _calibrated   = false;
    private bool _initialized  = false;

    // ============================================
    // SMOOTHING STATE
    // ============================================
    private float _smoothedHeight = 0f;
    private float _smoothedSpread = 0f;

    // ============================================
    // OUTPUT PROPERTIES
    // ============================================

    /// <summary>
    /// Height control rate: -1 = arms fully down, +1 = arms fully up.
    /// </summary>
    public float HeightControl { get; private set; }

    /// <summary>
    /// Absolute swarm separation target in meters, mapped from arm spread angle.
    /// </summary>
    public float SpreadControl { get; private set; }

    /// <summary>
    /// Returns true when all three IMUs are assigned AND have received at least one data packet.
    /// </summary>
    public bool IsAvailable => chestIMU != null && leftArmIMU != null && rightArmIMU != null
        && chestIMU.IsConnected && leftArmIMU.IsConnected && rightArmIMU.IsConnected;

    /// <summary>
    /// True when spread outputs absolute meters, false when rate-based (-1..+1).
    /// </summary>
    public bool IsAbsoluteMode => spreadMode == SpreadControlMode.Absolute;

    // ============================================
    // UPDATE LOOP
    // ============================================

    void Update()
    {
        // Auto-calibrate only once all three sensors have received real data.
        // Checking IsAvailable (which requires IsConnected) prevents calibrating
        // against Quaternion.identity when sensors haven't connected yet.
        if (!_initialized && autoCalibrateOnStart && IsAvailable)
        {
            CalibrateNeutral();
            _initialized = true;
        }

        if (Input.GetKeyDown(calibrateKey))
        {
            CalibrateNeutral();
        }

        if (!IsAvailable)
        {
            HeightControl = 0f;
            SpreadControl = minSwarmSeparation;
            return;
        }

        // Current relative orientation of each arm in the chest's frame.
        // Quaternion math removes ALL chest motion (pitch/yaw/roll) correctly,
        // unlike Euler subtraction which bleeds axes into each other.
        Quaternion chestRot = chestIMU.SensorOrientation;
        Quaternion leftRelNow  = Quaternion.Inverse(chestRot) * leftArmIMU.SensorOrientation;
        Quaternion rightRelNow = Quaternion.Inverse(chestRot) * rightArmIMU.SensorOrientation;

        // Delta from the neutral pose captured at calibration.
        // At calibration: deltaLeft = Identity → zero pitch, zero yaw.
        Quaternion leftDelta  = Quaternion.Inverse(_leftNeutralRel)  * leftRelNow;
        Quaternion rightDelta = Quaternion.Inverse(_rightNeutralRel) * rightRelNow;

        Vector3 leftEuler  = leftDelta.eulerAngles;
        Vector3 rightEuler = rightDelta.eulerAngles;

        float leftRelPitch  = NormalizeAngle(leftEuler.x);
        float rightRelPitch = NormalizeAngle(rightEuler.x);

        // Swing-twist decomposition: isolate the Y-axis (yaw) component of each
        // delta quaternion. Unlike eulerAngles.y or horizontal projection, this is
        // mathematically immune to pitch and roll regardless of rotation order —
        // so raising the arms for height no longer bleeds into spread.
        // PitchDiff: spread = |leftPitch - rightPitch|
        // Orthogonal to height (height = avg pitch, spread = pitch difference).
        // One arm up + other arm down = max spread; both arms same = zero spread.
        if (spreadAxis == SpreadAxis.PitchDiff)
        {
            ComputeHeight(leftRelPitch, rightRelPitch);
            ComputeSpread(leftRelPitch, rightRelPitch);  // reuse pitch values; ComputeSpread takes abs diff
        }
        else
        {
            float leftRelYaw  = ExtractAxisTwist(leftDelta,  spreadAxis);
            float rightRelYaw = ExtractAxisTwist(rightDelta, spreadAxis);
            ComputeHeight(leftRelPitch, rightRelPitch);
            ComputeSpread(leftRelYaw, rightRelYaw);
        }
    }

    // ============================================
    // COMPUTATION
    // ============================================

    void ComputeHeight(float leftPitch, float rightPitch)
    {
        float avgPitch = (leftPitch + rightPitch) * 0.5f;

        float normalized = InputCurves.ToSignedRateAfterDeadzone(avgPitch, pitchMaxAngle, pitchDeadzone);
        float sign = Mathf.Sign(normalized);
        float curved = Mathf.Pow(Mathf.Abs(normalized), pitchResponseCurve) * sign;

        float smoothSpeed = 1f - heightSmoothing;
        _smoothedHeight = Mathf.Lerp(_smoothedHeight, curved, smoothSpeed * Time.deltaTime * 10f);
        HeightControl = _smoothedHeight;
    }

    void ComputeSpread(float leftYaw, float rightYaw)
    {
        float smoothSpeed = 1f - spreadSmoothing;

        if (spreadMode == SpreadControlMode.Absolute)
        {
            // |yaw difference| maps to target separation.
            // Both arms spread outward → yaws diverge (one goes +, other −).
            float yawDiff = Mathf.Abs(leftYaw - rightYaw);
            if (yawDiff < yawDeadzone) yawDiff = 0f;

            float normalized = Mathf.Clamp01(yawDiff / yawMaxSpreadAngle);
            float curved = Mathf.Pow(normalized, spreadResponseCurve);

            _smoothedSpread = Mathf.Lerp(_smoothedSpread, curved, smoothSpeed * Time.deltaTime * 10f);
            SpreadControl = Mathf.Lerp(minSwarmSeparation, maxSwarmSeparation, _smoothedSpread);
        }
        else
        {
            // Rate-based: rightYaw - leftYaw → rate of change (-1..+1).
            // Neutral (arms forward): both yaws ≈ 0 → rate = 0 → hold spread.
            // Arms swinging wide (left goes −, right goes +): rightYaw - leftYaw is large positive → expand.
            // Arms pulled inward / crossed (left goes +, right goes −): negative → contract.
            float signedDiff = rightYaw - leftYaw;
            if (Mathf.Abs(signedDiff) < yawDeadzone) signedDiff = 0f;

            float signedNorm = Mathf.Clamp(signedDiff / yawMaxSpreadAngle, -1f, 1f);
            float sign = Mathf.Sign(signedNorm);
            float rateCurved = Mathf.Pow(Mathf.Abs(signedNorm), spreadResponseCurve) * sign;

            _smoothedSpread = Mathf.Lerp(_smoothedSpread, rateCurved, smoothSpeed * Time.deltaTime * 10f);
            SpreadControl = _smoothedSpread;
        }
    }

    // ============================================
    // CALIBRATION
    // ============================================

    /// <summary>
    /// Captures the current arm pose as neutral.
    /// After calibration, any chest motion is fully cancelled — only arm motion
    /// relative to the chest at this moment will produce height/spread output.
    /// </summary>
    public void CalibrateNeutral()
    {
        if (!IsAvailable)
        {
            Debug.LogWarning("ArmIMUSpreadHeightInput: Cannot calibrate — one or more IMUs not assigned.");
            return;
        }

        Quaternion chestRot = chestIMU.SensorOrientation;
        _leftNeutralRel  = Quaternion.Inverse(chestRot) * leftArmIMU.SensorOrientation;
        _rightNeutralRel = Quaternion.Inverse(chestRot) * rightArmIMU.SensorOrientation;
        _calibrated = true;

        Debug.Log("ArmIMU Calibrated (quaternion reference stored).");
    }

    // ============================================
    // HELPERS
    // ============================================

    /// <summary>
    /// Extracts the rotation around a chosen axis via swing-twist decomposition.
    /// Independent of the other two axes — no cross-axis bleed.
    /// </summary>
    float ExtractAxisTwist(Quaternion q, SpreadAxis axis)
    {
        float component;
        switch (axis)
        {
            case SpreadAxis.X: component = q.x; break;
            case SpreadAxis.Z: component = q.z; break;
            default:           component = q.y; break; // Y
        }
        float magnitude = Mathf.Sqrt(component * component + q.w * q.w);
        if (magnitude < 1e-6f) return 0f;
        float twist     = component / magnitude;
        float twistW    = q.w       / magnitude;
        return 2f * Mathf.Atan2(twist, twistW) * Mathf.Rad2Deg;
    }

    float NormalizeAngle(float angle)
    {
        if (angle > 180f)  return angle - 360f;
        if (angle < -180f) return angle + 360f;
        return angle;
    }

    // ============================================
    // DEBUG
    // ============================================

    [Header("Debug")]
    [Tooltip("Show the on-screen sensor status panel during play. Off by default to keep the screen clean during runs.")]
    public bool showDebugInfo = false;

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 580, 340, 160));
        GUILayout.Label("<b>Arm IMU Spread/Height</b>");
        bool chestOk = chestIMU != null && chestIMU.IsConnected;
        bool leftOk  = leftArmIMU != null && leftArmIMU.IsConnected;
        bool rightOk = rightArmIMU != null && rightArmIMU.IsConnected;
        GUILayout.Label($"Sensors: Chest={chestOk} L={leftOk} R={rightOk}  Calibrated={_calibrated}");

        if (IsAvailable)
        {
            Quaternion chestRot   = chestIMU.SensorOrientation;
            Quaternion leftDelta  = Quaternion.Inverse(_leftNeutralRel)  * (Quaternion.Inverse(chestRot) * leftArmIMU.SensorOrientation);
            Quaternion rightDelta = Quaternion.Inverse(_rightNeutralRel) * (Quaternion.Inverse(chestRot) * rightArmIMU.SensorOrientation);

            // All 3 Euler axes — lets us see which axis arm-spreading actually moves
            float lx = NormalizeAngle(leftDelta.eulerAngles.x);
            float ly = NormalizeAngle(leftDelta.eulerAngles.y);
            float lz = NormalizeAngle(leftDelta.eulerAngles.z);
            float rx = NormalizeAngle(rightDelta.eulerAngles.x);
            float ry = NormalizeAngle(rightDelta.eulerAngles.y);
            float rz = NormalizeAngle(rightDelta.eulerAngles.z);

            float lyTwist = ExtractAxisTwist(leftDelta,  SpreadAxis.Y);
            float ryTwist = ExtractAxisTwist(rightDelta, SpreadAxis.Y);

            GUILayout.Label($"Calibrated: {_calibrated}");
            GUILayout.Label($"Left  delta  X:{lx:F1}°  Y:{ly:F1}°  Z:{lz:F1}°");
            GUILayout.Label($"Right delta  X:{rx:F1}°  Y:{ry:F1}°  Z:{rz:F1}°");
            float lyTwistZ = ExtractAxisTwist(leftDelta,  SpreadAxis.Z);
            float ryTwistZ = ExtractAxisTwist(rightDelta, SpreadAxis.Z);
            GUILayout.Label($"Twist Y  L:{lyTwist:F1}°  R:{ryTwist:F1}°  Diff:{Mathf.Abs(lyTwist - ryTwist):F1}°");
            GUILayout.Label($"Twist Z  L:{lyTwistZ:F1}°  R:{ryTwistZ:F1}°  Diff:{Mathf.Abs(lyTwistZ - ryTwistZ):F1}°");
            GUILayout.Label($"SpreadAxis: {spreadAxis}");
            GUILayout.Label($"Height Output: {HeightControl:F3}");
            GUILayout.Label($"Spread Output: {SpreadControl:F2}" + (spreadMode == SpreadControlMode.Absolute ? "m" : " rate"));
        }

        GUILayout.EndArea();
    }
}
