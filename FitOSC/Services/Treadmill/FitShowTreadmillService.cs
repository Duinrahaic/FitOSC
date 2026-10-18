using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.FitShow, "FitShow", "Budget treadmills and walking pads with a FitShow controller", Experimental = true,
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb"])]
public class FitShowTreadmillService(ILogger<FitShowTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private const byte Start = 0x02;
    private const byte End = 0x03;
    private const byte ParameterCommand = (byte)'P';
    private const byte StatusCommand = (byte)'Q';
    private const byte ControlCommand = (byte)'S';
    private const byte InclineRangeParameter = 0x03;
    private const byte SetSpeedAndIncline = 0x02;
    private const byte StopControl = 0x03;

    private const int StatusDataLength = 13;

    // Kinomap's default incline cap, used until the treadmill reports its range.
    private const decimal DefaultMaxIncline = 16m;

    // The range query is repeated every few polls until the treadmill answers.
    private const int RangeQueryEveryTicks = 4;

    // Speed and incline are always set together, so each must resend what was last commanded.
    private readonly CommandedValue _speedRaw = new();
    private readonly CommandedValue _inclineRaw = new();

    private bool _rangeKnown;
    private int _pollTicks;
    private decimal _minIncline;
    private decimal _maxIncline = DefaultMaxIncline;

    protected override IReadOnlyList<SerialChannel> Channels { get; } =
    [
        new(Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"))
    ];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(500);

    protected override async Task OnConnectedAsync()
    {
        PublishConfiguration();
        await WriteAsync(Frame(ParameterCommand, InclineRangeParameter));
    }

    protected override void OnDisconnected()
    {
        _speedRaw.Reset();
        _inclineRaw.Reset();
        _rangeKnown = false;
        _pollTicks = 0;
        _minIncline = 0;
        _maxIncline = DefaultMaxIncline;
    }

    protected override async Task PollAsync()
    {
        if (!_rangeKnown && ++_pollTicks % RangeQueryEveryTicks == 0)
            await WriteAsync(Frame(ParameterCommand, InclineRangeParameter));
        await WriteAsync(Frame(StatusCommand));
    }

    public override Task StartAsync()
    {
        Logger.LogInformation("FitShow treadmills are started from the console");
        return Task.CompletedTask;
    }

    // The SDK has no pause command; stopping is the closest action.
    public override Task PauseAsync() => StopAsync();

    public override Task StopAsync() => WriteAsync(Frame(ControlCommand, StopControl));

    public override Task SetSpeedAsync(decimal speed)
    {
        _speedRaw.Command(ToSpeedRaw(speed));
        return WriteAsync(Frame(ControlCommand, SetSpeedAndIncline, _speedRaw.Value, _inclineRaw.Value));
    }

    public override Task SetInclineAsync(decimal incline)
    {
        if (_maxIncline <= _minIncline)
        {
            Logger.LogInformation("This FitShow treadmill reported no incline range");
            return Task.CompletedTask;
        }

        _inclineRaw.Command((byte)(sbyte)Math.Round(Math.Clamp(incline, _minIncline, _maxIncline)));
        return WriteAsync(Frame(ControlCommand, SetSpeedAndIncline, _speedRaw.Value, _inclineRaw.Value));
    }

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration
        {
            MinIncline = _minIncline,
            MaxIncline = _maxIncline,
            InclineIncrement = _maxIncline > _minIncline ? 1 : 0
        });

    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var body = StatusBody(data);
        if (body == null || !HasWorkoutData(body))
            return Telemetry((TreadmillTelemetryProperty.InstantaneousSpeed, 0), (TreadmillTelemetryProperty.InstantaneousPace, 0));

        var speed = body[2] / 10m;
        return Telemetry(
            (TreadmillTelemetryProperty.InstantaneousSpeed, speed),
            (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(speed)),
            (TreadmillTelemetryProperty.Incline, (sbyte)body[3]));
    }

    protected override void OnNotification(byte[] data)
    {
        var body = Body(data);
        if (body is [ParameterCommand, InclineRangeParameter, ..])
        {
            HandleInclineRange(data);
            return;
        }

        if (body is not [StatusCommand, _, ..])
            return;

        var status = (FitShowStatus)body[1];
        if (HasWorkoutData(body))
        {
            _speedRaw.Report(body[2]);
            _inclineRaw.Report(body[3]);
        }

        RaiseData(TranslateData(data));
        RaiseState(status switch
        {
            FitShowStatus.Running => TreadmillState.Running,
            FitShowStatus.Paused => TreadmillState.Paused,
            FitShowStatus.Normal or FitShowStatus.End or FitShowStatus.Stopping => TreadmillState.Stopped,
            _ => TreadmillState.Unknown
        });
    }

    /// <summary>
    /// Kinomap reads max then min from a long reply; a 7-byte reply carries the span and how far it reaches below zero.
    /// </summary>
    private void HandleInclineRange(byte[] frame)
    {
        int max, min;
        if (frame.Length > 7)
        {
            max = frame[3];
            min = frame[4];
        }
        else if (frame.Length == 7)
        {
            max = frame[3] - frame[4];
            min = -frame[4];
        }
        else
        {
            return;
        }

        _rangeKnown = true;
        _minIncline = min;
        _maxIncline = Math.Max(min, max);
        Logger.LogInformation("FitShow reported incline range {Min}-{Max}%", min, max);
        PublishConfiguration();
    }

    /// <summary>Returns the payload (command onwards) of a valid frame, or null.</summary>
    private static byte[]? Body(byte[] frame)
    {
        if (frame.Length < 5 || frame[0] != Start || frame[^1] != End)
            return null;

        var body = frame[1..^2];
        return Xor(body) == frame[^2] ? body : null;
    }

    /// <summary>Returns the status payload ('Q' onwards) of a valid status frame, or null.</summary>
    private static byte[]? StatusBody(byte[] frame) => Body(frame) is [StatusCommand, _, ..] body ? body : null;

    // The SDK only decodes the workout fields for the END, RUNNING and STOPPING states.
    private static bool HasWorkoutData(byte[] body) =>
        body.Length >= StatusDataLength &&
        (FitShowStatus)body[1] is FitShowStatus.End or FitShowStatus.Running or FitShowStatus.Stopping;

    private static byte ToSpeedRaw(decimal kph) => (byte)Math.Clamp(Math.Round(kph * 10), 0, 255);

    private static byte[] Frame(params byte[] payload)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = Start;
        payload.CopyTo(frame, 1);
        frame[^2] = Xor(payload);
        frame[^1] = End;
        return frame;
    }

    private static byte Xor(byte[] bytes)
    {
        byte value = 0;
        foreach (var b in bytes)
            value ^= b;
        return value;
    }

    private enum FitShowStatus : byte
    {
        Normal = 0,
        End = 1,
        Start = 2,
        Running = 3,
        Stopping = 4,
        Error = 5,
        Safety = 6,
        Paused = 10,
    }
}
