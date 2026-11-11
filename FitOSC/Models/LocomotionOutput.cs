namespace FitOSC.Models;

/// <summary>
/// Where walking movement is sent.
/// </summary>
public enum LocomotionOutput
{
    /// <summary>VRChat OSC /input/Vertical and /input/Horizontal.</summary>
    Osc = 0,

    /// <summary>The left controller's thumbstick, through the FitOSC SteamVR driver.</summary>
    Joystick = 1,
}
