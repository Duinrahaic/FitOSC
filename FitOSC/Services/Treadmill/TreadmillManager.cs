using FitOSC.Models;
using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Manages treadmill services (FTMS, WalkingPad, etc.)
/// and provides a unified interface for connecting and controlling treadmills.
/// </summary>
/// <remarks>
/// Uses dependency injection for logging and app state.
/// Keeps track of the currently active treadmill service.
/// </remarks>
public class TreadmillManager(ILoggerFactory loggerFactory, AppStateService appState)
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

        if (service == null) return;

        // Attempt to connect to the treadmill
        await service.ConnectAsync(deviceName);

        // If successful, set as the active treadmill
        if (service.IsConnected)
        {
            _active = service;
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
            await _active.RequestControlAsync();
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
            appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
        }
    }
}
