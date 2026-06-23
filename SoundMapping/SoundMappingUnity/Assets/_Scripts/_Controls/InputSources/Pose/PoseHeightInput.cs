using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Pose-tracking input for swarm height control.
/// Reads hand height from WebSocketClient (Python tracker, MediaPipe or RTMPose backend).
/// </summary>
public class PoseHeightInput : MonoBehaviour
{
    [Header("WebSocket Connection")]
    [Tooltip("Reference to WebSocketClient that receives pose-tracking data")]
    public WebSocketClient webSocketClient;

    [Header("Height Mapping Settings")]
    [Tooltip("Maximum vertical speed (units/second) when hands at extreme positions")]
    public float maxVerticalSpeed = 2.0f;

    [Header("Response")]
    [Tooltip("Linear (pass-through) or Exponential (sign(x) * |x|^exponent).")]
    public ResponseCurve curveType = ResponseCurve.Exponential;

    [Tooltip("Exponent used when curveType == Exponential. 2 = squared, 3 = cubed.")]
    [FormerlySerializedAs("responseCurve")]
    [Range(1f, 3f)]
    public float curveExponent = 2.0f;

    [Header("Deadzone")]
    [Tooltip("Ignore height values within this range of neutral (0 = no deadzone)")]
    [Range(0f, 0.5f)]
    public float heightDeadzone = 0.1f;

    [Header("Smoothing")]
    [Tooltip("Smoothing factor (0 = no smoothing, 1 = max smoothing)")]
    [Range(0f, 0.95f)]
    public float smoothing = 0.3f;

    private float _smoothedHeight = 0f;

    // ============================================
    // OUTPUT PROPERTIES
    // ============================================

    /// <summary>
    /// Current height control value (rate-based: -1 to +1)
    /// Positive = up, Negative = down, 0 = neutral
    /// </summary>
    public float HeightControl { get; private set; }

    /// <summary>
    /// Returns true if the pose tracker is connected and providing data
    /// </summary>
    public bool IsAvailable => webSocketClient != null && webSocketClient.IsConnected;

    /// <summary>
    /// Returns false - pose-tracked height is rate-based (like joystick)
    /// </summary>
    public bool IsAbsoluteMode => false;

    // ============================================
    // UPDATE LOOP
    // ============================================

    void Update()
    {
        if (IsAvailable)
        {
            // Get raw height from WebSocket (-1..+1, 0 = neutral)
            float rawHeight = webSocketClient.HandHeight;

            // Apply deadzone and exclude it from the remaining usable range.
            float heightRate = InputCurves.ToSignedRateAfterDeadzone(rawHeight, 1f, heightDeadzone);
            float curved = InputCurves.ApplyCurve(heightRate, curveType, curveExponent);

            // Apply smoothing
            _smoothedHeight = Mathf.Lerp(_smoothedHeight, curved, 1f - smoothing);

            // Output is already in -1..+1 rate format
            HeightControl = _smoothedHeight;
        }
        else
        {
            HeightControl = 0f;
        }
    }

    // ============================================
    // DEBUG HELPERS
    // ============================================

    [Header("Debug")]
    [Tooltip("Show the on-screen pose-height status panel during play. Off by default to keep the screen clean during runs.")]
    public bool showDebugInfo = false;

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(900, 30, 300, 100));
        GUILayout.Label($"<b>Pose Height Input</b> Connected: {IsAvailable}");
        if (IsAvailable)
        {
            GUILayout.Label($"Raw Height: {webSocketClient.HandHeight:F2}");
            GUILayout.Label($"Height Rate: {HeightControl:F2}");
        }
        GUILayout.EndArea();
    }
}
