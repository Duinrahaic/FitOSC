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
public class TreadmillManager(ILoggerFactory loggerFactory, AppStateService appState, WindowsBluetoothClient bluetoothClient)
{
    /// <summary>
    /// The currently active treadmill service implementation (FTMS or WalkingPad).
    /// </summary>
    private ITreadmillService? _active = null;
    
    /// <summary>
    /// Connects to a treadmill by device name and type.
    /// If a service is already active, it disconnects before switching.
    /// </summary>
    /// <param name="deviceName">The advertised BLE device name to connect to.</param>
    /// <param name="type">The treadmill type (default is FTMS).</param>
    /// <returns>The connected treadmill service, or null if connection fails.</returns>
    public async Task ConnectAsync(string deviceName, TreadmillType type = TreadmillType.FTMS)
    {
        const int ConnectionTimeoutSeconds = 30; // Total timeout for entire connection process
        var logger = loggerFactory.CreateLogger<TreadmillManager>();

        try
        {
            // If already connected to a treadmill, disconnect it first
            if (_active != null)
                await _active.DisconnectAsync();

            // Create the appropriate service based on treadmill type
            ITreadmillService? service = type switch
            {
                TreadmillType.FTMS => new FTMSTreadmillService(loggerFactory.CreateLogger<FTMSTreadmillService>(), appState),
                TreadmillType.WalkingPad => new WalkingPadTreadmillService(loggerFactory.CreateLogger<WalkingPadTreadmillService>(), appState),
                _ => null
            };

            if (service == null)
            {
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
                return;
            }

            // Show scanning state
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connecting);

            // Attempt to connect with timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            var connectTask = service.ConnectAsync(deviceName);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token));

            if (completedTask != connectTask)
            {
                // Timeout occurred
                logger.LogError("Connection timed out after {Timeout} seconds", ConnectionTimeoutSeconds);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
                return;
            }

            // Connection completed, check if successful
            if (service.IsConnected)
            {
                _active = service;

                // Request control first
                try
                {
                    await _active.RequestControlAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to request control from treadmill");
                    appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
                    return;
                }

                // Verify we have telemetry data (characteristics are working)
                var currentState = appState.GetCurrentAppStateInfo();
                if (!currentState.TreadmillTelemetry.Values.Any())
                {
                    logger.LogError("Connected but no telemetry characteristics available");
                    appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
                    return;
                }

                // Publish final state: Connected (single UI update)
                appState.SetConnectedDeviceName(deviceName);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
            }
            else
            {
                // Connection failed, publish final state: Disconnected (single UI update)
                appState.SetConnectedDeviceName(null);
                appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
            }
        }
        catch (Exception ex)
        {
            // On error, publish final state: Error (single UI update)
            logger.LogError(ex, "Error during treadmill connection");
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Error);
        }
    }

    /// <summary>
    /// Starts the treadmill (begin workout).
    /// </summary>
    public async Task StartAsync() =>
        await (_active?.StartAsync()  ?? Task.CompletedTask);

    /// <summary>
    /// Pauses the treadmill workout.
    /// </summary>
    public async Task PauseAsync() =>
        await (_active?.PauseAsync()  ?? Task.CompletedTask);

    /// <summary>
    /// Stops the treadmill workout.
    /// </summary>
    public async Task StopAsync() =>
        await (_active?.StopAsync()  ?? Task.CompletedTask);
    
    /// <summary>
    /// Sets treadmill speed.
    /// </summary>
    /// <param name="speed">Speed in km/h (telemetryProperty) or mph (imperial, depending on settings).</param>
    public async Task SetSpeedAsync(decimal speed) =>
        await (_active?.SetSpeedAsync(speed)  ?? Task.CompletedTask);

    /// <summary>
    /// Sets treadmill incline.
    /// </summary>
    /// <param name="incline">Incline in percent grade.</param>
    public async Task SetInclineAsync(decimal incline) =>
        await (_active?.SetInclineAsync(incline)  ?? Task.CompletedTask);

    /// <summary>
    /// Requests control of the treadmill (FTMS spec requires control before sending commands).
    /// </summary>
    public async Task RequestControlAsync() =>
        await (_active?.RequestControlAsync()  ?? Task.CompletedTask);
    
    /// <summary>
    /// Sends a raw command to the treadmill.
    /// </summary>
    /// <param name="data"></param>
    public async Task SendCommandAsync(byte[] data) =>
        await (_active?.SendCommandAsync(data)  ?? Task.CompletedTask);

    /// <summary>
    /// Disconnects from the current treadmill and clears the active service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_active != null)
        {
            await _active.DisconnectAsync();
            _active = null;
            appState.SetConnectedDeviceName(null);
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
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
