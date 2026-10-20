using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;
using FitOSC.Utilities.BLE.ESPHome;

namespace FitOSC.Services.Treadmill;

public enum RelayCheckResult { Reachable, Busy }

/// <summary>
/// Manages treadmill services (FTMS and the vendor drivers)
/// and provides a unified interface for connecting and controlling treadmills.
/// </summary>
/// <remarks>
/// Uses dependency injection for logging and app state.
/// Keeps track of the currently active treadmill service.
/// SINGLE ENTRY POINT for all treadmill operations.
/// Each connection attempt gets a fresh Bluetooth client for the session's connection (this PC or an ESPHome relay),
/// owned by the driver instance it is given to.
/// </remarks>
public class TreadmillManager(ILoggerFactory loggerFactory, AppStateService appState,
    BluetoothClientFactory clientFactory, ConfigurationService configurationService)
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

    /// <summary>The Bluetooth client <see cref="_active"/> was created with; it is disposed along with the service.</summary>
    private BaseBluetoothClient? _activeClient;

    /// <summary>
    /// Cancelled when the active session ends, stopping any in-flight connect or reconnect. Never disposed, so it can be
    /// cancelled without the lock and its token read by late callbacks.
    /// </summary>
    private CancellationTokenSource? _session;

    // The treadmill the session is for, from its first attempt until it ends.
    private string? _sessionDevice;
    private TreadmillType _sessionType;
    private BluetoothConnectionSettings? _sessionConnection;

    private int _reconnecting;

    // When the current connection came up, for logging how long it lasted before a drop.
    private long _connectedSince;

    /// <summary>
    /// Why the last connect or scan failed, for the UI to show; null after one succeeds or a disconnect.
    /// </summary>
    public string? LastConnectionError { get; private set; }

    /// <summary>
    /// Connects to a treadmill by device name and type. Asking for the treadmill the session already has, connected or
    /// still connecting, changes nothing; any other treadmill replaces the session. A successful connect is saved as
    /// the treadmill to connect to next time.
    /// </summary>
    /// <param name="deviceName">The advertised BLE device name to connect to.</param>
    /// <param name="type">The treadmill type (default is FTMS).</param>
    /// <param name="connection">How to reach the treadmill; the saved connection when null.</param>
    public async Task ConnectAsync(string deviceName, TreadmillType type = TreadmillType.FTMS,
        BluetoothConnectionSettings? connection = null)
    {
        BluetoothConnectionSettings settings;
        try
        {
            settings = ResolveConnection(connection);
        }
        catch (ArgumentException ex)
        {
            LastConnectionError = ex.Message;
            PublishStatus(ConnectionStatus.Error, ex.Message);
            return;
        }

        if (IsSessionFor(deviceName, type, settings))
            return;

        // Stop an in-flight connect or reconnect rather than waiting for it to finish.
        _session?.Cancel();
        await _sessionLock.WaitAsync();
        try
        {
            if (IsSessionFor(deviceName, type, settings))
                return;

            LastConnectionError = null;
            await EndSessionAsync();

            var driver = TreadmillDrivers.Find(type);
            if (driver == null)
            {
                _logger.LogError("No treadmill driver is registered for {Type}", type);
                LastConnectionError = $"No driver is available for {type}";
                PublishStatus(ConnectionStatus.Error, LastConnectionError);
                return;
            }

            var session = new CancellationTokenSource();
            _session = session;
            _sessionDevice = deviceName;
            _sessionType = type;
            _sessionConnection = settings;

            var started = Stopwatch.GetTimestamp();
            (ConnectionStatus Status, string? Reason) result;
            for (var attempt = 1; ; attempt++)
            {
                PublishStatus(ConnectionStatus.Connecting, attempt == 1
                    ? $"Connecting to {deviceName}"
                    : $"Connecting to {deviceName} (attempt {attempt} of {ConnectAttempts})");

                result = await TryConnectAsync(driver, deviceName, settings, session.Token);
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
                {
                    LastConnectionError = $"Connecting to {deviceName} was cancelled";
                    return;
                }

                await EndSessionAsync();
                LastConnectionError = result.Reason ?? $"Couldn't connect to {deviceName}";
                PublishStatus(result.Status, result.Reason);
                return;
            }

            PublishStatus(ConnectionStatus.Connected, null, deviceName);
            SaveTreadmill(deviceName, type, settings);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool IsSessionFor(string deviceName, TreadmillType type, BluetoothConnectionSettings connection) =>
        _session is { IsCancellationRequested: false } && _sessionType == type && _sessionConnection == connection &&
        string.Equals(_sessionDevice?.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The given or saved connection, trimmed and validated; throws <see cref="ArgumentException"/> with a reason.</summary>
    private BluetoothConnectionSettings ResolveConnection(BluetoothConnectionSettings? connection)
    {
        var selected = connection ?? configurationService.GetConfiguration().Treadmill.Connection;
        var settings = selected with { Host = selected.Host.Trim(), EncryptionKey = selected.EncryptionKey.Trim() };
        settings.Validate();
        return settings;
    }

    /// <summary>Saves a treadmill that connected as the one to connect to next time.</summary>
    private void SaveTreadmill(string deviceName, TreadmillType type, BluetoothConnectionSettings connection)
    {
        var config = configurationService.GetConfiguration();
        config.Treadmill.Connection = connection with { };
        config.Treadmill.LastDeviceName = deviceName;
        config.Treadmill.TreadmillType = type;
        configurationService.SaveConfiguration(config);
    }

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
        BluetoothConnectionSettings connection, CancellationToken session)
    {
        var client = clientFactory.Create(connection);
        ITreadmillService service;
        try
        {
            service = TreadmillDrivers.Create(driver, loggerFactory, appState, client);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the {Driver} driver", driver.Name);
            await client.DisposeAsync();
            return (ConnectionStatus.Error, $"The {driver.Name} driver could not start");
        }

        // Active before connecting so a drop during initialization is handled once the lock is free.
        _active = service;
        _activeClient = client;
        service.ConnectionLost += () => OnConnectionLost(service, driver, deviceName, connection, session);

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

    private void OnConnectionLost(ITreadmillService service, TreadmillDriver driver, string deviceName,
        BluetoothConnectionSettings connection, CancellationToken session)
    {
        if (session.IsCancellationRequested || service != _active || Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;

        _ = ReconnectAsync(driver, deviceName, connection, session);
    }

    /// <summary>
    /// Retires the dropped service and connects fresh instances of its driver, the first at once and then backing off
    /// up to <see cref="MaxReconnectDelay"/>, until one succeeds or the session is ended by <see cref="DisconnectAsync"/>
    /// or a connect to another treadmill.
    /// </summary>
    private async Task ReconnectAsync(TreadmillDriver driver, string deviceName, BluetoothConnectionSettings connection,
        CancellationToken session)
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
                    if ((await TryConnectAsync(driver, deviceName, connection, session)).Status == ConnectionStatus.Connected)
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
        _sessionConnection = null;
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
        _activeClient = null;
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
            LastConnectionError = null;
            PublishStatus(ConnectionStatus.Disconnected, null);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Scans for available Bluetooth treadmill devices. A relay the active treadmill is connected through is scanned
    /// over that session, since an ESPHome relay streams advertisements to one API session only; any other connection
    /// gets a client for the scan alone.
    /// </summary>
    /// <param name="duration">How long to scan for devices.</param>
    /// <param name="connection">Where to scan; the saved connection when null.</param>
    /// <returns>List of discovered device names.</returns>
    public async Task<IEnumerable<string>> ScanAsync(TimeSpan duration, BluetoothConnectionSettings? connection = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            LastConnectionError = null;
            var settings = ResolveConnection(connection);
            if (_activeClient is ESPHomeBluetoothClient { IsRelayConnected: true } live && _sessionConnection == settings)
                return await live.ScanAsync(duration);

            await using var client = clientFactory.Create(settings);
            return await client.ScanAsync(duration);
        }
        catch (Exception ex)
        {
            LastConnectionError = ex.Message;
            throw;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Checks that an ESPHome relay accepts an encrypted API session, without touching the treadmill, its Bluetooth
    /// client, or the saved configuration. A live relay session for the same settings answers directly; otherwise a
    /// temporary probe session is opened. Returns <see cref="RelayCheckResult.Busy"/> at once while a connect, scan or
    /// disconnect is running, for the caller to check again later. Throws with a user-facing reason when the relay is
    /// not usable.
    /// </summary>
    public async Task<RelayCheckResult> CheckRelayAsync(BluetoothConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var settings = connection with { Host = connection.Host.Trim(), EncryptionKey = connection.EncryptionKey.Trim() };
        if (!await _sessionLock.WaitAsync(0, cancellationToken))
            return RelayCheckResult.Busy;
        bool liveSession;
        try
        {
            liveSession = _activeClient is ESPHomeBluetoothClient { IsRelayConnected: true } && _sessionConnection == settings;
        }
        finally
        {
            _sessionLock.Release();
        }

        if (!liveSession)
            await ESPHomeRelayProbe.ProbeAsync(settings, cancellationToken);
        return RelayCheckResult.Reachable;
    }

    /// <summary>
    /// Gets detailed information about the currently connected device.
    /// </summary>
    /// <returns>Device information string, or error message if not connected.</returns>
    public async Task<string> GetDeviceInfoAsync() =>
        await (_active?.GetDeviceInfoAsync() ?? Task.FromResult("No device connected"));
}
