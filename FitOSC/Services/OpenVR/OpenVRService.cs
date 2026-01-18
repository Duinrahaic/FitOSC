using System.Numerics;
using System.Runtime.InteropServices;
using FitOSC.Models;
using FitOSC.Services.OpenVR;
using FitOSC.Services.State;

// ReSharper disable once CheckNamespace
namespace Valve.VR;

public class OpenVRService(IServiceProvider services) : IHostedService, IDisposable
{
    public const string AppKey = "fitosc.treadmill";

    public delegate void DataUpdateReceivedEventHandler(OpenVRDataEvent e);
    public delegate void ActionEventReceivedEventHandler(OpenVRActionEvent e);

    private static (float yaw, float pitch, float roll)? _initialDirection;

    private readonly ILogger<OpenVRService>? _logger = services.GetService<ILogger<OpenVRService>>();
    private readonly AppStateService? _appStateService = services.GetService<AppStateService>();
    private CancellationTokenSource? _cancellationTokenSource;
    private EVRInitError _initError;
    private bool _isRunning;
    private bool _isReconnecting = false;
    private int _reconnectionAttempts = 0;
    private bool _actionsInitialized = false;

    // Polling rate constants (in milliseconds)
    private const int ActivePollingRateMs = 100;  // 10Hz when walking mode is active
    private const int IdlePollingRateMs = 100;    // 10Hz when walking mode is disabled

    // SteamVR Action Handles
    private ulong _actionSetHandle;
    private ulong _speedModifierHandle;
    private ulong _manualMovementHandle;
    private ulong _toggleWalkingHandle;
    private ulong _recenterYawHandle;
    private ulong _overrideSpeedUpHandle;
    private ulong _overrideSpeedDownHandle;
    private ulong _trimUpHandle;
    private ulong _trimDownHandle;

    public bool IsMonitoring { get; private set; }
    public bool ActionsAvailable => _actionsInitialized;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Check if VR is disabled via launch argument
        if (FitOSC.App.DisableVR)
        {
            _logger?.LogInformation("OpenVR service disabled via --no-vr flag");
            _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Disconnected);
            return;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logger?.LogInformation("OpenVR service starting...");
        _isRunning = true;

        // Auto-start VR monitoring
        StartMonitoring();

        try
        {
            Task.Run(() => PollVrEvents(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("VR event polling was canceled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"An error occurred in VR event polling: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _isRunning = false;

        // Signal the cancellation of the VR event polling task
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
        OpenVR.Shutdown();
        // Shutdown OpenVR when the service stops
        return Task.CompletedTask;
    }


    ~OpenVRService()
    {
        Dispose(false);
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
            var appManifestPath = Path.Combine(exePath, "SteamVR", "fitosc.vrmanifest");

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

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TrimUp", ref _trimUpHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TrimUp action handle: {Error}", error);

            error = OpenVR.Input.GetActionHandle("/actions/fitosc/in/TrimDown", ref _trimDownHandle);
            if (error != EVRInputError.None)
                _logger?.LogWarning("Failed to get TrimDown action handle: {Error}", error);

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


    private bool TryInitialize()
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
            if (string.IsNullOrEmpty(errorMessage))
                return false;

            _logger?.LogError(errorMessage);
            return false;
        }

        _logger?.LogInformation("OpenVR initialized successfully. VR headset detected.");

        // Initialize SteamVR actions
        _actionsInitialized = InitializeActions();
        if (!_actionsInitialized)
        {
            _logger?.LogWarning("SteamVR actions not available. Controller bindings will not work.");
        }

        return true;
    }
    
    public void StartMonitoring()
    {
        bool success = TryInitialize();
        IsMonitoring = success;

        // Publish connection status
        if (success)
        {
            _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Connected);
            _isReconnecting = false; // Stop any reconnection attempts
        }
        else
        {
            _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Error);
            // Start auto-reconnect loop
            _ = StartReconnectLoop();
        }
    }

    public void StopMonitoring()
    {
        IsMonitoring = false;
        _isReconnecting = false; // Stop reconnection attempts

        // Publish disconnected status
        _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Disconnected);
    }

    public bool GetAutoLaunch()
    {
        try
        {
            if (OpenVR.Applications == null)
                return false;

            return OpenVR.Applications.GetApplicationAutoLaunch(AppKey);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get auto-launch status");
            return false;
        }
    }

    public bool SetAutoLaunch(bool enabled)
    {
        try
        {
            if (OpenVR.Applications == null)
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

    private async Task StartReconnectLoop()
    {
        if (_isReconnecting) return;
        _isReconnecting = true;
        _reconnectionAttempts = 0;

        try
        {
            while (!IsMonitoring && _isReconnecting && _isRunning && _cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                // Only log every 6 attempts (every minute) to reduce spam
                if (_reconnectionAttempts % 6 == 0)
                {
                    _logger?.LogInformation("Attempting to reconnect to SteamVR...");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token).ConfigureAwait(false);

                if (!_cancellationTokenSource.Token.IsCancellationRequested && _isReconnecting)
                {
                    _reconnectionAttempts++;
                    _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Connecting);

                    bool success = TryInitialize();
                    IsMonitoring = success;

                    if (success)
                    {
                        _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Connected);
                        _isReconnecting = false;
                        _reconnectionAttempts = 0;
                        _logger?.LogInformation("Successfully connected to SteamVR");
                    }
                    else
                    {
                        _appStateService?.PublishInterfaceConnectionStatuses(AppInterface.VR, ConnectionStatus.Error);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Silently handle cancellation - no need to log
        }
        catch (Exception ex)
        {
            // Only log unexpected errors
            _logger?.LogError(ex, "Unexpected error in VR reconnection loop");
        }
        finally
        {
            _isReconnecting = false;
            _reconnectionAttempts = 0;
        }
    }

    private async Task PollVrEvents(CancellationToken cancellationToken)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var vrSystem = OpenVR.System;
        HmdMatrix34_t poseMatrix;
        Quaternion quaternion;
        (float yaw, float pitch, float roll) euler;
        (float X, float Y, float Z) position = (0, 0, 0);

        // Action set for updating
        var actionSet = new VRActiveActionSet_t
        {
            ulActionSet = _actionSetHandle,
            ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
            nPriority = 0
        };
        var actionSetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));

        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

            // Poll SteamVR actions if initialized
            OpenVRActionEvent? actionEvent = null;
            if (_actionsInitialized)
            {
                actionEvent = PollActions(ref actionSet, actionSetSize);
            }

            if (vrSystem.IsTrackedDeviceConnected(OpenVR.k_unTrackedDeviceIndex_Hmd) &&
                poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid)
            {
                if (!IsMonitoring)
                {
                    _initialDirection = null;
                }
                else
                {
                    poseMatrix = poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking;
                    position = (poseMatrix.m3, poseMatrix.m7, poseMatrix.m11);
                    quaternion = GetRotationFromMatrix(poseMatrix);
                    euler = QuaternionToEuler(quaternion);

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
                    float rightThumbstickY = actionEvent?.SpeedModifier ?? 0f;
                    float leftThumbstickX = actionEvent?.ManualMovementX ?? 0f;
                    float leftThumbstickY = actionEvent?.ManualMovementY ?? 0f;

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
                    if (actionEvent != null)
                    {
                        OnActionReceived?.Invoke(actionEvent);
                    }
                }
            }

            // Adaptive polling rate: faster when walking mode is active, slower when idle
            var walkingMode = _appStateService?.CurrentWalkingMode ?? WalkingMode.Disabled;
            var pollingRate = walkingMode != WalkingMode.Disabled ? ActivePollingRateMs : IdlePollingRateMs;
            await Task.Delay(pollingRate, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool _loggedActionDebug = false;
    private static bool _loggedActionSuccess = false;

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

            if (_trimUpHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_trimUpHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    actionEvent.TrimUpPressed = digitalData.bChanged && digitalData.bState;
                    if (actionEvent.TrimUpPressed)
                    {
                        _logger?.LogInformation("SteamVR Action: Trim Up pressed");
                    }
                }
            }

            if (_trimDownHandle != 0)
            {
                error = OpenVR.Input.GetDigitalActionData(_trimDownHandle, ref digitalData, digitalDataSize, OpenVR.k_ulInvalidInputValueHandle);
                if (error == EVRInputError.None && digitalData.bActive)
                {
                    actionEvent.TrimDownPressed = digitalData.bChanged && digitalData.bState;
                    if (actionEvent.TrimDownPressed)
                    {
                        _logger?.LogInformation("SteamVR Action: Trim Down pressed");
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
        var info = new ControllerInfo();

        if (!IsMonitoring || OpenVR.System == null)
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

    private string GetStringProperty(uint deviceIndex, ETrackedDeviceProperty prop)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        var buffer = new System.Text.StringBuilder(256);
        OpenVR.System.GetStringTrackedDeviceProperty(deviceIndex, prop, buffer, 256, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? buffer.ToString() : string.Empty;
    }

    private void ReleaseUnmanagedResources()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
        OpenVR.Shutdown();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing) _cancellationTokenSource?.Dispose();
    }
}