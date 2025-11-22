using UnityEngine;

/// <summary>
/// Reads Meta Quest headset tracking data and exposes it as clean input values.
/// Uses delta/rate-based rotation for natural head-look camera control.
/// </summary>
public class MetaQuestInput : MonoBehaviour
{
    [Header("OVR References")]
    [Tooltip("Reference to the OVRCameraRig in your scene")]
    public OVRCameraRig cameraRig;

    [Header("Rotation Settings")]
    [Tooltip("Multiplier for head rotation sensitivity (1.0 = normal, 2.0 = twice as fast)")]
    [Range(0.1f, 5.0f)]
    public float rotationSensitivity = 1.0f;

    [Tooltip("Ignore head rotations smaller than this (degrees per second)")]
    [Range(0f, 10f)]
    public float yawDeadzone = 2f;

    [Tooltip("Maximum rotation rate value (degrees per second) - higher values = faster max turn")]
    public float maxRotationRate = 90f;

    [Header("Debug")]
    public bool showDebugInfo = false;

    // ============================================
    // PUBLIC OUTPUTS (Read by InputFusionManager)
    // ============================================
    
    /// <summary>
    /// Normalized yaw rotation rate from headset (-1 to +1, like joystick input)
    /// Positive = turning right, Negative = turning left
    /// </summary>
    public float HeadsetYawRate { get; private set; }

    /// <summary>
    /// Raw headset orientation (full quaternion)
    /// </summary>
    public Quaternion HeadsetOrientation { get; private set; }

    /// <summary>
    /// Current yaw angle (Y-axis rotation in degrees, 0-360)
    /// </summary>
    public float CurrentYaw { get; private set; }

    // ============================================
    // PRIVATE STATE
    // ============================================
    
    private float _previousYaw = 0f;
    private bool _initialized = false;

    // ============================================
    // INITIALIZATION
    // ============================================

    void Start()
    {
        ValidateReferences();
    }

    void ValidateReferences()
    {
        if (cameraRig == null)
        {
            // Try to find OVRCameraRig in scene
            cameraRig = FindObjectOfType<OVRCameraRig>();
            
            if (cameraRig == null)
            {
                Debug.LogWarning("MetaQuestInput: OVRCameraRig not found! Please assign it in the Inspector or ensure it exists in the scene.");
            }
            else
            {
                Debug.Log("MetaQuestInput: Automatically found OVRCameraRig in scene.");
            }
        }

        if (cameraRig != null && cameraRig.centerEyeAnchor != null)
        {
            Debug.Log("MetaQuestInput: Successfully connected to Meta Quest headset tracking.");
        }
    }

    // ============================================
    // UPDATE LOOP
    // ============================================

    void Update()
    {
        if (!IsHeadsetAvailable())
        {
            HeadsetYawRate = 0f;
            return;
        }

        UpdateHeadsetData();
        CalculateYawRate();
    }

    /// <summary>
    /// Check if headset tracking is available
    /// </summary>
    bool IsHeadsetAvailable()
    {
        return cameraRig != null && cameraRig.centerEyeAnchor != null;
    }

    /// <summary>
    /// Read current headset orientation and yaw
    /// </summary>
    void UpdateHeadsetData()
    {
        Transform centerEye = cameraRig.centerEyeAnchor;
        
        // Store full orientation
        HeadsetOrientation = centerEye.rotation;
        
        // Extract yaw (Y-axis rotation)
        CurrentYaw = centerEye.eulerAngles.y;

        // Initialize previous yaw on first frame
        if (!_initialized)
        {
            _previousYaw = CurrentYaw;
            _initialized = true;
        }
    }

    /// <summary>
    /// Calculate rate of yaw change (how fast user is turning head)
    /// </summary>
    void CalculateYawRate()
    {
        // Calculate angular difference (handles 360° wraparound correctly)
        float deltaYaw = Mathf.DeltaAngle(_previousYaw, CurrentYaw);
        
        // Convert to degrees per second
        float yawRateDegreesPerSec = deltaYaw / Time.deltaTime;

        // Apply deadzone (ignore small movements)
        if (Mathf.Abs(yawRateDegreesPerSec) < yawDeadzone)
        {
            yawRateDegreesPerSec = 0f;
        }

        // Apply sensitivity multiplier
        yawRateDegreesPerSec *= rotationSensitivity;

        // Normalize to -1 to +1 range (like joystick)
        HeadsetYawRate = Mathf.Clamp(yawRateDegreesPerSec / maxRotationRate, -1f, 1f);

        // Store for next frame
        _previousYaw = CurrentYaw;

        // Debug output
        if (showDebugInfo && Mathf.Abs(HeadsetYawRate) > 0.01f)
        {
            Debug.Log($"[MetaQuest] Yaw: {CurrentYaw:F1}° | Rate: {yawRateDegreesPerSec:F1}°/s | Normalized: {HeadsetYawRate:F2}");
        }
    }

    // ============================================
    // HELPER METHODS
    // ============================================

    /// <summary>
    /// Returns true if headset is actively being rotated
    /// </summary>
    public bool IsRotating()
    {
        return Mathf.Abs(HeadsetYawRate) > 0.01f;
    }

    /// <summary>
    /// Get raw yaw delta in degrees (before normalization)
    /// </summary>
    public float GetRawYawDelta()
    {
        return Mathf.DeltaAngle(_previousYaw, CurrentYaw);
    }

    /// <summary>
    /// Reset tracking (useful when teleporting or changing scenes)
    /// </summary>
    public void ResetTracking()
    {
        _initialized = false;
        HeadsetYawRate = 0f;
        _previousYaw = CurrentYaw;
    }

    // ============================================
    // DEBUG VISUALIZATION
    // ============================================

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 220, 300, 150));
        GUILayout.Label("<b>Meta Quest Input</b>");
        GUILayout.Label($"Headset Available: {IsHeadsetAvailable()}");
        GUILayout.Label($"Current Yaw: {CurrentYaw:F1}°");
        GUILayout.Label($"Yaw Rate: {HeadsetYawRate:F2}");
        GUILayout.Label($"Is Rotating: {IsRotating()}");
        GUILayout.EndArea();
    }
}
