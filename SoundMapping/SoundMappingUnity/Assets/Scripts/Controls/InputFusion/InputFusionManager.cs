using UnityEngine;

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

    [Tooltip("OpenZen IMU for swarm movement (forward/back/left/right)")]
    public OpenZenMoveObject openZenIMU;

    [Tooltip("Meta Quest headset for camera rotation (yaw)")]
    public MetaQuestInput metaQuestInput;

    // Future inputs will go here:
    // public HandIMUInput leftHandIMU;
    // public HandIMUInput rightHandIMU;

    // ============================================
    // INPUT PRIORITY TOGGLES
    // ============================================
    [Header("Input Priority Settings")]
    [Tooltip("Use OpenZen IMU for swarm movement (if false, uses traditional input)")]
    public bool useOpenZenForMovement = false; // Start with false until OpenZen is connected

    [Tooltip("Use Meta Quest headset yaw for camera rotation (if false, uses traditional input)")]
    public bool useMetaQuestForRotation = false; // Will enable when MetaQuest is added

    [Tooltip("Use fused hand IMU + Quest arms for spread control (if false, uses traditional input)")]
    public bool useFusedHandsForSpread = false; // Will enable when hand IMUs are added

    [Tooltip("Always allow traditional input as fallback when primary sources aren't moving")]
    public bool enableTraditionalFallback = true;

    // ============================================
    // FUSED OUTPUT PROPERTIES (Read by other systems)
    // ============================================
    // Fused Outputs (Read-Only) - these are computed from all input sources
    public Vector3 SwarmMovement { get; private set; }      // Final movement vector for swarm (XZ plane + Y height)
    public float SwarmSpread { get; private set; }          // Final spread/cohesion control value
    public float CameraRotation { get; private set; }       // Final camera rotation input

    // Button states (pass-through from traditional for now)
    public bool SelectionNextPressed { get; private set; }
    public bool SelectionPrevPressed { get; private set; }
    public bool EmbodimentPressed { get; private set; }
    public bool DisembodimentPressed { get; private set; }
    public bool ToggleDummyForcesPressed { get; private set; }

    // ============================================
    // INITIALIZATION
    // ============================================
    void Start()
    {
        ValidateReferences();
    }

    void ValidateReferences()
    {
        if (traditionalInput == null)
        {
            Debug.LogError("InputFusionManager: TraditionalInput reference is missing! Assign it in the Inspector.");
        }

        if (openZenIMU == null && useOpenZenForMovement)
        {
            Debug.LogWarning("InputFusionManager: OpenZenIMU is enabled but reference is missing. Falling back to traditional input.");
            useOpenZenForMovement = false;
        }

        if (metaQuestInput == null && useMetaQuestForRotation)
        {
            Debug.LogWarning("InputFusionManager: MetaQuestInput is enabled but reference is missing. Falling back to traditional input.");
            useMetaQuestForRotation = false;
        }
    }

    // ============================================
    // UPDATE LOOP - FUSION LOGIC
    // ============================================
    void Update()
    {
        FuseMovementInputs();
        FuseSpreadInputs();
        FuseRotationInputs();
        FuseButtonInputs();
    }

    /// <summary>
    /// Combines movement from OpenZen IMU and/or traditional input
    /// </summary>
    void FuseMovementInputs()
    {
        Vector3 movement = Vector3.zero;

        // PRIMARY: Use OpenZen IMU if enabled and available
        if (useOpenZenForMovement && openZenIMU != null)
        {
            movement = ConvertIMUToMovement(openZenIMU.SensorOrientation);
            
            // Keep height from traditional input (IMU doesn't control height yet)
            if (traditionalInput != null)
            {
                movement.y = traditionalInput.HeightInput;
            }
        }
        // FALLBACK: Use traditional input
        else if (traditionalInput != null)
        {
            Vector2 moveInput = traditionalInput.MovementInput;
            float heightInput = traditionalInput.HeightInput;
            movement = new Vector3(moveInput.x, heightInput, moveInput.y);
        }

        SwarmMovement = movement;
    }

    [Header("IMU Conversion Settings")]
    [Tooltip("Pitch angle (degrees) for maximum forward/backward speed")]
    public float pitchDeadzone = 5f;
    public float pitchMaxAngle = 30f;
    
    [Tooltip("Roll angle (degrees) for maximum left/right speed")]
    public float rollDeadzone = 5f;
    public float rollMaxAngle = 30f;

    [Tooltip("Invert pitch direction (forward becomes backward)")]
    public bool invertPitch = false;
    
    [Tooltip("Invert roll direction (left becomes right)")]
    public bool invertRoll = false;

    /// <summary>
    /// Converts IMU sensor orientation (quaternion) to swarm movement vector.
    /// Pitch → Forward/Backward (Z), Roll → Left/Right (X)
    /// </summary>
    Vector3 ConvertIMUToMovement(Quaternion imuOrientation)
    {
        // Convert quaternion to Euler angles (in degrees)
        Vector3 euler = imuOrientation.eulerAngles;
        
        // Normalize angles to -180 to +180 range
        float pitch = NormalizeAngle(euler.x);
        float roll = NormalizeAngle(euler.z);

        // Apply deadzones
        if (Mathf.Abs(pitch) < pitchDeadzone) pitch = 0f;
        if (Mathf.Abs(roll) < rollDeadzone) roll = 0f;

        // Map angles to -1 to +1 range
        float forward = Mathf.Clamp(pitch / pitchMaxAngle, -1f, 1f);
        float right = Mathf.Clamp(roll / rollMaxAngle, -1f, 1f);

        // Apply inversions if needed
        if (invertPitch) forward = -forward;
        if (invertRoll) right = -right;

        // Return as movement vector (X=right, Y=height, Z=forward)
        return new Vector3(right, 0f, forward);
    }

    /// <summary>
    /// Normalizes angle from 0-360 to -180 to +180 range
    /// </summary>
    float NormalizeAngle(float angle)
    {
        if (angle > 180f)
            return angle - 360f;
        return angle;
    }

    /// <summary>
    /// Combines spread control from hand IMUs and/or traditional input
    /// </summary>
    void FuseSpreadInputs()
    {
        float spread = 0f;

        // PHASE 1: Just pass through traditional input
        if (traditionalInput != null)
        {
            spread = traditionalInput.SpreadInput;
        }

        // Future: Add hand IMU + Quest arm fusion here when useFusedHandsForSpread is true

        SwarmSpread = spread;
    }

    /// <summary>
    /// Combines rotation from Meta Quest headset and/or traditional input
    /// </summary>
    void FuseRotationInputs()
    {
        float rotation = 0f;

        // PRIMARY: Use Meta Quest headset yaw if enabled and available
        if (useMetaQuestForRotation && metaQuestInput != null)
        {
            rotation = metaQuestInput.HeadsetYawRate;
        }
        // FALLBACK: Use traditional input (right stick / controller)
        else if (traditionalInput != null)
        {
            rotation = traditionalInput.RotationInput;
        }

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
        }
    }

    // ============================================
    // HELPER METHODS
    // ============================================

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
    // DEBUG VISUALIZATION
    // ============================================
    void OnGUI()
    {
        if (!Application.isPlaying) return;

        // Display current input state in top-left corner (for debugging)
        GUILayout.BeginArea(new Rect(10, 10, 300, 250));
        GUILayout.Label($"<b>Input Fusion Status</b>");
        GUILayout.Label($"Movement: {SwarmMovement}");
        GUILayout.Label($"Spread: {SwarmSpread:F2}");
        GUILayout.Label($"Rotation: {CameraRotation:F2}");
        GUILayout.Label($"---");
        GUILayout.Label($"OpenZen Active: {useOpenZenForMovement}");
        GUILayout.Label($"MetaQuest Active: {useMetaQuestForRotation}");
        GUILayout.Label($"Traditional Fallback: {enableTraditionalFallback}");
        GUILayout.EndArea();
    }
}
