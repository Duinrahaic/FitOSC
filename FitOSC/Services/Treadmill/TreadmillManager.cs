using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Manages treadmill services (FTMS and the vendor drivers)
/// and provides a unified interface for connecting and controlling treadmills.
/// </summary>
/// <remarks>
/// Uses dependency injection for logging and app state.
/// Keeps track of the currently active treadmill service.
/// SINGLE ENTRY POINT for all treadmill operations.
/// </remarks>
public class TreadmillManager(ILoggerFactory loggerFactory, AppStateService appState, WindowsBluetoothClient bluetoothClient)
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    // A first connect retries a few times within the connection timeout before it reports a failure.
    private const int ConnectAttempts = 3;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(1);

    // A dropped treadmill usually comes back within seconds, so reconnecting starts at once and backs off only a little.
    private static readonly TimeSpan FirstReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<TreadmillManager>();

    /// <summary>Serializes connect, reconnect and disconnect so only one session exists at a time.</summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    /// <summary>
    /// The currently active treadmill service implementation.
    /// </summary>
    private ITreadmillService? _active = null;

    /// <summary>
    /// Cancelled when the active session ends, stopping any in-flight connect or reconnect. Never disposed, so it can be
    /// cancelled without the lock and its token read by late callbacks.
    /// </summary>
    private CancellationTokenSource? _session;

    // The treadmill the session is for, from its first attempt until it ends.
    private string? _sessionDevice;
    private TreadmillType _sessionType;

    private int _reconnecting;

    // When the current connection came up, for logging how long it lasted before a drop.
    private long _connectedSince;

    /// <summary>
    /// Connects to a treadmill by device name and type. Asking for the treadmill the session already has, connected or
    /// still connecting, changes nothing; any other treadmill replaces the session.
    /// </summary>
    /// <param name="deviceName">The advertised BLE device name to connect to.</param>
    /// <param name="type">The treadmill type (default is FTMS).</param>
    public async Task ConnectAsync(string deviceName, TreadmillType type = TreadmillType.FTMS)
    {
        if (IsSessionFor(deviceName, type))
            return;

        // Stop an in-flight connect or reconnect rather than waiting for it to finish.
        _session?.Cancel();
        await _sessionLock.WaitAsync();
        try
        {
            if (IsSessionFor(deviceName, type))
                return;

            await EndSessionAsync();

            var driver = TreadmillDrivers.Find(type);
            if (driver == null)
            {
                _logger.LogError("No treadmill driver is registered for {Type}", type);
                PublishStatus(ConnectionStatus.Error, $"No driver is available for {type}");
                return;
            }

            var session = new CancellationTokenSource();
            _session = session;
            _sessionDevice = deviceName;
            _sessionType = type;

            var started = Stopwatch.GetTimestamp();
            (ConnectionStatus Status, string? Reason) result;
            for (var attempt = 1; ; attempt++)
            {
                PublishStatus(ConnectionStatus.Connecting, attempt == 1
                    ? $"Connecting to {deviceName}"
                    : $"Connecting to {deviceName} (attempt {attempt} of {ConnectAttempts})");

                result = await TryConnectAsync(driver, deviceName, session.Token);
                if (result.Status == ConnectionStatus.Connected || session.IsCancellationRequested ||
                    attempt == ConnectAttempts || Stopwatch.GetElapsedTime(started) >= ConnectionTimeout)
                    break;

                try
                {
                    await Task.Delay(ConnectRetryDelay, session.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (result.Status != ConnectionStatus.Connected)
            {
                // A session cancelled by another connect or a disconnect leaves the status to that call.
                if (session.IsCancellationRequested)
                    return;

                await EndSessionAsync();
                PublishStatus(result.Status, result.Reason);
                return;
            }

            PublishStatus(ConnectionStatus.Connected, null, deviceName);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool IsSessionFor(string deviceName, TreadmillType type) =>
        _session is { IsCancellationRequested: false } && _sessionType == type &&
        string.Equals(_sessionDevice?.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Publishes the Bluetooth status with the connected device's name and a reason the user can read.</summary>
    private void PublishStatus(ConnectionStatus status, string? detail, string? deviceName = null)
    {
        if (status == ConnectionStatus.Connected)
            Interlocked.Exchange(ref _connectedSince, Stopwatch.GetTimestamp());
        appState.SetConnectedDeviceName(deviceName);
        appState.PublishBluetoothStatus(status, detail);
    }

    /// <summary>
    /// Connects and initializes a fresh instance of the driver within <see cref="ConnectionTimeout"/>. On success it
    /// becomes the active service; on any failure it is disposed and the reason is returned. Callers hold the session lock.
    /// </summary>
    private async Task<(ConnectionStatus Status, string? Reason)> TryConnectAsync(TreadmillDriver driver, string deviceName,
        CancellationToken session)
    {
        ITreadmillService service;
        try
        {
            service = TreadmillDrivers.Create(driver, loggerFactory, appState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the {Driver} driver", driver.Name);
            return (ConnectionStatus.Error, $"The {driver.Name} driver could not start");
        }

        // Active before connecting so a drop during initialization is handled once the lock is free.
        _active = service;
        service.ConnectionLost += () => OnConnectionLost(service, driver, deviceName, session);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session);
        timeout.CancelAfter(ConnectionTimeout);

        var started = Stopwatch.GetTimestamp();
        (ConnectionStatus, string?) result;
        try
        {
            // Bounded here too, so a driver step that ignores the token cannot hold the session lock past the timeout.
            if (await service.ConnectAsync(deviceName, timeout.Token).WaitAsync(timeout.Token))
            {
                await service.RequestControlAsync().WaitAsync(timeout.Token);
                if (service.IsConnected)
                {
                    _logger.LogInformation("Attempt for {Device} connected in {Elapsed:F1} s", deviceName,
                        Stopwatch.GetElapsedTime(started).TotalSeconds);
                    return (ConnectionStatus.Connected, null);
                }

                _logger.LogWarning("{Device} set up but the link was already down", deviceName);
            }

            _logger.LogError("Treadmill {Device} did not finish connecting", deviceName);
            result = (ConnectionStatus.Disconnected, $"Couldn't find or set up {deviceName}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!session.IsCancellationRequested)
                _logger.LogError("Connection timed out after {Timeout} seconds", ConnectionTimeout.TotalSeconds);
            result = session.IsCancellationRequested
                ? (ConnectionStatus.Disconnected, null)
                : (ConnectionStatus.Error, $"Timed out connecting to {deviceName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during treadmill connection");
            result = (ConnectionStatus.Error, $"Error connecting to {deviceName}: {ex.Message}");
        }

        _logger.LogWarning("Attempt for {Device} failed after {Elapsed:F1} s: {Status} ({Reason})", deviceName,
            Stopwatch.GetElapsedTime(started).TotalSeconds, result.Item1, result.Item2 ?? "cancelled");
        await RetireActiveAsync();
        return result;
    }

    private void OnConnectionLost(ITreadmillService service, TreadmillDriver driver, string deviceName, CancellationToken session)
    {
        if (session.IsCancellationRequested || service != _active || Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;

        _ = ReconnectAsync(driver, deviceName, session);
    }

    /// <summary>
    /// Retires the dropped service and connects fresh instances of its driver, the first at once and then backing off
    /// up to <see cref="MaxReconnectDelay"/>, until one succeeds or the session is ended by <see cref="DisconnectAsync"/>
    /// or a connect to another treadmill.
    /// </summary>
    private async Task ReconnectAsync(TreadmillDriver driver, string deviceName, CancellationToken session)
    {
        try
        {
            var lostAt = Stopwatch.GetTimestamp();
            _logger.LogWarning("Lost connection to {Device} after {Uptime:F1} s connected; reconnecting", deviceName,
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _connectedSince), lostAt).TotalSeconds);
            await _sessionLock.WaitAsync(session);
            try
            {
                // Retire first so no late notification can republish the old reading. Connecting is published before
                // the idle reading so listeners such as history see a reconnect rather than a stopped treadmill.
                await RetireActiveAsync();
                PublishStatus(ConnectionStatus.Connecting, $"Lost {deviceName}; reconnecting");
                PublishIdleTreadmill();
            }
            finally
            {
                _sessionLock.Release();
            }

            var delay = TimeSpan.Zero;
            for (var attempt = 1; ; attempt++)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, session);

                await _sessionLock.WaitAsync(session);
                try
                {
                    PublishStatus(ConnectionStatus.Connecting, $"Reconnecting to {deviceName} (attempt {attempt})");
                    if ((await TryConnectAsync(driver, deviceName, session)).Status == ConnectionStatus.Connected)
                    {
                        // Losses are ignored while reconnecting, so re-check the link once they are handled again.
                        Interlocked.Exchange(ref _reconnecting, 0);
                        if (_active?.IsConnected == true)
                        {
                            PublishStatus(ConnectionStatus.Connected, null, deviceName);
                            _logger.LogInformation("Reconnected to {Device} after {Attempts} attempt(s) in {Elapsed:F1} s", deviceName,
                                attempt, Stopwatch.GetElapsedTime(lostAt).TotalSeconds);
                            return;
                        }

                        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
                            return; // A new loss started its own reconnect.
                        await RetireActiveAsync();
                    }
                }
                finally
                {
                    _sessionLock.Release();
                }

                delay = delay == TimeSpan.Zero
                    ? FirstReconnectDelay
                    : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // The session was ended deliberately.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while reconnecting to {Device}", deviceName);
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>Ends the active session, if any, and clears everything it published. Callers hold the session lock.</summary>
    private async Task EndSessionAsync()
    {
        _session?.Cancel();
        _session = null;
        _sessionDevice = null;
        Interlocked.Exchange(ref _reconnecting, 0);

        await RetireActiveAsync();
        PublishIdleTreadmill();
        appState.PublishTreadmillConfiguration(new TreadmillConfiguration());
    }

    /// <summary>Disconnects and disposes the active service; it publishes nothing afterwards. Callers hold the session lock.</summary>
    private async Task RetireActiveAsync()
    {
        var active = _active;
        _active = null;
        if (active == null)
            return;

        try
        {
            await active.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while disconnecting the treadmill");
        }
    }

    /// <summary>Replaces the last reading with an empty one so stale speed and state are not left on screen or in OSC.</summary>
    private void PublishIdleTreadmill()
    {
        appState.PublishTreadmillData(new TreadmillTelemetry());
        appState.PublishTreadmillState(TreadmillState.Stopped);
    }

    /// <summary>
    /// Starts the treadmill (begin workout).
    /// </summary>
    public async Task StartAsync()
    {
        if (Accepting(TreadmillCommands.Start, out var active, out _))
            await active.StartAsync();
    }

    /// <summary>
    /// Pauses the treadmill workout.
    /// </summary>
    public async Task PauseAsync()
    {
        if (Accepting(TreadmillCommands.Pause, out var active, out _))
            await active.PauseAsync();
    }

    /// <summary>
    /// Stops the treadmill workout.
    /// </summary>
    public async Task StopAsync()
    {
        if (Accepting(TreadmillCommands.Stop, out var active, out _))
            await active.StopAsync();
    }

    /// <summary>
    /// Sets treadmill speed, within the range the treadmill reported.
    /// </summary>
    /// <param name="speed">Speed in km/h.</param>
    public async Task SetSpeedAsync(decimal speed)
    {
        if (Accepting(TreadmillCommands.Speed, out var active, out var configuration))
            await active.SetSpeedAsync(ClampToRange(speed, configuration.MinSpeed, configuration.MaxSpeed));
    }

    /// <summary>
    /// Sets treadmill incline, within the range the treadmill reported.
    /// </summary>
    /// <param name="incline">Incline in percent grade.</param>
    public async Task SetInclineAsync(decimal incline)
    {
        if (Accepting(TreadmillCommands.Incline, out var active, out var configuration))
            await active.SetInclineAsync(ClampToRange(incline, configuration.MinIncline, configuration.MaxIncline));
    }

    /// <summary>
    /// Every control (UI, OSC, MIDI, debug console) comes through here, so a command the connected treadmill's
    /// published configuration leaves out is dropped for all of them alike.
    /// </summary>
    private bool Accepting(TreadmillCommands command, [NotNullWhen(true)] out ITreadmillService? active,
        out TreadmillConfiguration configuration)
    {
        active = _active;
        configuration = appState.LatestConfiguration ?? new TreadmillConfiguration();
        if (active == null)
            return false;
        if (configuration.Accepts(command))
            return true;

        _logger.LogDebug("The connected treadmill does not take {Command} commands from FitOSC", command);
        return false;
    }

    /// <summary>Limits a target to the treadmill's reported range; one that reported none is left to its driver.</summary>
    private static decimal ClampToRange(decimal value, decimal min, decimal max) =>
        max > min ? Math.Clamp(value, min, max) : value;

    /// <summary>
    /// Requests control of the treadmill (FTMS spec requires control before sending commands).
    /// </summary>
    public async Task RequestControlAsync() =>
        await (_active?.RequestControlAsync() ?? Task.CompletedTask);

    /// <summary>
    /// Sends a raw command to the treadmill.
    /// </summary>
    /// <param name="data"></param>
    public async Task SendCommandAsync(byte[] data) =>
        await (_active?.SendCommandAsync(data) ?? Task.CompletedTask);

    /// <summary>
    /// Disconnects from the current treadmill and clears the active service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        // Stop an in-flight connect or reconnect rather than waiting for it to finish.
        _session?.Cancel();
        await _sessionLock.WaitAsync();
        try
        {
            await EndSessionAsync();
            PublishStatus(ConnectionStatus.Disconnected, null);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Scans for available Bluetooth treadmill devices.
    /// </summary>
    /// <param name="duration">How long to scan for devices.</param>
    /// <returns>List of discovered device names.</returns>
    public async Task<IEnumerable<string>> ScanAsync(TimeSpan duration) =>
        await bluetoothClient.ScanAsync(duration);

    /// <summary>
    /// Gets detailed information about the currently connected device.
    /// </summary>
    /// <returns>Device information string, or error message if not connected.</returns>
    public async Task<string> GetDeviceInfoAsync() =>
        await (_active?.GetDeviceInfoAsync() ?? Task.FromResult("No device connected"));
}
