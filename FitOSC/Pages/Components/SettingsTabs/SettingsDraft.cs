using FitOSC.Models;
using FitOSC.Services.Treadmill;

namespace FitOSC.Pages.Components.SettingsTabs;

public enum SettingsPage
{
    General,
    Treadmill,
    Walking,
    Integrations,
    Diagnostics,
    About
}

/// <summary>
/// Editable copy of the configuration shown in the settings window. Pages edit this
/// draft; nothing is written to <see cref="AppConfiguration"/> until the user saves.
/// </summary>
public sealed class SettingsDraft
{
    public GeneralDraft General { get; private set; } = new();
    public TreadmillDraft Treadmill { get; private set; } = new();
    public WalkingDraft Walking { get; private set; } = new();
    public IntegrationsDraft Integrations { get; private set; } = new();

    public static SettingsDraft From(AppConfiguration config, bool launchWithSteamVR, bool webSocketEnabled, UpdateChannel updateChannel, DateFormat dateFormat, TimeFormat timeFormat) => new()
    {
        General = new GeneralDraft
        {
            PreferMetric = config.User.PreferMetric,
            DateFormat = dateFormat,
            TimeFormat = timeFormat,
            HistoryEnabled = config.User.HistoryEnabled,
            MinimumSessionDuration = config.User.MinimumSessionDuration,
            UseHardwareAcceleration = config.User.UseHardwareAcceleration,
            LaunchWithSteamVR = launchWithSteamVR,
            AutoUpdate = config.Updates.AutoUpdate,
            UpdateChannel = updateChannel
        },
        Treadmill = new TreadmillDraft
        {
            TreadmillType = config.Treadmill.TreadmillType,
            Connection = config.Treadmill.Connection with { },
            AutoConnect = config.Treadmill.AutoConnect
        },
        Walking = new WalkingDraft
        {
            DefaultMode = config.WalkingMode.DefaultMode,
            Steering = config.WalkingMode.Steering,
            MaxSpeedKph = config.WalkingMode.MaxSpeed,
            DefaultTrim = config.WalkingMode.DefaultTrim,
            SmoothingFactor = config.WalkingMode.SmoothingFactor,
            MaxTurnAngle = config.WalkingMode.MaxTurnAngle,
            UpdateIntervalMs = config.WalkingMode.UpdateIntervalMs,
            ThumbstickRampSpeed = config.WalkingMode.ThumbstickRampSpeed
        },
        Integrations = new IntegrationsDraft
        {
            WebSocketEnabled = webSocketEnabled,
            PulsoidEnabled = config.Pulsoid.Enabled,
            PulsoidApiKey = config.Pulsoid.ApiKey ?? "",
            MidiEnabled = config.Midi.Enabled,
            MidiOutputDevice = config.Midi.OutputDeviceName,
            MidiChannel = config.Midi.Channel,
            MidiSpeedCC = config.Midi.SpeedCC,
            MidiHeartRateCC = config.Midi.HeartRateCC,
            MidiInclineCC = config.Midi.InclineCC
        }
    };

    public SettingsDraft Clone() => new()
    {
        General = General with { },
        Treadmill = Treadmill with { Connection = Treadmill.Connection with { } },
        Walking = Walking with { },
        Integrations = Integrations with { }
    };

    /// <summary>Accepts the persisted treadmill identity written by a successful connection.</summary>
    public void AcceptConnectedTreadmill(TreadmillSettings saved) =>
        Treadmill = Treadmill with { TreadmillType = saved.TreadmillType, Connection = saved.Connection with { } };

    public bool IsChanged(SettingsPage page, SettingsDraft baseline) => page switch
    {
        SettingsPage.General => General != baseline.General,
        SettingsPage.Treadmill => Treadmill != baseline.Treadmill,
        SettingsPage.Walking => Walking != baseline.Walking,
        SettingsPage.Integrations => Integrations != baseline.Integrations,
        _ => false
    };

    public int ErrorCount(SettingsDraft baseline) =>
        General.ErrorCount + Walking.ErrorCount + Integrations.ErrorCount
        + (Treadmill.ConnectionError(baseline.Treadmill) is null ? 0 : 1);

    public SettingsPage? FirstPageWithErrors(SettingsDraft baseline)
    {
        if (General.ErrorCount > 0) return SettingsPage.General;
        if (Treadmill.ConnectionError(baseline.Treadmill) is not null) return SettingsPage.Treadmill;
        if (Walking.ErrorCount > 0) return SettingsPage.Walking;
        if (Integrations.ErrorCount > 0) return SettingsPage.Integrations;
        return null;
    }

    private static string? Range(decimal value, decimal min, decimal max, string message) =>
        value < min || value > max ? message : null;

    public sealed record GeneralDraft
    {
        public bool PreferMetric { get; set; } = true;
        public DateFormat DateFormat { get; set; }
        public TimeFormat TimeFormat { get; set; }
        public bool HistoryEnabled { get; set; } = true;
        public int MinimumSessionDuration { get; set; } = 5;
        public bool UseHardwareAcceleration { get; set; } = true;
        public bool LaunchWithSteamVR { get; set; }
        public bool AutoUpdate { get; set; } = true;
        public UpdateChannel UpdateChannel { get; set; }

        public string? MinimumSessionDurationError =>
            HistoryEnabled ? Range(MinimumSessionDuration, 1, 60, "Whole number, 1–60.") : null;

        public int ErrorCount => MinimumSessionDurationError is null ? 0 : 1;
    }

    public sealed record TreadmillDraft
    {
        public TreadmillType TreadmillType { get; set; } = TreadmillType.FTMS;
        public BluetoothConnectionSettings Connection { get; set; } = new();
        public bool AutoConnect { get; set; }

        public BluetoothConnectionSettings TrimmedConnection =>
            Connection with { Host = Connection.Host.Trim(), EncryptionKey = Connection.EncryptionKey.Trim() };

        /// <summary>Validates the connection only when it differs from what is already saved.</summary>
        public string? ConnectionError(TreadmillDraft baseline)
        {
            if (TrimmedConnection == baseline.TrimmedConnection)
                return null;
            try { TrimmedConnection.Validate(); }
            catch (ArgumentException ex) { return ex.Message; }
            return null;
        }
    }

    public sealed record WalkingDraft
    {
        /// <summary>Stored in km/h, matching <see cref="WalkingModeSettings.MaxSpeed"/>.</summary>
        public decimal MaxSpeedKph { get; set; } = 8.0m;
        public WalkingMode DefaultMode { get; set; } = WalkingMode.Dynamic;
        public WalkingSteering Steering { get; set; } = WalkingSteering.Locked;
        public float DefaultTrim { get; set; } = 0.80f;
        public float SmoothingFactor { get; set; } = 0.8f;
        public float MaxTurnAngle { get; set; } = 90f;
        public int UpdateIntervalMs { get; set; } = 25;
        public float ThumbstickRampSpeed { get; set; } = 0.05f;

        public decimal MaxSpeed(bool metric) => metric ? MaxSpeedKph : Math.Round(MaxSpeedKph * 0.621371m, 1);

        public void SetMaxSpeed(decimal value, bool metric) =>
            MaxSpeedKph = metric ? value : Math.Round(value * 1.60934m, 1);

        public string? MaxSpeedError => MaxSpeedKph > 0 ? null : "Must be above 0.";
        public string? DefaultTrimError => Range((decimal)DefaultTrim, 0, 2, "0–2");
        public string? SmoothingFactorError => Range((decimal)SmoothingFactor, 0, 1, "0–1");
        public string? MaxTurnAngleError => Range((decimal)MaxTurnAngle, 45, 180, "45–180");
        public string? UpdateIntervalMsError => Range(UpdateIntervalMs, 10, 100, "Whole number, 10–100.");
        public string? ThumbstickRampSpeedError => Range((decimal)ThumbstickRampSpeed, 0.01m, 0.5m, "0.01–0.5");

        public int AdvancedErrorCount =>
            new[] { SmoothingFactorError, MaxTurnAngleError, UpdateIntervalMsError, ThumbstickRampSpeedError }
                .Count(error => error is not null);

        public int ErrorCount =>
            AdvancedErrorCount + new[] { MaxSpeedError, DefaultTrimError }.Count(error => error is not null);
    }

    public sealed record IntegrationsDraft
    {
        public bool WebSocketEnabled { get; set; }
        public bool PulsoidEnabled { get; set; }
        public string PulsoidApiKey { get; set; } = "";
        public bool MidiEnabled { get; set; }
        public string? MidiOutputDevice { get; set; }
        public int MidiChannel { get; set; } = 1;
        public int MidiSpeedCC { get; set; } = 1;
        public int MidiHeartRateCC { get; set; } = 2;
        public int MidiInclineCC { get; set; } = 3;

        public bool PulsoidChanged(IntegrationsDraft baseline) =>
            PulsoidEnabled != baseline.PulsoidEnabled || PulsoidApiKey != baseline.PulsoidApiKey;

        public bool MidiChanged(IntegrationsDraft baseline) =>
            (MidiEnabled, MidiOutputDevice, MidiChannel, MidiSpeedCC, MidiHeartRateCC, MidiInclineCC)
            != (baseline.MidiEnabled, baseline.MidiOutputDevice, baseline.MidiChannel,
                baseline.MidiSpeedCC, baseline.MidiHeartRateCC, baseline.MidiInclineCC);

        public string? MidiChannelError => MidiEnabled ? Range(MidiChannel, 1, 16, "1–16") : null;
        public string? MidiSpeedCCError => CCError(MidiSpeedCC);
        public string? MidiHeartRateCCError => CCError(MidiHeartRateCC);
        public string? MidiInclineCCError => CCError(MidiInclineCC);

        private string? CCError(int value) => MidiEnabled ? Range(value, 0, 127, "0–127") : null;

        public int ErrorCount =>
            new[] { MidiChannelError, MidiSpeedCCError, MidiHeartRateCCError, MidiInclineCCError }
                .Count(error => error is not null);
    }
}
