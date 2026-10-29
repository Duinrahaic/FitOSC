namespace FitOSC.Models;

/// <summary>
/// What sets the walking direction.
/// </summary>
public enum WalkingSteering
{
    /// <summary>Direction is locked to where the head faced when walking started.</summary>
    Locked = 0,

    /// <summary>Direction follows the SteamVR waist tracker.</summary>
    Hips = 1,
}
