using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public record SerialChannel(Guid Service, Guid Notify, Guid Write);

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public abstract class SerialTreadmillService(ILogger logger, AppStateService appState)
    : TreadmillService(appState, new WindowsBluetoothClient(logger))
{
    protected readonly ILogger Logger = logger;

    private CancellationTokenSource? _sessionCts;
    private SerialChannel? _channel;

    /// <summary>Channels to try in order; the first one the device exposes is used.</summary>
    protected abstract IReadOnlyList<SerialChannel> Channels { get; }

    protected abstract TimeSpan PollInterval { get; }

    protected abstract void OnNotification(byte[] data);

    /// <summary>Writes the request(s) for one poll tick, including handshake retries until the device is ready.</summary>
    protected abstract Task PollAsync();

    /// <summary>Runs after notifications are enabled and before polling starts (handshakes, parameter queries).</summary>
    protected virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>Clears protocol state when the connection ends.</summary>
    protected virtual void OnDisconnected()
    {
    }

    /// <summary>Whether the protocol handshake has finished; connecting waits for it.</summary>
    protected virtual bool IsReady => true;

    private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Cancelled when the connection ends; delayed work must observe it so it cannot leak into the next session.</summary>
    protected CancellationToken SessionToken => _sessionCts?.Token ?? new CancellationToken(canceled: true);

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        foreach (var channel in Channels)
        {
            if (!await Client.ConnectAsync(channel.Service, deviceName, cancellationToken))
                continue;

            if (!await Client.SubscribeAsync(channel.Notify, OnNotification))
            {
                Logger.LogError("Failed to subscribe to notify characteristic {Uuid}", channel.Notify);
                return false;
            }

            // Resolve the write characteristic now so a missing one fails the connection instead of every command.
            if (!await Client.SubscribeAsync(channel.Write, null))
            {
                Logger.LogError("Write characteristic {Uuid} not found", channel.Write);
                return false;
            }

            _channel = channel;
            _sessionCts = new CancellationTokenSource();
            await OnConnectedAsync();
            _ = RunPollingAsync(_sessionCts.Token);

            // Polling retries the handshake; the caller's timeout bounds the wait.
            while (!IsReady)
                await Task.Delay(ReadyCheckInterval, cancellationToken);
            return true;
        }

        Logger.LogError("No supported serial service found on {Device}", deviceName);
        return false;
    }

    public override async Task DisconnectAsync()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        OnDisconnected();
        _channel = null;
        await Client.DisconnectAsync();
    }

    // Serial protocols have no FTMS-style control handshake.
    public override Task RequestControlAsync() => Task.CompletedTask;

    public override Task GetTreadmillConfigurationAsync() => Task.CompletedTask;

    public override async Task SendCommandAsync(byte[] command) => await WriteAsync(command);

    protected async Task WriteAsync(byte[] frame)
    {
        if (_channel == null)
            return;
        await Client.WriteAsync(_channel.Write, frame);
    }

    private async Task RunPollingAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Client.IsConnected)
                    await PollAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Polling stopped on disconnect.
        }
    }

    /// <summary>Builds a telemetry snapshot with only the given values enabled.</summary>
    protected static TreadmillTelemetry Telemetry(params (TreadmillTelemetryProperty Property, decimal Value)[] values)
    {
        var td = new TreadmillTelemetry();
        foreach (var (property, value) in values)
        {
            td.Values[property].Enabled = true;
            td.Values[property].Value = value;
        }
        return td;
    }

    protected static decimal PaceFromSpeed(decimal kph) => kph > 0 ? 3600m / kph : 0;
}
