using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.Endex, "Endex", "Treadmills with an Endex console", Experimental = true,
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb"])]
public class EndexTreadmillService(ILogger<EndexTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private const byte Query = 0xA1;
    private const byte Handshake1 = 0xE2;
    private const byte Handshake3 = 0xE1;
    private const byte SetIncline = 0xD2;
    private const byte Handshake2 = 0xD3;
    private const byte SetMachineType = 0xD4;
    private const byte TreadmillMachineType = 0x02;

    private const byte QueryConnect = 0xC2;
    private const byte QuerySpeed = 0x82;
    private const byte QueryIncline = 0x83;
    private const byte QueryDistance = 0x85;
    private const byte QueryStatus = 0x91;

    // Kinomap's poll cycle, in order. 0x8A, 0x84 and 0x86 are requested but their meaning is not confirmed.
    private static readonly byte[] PollQueries = [0x8A, 0x84, QueryDistance, 0x86, QuerySpeed, QueryIncline, QueryStatus];

    private const int ReplyLength = 7;

    // Kinomap's default equipment incline cap; the console reports no range.
    private const decimal MaxIncline = 16m;

    // The console only answers polls after a four-step handshake; restart it when a reply goes missing.
    private static readonly TimeSpan HandshakeRetry = TimeSpan.FromSeconds(3);

    private bool _ready;
    private long _handshakeProgressAt;
    private EndexStatus _status;
    private decimal _speed;
    private decimal _incline;
    private int _distanceMeters;

    protected override IReadOnlyList<SerialChannel> Channels { get; } =
    [
        new(Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"))
    ];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    protected override bool IsReady => _ready;

    protected override Task OnConnectedAsync()
    {
        PublishConfiguration();
        return StartHandshakeAsync();
    }

    private Task StartHandshakeAsync()
    {
        _handshakeProgressAt = Environment.TickCount64;
        return WriteAsync(Command(Query, QueryConnect));
    }

    protected override void OnDisconnected()
    {
        _ready = false;
        _status = EndexStatus.Unknown;
        _speed = 0;
        _incline = 0;
        _distanceMeters = 0;
    }

    protected override async Task PollAsync()
    {
        if (!_ready)
        {
            if (Environment.TickCount64 - _handshakeProgressAt >= HandshakeRetry.TotalMilliseconds)
                await StartHandshakeAsync();
            return;
        }

        foreach (var query in PollQueries)
            await WriteAsync(Command(Query, query));
    }

    public override Task StartAsync()
    {
        Logger.LogInformation("Endex treadmills are started from the console");
        return Task.CompletedTask;
    }

    public override Task PauseAsync() => Task.CompletedTask;

    public override Task StopAsync() => Task.CompletedTask;

    public override Task SetSpeedAsync(decimal speed)
    {
        Logger.LogInformation("Endex speed is controlled from the console");
        return Task.CompletedTask;
    }

    // Kinomap sends whole incline percent, floored at zero.
    public override Task SetInclineAsync(decimal incline) =>
        WriteAsync(Command(SetIncline, (byte)(int)Math.Clamp(incline, 0, MaxIncline)));

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration
        {
            MinIncline = 0,
            MaxIncline = MaxIncline,
            InclineIncrement = 1,
            Commands = TreadmillCommands.Incline
        });

    public override TreadmillTelemetry TranslateData(byte[] data) => Telemetry(
        (TreadmillTelemetryProperty.InstantaneousSpeed, _speed),
        (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(_speed)),
        (TreadmillTelemetryProperty.Incline, _incline),
        (TreadmillTelemetryProperty.TotalDistance, _distanceMeters));

    protected override void OnNotification(byte[] data)
    {
        if (data.Length != ReplyLength)
            return;

        switch (data[0])
        {
            case Query:
                HandleQueryReply(data);
                break;
            case Handshake1:
                _handshakeProgressAt = Environment.TickCount64;
                _ = WriteAsync(Command(SetMachineType, TreadmillMachineType));
                break;
            case SetMachineType:
                _handshakeProgressAt = Environment.TickCount64;
                _ = WriteAsync(Command(Handshake2, 0x00, 0x05, 0x00, 0x01));
                break;
            case Handshake2:
                _handshakeProgressAt = Environment.TickCount64;
                _ = WriteAsync(Command(Handshake3));
                break;
            case Handshake3:
                _ready = true;
                break;
        }
    }

    private void HandleQueryReply(byte[] data)
    {
        switch (data[1])
        {
            case QueryConnect:
                _handshakeProgressAt = Environment.TickCount64;
                _ = WriteAsync(Command(Handshake1));
                return;
            case QuerySpeed:
                // Whole km/h plus hundredths; Kinomap reports zero unless the console says it is running.
                _speed = _status == EndexStatus.Running ? data[2] + data[3] / 100m : 0;
                break;
            case QueryIncline:
                // Signed whole percent, as Kinomap reads it.
                _incline = (sbyte)data[2];
                break;
            case QueryDistance:
                _distanceMeters = data[2] * 1000 + data[3] * 10;
                break;
            case QueryStatus:
                _status = (EndexStatus)data[2];
                if (_status != EndexStatus.Running)
                    _speed = 0;
                RaiseState(_status switch
                {
                    EndexStatus.Running => TreadmillState.Running,
                    EndexStatus.Paused => TreadmillState.Paused,
                    EndexStatus.Idle => TreadmillState.Stopped,
                    _ => TreadmillState.Unknown
                });
                break;
            default:
                return;
        }

        RaiseData(TranslateData(data));
    }

    private static byte[] Command(byte command, params byte[] data)
    {
        var frame = new byte[6];
        frame[0] = command;
        data.CopyTo(frame, 1);

        var check = command ^ 0xFF;
        for (var i = 1; i <= 4; i++)
            check ^= frame[i];
        frame[5] = (byte)check;
        return frame;
    }

    private enum EndexStatus : byte
    {
        Unknown = 0,
        Idle = 1,
        Running = 3,
        Paused = 5,
        Edit = 9,
        Error = 15,
    }
}
