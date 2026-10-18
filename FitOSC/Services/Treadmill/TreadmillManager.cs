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
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
                return;
            }

            var service = TreadmillDrivers.Create(driver, loggerFactory, appState);

            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connecting);

            // The session exists before connecting so a drop during initialization is handled once the lock is free.
            var session = new CancellationTokenSource();
            var token = session.Token;
            _active = service;
            _session = session;
            service.ConnectionLost += () => OnConnectionLost(service, deviceName, token);

            var status = await TryConnectAsync(service, deviceName, token);
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
    /// Connects and initializes the service within <see cref="ConnectionTimeout"/>. Any failure leaves it disconnected.
    /// </summary>
    private async Task<ConnectionStatus> TryConnectAsync(ITreadmillService service, string deviceName, CancellationToken session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session);
        timeout.CancelAfter(ConnectionTimeout);

        try
        {
            if (await service.ConnectAsync(deviceName, timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                await service.RequestControlAsync();
                if (service.IsConnected)
                    return ConnectionStatus.Connected;
            }

            _logger.LogError("Treadmill {Device} did not finish connecting", deviceName);
            await service.DisconnectAsync();
            return ConnectionStatus.Disconnected;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!session.IsCancellationRequested)
                _logger.LogError("Connection timed out after {Timeout} seconds", ConnectionTimeout.TotalSeconds);
            await service.DisconnectAsync();
            return session.IsCancellationRequested ? ConnectionStatus.Disconnected : ConnectionStatus.Error;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during treadmill connection");
            await service.DisconnectAsync();
            return ConnectionStatus.Error;
        }
    }

    private void OnConnectionLost(ITreadmillService service, string deviceName, CancellationToken session)
    {
        if (session.IsCancellationRequested || Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;

        _ = ReconnectAsync(service, deviceName, session);
    }

    /// <summary>
    /// Tears the dropped session down and re-runs the full driver connect, backing off until it succeeds or the
    /// session is ended by <see cref="DisconnectAsync"/> or a new <see cref="ConnectAsync"/>.
    /// </summary>
    private async Task ReconnectAsync(ITreadmillService service, string deviceName, CancellationToken session)
    {
        try
        {
            _logger.LogWarning("Lost connection to {Device}; reconnecting", deviceName);
            await _sessionLock.WaitAsync(session);
            try
            {
                // Tear down first so no late notification can republish the old reading.
                await service.DisconnectAsync();
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
                    if (await TryConnectAsync(service, deviceName, session) == ConnectionStatus.Connected)
                    {
                        // Losses are ignored while reconnecting, so re-check the link once they are handled again.
                        Interlocked.Exchange(ref _reconnecting, 0);
                        if (service.IsConnected)
                        {
                            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
                            _logger.LogInformation("Reconnected to {Device}", deviceName);
                            return;
                        }

                        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
                            return; // A new loss started its own reconnect.
                        await service.DisconnectAsync();
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

        if (_active != null)
        {
            var active = _active;
            _active = null;
            await active.DisconnectAsync();
        }

        PublishIdleTreadmill();
        appState.PublishTreadmillConfiguration(new TreadmillConfiguration());
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
