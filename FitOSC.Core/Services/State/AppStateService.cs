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

    /// <summary>
    /// Event raised when a yaw reset is requested (e.g. from VRChat OSC menu).
    /// </summary>
    public event Action? YawResetRequested;

    /// <summary>
    /// Request the locomotion service to reset the initial yaw direction.
    /// </summary>
    public void RequestYawReset() => YawResetRequested?.Invoke();

    // Throttling configuration
    private DateTime _lastNotifyTime = DateTime.MinValue;
    private const int MinNotifyIntervalMs = 100; // Max 10 updates per second to UI
    private bool _hasPendingNotify = false;
    private long _notifyVersion;
    private readonly object _notifyLock = new();
    private readonly Queue<AppStateInfo> _notifications = new();
    private bool _isDrainingNotifications;

    // Previous OpenVR values for dirty checking
    private float _prevYaw, _prevPitch, _prevRoll, _prevThumbstickY;
    private const float OpenVRChangeTolerance = 0.01f; // Only update if change exceeds this threshold

    /// <summary>
    /// Queues a snapshot while the caller holds _notifyLock.
    /// The caller must drain notifications after releasing the lock.
    /// </summary>
    private void PrepareAppStateNotification(bool forceImmediate = false)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastNotifyTime).TotalMilliseconds;

        if (!forceImmediate && elapsed < MinNotifyIntervalMs)
        {
            if (!_hasPendingNotify)
            {
                _hasPendingNotify = true;
                var version = ++_notifyVersion;
                _ = Task.Delay(MinNotifyIntervalMs - (int)elapsed).ContinueWith(_ =>
                {
                    lock (_notifyLock)
                    {
                        if (!_hasPendingNotify || version != _notifyVersion)
                            return;

                        _hasPendingNotify = false;
                        _lastNotifyTime = DateTime.UtcNow;
                        _notifications.Enqueue(GetCurrentAppStateInfo());
                    }

                    DrainAppStateNotifications();
                });
            }
            return;
        }

        _hasPendingNotify = false;
        ++_notifyVersion;
        _lastNotifyTime = now;
        _notifications.Enqueue(GetCurrentAppStateInfo());
    }

    private void DrainAppStateNotifications()
    {
        lock (_notifyLock)
        {
            if (_isDrainingNotifications || _notifications.Count == 0)
                return;

            _isDrainingNotifications = true;
        }

        List<Exception>? subscriberErrors = null;
        while (true)
        {
            AppStateInfo snapshot;
            lock (_notifyLock)
            {
                if (_notifications.Count == 0)
                {
                    _isDrainingNotifications = false;
                    break;
                }

                snapshot = _notifications.Dequeue();
            }

            try
            {
                AppStateUpdated?.Invoke(snapshot);
            }
            catch (Exception exception)
            {
                // Finish queued publications before propagating subscriber failures.
                // Reentrant state changes must not be stranded behind a failed notification.
                (subscriberErrors ??= new()).Add(exception);
            }
        }

        if (subscriberErrors?.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(subscriberErrors[0]).Throw();
        if (subscriberErrors?.Count > 1)
            throw new AggregateException(subscriberErrors);
    }

    public AppStateInfo GetCurrentAppStateInfo()
    {
        lock (_notifyLock)
        {
            return new AppStateInfo
            {
                TreadmillState = LatestState,
                TreadmillTelemetry = _latestData.Snapshot(),
                TreadmillConfiguration = LatestConfiguration!,
                UserMeasurementType = Units,
                ConnectionStates = new System.Collections.ObjectModel.ReadOnlyDictionary<AppInterface, ConnectionStatus>(
                    new Dictionary<AppInterface, ConnectionStatus>(InterfaceStatus)),
                DeviceName = ConnectedDeviceName,
                OpenVR = new OpenVRData
                {
                    Yaw = LatestHeadYaw,
                    Pitch = LatestHeadPitch,
                    Roll = LatestHeadRoll,
                    RightThumbstickY = LatestRightThumbstickY
                },
                Walking = new WalkingData
                {
                    Mode = CurrentWalkingMode,
                    Config = new WalkingModeConfigurationSnapshot
                    {
                        MaxSpeed = _walkingModeConfig.MaxSpeed,
                        WalkingTrim = _walkingModeConfig.WalkingTrim,
                        OverrideSpeeds = Array.AsReadOnly(_walkingModeConfig.OverrideSpeeds.ToArray()),
                        CurrentOverrideIndex = _walkingModeConfig.CurrentOverrideIndex,
                        SmoothingFactor = _walkingModeConfig.SmoothingFactor,
                        MaxTurnAngle = _walkingModeConfig.MaxTurnAngle,
                        UpdateIntervalMs = _walkingModeConfig.UpdateIntervalMs,
                        ThumbstickRampSpeed = _walkingModeConfig.ThumbstickRampSpeed
                    },
                    Velocity = LatestWalkingVelocity,
                    Horizontal = LatestWalkingHorizontal,
                    Vertical = LatestWalkingVertical,
                    IsManualOverride = IsManualOverrideActive
                }
            };
        }
    }

    // ---- Settings ----
    /// <summary>
    /// Current unit system (Metric/Imperial).
    /// </summary>
    public MeasurementType Units { get; private set; } = MeasurementType.Imperial;

    /// <summary>
    /// Latest treadmill telemetry (raw values as parsed from BLE).
    /// </summary>
    private TreadmillTelemetry _latestData = new();
    private object? _treadmillInput;

    /// <summary>Starts ownership of treadmill publications for a new service.</summary>
    public void BeginTreadmillInput(object input)
    {
        lock (_notifyLock) _treadmillInput = input;
    }

    /// <summary>Atomically releases this input; callbacks from older services cannot change newer input.</summary>
    public void ReleaseTreadmillInput(object input)
    {
        lock (_notifyLock)
        {
            if (!ReferenceEquals(_treadmillInput, input)) return;
            _treadmillInput = null;
            var cleared = new TreadmillTelemetry();
            if (InterfaceStatus[AppInterface.Pulsoid] == ConnectionStatus.Connected)
                cleared.Values[TreadmillTelemetryProperty.HeartRate] = _latestData.Values[TreadmillTelemetryProperty.HeartRate].Copy();
            _latestData = cleared;
            LatestState = TreadmillState.Stopped;
            ConnectedDeviceName = null;
            InterfaceStatus[AppInterface.Bluetooth] = ConnectionStatus.Disconnected;
            PrepareAppStateNotification(forceImmediate: true);
        }
        DrainAppStateNotifications();
    }
    public TreadmillTelemetrySnapshot LatestData
    {
        get
        {
            lock (_notifyLock) return _latestData.Snapshot();
        }
    }

    /// <summary>
    /// Push new treadmill telemetry into app state.
    /// </summary>
    public void PublishTreadmillData(TreadmillTelemetry telemetry, object input)
    {
        lock (_notifyLock)
        {
            if (!ReferenceEquals(_treadmillInput, input)) return;
            var data = telemetry.Copy();
            if (data.Values.TryGetValue(TreadmillTelemetryProperty.HeartRate, out var incomingHeartRate)
                && !incomingHeartRate.Enabled
                && _latestData.Values.TryGetValue(TreadmillTelemetryProperty.HeartRate, out var previousHeartRate))
            {
                incomingHeartRate.Value = previousHeartRate.Value;
                incomingHeartRate.Enabled = previousHeartRate.Enabled;
            }
            _latestData = data;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    ///  Latest treadmill configuration (e.g., max speed, incline).
    /// </summary>
    public TreadmillConfiguration? LatestConfiguration { get; private set; } = new TreadmillConfiguration();

    /// <summary>
    /// Push new treadmill configuration into app state.
    /// </summary>
    /// <param name="config"></param>
    public void PublishTreadmillConfiguration(TreadmillConfiguration config, object input)
    {
        lock (_notifyLock)
        {
            if (!ReferenceEquals(_treadmillInput, input)) return;
            LatestConfiguration = config;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }




    /// <summary>
    /// Latest treadmill state (e.g., Running, Paused, Stopped).
    /// </summary>
    public TreadmillState LatestState { get; private set; } = TreadmillState.Stopped;



    /// <summary>
    /// Push new treadmill state into app state.
    /// </summary>
    public void PublishTreadmillState(TreadmillState state, object input)
    {
        lock (_notifyLock)
        {
            if (!ReferenceEquals(_treadmillInput, input)) return;
            LatestState = state;
            PrepareAppStateNotification(forceImmediate: true); // Treadmill state changes should be immediate
        }

        DrainAppStateNotifications();
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
        lock (_notifyLock)
        {
            InterfaceStatus[appInterface] = status;
            PrepareAppStateNotification(forceImmediate: true); // Connection changes should be immediate
        }

        DrainAppStateNotifications();
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
        lock (_notifyLock)
        {
            ConnectedDeviceName = deviceName;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Current walking mode (Disabled, Dynamic, Override)
    /// </summary>
    public WalkingMode CurrentWalkingMode { get; private set; } = WalkingMode.Disabled;

    /// <summary>
    /// Walking mode configuration settings
    /// </summary>
    private WalkingModeConfiguration _walkingModeConfig = new();
    public WalkingModeConfiguration WalkingModeConfig
    {
        get
        {
            lock (_notifyLock) return CopyWalkingConfig(_walkingModeConfig);
        }
    }

    private static WalkingModeConfiguration CopyWalkingConfig(WalkingModeConfiguration config) => new()
    {
        MaxSpeed = config.MaxSpeed,
        WalkingTrim = config.WalkingTrim,
        OverrideSpeeds = new List<float>(config.OverrideSpeeds),
        CurrentOverrideIndex = config.CurrentOverrideIndex,
        SmoothingFactor = config.SmoothingFactor,
        MaxTurnAngle = config.MaxTurnAngle,
        UpdateIntervalMs = config.UpdateIntervalMs,
        ThumbstickRampSpeed = config.ThumbstickRampSpeed
    };

    /// <summary>
    /// Set the current walking mode
    /// </summary>
    public void SetWalkingMode(WalkingMode mode)
    {
        lock (_notifyLock)
        {
            CurrentWalkingMode = mode;
            PrepareAppStateNotification(forceImmediate: true); // User action should be immediate
        }

        DrainAppStateNotifications();
    }

    private WalkingMode _lastActiveWalkingMode = WalkingMode.Dynamic;
    private int _lastOverrideIndex = 0;

    /// <summary>
    /// Enable auto-walk, restoring the last active mode and override index.
    /// </summary>
    public void EnableAutoWalk()
    {
        lock (_notifyLock)
        {
            _walkingModeConfig.CurrentOverrideIndex = _lastOverrideIndex;
            CurrentWalkingMode = _lastActiveWalkingMode;
            PrepareAppStateNotification(forceImmediate: true);
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Disable auto-walk, remembering the current mode and override index, then resetting index to 0.
    /// </summary>
    public void DisableAutoWalk()
    {
        lock (_notifyLock)
        {
            if (CurrentWalkingMode != WalkingMode.Disabled)
            {
                _lastActiveWalkingMode = CurrentWalkingMode;
                _lastOverrideIndex = _walkingModeConfig.CurrentOverrideIndex;
            }
            _walkingModeConfig.CurrentOverrideIndex = 0;
            CurrentWalkingMode = WalkingMode.Disabled;
            PrepareAppStateNotification(forceImmediate: true);
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Set the preferred walking mode. If walking is active, switches immediately.
    /// If disabled, remembers the mode for the next EnableAutoWalk call.
    /// </summary>
    public void SetPreferredWalkingMode(WalkingMode mode)
    {
        lock (_notifyLock)
        {
            _lastActiveWalkingMode = mode;
            if (CurrentWalkingMode == WalkingMode.Disabled)
                return;

            CurrentWalkingMode = mode;
            PrepareAppStateNotification(forceImmediate: true);
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Update walking mode configuration
    /// </summary>
    public void UpdateWalkingModeConfig(WalkingModeConfiguration config)
    {
        lock (_notifyLock)
        {
            _walkingModeConfig = CopyWalkingConfig(config);
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Set the current override speed index
    /// </summary>
    public void SetOverrideSpeedIndex(int index)
    {
        lock (_notifyLock)
        {
            if (index < 0 || index >= _walkingModeConfig.OverrideSpeeds.Count)
                return;

            _walkingModeConfig.CurrentOverrideIndex = index;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Adjust the walking trim by a delta value
    /// </summary>
    public void AdjustWalkingTrim(float delta)
    {
        lock (_notifyLock)
        {
            var newTrim = Math.Clamp(_walkingModeConfig.WalkingTrim + delta, 0.0f, 2.0f);
            _walkingModeConfig.WalkingTrim = newTrim;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Temporary walking speed multiplier applied while a boost button is held (default 1.0).
    /// </summary>
    public float TemporaryWalkingBoost { get; private set; } = 1.0f;

    /// <summary>
    /// Set the temporary walking speed multiplier (1.0 = normal, >1.0 = faster, <1.0 = slower).
    /// </summary>
    public void SetTemporaryWalkingBoost(float multiplier)
    {
        lock (_notifyLock)
        {
            TemporaryWalkingBoost = multiplier;
        }
    }

    /// <summary>
    /// Update preferred unit system (Metric/Imperial)
    /// </summary>
    public void UpdatePreferredUnits(bool preferMetric)
    {
        lock (_notifyLock)
        {
            Units = preferMetric ? MeasurementType.Metric : MeasurementType.Imperial;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
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
        lock (_notifyLock)
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
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
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
        lock (_notifyLock)
        {
            LatestWalkingVelocity = velocity;
            LatestWalkingHorizontal = horizontal;
            LatestWalkingVertical = vertical;
            IsManualOverrideActive = isManualOverride;
            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Push new Pulsoid heart rate into app state.
    /// Directly updates the telemetry HeartRate value.
    /// </summary>
    public void PublishPulsoidHeartRate(int heartRate)
    {
        lock (_notifyLock)
        {
            // Directly update the telemetry heart rate - no separate field needed
            if (_latestData.Values.TryGetValue(TreadmillTelemetryProperty.HeartRate, out var hrValue))
            {
                hrValue.Value = heartRate;
                hrValue.Enabled = heartRate > 0;
            }

            PrepareAppStateNotification();
        }

        DrainAppStateNotifications();
    }

    /// <summary>
    /// Check if Pulsoid is connected
    /// </summary>
    public bool IsPulsoidConnected
    {
        get
        {
            lock (_notifyLock)
                return InterfaceStatus.TryGetValue(AppInterface.Pulsoid, out var status) && status == ConnectionStatus.Connected;
        }
    }
}
