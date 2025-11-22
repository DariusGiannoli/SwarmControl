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
    public float SpreadInput { get; private set; }          // Swarm spread control

    // Button States (One-Frame Events)
    public bool SelectionNextPressed { get; private set; }      // Button 5
    public bool SelectionPrevPressed { get; private set; }      // Button 4
    public bool EmbodimentPressed { get; private set; }         // Button 0
    public bool DisembodimentPressed { get; private set; }      // Button 1
    public bool ToggleDummyForcesPressed { get; private set; }  // Button 3

    [Header("Input Axis Names (Unity Input Manager)")]
    [Tooltip("Left stick horizontal or A/D keys")]
    public string horizontalAxis = "Horizontal";
    
    [Tooltip("Left stick vertical or W/S keys")]
    public string verticalAxis = "Vertical";
    
    [Tooltip("Right stick horizontal for camera rotation")]
    public string rotationAxis = "JoystickRightHorizontal";
    
    [Tooltip("Right stick vertical for height control")]
    public string heightAxis = "JoystickRightVertical";
    
    [Tooltip("LR axis for spread control (triggers/bumpers)")]
    public string spreadAxis = "LR";

    [Header("Button Numbers (Joystick)")]
    public int selectionNextButton = 5;
    public int selectionPrevButton = 4;
    public int embodimentButton = 0;
    public int disembodimentButton = 1;
    public int toggleDummyForcesButton = 3;

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
        float horizontal = Input.GetAxis(horizontalAxis);
        float vertical = Input.GetAxis(verticalAxis);
        MovementInput = new Vector2(horizontal, vertical);

        // Height (right stick vertical)
        HeightInput = Input.GetAxis(heightAxis);

        // Rotation (right stick horizontal)
        RotationInput = Input.GetAxis(rotationAxis);

        // Spread (triggers/bumpers)
        SpreadInput = Input.GetAxis(spreadAxis);
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
