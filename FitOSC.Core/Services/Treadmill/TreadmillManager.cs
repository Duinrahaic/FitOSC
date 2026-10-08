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
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30); // Starts when connecting the replacement service

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
    private long _disconnectVersion;
    private Action? _activeLossHandler;
    private readonly List<Task> _lossTasks = [];
    private readonly List<Exception> _lossFailures = [];

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
        long disconnectVersion;
        lock (_connectCancellationGate) disconnectVersion = _disconnectVersion;
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource();
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        ITreadmillService? service = null;
        bool releaseStarted = false;
        bool replacementStarted = false;
        try
        {
            lock (_connectCancellationGate)
            {
                _connectCancellation = connectCancellation;
                if (disconnectVersion != _disconnectVersion) connectCancellation.Cancel();
            }

            connectCancellation.Token.ThrowIfCancellationRequested();
            // Release the old owner even when replacement was canceled during its cleanup.
            releaseStarted = true;
            await ReleaseActiveAsync().ConfigureAwait(false);
            connectCancellation.Token.ThrowIfCancellationRequested();

            service = type switch
            {
                TreadmillType.FTMS => new FTMSTreadmillService(loggerFactory.CreateLogger<FTMSTreadmillService>(), appState, bluetoothClientFactory.Create()),
                TreadmillType.WalkingPad => new WalkingPadTreadmillService(appState, bluetoothClientFactory.Create()),
                _ => null
            };
            connectCancellation.Token.ThrowIfCancellationRequested();
            if (service == null)
            {
                PublishDisconnected();
                return;
            }

            // Own the service during setup too: loss must cancel subscription and control work.
            lock (_connectCancellationGate)
            {
                _active = service;
                _activeLossHandler = () => OnConnectionLost(service);
                service.ConnectionLost += _activeLossHandler;
            }
            replacementStarted = true;
            timeout.CancelAfter(ConnectionTimeout);
            await ConnectServiceAsync(service, deviceName, connectCancellation.Token).ConfigureAwait(false);
            connectCancellation.Token.ThrowIfCancellationRequested();
            if (!service.IsConnected)
            {
                await ReleaseActiveAsync().ConfigureAwait(false);
                PublishDisconnected();
            }
        }
        catch (OperationCanceledException) when (connectCancellation.IsCancellationRequested)
        {
            if (!releaseStarted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            if (replacementStarted) await ReleaseActiveAsync().ConfigureAwait(false);
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
        catch (Exception ex) when (replacementStarted)
        {
            await ReleaseActiveAsync().ConfigureAwait(false);
            _logger.LogError(ex, "Error during treadmill connection");
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
        }
        finally
        {
            lock (_connectCancellationGate) _connectCancellation = null;
            try
            {
                // Construction can race cancellation before the service becomes owned.
                if (service != null && !replacementStarted)
                    await service.DisposeAsync().ConfigureAwait(false);
            }
            finally { _connectionLock.Release(); }
        }
    }

    /// <summary>
    /// Connects the given service and makes it the active one.
    /// A connected service stays active even if it rejects control or sends no telemetry, matching the Error status.
    /// </summary>
    private async Task ConnectServiceAsync(ITreadmillService service, string deviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();

        // Request control first
        try
        {
            await service.RequestControlAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && service.IsConnected)
        {
            _logger.LogError(ex, "Failed to request control from treadmill");
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!service.IsConnected) return;

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
        await ExecuteCommandAsync(service => service.StartAsync()).ConfigureAwait(false);

    /// <summary>
    /// Pauses the treadmill workout.
    /// </summary>
    public async Task PauseAsync() =>
        await ExecuteCommandAsync(service => service.PauseAsync()).ConfigureAwait(false);

    /// <summary>
    /// Stops the treadmill workout.
    /// </summary>
    public async Task StopAsync() =>
        await ExecuteCommandAsync(service => service.StopAsync()).ConfigureAwait(false);

    /// <summary>
    /// Sets treadmill speed.
    /// </summary>
    /// <param name="speed">Speed in km/h (telemetryProperty) or mph (imperial, depending on settings).</param>
    public async Task SetSpeedAsync(decimal speed) =>
        await ExecuteCommandAsync(service => service.SetSpeedAsync(speed)).ConfigureAwait(false);

    /// <summary>
    /// Sets treadmill incline.
    /// </summary>
    /// <param name="incline">Incline in percent grade.</param>
    public async Task SetInclineAsync(decimal incline) =>
        await ExecuteCommandAsync(service => service.SetInclineAsync(incline)).ConfigureAwait(false);

    /// <summary>
    /// Requests control of the treadmill (FTMS spec requires control before sending commands).
    /// </summary>
    public async Task RequestControlAsync() =>
        await ExecuteCommandAsync(service => service.RequestControlAsync()).ConfigureAwait(false);

    /// <summary>
    /// Sends a raw command to the treadmill.
    /// </summary>
    /// <param name="data"></param>
    public async Task SendCommandAsync(byte[] data) =>
        await ExecuteCommandAsync(service => service.SendCommandAsync(data)).ConfigureAwait(false);

    /// <summary>
    /// Disconnects from the current treadmill and clears the active service.
    /// Cancels a connection attempt in progress and waits for it to finish first.
    /// </summary>
    public async Task DisconnectAsync()
    {
        var failures = new List<Exception>();
        ITreadmillService? service;
        lock (_connectCancellationGate)
        {
            ++_disconnectVersion;
            try { _connectCancellation?.Cancel(); }
            catch (Exception ex) { failures.Add(ex); }
            service = _active;
        }
        try { service?.InvalidateTelemetry(); }
        catch (Exception ex) { failures.Add(ex); }

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseActiveAsync().ConfigureAwait(false);
            PublishDisconnected();
        }
        catch (Exception ex) { failures.Add(ex); }
        finally { _connectionLock.Release(); }

        Task[] losses;
        lock (_connectCancellationGate) losses = _lossTasks.ToArray();
        try { await Task.WhenAll(losses).ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        lock (_connectCancellationGate)
        {
            foreach (var loss in losses) _lossTasks.Remove(loss);
            failures.AddRange(_lossFailures);
            _lossFailures.Clear();
        }
        ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Treadmill release failed.", failures);
    }

    private void PublishDisconnected()
    {
        appState.SetConnectedDeviceName(null);
        appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
    }

    private void OnConnectionLost(ITreadmillService service)
    {
        lock (_connectCancellationGate)
        {
            if (_active != service) return;
            try { _connectCancellation?.Cancel(); }
            finally
            {
                // Dispatch separately so session cleanup cannot await its own notification callback.
                _lossTasks.Add(Task.Run(() => ReleaseLostServiceAsync(service)));
            }
        }
    }

    private async Task ReleaseLostServiceAsync(ITreadmillService service)
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active != service) return;
            await ReleaseActiveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Observe cleanup failures now and retain them for the owner's disconnect drain.
            lock (_connectCancellationGate) _lossFailures.Add(ex);
            _logger.LogError(ex, "Failed to release a lost treadmill.");
        }
        finally { _connectionLock.Release(); }
    }

    private async Task ExecuteCommandAsync(Func<ITreadmillService, Task> command)
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var service = _active;
            if (service == null) return;
            try
            {
                if (service.IsConnected) await command(service).ConfigureAwait(false);
            }
            catch (BluetoothConnectionLostException)
            {
                // A command racing session loss joins the same awaited release boundary.
                await ReleaseActiveAsync().ConfigureAwait(false);
                PublishDisconnected();
                return;
            }
            if (!service.IsConnected)
            {
                await ReleaseActiveAsync().ConfigureAwait(false);
                PublishDisconnected();
            }
        }
        finally { _connectionLock.Release(); }
    }

    /// <summary>Disconnects and disposes the owned service. Caller holds the connection lock.</summary>
    private async Task ReleaseActiveAsync()
    {
        ITreadmillService? service;
        lock (_connectCancellationGate)
        {
            service = _active;
            if (service == null) return;
            _active = null;
            service.ConnectionLost -= _activeLossHandler;
            _activeLossHandler = null;
        }
        var failures = new List<Exception>();
        try { service.InvalidateTelemetry(); }
        catch (Exception ex) { failures.Add(ex); }
        try { await service.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        try { await service.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count != 0)
        {
            try { appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error); }
            catch (Exception ex) { failures.Add(ex); }
            _logger.LogError(new AggregateException(failures), "Failed to disconnect the active treadmill.");
            ThrowFailures(failures);
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
    public async Task<string> GetDeviceInfoAsync()
    {
        string result = "No device connected";
        await ExecuteCommandAsync(async service => result = await service.GetDeviceInfoAsync().ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }
}
