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

    // Hold-repeat state — tracks cancellation tokens for buttons held down
    private readonly Dictionary<string, CancellationTokenSource> _heldButtons = new();
    private const int HoldInitialDelayMs = 500;
    private const int HoldRepeatIntervalMs = 200;
    private CancellationToken _serviceCancellation = CancellationToken.None;

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

        _parameterHandlers = new Dictionary<string, Action<object>>
        {
            // Treadmill
            { "/avatar/parameters/TMC/Treadmill/Enable",    HandleTreadmillEnable },
            { "/avatar/parameters/TMC/Treadmill/Speed",     HandleTreadmillSpeed },
            { "/avatar/parameters/TMC/Treadmill/SpeedUp",   HandleSpeedUp },
            { "/avatar/parameters/TMC/Treadmill/SlowDown",  HandleSlowDown },
            { "/avatar/parameters/TMC/Treadmill/InclineUp", HandleInclineUp },
            { "/avatar/parameters/TMC/Treadmill/InclineDown", HandleInclineDown },

            // Walking
            // Walking - General
            { "/avatar/parameters/TMC/Walking/Enable",                  HandleWalkingEnable },
            { "/avatar/parameters/TMC/Walking/YawReset",                HandleYawReset },
            { "/avatar/parameters/TMC/Walking/TempSpeedUp",             HandleWalkTempSpeedUp },
            { "/avatar/parameters/TMC/Walking/TempSpeedDown",           HandleWalkTempSpeedDown },

            // Walking - Dynamic
            { "/avatar/parameters/TMC/Walking/Dynamic",                 HandleWalkingDynamic },
            { "/avatar/parameters/TMC/Walking/Dynamic/TrimUp",          HandleTrimUp },
            { "/avatar/parameters/TMC/Walking/Dynamic/TrimDown",        HandleTrimDown },

            // Walking - Override
            { "/avatar/parameters/TMC/Walking/Override",                HandleWalkingOverride },

            // Walking Override step-through
            { "/avatar/parameters/TMC/Walking/Override/SpeedUp",   HandleOverrideSpeedUp },
            { "/avatar/parameters/TMC/Walking/Override/SpeedDown", HandleOverrideSpeedDown },

            // Walking Override presets (0 = stopped at 0% speed, still in Override mode)
            { "/avatar/parameters/TMC/Walking/Override/Preset0", _ => HandleOverridePreset(_, 0) },
            { "/avatar/parameters/TMC/Walking/Override/Preset1", _ => HandleOverridePreset(_, 1) },
            { "/avatar/parameters/TMC/Walking/Override/Preset2", _ => HandleOverridePreset(_, 2) },
            { "/avatar/parameters/TMC/Walking/Override/Preset3", _ => HandleOverridePreset(_, 3) },
            { "/avatar/parameters/TMC/Walking/Override/Preset4", _ => HandleOverridePreset(_, 4) },
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting VRChat Parameter Handler Service");
        _serviceCancellation = cancellationToken;
        _oscService.OnOscMessageReceived += OnOscMessageReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping VRChat Parameter Handler Service");
        _oscService.OnOscMessageReceived -= OnOscMessageReceived;
        foreach (var cts in _heldButtons.Values) { cts.Cancel(); cts.Dispose(); }
        _heldButtons.Clear();
        return Task.CompletedTask;
    }

    private void OnOscMessageReceived(object? sender, OscSubscriptionEvent e)
    {
        var message = e.Message;

        if (!message.Address.StartsWith("/avatar/parameters/TMC/"))
            return;

        if (_parameterHandlers.TryGetValue(message.Address, out var handler))
        {
            try
            {
                var value = message.Arguments.FirstOrDefault();
                if (value != null)
                    handler(value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling OSC parameter: {Address}", message.Address);
            }
        }
    }

    #region Treadmill Handlers

    private bool ShouldExecuteCommand(string commandName)
    {
        if (_lastCommandTime.TryGetValue(commandName, out var lastTime))
        {
            if ((DateTime.UtcNow - lastTime).TotalMilliseconds < CommandDebounceMs)
                return false;
        }

        _lastCommandTime[commandName] = DateTime.UtcNow;
        return true;
    }

    private void HandleTreadmillEnable(object value)
    {
        if (value is bool boolValue && ShouldExecuteCommand("TreadmillEnable"))
        {
            if (boolValue)
            {
                _logger.LogInformation("OSC: Starting treadmill");
                _ = _treadmillManager.StartAsync();
            }
            else
            {
                _logger.LogInformation("OSC: Stopping treadmill");
                _ = _treadmillManager.StopAsync();
            }
        }
    }

    private void HandleTreadmillSpeed(object value)
    {
        if (value is float floatValue)
        {
            float normalized = Math.Clamp(floatValue, 0f, 1f);
            var maxSpeed = _appStateService.WalkingModeConfig.MaxSpeed;
            var targetSpeed = (decimal)normalized * maxSpeed;
            _logger.LogInformation("OSC: Setting treadmill speed to {Speed:F2} km/h ({Normalized:P0})", targetSpeed, normalized);
            _ = _treadmillManager.SetSpeedAsync(targetSpeed);
        }
    }

    private void StartHoldRepeat(string key, Action action, CancellationToken serviceStopping)
    {
        StopHoldRepeat(key);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
        _heldButtons[key] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                action();
                await Task.Delay(HoldInitialDelayMs, cts.Token);
                while (!cts.Token.IsCancellationRequested)
                {
                    action();
                    await Task.Delay(HoldRepeatIntervalMs, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private void StopHoldRepeat(string key)
    {
        if (_heldButtons.TryGetValue(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _heldButtons.Remove(key);
        }
    }

    private void HandleSpeedUp(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("SpeedUp", () =>
            {
                var current = _appStateService.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;
                _ = _treadmillManager.SetSpeedAsync(current + SpeedIncrementKmh);
            }, _serviceCancellation);
            else StopHoldRepeat("SpeedUp");
        }
    }

    private void HandleSlowDown(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("SlowDown", () =>
            {
                var current = _appStateService.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;
                _ = _treadmillManager.SetSpeedAsync(Math.Max(0m, current - SpeedIncrementKmh));
            }, _serviceCancellation);
            else StopHoldRepeat("SlowDown");
        }
    }

    private void HandleInclineUp(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("InclineUp", () =>
            {
                var config = _appStateService.LatestConfiguration;
                if (config == null) return;
                var current = _appStateService.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.Incline)?.Value ?? 0m;
                _ = _treadmillManager.SetInclineAsync(Math.Clamp(current + config.InclineIncrement, config.MinIncline, config.MaxIncline));
            }, _serviceCancellation);
            else StopHoldRepeat("InclineUp");
        }
    }

    private void HandleInclineDown(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("InclineDown", () =>
            {
                var config = _appStateService.LatestConfiguration;
                if (config == null) return;
                var current = _appStateService.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.Incline)?.Value ?? 0m;
                _ = _treadmillManager.SetInclineAsync(Math.Clamp(current - config.InclineIncrement, config.MinIncline, config.MaxIncline));
            }, _serviceCancellation);
            else StopHoldRepeat("InclineDown");
        }
    }

    #endregion

    #region Walking Handlers

    private void HandleWalkingEnable(object value)
    {
        if (value is bool boolValue && ShouldExecuteCommand("WalkingEnable"))
        {
            if (boolValue)
            {
                _logger.LogInformation("OSC: Enabling auto-walk");
                _appStateService.EnableAutoWalk();
            }
            else
            {
                _logger.LogInformation("OSC: Disabling auto-walk");
                _appStateService.DisableAutoWalk();
            }
        }
    }

    private void HandleWalkingDynamic(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("WalkingDynamic"))
        {
            _logger.LogInformation("OSC: Setting preferred mode to Dynamic");
            _appStateService.SetPreferredWalkingMode(WalkingMode.Dynamic);
        }
    }

    private void HandleWalkingOverride(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("WalkingOverride"))
        {
            _logger.LogInformation("OSC: Setting preferred mode to Override");
            _appStateService.SetPreferredWalkingMode(WalkingMode.Override);
        }
    }

    private void HandleTrimUp(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("TrimUp", () => _appStateService.AdjustWalkingTrim(0.05f), _serviceCancellation);
            else StopHoldRepeat("TrimUp");
        }
    }

    private void HandleTrimDown(object value)
    {
        if (value is bool boolValue)
        {
            if (boolValue) StartHoldRepeat("TrimDown", () => _appStateService.AdjustWalkingTrim(-0.05f), _serviceCancellation);
            else StopHoldRepeat("TrimDown");
        }
    }

    private void HandleYawReset(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("YawReset"))
        {
            _logger.LogInformation("OSC: Requesting yaw reset");
            _appStateService.RequestYawReset();
        }
    }

    private void HandleWalkTempSpeedUp(object value)
    {
        if (value is bool boolValue)
        {
            _appStateService.SetTemporaryWalkingBoost(boolValue ? 2.0f : 1.0f);
            _logger.LogInformation("OSC: Temp speed up {State}", boolValue ? "active" : "released");
        }
    }

    private void HandleWalkTempSpeedDown(object value)
    {
        if (value is bool boolValue)
        {
            _appStateService.SetTemporaryWalkingBoost(boolValue ? 0.0f : 1.0f);
            _logger.LogInformation("OSC: Temp speed down {State}", boolValue ? "active" : "released");
        }
    }

    private void HandleOverrideSpeedUp(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("OverrideSpeedUp"))
        {
            var newIndex = _appStateService.WalkingModeConfig.CurrentOverrideIndex + 1;
            _appStateService.SetOverrideSpeedIndex(newIndex);
            _appStateService.SetPreferredWalkingMode(WalkingMode.Override);
            _logger.LogInformation("OSC: Override speed up (index {Index})", newIndex);
        }
    }

    private void HandleOverrideSpeedDown(object value)
    {
        if (value is bool boolValue && boolValue && ShouldExecuteCommand("OverrideSpeedDown"))
        {
            var newIndex = _appStateService.WalkingModeConfig.CurrentOverrideIndex - 1;
            _appStateService.SetOverrideSpeedIndex(newIndex);
            _appStateService.SetPreferredWalkingMode(WalkingMode.Override);
            _logger.LogInformation("OSC: Override speed down (index {Index})", newIndex);
        }
    }

    private void HandleOverridePreset(object value, int preset)
    {
        if (value is bool boolValue && ShouldExecuteCommand($"OverridePreset{preset}"))
        {
            if (boolValue)
            {
                _appStateService.SetOverrideSpeedIndex(preset);
                _appStateService.SetPreferredWalkingMode(WalkingMode.Override);
                _logger.LogInformation("OSC: Override preset set to Preset{Preset}", preset);
            }
            else if (_appStateService.CurrentWalkingMode == WalkingMode.Override &&
                     _appStateService.WalkingModeConfig.CurrentOverrideIndex == preset)
            {
                _appStateService.DisableAutoWalk();
                _logger.LogInformation("OSC: Walking disabled by toggling off Preset{Preset}", preset);
            }
        }
    }

    #endregion

    public void Dispose()
    {
        _oscService.OnOscMessageReceived -= OnOscMessageReceived;
    }
}
