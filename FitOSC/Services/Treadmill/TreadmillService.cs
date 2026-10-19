using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// One connection to one treadmill. The manager creates a fresh instance for every connection attempt and disposes it
/// when the attempt fails or the session ends; a disposed instance never publishes again.
/// </summary>
public interface ITreadmillService : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised when an established connection drops on its own.</summary>
    event Action? ConnectionLost;

    /// <summary>Connects and initializes the treadmill. Returns true only once it is ready to stream and accept commands.</summary>
    Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken);
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

    public event Action? ConnectionLost;

    private readonly AppStateService AppState;

    // Publishing and closing share this gate, so nothing a callback was still processing lands after disposal.
    private readonly object _publishGate = new();
    private volatile bool _closed;

    protected TreadmillService(AppStateService appState, BaseBluetoothClient client)
    {
        Client = client;
        AppState = appState;
        Client.ConnectionLost += () =>
        {
            if (!_closed)
                ConnectionLost?.Invoke();
        };
    }

    public abstract Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken);
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
    protected void RaiseData(TreadmillTelemetry telemetry) => Publish(() => AppState.PublishTreadmillData(telemetry));
    protected void RaiseState(TreadmillState state) => Publish(() => AppState.PublishTreadmillState(state));
    protected void RaiseConfiguration(TreadmillConfiguration config) => Publish(() => AppState.PublishTreadmillConfiguration(config));

    private void Publish(Action publish)
    {
        lock (_publishGate)
        {
            if (!_closed)
                publish();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_publishGate)
            _closed = true;

        await DisconnectAsync();
        await Client.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}