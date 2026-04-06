using FitOSC.Models;
using FitOSC.Services.OpenVR;
using FitOSC.Services.OSC;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using FitOSC.Utilities;
using Valve.VR;

namespace FitOSC.Services.VRChat;

/// <summary>
/// Service that combines treadmill speed and OpenVR head tracking to control VRChat avatar locomotion.
/// Supports three walking modes: Disabled, Dynamic, and Override.
/// </summary>
public class VRChatLocomotionService : IHostedService, IDisposable
{
    private readonly ILogger<VRChatLocomotionService> _logger;
    private readonly AppStateService _appState;
    private readonly IOscService _oscService;
    private readonly OpenVRService _openVRService;
    private readonly TreadmillManager _treadmillManager;

    // Latest data from treadmill and OpenVR
    private TreadmillTelemetry? _latestTelemetry;
    private OpenVRDataEvent? _latestOpenVRData;
    private WalkingMode _currentMode = WalkingMode.Disabled;
    private WalkingModeConfiguration _modeConfig = new();

    // Initial direction when walking is enabled
    private float? _initialYaw = null;

    // Smoothed values for reducing jitter
    private float _smoothedVelocity = 0f;
    private float _smoothedStrafe = 0f;
    private float _smoothedVertical = 0f;

    // Speed modifier ramp — lerps toward the current target each tick
    private float _currentSpeedModifier = 1f;

    // Stick boost target — set from SteamVR SpeedModifier action (optional, 0–2×)
    private float _stickBoostTarget = 1f;

    // Manual override state (left thumbstick active)
    private bool _isManualOverride = false;


    // Periodic update timer
    private Timer? _updateTimer;


    public VRChatLocomotionService(
        ILogger<VRChatLocomotionService> logger,
        AppStateService appState,
        IOscService oscService,
        OpenVRService openVRService,
        TreadmillManager treadmillManager)
    {
        _logger = logger;
        _appState = appState;
        _oscService = oscService;
        _openVRService = openVRService;
        _treadmillManager = treadmillManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting VRChat Locomotion Service");

        // Subscribe to app state updates (treadmill telemetry, walking mode changes)
        _appState.AppStateUpdated += OnAppStateUpdated;

        // Subscribe to OpenVR updates (head tracking)
        _openVRService.OnDataUpdateReceived += OnOpenVRDataReceived;

        // Subscribe to SteamVR action events
        _openVRService.OnActionReceived += OnActionReceived;

        // Subscribe to yaw reset requests (from OSC menu or SteamVR)
        _appState.YawResetRequested += OnYawResetRequested;

        // Start periodic update timer with default interval
        _updateTimer = new Timer(PeriodicUpdate, null, 0, _modeConfig.UpdateIntervalMs);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping VRChat Locomotion Service");

        // Stop timer
        _updateTimer?.Change(Timeout.Infinite, 0);
        _updateTimer?.Dispose();
        _updateTimer = null;

        // Unsubscribe from events
        _appState.AppStateUpdated -= OnAppStateUpdated;
        _openVRService.OnDataUpdateReceived -= OnOpenVRDataReceived;
        _openVRService.OnActionReceived -= OnActionReceived;
        _appState.YawResetRequested -= OnYawResetRequested;

        return Task.CompletedTask;
    }

    private void OnAppStateUpdated(AppStateInfo state)
    {
        _latestTelemetry = state.TreadmillTelemetry;

        // Detect walking mode changes
        var previousMode = _currentMode;
        var previousUpdateInterval = _modeConfig.UpdateIntervalMs;

        _currentMode = state.Walking.Mode;
        _modeConfig = state.Walking.Config;

        // Restart timer if update interval changed
        if (previousUpdateInterval != _modeConfig.UpdateIntervalMs && _updateTimer != null)
        {
            _updateTimer.Change(0, _modeConfig.UpdateIntervalMs);
            _logger.LogInformation($"Update interval changed to {_modeConfig.UpdateIntervalMs}ms ({1000.0 / _modeConfig.UpdateIntervalMs:F1}Hz)");
        }

        // Capture initial direction when walking is enabled
        if (previousMode == WalkingMode.Disabled && _currentMode != WalkingMode.Disabled)
        {
            // Walking just enabled - lock in current direction
            _initialYaw = _latestOpenVRData?.Yaw;
            _logger.LogInformation($"Walking enabled - initial yaw locked at {_initialYaw?.ToString("F3") ?? "null"}");
        }
        else if (_currentMode == WalkingMode.Disabled)
        {
            // Walking disabled - clear initial direction and reset smoothed values
            _initialYaw = null;
            _smoothedVelocity = 0f;
            _smoothedStrafe = 0f;
            _smoothedVertical = 0f;
            _currentSpeedModifier = 1f;
            _stickBoostTarget = 1f;

        }
    }

    private void OnOpenVRDataReceived(OpenVRDataEvent data)
    {
        _latestOpenVRData = data;
    }

    private void ActivatePreset(int preset)
    {
        _appState.SetOverrideSpeedIndex(preset);
        _appState.SetPreferredWalkingMode(WalkingMode.Override);
        _logger.LogInformation($"Preset {preset} activated via SteamVR action");
    }

    private void OnYawResetRequested()
    {
        if (_latestOpenVRData != null)
        {
            _initialYaw = _latestOpenVRData.Yaw;
            _logger.LogInformation($"Yaw recentered (new yaw: {_initialYaw:F3})");
        }
    }

    private void OnActionReceived(OpenVRActionEvent action)
    {
        try
        {
            // Handle toggle walking action
            if (action.ToggleWalkingPressed)
            {
                if (_currentMode == WalkingMode.Disabled)
                {
                    _appState.EnableAutoWalk();
                    _logger.LogInformation("Walking enabled via SteamVR action");
                }
                else
                {
                    _appState.DisableAutoWalk();
                    _logger.LogInformation("Walking disabled via SteamVR action");
                }
            }

            // Handle recenter yaw action
            if (action.RecenterYawPressed)
                OnYawResetRequested();

            // Handle override speed up action
            if (action.OverrideSpeedUpPressed)
            {
                var newIndex = _modeConfig.CurrentOverrideIndex + 1;
                if (newIndex < _modeConfig.OverrideSpeeds.Count)
                {
                    _appState.SetOverrideSpeedIndex(newIndex);
                    _appState.SetPreferredWalkingMode(WalkingMode.Override);
                    _logger.LogInformation($"Override speed up via SteamVR action (index: {newIndex})");
                }
            }

            // Handle override speed down action
            if (action.OverrideSpeedDownPressed)
            {
                var newIndex = _modeConfig.CurrentOverrideIndex - 1;
                if (newIndex >= 0)
                {
                    _appState.SetOverrideSpeedIndex(newIndex);
                    _appState.SetPreferredWalkingMode(WalkingMode.Override);
                    _logger.LogInformation($"Override speed down via SteamVR action (index: {newIndex})");
                }
            }

            // Handle treadmill controls
            if (action.TreadmillEnablePressed)
            {
                if (_appState.LatestState == TreadmillState.Running)
                {
                    _ = _treadmillManager.StopAsync();
                    _logger.LogInformation("Treadmill stopped via SteamVR action");
                }
                else
                {
                    _ = _treadmillManager.StartAsync();
                    _logger.LogInformation("Treadmill started via SteamVR action");
                }
            }

            if (action.TreadmillSpeedUpPressed)
            {
                var currentSpeed = _appState.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;
                _ = _treadmillManager.SetSpeedAsync(currentSpeed + 0.2m * 1.609344m);
                _logger.LogInformation("Treadmill speed up via SteamVR action");
            }

            if (action.TreadmillSlowDownPressed)
            {
                var currentSpeed = _appState.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed)?.Value ?? 0m;
                _ = _treadmillManager.SetSpeedAsync(Math.Max(0m, currentSpeed - 0.2m * 1.609344m));
                _logger.LogInformation("Treadmill slow down via SteamVR action");
            }

            if (action.TreadmillInclineUpPressed)
            {
                var currentIncline = _appState.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.Incline)?.Value ?? 0m;
                var config = _appState.LatestConfiguration;
                if (config != null)
                {
                    _ = _treadmillManager.SetInclineAsync(Math.Clamp(currentIncline + config.InclineIncrement, config.MinIncline, config.MaxIncline));
                    _logger.LogInformation("Treadmill incline up via SteamVR action");
                }
            }

            if (action.TreadmillInclineDownPressed)
            {
                var currentIncline = _appState.LatestData?.GetTelemetryValue(TreadmillTelemetryProperty.Incline)?.Value ?? 0m;
                var config = _appState.LatestConfiguration;
                if (config != null)
                {
                    _ = _treadmillManager.SetInclineAsync(Math.Clamp(currentIncline - config.InclineIncrement, config.MinIncline, config.MaxIncline));
                    _logger.LogInformation("Treadmill incline down via SteamVR action");
                }
            }

            // Handle mode selection
            if (action.EnableDynamicPressed)
            {
                _appState.SetPreferredWalkingMode(WalkingMode.Dynamic);
                _logger.LogInformation("Enable Dynamic mode via SteamVR action");
            }

            if (action.WalkingSpeedUpPressed)
            {
                _appState.AdjustWalkingTrim(0.05f);
                _logger.LogInformation("Walking speed up via SteamVR action");
            }

            if (action.WalkingSpeedDownPressed)
            {
                _appState.AdjustWalkingTrim(-0.05f);
                _logger.LogInformation("Walking speed down via SteamVR action");
            }

            if (action.EnableOverridePressed)
            {
                _appState.SetPreferredWalkingMode(WalkingMode.Override);
                _logger.LogInformation("Enable Override mode via SteamVR action");
            }

            // Handle direct preset selection
            if (action.Preset0Pressed) ActivatePreset(0);
            if (action.Preset1Pressed) ActivatePreset(1);
            if (action.Preset2Pressed) ActivatePreset(2);
            if (action.Preset3Pressed) ActivatePreset(3);
            if (action.Preset4Pressed) ActivatePreset(4);

            // Handle temp speed hold actions (SteamVR buttons) — ramp toward 2x or 0x while held
            if (action.TempSpeedUpHeld)
                _appState.SetTemporaryWalkingBoost(2.0f);
            else if (action.TempSpeedDownHeld)
                _appState.SetTemporaryWalkingBoost(0.0f);
            else
                _appState.SetTemporaryWalkingBoost(1.0f);

            // Update stick boost target from SteamVR SpeedModifier action (optional analog input)
            _stickBoostTarget = MathF.Abs(action.SpeedModifier) > 0.1f
                ? Math.Clamp(1f + action.SpeedModifier, 0f, 2f)
                : 1f;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SteamVR action");
        }
    }

    private void PeriodicUpdate(object? state)
    {
        try
        {
            // Check for manual override (left thumbstick active)
            _isManualOverride = _latestOpenVRData != null &&
                (MathF.Abs(_latestOpenVRData.LeftThumbstickX) > 0.1f || MathF.Abs(_latestOpenVRData.LeftThumbstickY) > 0.1f);

            // Calculate target velocity based on walking mode
            float targetSpeed = CalculateVelocity();

            // Apply smoothing to gradually ramp up/down to target speed
            _smoothedVelocity = Lerp(_smoothedVelocity, targetSpeed, _modeConfig.SmoothingFactor);

            // Send OSC messages to VRChat (calculates horizontal/vertical components)
            SendVRChatInputs(_smoothedVelocity);

            // Publish walking data to app state for visualization
            // When manual override is active, show 0 velocity to indicate paused state
            float displayVelocity = _isManualOverride ? 0f : _smoothedVelocity;
            float displayStrafe = _isManualOverride ? 0f : _smoothedStrafe;
            float displayVertical = _isManualOverride ? 0f : _smoothedVertical;
            _appState.PublishWalkingData(displayVelocity, displayStrafe, displayVertical, _isManualOverride);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in periodic VRChat locomotion update");
        }
    }

    private float CalculateVelocity()
    {
        // Get base speed from treadmill - this is the magnitude of the movement vector
        float baseSpeed = _currentMode switch
        {
            WalkingMode.Disabled => 0f,
            WalkingMode.Dynamic => CalculateDynamicVelocity(),
            WalkingMode.Override => CalculateOverrideVelocity(),
            _ => 0f
        };

        // Determine target modifier from available inputs (all optional, any combination):
        //   Stick (SteamVR only):   analog 0–2×, active when pushed beyond deadzone
        //   Buttons (OSC or SteamVR): discrete 0, 1, or 2×
        // Stick takes priority when active; otherwise buttons/OSC source is used.
        // Stick takes priority when active; otherwise buttons/OSC source is used.
        float targetModifier = MathF.Abs(_stickBoostTarget - 1f) > 0.1f
            ? _stickBoostTarget
            : _appState.TemporaryWalkingBoost;

        // Gradually ramp the current modifier toward the target
        _currentSpeedModifier = Lerp(_currentSpeedModifier, targetModifier, _modeConfig.ThumbstickRampSpeed);

        // Apply the unified smoothed speed modifier
        baseSpeed *= _currentSpeedModifier;

        return Math.Clamp(baseSpeed, 0f, 1f);
    }

    private float CalculateDynamicVelocity()
    {
        if (_latestTelemetry == null)
            return 0f;

        // Get instantaneous speed from treadmill (in km/h)
        var speedValue = _latestTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed);
        if (speedValue == null)
            return 0f;

        decimal currentSpeed = speedValue.IsMetric ? speedValue.Value : speedValue.Value.ConvertMphToKph();

        // Normalize speed to 0.0-1.0 range based on max speed
        float normalizedSpeed = (float)(Math.Clamp(currentSpeed,0,_modeConfig.MaxSpeed) / _modeConfig.MaxSpeed);

        // Clamp to 0.0-1.0 range
        normalizedSpeed = Math.Clamp(normalizedSpeed, 0f, 1f);

        // Apply walking trim (multiplier: 0.0 to 2.0)
        float adjustedSpeed = normalizedSpeed * _modeConfig.WalkingTrim;

        // Clamp final output to 0.0-1.0 range for VRChat
        return Math.Clamp(adjustedSpeed, 0f, 1f);
    }

    private float CalculateOverrideVelocity()
    {
        // Return the speed at the current override index
        if (_modeConfig.CurrentOverrideIndex >= 0 &&
            _modeConfig.CurrentOverrideIndex < _modeConfig.OverrideSpeeds.Count)
        {
            return _modeConfig.OverrideSpeeds[_modeConfig.CurrentOverrideIndex];
        }

        return 0f;
    }

    private float CalculateLookHorizontal()
    {
        if (_latestOpenVRData == null)
            return 0f;

        // Only use snap turns from head tilt for comfort turning
        if (_latestOpenVRData.Turn == OpenVRTurn.Left)
            return -1f;
        if (_latestOpenVRData.Turn == OpenVRTurn.Right)
            return 1f;

        // No smooth turning - strafing handles direction
        return 0f;
    }


    private void SendVRChatInputs(float speed)
    {
        float vertical = 0f;
        float horizontal = 0f;

        // Vector decomposition: maintain constant speed in initial direction
        // Skip if manual override is active (left thumbstick) to let user control movement
        if (!_isManualOverride && _currentMode != WalkingMode.Disabled && speed > 0.01f)
        {
            if (_latestOpenVRData == null)
            {
                // No OpenVR — move straight forward with no yaw compensation
                vertical = speed;
                horizontal = 0f;
            }
            else if (_initialYaw == null)
            {
                // First frame with OpenVR data — lock initial direction and move forward
                _initialYaw = _latestOpenVRData.Yaw;
                vertical = speed;
                horizontal = 0f;
            }
            else
            {
                // Calculate angle difference: how much head has turned from initial direction
                float currentYaw = _latestOpenVRData.Yaw;
                float yawDiff = currentYaw - _initialYaw.Value;

                // Normalize angle to [-π, π] range
                while (yawDiff > MathF.PI) yawDiff -= 2 * MathF.PI;
                while (yawDiff < -MathF.PI) yawDiff += 2 * MathF.PI;

                // Vector decomposition to maintain movement in initial direction
                vertical = speed * MathF.Cos(yawDiff);
                horizontal = speed * MathF.Sin(yawDiff);
            }
        }

        // Apply smoothing to reduce jitter/choppiness
        // Use configured smoothing factor from settings
        _smoothedVertical = Lerp(_smoothedVertical, vertical, _modeConfig.SmoothingFactor);
        _smoothedStrafe = Lerp(_smoothedStrafe, horizontal, _modeConfig.SmoothingFactor);

        // Locomotion inputs must be sent every tick — VRChat resets them to 0 if they stop arriving
        _oscService.SendMessage("/input/Vertical", _smoothedVertical);
        _oscService.SendMessage("/input/Horizontal", _smoothedStrafe);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    public string GetWalkingDebugInfo()
    {
        var output = new System.Text.StringBuilder();
        output.AppendLine("\n🚶 Walking System Debug Info");
        output.AppendLine("═══════════════════════════════════════════════");

        // Walking Mode
        output.AppendLine($"\n📋 Walking Mode: {_currentMode}");
        output.AppendLine($"   Max Speed: {_modeConfig.MaxSpeed:F2} km/h");
        output.AppendLine($"   Walking Trim: {_modeConfig.WalkingTrim:F2}");

        if (_currentMode == WalkingMode.Override)
        {
            output.AppendLine($"   Override Index: {_modeConfig.CurrentOverrideIndex}");
            output.AppendLine($"   Override Speeds: [{string.Join(", ", _modeConfig.OverrideSpeeds.Select(s => s.ToString("F2")))}]");
        }

        // Current Output Values - using vector decomposition
        float baseSpeed = CalculateVelocity();
        float vertical = 0f;
        float horizontal = 0f;

        // Calculate vector components same as SendVRChatInputs does
        if (_currentMode != WalkingMode.Disabled && baseSpeed > 0.01f && _latestOpenVRData != null && _initialYaw.HasValue)
        {
            float currentYaw = _latestOpenVRData.Yaw;
            float yawDiff = currentYaw - _initialYaw.Value;
            while (yawDiff > MathF.PI) yawDiff -= 2 * MathF.PI;
            while (yawDiff < -MathF.PI) yawDiff += 2 * MathF.PI;

            vertical = baseSpeed * MathF.Cos(yawDiff);
            horizontal = baseSpeed * MathF.Sin(yawDiff);
        }
        else if (baseSpeed > 0.01f)
        {
            vertical = baseSpeed;
        }

        output.AppendLine($"\n📊 Current Outputs (Vector Decomposition):");
        output.AppendLine($"   Base Speed: {baseSpeed:F3}");
        output.AppendLine($"   Vertical (Forward): {vertical:F3}");
        output.AppendLine($"   Horizontal (Strafe): {horizontal:F3}");
        output.AppendLine($"   Resultant Magnitude: {MathF.Sqrt(vertical * vertical + horizontal * horizontal):F3}");

        // VR Headset Tracking
        if (_latestOpenVRData != null)
        {
            output.AppendLine($"\n🥽 VR Headset Tracking:");
            output.AppendLine($"   Current Yaw:   {_latestOpenVRData.Yaw:F3} rad ({_latestOpenVRData.Yaw * 180 / Math.PI:F1}°)");
            output.AppendLine($"   Initial Yaw:   {(_initialYaw.HasValue ? $"{_initialYaw.Value:F3} rad ({_initialYaw.Value * 180 / Math.PI:F1}°)" : "Not set")}");

            if (_initialYaw.HasValue)
            {
                float yawDiff = _latestOpenVRData.Yaw - _initialYaw.Value;
                while (yawDiff > MathF.PI) yawDiff -= 2 * MathF.PI;
                while (yawDiff < -MathF.PI) yawDiff += 2 * MathF.PI;
                output.AppendLine($"   Yaw Difference: {yawDiff:F3} rad ({yawDiff * 180 / Math.PI:F1}°)");
            }

            output.AppendLine($"   Pitch (X-axis): {_latestOpenVRData.Pitch:F3} rad ({_latestOpenVRData.Pitch * 180 / Math.PI:F1}°)");
            output.AppendLine($"   Roll (Z-axis):  {_latestOpenVRData.Roll:F3} rad ({_latestOpenVRData.Roll * 180 / Math.PI:F1}°)");
            output.AppendLine($"   Position: X={_latestOpenVRData.PositionX:F3}, Y={_latestOpenVRData.PositionY:F3}, Z={_latestOpenVRData.PositionZ:F3}");
            output.AppendLine($"\n🎮 Right Controller:");
            output.AppendLine($"   Thumbstick Y: {_latestOpenVRData.RightThumbstickY:F3} (Up=+1, Down=-1)");
            float targetMod = 1f + _latestOpenVRData.RightThumbstickY;
            output.AppendLine($"   Target Modifier: {Math.Clamp(targetMod, 0f, 2f):F2}x");
            output.AppendLine($"   Current Modifier: {_currentSpeedModifier:F2}x (smoothed)");

            output.AppendLine($"\n🎮 Left Controller:");
            output.AppendLine($"   Thumbstick X: {_latestOpenVRData.LeftThumbstickX:F3}");
            output.AppendLine($"   Thumbstick Y: {_latestOpenVRData.LeftThumbstickY:F3}");
            output.AppendLine($"   Manual Override: {(_isManualOverride ? "ACTIVE (output disabled)" : "Inactive")}");
        }
        else
        {
            output.AppendLine($"\n🥽 VR Headset: No tracking data");
        }

        // Treadmill Data
        if (_latestTelemetry != null)
        {
            var speedValue = _latestTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed);
            if (speedValue != null)
            {
                output.AppendLine($"\n🏃 Treadmill:");
                output.AppendLine($"   Speed: {speedValue.Value:F2} km/h");
            }
        }
        else
        {
            output.AppendLine($"\n🏃 Treadmill: No telemetry data");
        }

        return output.ToString();
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _appState.AppStateUpdated -= OnAppStateUpdated;
        _appState.YawResetRequested -= OnYawResetRequested;
        _openVRService.OnDataUpdateReceived -= OnOpenVRDataReceived;
        _openVRService.OnActionReceived -= OnActionReceived;
    }
}
