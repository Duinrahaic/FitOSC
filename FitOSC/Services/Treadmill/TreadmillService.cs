using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

public interface ITreadmillService
{
    bool IsConnected { get; }
    Task ConnectAsync(string deviceName);
    Task DisconnectAsync();
    Task RequestControlAsync();
    Task StartAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SetSpeedAsync(decimal speed);
    Task SetInclineAsync(decimal incline);
    Task SendCommandAsync(byte[] command);
    TreadmillTelemetry TranslateData(byte[] data);
}

public abstract class TreadmillService : ITreadmillService
{
    protected readonly BaseBluetoothClient Client;
    public bool IsConnected => Client.IsConnected;

    private readonly AppStateService AppState;
    
    protected TreadmillService(AppStateService appState, BaseBluetoothClient client)
    {
        Client = client;
        AppState = appState;
    }

    public abstract Task ConnectAsync(string deviceName);
    public abstract Task DisconnectAsync();
    public abstract Task StartAsync();
    public abstract Task PauseAsync();
    public abstract Task StopAsync();
    public abstract Task GetTreadmillConfigurationAsync();
    public abstract Task SetSpeedAsync(decimal speed);
    public abstract Task SetInclineAsync(decimal incline);
    public abstract Task RequestControlAsync();
    public abstract Task SendCommandAsync(byte[] command);
    public abstract TreadmillTelemetry TranslateData(byte[] data);
    protected void RaiseData(TreadmillTelemetry telemetry) => AppState.PublishTreadmillData(telemetry);
    protected void RaiseState(TreadmillState state) => AppState.PublishTreadmillState(state);
    protected void RaiseConfiguration(TreadmillConfiguration config) => AppState.PublishTreadmillConfiguration(config);
}