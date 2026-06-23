using UnityEngine;

/// <summary>
/// Pose-tracking head-yaw input for camera rotation.
/// Reads neutral-subtracted yaw (degrees) from the Python tracker's WebSocket
/// payload and converts to a rate (-1..+1) using Inspector-set bounds.
///
/// The Python side already subtracts the participant's calibrated neutral pose,
/// so a value of 0 here means "facing the screen". This source lives in the
/// FuseRotationInputs chain between MetaQuest and Traditional — a fallback for
/// non-VR participants who can't wear the headset.
/// </summary>
public class PoseYawInput : MonoBehaviour
{
    [Header("WebSocket Connection")]
    [Tooltip("Reference to WebSocketClient that receives pose-tracking data")]
    public WebSocketClient webSocketClient;

    [Header("Yaw Range (degrees from neutral)")]
    [Tooltip("Head turn that produces full +1 rate. Typical range: 15–35°.")]
    [Range(5f, 60f)]
    public float maxYawDeg = 25f;

    [Header("Response")]
    [Tooltip("Linear (pass-through) or Exponential (sign(x) * |x|^exponent).")]
    public ResponseCurve curveType = ResponseCurve.Exponential;

    [Tooltip("Exponent used when curveType == Exponential.")]
    [Range(1f, 3f)]
    public float curveExponent = 2.0f;

    [Header("Deadzone")]
    [Tooltip("Ignore head turns within this many degrees of neutral.")]
    [Range(0f, 15f)]
    public float deadzoneDeg = 3f;

    [Header("Smoothing")]
    [Tooltip("Lerp factor (0 = no smoothing, 1 = max smoothing).")]
    [Range(0f, 0.95f)]
    public float smoothing = 0.3f;

    [Header("Sign")]
    [Tooltip("Flip if head-turn direction maps the wrong way to camera pan in your setup.")]
    public bool invertSign = false;

    [Header("Debug")]
    public bool showDebugInfo = false;

    private float _smoothedRate = 0f;

    // ============================================
    // PUBLIC OUTPUTS (read by InputFusionManager)
    // ============================================

    /// <summary>Yaw rate in [-1, +1]. + = pan right, - = pan left (subject to invertSign).</summary>
    public float YawRate { get; private set; }

    /// <summary>True if the WebSocket is connected. The yaw value is 0 by default,
    /// which is the neutral signal — so "available" is the same condition as for
    /// PoseSpread/PoseHeight.</summary>
    public bool IsAvailable => webSocketClient != null && webSocketClient.IsConnected;

    void Update()
    {
        if (!IsAvailable)
        {
            YawRate = 0f;
            return;
        }

        float yawDeg = webSocketClient.HeadYawDeg;
        if (invertSign) yawDeg = -yawDeg;

        // Map degrees to rate while excluding the deadzone from the usable range.
        float rawRate = InputCurves.ToSignedRateAfterDeadzone(yawDeg, maxYawDeg, deadzoneDeg);
        float curved = InputCurves.ApplyCurve(rawRate, curveType, curveExponent);

        _smoothedRate = Mathf.Lerp(_smoothedRate, curved, 1f - smoothing);
        YawRate = _smoothedRate;
    }

    void OnGUI()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(900, 130, 320, 100));
        GUILayout.Label($"<b>Pose Yaw Input</b>  Connected: {IsAvailable}");
        if (IsAvailable)
        {
            GUILayout.Label($"Raw yaw: {webSocketClient.HeadYawDeg:+0.0;-0.0;0.0}°  bound: ±{maxYawDeg:F0}°");
            GUILayout.Label($"Rate: {YawRate:F2}  curve: {curveType}");
        }
        GUILayout.EndArea();
    }
}
