namespace FitOSC.Models;

/// <summary>
/// Configuration settings for walking modes
/// </summary>
public class WalkingModeConfiguration
{
    /// <summary>
    /// Maximum speed for Dynamic mode calculation (in km/h)
    /// </summary>
    public decimal MaxSpeed { get; set; } = 8.0m;

    /// <summary>
    /// Trim multiplier for adjusting walking speed (0.0 - 2.0, default: 0.80)
    /// Values above 1.0 allow reaching full VRChat speed at lower treadmill speeds
    /// Final output is clamped to 0.0 - 1.0 range
    /// </summary>
    public float WalkingTrim { get; set; } = 0.80f;

    /// <summary>
    /// Predefined speed states for Override mode (0.0 - 1.0)
    /// </summary>
    public List<float> OverrideSpeeds { get; set; } = new()
    {
        0.0f,   // Stopped
        0.25f,  // Slow walk
        0.5f,   // Normal walk
        0.75f,  // Fast walk
        1.0f    // Run
    };

    /// <summary>
    /// Current override speed index (for Override mode)
    /// </summary>
    public int CurrentOverrideIndex { get; set; } = 0;

    // Advanced Settings

    /// <summary>
    /// Smoothing factor for movement inputs (0.0 - 1.0)
    /// Lower = smoother but slower response, Higher = more responsive but jittery
    /// Default: 0.8
    /// </summary>
    public float SmoothingFactor { get; set; } = 0.8f;

    /// <summary>
    /// Maximum turn angle in degrees for vector decomposition
    /// This defines the full range of head rotation (total range is 2x this value)
    /// Default: 90 degrees (180 degree total range)
    /// </summary>
    public float MaxTurnAngle { get; set; } = 90f;

    /// <summary>
    /// OSC update interval in milliseconds
    /// Lower = more responsive, Higher = less network traffic
    /// Default: 100ms (10Hz)
    /// </summary>
    public int UpdateIntervalMs { get; set; } = 100;

    /// <summary>
    /// How fast the thumbstick speed modifier ramps up/down (0.01 - 0.5)
    /// Lower = slower, more gradual ramp; Higher = faster, more responsive
    /// Default: 0.05 (~0.5 seconds to fully ramp at 100Hz)
    /// </summary>
    public float ThumbstickRampSpeed { get; set; } = 0.05f;
}
