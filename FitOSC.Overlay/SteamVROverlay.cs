using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.OpenVR;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Valve.VR;

namespace FitOSC.Overlay;

public sealed class SteamVROverlay : IOpenVROverlay, IDisposable
{
    private sealed record Settings(bool HudEnabled, bool WristEnabled, WristHand Hand);
    private readonly AppStateService _state;
    private readonly ILogger<SteamVROverlay> _logger;
    private readonly SKTypeface _typeface;
    private readonly SKSurface _hudSurface;
    private readonly SKSurface _wristSurface;
    private volatile Settings _settings;
    private volatile AppStateInfo _snapshot;
    private ulong _hud, _wrist;
    private uint _attachedDevice = OpenVR.k_unTrackedDeviceIndexInvalid;
    private WristHand? _attachedHand;
    private bool _hudVisible, _wristVisible, _hudHidden, _subscribed, _imageWarning;
    private WristButton _hover, _pressed;
    private HudView? _lastHud;
    private WristView? _lastWrist;
    private long _hudUploadTicks, _wristUploadTicks;
    private int _hudUploads, _wristUploads;
    private int _disposed;

    public SteamVROverlay(AppStateService state, ConfigurationService configuration, ILogger<SteamVROverlay> logger)
    {
        _state = state;
        _logger = logger;
        _snapshot = state.GetCurrentAppStateInfo();
        _settings = CopySettings(configuration.GetConfiguration().Overlay);
        _typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", "VarelaRound-Regular.ttf"), 0)
            ?? throw new InvalidOperationException("The bundled Varela Round font could not be loaded.");
        try
        {
            _hudSurface = SKSurface.Create(new SKImageInfo(512, 128, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not create the HUD raster surface.");
            try
            {
                _wristSurface = SKSurface.Create(new SKImageInfo(512, 256, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException("Could not create the wrist raster surface.");
            }
            catch { _hudSurface.Dispose(); throw; }
        }
        catch { _typeface.Dispose(); throw; }
    }

    private static Settings CopySettings(OverlaySettings settings)
    {
        if (settings.WristHand is not WristHand.Left and not WristHand.Right)
            throw new ArgumentOutOfRangeException(nameof(settings.WristHand), settings.WristHand, "Unknown wrist hand.");
        return new(settings.HudEnabled, settings.WristEnabled, settings.WristHand);
    }

    public void ApplySettings(OverlaySettings settings) => _settings = CopySettings(settings);
    private void UpdateSnapshot(AppStateInfo snapshot) => _snapshot = snapshot;
    private static void Check(EVROverlayError error)
    {
        if (error != EVROverlayError.None) throw new InvalidOperationException($"SteamVR overlay error: {error}");
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            _state.AppStateUpdated += UpdateSnapshot;
            _subscribed = true;
            _snapshot = _state.GetCurrentAppStateInfo();
            Check(OpenVR.Overlay.CreateOverlay("fitosc.treadmill.hud", "FitOSC HUD", ref _hud));
            Check(OpenVR.Overlay.SetOverlayWidthInMeters(_hud, 0.34f));
            Check(OpenVR.Overlay.SetOverlayAlpha(_hud, 0.85f));
            Check(OpenVR.Overlay.SetOverlayInputMethod(_hud, VROverlayInputMethod.None));
            var transform = new HmdMatrix34_t
            {
                m0 = 1,
                m5 = 0.9744f,
                m6 = 0.2250f,
                m7 = -0.20f,
                m9 = -0.2250f,
                m10 = 0.9744f,
                m11 = -0.85f
            };
            Check(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_hud, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform));
            Check(OpenVR.Overlay.HideOverlay(_hud));
            Check(OpenVR.Overlay.CreateOverlay("fitosc.treadmill.wrist", "FitOSC Wrist", ref _wrist));
            Check(OpenVR.Overlay.SetOverlayWidthInMeters(_wrist, 0.16f));
            Check(OpenVR.Overlay.SetOverlayAlpha(_wrist, 0.95f));
            Check(OpenVR.Overlay.SetOverlayInputMethod(_wrist, VROverlayInputMethod.Mouse));
            var scale = new HmdVector2_t { v0 = 512, v1 = 256 };
            Check(OpenVR.Overlay.SetOverlayMouseScale(_wrist, ref scale));
            Check(OpenVR.Overlay.SetOverlayFlag(_wrist, VROverlayFlags.MakeOverlaysInteractiveIfVisible, true));
            Check(OpenVR.Overlay.HideOverlay(_wrist));
            _hudHidden = false;
            _imageWarning = false;
            _hudUploads = _wristUploads = 0;
            _hudUploadTicks = _wristUploadTicks = 0;
        }
        catch
        {
            // Release partial handles and the subscription before Core disables this connection.
            Disconnect();
            throw;
        }
    }

    public bool Update(TrackedDevicePose_t[] poses, OpenVRActionEvent input)
    {
        var settings = _settings;
        var snapshot = _snapshot;
        if (input.ToggleHudPressed) _hudHidden = !_hudHidden;
        SetVisible(_hud, settings.HudEnabled && !_hudHidden, ref _hudVisible);
        UpdateWristVisibility(poses, settings);
        var view = BuildWristView(snapshot);
        PollEvents(_hud, view, input);
        var clicked = PollEvents(_wrist, view, input);
        var hudView = BuildHudView(snapshot);
        if (_hudVisible && hudView != _lastHud)
        {
            HudPanel.Draw(_hudSurface.Canvas, hudView, _typeface);
            Upload(_hud, _hudSurface, 128, ref _hudUploadTicks, ref _hudUploads, "HUD");
            _lastHud = hudView;
        }
        view = view with { Hover = _hover, Pressed = _pressed };
        if (_wristVisible && view != _lastWrist)
        {
            WristPanel.Draw(_wristSurface.Canvas, view, _typeface);
            Upload(_wrist, _wristSurface, 256, ref _wristUploadTicks, ref _wristUploads, "wrist");
            _lastWrist = view;
        }
        return clicked;
    }

    private void SetVisible(ulong handle, bool visible, ref bool current)
    {
        if (current == visible) return;
        Check(visible ? OpenVR.Overlay.ShowOverlay(handle) : OpenVR.Overlay.HideOverlay(handle));
        current = visible;
        if (handle == _hud) _lastHud = null;
        else
        {
            _lastWrist = null;
            if (!visible) _hover = _pressed = WristButton.None;
        }
    }

    private void UpdateWristVisibility(TrackedDevicePose_t[] poses, Settings settings)
    {
        var device = OpenVR.System.GetTrackedDeviceIndexForControllerRole(settings.Hand == WristHand.Left
            ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
        var hmd = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!settings.WristEnabled || device == OpenVR.k_unTrackedDeviceIndexInvalid || device >= poses.Length ||
            !poses[device].bPoseIsValid || !poses[device].bDeviceIsConnected || !hmd.bPoseIsValid || !hmd.bDeviceIsConnected)
        {
            SetVisible(_wrist, false, ref _wristVisible);
            _hover = _pressed = WristButton.None;
            return;
        }
        if (device != _attachedDevice || settings.Hand != _attachedHand)
        {
            var transform = new HmdMatrix34_t
            {
                m1 = settings.Hand == WristHand.Left ? -1 : 1,
                m6 = 1,
                m7 = 0.03f,
                m8 = settings.Hand == WristHand.Left ? -1 : 1,
                m11 = 0.12f
            };
            Check(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_wrist, device, ref transform));
            _attachedDevice = device;
            _attachedHand = settings.Hand;
            _hover = _pressed = WristButton.None;
        }
        var pose = poses[device].mDeviceToAbsoluteTracking;
        var centre = new Vector3(pose.m1 * 0.03f + pose.m2 * 0.12f + pose.m3,
            pose.m5 * 0.03f + pose.m6 * 0.12f + pose.m7,
            pose.m9 * 0.03f + pose.m10 * 0.12f + pose.m11);
        var normal = new Vector3(pose.m1, pose.m5, pose.m9);
        var head = hmd.mDeviceToAbsoluteTracking;
        var direction = new Vector3(head.m3, head.m7, head.m11) - centre;
        var distance = direction.Length();
        var cosine = Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(direction));
        var visible = _wristVisible
            ? distance <= 0.9f && cosine >= MathF.Cos(50 * MathF.PI / 180)
            : distance < 0.8f && cosine > MathF.Cos(35 * MathF.PI / 180);
        SetVisible(_wrist, visible, ref _wristVisible);
    }

    private bool PollEvents(ulong handle, WristView view, OpenVRActionEvent input)
    {
        bool clicked = false;
        var ev = new VREvent_t();
        while (OpenVR.Overlay.PollNextOverlayEvent(handle, ref ev, (uint)Marshal.SizeOf<VREvent_t>()))
        {
            var type = (EVREventType)ev.eventType;
            if (type == EVREventType.VREvent_ImageFailed && !_imageWarning)
            {
                _logger.LogWarning("SteamVR failed to display an overlay image.");
                _imageWarning = true;
            }
            if (handle != _wrist) continue;
            if (type == EVREventType.VREvent_FocusLeave)
            {
                _hover = _pressed = WristButton.None;
                continue;
            }
            if (!_wristVisible) continue;
            var mouse = ev.data.mouse;
            var button = WristPanel.HitTest(mouse.x, 256 - mouse.y);
            if (type == EVREventType.VREvent_MouseMove) _hover = button;
            if (mouse.button != (uint)EVRMouseButton.Left) continue;
            if (type == EVREventType.VREvent_MouseButtonDown)
                _pressed = Enabled(button, view) ? button : WristButton.None;
            if (type == EVREventType.VREvent_MouseButtonUp)
            {
                if (button != WristButton.None && button == _pressed && Enabled(button, view))
                    clicked |= Command(button, view.Mode, input);
                _pressed = WristButton.None;
            }
        }
        return clicked;
    }

    private static bool Enabled(WristButton button, WristView view) => button switch
    {
        WristButton.None => false,
        WristButton.Decrease => view.CanDecrease,
        WristButton.Increase => view.CanIncrease,
        _ => true
    };

    private static bool Command(WristButton button, WalkingMode mode, OpenVRActionEvent input)
    {
        switch (button)
        {
            case WristButton.Walk: input.ToggleWalkingPressed = true; break;
            case WristButton.Recenter: input.RecenterYawPressed = true; break;
            case WristButton.Dynamic: input.EnableDynamicPressed = true; break;
            case WristButton.Override: input.EnableOverridePressed = true; break;
            case WristButton.Decrease when mode == WalkingMode.Dynamic: input.WalkingSpeedDownPressed = true; break;
            case WristButton.Increase when mode == WalkingMode.Dynamic: input.WalkingSpeedUpPressed = true; break;
            case WristButton.Decrease when mode == WalkingMode.Override: input.OverrideSpeedDownPressed = true; break;
            case WristButton.Increase when mode == WalkingMode.Override: input.OverrideSpeedUpPressed = true; break;
            default: return false;
        }
        return true;
    }

    private static string ModeText(WalkingData walking) => walking.Mode switch
    {
        WalkingMode.Disabled => "WALK OFF",
        WalkingMode.Dynamic => walking.IsManualOverride ? "MANUAL" : "DYNAMIC",
        WalkingMode.Override => walking.IsManualOverride ? "MANUAL" : "OVERRIDE",
        _ => throw new ArgumentOutOfRangeException(nameof(walking.Mode))
    };
    private static string Percent(float value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string HeartRate(AppStateInfo snapshot) => snapshot.CurrentHeartRate == 0 ? "HR --" : $"HR {snapshot.CurrentHeartRate}";

    public static HudView BuildHudView(AppStateInfo snapshot)
    {
        var speed = snapshot.TreadmillTelemetry.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed);
        var metric = snapshot.UserMeasurementType switch
        {
            MeasurementType.Metric => true,
            MeasurementType.Imperial => false,
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot.UserMeasurementType))
        };
        var name = snapshot.DeviceName;
        var treadmill = string.IsNullOrEmpty(name) ? "No treadmill" : $"{snapshot.TreadmillState} {name[..Math.Min(16, name.Length)]}";
        return new(snapshot.HasMetrics && speed is { Enabled: true }
                ? (metric ? speed.AsMetric() : speed.AsImperial()).Value.ToString("0.0", CultureInfo.InvariantCulture) : "--",
            metric ? "KM/H" : "MPH", treadmill, ModeText(snapshot.Walking),
            snapshot.Walking.Mode == WalkingMode.Disabled ? "" : Percent(snapshot.Walking.Velocity),
            HeartRate(snapshot), snapshot.ConnectionStates[AppInterface.Bluetooth], snapshot.ConnectionStates[AppInterface.OSC]);
    }

    public static WristView BuildWristView(AppStateInfo snapshot)
    {
        var walking = snapshot.Walking;
        var config = walking.Config;
        var text = walking.Mode switch
        {
            WalkingMode.Disabled => "WALK OFF",
            WalkingMode.Dynamic => "TRIM x" + config.WalkingTrim.ToString("0.00", CultureInfo.InvariantCulture),
            WalkingMode.Override => $"PRESET {config.CurrentOverrideIndex}  {Percent(config.OverrideSpeeds[config.CurrentOverrideIndex])}",
            _ => throw new ArgumentOutOfRangeException(nameof(walking.Mode))
        };
        return new(walking.Mode, walking.IsManualOverride, Percent(walking.Velocity), HeartRate(snapshot), text,
            walking.Mode == WalkingMode.Dynamic && config.WalkingTrim > 0 || walking.Mode == WalkingMode.Override && config.CurrentOverrideIndex > 0,
            walking.Mode == WalkingMode.Dynamic && config.WalkingTrim < 2 || walking.Mode == WalkingMode.Override && config.CurrentOverrideIndex < config.OverrideSpeeds.Count - 1,
            WristButton.None, WristButton.None);
    }

    private void Upload(ulong handle, SKSurface surface, uint height, ref long ticks, ref int count, string name)
    {
        using var pixels = surface.PeekPixels();
        var start = Stopwatch.GetTimestamp();
        Check(OpenVR.Overlay.SetOverlayRaw(handle, pixels.GetPixels(), 512, height, 4));
        ticks += Stopwatch.GetTimestamp() - start;
        if (++count == 100)
        {
            _logger.LogDebug("{Overlay} SetOverlayRaw average over 100 uploads: {Milliseconds:F3} ms", name,
                ticks * 1000.0 / Stopwatch.Frequency / count);
            ticks = 0;
            count = 0;
        }
    }

    public void Disconnect()
    {
        if (_subscribed)
        {
            _state.AppStateUpdated -= UpdateSnapshot;
            _subscribed = false;
        }
        // Attempt both destroys even if one fails; clear handles before returning.
        Exception? error = null;
        foreach (var handle in new[] { _hud, _wrist })
        {
            if (handle == 0) continue;
            try { Check(OpenVR.Overlay.DestroyOverlay(handle)); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to destroy overlay {Handle}", handle); error ??= ex; }
        }
        _hud = _wrist = 0;
        _attachedDevice = OpenVR.k_unTrackedDeviceIndexInvalid;
        _attachedHand = null;
        _hudVisible = _wristVisible = false;
        _hover = _pressed = WristButton.None;
        _lastHud = null;
        _lastWrist = null;
        if (error != null) throw error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Core already disconnected before Shutdown; disposal never calls OpenVR.
        if (_subscribed) _state.AppStateUpdated -= UpdateSnapshot;
        _subscribed = false;
        _wristSurface.Dispose();
        _hudSurface.Dispose();
        _typeface.Dispose();
    }
}
