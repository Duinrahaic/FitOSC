namespace FitOSC.Services.OpenVR;

/// <summary>
/// Event data for SteamVR action-based inputs.
/// All button actions use "changed" semantics - they're true only on the frame the button was pressed.
/// </summary>
public class OpenVRActionEvent
{
    /// <summary>
    /// Speed modifier value from analog input (-1 to 1, typically right stick Y axis).
    /// Positive = speed up, Negative = slow down.
    /// </summary>
    public float SpeedModifier { get; set; } = 0f;

    /// <summary>
    /// Manual movement vector from analog input (left stick).
    /// Used to detect when user wants manual control.
    /// </summary>
    public float ManualMovementX { get; set; } = 0f;
    public float ManualMovementY { get; set; } = 0f;

    /// <summary>
    /// True on the frame when toggle walking button was pressed.
    /// </summary>
    public bool ToggleWalkingPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when recenter yaw button was pressed.
    /// </summary>
    public bool RecenterYawPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when override speed up button was pressed.
    /// </summary>
    public bool OverrideSpeedUpPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when override speed down button was pressed.
    /// </summary>
    public bool OverrideSpeedDownPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when trim up button was pressed.
    /// </summary>
    public bool TrimUpPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when trim down button was pressed.
    /// </summary>
    public bool TrimDownPressed { get; set; } = false;

    /// <summary>
    /// Whether manual movement is currently active (magnitude > threshold).
    /// </summary>
    public bool IsManualMovementActive =>
        MathF.Abs(ManualMovementX) > 0.1f || MathF.Abs(ManualMovementY) > 0.1f;
}
