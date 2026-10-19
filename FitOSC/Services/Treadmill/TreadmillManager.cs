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
    private static readonly TimeSpan FirstReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

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

    private int _reconnecting;

    /// <summary>
    /// Connects to a treadmill by device name and type.
    /// If a service is already active, it disconnects before switching.
    /// </summary>
    /// <param name="deviceName">The advertised BLE device name to connect to.</param>
    /// <param name="type">The treadmill type (default is FTMS).</param>
    public async Task ConnectAsync(string deviceName, TreadmillType type = TreadmillType.FTMS)
    {
        // Stop an in-flight connect or reconnect rather than waiting for it to finish.
        _session?.Cancel();
        await _sessionLock.WaitAsync();
        try
        {
            await EndSessionAsync();

            var driver = TreadmillDrivers.Find(type);
            if (driver == null)
            {
                _logger.LogError("No treadmill driver is registered for {Type}", type);
                appState.SetConnectedDeviceName(null);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
                return;
            }

            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connecting);

            var session = new CancellationTokenSource();
            _session = session;

            var status = await TryConnectAsync(driver, deviceName, session.Token);
            if (status != ConnectionStatus.Connected)
            {
                await EndSessionAsync();
                appState.SetConnectedDeviceName(null);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, status);
                return;
            }

            appState.SetConnectedDeviceName(deviceName);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Connects and initializes a fresh instance of the driver within <see cref="ConnectionTimeout"/>. On success it
    /// becomes the active service; on any failure it is disposed. Callers hold the session lock.
    /// </summary>
    private async Task<ConnectionStatus> TryConnectAsync(TreadmillDriver driver, string deviceName, CancellationToken session)
    {
        ITreadmillService service;
        try
        {
            service = TreadmillDrivers.Create(driver, loggerFactory, appState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the {Driver} driver", driver.Name);
            return ConnectionStatus.Error;
        }

        // Active before connecting so a drop during initialization is handled once the lock is free.
        _active = service;
        service.ConnectionLost += () => OnConnectionLost(service, driver, deviceName, session);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session);
        timeout.CancelAfter(ConnectionTimeout);

        ConnectionStatus status;
        try
        {
            // Bounded here too, so a driver step that ignores the token cannot hold the session lock past the timeout.
            if (await service.ConnectAsync(deviceName, timeout.Token).WaitAsync(timeout.Token))
            {
                await service.RequestControlAsync().WaitAsync(timeout.Token);
                if (service.IsConnected)
                    return ConnectionStatus.Connected;
            }

            _logger.LogError("Treadmill {Device} did not finish connecting", deviceName);
            status = ConnectionStatus.Disconnected;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!session.IsCancellationRequested)
                _logger.LogError("Connection timed out after {Timeout} seconds", ConnectionTimeout.TotalSeconds);
            status = session.IsCancellationRequested ? ConnectionStatus.Disconnected : ConnectionStatus.Error;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during treadmill connection");
            status = ConnectionStatus.Error;
        }

        await RetireActiveAsync();
        return status;
    }

    private void OnConnectionLost(ITreadmillService service, TreadmillDriver driver, string deviceName, CancellationToken session)
    {
        if (session.IsCancellationRequested || service != _active || Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;

        _ = ReconnectAsync(driver, deviceName, session);
    }

    /// <summary>
    /// Retires the dropped service and connects fresh instances of its driver, backing off until one succeeds or the
    /// session is ended by <see cref="DisconnectAsync"/> or a new <see cref="ConnectAsync"/>.
    /// </summary>
    private async Task ReconnectAsync(TreadmillDriver driver, string deviceName, CancellationToken session)
    {
        try
        {
            _logger.LogWarning("Lost connection to {Device}; reconnecting", deviceName);
            await _sessionLock.WaitAsync(session);
            try
            {
                // Retire first so no late notification can republish the old reading.
                await RetireActiveAsync();
                PublishIdleTreadmill();
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connecting);
            }
            finally
            {
                _sessionLock.Release();
            }

            var delay = FirstReconnectDelay;
            while (true)
            {
                await Task.Delay(delay, session);

                await _sessionLock.WaitAsync(session);
                try
                {
                    if (await TryConnectAsync(driver, deviceName, session) == ConnectionStatus.Connected)
                    {
                        // Losses are ignored while reconnecting, so re-check the link once they are handled again.
                        Interlocked.Exchange(ref _reconnecting, 0);
                        if (_active?.IsConnected == true)
                        {
                            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
                            _logger.LogInformation("Reconnected to {Device}", deviceName);
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

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
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
    public async Task StartAsync() =>
        await (_active?.StartAsync() ?? Task.CompletedTask);

    /// <summary>
    /// Pauses the treadmill workout.
    /// </summary>
    public async Task PauseAsync() =>
        await (_active?.PauseAsync() ?? Task.CompletedTask);

    /// <summary>
    /// Stops the treadmill workout.
    /// </summary>
    public async Task StopAsync() =>
        await (_active?.StopAsync() ?? Task.CompletedTask);

    /// <summary>
    /// Sets treadmill speed.
    /// </summary>
    /// <param name="speed">Speed in km/h (telemetryProperty) or mph (imperial, depending on settings).</param>
    public async Task SetSpeedAsync(decimal speed) =>
        await (_active?.SetSpeedAsync(speed) ?? Task.CompletedTask);

    /// <summary>
    /// Sets treadmill incline.
    /// </summary>
    /// <param name="incline">Incline in percent grade.</param>
    public async Task SetInclineAsync(decimal incline) =>
        await (_active?.SetInclineAsync(incline) ?? Task.CompletedTask);

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
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
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
        await bluetoothClient.GetDeviceInfo();
}
