using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

public interface ITreadmillService : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action? ConnectionLost;
    void InvalidateTelemetry();
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
    public event Action? ConnectionLost;

    private readonly AppStateService AppState;
    
    protected TreadmillService(AppStateService appState, IBluetoothClient client)
    {
        Client = client;
        AppState = appState;
        appState.BeginTreadmillInput(this);
        Client.ConnectionLost += OnConnectionLost;
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
    public async ValueTask DisposeAsync()
    {
        Client.ConnectionLost -= OnConnectionLost;
        Exception? publicationFailure = null;
        try { InvalidateTelemetry(); }
        catch (Exception ex) { publicationFailure = ex; }
        try { await Client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (publicationFailure != null)
        {
            throw new AggregateException(publicationFailure, ex);
        }
        if (publicationFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publicationFailure).Throw();
    }

    public void InvalidateTelemetry() => AppState.ReleaseTreadmillInput(this);

    private void OnConnectionLost()
    {
        try { InvalidateTelemetry(); }
        finally
        {
            // The owner schedules release even if a state subscriber failed.
            // Never await notification dispatch from its own callback.
            ConnectionLost?.Invoke();
        }
    }

    protected void RaiseData(TreadmillTelemetry telemetry) => AppState.PublishTreadmillData(telemetry, this);
    protected void RaiseState(TreadmillState state) => AppState.PublishTreadmillState(state, this);
    protected void RaiseConfiguration(TreadmillConfiguration config) => AppState.PublishTreadmillConfiguration(config, this);
}
