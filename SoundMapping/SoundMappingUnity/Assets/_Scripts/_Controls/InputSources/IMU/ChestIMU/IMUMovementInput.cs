using UnityEngine;

/// <summary>
/// Rate-based movement input from chest IMU.
/// Torso pitch tilt → forward/back speed; torso roll tilt → left/right speed.
/// Output is a normalized command vector in [-1, +1], matching joystick input.
/// InputFusionManager applies the final movement speed multipliers.
///
/// Single concrete class. Replaces the old abstract base + selector + Linear /
/// Exponential / RateBased modes — all variants were rate-based with different
/// curves, which collapses cleanly to one class with a configurable response curve.
/// </summary>
public class IMUMovementInput : MonoBehaviour
{
    [Header("IMU Source")]
    [Tooltip("Reference to the OpenZen chest IMU sensor")]
    public OpenZenMoveObject openZenIMU;

    [Header("Angle Mapping (degrees)")]
    [Tooltip("Pitch angle that produces maximum forward/backward speed")]
    public float pitchMaxAngle = 30f;

    [Tooltip("Roll angle that produces maximum left/right speed")]
    public float rollMaxAngle = 30f;

    [Header("V-Key Calibrated Directional Angles (degrees from neutral)")]
    [Tooltip("Use the per-direction angles captured by the guided V-key calibration flow.")]
    public bool useDirectionalAngleCalibration = false;

    [Tooltip("Roll angle captured at the participant's comfortable maximum forward lean.")]
    public float forwardRollAngle = 30f;

    [Tooltip("Roll angle captured at the participant's comfortable maximum backward lean.")]
    public float backwardRollAngle = -30f;

    [Tooltip("Pitch angle captured at the participant's comfortable maximum left lean.")]
    public float leftPitchAngle = -30f;

    [Tooltip("Pitch angle captured at the participant's comfortable maximum right lean.")]
    public float rightPitchAngle = 30f;

    [Header("Legacy Speed Multipliers")]
    [Tooltip("Legacy field kept for scene compatibility. IMU movement now outputs normalized commands; InputFusionManager applies speed.")]
    public float maxPitchSpeed = 4f;

    [Tooltip("Legacy field kept for scene compatibility. IMU movement now outputs normalized commands; InputFusionManager applies speed.")]
    public float maxRollSpeed = 4f;

    [Header("Response Curve")]
    [Tooltip("Curve exponent. 1 = linear, 2 = squared (precise near center, fast at extremes), 3 = cubic.")]
    [Range(1f, 3f)]
    public float responseCurve = 2f;

    [Header("Deadzones (degrees)")]
    public float pitchDeadzone = 5f;
    public float rollDeadzone = 5f;

    [Header("Inversion")]
    public bool invertPitch = false;
    public bool invertRoll = true;

    [Header("Smoothing")]
    [Tooltip("0 = instant, 1 = max smoothing. Higher = less jitter, more lag.")]
    [Range(0f, 1f)]
    public float smoothingFactor = 0.3f;

    [Header("Local Test Calibration")]
    [Tooltip("Optional standalone calibrate key — official flow is via InputFusionManager.PerformCalibration()")]
    public KeyCode calibrateKey = KeyCode.C;

    [Tooltip("Calibrate neutral on Start (rarely needed if OpenZenMoveObject already calibrates)")]
    public bool autoCalibrateOnStart = false;

    [Header("Debug")]
    public bool showDebugInfo = false;

    // ============================================
    // PUBLIC OUTPUTS (read by InputFusionManager)
    // ============================================

    /// <summary>Normalized movement command. X = left/right, Y = 0, Z = forward/back, each axis in [-1, +1].</summary>
    public Vector3 MovementVector { get; private set; }

    /// <summary>True when the chest IMU sensor reference is assigned.</summary>
    public bool IsAvailable => openZenIMU != null;

    /// <summary>True when pitch is actively producing left/right movement — used by InputFusionManager to disable headset yaw rotation when tilting.</summary>
    public bool IsPitchActive { get; private set; }

    // ============================================
    // PRIVATE STATE
    // ============================================

    private Vector3 _calibrationOffset = Vector3.zero;
    private bool _hasNeutralCalibration = false;
    private bool _initialized = false;
    private Vector3 _smoothedMovementVector = Vector3.zero;

    public Vector3 CalibrationOffset => _calibrationOffset;
    public bool HasNeutralCalibration => _hasNeutralCalibration;

    void Update()
    {
        if (!_initialized && IsAvailable && autoCalibrateOnStart)
        {
            CalibrateNeutral();
            _initialized = true;
        }

        if (Input.GetKeyDown(calibrateKey)) CalibrateNeutral();

        if (!IsAvailable)
        {
            MovementVector = Vector3.zero;
            _smoothedMovementVector = Vector3.zero;
            return;
        }

        Vector3 angles = openZenIMU.SensorEulerAnglesDirect;
        if (_hasNeutralCalibration)
            angles -= _calibrationOffset;

        Vector3 rawMovement = ConvertIMUToMovement(angles);

        float smoothSpeed = 1f - smoothingFactor;
        _smoothedMovementVector = Vector3.Lerp(_smoothedMovementVector, rawMovement, smoothSpeed * Time.deltaTime * 10f);
        MovementVector = _smoothedMovementVector;
    }

    Vector3 ConvertIMUToMovement(Vector3 eulerAngles)
    {
        // SWAPPED: Roll → forward/back; Pitch → left/right
        float pitch = NormalizeAngle(eulerAngles.x);
        float roll  = NormalizeAngle(eulerAngles.z);

        float forwardRate;
        float rightRate;

        if (useDirectionalAngleCalibration)
        {
            forwardRate = ToDirectionalRate(roll, backwardRollAngle, forwardRollAngle, rollDeadzone);
            rightRate   = ToDirectionalRate(pitch, leftPitchAngle, rightPitchAngle, pitchDeadzone);
        }
        else
        {
            forwardRate = SignedRateAfterDeadzone(roll, rollMaxAngle, rollDeadzone);
            rightRate   = SignedRateAfterDeadzone(pitch, pitchMaxAngle, pitchDeadzone);
        }

        float forward = ApplyResponseCurve(forwardRate);
        float right   = ApplyResponseCurve(rightRate);

        if (!useDirectionalAngleCalibration)
        {
            if (invertPitch) forward = -forward;
            if (invertRoll)  right   = -right;
        }

        IsPitchActive = Mathf.Abs(right) > 0.01f;

        return new Vector3(right, 0f, forward);
    }

    float ToDirectionalRate(float angle, float negativeExtreme, float positiveExtreme, float deadzone)
    {
        float positive = DirectionStrength(angle, positiveExtreme, deadzone);
        float negative = DirectionStrength(angle, negativeExtreme, deadzone);
        return positive >= negative ? positive : -negative;
    }

    float DirectionStrength(float angle, float extreme, float deadzone)
    {
        if (Mathf.Abs(extreme) < 0.001f) return 0f;
        if (angle * extreme <= 0f) return 0f;
        return RateAfterDeadzone(Mathf.Abs(angle), Mathf.Abs(extreme), deadzone);
    }

    float SignedRateAfterDeadzone(float angle, float maxAngle, float deadzone)
    {
        float sign = Mathf.Sign(angle);
        return RateAfterDeadzone(Mathf.Abs(angle), Mathf.Abs(maxAngle), deadzone) * sign;
    }

    float RateAfterDeadzone(float angleMagnitude, float maxMagnitude, float deadzone)
    {
        float safeDeadzone = Mathf.Max(0f, deadzone);
        if (angleMagnitude <= safeDeadzone) return 0f;

        float usableRange = maxMagnitude - safeDeadzone;
        if (usableRange <= 0.001f) return 0f;

        return Mathf.Clamp01((angleMagnitude - safeDeadzone) / usableRange);
    }

    float ApplyResponseCurve(float signedRate)
    {
        float sign = Mathf.Sign(signedRate);
        return Mathf.Pow(Mathf.Abs(signedRate), responseCurve) * sign;
    }

    static float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }

    /// <summary>Set current chest IMU orientation as the new neutral.</summary>
    public void CalibrateNeutral()
    {
        if (!IsAvailable)
        {
            Debug.LogWarning("IMUMovementInput: Cannot calibrate — IMU not available.");
            return;
        }
        _calibrationOffset = openZenIMU.SensorEulerAnglesDirect;
        _hasNeutralCalibration = true;
        _smoothedMovementVector = Vector3.zero;
        MovementVector = Vector3.zero;
        Debug.Log($"IMUMovementInput: calibrated. Offset = {_calibrationOffset}");
    }

    public void CaptureForwardMax()
    {
        if (!IsAvailable) return;
        forwardRollAngle = GetRawRollAngle();
        useDirectionalAngleCalibration = true;
        Debug.Log($"IMUMovementInput: captured FORWARD roll = {forwardRollAngle:F1}°");
    }

    public void CaptureBackwardMax()
    {
        if (!IsAvailable) return;
        backwardRollAngle = GetRawRollAngle();
        useDirectionalAngleCalibration = true;
        Debug.Log($"IMUMovementInput: captured BACKWARD roll = {backwardRollAngle:F1}°");
    }

    public void CaptureLeftMax()
    {
        if (!IsAvailable) return;
        leftPitchAngle = GetRawPitchAngle();
        useDirectionalAngleCalibration = true;
        Debug.Log($"IMUMovementInput: captured LEFT pitch = {leftPitchAngle:F1}°");
    }

    public void CaptureRightMax()
    {
        if (!IsAvailable) return;
        rightPitchAngle = GetRawPitchAngle();
        useDirectionalAngleCalibration = true;
        Debug.Log($"IMUMovementInput: captured RIGHT pitch = {rightPitchAngle:F1}°");
    }

    public void RestoreCalibration(
        Vector3 calibrationOffset,
        bool hasNeutralCalibration,
        bool useDirectionalCalibration,
        float forwardRoll,
        float backwardRoll,
        float leftPitch,
        float rightPitch)
    {
        _calibrationOffset = calibrationOffset;
        _hasNeutralCalibration = hasNeutralCalibration;
        useDirectionalAngleCalibration = useDirectionalCalibration;
        forwardRollAngle = forwardRoll;
        backwardRollAngle = backwardRoll;
        leftPitchAngle = leftPitch;
        rightPitchAngle = rightPitch;
        _smoothedMovementVector = Vector3.zero;
        MovementVector = Vector3.zero;
    }

    // ============================================
    // DEBUG HELPERS
    // ============================================

    public float GetRawPitchAngle() => IsAvailable
        ? NormalizeAngle(openZenIMU.SensorEulerAnglesDirect.x - _calibrationOffset.x)
        : 0f;

    public float GetRawRollAngle() => IsAvailable
        ? NormalizeAngle(openZenIMU.SensorEulerAnglesDirect.z - _calibrationOffset.z)
        : 0f;

    public float GetPitchAngle() => IsAvailable
        ? ApplyDeadzone(GetRawPitchAngle(), pitchDeadzone)
        : 0f;

    public float GetRollAngle()  => IsAvailable
        ? ApplyDeadzone(GetRawRollAngle(), rollDeadzone)
        : 0f;

    public float GetYawAngle()   => IsAvailable
        ? NormalizeAngle(openZenIMU.SensorEulerAnglesDirect.y - _calibrationOffset.y)
        : 0f;

    static float ApplyDeadzone(float angle, float deadzone)
    {
        return Mathf.Abs(angle) < deadzone ? 0f : angle;
    }

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying || !IsAvailable) return;
        GUILayout.BeginArea(new Rect(10, 840, 420, 130));
        GUILayout.Label("<b>=== IMU MOVEMENT (rate) ===</b>");
        GUILayout.Label($"Pitch: {GetPitchAngle():F1}°  Roll: {GetRollAngle():F1}°");
        if (useDirectionalAngleCalibration)
            GUILayout.Label($"Bounds F/B roll: {forwardRollAngle:F1}/{backwardRollAngle:F1}  L/R pitch: {leftPitchAngle:F1}/{rightPitchAngle:F1}");
        GUILayout.Label($"Output: {MovementVector}");
        GUILayout.Label($"Pitch active (yaw lock): {IsPitchActive}");
        GUILayout.EndArea();
    }
}
