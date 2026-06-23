using UnityEngine;

/// <summary>
/// Reads yaw (Y-axis rotation) from OpenZen IMU sensor for camera rotation control.
/// Uses the same chest sensor as IMUMovementInput but focuses on yaw instead of pitch/roll.
/// </summary>
public class IMUYawInput : MonoBehaviour
{
    [Header("IMU Source")]
    [Tooltip("Reference to the OpenZen IMU sensor (same sensor used for movement)")]
    public OpenZenMoveObject openZenIMU;

    [Header("Yaw Settings")]
    [Tooltip("Yaw angle (degrees) that produces maximum rotation speed")]
    public float yawMaxAngle = 30f;

    [Tooltip("Ignore yaw angles smaller than this (degrees)")]
    public float yawDeadzone = 5f;

    [Tooltip("Maximum rotation speed at full yaw tilt")]
    public float maxRotationSpeed = 2.0f;

    [Tooltip("Invert yaw direction (left becomes right)")]
    public bool invertYaw = false;

    [Header("Auto-Calibration")]
    [Tooltip("Automatically calibrate neutral position on Start (not needed - OpenZenMoveObject handles all calibration)")]
    public bool autoCalibrateOnStart = false;

    [Header("Debug")]
    public bool showDebugInfo = false;

    private Vector3 _calibrationOffset = Vector3.zero;
    private bool _initialized = false;

    // ============================================
    // OUTPUT PROPERTIES
    // ============================================

    /// <summary>
    /// Normalized yaw rotation rate (-1 to +1)
    /// Positive = turning right, Negative = turning left
    /// </summary>
    public float YawRotationRate { get; private set; }

    /// <summary>
    /// Returns true if IMU is available and providing data
    /// </summary>
    public bool IsAvailable => openZenIMU != null;

    // ============================================
    // UPDATE LOOP
    // ============================================

    void Update()
    {
        // Calibration is triggered by the global C key via
        // InputFusionManager.PerformCalibration() → CalibrateNeutral().
        // No local key binding here — single source of truth.

        if (IsAvailable)
        {
            // Subtract the calibrated neutral so the rotation output is anchored
            // to the participant's "facing-forward" pose at calibration time.
            Vector3 angles = openZenIMU.SensorEulerAnglesDirect - _calibrationOffset;

            YawRotationRate = ConvertYawToRotation(angles);
        }
        else
        {
            YawRotationRate = 0f;
        }
    }

    // ============================================
    // CONVERSION LOGIC
    // ============================================

    /// <summary>
    /// Converts IMU yaw angle to normalized rotation rate.
    /// Uses the same deadzone + max-angle pattern as IMUMovementInput's pitch/roll processing.
    /// </summary>
    float ConvertYawToRotation(Vector3 eulerAngles)
    {
        // Normalize angle to -180 to +180 range
        float yaw = NormalizeAngle(eulerAngles.y);

        if (showDebugInfo)
        {
            Debug.Log($"[IMU Yaw] Raw: {eulerAngles.y:F1}° | Normalized: {yaw:F1}° | Deadzone: {yawDeadzone}° | Passes? {Mathf.Abs(yaw) >= yawDeadzone}");
        }

        // Normalize after subtracting the deadzone from both the current yaw and max range.
        float yawRate = InputCurves.ToSignedRateAfterDeadzone(yaw, yawMaxAngle, yawDeadzone);
        float rotation = yawRate * maxRotationSpeed;
        if (invertYaw) rotation = -rotation;

        if (showDebugInfo)
        {
            Debug.Log($"[IMU Yaw] After deadzone: ({Mathf.Abs(yaw):F1}-{yawDeadzone:F1})/({yawMaxAngle:F1}-{yawDeadzone:F1}) = {Mathf.Abs(yawRate):F2} | Output: {rotation:F2}");
        }

        return rotation;
    }

    /// <summary>
    /// Normalizes angle from 0-360 range to -180 to +180 range
    /// </summary>
    float NormalizeAngle(float angle)
    {
        // Wrap angle to -180 to +180 range
        while (angle > 180f)
            angle -= 360f;
        while (angle < -180f)
            angle += 360f;
        return angle;
    }

    /// <summary>
    /// Calibrates the current IMU yaw as the neutral position
    /// Only calibrates yaw (Y), not pitch or roll (optional second-layer calibration)
    /// </summary>
    public void CalibrateNeutral()
    {
        if (IsAvailable)
        {
            // Only calibrate yaw (Y component), leave pitch/roll at 0
            // This is a second-layer calibration on top of OpenZenMoveObject
            Vector3 currentAngles = openZenIMU.SensorEulerAnglesDirect;
            _calibrationOffset = new Vector3(0f, currentAngles.y, 0f);
            Debug.Log($"IMU Yaw Second-Layer Calibrated. Yaw offset: {_calibrationOffset.y:F2}°");
        }
        else
        {
            Debug.LogWarning("Cannot calibrate: IMU not available");
        }
    }

    /// <summary>
    /// Returns current yaw angle after normalization and deadzone
    /// </summary>
    public float GetYawAngle()
    {
        if (!IsAvailable) return 0f;

        Vector3 calibratedAngles = openZenIMU.SensorEulerAnglesDirect - _calibrationOffset;
        float yaw = NormalizeAngle(calibratedAngles.y);
        if (Mathf.Abs(yaw) < yawDeadzone) yaw = 0f;
        return yaw;
    }

    // ============================================
    // DEBUG VISUALIZATION
    // ============================================

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 420, 300, 120));
        GUILayout.Label("<b>IMU Yaw Input</b>");
        GUILayout.Label($"Available: {IsAvailable}");
        if (IsAvailable)
        {
            GUILayout.Label($"Yaw Angle: {GetYawAngle():F1}°");
            GUILayout.Label($"Rotation Rate: {YawRotationRate:F2}");
        }
        GUILayout.EndArea();
    }
}
