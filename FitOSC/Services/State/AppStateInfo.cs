using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

public class AppStateInfo
{
    public TreadmillState TreadmillState { get; set; } = TreadmillState.Unknown;
    public TreadmillTelemetry TreadmillTelemetry { get; set; } = new TreadmillTelemetry();

    public TreadmillConfiguration TreadmillConfiguration { get; set; } = new TreadmillConfiguration();

    public MeasurementType UserMeasurementType { get; set; } = MeasurementType.Metric;
    public Dictionary<AppInterface, ConnectionStatus> ConnectionStates { get; set; } =
        new()
        {
            { AppInterface.Bluetooth, ConnectionStatus.Disconnected },
            { AppInterface.VR, ConnectionStatus.Disconnected },
            { AppInterface.Pulsoid, ConnectionStatus.Disconnected },
            { AppInterface.OSC, ConnectionStatus.Disconnected }
        };

    public string? DeviceName { get; set; }

    // OpenVR tracking data
    public OpenVRData OpenVR { get; set; } = new OpenVRData();

    // Walking/locomotion data
    public WalkingData Walking { get; set; } = new WalkingData();

    public bool HasMetrics => ConnectionStates.TryGetValue(AppInterface.Bluetooth, out var status)
                              && status == ConnectionStatus.Connected;

    // Convenience properties for telemetry
    public decimal CurrentSpeed => TreadmillTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0;
    public decimal CurrentIncline => TreadmillTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.Incline)?.Value ?? 0;

    /// <summary>
    /// Current heart rate - reads directly from telemetry (Pulsoid updates this value when connected)
    /// </summary>
    public int CurrentHeartRate => (int)(TreadmillTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.HeartRate)?.Value ?? 0);
}

public class OpenVRData
{
    public float Yaw { get; set; } = 0;
    public float Pitch { get; set; } = 0;
    public float Roll { get; set; } = 0;
    public float RightThumbstickY { get; set; } = 0; // Right controller thumbstick Y axis (-1 = down, +1 = up)
}

public class WalkingData
{
    public WalkingMode Mode { get; set; } = WalkingMode.Disabled;
    public WalkingModeConfiguration Config { get; set; } = new WalkingModeConfiguration();
    public float Velocity { get; set; } = 0;
    public float Horizontal { get; set; } = 0; // Left/right strafe (-1 to 1)
    public float Vertical { get; set; } = 0; // Forward/backward (0 to 1)
    public bool IsManualOverride { get; set; } = false; // True when left thumbstick is active (walking paused)
}
