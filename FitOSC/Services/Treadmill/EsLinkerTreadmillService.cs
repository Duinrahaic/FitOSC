using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// EQiSports / CitySports treadmills, over the ES Linker or EFC serial protocol depending on the service the treadmill
/// offers.
/// </summary>
[TreadmillDriver(TreadmillType.EsLinker, "EQiSports / CitySports", "CitySports, GearStone, Esang and Superfit treadmills with an ES Linker module",
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb", "ffeeddcc-bbaa-9988-7766-554433221100"])]
public partial class EsLinkerTreadmillService(ILogger<EsLinkerTreadmillService> logger, AppStateService appState,
    BaseBluetoothClient client) : SerialTreadmillService(logger, appState, client)
{
    // The EQiSports app speaks ES over fff0 and EFC over this vendor service, choosing by which one the treadmill has.
    private static readonly SerialChannel EsChannel = new(
        Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"));

    private static readonly SerialChannel EfcChannel = new(
        Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
        Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221102"),
        Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221101"));

    private const decimal KilometresPerMile = 1.609344m;

    // Created for each connection, so no protocol state outlives its session.
    private EqiProtocol? _protocol;

    protected override IReadOnlyList<SerialChannel> Channels { get; } = [EsChannel, EfcChannel];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    protected override Task OnConnectedAsync()
    {
        _protocol = ConnectedChannel == EfcChannel ? new EfcProtocol(this) : new EsProtocol(this);
        Logger.LogInformation("EQiSports treadmill speaks the {Protocol} protocol", _protocol.Name);
        return _protocol.OnConnectedAsync();
    }

    protected override void OnDisconnected() => _protocol = null;

    protected override Task PollAsync() => _protocol?.PollAsync() ?? Task.CompletedTask;

    protected override void OnNotification(byte[] data) => _protocol?.OnNotification(data);

    public override Task StartAsync() => _protocol?.StartAsync() ?? Task.CompletedTask;

    public override Task PauseAsync() => _protocol?.PauseAsync() ?? Task.CompletedTask;

    public override Task StopAsync() => _protocol?.StopAsync() ?? Task.CompletedTask;

    public override Task SetSpeedAsync(decimal speed) => _protocol?.SetSpeedAsync(speed) ?? Task.CompletedTask;

    public override Task SetInclineAsync(decimal incline) => _protocol?.SetInclineAsync(incline) ?? Task.CompletedTask;

    public override Task GetTreadmillConfigurationAsync()
    {
        _protocol?.PublishConfiguration();
        return Task.CompletedTask;
    }

    public override TreadmillTelemetry TranslateData(byte[] data) => _protocol?.Telemetry() ?? new TreadmillTelemetry();

    private static int ReadBigEndian(byte[] data, int offset, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }

    private static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte xor = 0;
        foreach (var b in bytes)
            xor ^= b;
        return xor;
    }

    /// <summary>
    /// State and conversions shared by the ES Linker and EFC protocols.
    /// </summary>
    private abstract class EqiProtocol(EsLinkerTreadmillService service, int caloriesWrap, decimal caloriesPerKcal)
    {
        // The console's counters roll over at 100 min and 9,999 steps.
        private const int ElapsedSecondsWrap = 6000;
        private const int StepsWrap = 10000;

        // Used until the treadmill reports its speed range, in tenths of the treadmill's unit.
        private const byte DefaultMinSpeedRaw = 10;
        private const byte DefaultMaxSpeedRaw = 120;

        private bool _imperial;
        private EqiStatus _status;
        private byte _speedRaw;
        private byte _inclineRaw;
        private int _heartRate;

        // Speeds are kept raw so they convert with whichever unit the treadmill reports, whenever it reports it.
        private byte _minSpeedRaw = DefaultMinSpeedRaw;
        private byte _maxSpeedRaw = DefaultMaxSpeedRaw;

        // Whole percent; a treadmill that reports no incline range has no incline control.
        private byte _minIncline;
        private byte _maxIncline;
        private readonly WrappingCounter _calories = new(caloriesWrap);
        private int _distanceMeters;
        private readonly WrappingCounter _steps = new(StepsWrap);
        private readonly WrappingCounter _elapsedSeconds = new(ElapsedSecondsWrap);

        protected ILogger Logger => service.Logger;

        protected bool Imperial => _imperial;

        protected EqiStatus Status => _status;

        protected byte SpeedRaw => _speedRaw;

        protected bool PauseSupported { get; set; }

        /// <summary>The protocol's name in log lines.</summary>
        public abstract string Name { get; }

        public abstract Task OnConnectedAsync();

        public abstract Task PollAsync();

        public abstract void OnNotification(byte[] data);

        protected abstract byte[] StartFrame(bool resume);

        protected abstract byte[] PauseFrame();

        protected abstract byte[] StopFrame();

        protected abstract byte[] SpeedFrame(byte raw);

        protected abstract byte[] InclineFrame(byte percent);

        public Task StartAsync() => Write(StartFrame(_status is EqiStatus.Paused or EqiStatus.Pausing));

        // Only offered while PauseSupported is set.
        public Task PauseAsync() => Write(PauseFrame());

        public Task StopAsync() => Write(StopFrame());

        public Task SetSpeedAsync(decimal speed)
        {
            var raw = Math.Clamp(ToRawSpeed(speed), _minSpeedRaw, _maxSpeedRaw);
            Logger.LogInformation("Setting {Protocol} speed: {Speed:F1} km/h", Name, FromRawSpeed(raw));
            return Write(SpeedFrame(raw));
        }

        public Task SetInclineAsync(decimal incline)
        {
            if (_maxIncline <= _minIncline)
            {
                Logger.LogInformation("This {Protocol} treadmill has no incline control", Name);
                return Task.CompletedTask;
            }

            // Whole percent, rounded half up as the app's BigDecimal HALF_UP does.
            var percent = Math.Round(incline, MidpointRounding.AwayFromZero);
            return Write(InclineFrame((byte)Math.Clamp(percent, _minIncline, _maxIncline)));
        }

        // One raw step is 0.1 of the treadmill's unit: 0.1 km/h, or 0.1 mph in imperial mode.
        public void PublishConfiguration() =>
            service.RaiseConfiguration(new TreadmillConfiguration
            {
                MinSpeed = FromRawSpeed(_minSpeedRaw),
                MaxSpeed = FromRawSpeed(_maxSpeedRaw),
                SpeedIncrement = FromRawSpeed(1),
                MinIncline = _minIncline,
                MaxIncline = _maxIncline,
                InclineIncrement = _maxIncline > _minIncline ? 1 : 0,
                Commands = TreadmillCommands.Start | TreadmillCommands.Stop | TreadmillCommands.Speed | TreadmillCommands.Incline |
                           (PauseSupported ? TreadmillCommands.Pause : TreadmillCommands.None)
            });

        public TreadmillTelemetry Telemetry()
        {
            var speed = FromRawSpeed(_speedRaw);
            var telemetry = SerialTreadmillService.Telemetry(
                (TreadmillTelemetryProperty.InstantaneousSpeed, speed),
                (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(speed)),
                (TreadmillTelemetryProperty.Incline, _inclineRaw),
                (TreadmillTelemetryProperty.ElapsedTime, _elapsedSeconds.Value),
                (TreadmillTelemetryProperty.TotalDistance, _distanceMeters),
                (TreadmillTelemetryProperty.Calories, _calories.Value / caloriesPerKcal),
                (TreadmillTelemetryProperty.StepCount, _steps.Value));

            // Zero means no hand-grip reading.
            if (_heartRate > 0)
            {
                telemetry.Values[TreadmillTelemetryProperty.HeartRate].Enabled = true;
                telemetry.Values[TreadmillTelemetryProperty.HeartRate].Value = _heartRate;
            }

            return telemetry;
        }

        protected Task Write(byte[] frame) => service.WriteAsync(frame);

        protected void PublishData() => service.RaiseData(Telemetry());

        protected void SetUnit(bool imperial)
        {
            if (imperial == _imperial)
                return;

            _imperial = imperial;
            PublishConfiguration();
        }

        protected void SetSpeedRaw(byte raw) => _speedRaw = raw;

        protected void SetInclineRaw(byte raw) => _inclineRaw = raw;

        /// <summary>
        /// Applies a reported speed range (tenths of the treadmill's unit) and incline range (whole percent); an incline
        /// range whose maximum is not above its minimum means no incline control. Returns false, changing nothing, for
        /// an unusable speed range.
        /// </summary>
        protected bool TrySetRanges(byte minSpeed, byte maxSpeed, byte minIncline, byte maxIncline)
        {
            // A minimum of zero is valid; the EQI app accepts it.
            if (maxSpeed == 0 || minSpeed > maxSpeed)
                return false;

            if (maxIncline <= minIncline)
                minIncline = maxIncline = 0;

            if (minSpeed == _minSpeedRaw && maxSpeed == _maxSpeedRaw && minIncline == _minIncline && maxIncline == _maxIncline)
                return true;

            _minSpeedRaw = minSpeed;
            _maxSpeedRaw = maxSpeed;
            _minIncline = minIncline;
            _maxIncline = maxIncline;
            Logger.LogInformation("{Protocol} reported speed range {Min:F1}-{Max:F1} km/h, incline {MinIncline}-{MaxIncline}%",
                Name, FromRawSpeed(_minSpeedRaw), FromRawSpeed(_maxSpeedRaw), _minIncline, _maxIncline);
            PublishConfiguration();
            return true;
        }

        protected void UpdateWorkout(int elapsedSeconds, int distanceMeters, int calories, int steps, int heartRate = 0)
        {
            _elapsedSeconds.Update(elapsedSeconds);
            _distanceMeters = distanceMeters;
            _calories.Update(calories);
            _steps.Update(steps);
            _heartRate = heartRate;
            PublishData();
        }

        protected void HandleStatus(EqiStatus status)
        {
            // A new workout restarts the console's counters from zero; that is not a rollover.
            if (status is EqiStatus.PreWorkout or EqiStatus.Running &&
                _status is EqiStatus.Idle or EqiStatus.PostWorkout or EqiStatus.Stopped)
            {
                _calories.Reset();
                _steps.Reset();
                _elapsedSeconds.Reset();
            }

            _status = status;
            if (status is not (EqiStatus.PreWorkout or EqiStatus.Running) && _speedRaw != 0)
            {
                // The belt is not moving; the treadmill does not always send a final speed of zero.
                _speedRaw = 0;
                PublishData();
            }

            service.RaiseState(status switch
            {
                EqiStatus.PreWorkout or EqiStatus.Running => TreadmillState.Running,
                EqiStatus.Pausing or EqiStatus.Paused => TreadmillState.Paused,
                EqiStatus.Idle or EqiStatus.PostWorkout or EqiStatus.Stopped => TreadmillState.Stopped,
                _ => TreadmillState.Unknown
            });
        }

        protected decimal FromRawSpeed(byte raw) => _imperial ? raw / 10m * KilometresPerMile : raw / 10m;

        private byte ToRawSpeed(decimal kph)
        {
            var tenths = Math.Round((_imperial ? kph / KilometresPerMile : kph) * 10, MidpointRounding.AwayFromZero);
            return (byte)Math.Clamp(tenths, 0, 255);
        }
    }

    /// <summary>
    /// A console counter that rolls over at <paramref name="wrap"/>; each drop in the raw value adds one full period.
    /// </summary>
    private sealed class WrappingCounter(int wrap)
    {
        private int _last;
        private int _offset;

        public int Value => _offset + _last;

        public void Update(int raw)
        {
            if (raw < _last)
                _offset += wrap;
            _last = raw;
        }

        public void Reset()
        {
            _last = 0;
            _offset = 0;
        }
    }

    private enum EqiStatus : byte
    {
        Idle = 0,
        PreWorkout = 1,
        Running = 2,
        Pausing = 3,
        Paused = 4,
        PostWorkout = 5,
        Stopped = 6,
    }
}
