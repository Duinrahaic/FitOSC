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

    /// <summary>Describes the connected device and its GATT services, for debugging.</summary>
    Task<string> GetDeviceInfoAsync();
}

public abstract class TreadmillService : ITreadmillService
{
    protected readonly IBluetoothClient Client;
    public bool IsConnected => Client.IsConnected;

    public event Action? ConnectionLost;

    private readonly AppStateService AppState;

    private volatile bool _closed;

    /// <summary>
    /// Takes over the app state's treadmill input, so publications from any earlier instance are dropped from here on.
    /// </summary>
    protected TreadmillService(AppStateService appState, IBluetoothClient client)
    {
        Client = client;
        AppState = appState;
        appState.BeginTreadmillInput(this);
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
    public Task<string> GetDeviceInfoAsync() => Client.GetDeviceInfoAsync();
    protected void RaiseData(TreadmillTelemetry telemetry) => AppState.PublishTreadmillData(telemetry, this);
    protected void RaiseState(TreadmillState state) => AppState.PublishTreadmillState(state, this);
    protected void RaiseConfiguration(TreadmillConfiguration config) => AppState.PublishTreadmillConfiguration(config, this);

    /// <summary>
    /// Releases the treadmill input first, which atomically clears what this instance published and drops anything a
    /// callback was still processing, then disconnects.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        AppState.ReleaseTreadmillInput(this);

        await DisconnectAsync();
        await Client.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
