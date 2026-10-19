using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// WalkingPad and older KingSmith treadmills, over the FE00 serial protocol.
/// </summary>
[TreadmillDriver(TreadmillType.KingSmith, "KingSmith WalkingPad / Legacy", "WalkingPad and older KingSmith treadmills",
    AdvertisedServices = ["0000fe00-0000-1000-8000-00805f9b34fb"])]
public class KingSmithTreadmillService(ILogger<KingSmithTreadmillService> logger, AppStateService appState)
    : SerialTreadmillService(logger, appState)
{
    private static readonly SerialChannel Channel = new(
        Guid.Parse("0000fe00-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000fe01-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000fe02-0000-1000-8000-00805f9b34fb"));

    private static readonly TimeSpan ModeChangeTimeout = TimeSpan.FromSeconds(3);

    // KS Fit floors the minimum speed at 0.5 km/h and falls back to 6.0 km/h when the device reports no maximum.
    private const decimal MinSpeed = 0.5m;
    private const decimal DefaultMaxSpeed = 6.0m;
    private const decimal SpeedIncrement = 0.1m;

    // The parameter query is repeated every few polls until the treadmill answers.
    private const int ParameterQueryEveryTicks = 4;

    private readonly KingSmithFrameReader _frameReader = new();
    private KingSmithStatus? _status;
    private decimal _maxSpeed = DefaultMaxSpeed;
    private bool _parametersKnown;
    private int _pollTicks;

    // The protocol has no pause, so a paused belt is held at speed 0 and looks stopped. This keeps it reported as
    // paused, so the workout is not ended, until it is started, stopped or given a speed. Commands set it while
    // notifications read it.
    private volatile bool _paused;

    protected override IReadOnlyList<SerialChannel> Channels { get; } = [Channel];

    protected override TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(500);

    protected override async Task OnConnectedAsync()
    {
        PublishConfiguration();
        await WriteAsync(KingSmithProtocol.QueryParameters());
    }

    protected override void OnDisconnected()
    {
        _frameReader.Reset();
        _status = null;
        _maxSpeed = DefaultMaxSpeed;
        _parametersKnown = false;
        _pollTicks = 0;
        _paused = false;
    }

    protected override async Task PollAsync()
    {
        if (!_parametersKnown && ++_pollTicks % ParameterQueryEveryTicks == 0)
            await WriteAsync(KingSmithProtocol.QueryParameters());
        await WriteAsync(KingSmithProtocol.QueryStatus());
    }

    public override async Task StartAsync()
    {
        if (_status?.Mode == KingSmithMode.Standby)
        {
            await WriteAsync(KingSmithProtocol.SetMode(KingSmithMode.Manual));
            if (!await WaitForModeAsync(KingSmithMode.Manual))
            {
                Logger.LogWarning("KingSmith treadmill did not leave standby; start not sent");
                return;
            }
        }

        if (_status?.BeltState != KingSmithBeltState.Running && !await WriteAsync(KingSmithProtocol.StartOrPause()))
            return;
        _paused = false;
    }

    /// <summary>Stops a moving belt by setting its speed to 0; Start sets it running again.</summary>
    public override async Task PauseAsync()
    {
        if (_status is not { } status || (status.BeltState != KingSmithBeltState.Running && status.SpeedKph == 0))
            return;

        if (await WriteAsync(KingSmithProtocol.StopBelt()))
        {
            _paused = true;
            RaiseState(TreadmillState.Paused);
        }
    }

    public override Task StopAsync()
    {
        _paused = false;
        return WriteAsync(KingSmithProtocol.SetMode(KingSmithMode.Standby));
    }

    public override async Task SetSpeedAsync(decimal speed)
    {
        // A speed sets the belt moving again.
        _paused = false;
        var clamped = Math.Clamp(speed, MinSpeed, _maxSpeed);
        Logger.LogInformation("Setting KingSmith speed: {Speed:F1} km/h", clamped);
        await WriteAsync(KingSmithProtocol.SetSpeed(clamped));
    }

    // Incline is not part of the legacy protocol.
    public override Task SetInclineAsync(decimal incline) => Task.CompletedTask;

    public override Task GetTreadmillConfigurationAsync()
    {
        PublishConfiguration();
        return Task.CompletedTask;
    }

    private void PublishConfiguration() =>
        RaiseConfiguration(new TreadmillConfiguration
        {
            MinSpeed = MinSpeed,
            MaxSpeed = _maxSpeed,
            SpeedIncrement = SpeedIncrement
        });

    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var status = KingSmithProtocol.ParseStatus(data);
        if (status == null)
            return new TreadmillTelemetry();

        return Telemetry(
            (TreadmillTelemetryProperty.InstantaneousSpeed, status.SpeedKph),
            (TreadmillTelemetryProperty.InstantaneousPace, PaceFromSpeed(status.SpeedKph)),
            (TreadmillTelemetryProperty.TotalDistance, status.DistanceMeters),
            (TreadmillTelemetryProperty.ElapsedTime, status.ElapsedSeconds),
            (TreadmillTelemetryProperty.StepCount, status.Steps));
    }

    protected override void OnNotification(byte[] chunk)
    {
        foreach (var frame in _frameReader.Append(chunk))
        {
            switch (frame[1])
            {
                case KingSmithProtocol.StatusCommand:
                    HandleStatus(frame);
                    break;
                case KingSmithProtocol.ParameterCommand:
                    HandleParameters(frame);
                    break;
            }
        }
    }

    private void HandleStatus(byte[] frame)
    {
        var status = KingSmithProtocol.ParseStatus(frame);
        if (status == null)
            return;

        _status = status;
        RaiseData(TranslateData(frame));
        RaiseState(_paused ? TreadmillState.Paused : status.BeltState switch
        {
            KingSmithBeltState.Running => TreadmillState.Running,
            KingSmithBeltState.Paused => TreadmillState.Paused,
            KingSmithBeltState.Ready or KingSmithBeltState.Stopped or KingSmithBeltState.Sleep => TreadmillState.Stopped,
            _ => TreadmillState.Unknown
        });
    }

    private void HandleParameters(byte[] frame)
    {
        _parametersKnown = true;
        var maxSpeed = KingSmithProtocol.ParseMaxSpeed(frame);
        if (maxSpeed == null || maxSpeed == _maxSpeed)
            return;

        Logger.LogInformation("KingSmith reported max speed {MaxSpeed:F1} km/h", maxSpeed);
        _maxSpeed = maxSpeed.Value;
        PublishConfiguration();
    }

    private async Task<bool> WaitForModeAsync(KingSmithMode mode)
    {
        var deadline = DateTime.UtcNow + ModeChangeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_status?.Mode == mode)
                return true;
            await Task.Delay(PollInterval);
        }
        return false;
    }
}
