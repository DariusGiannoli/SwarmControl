using UnityEngine;

/// <summary>
/// Handles traditional input from keyboard and game controller.
/// Exposes clean public properties that other systems can read.
/// This is the ONLY place where Unity's Input system should be accessed for traditional controls.
/// </summary>
public class TraditionalInput : MonoBehaviour
{
    // ============================================
    // PUBLIC PROPERTIES (Read by other systems)
    // ============================================
    
    // Movement Inputs
    public Vector2 MovementInput { get; private set; }      // Horizontal/Vertical axes combined
    public float HeightInput { get; private set; }          // Up/Down control
    public float RotationInput { get; private set; }        // Camera rotation
    public float SpreadInput { get; private set; }          // Swarm spread control (rate OR absolute depending on spreadAbsoluteMode)
    public bool IsSpreadAbsolute => spreadAbsoluteMode;

    // Button States (One-Frame Events)
    public bool SelectionNextPressed { get; private set; }      // Button 5
    public bool SelectionPrevPressed { get; private set; }      // Button 4
    public bool EmbodimentPressed { get; private set; }         // Button 0
    public bool DisembodimentPressed { get; private set; }      // Button 1
    public bool ToggleDummyForcesPressed { get; private set; }  // Button 3
    public bool CalibratePressed { get; private set; }          // Button 2 or keyboard key

    [Header("Input Axis Names (Unity Input Manager)")]
    [Tooltip("Left stick horizontal or A/D keys")]
    public string horizontalAxis = "Horizontal";
    
    [Tooltip("Left stick vertical or W/S keys")]
    public string verticalAxis = "Vertical";
    
    [Tooltip("Right stick horizontal for camera rotation")]
    public string rotationAxis = "JoystickRightHorizontal";
    
    [Tooltip("Left stick vertical (throttle) for height control")]
    public string heightAxis = "Throttle";
    
    [Tooltip("LR axis for spread control (knob/triggers)")]
    public string spreadAxis = "";

    [Tooltip("Ignore small joystick/controller axis drift. Remaining range is rescaled to keep full speed at stick extremes.")]
    [Range(0f, 0.5f)]
    public float axisDeadzone = 0.12f;

    [Tooltip("If true, knob/axis value maps directly to spread distance (absolute). If false, value is a rate.")]
    public bool spreadAbsoluteMode = true;

    [Tooltip("Min spread distance when knob is at -1 (only used in absolute mode)")]
    public float spreadMin = 1f;

    [Tooltip("Max spread distance when knob is at +1 (only used in absolute mode)")]
    public float spreadMax = 5f;

    [Header("Keyboard Keys")]
    [Tooltip("Keyboard key for upward height control")]
    public KeyCode heightUpKey = KeyCode.E;
    
    [Tooltip("Keyboard key for downward height control")]
    public KeyCode heightDownKey = KeyCode.Q;
    
    [Tooltip("Height change rate when using keyboard (units per second)")]
    public float keyboardHeightSpeed = 1.0f;

    [Header("Button Numbers (Joystick)")]
    public int selectionNextButton = 5;
    public int selectionPrevButton = 4;
    public int embodimentButton = 0;
    public int disembodimentButton = 1;
    public int toggleDummyForcesButton = 3;
    public int calibrateButton = 2;
    
    [Tooltip("Keyboard key for calibration (also works as alternative to joystick button)")]
    public KeyCode calibrateKey = KeyCode.C;

    // ============================================
    // UPDATE LOOP
    // ============================================
    
    void Update()
    {
        UpdateAxes();
        UpdateButtons();
    }

    void UpdateAxes()
    {
        // Movement (left stick / WASD)
        float horizontal = ApplyAxisDeadzone(Input.GetAxis(horizontalAxis));
        float vertical = ApplyAxisDeadzone(Input.GetAxis(verticalAxis));
        MovementInput = new Vector2(horizontal, vertical);

        // Height (right stick vertical + keyboard keys)
        float axisHeight = ApplyAxisDeadzone(Input.GetAxis(heightAxis));
        float keyboardHeight = 0f;
        
        if (Input.GetKey(heightUpKey))
            keyboardHeight += keyboardHeightSpeed;
        if (Input.GetKey(heightDownKey))
            keyboardHeight -= keyboardHeightSpeed;
        
        // Combine axis and keyboard input (keyboard takes priority if both active)
        HeightInput = Mathf.Abs(keyboardHeight) > 0.01f ? keyboardHeight : axisHeight;

        // Rotation (right stick horizontal)
        RotationInput = ApplyAxisDeadzone(Input.GetAxis(rotationAxis));

        // Spread (knob/triggers)
        float rawSpread = string.IsNullOrEmpty(spreadAxis) ? 0f : Input.GetAxis(spreadAxis);
        if (spreadAbsoluteMode)
        {
            // Map knob -1..+1 → spreadMin..spreadMax
            float t = (rawSpread + 1f) * 0.5f; // remap to 0..1
            SpreadInput = Mathf.Lerp(spreadMin, spreadMax, t);
        }
        else
        {
            SpreadInput = ApplyAxisDeadzone(rawSpread);
        }
    }

    float ApplyAxisDeadzone(float value)
    {
        float dz = Mathf.Clamp01(axisDeadzone);
        float magnitude = Mathf.Abs(value);
        if (magnitude <= dz) return 0f;

        float usableRange = Mathf.Max(1f - dz, 0.0001f);
        return Mathf.Sign(value) * Mathf.Clamp01((magnitude - dz) / usableRange);
    }

    void UpdateButtons()
    {
        // Selection buttons (one-frame press detection)
        SelectionNextPressed = Input.GetKeyDown("joystick button " + selectionNextButton);
        SelectionPrevPressed = Input.GetKeyDown("joystick button " + selectionPrevButton);

        // Embodiment control
        EmbodimentPressed = Input.GetKeyDown("joystick button " + embodimentButton);
        DisembodimentPressed = Input.GetKeyDown("joystick button " + disembodimentButton);

        // Toggle dummy forces
        ToggleDummyForcesPressed = Input.GetKeyDown("joystick button " + toggleDummyForcesButton);

        // Calibration (joystick button OR keyboard key)
        CalibratePressed = Input.GetKeyDown("joystick button " + calibrateButton) || Input.GetKeyDown(calibrateKey);
    }

    // ============================================
    // HELPER METHODS (Optional)
    // ============================================

    /// <summary>
    /// Returns true if any selection button was pressed this frame
    /// </summary>
    public bool AnySelectionPressed()
    {
        return SelectionNextPressed || SelectionPrevPressed;
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

    /// <summary>
    /// Returns true if any movement input is active
    /// </summary>
    public bool IsMoving()
    {
        return MovementInput.sqrMagnitude > 0.01f || Mathf.Abs(HeightInput) > 0.01f;
    }

    /// <summary>
    /// Returns true if any input is currently active (for idle detection)
    /// </summary>
    public bool IsAnyInputActive()
    {
        return IsMoving() || 
               Mathf.Abs(RotationInput) > 0.01f || 
               Mathf.Abs(SpreadInput) > 0.01f;
    }
}
