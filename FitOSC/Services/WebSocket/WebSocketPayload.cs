using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using FitOSC.Utilities;

namespace FitOSC.Services.WebSocket;

/// <summary>
/// Root WebSocket payload broadcast to all connected clients. All telemetry values
/// are converted to the user's configured unit system before serialization.
/// </summary>
public class WebSocketPayload
{
    /// <summary>Bluetooth display name of the connected treadmill, or <c>null</c> if no device is connected.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Current treadmill state. Possible values: <c>Unknown</c>, <c>Stopped</c>, <c>Running</c>, <c>Paused</c>.</summary>
    public string TreadmillState { get; set; } = "Unknown";

    /// <summary>The unit system used for all values in this payload. Either <c>metric</c> or <c>imperial</c>.</summary>
    public string UnitSystem { get; set; } = "metric";

    /// <summary>Connection status for each interface (Bluetooth, VR, Pulsoid, OSC).</summary>
    public WebSocketConnections Connections { get; set; } = new();

    /// <summary>
    /// Device capability limits (min/max speed, incline range, step sizes).
    /// Speed values are expressed in <see cref="WebSocketConfiguration.SpeedUnit"/>.
    /// <c>null</c> when no device is connected.
    /// </summary>
    public WebSocketConfiguration? Configuration { get; set; }

    /// <summary>Live telemetry metrics in the user's preferred unit system.</summary>
    public WebSocketMetrics Metrics { get; set; } = new();

    /// <summary>VRChat locomotion state, active mode, and mode configuration.</summary>
    public WebSocketWalking Walking { get; set; } = new();

    public static WebSocketPayload From(AppStateInfo state)
    {
        var isMetric = state.UserMeasurementType == MeasurementType.Metric;
        var telemetry = state.TreadmillTelemetry;
        var cfg = state.TreadmillConfiguration;

        return new WebSocketPayload
        {
            DeviceName = state.DeviceName,
            TreadmillState = state.TreadmillState.ToString(),
            UnitSystem = isMetric ? "metric" : "imperial",
            Connections = new WebSocketConnections
            {
                Bluetooth = GetConnectionStatus(state, AppInterface.Bluetooth),
                Vr = GetConnectionStatus(state, AppInterface.VR),
                Pulsoid = GetConnectionStatus(state, AppInterface.Pulsoid),
                Osc = GetConnectionStatus(state, AppInterface.OSC),
            },
            Configuration = new WebSocketConfiguration
            {
                MaxSpeed = isMetric ? cfg.MaxSpeed : cfg.MaxSpeed.ConvertKphToMph(),
                MinSpeed = isMetric ? cfg.MinSpeed : cfg.MinSpeed.ConvertKphToMph(),
                SpeedIncrement = isMetric ? cfg.SpeedIncrement : cfg.SpeedIncrement.ConvertKphToMph(),
                MaxIncline = cfg.MaxIncline,
                MinIncline = cfg.MinIncline,
                InclineIncrement = cfg.InclineIncrement,
                SpeedUnit = isMetric ? "KPH" : "MPH",
            },
            Metrics = new WebSocketMetrics
            {
                Speed = BuildMetric(telemetry, TreadmillTelemetryProperty.InstantaneousSpeed, isMetric),
                AvgSpeed = BuildMetric(telemetry, TreadmillTelemetryProperty.AverageSpeed, isMetric),
                Distance = BuildMetric(telemetry, TreadmillTelemetryProperty.TotalDistance, isMetric),
                Incline = BuildMetric(telemetry, TreadmillTelemetryProperty.Incline, isMetric),
                RampAngle = BuildMetric(telemetry, TreadmillTelemetryProperty.RampAngle, isMetric),
                ElevationGain = BuildMetric(telemetry, TreadmillTelemetryProperty.ElevationGain, isMetric),
                Pace = BuildMetric(telemetry, TreadmillTelemetryProperty.InstantaneousPace, isMetric),
                AvgPace = BuildMetric(telemetry, TreadmillTelemetryProperty.AveragePace, isMetric),
                Calories = BuildMetric(telemetry, TreadmillTelemetryProperty.Calories, isMetric),
                HeartRate = BuildMetric(telemetry, TreadmillTelemetryProperty.HeartRate, isMetric),
                Met = BuildMetric(telemetry, TreadmillTelemetryProperty.MetabolicEquivalent, isMetric),
                ElapsedTime = BuildMetric(telemetry, TreadmillTelemetryProperty.ElapsedTime, isMetric),
                RemainingTime = BuildMetric(telemetry, TreadmillTelemetryProperty.RemainingTime, isMetric),
                ForceOnBelt = BuildMetric(telemetry, TreadmillTelemetryProperty.ForceOnBelt, isMetric),
                Power = BuildMetric(telemetry, TreadmillTelemetryProperty.Power, isMetric),
                StepCount = BuildMetric(telemetry, TreadmillTelemetryProperty.StepCount, isMetric),
            },
            Walking = new WebSocketWalking
            {
                Mode = state.Walking.Mode.ToString(),
                Velocity = state.Walking.Velocity,
                Horizontal = state.Walking.Horizontal,
                Vertical = state.Walking.Vertical,
                IsManualOverride = state.Walking.IsManualOverride,
                Config = new WebSocketWalkingConfig
                {
                    MaxSpeed = state.Walking.Config.MaxSpeed,
                    WalkingTrim = state.Walking.Config.WalkingTrim,
                    SmoothingFactor = state.Walking.Config.SmoothingFactor,
                    MaxTurnAngle = state.Walking.Config.MaxTurnAngle,
                    UpdateIntervalMs = state.Walking.Config.UpdateIntervalMs,
                    ThumbstickRampSpeed = state.Walking.Config.ThumbstickRampSpeed,
                    OverrideSpeeds = state.Walking.Config.OverrideSpeeds,
                    CurrentOverrideIndex = state.Walking.Config.CurrentOverrideIndex,
                },
            }
        };
    }

    private static string GetConnectionStatus(AppStateInfo state, AppInterface iface) =>
        state.ConnectionStates.TryGetValue(iface, out var status) ? status.ToString() : "Disconnected";

    private static WebSocketMetricValue? BuildMetric(TreadmillTelemetry telemetry, TreadmillTelemetryProperty property, bool isMetric)
    {
        var raw = telemetry.GetTelemetryValue(property);
        if (raw == null) return null;

        var converted = isMetric ? raw.AsMetric() : raw.AsImperial();
        var unit = isMetric
            ? converted.MetricUnit
            : (!string.IsNullOrEmpty(converted.ImperialUnit) ? converted.ImperialUnit : converted.MetricUnit);

        return new WebSocketMetricValue
        {
            Value = converted.Value,
            Unit = unit,
            Enabled = raw.Enabled,
        };
    }
}

/// <summary>
/// Connection status for each external interface. Each value is a string representation
/// of <c>ConnectionStatus</c>: <c>Connected</c>, <c>Disconnected</c>, or <c>Connecting</c>.
/// </summary>
public class WebSocketConnections
{
    /// <summary>Bluetooth LE connection to the treadmill.</summary>
    public string Bluetooth { get; set; } = "Disconnected";

    /// <summary>OpenVR / SteamVR runtime connection.</summary>
    public string Vr { get; set; } = "Disconnected";

    /// <summary>Pulsoid heart rate monitor connection.</summary>
    public string Pulsoid { get; set; } = "Disconnected";

    /// <summary>VRChat OSC connection.</summary>
    public string Osc { get; set; } = "Disconnected";
}

/// <summary>
/// Treadmill device capability limits. Speed values are expressed in
/// <see cref="SpeedUnit"/>; incline values are always in percent (%).
/// </summary>
public class WebSocketConfiguration
{
    /// <summary>Maximum speed supported by the device.</summary>
    public decimal MaxSpeed { get; set; }

    /// <summary>Minimum speed supported by the device.</summary>
    public decimal MinSpeed { get; set; }

    /// <summary>Speed adjustment step size.</summary>
    public decimal SpeedIncrement { get; set; }

    /// <summary>Maximum incline supported by the device (%).</summary>
    public decimal MaxIncline { get; set; }

    /// <summary>Minimum incline supported by the device (%).</summary>
    public decimal MinIncline { get; set; }

    /// <summary>Incline adjustment step size (%).</summary>
    public decimal InclineIncrement { get; set; }

    /// <summary>Unit label for all speed fields in this object. Either <c>KPH</c> or <c>MPH</c>.</summary>
    public string SpeedUnit { get; set; } = "KPH";
}

/// <summary>
/// A single telemetry reading in the user's preferred unit system.
/// <c>null</c> metrics indicate the connected device does not report that measurement.
/// </summary>
public class WebSocketMetricValue
{
    /// <summary>Current reading expressed in <see cref="Unit"/>.</summary>
    public decimal Value { get; set; }

    /// <summary>Unit label for <see cref="Value"/> (e.g. <c>MPH</c>, <c>BPM</c>, <c>KCAL</c>).</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary><c>true</c> if the device is actively reporting this metric.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// VRChat locomotion state. Reflects what is currently being sent to VRChat via OSC.
/// </summary>
public class WebSocketWalking
{
    /// <summary>
    /// Active locomotion mode. Possible values:
    /// <list type="bullet">
    ///   <item><term>Disabled</term><description>No locomotion output is sent to VRChat.</description></item>
    ///   <item><term>Dynamic</term><description>Speed is proportionally mapped from treadmill speed. Head yaw decomposes movement into horizontal/vertical axes.</description></item>
    ///   <item><term>Override</term><description>Speed is fixed to a preset from <see cref="Config"/>.<see cref="WebSocketWalkingConfig.OverrideSpeeds"/>, ignoring treadmill speed.</description></item>
    /// </list>
    /// </summary>
    public string Mode { get; set; } = "Disabled";

    /// <summary>Combined locomotion magnitude sent to VRChat (0.0–1.0).</summary>
    public float Velocity { get; set; }

    /// <summary>Left/right strafe axis sent to VRChat (-1.0 = full left, 1.0 = full right).</summary>
    public float Horizontal { get; set; }

    /// <summary>Forward/backward axis sent to VRChat (0.0 = stopped, 1.0 = full forward).</summary>
    public float Vertical { get; set; }

    /// <summary><c>true</c> when the left thumbstick is active and treadmill locomotion is paused.</summary>
    public bool IsManualOverride { get; set; }

    /// <summary>Active configuration for the current walking mode.</summary>
    public WebSocketWalkingConfig Config { get; set; } = new();
}

/// <summary>
/// Configuration parameters that govern VRChat locomotion output.
/// </summary>
public class WebSocketWalkingConfig
{
    /// <summary>
    /// Reference top speed in km/h used to normalize treadmill speed to a 0.0–1.0 range in Dynamic mode.
    /// Formula: <c>normalized = treadmillSpeed / MaxSpeed</c>.
    /// </summary>
    public decimal MaxSpeed { get; set; }

    /// <summary>
    /// Multiplier applied to normalized speed (0.0–2.0). Values above 1.0 allow reaching full
    /// VRChat speed at lower treadmill speeds. Output is always clamped to 0.0–1.0.
    /// Formula: <c>velocity = clamp(normalized * WalkingTrim, 0.0, 1.0)</c>.
    /// </summary>
    public float WalkingTrim { get; set; }

    /// <summary>
    /// Input smoothing factor (0.0–1.0). Lower values produce smoother but slower transitions;
    /// higher values are more responsive but may feel jittery.
    /// </summary>
    public float SmoothingFactor { get; set; }

    /// <summary>
    /// Half the total head-yaw range used for direction decomposition (degrees).
    /// Default 90 = ±90° (180° total). Head yaw is clamped to this range before
    /// being decomposed into <c>horizontal</c> and <c>vertical</c> components.
    /// </summary>
    public float MaxTurnAngle { get; set; }

    /// <summary>How often locomotion updates are sent via OSC (milliseconds). Lower = more responsive; higher = less network traffic.</summary>
    public int UpdateIntervalMs { get; set; }

    /// <summary>
    /// How fast the thumbstick speed modifier ramps up or down (0.01–0.5).
    /// Lower = gradual ramp; higher = snappy response.
    /// </summary>
    public float ThumbstickRampSpeed { get; set; }

    /// <summary>
    /// Preset speed values available in Override mode (0.0–1.0).
    /// Default ladder: 0.0 (stopped), 0.25 (slow walk), 0.5 (normal walk), 0.75 (fast walk), 1.0 (run).
    /// </summary>
    public List<float> OverrideSpeeds { get; set; } = new();

    /// <summary>Index into <see cref="OverrideSpeeds"/> that is currently active in Override mode.</summary>
    public int CurrentOverrideIndex { get; set; }
}

/// <summary>
/// Live telemetry metrics from the connected treadmill in the user's preferred unit system.
/// Any property that is <c>null</c> indicates the connected device does not report that measurement.
/// </summary>
public class WebSocketMetrics
{
    /// <summary>Instantaneous speed. Unit: <c>KPH</c> or <c>MPH</c>.</summary>
    public WebSocketMetricValue? Speed { get; set; }

    /// <summary>Session average speed. Unit: <c>KPH</c> or <c>MPH</c>.</summary>
    public WebSocketMetricValue? AvgSpeed { get; set; }

    /// <summary>Total distance traveled this session. Unit: <c>KILOMETERS</c> or <c>MILES</c>.</summary>
    public WebSocketMetricValue? Distance { get; set; }

    /// <summary>Current incline grade. Unit: <c>%</c>.</summary>
    public WebSocketMetricValue? Incline { get; set; }

    /// <summary>Ramp angle. Unit: <c>%</c>.</summary>
    public WebSocketMetricValue? RampAngle { get; set; }

    /// <summary>Cumulative elevation gained this session. Unit: <c>METERS</c> or <c>FEET</c>.</summary>
    public WebSocketMetricValue? ElevationGain { get; set; }

    /// <summary>Instantaneous pace in seconds per unit distance. Unit: <c>PACE</c>.</summary>
    public WebSocketMetricValue? Pace { get; set; }

    /// <summary>Session average pace in seconds per unit distance. Unit: <c>PACE</c>.</summary>
    public WebSocketMetricValue? AvgPace { get; set; }

    /// <summary>Calories burned this session. Unit: <c>KCAL</c>.</summary>
    public WebSocketMetricValue? Calories { get; set; }

    /// <summary>Heart rate. Sourced from treadmill or Pulsoid when connected. Unit: <c>BPM</c>.</summary>
    public WebSocketMetricValue? HeartRate { get; set; }

    /// <summary>Metabolic equivalent of task. Unit: <c>MET</c>.</summary>
    public WebSocketMetricValue? Met { get; set; }

    /// <summary>Elapsed session time. Unit: <c>SECS</c>.</summary>
    public WebSocketMetricValue? ElapsedTime { get; set; }

    /// <summary>Remaining session time, if reported by the device. Unit: <c>SECS</c>.</summary>
    public WebSocketMetricValue? RemainingTime { get; set; }

    /// <summary>Force on the treadmill belt. Unit: <c>NEWTONS</c>.</summary>
    public WebSocketMetricValue? ForceOnBelt { get; set; }

    /// <summary>Power output. Unit: <c>WATTS</c>.</summary>
    public WebSocketMetricValue? Power { get; set; }

    /// <summary>Total step count this session. Unit: <c>STEPS</c>.</summary>
    public WebSocketMetricValue? StepCount { get; set; }
}
