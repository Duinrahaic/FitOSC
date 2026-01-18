using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.State;

/// <summary>
/// Global app state service for settings, treadmill telemetry, and state publishing.
/// </summary>
public class AppStateService
{
    /// <summary>
    /// Event raised whenever treadmill telemetry is updated.
    /// </summary>
    public event Action<AppStateInfo>? AppStateUpdated;

    // Throttling configuration
    private DateTime _lastNotifyTime = DateTime.MinValue;
    private const int MinNotifyIntervalMs = 100; // Max 10 updates per second to UI
    private bool _hasPendingNotify = false;
    private readonly object _notifyLock = new();

    // Previous OpenVR values for dirty checking
    private float _prevYaw, _prevPitch, _prevRoll, _prevThumbstickY;
    private const float OpenVRChangeTolerance = 0.01f; // Only update if change exceeds this threshold

    // Cached AppStateInfo to reduce allocations - reused objects
    private readonly AppStateInfo _cachedAppState = new()
    {
        OpenVR = new OpenVRData(),
        Walking = new WalkingData()
    };

    /// <summary>
    /// Raises the <see cref="AppStateUpdated"/> event to notify subscribers of state changes.
    /// Includes throttling to prevent excessive UI updates.
    /// </summary>
    private void NotifyAppStateUpdated(bool forceImmediate = false)
    {
        lock (_notifyLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastNotifyTime).TotalMilliseconds;

            if (!forceImmediate && elapsed < MinNotifyIntervalMs)
            {
                // Schedule a pending notification if not already scheduled
                if (!_hasPendingNotify)
                {
                    _hasPendingNotify = true;
                    _ = Task.Delay(MinNotifyIntervalMs - (int)elapsed).ContinueWith(_ =>
                    {
                        lock (_notifyLock)
                        {
                            if (_hasPendingNotify)
                            {
                                _hasPendingNotify = false;
                                _lastNotifyTime = DateTime.UtcNow;
                                UpdateCachedAppState();
                                AppStateUpdated?.Invoke(_cachedAppState);
                            }
                        }
                    });
                }
                return;
            }

            _hasPendingNotify = false;
            _lastNotifyTime = now;
        }

        // Update and send cached app state info (no new allocations)
        UpdateCachedAppState();
        AppStateUpdated?.Invoke(_cachedAppState);
    }

    /// <summary>
    /// Updates the cached AppStateInfo in-place to avoid allocations.
    /// </summary>
    private void UpdateCachedAppState()
    {
        _cachedAppState.TreadmillState = LatestState;
        _cachedAppState.TreadmillTelemetry = LatestData ?? _cachedAppState.TreadmillTelemetry;
        _cachedAppState.UserMeasurementType = Units;
        _cachedAppState.ConnectionStates = InterfaceStatus;
        _cachedAppState.TreadmillConfiguration = LatestConfiguration ?? _cachedAppState.TreadmillConfiguration;
        _cachedAppState.DeviceName = ConnectedDeviceName;

        // Update nested OpenVR data in-place
        _cachedAppState.OpenVR.Yaw = LatestHeadYaw;
        _cachedAppState.OpenVR.Pitch = LatestHeadPitch;
        _cachedAppState.OpenVR.Roll = LatestHeadRoll;
        _cachedAppState.OpenVR.RightThumbstickY = LatestRightThumbstickY;

        // Update nested Walking data in-place
        _cachedAppState.Walking.Mode = CurrentWalkingMode;
        _cachedAppState.Walking.Config = WalkingModeConfig;
        _cachedAppState.Walking.Velocity = LatestWalkingVelocity;
        _cachedAppState.Walking.Horizontal = LatestWalkingHorizontal;
        _cachedAppState.Walking.Vertical = LatestWalkingVertical;
        _cachedAppState.Walking.IsManualOverride = IsManualOverrideActive;
    }

    public AppStateInfo GetCurrentAppStateInfo()
    {
        UpdateCachedAppState();
        return _cachedAppState;
    }
    
    
    // ---- Settings ----
    /// <summary>
    /// Current unit system (Metric/Imperial).
    /// </summary>
    public MeasurementType Units { get; private set; } = MeasurementType.Imperial;
    
    /// <summary>
    /// Latest treadmill telemetry (raw values as parsed from BLE).
    /// </summary>
    public TreadmillTelemetry? LatestData { get; private set; } = new TreadmillTelemetry();

    /// <summary>
    /// Push new treadmill telemetry into app state.
    /// </summary>
    public void PublishTreadmillData(TreadmillTelemetry telemetry)
    {
        LatestData = telemetry;
        NotifyAppStateUpdated();
    }
    
    /// <summary>
    ///  Latest treadmill configuration (e.g., max speed, incline).
    /// </summary>
    public TreadmillConfiguration? LatestConfiguration { get; private set; } = new TreadmillConfiguration(); 
    
    /// <summary>
    /// Push new treadmill configuration into app state.
    /// </summary>
    /// <param name="config"></param>
    public void PublishTreadmillConfiguration(TreadmillConfiguration config)
    {
        LatestConfiguration = config;
        NotifyAppStateUpdated();
    }
    
    
    
    
    /// <summary>
    /// Latest treadmill state (e.g., Running, Paused, Stopped).
    /// </summary>
    public TreadmillState LatestState { get; private set; } = TreadmillState.Stopped;

 
    
    /// <summary>
    /// Push new treadmill state into app state.
    /// </summary>
    public void PublishTreadmillState(TreadmillState state)
    {
        LatestState = state;
        NotifyAppStateUpdated(forceImmediate: true); // Treadmill state changes should be immediate
    }



    private readonly Dictionary<AppInterface, ConnectionStatus> InterfaceStatus = new()
    {
        { AppInterface.Bluetooth, ConnectionStatus.Disconnected },
        { AppInterface.VR, ConnectionStatus.Disconnected },
        { AppInterface.Pulsoid, ConnectionStatus.Disconnected },
        { AppInterface.OSC, ConnectionStatus.Disconnected }
    };

    public void PublishInterfaceConnectionStatuses(AppInterface appInterface, ConnectionStatus status)
    {
        InterfaceStatus[appInterface] = status;
        NotifyAppStateUpdated(forceImmediate: true); // Connection changes should be immediate
    }

    /// <summary>
    /// Currently connected Bluetooth device name
    /// </summary>
    public string? ConnectedDeviceName { get; private set; }

    /// <summary>
    /// Set the connected device name
    /// </summary>
    public void SetConnectedDeviceName(string? deviceName)
    {
        ConnectedDeviceName = deviceName;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Current walking mode (Disabled, Dynamic, Override)
    /// </summary>
    public WalkingMode CurrentWalkingMode { get; private set; } = WalkingMode.Disabled;

    /// <summary>
    /// Walking mode configuration settings
    /// </summary>
    public WalkingModeConfiguration WalkingModeConfig { get; private set; } = new WalkingModeConfiguration();

    /// <summary>
    /// Set the current walking mode
    /// </summary>
    public void SetWalkingMode(WalkingMode mode)
    {
        CurrentWalkingMode = mode;
        NotifyAppStateUpdated(forceImmediate: true); // User action should be immediate
    }

    /// <summary>
    /// Update walking mode configuration
    /// </summary>
    public void UpdateWalkingModeConfig(WalkingModeConfiguration config)
    {
        WalkingModeConfig = config;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Set the current override speed index
    /// </summary>
    public void SetOverrideSpeedIndex(int index)
    {
        if (index >= 0 && index < WalkingModeConfig.OverrideSpeeds.Count)
        {
            WalkingModeConfig.CurrentOverrideIndex = index;
            NotifyAppStateUpdated();
        }
    }

    /// <summary>
    /// Adjust the walking trim by a delta value
    /// </summary>
    public void AdjustWalkingTrim(float delta)
    {
        var newTrim = Math.Clamp(WalkingModeConfig.WalkingTrim + delta, 0.0f, 2.0f);
        WalkingModeConfig.WalkingTrim = newTrim;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Update preferred unit system (Metric/Imperial)
    /// </summary>
    public void UpdatePreferredUnits(bool preferMetric)
    {
        Units = preferMetric ? MeasurementType.Metric : MeasurementType.Imperial;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Latest OpenVR head tracking data
    /// </summary>
    public float LatestHeadYaw { get; private set; } = 0;
    public float LatestHeadPitch { get; private set; } = 0;
    public float LatestHeadRoll { get; private set; } = 0;
    public float LatestRightThumbstickY { get; private set; } = 0;

    /// <summary>
    /// Push new OpenVR tracking data into app state.
    /// Uses dirty checking to skip updates when values haven't changed significantly.
    /// </summary>
    public void PublishOpenVRData(float yaw, float pitch, float roll, float rightThumbstickY = 0)
    {
        // Dirty check: only update if values have changed beyond tolerance
        bool hasSignificantChange =
            MathF.Abs(yaw - _prevYaw) > OpenVRChangeTolerance ||
            MathF.Abs(pitch - _prevPitch) > OpenVRChangeTolerance ||
            MathF.Abs(roll - _prevRoll) > OpenVRChangeTolerance ||
            MathF.Abs(rightThumbstickY - _prevThumbstickY) > OpenVRChangeTolerance;

        if (!hasSignificantChange)
            return;

        // Update previous values
        _prevYaw = yaw;
        _prevPitch = pitch;
        _prevRoll = roll;
        _prevThumbstickY = rightThumbstickY;

        // Update current values
        LatestHeadYaw = yaw;
        LatestHeadPitch = pitch;
        LatestHeadRoll = roll;
        LatestRightThumbstickY = rightThumbstickY;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Latest walking velocity and direction (calculated from VRChat locomotion service)
    /// </summary>
    public float LatestWalkingVelocity { get; private set; } = 0;
    public float LatestWalkingHorizontal { get; private set; } = 0;
    public float LatestWalkingVertical { get; private set; } = 0;
    public bool IsManualOverrideActive { get; private set; } = false;

    /// <summary>
    /// Push new walking data into app state
    /// </summary>
    public void PublishWalkingData(float velocity, float horizontal, float vertical, bool isManualOverride = false)
    {
        LatestWalkingVelocity = velocity;
        LatestWalkingHorizontal = horizontal;
        LatestWalkingVertical = vertical;
        IsManualOverrideActive = isManualOverride;
        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Push new Pulsoid heart rate into app state.
    /// Directly updates the telemetry HeartRate value.
    /// </summary>
    public void PublishPulsoidHeartRate(int heartRate)
    {
        // Directly update the telemetry heart rate - no separate field needed
        if (LatestData != null && LatestData.Values.TryGetValue(TreadmillTelemetryProperty.HeartRate, out var hrValue))
        {
            hrValue.Value = heartRate;
            hrValue.Enabled = heartRate > 0;
        }

        NotifyAppStateUpdated();
    }

    /// <summary>
    /// Check if Pulsoid is connected
    /// </summary>
    public bool IsPulsoidConnected => InterfaceStatus.TryGetValue(AppInterface.Pulsoid, out var status)
                                      && status == ConnectionStatus.Connected;
}