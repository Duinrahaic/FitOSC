using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.IConsole, "iConsole iRunning+", "Treadmills that work with the iConsole+ app", Experimental = true,
    AdvertisedServices = ["0000fff0-0000-1000-8000-00805f9b34fb", "49535343-fe7d-4ae5-8fa9-9fafd205e455"])]
public class IConsoleTreadmillService(ILogger<IConsoleTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private const byte Header = 0xF0;

    private const byte Connect = 0xA0;
    private const byte GetMaxLoad = 0xA1;
    private const byte GetData = 0xA2;
    private const byte SportMode = 0xA3;
    private const byte SportSetData = 0xA4;
    private const byte ResetOrStart = 0xA5;
    private const byte RouteInfo = 0xAC;
    private const byte LoadingStatus = 0xAF;

    private const byte ConnectReply = 0xB0;
    private const byte MaxLoadReply = 0xB1;
    private const byte DataReply = 0xB2;
    private const byte SportModeReply = 0xB3;
    private const byte SportSetDataReply = 0xB4;
    private const byte LoadingStatusReply = 0xBF;

    private static readonly byte[] ResetData = [0x04];
    private static readonly byte[] StartData = [0x02];
    private static readonly byte[] SportModeData = [0x04, 0x01, 0x01, 0x01, 0x01, 0x01, 0x47];
    private static readonly byte[] RouteInfoData = [0x01, 0x64, 0x64];
    private static readonly byte[] SportSetDataData = [0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x0B, 0x01, 0x01];
    private static readonly byte[] LoadingStartData = [0x02];

    private const int DataReplyMinLength = 18;

    // Kinomap's default equipment incline cap; the console reports no range.
    private const decimal MaxIncline = 16m;

    // The console only streams data after a multi-step start sequence; restart it when a reply goes missing.
    private static readonly TimeSpan HandshakeRetry = TimeSpan.FromSeconds(5);

    private bool _ready;
    private bool _initStarted;
    private long _handshakeProgressAt;
    private byte _id1 = 0x02;
    private byte _id2 = 0x02;
    private decimal _speed;

    // The console does not report incline, so the last commanded value is published instead.
    private decimal _incline;

    protected override IReadOnlyList<SerialChannel> Channels { get; } =
    [
        new(Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
            Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb")),
        // Microchip ISSC transparent UART, used by some consoles instead of FFF0.
        new(Guid.Parse("49535343-fe7d-4ae5-8fa9-9fafd205e455"),
            Guid.Parse("49535343-1e4d-4bd9-ba61-23c647249616"),
            Guid.Parse("49535343-8841-43f4-a8d4-ecbe34729bb3"))
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
        _initStarted = false;
        _handshakeProgressAt = Environment.TickCount64;
        return Send(Connect);
    }

    private void HandshakeProgressed() => _handshakeProgressAt = Environment.TickCount64;

    protected override void OnDisconnected()
    {
        _ready = false;
        _initStarted = false;
        _id1 = 0x02;
        _id2 = 0x02;
        _speed = 0;
        _incline = 0;
    }

    // Polling waits until the console reports it has finished loading the session.
    protected override Task PollAsync()
    {
        if (_ready)
            return Send(GetData);

        return Environment.TickCount64 - _handshakeProgressAt >= HandshakeRetry.TotalMilliseconds
            ? StartHandshakeAsync()
            : Task.CompletedTask;
    }

    public override Task StartAsync()
    {
        Logger.LogInformation("iConsole treadmills start through the connection handshake");
        return Task.CompletedTask;
    }

    public override Task PauseAsync() => Task.CompletedTask;

    public override Task StopAsync() => Task.CompletedTask;

    public override Task SetSpeedAsync(decimal speed)
    {
        Logger.LogInformation("iConsole speed is controlled from the console");
        return Task.CompletedTask;
    }

    public override Task SetInclineAsync(decimal incline)
    {
        _incline = Math.Round(Math.Clamp(incline, 0, MaxIncline));
        RaiseData(TranslateData([]));
        return Send(RouteInfo, (byte)((int)_incline + 1), 0x64, 0x64);
    }

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration { MinIncline = 0, MaxIncline = MaxIncline, InclineIncrement = 1 });

    public override TreadmillTelemetry TranslateData(byte[] data) => Telemetry(
        (TreadmillTelemetryProperty.InstantaneousSpeed, _speed),
        (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(_speed)),
        (TreadmillTelemetryProperty.Incline, _incline));

    protected override void OnNotification(byte[] data)
    {
        if (data.Length < 5 || data[0] != Header || data[^1] != Checksum(data))
            return;

        switch (data[1])
        {
            case ConnectReply:
                HandleConnectReply(data[2], data[3]);
                break;
            case MaxLoadReply when !_ready:
                HandshakeProgressed();
                _ = Send(SportMode, SportModeData);
                break;
            case SportModeReply when !_ready:
                HandshakeProgressed();
                _ = SendSequenceAsync(RouteInfo, RouteInfoData, SportSetData, SportSetDataData);
                break;
            case SportSetDataReply when !_ready:
                HandshakeProgressed();
                _ = Send(LoadingStatus, LoadingStartData);
                break;
            case LoadingStatusReply when data.Length >= 5:
                HandleLoadingStatus(data[4] - 1);
                break;
            case DataReply when data.Length >= DataReplyMinLength:
                HandleData(data);
                break;
        }
    }

    private void HandleConnectReply(byte id1, byte id2)
    {
        // Repeated replies must not restart a sequence that is already running.
        if (_ready || _initStarted)
            return;

        HandshakeProgressed();
        if (id1 != _id1 || id2 != _id2)
        {
            _id1 = id1;
            _id2 = id2;
            _ = Send(Connect);
            return;
        }

        _initStarted = true;
        _ = SendSequenceAsync(ResetOrStart, ResetData, GetMaxLoad, []);
    }

    private void HandleLoadingStatus(int state)
    {
        if (_ready)
            return;

        HandshakeProgressed();
        if (state == 1)
        {
            _ = Send(ResetOrStart, ResetData);
            _ready = true;
        }
        else if (state != 2)
        {
            _ = Send(LoadingStatus, LoadingStartData);
        }
    }

    private void HandleData(byte[] data)
    {
        var program = data[15] - 1;
        var phase = data[16] - 1;
        var mode = data[17] - 1;

        if (program == 2 && phase == 1 && mode == 1)
        {
            // Console is ready and waiting for the start command.
            _ = Send(ResetOrStart, StartData);
            _speed = 0;
            RaiseData(TranslateData(data));
            RaiseState(TreadmillState.Stopped);
            return;
        }

        if (program == 2 && phase == 3 && mode == 2)
        {
            // Speed is two base-100 digits in 0.1 km/h, each sent +1.
            _speed = (((data[12] - 1) & 0xFF) * 100 + ((data[13] - 1) & 0xFF)) / 10m;
            RaiseData(TranslateData(data));
            RaiseState(TreadmillState.Running);
            return;
        }

        if (program == 3 || (program == 1 && mode == 1))
        {
            _speed = 0;
            RaiseData(TranslateData(data));
            RaiseState(program == 3 ? TreadmillState.Paused : TreadmillState.Stopped);
        }
    }

    /// <summary>
    /// Sends the first command three times 500 ms apart, then the second at 2 s, as Kinomap does.
    /// Stops when the connection ends so the writes cannot reach a later session.
    /// </summary>
    private async Task SendSequenceAsync(byte first, byte[] firstData, byte second, byte[] secondData)
    {
        var session = SessionToken;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                await Send(first, firstData);
                await Task.Delay(500, session);
            }
            await Task.Delay(500, session);
            await Send(second, secondData);
        }
        catch (OperationCanceledException)
        {
            // Disconnected mid-sequence.
        }
    }

    private Task Send(byte command, params byte[] data) => WriteAsync(Frame(command, _id1, _id2, data));

    private static byte[] Frame(byte command, byte id1, byte id2, byte[] data)
    {
        var frame = new byte[5 + data.Length];
        frame[0] = Header;
        frame[1] = command;
        frame[2] = id1;
        frame[3] = id2;
        data.CopyTo(frame, 4);
        frame[^1] = Checksum(frame);
        return frame;
    }

    /// <summary>Low byte of the sum of every byte before the checksum.</summary>
    private static byte Checksum(byte[] frame)
    {
        var sum = 0;
        for (var i = 0; i < frame.Length - 1; i++)
            sum += frame[i];
        return (byte)sum;
    }
}
