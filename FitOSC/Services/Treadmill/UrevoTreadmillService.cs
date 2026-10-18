using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.Urevo, "UREVO", "UREVO treadmills and SpaceWalk walking pads", Experimental = true,
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb"])]
public class UrevoTreadmillService(ILogger<UrevoTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private const byte FrameStart = 0x02;
    private const byte FrameEnd = 0x03;
    private const byte ChecksumMask = 0x5A;

    private const byte ParameterCommand = (byte)'P';
    private const byte StatusCommand = (byte)'Q';
    private const byte ControlCommand = (byte)'S';
    private const byte BikeCommand = (byte)'B';

    private const byte SpeedRangeParameter = 0x02;
    private const byte InclineRangeParameter = 0x06;
    private const byte StartControl = 0x01;
    private const byte SetSpeedAndInclineControl = 0x02;
    private const byte StopControl = 0x03;
    private const byte ResumeControl = 0x09;
    private const byte PauseControl = 0x0A;

    private const int StatusDataLength = 13;

    // Used until the treadmill reports its speed range.
    private const decimal DefaultMinSpeed = 1.0m;
    private const decimal DefaultMaxSpeed = 6.0m;
    private const decimal SpeedIncrement = 0.1m;
    private const decimal InclineIncrement = 0.1m;

    // Range queries are repeated every few polls until the treadmill answers.
    private const int RangeQueryEveryTicks = 4;

    // URTM018 is the app's "FT" treadmill: same protocol, but distance is reported in metres instead of 10 m.
    private const string MetreDistanceModel = "URTM018";

    private UrevoStatus? _status;

    // Speed and incline are always set together, so each must resend what was last commanded.
    private readonly CommandedValue _speedRaw = new();
    private readonly CommandedValue _inclineRaw = new();

    private bool _speedRangeKnown;
    private bool _inclineRangeKnown;
    private int _pollTicks;
    private decimal _minSpeed = DefaultMinSpeed;
    private decimal _maxSpeed = DefaultMaxSpeed;
    private decimal _minIncline;
    private decimal _maxIncline;
    private decimal _metresPerDistanceUnit = 10m;

    protected override IReadOnlyList<SerialChannel> Channels { get; } =
    [
        new(Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"))
    ];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(500);

    public override Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        _metresPerDistanceUnit = deviceName.StartsWith(MetreDistanceModel, StringComparison.OrdinalIgnoreCase) ? 1m : 10m;
        return base.ConnectAsync(deviceName, cancellationToken);
    }

    protected override async Task OnConnectedAsync()
    {
        PublishConfiguration();
        await QueryRangesAsync();
    }

    protected override void OnDisconnected()
    {
        _status = null;
        _speedRaw.Reset();
        _inclineRaw.Reset();
        _speedRangeKnown = false;
        _inclineRangeKnown = false;
        _pollTicks = 0;
        _minSpeed = DefaultMinSpeed;
        _maxSpeed = DefaultMaxSpeed;
        _minIncline = 0;
        _maxIncline = 0;
    }

    protected override async Task PollAsync()
    {
        if (++_pollTicks % RangeQueryEveryTicks == 0)
            await QueryRangesAsync();
        await WriteAsync(Frame(StatusCommand, null));
    }

    private async Task QueryRangesAsync()
    {
        if (!_speedRangeKnown)
            await WriteAsync(Frame(ParameterCommand, SpeedRangeParameter));
        if (!_inclineRangeKnown)
            await WriteAsync(Frame(ParameterCommand, InclineRangeParameter));
    }

    // A paused belt resumes; otherwise start a free run (no calorie, time or distance target).
    public override Task StartAsync() =>
        WriteAsync(_status == UrevoStatus.Paused
            ? Frame(ControlCommand, ResumeControl)
            : Frame(ControlCommand, StartControl, new byte[8]));

    public override Task PauseAsync() => WriteAsync(Frame(ControlCommand, PauseControl));

    public override Task StopAsync() => WriteAsync(Frame(ControlCommand, StopControl));

    public override Task SetSpeedAsync(decimal speed)
    {
        var clamped = Math.Clamp(speed, _minSpeed, _maxSpeed);
        Logger.LogInformation("Setting UREVO speed: {Speed:F1} km/h", clamped);
        _speedRaw.Command(ToTenths(clamped));
        return WriteAsync(Frame(ControlCommand, SetSpeedAndInclineControl, _speedRaw.Value, _inclineRaw.Value));
    }

    public override Task SetInclineAsync(decimal incline)
    {
        if (_maxIncline <= _minIncline)
        {
            Logger.LogInformation("This UREVO treadmill has not reported an incline range");
            return Task.CompletedTask;
        }

        _inclineRaw.Command(ToTenths(Math.Clamp(incline, _minIncline, _maxIncline)));
        return WriteAsync(Frame(ControlCommand, SetSpeedAndInclineControl, _speedRaw.Value, _inclineRaw.Value));
    }

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration
        {
            MinSpeed = _minSpeed,
            MaxSpeed = _maxSpeed,
            SpeedIncrement = SpeedIncrement,
            MinIncline = _minIncline,
            MaxIncline = _maxIncline,
            InclineIncrement = _maxIncline > _minIncline ? InclineIncrement : 0
        });

    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var packet = Parse(data);
        if (packet is not { Command: StatusCommand } || packet.Data.Length < StatusDataLength)
            return Telemetry((TreadmillTelemetryProperty.InstantaneousSpeed, 0), (TreadmillTelemetryProperty.InstantaneousPace, 0));

        var body = packet.Data;
        var speed = body[1] / 10m;
        return Telemetry(
            (TreadmillTelemetryProperty.InstantaneousSpeed, speed),
            (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(speed)),
            (TreadmillTelemetryProperty.Incline, body[2] / 10m),
            (TreadmillTelemetryProperty.ElapsedTime, BitConverter.ToUInt16(body, 3)),
            (TreadmillTelemetryProperty.TotalDistance, BitConverter.ToUInt16(body, 5) * _metresPerDistanceUnit),
            (TreadmillTelemetryProperty.Calories, BitConverter.ToUInt16(body, 7) / 10m),
            (TreadmillTelemetryProperty.StepCount, BitConverter.ToUInt16(body, 9)));
    }

    protected override void OnNotification(byte[] data)
    {
        var packet = Parse(data);
        if (packet == null)
            return;

        switch (packet)
        {
            case { Command: StatusCommand }:
                HandleStatus(data, packet.Data);
                break;
            case { Command: ParameterCommand, Sub: SpeedRangeParameter }:
                HandleSpeedRange(packet.Data);
                break;
            case { Command: ParameterCommand, Sub: InclineRangeParameter }:
                HandleInclineRange(packet.Data);
                break;
        }
    }

    private void HandleStatus(byte[] frame, byte[] body)
    {
        if (body.Length == 0)
            return;

        _status = (UrevoStatus)body[0];
        if (body.Length >= StatusDataLength)
        {
            _speedRaw.Report(body[1]);
            _inclineRaw.Report(body[2]);
        }

        RaiseData(TranslateData(frame));
        RaiseState(_status switch
        {
            UrevoStatus.Running => TreadmillState.Running,
            UrevoStatus.Paused => TreadmillState.Paused,
            UrevoStatus.Standby or UrevoStatus.Stopped or UrevoStatus.Stopping or UrevoStatus.Prepared =>
                TreadmillState.Stopped,
            _ => TreadmillState.Unknown
        });
    }

    // The reply carries two speeds in 0.1 km/h; take the lower as the minimum rather than rely on field order.
    private void HandleSpeedRange(byte[] body)
    {
        if (body.Length != 2)
            return;

        _speedRangeKnown = true;
        if (body[0] == 0 || body[1] == 0)
            return;

        _minSpeed = Math.Min(body[0], body[1]) / 10m;
        _maxSpeed = Math.Max(body[0], body[1]) / 10m;
        Logger.LogInformation("UREVO reported speed range {Min:F1}-{Max:F1} km/h", _minSpeed, _maxSpeed);
        PublishConfiguration();
    }

    // The app reads max then min incline in 0.1 %, then a configuration byte; take the lower as the minimum here too.
    private void HandleInclineRange(byte[] body)
    {
        if (body.Length < 2)
            return;

        _inclineRangeKnown = true;
        if (body[0] == body[1])
            return;

        _minIncline = Math.Min(body[0], body[1]) / 10m;
        _maxIncline = Math.Max(body[0], body[1]) / 10m;
        Logger.LogInformation("UREVO reported incline range {Min:F1}-{Max:F1}%", _minIncline, _maxIncline);
        PublishConfiguration();
    }

    private static byte ToTenths(decimal value) => (byte)Math.Clamp(Math.Round(value * 10), 0, 255);

    private static bool HasSub(byte command) => command is not (StatusCommand or BikeCommand);

    private static byte[] Frame(byte command, byte? sub, params byte[] data)
    {
        var frame = new List<byte>(data.Length + 5) { FrameStart, command };
        if (sub != null)
            frame.Add(sub.Value);
        frame.AddRange(data);
        frame.Add(Checksum(command, sub, data));
        frame.Add(FrameEnd);
        return frame.ToArray();
    }

    private static byte Checksum(byte command, byte? sub, IEnumerable<byte> data)
    {
        var sum = command + (sub ?? 0);
        foreach (var b in data)
            sum += b;
        return (byte)((sum & 0xFF) ^ ChecksumMask);
    }

    private static UrevoPacket? Parse(byte[] frame)
    {
        if (frame.Length < 4 || frame[0] != FrameStart || frame[^1] != FrameEnd)
            return null;

        var command = frame[1];
        byte? sub = null;
        var dataStart = 2;
        if (HasSub(command))
        {
            if (frame.Length < 5)
                return null;
            sub = frame[2];
            dataStart = 3;
        }

        var data = frame[dataStart..^2];
        return Checksum(command, sub, data) == frame[^2] ? new UrevoPacket(command, sub, data) : null;
    }

    private sealed record UrevoPacket(byte Command, byte? Sub, byte[] Data);

    private enum UrevoStatus : byte
    {
        Standby = 0,
        Stopped = 1,
        Starting = 2,
        Running = 3,
        Stopping = 4,
        Error = 5,
        Forbidden = 6,
        Prepared = 9,
        Paused = 10,
    }
}
