using UnityEngine;

/// <summary>
/// Reads Meta Quest hand tracking data and converts hand distance to swarm spread control.
/// Hands close together = contract swarm, hands far apart = expand swarm.
/// </summary>
public class HandTrackingInput : MonoBehaviour
{
    [Header("Hand References")]
    [Tooltip("Left hand tracker from OVRCameraRig/TrackingSpace/LeftHandAnchor")]
    public OVRHand leftHand;

    [Tooltip("Right hand tracker from OVRCameraRig/TrackingSpace/RightHandAnchor")]
    public OVRHand rightHand;

    [Header("Distance Mapping")]
    [Tooltip("Hand distance (meters) where spread is neutral (no change)")]
    [Range(0.2f, 0.6f)]
    public float neutralDistance = 0.35f;

    [Tooltip("Deadzone around neutral distance (meters) - no spread change in this range")]
    [Range(0.0f, 0.2f)]
    public float deadzone = 0.1f;

    [Tooltip("Minimum hand distance (meters) for maximum contraction")]
    [Range(0.05f, 0.3f)]
    public float minDistance = 0.1f;

    [Tooltip("Maximum hand distance (meters) for maximum expansion")]
    [Range(0.5f, 1.5f)]
    public float maxDistance = 0.8f;

    [Header("Filtering")]
    [Tooltip("Only use tracking when confidence is high (more stable, may lose control briefly)")]
    public bool requireHighConfidence = true;

    [Tooltip("Smoothing speed for spread changes (higher = faster response)")]
    [Range(1f, 20f)]
    public float smoothingSpeed = 6f;

    [Header("Debug")]
    public bool showDebugInfo = false;

    // ============================================
    // PUBLIC OUTPUTS (Read by InputFusionManager)
    // ============================================

    /// <summary>
    /// Spread rate control value (-1 to +1)
    /// Negative = contract swarm, 0 = no change, Positive = expand swarm
    /// Based on how far hands are from target/neutral position
    /// </summary>
    public float HandSpreadControl { get; private set; }

    /// <summary>
    /// Current distance between hands (meters)
    /// </summary>
    public float CurrentHandDistance { get; private set; }

    /// <summary>
    /// Are both hands being tracked with sufficient confidence?
    /// </summary>
    public bool BothHandsTracked { get; private set; }

    // ============================================
    // PRIVATE STATE
    // ============================================

    private float _targetSpread = 0f;
    private float _lastValidDistance = 0f;
    private bool _initialized = false;

    // ============================================
    // INITIALIZATION
    // ============================================

    void Start()
    {
        ValidateReferences();
        _lastValidDistance = neutralDistance;
    }

    void ValidateReferences()
    {
        if (leftHand == null)
        {
            Debug.LogWarning("HandTrackingInput: Left hand reference is missing! Assign OVRHand from LeftHandAnchor.");
        }

        if (rightHand == null)
        {
            Debug.LogWarning("HandTrackingInput: Right hand reference is missing! Assign OVRHand from RightHandAnchor.");
        }

        if (leftHand != null && rightHand != null)
        {
            Debug.Log("HandTrackingInput: Hand tracking initialized successfully.");
        }
    }

    // ============================================
    // UPDATE LOOP
    // ============================================

    void Update()
    {
        if (!AreHandsAvailable())
        {
            // Maintain last value when hands not tracked (don't snap to 0)
            // This prevents sudden spread changes when tracking is lost briefly
        }
        else
        {
            UpdateHandDistance();
            CalculateSpreadControl();
        }

        // Apply smoothing
        HandSpreadControl = Mathf.Lerp(HandSpreadControl, _targetSpread, Time.deltaTime * smoothingSpeed);
    }

    /// <summary>
    /// Check if hand tracking is available and meets confidence requirements
    /// </summary>
    bool AreHandsAvailable()
    {
        if (leftHand == null || rightHand == null)
        {
            BothHandsTracked = false;
            return false;
        }

        // Check if hands are tracked
        bool leftTracked = leftHand.IsTracked;
        bool rightTracked = rightHand.IsTracked;

        if (!leftTracked || !rightTracked)
        {
            BothHandsTracked = false;
            return false;
        }

        // Check confidence if required
        if (requireHighConfidence)
        {
            bool leftHighConfidence = leftHand.IsTracked && leftHand.HandConfidence == OVRHand.TrackingConfidence.High;
            bool rightHighConfidence = rightHand.IsTracked && rightHand.HandConfidence == OVRHand.TrackingConfidence.High;

            BothHandsTracked = leftHighConfidence && rightHighConfidence;
            return BothHandsTracked;
        }

        BothHandsTracked = true;
        return true;
    }

    /// <summary>
    /// Calculate distance between hands
    /// </summary>
    void UpdateHandDistance()
    {
        Vector3 leftPos = leftHand.transform.position;
        Vector3 rightPos = rightHand.transform.position;

        CurrentHandDistance = Vector3.Distance(leftPos, rightPos);
        _lastValidDistance = CurrentHandDistance;

        if (!_initialized)
        {
            _initialized = true;
        }
    }

    /// <summary>
    /// Convert hand distance to spread rate control value
    /// Works like a joystick: distance from neutral position determines rate
    /// </summary>
    void CalculateSpreadControl()
    {
        float distance = CurrentHandDistance;

        // Apply deadzone around neutral position
        float deadzoneMin = neutralDistance - deadzone;
        float deadzoneMax = neutralDistance + deadzone;

        if (distance >= deadzoneMin && distance <= deadzoneMax)
        {
            // In deadzone - no change
            _targetSpread = 0f;
        }
        else if (distance < deadzoneMin)
        {
            // Hands closer than neutral - contract swarm (negative rate)
            // Map from minDistance to deadzoneMin → -1 to 0
            float range = deadzoneMin - minDistance;
            float normalizedDist = (distance - minDistance) / range;
            _targetSpread = (normalizedDist - 1f); // Results in -1 to 0
            _targetSpread = Mathf.Clamp(_targetSpread, -1f, 0f);
        }
        else // distance > deadzoneMax
        {
            // Hands farther than neutral - expand swarm (positive rate)
            // Map from deadzoneMax to maxDistance → 0 to +1
            float range = maxDistance - deadzoneMax;
            float normalizedDist = (distance - deadzoneMax) / range;
            _targetSpread = normalizedDist;
            _targetSpread = Mathf.Clamp(_targetSpread, 0f, 1f);
        }

        if (showDebugInfo && Mathf.Abs(_targetSpread) > 0.01f)
        {
            Debug.Log($"[HandTracking] Distance: {distance:F3}m | Spread Rate: {_targetSpread:F2}");
        }
    }

    // ============================================
    // HELPER METHODS
    // ============================================

    /// <summary>
    /// Returns true if hands are actively controlling spread (outside deadzone)
    /// </summary>
    public bool IsControllingSpread()
    {
        return Mathf.Abs(HandSpreadControl) > 0.01f;
    }

    /// <summary>
    /// Get raw hand distance without any processing
    /// </summary>
    public float GetRawHandDistance()
    {
        if (leftHand == null || rightHand == null) return 0f;
        return Vector3.Distance(leftHand.transform.position, rightHand.transform.position);
    }

    /// <summary>
    /// Reset spread control to neutral
    /// </summary>
    public void ResetSpread()
    {
        HandSpreadControl = 0f;
        _targetSpread = 0f;
    }

    // ============================================
    // DEBUG VISUALIZATION
    // ============================================

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 380, 300, 180));
        GUILayout.Label("<b>Hand Tracking Input</b>");
        GUILayout.Label($"Both Hands Tracked: {BothHandsTracked}");
        GUILayout.Label($"Hand Distance: {CurrentHandDistance:F3}m");
        GUILayout.Label($"Neutral: {neutralDistance:F2}m ± {deadzone:F2}m");
        GUILayout.Label($"Spread Rate: {HandSpreadControl:F2}");
        GUILayout.Label($"Is Controlling: {IsControllingSpread()}");
        
        // Visual indicator
        if (BothHandsTracked)
        {
            string indicator = HandSpreadControl < -0.1f ? "<<< CONTRACTING" :
                              HandSpreadControl > 0.1f ? "EXPANDING >>>" :
                              "= NEUTRAL =";
            GUILayout.Label($"<b>{indicator}</b>");
        }
        GUILayout.EndArea();
    }

    // Optional: Draw gizmos in Scene view to visualize hand positions
    void OnDrawGizmos()
    {
        if (!showDebugInfo || !Application.isPlaying) return;
        if (leftHand == null || rightHand == null) return;
        if (!BothHandsTracked) return;

        // Draw line between hands
        Gizmos.color = IsControllingSpread() ? Color.green : Color.yellow;
        Gizmos.DrawLine(leftHand.transform.position, rightHand.transform.position);

        // Draw spheres at hand positions
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(leftHand.transform.position, 0.05f);
        Gizmos.DrawWireSphere(rightHand.transform.position, 0.05f);
    }
}
