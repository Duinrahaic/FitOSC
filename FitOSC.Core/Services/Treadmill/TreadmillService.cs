using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

public interface ITreadmillService : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(string deviceName, CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task RequestControlAsync(CancellationToken cancellationToken = default);
    Task StartAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SetSpeedAsync(decimal speed);
    Task SetInclineAsync(decimal incline);
    Task SendCommandAsync(byte[] command);
    TreadmillTelemetry TranslateData(byte[] data);
    Task<string> GetDeviceInfoAsync();
}

public abstract class TreadmillService : ITreadmillService
{
    protected readonly IBluetoothClient Client;
    public bool IsConnected => Client.IsConnected;

    private readonly AppStateService AppState;
    
    protected TreadmillService(AppStateService appState, IBluetoothClient client)
    {
        Client = client;
        AppState = appState;
    }

    public abstract Task ConnectAsync(string deviceName, CancellationToken cancellationToken);
    public abstract Task DisconnectAsync();
    public abstract Task StartAsync();
    public abstract Task PauseAsync();
    public abstract Task StopAsync();
    public abstract Task GetTreadmillConfigurationAsync(CancellationToken cancellationToken);
    public abstract Task SetSpeedAsync(decimal speed);
    public abstract Task SetInclineAsync(decimal incline);
    public abstract Task RequestControlAsync(CancellationToken cancellationToken = default);
    public abstract Task SendCommandAsync(byte[] command);
    public abstract TreadmillTelemetry TranslateData(byte[] data);
    public Task<string> GetDeviceInfoAsync() => Client.GetDeviceInfoAsync();
    public ValueTask DisposeAsync() => Client.DisposeAsync();
    protected void RaiseData(TreadmillTelemetry telemetry) => AppState.PublishTreadmillData(telemetry);
    protected void RaiseState(TreadmillState state) => AppState.PublishTreadmillState(state);
    protected void RaiseConfiguration(TreadmillConfiguration config) => AppState.PublishTreadmillConfiguration(config);
}