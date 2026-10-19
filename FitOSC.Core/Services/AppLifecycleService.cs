using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services;

public sealed class AppLifecycleService(ConfigurationService configuration, AppStateService appState,
    TreadmillManager treadmill, ILogger<AppLifecycleService> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _autoConnect;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = configuration.GetConfiguration();
        appState.UpdatePreferredUnits(config.User.PreferMetric);
        var walkingModeConfig = new WalkingModeConfiguration
        {
            MaxSpeed = config.WalkingMode.MaxSpeed,
            WalkingTrim = config.WalkingMode.DefaultTrim,
            SmoothingFactor = config.WalkingMode.SmoothingFactor,
            MaxTurnAngle = config.WalkingMode.MaxTurnAngle,
            UpdateIntervalMs = config.WalkingMode.UpdateIntervalMs,
            ThumbstickRampSpeed = config.WalkingMode.ThumbstickRampSpeed
        };
        appState.UpdateWalkingModeConfig(walkingModeConfig);

        if (config.Treadmill.AutoConnect && !string.IsNullOrEmpty(config.Treadmill.LastDeviceName))
        {
            var stoppingToken = _stopping.Token;
            _autoConnect = Task.Run(async () =>
            {
                try
                {
                    await treadmill.ConnectAsync(config.Treadmill.LastDeviceName, config.Treadmill.TreadmillType, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to auto-connect to treadmill");
                }
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels auto-connect and waits for it, then releases the active treadmill.
    /// Both are awaited to completion rather than abandoned at the host's shutdown timeout,
    /// so no Bluetooth work outlives the host.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_autoConnect != null)
            await _autoConnect.ConfigureAwait(false);

        await treadmill.DisconnectAsync().ConfigureAwait(false);
    }
}
