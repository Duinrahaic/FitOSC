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
    /// True while the temporary speed up button is held.
    /// </summary>
    public bool TempSpeedUpHeld { get; set; } = false;

    /// <summary>
    /// True while the temporary speed down button is held.
    /// </summary>
    public bool TempSpeedDownHeld { get; set; } = false;

    /// <summary>
    /// Treadmill control actions.
    /// </summary>
    public bool TreadmillEnablePressed { get; set; } = false;
    public bool TreadmillSpeedUpPressed { get; set; } = false;
    public bool TreadmillSlowDownPressed { get; set; } = false;
    public bool TreadmillInclineUpPressed { get; set; } = false;
    public bool TreadmillInclineDownPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when enable dynamic mode button was pressed.
    /// </summary>
    public bool EnableDynamicPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when walking speed up/down buttons were pressed (permanent WalkingTrim adjustment).
    /// </summary>
    public bool WalkingSpeedUpPressed { get; set; } = false;
    public bool WalkingSpeedDownPressed { get; set; } = false;

    /// <summary>
    /// True on the frame when enable override mode button was pressed.
    /// </summary>
    public bool EnableOverridePressed { get; set; } = false;

    /// <summary>
    /// True on the frame when a direct override preset button was pressed.
    /// </summary>
    public bool Preset0Pressed { get; set; } = false;
    public bool Preset1Pressed { get; set; } = false;
    public bool Preset2Pressed { get; set; } = false;
    public bool Preset3Pressed { get; set; } = false;
    public bool Preset4Pressed { get; set; } = false;

    /// <summary>
    /// Whether manual movement is currently active (magnitude > threshold).
    /// </summary>
    public bool IsManualMovementActive =>
        MathF.Abs(ManualMovementX) > 0.1f || MathF.Abs(ManualMovementY) > 0.1f;
}
