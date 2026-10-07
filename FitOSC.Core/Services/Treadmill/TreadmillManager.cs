using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Manages treadmill services (FTMS, WalkingPad, etc.)
/// and provides a unified interface for connecting and controlling treadmills.
/// </summary>
/// <remarks>
/// Uses dependency injection for logging and app state.
/// Keeps track of the currently active treadmill service.
/// SINGLE ENTRY POINT for all treadmill operations.
/// </remarks>
public class TreadmillManager(ILoggerFactory loggerFactory, AppStateService appState, IBluetoothClientFactory bluetoothClientFactory)
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30); // Total timeout for entire connection process

    private readonly ILogger _logger = loggerFactory.CreateLogger<TreadmillManager>();

    /// <summary>
    /// Serializes connect and disconnect so only one of them changes the active service at a time.
    /// </summary>
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Guards <see cref="_connectCancellation"/>, which DisconnectAsync cancels without holding the connection lock.
    /// </summary>
    private readonly Lock _connectCancellationGate = new();
    private CancellationTokenSource? _connectCancellation;

    /// <summary>
    /// The currently active treadmill service implementation (FTMS or WalkingPad).
    /// </summary>
    private ITreadmillService? _active = null;

    /// <summary>
    /// Connects to a treadmill by device name and type.
    /// If a service is already active, it disconnects before switching.
    /// Waits for any connect or disconnect already in progress.
    /// </summary>
    /// <param name="deviceName">The advertised BLE device name to connect to.</param>
    /// <param name="type">The treadmill type (default is FTMS).</param>
    /// <param name="cancellationToken">Cancels the connection attempt, including in-flight Bluetooth operations.</param>
    public async Task ConnectAsync(string deviceName, TreadmillType type = TreadmillType.FTMS, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // If already connected to a treadmill, disconnect it first
            await ReleaseActiveAsync().ConfigureAwait(false);

            // Create the appropriate service based on treadmill type
            ITreadmillService? service = type switch
            {
                TreadmillType.FTMS => new FTMSTreadmillService(loggerFactory.CreateLogger<FTMSTreadmillService>(), appState, bluetoothClientFactory.Create()),
                TreadmillType.WalkingPad => new WalkingPadTreadmillService(appState, bluetoothClientFactory.Create()),
                _ => null
            };

            if (service == null)
            {
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
                return;
            }

            using var timeout = new CancellationTokenSource(ConnectionTimeout);
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            lock (_connectCancellationGate) _connectCancellation = connectCancellation;
            try
            {
                await ConnectServiceAsync(service, deviceName, connectCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectCancellation.IsCancellationRequested)
            {
                if (_active == service)
                    _active = null;

                appState.SetConnectedDeviceName(null);
                if (timeout.IsCancellationRequested)
                {
                    _logger.LogError("Connection timed out after {Timeout} seconds", ConnectionTimeout.TotalSeconds);
                    appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
                }
                else
                {
                    appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                // On error, publish final state: Error (single UI update)
                _logger.LogError(ex, "Error during treadmill connection");
                appState.SetConnectedDeviceName(null);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
            }
            finally
            {
                lock (_connectCancellationGate) _connectCancellation = null;

                // Every connect call above has completed, so nothing still uses a service that did not become active.
                if (_active != service)
                    await service.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects the given service and makes it the active one.
    /// A connected service stays active even if it rejects control or sends no telemetry, matching the Error status.
    /// </summary>
    private async Task ConnectServiceAsync(ITreadmillService service, string deviceName, CancellationToken cancellationToken)
    {
        // Show scanning state
        appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connecting);

        await service.ConnectAsync(deviceName, cancellationToken).ConfigureAwait(false);

        if (!service.IsConnected)
        {
            // Connection failed, publish final state: Disconnected (single UI update)
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
            return;
        }

        _active = service;

        // Request control first
        try
        {
            await service.RequestControlAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to request control from treadmill");
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
            return;
        }

        // Verify we have telemetry data (characteristics are working)
        var currentState = appState.GetCurrentAppStateInfo();
        if (!currentState.TreadmillTelemetry.Values.Any())
        {
            _logger.LogError("Connected but no telemetry characteristics available");
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
            return;
        }

        // Publish final state: Connected (single UI update)
        appState.SetConnectedDeviceName(deviceName);
        appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
    }

    /// <summary>
    /// Starts the treadmill (begin workout).
    /// </summary>
    public async Task StartAsync() =>
        await (_active?.StartAsync()  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Pauses the treadmill workout.
    /// </summary>
    public async Task PauseAsync() =>
        await (_active?.PauseAsync()  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Stops the treadmill workout.
    /// </summary>
    public async Task StopAsync() =>
        await (_active?.StopAsync()  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Sets treadmill speed.
    /// </summary>
    /// <param name="speed">Speed in km/h (telemetryProperty) or mph (imperial, depending on settings).</param>
    public async Task SetSpeedAsync(decimal speed) =>
        await (_active?.SetSpeedAsync(speed)  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Sets treadmill incline.
    /// </summary>
    /// <param name="incline">Incline in percent grade.</param>
    public async Task SetInclineAsync(decimal incline) =>
        await (_active?.SetInclineAsync(incline)  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Requests control of the treadmill (FTMS spec requires control before sending commands).
    /// </summary>
    public async Task RequestControlAsync() =>
        await (_active?.RequestControlAsync()  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Sends a raw command to the treadmill.
    /// </summary>
    /// <param name="data"></param>
    public async Task SendCommandAsync(byte[] data) =>
        await (_active?.SendCommandAsync(data)  ?? Task.CompletedTask).ConfigureAwait(false);

    /// <summary>
    /// Disconnects from the current treadmill and clears the active service.
    /// Cancels a connection attempt in progress and waits for it to finish first.
    /// </summary>
    public async Task DisconnectAsync()
    {
        lock (_connectCancellationGate) _connectCancellation?.Cancel();

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active == null) return;

            await ReleaseActiveAsync().ConfigureAwait(false);
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Disconnects and disposes the active service, if any.
    /// Callers must hold the connection lock.
    /// </summary>
    private async Task ReleaseActiveAsync()
    {
        if (_active == null) return;

        var service = _active;
        _active = null;
        try
        {
            try
            {
                await service.DisconnectAsync().ConfigureAwait(false);
            }
            finally
            {
                await service.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
            _logger.LogError(ex, "Failed to disconnect the active treadmill.");
            throw;
        }
    }

    /// <summary>
    /// Scans for available Bluetooth treadmill devices.
    /// </summary>
    /// <param name="duration">How long to scan for devices.</param>
    /// <returns>List of discovered device names.</returns>
    public async Task<IEnumerable<string>> ScanAsync(TimeSpan duration)
    {
        await using var client = bluetoothClientFactory.Create();
        var devices = await client.ScanAsync(duration).ConfigureAwait(false);
        return devices.Select(d => d.Name).ToList();
    }

    /// <summary>
    /// Scans for nearby Bluetooth LE devices, including the services each one advertises.
    /// </summary>
    public async Task<IReadOnlyList<BluetoothAdvertisement>> ScanDevicesAsync(TimeSpan duration)
    {
        await using var client = bluetoothClientFactory.Create();
        return await client.ScanAsync(duration).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets detailed information about the currently connected device.
    /// </summary>
    /// <returns>Device information string, or error message if not connected.</returns>
    public async Task<string> GetDeviceInfoAsync() =>
        _active != null ? await _active.GetDeviceInfoAsync().ConfigureAwait(false) : "No device connected";
}
