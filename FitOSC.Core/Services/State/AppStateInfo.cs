using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

public sealed record AppStateInfo
{
    public TreadmillState TreadmillState { get; init; } = TreadmillState.Unknown;
    public TreadmillTelemetrySnapshot TreadmillTelemetry { get; init; } = new TreadmillTelemetry().Snapshot();

    public TreadmillConfiguration TreadmillConfiguration { get; init; } = new TreadmillConfiguration();

    public MeasurementType UserMeasurementType { get; init; } = MeasurementType.Metric;
    public IReadOnlyDictionary<AppInterface, ConnectionStatus> ConnectionStates { get; init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<AppInterface, ConnectionStatus>(new Dictionary<AppInterface, ConnectionStatus>
        {
            { AppInterface.Bluetooth, ConnectionStatus.Disconnected },
            { AppInterface.VR, ConnectionStatus.Disconnected },
            { AppInterface.Pulsoid, ConnectionStatus.Disconnected },
            { AppInterface.OSC, ConnectionStatus.Disconnected }
        });

    public string? DeviceName { get; init; }

    // OpenVR tracking data
    public OpenVRData OpenVR { get; init; } = new OpenVRData();

    // Walking/locomotion data
    public WalkingData Walking { get; init; } = new WalkingData();

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

public sealed record OpenVRData
{
    public float Yaw { get; init; } = 0;
    public float Pitch { get; init; } = 0;
    public float Roll { get; init; } = 0;
    public float RightThumbstickY { get; init; } = 0; // Right controller thumbstick Y axis (-1 = down, +1 = up)
}

public sealed record WalkingData
{
    public WalkingMode Mode { get; init; } = WalkingMode.Disabled;
    public WalkingModeConfigurationSnapshot Config { get; init; } = new WalkingModeConfigurationSnapshot();
    public float Velocity { get; init; } = 0;
    public float Horizontal { get; init; } = 0; // Left/right strafe (-1 to 1)
    public float Vertical { get; init; } = 0; // Forward/backward (0 to 1)
    public bool IsManualOverride { get; init; } = false; // True when left thumbstick is active (walking paused)
}
