using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

/// <summary>
/// The rules for the on-screen controls, shared by clicks and SteamVR presses.
/// </summary>
public partial class AppStateService
{
    private const float WalkingTrimStep = 0.05f;
    private const float MaxWalkingTrim = 2.0f;

    /// <summary>
    /// Raised for every press of a control, on screen or from SteamVR, with whether it acts.
    /// </summary>
    public event Action<RemoteControl, bool>? ControlPressed;

    /// <summary>
    /// Whether the control is available at all. One that isn't is shown disabled.
    /// </summary>
    public bool IsEnabled(RemoteControl control) => control switch
    {
        // A preset also starts walking at its speed, so like the toggle it is always available.
        RemoteControl.WalkingToggle or RemoteControl.WalkingPreset => true,
        RemoteControl.WalkingIncrease or RemoteControl.WalkingDecrease => CurrentWalkingMode != WalkingMode.Disabled,
        // Treadmills ignore speed changes while the belt is still, and not all of them report stopped or paused.
        RemoteControl.TreadmillSpeedIncrease or RemoteControl.TreadmillSpeedDecrease =>
            TreadmillTakes(TreadmillCommands.Speed) && Reported(TreadmillTelemetryProperty.InstantaneousSpeed) is { Value: > 0 },
        RemoteControl.TreadmillInclineIncrease or RemoteControl.TreadmillInclineDecrease =>
            TreadmillTakes(TreadmillCommands.Incline) && Reported(TreadmillTelemetryProperty.Incline) != null,
        RemoteControl.TreadmillStart => TreadmillTakes(TreadmillCommands.Start),
        RemoteControl.TreadmillPause => TreadmillTakes(TreadmillCommands.Pause),
        RemoteControl.TreadmillStop => TreadmillTakes(TreadmillCommands.Stop),
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
    };

    /// <summary>
    /// Whether pressing the control acts now: it is enabled and not already at its limit.
    /// </summary>
    public bool CanAct(RemoteControl control) => IsEnabled(control) && control switch
    {
        RemoteControl.WalkingIncrease => CurrentWalkingMode == WalkingMode.Override
            ? WalkingModeConfig.CurrentOverrideIndex < WalkingModeConfig.OverrideSpeeds.Count - 1
            : WalkingModeConfig.WalkingTrim < MaxWalkingTrim,
        RemoteControl.WalkingDecrease => CurrentWalkingMode == WalkingMode.Override
            ? WalkingModeConfig.CurrentOverrideIndex > 0
            : WalkingModeConfig.WalkingTrim > 0f,
        RemoteControl.TreadmillSpeedIncrease => !HasSpeedRange || ReportedValue(TreadmillTelemetryProperty.InstantaneousSpeed) < LatestConfiguration!.MaxSpeed,
        RemoteControl.TreadmillSpeedDecrease => ReportedValue(TreadmillTelemetryProperty.InstantaneousSpeed) > (HasSpeedRange ? LatestConfiguration!.MinSpeed : 0m),
        RemoteControl.TreadmillInclineIncrease => ReportedValue(TreadmillTelemetryProperty.Incline) < LatestConfiguration!.MaxIncline,
        RemoteControl.TreadmillInclineDecrease => ReportedValue(TreadmillTelemetryProperty.Incline) > LatestConfiguration!.MinIncline,
        _ => true,
    };

    /// <summary>
    /// Reports a press of a control, on screen or from SteamVR, and returns whether it acts. The caller acts only when it does.
    /// </summary>
    public bool PressControl(RemoteControl control)
    {
        var acts = CanAct(control);
        ControlPressed?.Invoke(control, acts);
        return acts;
    }

    /// <summary>
    /// Steps the override preset in Override mode, otherwise the walking trim.
    /// </summary>
    public void StepWalkingSpeed(int direction)
    {
        if (CurrentWalkingMode == WalkingMode.Override)
        {
            WalkingModeConfig.CurrentOverrideIndex = Math.Clamp(WalkingModeConfig.CurrentOverrideIndex + direction,
                0, WalkingModeConfig.OverrideSpeeds.Count - 1);
        }
        else
        {
            WalkingModeConfig.WalkingTrim = Math.Clamp(WalkingModeConfig.WalkingTrim + WalkingTrimStep * direction,
                0f, MaxWalkingTrim);
        }
        NotifyAppStateUpdated();
    }

    // Takes commands only while fully connected, not while scanning.
    private bool TreadmillTakes(TreadmillCommands command) =>
        InterfaceStatus.GetValueOrDefault(AppInterface.Bluetooth) == ConnectionStatus.Connected
        && LatestConfiguration?.Accepts(command) == true;

    private TreadmillTelemetryValue? Reported(TreadmillTelemetryProperty property) =>
        LatestData?.GetTelemetryValue(property) is { Enabled: true } value ? value : null;

    private decimal ReportedValue(TreadmillTelemetryProperty property) => Reported(property)!.Value;

    // A treadmill that reported no speed range is left to its driver, as in TreadmillManager.
    private bool HasSpeedRange => LatestConfiguration!.MaxSpeed > LatestConfiguration.MinSpeed;
}
