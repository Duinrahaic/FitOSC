using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

public class AppStateInfo
{
    public TreadmillState TreadmillState { get; init; } = TreadmillState.Unknown;
    public TreadmillTelemetry TreadmillTelemetry { get; init; } = new TreadmillTelemetry();
    
    public TreadmillConfiguration TreadmillConfiguration { get; init; } = new TreadmillConfiguration();
    
    public MeasurementType UserMeasurementType { get; init; } = MeasurementType.Metric;
    public Dictionary<AppInterface, ConnectionStatus> ConnectionStates { get; init; } =
        new()
        {
            { AppInterface.Bluetooth, ConnectionStatus.Disconnected },
            { AppInterface.VR, ConnectionStatus.Disconnected },
            { AppInterface.Pulsoid, ConnectionStatus.Disconnected },
            { AppInterface.OSC, ConnectionStatus.Disconnected }
        };
    
    public bool HasMetrics => TreadmillTelemetry.Values.Any();
}
