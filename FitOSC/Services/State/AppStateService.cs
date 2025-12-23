using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

/// <summary>
/// Global app state service for settings, treadmill telemetry, and state publishing.
/// </summary>
public class AppStateService
{
    /// <summary>
    /// Event raised whenever treadmill telemetry is updated.
    /// </summary>
    public event Action<AppStateInfo>? AppStateUpdated;
 
    
    /// <summary>
    /// Raises the <see cref="AppStateUpdated"/> event to notify subscribers of state changes.
    /// </summary>
    private void NotifyAppStateUpdated()
    {
        // build app state info
        AppStateInfo app = GetCurrentAppStateInfo();
        AppStateUpdated?.Invoke(app);
    }

    public AppStateInfo GetCurrentAppStateInfo()
    {
        return new()
        {
            TreadmillState = LatestState,
            TreadmillTelemetry = LatestData ?? new(),
            UserMeasurementType = Units,
            ConnectionStates = InterfaceStatus,
            TreadmillConfiguration = LatestConfiguration ?? new()
        };
    }
    
    
    // ---- Settings ----
    /// <summary>
    /// Current unit system (TelemetryProperty/Imperial).
    /// </summary>
    public readonly MeasurementType Units = MeasurementType.Imperial;
    
    /// <summary>
    /// Latest treadmill telemetry (raw values as parsed from BLE).
    /// </summary>
    public TreadmillTelemetry? LatestData { get; private set; } = new TreadmillTelemetry();

    /// <summary>
    /// Push new treadmill telemetry into app state.
    /// </summary>
    public void PublishTreadmillData(TreadmillTelemetry telemetry)
    {
        LatestData = telemetry;
        NotifyAppStateUpdated();
    }
    
    /// <summary>
    ///  Latest treadmill configuration (e.g., max speed, incline).
    /// </summary>
    public TreadmillConfiguration? LatestConfiguration { get; private set; } = new TreadmillConfiguration(); 
    
    /// <summary>
    /// Push new treadmill configuration into app state.
    /// </summary>
    /// <param name="config"></param>
    public void PublishTreadmillConfiguration(TreadmillConfiguration config)
    {
        LatestConfiguration = config;
        NotifyAppStateUpdated();
    }
    
    
    
    
    /// <summary>
    /// Latest treadmill state (e.g., Running, Paused, Stopped).
    /// </summary>
    public TreadmillState LatestState { get; private set; } = TreadmillState.Stopped;

 
    
    /// <summary>
    /// Push new treadmill state into app state.
    /// </summary>
    public void PublishTreadmillState(TreadmillState state)
    {
        LatestState = state;
        NotifyAppStateUpdated();
    }



    private readonly Dictionary<AppInterface, ConnectionStatus> InterfaceStatus = new()
    {
        { AppInterface.Bluetooth, ConnectionStatus.Disconnected },
        { AppInterface.VR, ConnectionStatus.Disconnected },
        { AppInterface.Pulsoid, ConnectionStatus.Disconnected },
        { AppInterface.OSC, ConnectionStatus.Disconnected }
    };
    
    public void PublishInterfaceConnectionStatuses(AppInterface appInterface, ConnectionStatus status)
    {
        InterfaceStatus[appInterface] = status;
        NotifyAppStateUpdated();
    }
}