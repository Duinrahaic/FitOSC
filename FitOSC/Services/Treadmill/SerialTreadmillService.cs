using System.Diagnostics;
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
/// <remarks>
/// Notifications and poll ticks run one at a time. Commands (start, speed, incline and so on) arrive from the UI and
/// may run alongside either, so state they share with notifications must be thread-safe, e.g. a
/// <see cref="CommandedValue"/>.
/// </remarks>
public abstract class SerialTreadmillService(ILogger logger, AppStateService appState)
    : TreadmillService(appState, new WindowsBluetoothClient(logger))
{
    protected readonly ILogger Logger = logger;

    private CancellationTokenSource? _sessionCts;
    private CancellationToken _sessionToken = new(canceled: true);
    private Task? _polling;
    private SerialChannel? _channel;

    // Notifications and poll ticks take turns, so protocol state is only changed by one of them at a time.
    private readonly SemaphoreSlim _protocolGate = new(1, 1);

    // Diagnostics for drops: how long the session ran, and whether the treadmill kept answering and accepting writes.
    private static readonly TimeSpan NotificationGapWarning = TimeSpan.FromSeconds(3);
    private readonly Stopwatch _sessionAge = new();
    private long _notifications;
    private long _lastNotificationAt;
    private int _writes;
    private int _failedWrites;

    /// <summary>Channels to try in order; the first one the device exposes is used.</summary>
    protected abstract IReadOnlyList<SerialChannel> Channels { get; }

    /// <summary>The channel this session connected on; set before <see cref="OnConnectedAsync"/> runs.</summary>
    protected SerialChannel? ConnectedChannel => _channel;

    protected abstract TimeSpan PollInterval { get; }

    /// <summary>Handles one notification. Never runs at the same time as <see cref="PollAsync"/>.</summary>
    protected abstract void OnNotification(byte[] data);

    /// <summary>
    /// Writes the request(s) for one poll tick, including handshake retries until the device is ready. Notifications
    /// wait until it completes, so it must not wait for one.
    /// </summary>
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
    protected CancellationToken SessionToken => _sessionToken;

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        foreach (var channel in Channels)
        {
            if (!await Client.ConnectAsync(channel.Service, deviceName, cancellationToken))
                continue;

            if (!await Client.SubscribeAsync(channel.Notify, HandleNotification, cancellationToken))
            {
                Logger.LogError("Failed to subscribe to notify characteristic {Uuid}", channel.Notify);
                return false;
            }

            // Resolve the write characteristic now so a missing one fails the connection instead of every command.
            if (!await Client.SubscribeAsync(channel.Write, null, cancellationToken))
            {
                Logger.LogError("Write characteristic {Uuid} not found", channel.Write);
                return false;
            }

            _channel = channel;
            _notifications = 0;
            _writes = 0;
            _failedWrites = 0;
            _sessionAge.Restart();
            Logger.LogInformation("Serial channel {Service} ready; polling every {Interval} ms", channel.Service,
                PollInterval.TotalMilliseconds);
            _sessionCts = new CancellationTokenSource();
            _sessionToken = _sessionCts.Token;
            // The connect timeout bounds setup; ending the session cancels any write it left running.
            await OnConnectedAsync().WaitAsync(cancellationToken);
            _polling = RunPollingAsync(_sessionToken);

            // Polling retries the handshake; the caller's timeout bounds the wait.
            while (!IsReady)
                await Task.Delay(ReadyCheckInterval, cancellationToken);
            return true;
        }

        Logger.LogError("No supported serial service found on {Device}", deviceName);
        return false;
    }

    /// <summary>
    /// Stops polling and waits for an in-progress tick, so no request from this session is written after it ends.
    /// </summary>
    public override async Task DisconnectAsync()
    {
        if (_sessionAge.IsRunning)
        {
            Logger.LogInformation(
                "Serial session ended after {Age:F1} s: {Notifications} notifications (last {Since:F1} s ago), {Writes} writes, {Failed} failed",
                _sessionAge.Elapsed.TotalSeconds, Interlocked.Read(ref _notifications), SecondsSinceLastNotification(), _writes, _failedWrites);
            _sessionAge.Reset();
        }

        _sessionCts?.Cancel();
        if (_polling != null)
            await _polling;
        _polling = null;
        _sessionCts?.Dispose();
        _sessionCts = null;

        _channel = null;
        await Client.DisconnectAsync();

        await _protocolGate.WaitAsync();
        try
        {
            OnDisconnected();
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    // Serial protocols have no FTMS-style control handshake.
    public override Task RequestControlAsync() => Task.CompletedTask;

    public override Task GetTreadmillConfigurationAsync() => Task.CompletedTask;

    public override async Task SendCommandAsync(byte[] command) => await WriteAsync(command);

    /// <summary>
    /// Writes a frame to the session's write characteristic; returns whether it reached the device. Ending the session
    /// cancels it.
    /// </summary>
    protected async Task<bool> WriteAsync(byte[] frame)
    {
        var channel = _channel;
        if (channel == null)
            return false;

        Interlocked.Increment(ref _writes);
        if (await Client.WriteAsync(channel.Write, frame, cancellationToken: _sessionToken))
            return true;

        if (!_sessionToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failedWrites);
            Logger.LogWarning("Write {Frame} failed {Age:F1} s into the session (link up: {Connected})",
                Convert.ToHexString(frame), _sessionAge.Elapsed.TotalSeconds, Client.IsConnected);
        }
        return false;
    }

    private double SecondsSinceLastNotification() =>
        Interlocked.Read(ref _notifications) == 0 ? double.NaN
            : TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastNotificationAt)).TotalSeconds;

    private void HandleNotification(byte[] data)
    {
        var count = Interlocked.Increment(ref _notifications);
        var gap = count == 1 ? 0 : SecondsSinceLastNotification();
        Interlocked.Exchange(ref _lastNotificationAt, Environment.TickCount64);
        if (count == 1)
            Logger.LogInformation("First notification {Age:F1} s into the session", _sessionAge.Elapsed.TotalSeconds);
        else if (gap >= NotificationGapWarning.TotalSeconds)
            Logger.LogWarning("No notification for {Gap:F1} s before this one", gap);

        _protocolGate.Wait();
        try
        {
            OnNotification(data);
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    private async Task RunPollingAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!Client.IsConnected)
                    continue;

                await _protocolGate.WaitAsync(token);
                try
                {
                    await PollAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Treadmill poll failed");
                }
                finally
                {
                    _protocolGate.Release();
                }
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
