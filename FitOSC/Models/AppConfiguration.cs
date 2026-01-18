using FitOSC.Services.Treadmill;

namespace FitOSC.Models;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// Treadmill settings
    /// </summary>
    public TreadmillSettings Treadmill { get; set; } = new();

    /// <summary>
    /// VRChat OSC settings
    /// </summary>
    public OscSettings Osc { get; set; } = new();

    /// <summary>
    /// Walking mode settings
    /// </summary>
    public WalkingModeSettings WalkingMode { get; set; } = new();

    /// <summary>
    /// User preferences
    /// </summary>
    public UserSettings User { get; set; } = new();

    /// <summary>
    /// WebSocket server settings
    /// </summary>
    public WebSocketSettings WebSocket { get; set; } = new();

    /// <summary>
    /// OpenVR/SteamVR settings
    /// </summary>
    public OpenVRSettings OpenVR { get; set; } = new();

    /// <summary>
    /// Pulsoid heart rate settings
    /// </summary>
    public PulsoidSettings Pulsoid { get; set; } = new();

    /// <summary>
    /// MIDI settings
    /// </summary>
    public MidiSettings Midi { get; set; } = new();
}

public class TreadmillSettings
{
    /// <summary>
    /// Auto-connect to last known device on startup
    /// </summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>
    /// Last connected device name
    /// </summary>
    public string? LastDeviceName { get; set; }

    /// <summary>
    /// Default treadmill type
    /// </summary>
    public TreadmillType TreadmillType { get; set; } = TreadmillType.FTMS;
}

public class OscSettings
{
    /// <summary>
    /// OSC IP Address (null for auto-discovery)
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// OSC Port (null for auto-discovery)
    /// </summary>
    public int? Port { get; set; }
}

public class WalkingModeSettings
{
    /// <summary>
    /// Maximum speed in km/h
    /// </summary>
    public decimal MaxSpeed { get; set; } = 8.0m;

    /// <summary>
    /// Default walking mode when enabled (Dynamic or Override)
    /// </summary>
    public WalkingMode DefaultMode { get; set; } = WalkingMode.Dynamic;

    /// <summary>
    /// Default walking trim (multiplier: 0.0 to 2.0)
    /// Values above 1.0 allow reaching full VRChat speed at lower treadmill speeds
    /// </summary>
    public float DefaultTrim { get; set; } = 0.80f;

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
    /// Default: 25ms (40Hz)
    /// </summary>
    public int UpdateIntervalMs { get; set; } = 25;

    /// <summary>
    /// How fast the thumbstick speed modifier ramps up/down (0.01 - 0.5)
    /// Lower = slower, more gradual ramp; Higher = faster, more responsive
    /// Default: 0.05 (~0.5 seconds to fully ramp at 100Hz)
    /// </summary>
    public float ThumbstickRampSpeed { get; set; } = 0.05f;
}

public class UserSettings
{
    /// <summary>
    /// Preferred unit system (true = metric, false = imperial)
    /// </summary>
    public bool PreferMetric { get; set; } = true;

    /// <summary>
    /// Enable session history tracking
    /// </summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>
    /// Minimum session duration in minutes to save to history
    /// </summary>
    public int MinimumSessionDuration { get; set; } = 5;

    /// <summary>
    /// Enable GPU hardware acceleration for the WebView.
    /// Disabling may reduce GPU usage but could affect rendering performance.
    /// Requires app restart to take effect.
    /// </summary>
    public bool UseHardwareAcceleration { get; set; } = true;
}

public class WebSocketSettings
{
    /// <summary>
    /// Enable WebSocket server for telemetry broadcasting
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// WebSocket server port (default: 6547)
    /// </summary>
    public int Port { get; set; } = 6547;
}

public class OpenVRSettings
{
    /// <summary>
    /// Auto-launch FitOSC when SteamVR starts
    /// </summary>
    public bool AutoLaunch { get; set; } = false;
}

public class PulsoidSettings
{
    /// <summary>
    /// Enable Pulsoid heart rate integration
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Pulsoid API key for authentication
    /// </summary>
    public string? ApiKey { get; set; }
}

public class MidiSettings
{
    /// <summary>
    /// Enable MIDI integration
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Selected MIDI output device name
    /// </summary>
    public string? OutputDeviceName { get; set; }

    /// <summary>
    /// MIDI channel to send on (1-16)
    /// </summary>
    public int Channel { get; set; } = 1;

    /// <summary>
    /// CC number for speed (0-127)
    /// </summary>
    public int SpeedCC { get; set; } = 1;

    /// <summary>
    /// CC number for heart rate (0-127)
    /// </summary>
    public int HeartRateCC { get; set; } = 2;

    /// <summary>
    /// CC number for incline (0-127)
    /// </summary>
    public int InclineCC { get; set; } = 3;
}
