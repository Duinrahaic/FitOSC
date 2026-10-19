using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.EsLinker, "EQiSports / CitySports", "CitySports, GearStone, Esang and Superfit treadmills with an ES Linker module", Experimental = true,
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb"])]
public class EsLinkerTreadmillService(ILogger<EsLinkerTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private const byte Header = 0xA9;

    private const byte SetSpeedCommand = 0x01;
    private const byte HeartbeatCommand = 0x02;
    private const byte ErrorCommand = 0x03;
    private const byte SetInclineCommand = 0x04;
    private const byte PairingCommand = 0x08;
    private const byte StatusCommand = 0x09;
    private const byte SpeedRangeCommand = 0x0A;
    private const byte PauseSupportQuery = 0x9E;
    private const byte KeepAliveCommand = 0xA0;
    private const byte ControlCommand = 0xA3;
    private const byte SpeedChangedCommand = 0xE0;
    private const byte InclineChangedCommand = 0xE1;
    private const byte UnitCommand = 0xF2;
    private const byte PauseSupportReply = 0xF3;
    private const byte CurrentSpeedCommand = 0xF4;
    private const byte RunningCommand = 0xFE;

    private const byte HandshakeLength = 0x01;
    private const byte ChallengeLength = 0x04;
    private const byte StopControl = 0x00;
    private const byte StartControl = 0x01;
    private const byte ResumeControl = 0x02;
    private const byte PauseControl = 0x03;
    private const byte ImperialUnit = 0x02;

    private const int ShortHeartbeatLength = 17;
    private const int LongHeartbeatLength = 18;
    private const int FrameOverhead = 4; // header, command, length, XOR
    private const decimal KilometresPerMile = 1.609344m;

    // The console's heartbeat counters roll over at these raw values: 100 min, 9,999 steps, 1,000 kcal.
    private const int ElapsedSecondsWrap = 6000;
    private const int StepsWrap = 10000;
    private const int CaloriesWrap = 100000;

    // The app repeats the handshake every three seconds until the treadmill sends its challenge.
    private const int HandshakeEveryTicks = 3;

    // Used until the treadmill reports its speed range, in tenths of the treadmill's unit.
    private const byte DefaultMinSpeedRaw = 10;
    private const byte DefaultMaxSpeedRaw = 120;

    private bool _paired;
    private int _handshakeTicks;
    private byte[]? _lastChallenge;
    private byte[]? _lastPairingCode;
    private bool _unitKnown;
    private bool _rangeKnown;
    private bool _imperial;
    private bool _pauseSupported;
    private EsStatus _status;
    private byte _speedRaw;
    private byte _inclineRaw;

    // Speeds are kept raw so they convert with whichever unit the treadmill reports, whenever it reports it.
    private byte _minSpeedRaw = DefaultMinSpeedRaw;
    private byte _maxSpeedRaw = DefaultMaxSpeedRaw;

    // Whole percent; a treadmill that reports no incline range has no incline control.
    private byte _minIncline;
    private byte _maxIncline;
    private readonly WrappingCounter _calories = new(CaloriesWrap);
    private int _distanceMeters;
    private readonly WrappingCounter _steps = new(StepsWrap);
    private readonly WrappingCounter _elapsedSeconds = new(ElapsedSecondsWrap);

    protected override IReadOnlyList<SerialChannel> Channels { get; } =
    [
        new(Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"))
    ];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    protected override async Task OnConnectedAsync()
    {
        PublishConfiguration();
        await WriteAsync(UnitQuery());
    }

    protected override void OnDisconnected()
    {
        _paired = false;
        _handshakeTicks = 0;
        _lastChallenge = null;
        _lastPairingCode = null;
        _unitKnown = false;
        _rangeKnown = false;
        _imperial = false;
        _pauseSupported = false;
        _status = EsStatus.Idle;
        _speedRaw = 0;
        _inclineRaw = 0;
        _minSpeedRaw = DefaultMinSpeedRaw;
        _maxSpeedRaw = DefaultMaxSpeedRaw;
        _minIncline = 0;
        _maxIncline = 0;
        _distanceMeters = 0;
        ResetCounters();
    }

    private void ResetCounters()
    {
        _calories.Reset();
        _steps.Reset();
        _elapsedSeconds.Reset();
    }

    // Handshake until paired, then echo state and speed back as a keep-alive.
    protected override Task PollAsync()
    {
        if (_paired)
            return WriteAsync(Frame(KeepAliveCommand, (byte)_status, _speedRaw, 0x00));
        return ++_handshakeTicks % HandshakeEveryTicks == 0 ? WriteAsync(HandshakeFrame()) : Task.CompletedTask;
    }

    public override Task StartAsync() =>
        WriteAsync(Frame(ControlCommand, _status is EsStatus.Paused or EsStatus.Pausing ? ResumeControl : StartControl));

    public override Task PauseAsync()
    {
        if (!_pauseSupported)
        {
            Logger.LogInformation("This ES Linker treadmill does not support pausing");
            return Task.CompletedTask;
        }
        return WriteAsync(Frame(ControlCommand, PauseControl));
    }

    public override Task StopAsync() => WriteAsync(Frame(ControlCommand, StopControl));

    public override Task SetSpeedAsync(decimal speed)
    {
        var raw = Math.Clamp(ToRawSpeed(speed), _minSpeedRaw, _maxSpeedRaw);
        Logger.LogInformation("Setting ES Linker speed: {Speed:F1} km/h", FromRawSpeed(raw));
        return WriteAsync(Frame(SetSpeedCommand, raw));
    }

    public override Task SetInclineAsync(decimal incline)
    {
        if (_maxIncline <= _minIncline)
        {
            Logger.LogInformation("This ES Linker treadmill has no incline control");
            return Task.CompletedTask;
        }

        return WriteAsync(Frame(SetInclineCommand, (byte)Math.Clamp(Math.Round(incline), _minIncline, _maxIncline)));
    }

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    // One raw step is 0.1 of the treadmill's unit: 0.1 km/h, or 0.1 mph in imperial mode.
    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration
        {
            MinSpeed = FromRawSpeed(_minSpeedRaw),
            MaxSpeed = FromRawSpeed(_maxSpeedRaw),
            SpeedIncrement = FromRawSpeed(1),
            MinIncline = _minIncline,
            MaxIncline = _maxIncline,
            InclineIncrement = _maxIncline > _minIncline ? 1 : 0
        });

    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var speed = FromRawSpeed(_speedRaw);
        return Telemetry(
            (TreadmillTelemetryProperty.InstantaneousSpeed, speed),
            (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(speed)),
            (TreadmillTelemetryProperty.Incline, _inclineRaw),
            (TreadmillTelemetryProperty.ElapsedTime, _elapsedSeconds.Value),
            (TreadmillTelemetryProperty.TotalDistance, _distanceMeters),
            (TreadmillTelemetryProperty.Calories, _calories.Value / 100m),
            (TreadmillTelemetryProperty.StepCount, _steps.Value));
    }

    protected override void OnNotification(byte[] data)
    {
        // Every frame declares its payload length; drop anything truncated or padded.
        if (data.Length < FrameOverhead || data[0] != Header || data.Length != data[2] + FrameOverhead)
            return;

        switch (data[1])
        {
            case PairingCommand when data[2] == HandshakeLength:
                // The treadmill answered the handshake; keep handshaking until it sends a pairing challenge.
                if (!_paired)
                    _ = WriteAsync(HandshakeFrame());
                break;
            case PairingCommand when data[2] == ChallengeLength:
                HandleChallenge(data);
                break;
            case HeartbeatCommand when data.Length is ShortHeartbeatLength or LongHeartbeatLength:
                HandleHeartbeat(data);
                break;
            case StatusCommand when data[2] >= 1:
                HandleStatus((EsStatus)data[3]);
                break;
            case RunningCommand:
                HandleStatus(EsStatus.Running);
                break;
            case CurrentSpeedCommand or SpeedChangedCommand when data[2] >= 1:
                _speedRaw = data[3];
                RaiseData(TranslateData(data));
                break;
            case InclineChangedCommand when data[2] >= 1:
                _inclineRaw = data[3];
                RaiseData(TranslateData(data));
                break;
            case UnitCommand when data[2] >= 1:
                HandleUnit(data);
                break;
            case SpeedRangeCommand when data[2] >= 2:
                HandleSpeedRange(data);
                break;
            case PauseSupportReply when data[2] >= 1:
                _pauseSupported = data[3] == 1;
                break;
            case ErrorCommand:
                Logger.LogWarning("ES Linker treadmill reported error {Frame}", Convert.ToHexString(data));
                break;
        }
    }

    private void HandleChallenge(byte[] data)
    {
        // The treadmill repeats its challenge until it is answered; repeat the same answer in case ours was lost.
        if (_lastChallenge == null || !_lastChallenge.AsSpan().SequenceEqual(data))
        {
            _lastChallenge = data;
            _lastPairingCode = Frame(PairingCommand, PairingCode(data[^2]));
            Logger.LogInformation("ES Linker treadmill paired");
        }

        _paired = true;
        _ = WriteAsync(_lastPairingCode!);
    }

    private void HandleHeartbeat(byte[] data)
    {
        if (!HasValidHeartbeatXor(data))
            return;

        if (!_unitKnown)
            _ = WriteAsync(UnitQuery());
        if (!_rangeKnown)
            _ = WriteAsync(Frame(SpeedRangeCommand, (byte)Random.Shared.Next(255)));

        // Calories arrive in units of 10 cal.
        _calories.Update(ReadBigEndian(data, 4, 3));
        _distanceMeters = ReadBigEndian(data, 7, 2);
        _steps.Update(ReadBigEndian(data, 9, 3));
        _elapsedSeconds.Update(ReadBigEndian(data, 12, 2));
        RaiseData(TranslateData(data));
    }

    private void HandleStatus(EsStatus status)
    {
        // A new workout restarts the console's counters from zero; that is not a rollover.
        if (status is EsStatus.PreWorkout or EsStatus.Running &&
            _status is EsStatus.Idle or EsStatus.PostWorkout or EsStatus.Stopped)
            ResetCounters();

        _status = status;
        if (status is not (EsStatus.PreWorkout or EsStatus.Running) && _speedRaw != 0)
        {
            // The belt is not moving; the treadmill does not always send a final speed of zero.
            _speedRaw = 0;
            RaiseData(TranslateData([]));
        }

        RaiseState(status switch
        {
            EsStatus.PreWorkout or EsStatus.Running => TreadmillState.Running,
            EsStatus.Pausing or EsStatus.Paused => TreadmillState.Paused,
            EsStatus.Idle or EsStatus.PostWorkout or EsStatus.Stopped => TreadmillState.Stopped,
            _ => TreadmillState.Unknown
        });
    }

    // Some consoles append the current speed and incline to the unit reply.
    private void HandleUnit(byte[] data)
    {
        _unitKnown = true;
        var imperial = data[3] == ImperialUnit;
        if (imperial != _imperial)
        {
            _imperial = imperial;
            PublishConfiguration();
        }

        if (data.Length > 6)
        {
            _speedRaw = data[4];
            _inclineRaw = data[5];
            RaiseData(TranslateData(data));
        }
    }

    // Speed range in bytes 3-4; consoles with incline control append its range (whole percent) in bytes 5-6.
    private void HandleSpeedRange(byte[] data)
    {
        _rangeKnown = true;
        if (data[3] == 0 || data[4] == 0 || data[3] > data[4])
        {
            Logger.LogWarning("Ignoring ES Linker speed range {Frame}", Convert.ToHexString(data));
            return;
        }

        _minSpeedRaw = data[3];
        _maxSpeedRaw = data[4];
        if (data.Length > 7 && data[6] > data[5])
        {
            _minIncline = data[5];
            _maxIncline = data[6];
        }

        Logger.LogInformation("ES Linker reported speed range {Min:F1}-{Max:F1} km/h, incline {MinIncline}-{MaxIncline}%",
            FromRawSpeed(_minSpeedRaw), FromRawSpeed(_maxSpeedRaw), _minIncline, _maxIncline);
        PublishConfiguration();
        _ = WriteAsync(Frame(PauseSupportQuery, 0xFE));
    }

    private decimal FromRawSpeed(byte raw) => _imperial ? raw / 10m * KilometresPerMile : raw / 10m;

    private byte ToRawSpeed(decimal kph)
    {
        var tenths = Math.Round((_imperial ? kph / KilometresPerMile : kph) * 10);
        return (byte)Math.Clamp(tenths, 0, 255);
    }

    private static byte[] UnitQuery() => Frame(UnitCommand, 0x2F);

    private static byte[] HandshakeFrame() => Frame(PairingCommand, (byte)Random.Shared.Next(255));

    /// <summary>
    /// The pairing reply: two random nibbles, their sum and their product, in an order chosen by the challenge byte.
    /// </summary>
    private static byte[] PairingCode(byte challenge)
    {
        var a = (byte)Random.Shared.Next(1, 16);
        var b = (byte)Random.Shared.Next(1, 16);
        var sum = (byte)(a + b);
        var product = (byte)(a * b);
        return (challenge % 6) switch
        {
            0 => [a, b, sum, product],
            1 => [a, b, product, sum],
            2 => [sum, a, b, product],
            3 => [product, a, b, sum],
            4 => [sum, product, a, b],
            _ => [product, sum, a, b]
        };
    }

    private static int ReadBigEndian(byte[] data, int offset, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }

    private static byte[] Frame(byte command, params byte[] data)
    {
        byte[] body = [Header, command, (byte)data.Length, .. data];
        return [.. body, Xor(body)];
    }

    // Matches the EQI app, whose heartbeat checksum leaves out the byte before the XOR.
    private static bool HasValidHeartbeatXor(byte[] frame) => Xor(frame.AsSpan(0, frame.Length - 2)) == frame[^1];

    private static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte xor = 0;
        foreach (var b in bytes)
            xor ^= b;
        return xor;
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

    private enum EsStatus : byte
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
