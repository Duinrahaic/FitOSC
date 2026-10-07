using System.Numerics;
using System.Runtime.InteropServices;
using FitOSC.Models;
using FitOSC.Services.OpenVR;
using FitOSC.Services.State;

// ReSharper disable once CheckNamespace
namespace Valve.VR;

public class OpenVRService(IServiceProvider services, OpenVROptions options, IOpenVROverlay overlay) : IHostedService, IDisposable
{
    public const string AppKey = "fitosc.treadmill";

    public delegate void DataUpdateReceivedEventHandler(OpenVRDataEvent e);
    public delegate void ActionEventReceivedEventHandler(OpenVRActionEvent e);

    private static (float yaw, float pitch, float roll)? _initialDirection;

    private readonly ILogger<OpenVRService>? _logger = services.GetService<ILogger<OpenVRService>>();
    private readonly AppStateService? _appStateService = services.GetService<AppStateService>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _wakeLock = new();
    private readonly Lock _runtimeLock = new();
    private Task? _loop;
    private volatile bool _monitoringRequested;
    private volatile bool _reconnectRequested;
    private bool _connected;
    private bool _overlayActive;
    private int _disposed;
    private EVRInitError _initError;
    private bool _actionsInitialized;

    // Polling rate constants (in milliseconds)
    private const int ActivePollingRateMs = 100;  // 10Hz when walking mode is active
    private const int IdlePollingRateMs = 100;    // 10Hz when walking mode is disabled

    // SteamVR Action Handles
    private ulong _actionSetHandle;
    private ulong _speedModifierHandle;
    private ulong _manualMovementHandle;
    private ulong _toggleWalkingHandle;
    private ulong _toggleHudHandle;
    private ulong _recenterYawHandle;
    private ulong _overrideSpeedUpHandle;
    private ulong _overrideSpeedDownHandle;
    private ulong _tempSpeedUpHandle;
    private ulong _tempSpeedDownHandle;
    private ulong _treadmillEnableHandle;
    private ulong _treadmillSpeedUpHandle;
    private ulong _treadmillSlowDownHandle;
    private ulong _treadmillInclineUpHandle;
    private ulong _treadmillInclineDownHandle;
    private ulong _enableDynamicHandle;
    private ulong _walkingSpeedUpHandle;
    private ulong _walkingSpeedDownHandle;
    private ulong _enableOverrideHandle;
    private ulong _preset1Handle;
    private ulong _preset2Handle;
    private ulong _preset3Handle;
    private ulong _preset4Handle;

    public bool IsMonitoring { get; private set; }
    public bool ActionsAvailable => _actionsInitialized;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Dispose();
        _wake.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitoringRequested = !options.Disabled;
        if (options.Disabled)
        {
            _logger?.LogInformation("OpenVR service disabled via --no-vr flag");
            PublishStatus(ConnectionStatus.Disconnected);
        }
        _loop = Task.Run(() => RunAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
    }

    private void Wake()
    {
        // Serialize producers; the consumer can only decrease the count.
        lock (_wakeLock)
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    public void StartMonitoring()
    {
        lock (_runtimeLock)
        {
            if (_connected) _reconnectRequested = true;
            _monitoringRequested = true;
        }
        Wake();
    }

    public void StopMonitoring()
    {
        _monitoringRequested = false;
        Wake();
    }

    private void PublishStatus(ConnectionStatus status) =>
        _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, status);

    private bool Connected
    {
        get { lock (_runtimeLock) return _connected; }
    }

    private bool TakeReconnectRequest()
    {
        lock (_runtimeLock)
        {
            var requested = _reconnectRequested;
            _reconnectRequested = false;
            return requested;
        }
    }

    // One serialized logical owner; awaits may resume on a different OS thread.
    private async Task RunAsync(CancellationToken token)
    {
        var attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_monitoringRequested)
                {
                    if (Connected) Disconnect(ConnectionStatus.Disconnected);
                    await _wake.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (TakeReconnectRequest())
                    {
                        if (Connected) Disconnect(ConnectionStatus.Connecting);
                    }
                    if (!Connected)
                    {
                        if (attempt > 0)
                        {
                            if (attempt % 6 == 0) _logger?.LogInformation("Attempting to reconnect to SteamVR...");
                            PublishStatus(ConnectionStatus.Connecting);
                        }
                        if (!TryConnect())
                        {
                            attempt++;
                            await _wake.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                            continue;
                        }
                        attempt = 0;
                    }
                    if (!Tick())
                    {
                        Disconnect(ConnectionStatus.Disconnected);
                        await _wake.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "OpenVR connection or polling failed");
                    if (Connected) Disconnect(ConnectionStatus.Error);
                    else PublishStatus(ConnectionStatus.Error);
                    await _wake.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    continue;
                }
                var rate = _appStateService?.CurrentWalkingMode != WalkingMode.Disabled
                    ? ActivePollingRateMs : IdlePollingRateMs;
                await _wake.WaitAsync(rate, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (Connected) Disconnect(ConnectionStatus.Disconnected);
        }
    }

    private void DisconnectOverlay()
    {
        try { overlay.Disconnect(); }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to disconnect overlays"); }
        finally { _overlayActive = false; }
    }

    private void Disconnect(ConnectionStatus status)
    {
        lock (_runtimeLock)
        {
            if (_overlayActive) DisconnectOverlay();
            try { OpenVR.Shutdown(); }
            finally
            {
                _connected = false;
                _actionsInitialized = false;
                IsMonitoring = false;
                _initialDirection = null;
            }
        }
        PublishStatus(status);
    }

    public event DataUpdateReceivedEventHandler? OnDataUpdateReceived;
    public event ActionEventReceivedEventHandler? OnActionReceived;

    private bool InitializeActions()
    {
        try
        {
            // Get the path to the action manifest
            var exePath = AppContext.BaseDirectory;
            var actionManifestPath = Path.Combine(exePath, "SteamVR", "actions.json");
            var appManifestPath = options.ApplicationManifestPath;

            if (!File.Exists(actionManifestPath))
            {
                _logger?.LogWarning("SteamVR action manifest not found at: {Path}", actionManifestPath);
                return false;
            }

            // Register the application manifest so FitOSC appears in SteamVR bindings UI
            if (File.Exists(appManifestPath))
            {
                var appError = OpenVR.Applications.AddApplicationManifest(appManifestPath, false);
                if (appError != EVRApplicationError.None)
                {
                    _logger?.LogWarning("Failed to register app manifest (non-critical): {Error}", appError);
                }
                else
                {
                    _logger?.LogInformation("FitOSC registered with SteamVR");
                }
            }

            // Set the action manifest path
            var error = OpenVR.Input.SetActionManifestPath(actionManifestPath);
            if (error != EVRInputError.None)
            {
                _logger?.LogError("Failed to set action manifest path: {Error}", error);
                return false;
            }

            // Get action set handle
            error = OpenVR.Input.GetActionSetHandle("/actions/fitosc", ref _actionSetHandle);
            if (error != EVRInputError.None)
            {
                _logger?.LogError("Failed to get action set handle: {Error}", error);
                return false;
            }

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/ToggleHud", ref _toggleHudHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get ToggleHud action handle: {Error}", error);

            // Get action handles
            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/SpeedModifier", ref _speedModifierHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get SpeedModifier action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/ManualMovement", ref _manualMovementHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get ManualMovement action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/ToggleWalking", ref _toggleWalkingHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get ToggleWalking action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/RecenterYaw", ref _recenterYawHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get RecenterYaw action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/OverrideSpeedUp", ref _overrideSpeedUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get OverrideSpeedUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/OverrideSpeedDown", ref _overrideSpeedDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get OverrideSpeedDown action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TempSpeedUp", ref _tempSpeedUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TempSpeedUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TempSpeedDown", ref _tempSpeedDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TempSpeedDown action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TreadmillEnable", ref _treadmillEnableHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TreadmillEnable action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TreadmillSpeedUp", ref _treadmillSpeedUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TreadmillSpeedUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TreadmillSlowDown", ref _treadmillSlowDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TreadmillSlowDown action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TreadmillInclineUp", ref _treadmillInclineUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TreadmillInclineUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TreadmillInclineDown", ref _treadmillInclineDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TreadmillInclineDown action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/EnableDynamic", ref _enableDynamicHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get EnableDynamic action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/WalkingSpeedUp", ref _walkingSpeedUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get WalkingSpeedUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/WalkingSpeedDown", ref _walkingSpeedDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get WalkingSpeedDown action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/EnableOverride", ref _enableOverrideHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get EnableOverride action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/Preset1", ref _preset1Handle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get Preset1 action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/Preset2", ref _preset2Handle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get Preset2 action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/Preset3", ref _preset3Handle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get Preset3 action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/Preset4", ref _preset4Handle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get Preset4 action handle: {Error}", error);

            _logger?.LogInformation("SteamVR actions initialized successfully. Handles: ActionSet={ActionSet}, SpeedMod={Speed}, Manual={Manual}, Toggle={Toggle}, Recenter={Recenter}",
                _actionSetHandle, _speedModifierHandle, _manualMovementHandle, _toggleWalkingHandle, _recenterYawHandle);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize SteamVR actions");
            return false;
        }
    }


    private bool TryConnect()
    {
        bool success;
        lock (_runtimeLock)
        {
            OpenVR.Shutdown();
            _actionsInitialized = false;

            OpenVR.Init(ref _initError, EVRApplicationType.VRApplication_Overlay);
            if (_initError != EVRInitError.None)
            {
                var errorMessage = _initError switch
                {
                    EVRInitError.Init_HmdNotFound => "VR headset not found. Make sure your headset is connected.",
                    EVRInitError.Init_VRClientDLLNotFound => "SteamVR is not installed or not found.",
                    EVRInitError.Init_InterfaceNotFound => "SteamVR is not running. Please start SteamVR.",
                    EVRInitError.Init_PathRegistryNotFound => "SteamVR path registry not found. Try reinstalling SteamVR.",
                    EVRInitError.Init_NoConfigPath => "SteamVR configuration path not found.",
                    _ => ""
                };
                _logger?.LogError("OpenVR initialization failed: {Error}. {Message}", _initError, errorMessage);
                success = false;
            }
            else
            {
                _connected = true;
                IsMonitoring = true;

                _logger?.LogInformation("OpenVR initialized successfully. VR headset detected.");

                // Initialize SteamVR actions
                _actionsInitialized = InitializeActions();
                if (!_actionsInitialized)
                {
                    _logger?.LogWarning("SteamVR actions not available. Controller bindings will not work.");
                }

                // Reset logging flags so warnings surface again after reconnection
                _loggedActionDebug = false;
                _loggedActionSuccess = false;

                try
                {
                    overlay.Connect();
                    _overlayActive = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Overlays unavailable for this connection");
                    DisconnectOverlay();
                }
                success = true;
            }
        }
        PublishStatus(success ? ConnectionStatus.Connected : ConnectionStatus.Error);
        return success;
    }

    public bool GetAutoLaunch()
    {
        lock (_runtimeLock)
        {
            try
            {
                if (!_connected)
                    return false;

                return OpenVR.Applications.GetApplicationAutoLaunch(AppKey);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get auto-launch status");
                return false;
            }
        }
    }

    public bool SetAutoLaunch(bool enabled)
    {
        lock (_runtimeLock)
        {
            try
            {
                if (!_connected)
                {
                    _logger?.LogWarning("Cannot set auto-launch: OpenVR.Applications not available");
                    return false;
                }

                var error = OpenVR.Applications.SetApplicationAutoLaunch(AppKey, enabled);
                if (error != EVRApplicationError.None)
                {
                    _logger?.LogError("Failed to set auto-launch: {Error}", error);
                    return false;
                }

                _logger?.LogInformation("SteamVR auto-launch {Status}", enabled ? "enabled" : "disabled");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to set auto-launch status");
                return false;
            }
        }
    }

    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

    private bool Tick()
    {
        OpenVRActionEvent? actionEvent;
        OpenVRActionEvent vrInput;
        bool wristPressed = false;
        bool hmdValid;
        HmdMatrix34_t poseMatrix;
        lock (_runtimeLock)
        {
            var vrSystem = OpenVR.System;
            var ev = new VREvent_t();
            while (vrSystem.PollNextEvent(ref ev, (uint)Marshal.SizeOf<VREvent_t>()))
                if (ev.eventType == (uint)EVREventType.VREvent_Quit) return false;
            vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
            var actionSet = new VRActiveActionSet_t
            {
                ulActionSet = _actionSetHandle,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                nPriority = 0
            };
            actionEvent = _actionsInitialized ? PollActions(ref actionSet, (uint)Marshal.SizeOf<VRActiveActionSet_t>()) : null;
            vrInput = actionEvent ?? new OpenVRActionEvent();
            if (_overlayActive)
            {
                try { wristPressed = overlay.Update(_poses, vrInput); }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Overlay update failed; disabling overlays for this connection");
                    DisconnectOverlay();
                }
            }
            hmdValid = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bDeviceIsConnected &&
                _poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid;
            poseMatrix = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking;
        }
        if (hmdValid)
        {
            var position = (X: poseMatrix.m3, Y: poseMatrix.m7, Z: poseMatrix.m11);
            var quaternion = GetRotationFromMatrix(poseMatrix);
            var euler = QuaternionToEuler(quaternion);

            if (_initialDirection == null)
            {
                _initialDirection = euler;
                _logger?.LogInformation($"Initial Direction: {_initialDirection}");
            }

            var yawDifference = NormalizeAngleDifference(euler.yaw - _initialDirection.Value.yaw);
            var verticalInput = 1f;
            var horizontalInput = Math.Clamp(yawDifference, -1f, 1f);

            var turnCmd = euler.roll switch
            {
                > 0.6f => OpenVRTurn.Left,
                < -0.6f => OpenVRTurn.Right,
                _ => OpenVRTurn.None
            };

            // Use action-based input if available, otherwise use values from action polling
            float rightThumbstickY = vrInput.SpeedModifier;
            float leftThumbstickX = vrInput.ManualMovementX;
            float leftThumbstickY = vrInput.ManualMovementY;

            // Publish to AppStateService for centralized state management
            _appStateService?.PublishOpenVRData(euler.yaw, euler.pitch, euler.roll, rightThumbstickY);

            OnDataUpdateReceived?.Invoke(new OpenVRDataEvent
            {
                Turn = turnCmd,
                VerticalAdjustment = verticalInput,
                HorizontalAdjustment = horizontalInput,
                Yaw = euler.yaw,
                Pitch = euler.pitch,
                Roll = euler.roll,
                PositionX = position.X,
                PositionY = position.Y,
                PositionZ = position.Z,
                RightThumbstickY = rightThumbstickY,
                LeftThumbstickX = leftThumbstickX,
                LeftThumbstickY = leftThumbstickY
            });

            // Fire action event if we have one
            if (actionEvent != null || wristPressed)
            {
                OnActionReceived?.Invoke(vrInput);
            }
        }
        return true;
    }

    private bool _loggedActionDebug = false;
    private bool _loggedActionSuccess = false;

    private OpenVRActionEvent? PollActions(ref VRActiveActionSet_t actionSet, uint actionSetSize)
    {
        try
        {
            // Update action state
            var actionSets = new VRActiveActionSet_t[] { actionSet };
            var error = OpenVR.Input.UpdateActionState(actionSets, actionSetSize);
            if (error != EVRInputError.None)
            {
                if (!_loggedActionDebug)
                {
                    _logger?.LogWarning("UpdateActionState failed: {Error}", error);
                    _loggedActionDebug = true;
                }
                return null;
            }

            if (!_loggedActionSuccess)
            {
                _logger?.LogInformation("SteamVR action polling active");
                _loggedActionSuccess = true;
            }

            var actionEvent = new OpenVRActionEvent();

            // Get analog action data for speed modifier (vector1 - we use Y component)
            var analogData = new InputAnalogActionData_t();
            var analogDataSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));

            if (_speedModifierHandle != 0)
            {
                error = OpenVR.Input.GetAnalogActionData(_speedModifierHandle, ref analogData, analogDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None)
                {
                    if (analogData.bActive)
                    {
                        // Use Y axis for up/down speed modification
                        actionEvent.SpeedModifier = analogData.y;
                    }
                    else if (!_loggedActionDebug)
                    {
                        _logger?.LogWarning("SpeedModifier action not active - check SteamVR bindings");
                        _loggedActionDebug = true;
                    }
                }
                else if (!_loggedActionDebug)
                {
                    _logger?.LogWarning("SpeedModifier GetAnalogActionData failed: {Error}", error);
                    _loggedActionDebug = true;
                }
            }

            // Get analog action data for manual movement (vector2)
            if (_manualMovementHandle != 0)
            {
                error = OpenVR.Input.GetAnalogActionData(_manualMovementHandle, ref analogData, analogDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && analogData.bActive)
                {
                    actionEvent.ManualMovementX = analogData.x;
                    actionEvent.ManualMovementY = analogData.y;
                }
            }

            // Get digital action data for buttons
            var digitalData = new InputDigitalActionData_t();
            var digitalDataSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));

            if (_toggleHudHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_toggleHudHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                actionEvent.ToggleHudPressed = error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState;
            }

            if (_toggleWalkingHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_toggleWalkingHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    // bChanged is true only on the frame the button state changes
                    actionEvent.ToggleWalkingPressed = digitalData.bChanged && digitalData.bState;
                    if (actionEvent.ToggleWalkingPressed)
                    {
                        _logger?.LogInformation("SteamVR Action: Toggle Walking pressed");
                    }
                }
            }

            if (_recenterYawHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_recenterYawHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    actionEvent.RecenterYawPressed = digitalData.bChanged && digitalData.bState;
                }
            }

            if (_overrideSpeedUpHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_overrideSpeedUpHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    actionEvent.OverrideSpeedUpPressed = digitalData.bChanged && digitalData.bState;
                }
            }

            if (_overrideSpeedDownHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_overrideSpeedDownHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    actionEvent.OverrideSpeedDownPressed = digitalData.bChanged && digitalData.bState;
                }
            }

            if (_tempSpeedUpHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_tempSpeedUpHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                    actionEvent.TempSpeedUpHeld = digitalData.bState;
            }

            if (_tempSpeedDownHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_tempSpeedDownHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                    actionEvent.TempSpeedDownHeld = digitalData.bState;
            }

            foreach (var (handle, setter) in new (ulong, Action<bool>)[] {
                (_treadmillEnableHandle,    v => actionEvent.TreadmillEnablePressed    = v),
                (_treadmillSpeedUpHandle,   v => actionEvent.TreadmillSpeedUpPressed   = v),
                (_treadmillSlowDownHandle,  v => actionEvent.TreadmillSlowDownPressed  = v),
                (_treadmillInclineUpHandle, v => actionEvent.TreadmillInclineUpPressed = v),
                (_treadmillInclineDownHandle, v => actionEvent.TreadmillInclineDownPressed = v),
            })
            {
                if (handle != 0)
                {
                    error = OpenVR.Input.GetDigitalActionData(handle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                    if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                        setter(true);
                }
            }

            if (_enableDynamicHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_enableDynamicHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                    actionEvent.EnableDynamicPressed = true;
            }

            if (_walkingSpeedUpHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_walkingSpeedUpHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                    actionEvent.WalkingSpeedUpPressed = true;
            }

            if (_walkingSpeedDownHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_walkingSpeedDownHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                    actionEvent.WalkingSpeedDownPressed = true;
            }

            if (_enableOverrideHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_enableOverrideHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                    actionEvent.EnableOverridePressed = true;
            }

            foreach (var (handle, index) in new[] {
                (_preset1Handle, 1), (_preset2Handle, 2), (_preset3Handle, 3), (_preset4Handle, 4) })
            {
                if (handle != 0)
                {
                    error = OpenVR.Input.GetDigitalActionData(handle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                    if (error == EVRInputError.None && digitalData.bActive && digitalData.bChanged && digitalData.bState)
                    {
                        switch (index)
                        {
                            case 1: actionEvent.Preset1Pressed = true; break;
                            case 2: actionEvent.Preset2Pressed = true; break;
                            case 3: actionEvent.Preset3Pressed = true; break;
                            case 4: actionEvent.Preset4Pressed = true; break;
                        }
                    }
                }
            }

            return actionEvent;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error polling SteamVR actions");
            return null;
        }
    }

    private static float NormalizeAngleDifference(float angle)
    {
        while (angle > Math.PI) angle -= 2 * (float)Math.PI;
        while (angle < -Math.PI) angle += 2 * (float)Math.PI;
        return angle;
    }

    private static (float yaw, float pitch, float roll) QuaternionToEuler(Quaternion q)
    {
        q = NormalizeQuaternion(q);

        var yaw = (float)Math.Atan2(2.0f * (q.Y * q.W + q.X * q.Z), 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y));
        var sinp = 2.0f * (q.W * q.X - q.Z * q.Y);
        var pitch = Math.Abs(sinp) >= 1 ? (float)Math.CopySign(Math.PI / 2, sinp) : (float)Math.Asin(sinp);
        var roll = (float)Math.Atan2(2.0f * (q.W * q.Z + q.X * q.Y), 1.0f - 2.0f * (q.X * q.X + q.Z * q.Z));

        return (yaw, pitch, roll);
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        var length = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    private static Quaternion GetRotationFromMatrix(HmdMatrix34_t matrix)
    {
        var w = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 + matrix.m5 + matrix.m10)) / 2;
        var x = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 - matrix.m5 - matrix.m10)) / 2;
        var y = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 + matrix.m5 - matrix.m10)) / 2;
        var z = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 - matrix.m5 + matrix.m10)) / 2;
        x = (float)Math.CopySign(x, matrix.m9 - matrix.m6);
        y = (float)Math.CopySign(y, matrix.m2 - matrix.m8);
        z = (float)Math.CopySign(z, matrix.m4 - matrix.m1);

        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Gets information about connected controllers
    /// </summary>
    public ControllerInfo GetControllerInfo()
    {
        lock (_runtimeLock)
        {
            var info = new ControllerInfo();

            if (!_connected)
                return info;

            try
            {
                // Find left and right controller indices
                for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
                {
                    var deviceClass = OpenVR.System.GetTrackedDeviceClass(i);
                    if (deviceClass != ETrackedDeviceClass.Controller)
                        continue;

                    var role = OpenVR.System.GetControllerRoleForTrackedDeviceIndex(i);
                    var controllerType = GetStringProperty(i, ETrackedDeviceProperty.Prop_ControllerType_String);
                    var modelNumber = GetStringProperty(i, ETrackedDeviceProperty.Prop_ModelNumber_String);
                    var renderModel = GetStringProperty(i, ETrackedDeviceProperty.Prop_RenderModelName_String);

                    if (role == ETrackedControllerRole.LeftHand)
                    {
                        info.LeftControllerType = controllerType;
                        info.LeftModelNumber = modelNumber;
                        info.LeftRenderModel = renderModel;
                        info.LeftConnected = true;
                    }
                    else if (role == ETrackedControllerRole.RightHand)
                    {
                        info.RightControllerType = controllerType;
                        info.RightModelNumber = modelNumber;
                        info.RightRenderModel = renderModel;
                        info.RightConnected = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get controller info");
            }

            return info;
        }
    }

    private string GetStringProperty(uint deviceIndex, ETrackedDeviceProperty prop)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        var buffer = new System.Text.StringBuilder(256);
        OpenVR.System.GetStringTrackedDeviceProperty(deviceIndex, prop, buffer, 256, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? buffer.ToString() : string.Empty;
    }

}
