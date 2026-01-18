using FitOSC.Models;
using FitOSC.Services.OSC;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.VRChat;

/// <summary>
/// Handles incoming OSC messages from VRChat avatar parameters and routes them to appropriate services.
/// Manages walking mode control and treadmill commands via OSC.
/// </summary>
public class VRChatParameterHandlerService : IHostedService, IDisposable
{
    private readonly ILogger<VRChatParameterHandlerService> _logger;
    private readonly IOscService _oscService;
    private readonly AppStateService _appStateService;
    private readonly TreadmillManager _treadmillManager;

    // Debouncing for button presses to prevent VRChat parameter toggle duplicates
    private readonly Dictionary<string, DateTime> _lastCommandTime = new();
    private const int CommandDebounceMs = 300;

    // Speed adjustment increment (in km/h) - matches UI increment of 0.2 mph
    private const decimal SpeedIncrementKmh = 0.2m * 1.609344m; // 0.2 mph = 0.321869 km/h

    // Dictionary for routing OSC parameters to handlers
    private readonly Dictionary<string, Action<object>> _parameterHandlers;

    public VRChatParameterHandlerService(
        ILogger<VRChatParameterHandlerService> logger,
        IOscService oscService,
        AppStateService appStateService,
        TreadmillManager treadmillManager)
    {
        _logger = logger;
        _oscService = oscService;
        _appStateService = appStateService;
        _treadmillManager = treadmillManager;

        // Initialize parameter handlers
        _parameterHandlers = new Dictionary<string, Action<object>>
        {
            // Walking Mode Commands
            { "/avatar/parameters/TMC_Walk_Disable", HandleWalkModeDisable },
            { "/avatar/parameters/TMC_Walk_Dynamic", HandleWalkModeDynamic },
            { "/avatar/parameters/TMC_Walk_Preset", HandleWalkModePreset },
            { "/avatar/parameters/TMC_WalkingTrim", HandleWalkingTrim },

            // Treadmill Control Commands
            { "/avatar/parameters/TMC_Start", HandleTreadmillStart },
            { "/avatar/parameters/TMC_Pause", HandleTreadmillPause },
            { "/avatar/parameters/TMC_Stop", HandleTreadmillStop },
            { "/avatar/parameters/TMC_SpeedUp", HandleSpeedUp },
            { "/avatar/parameters/TMC_SlowDown", HandleSlowDown },
            { "/avatar/parameters/TMC_InclineUp", HandleInclineUp },
            { "/avatar/parameters/TMC_InclineDown", HandleInclineDown },
            { "/avatar/parameters/TMC_Reset", HandleReset }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting VRChat Parameter Handler Service");
        _oscService.OnOscMessageReceived += OnOscMessageReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping VRChat Parameter Handler Service");
        _oscService.OnOscMessageReceived -= OnOscMessageReceived;
        return Task.CompletedTask;
    }

    private void OnOscMessageReceived(object? sender, OscSubscriptionEvent e)
    {
        var message = e.Message;

        // Only process TMC parameters
        if (!message.Address.StartsWith("/avatar/parameters/TMC_"))
            return;

        // Route to appropriate handler
        if (_parameterHandlers.TryGetValue(message.Address, out var handler))
        {
            try
            {
                // Extract value (VRChat sends bool or float)
                var value = message.Arguments.FirstOrDefault();
                if (value != null)
                {
                    handler(value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling OSC parameter: {Address}", message.Address);
            }
        }
    }

    #region Walking Mode Handlers

    private void HandleWalkModeDisable(object value)
    {
        if (value is bool boolValue && boolValue)
        {
            _logger.LogInformation("OSC: Setting walking mode to Disabled");
            _appStateService.SetWalkingMode(WalkingMode.Disabled);
        }
    }

    private void HandleWalkModeDynamic(object value)
    {
        if (value is bool boolValue && boolValue)
        {
            _logger.LogInformation("OSC: Setting walking mode to Dynamic");
            _appStateService.SetWalkingMode(WalkingMode.Dynamic);
        }
    }

    private void HandleWalkModePreset(object value)
    {
        if (value is bool boolValue && boolValue)
        {
            _logger.LogInformation("OSC: Setting walking mode to Override (Override)");
            _appStateService.SetWalkingMode(WalkingMode.Override);
        }
    }

    private void HandleWalkingTrim(object value)
    {
        // VRChat sends float 0.0 to 1.0
        if (value is float floatValue)
        {
            // Clamp to valid range
            float trimValue = Math.Clamp(floatValue, 0f, 1f);

            _logger.LogInformation("OSC: Setting walking trim to {Trim:F2}", trimValue);

            // Update config
            var currentConfig = _appStateService.WalkingModeConfig;
            currentConfig.WalkingTrim = trimValue;
            _appStateService.UpdateWalkingModeConfig(currentConfig);
        }
    }

    #endregion

    #region Treadmill Control Handlers

    private bool ShouldExecuteCommand(string commandName)
    {
        if (_lastCommandTime.TryGetValue(commandName, out var lastTime))
        {
            var elapsed = (DateTime.UtcNow - lastTime).TotalMilliseconds;
            if (elapsed < CommandDebounceMs)
            {
                return false; // Too soon, ignore
            }
        }

        _lastCommandTime[commandName] = DateTime.UtcNow;
        return true;
    }

    private void HandleTreadmillStart(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("Start"))
        {
            _logger.LogInformation("OSC: Starting treadmill");
            _ = _treadmillManager.StartAsync();
        }
    }

    private void HandleTreadmillPause(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("Pause"))
        {
            _logger.LogInformation("OSC: Pausing treadmill");
            _ = _treadmillManager.PauseAsync();
        }
    }

    private void HandleTreadmillStop(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("Stop"))
        {
            _logger.LogInformation("OSC: Stopping treadmill");
            _ = _treadmillManager.StopAsync();
        }
    }

    private void HandleSpeedUp(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("SpeedUp"))
        {
            var currentSpeed = _appStateService.LatestData?.GetTelemetryValue(
                TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;

            var newSpeed = currentSpeed + SpeedIncrementKmh;

            _logger.LogInformation("OSC: Increasing speed from {Current:F2} to {New:F2} km/h",
                currentSpeed, newSpeed);

            _ = _treadmillManager.SetSpeedAsync(newSpeed);
        }
    }

    private void HandleSlowDown(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("SlowDown"))
        {
            var currentSpeed = _appStateService.LatestData?.GetTelemetryValue(
                TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;

            var newSpeed = Math.Max(0m, currentSpeed - SpeedIncrementKmh);

            _logger.LogInformation("OSC: Decreasing speed from {Current:F2} to {New:F2} km/h",
                currentSpeed, newSpeed);

            _ = _treadmillManager.SetSpeedAsync(newSpeed);
        }
    }

    private void HandleInclineUp(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("InclineUp"))
        {
            var currentIncline = _appStateService.LatestData?.GetTelemetryValue(
                TreadmillTelemetryProperty.Incline)?.Value ?? 0m;

            var config = _appStateService.LatestConfiguration;
            if (config == null)
            {
                _logger.LogWarning("OSC: Cannot adjust incline - no treadmill configuration available");
                return;
            }

            var newIncline = currentIncline + config.InclineIncrement;
            newIncline = Math.Clamp(newIncline, config.MinIncline, config.MaxIncline);

            _logger.LogInformation("OSC: Increasing incline from {Current:F1} to {New:F1} degrees",
                currentIncline, newIncline);

            _ = _treadmillManager.SetInclineAsync(newIncline);
        }
    }

    private void HandleInclineDown(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("InclineDown"))
        {
            var currentIncline = _appStateService.LatestData?.GetTelemetryValue(
                TreadmillTelemetryProperty.Incline)?.Value ?? 0m;

            var config = _appStateService.LatestConfiguration;
            if (config == null)
            {
                _logger.LogWarning("OSC: Cannot adjust incline - no treadmill configuration available");
                return;
            }

            var newIncline = currentIncline - config.InclineIncrement;
            newIncline = Math.Clamp(newIncline, config.MinIncline, config.MaxIncline);

            _logger.LogInformation("OSC: Decreasing incline from {Current:F1} to {New:F1} degrees",
                currentIncline, newIncline);

            _ = _treadmillManager.SetInclineAsync(newIncline);
        }
    }

    private void HandleReset(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("Reset"))
        {
            _logger.LogInformation("OSC: Resetting treadmill (stop + disable walking)");

            _ = _treadmillManager.StopAsync();
            _appStateService.SetWalkingMode(WalkingMode.Disabled);
        }
    }

    #endregion

    public void Dispose()
    {
        _oscService.OnOscMessageReceived -= OnOscMessageReceived;
    }
}
