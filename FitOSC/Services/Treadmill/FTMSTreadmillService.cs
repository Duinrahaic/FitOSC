using System.Buffers.Binary;
using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Utilities;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

[TreadmillDriver(TreadmillType.FTMS, "FTMS (Generic)", "Most Bluetooth treadmills use this standard",
    AdvertisedServices = ["00001826-0000-1000-8000-00805f9b34fb"])]
public class FTMSTreadmillService(ILogger logger, AppStateService appState)
    : TreadmillService(appState, new WindowsBluetoothClient(logger))
{
    private static readonly Guid FtmsService = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    protected static readonly Guid ControlPoint = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");
    private static readonly Guid TreadmillData = Guid.Parse("00002acd-0000-1000-8000-00805f9b34fb");
    private static readonly Guid StatusPoint = Guid.Parse("00002ada-0000-1000-8000-00805f9b34fb");
    private static readonly Guid SupportedSpeed = Guid.Parse("00002ad4-0000-1000-8000-00805f9b34fb");
    private static readonly Guid SupportedIncline = Guid.Parse("00002ad5-0000-1000-8000-00805f9b34fb");

    // Control Point response: 0x80, request opcode, result code (0x01 = success).
    private const byte ResponseCode = 0x80;
    private const byte ResultSuccess = 0x01;
    private const byte RequestControlOpcode = 0x00;

    // Total Energy of 0xFFFF means the machine does not know it.
    private const ushort EnergyNotAvailable = 0xFFFF;

    // Inclination, Ramp Angle Setting, Force on Belt and Power Output of 0x7FFF mean 'Data Not Available'.
    private const short SignedNotAvailable = 0x7FFF;

    private const byte SetTargetSpeedOpcode = 0x02;
    private const byte SetTargetInclineOpcode = 0x03;

    // How long a Control Point procedure waits for the machine's response before the next one may start.
    private static readonly TimeSpan ControlPointTimeout = TimeSpan.FromSeconds(3);

    // One Control Point procedure at a time; each completes with the response to its own opcode.
    private readonly SemaphoreSlim _controlPointGate = new(1, 1);
    private TaskCompletionSource<byte>? _pendingResponse;
    private byte _pendingOpcode;

    // Set once a procedure goes unanswered: the machine does not respond, so later procedures do not wait for it, and
    // a late answer cannot be mistaken for the response to the next request.
    private volatile bool _responsesUnanswered;

    // Cancelled when the connection ends, releasing queued and in-flight procedures.
    private readonly CancellationTokenSource _closing = new();

    // Only the newest speed and incline target is sent; one that is replaced while waiting is dropped.
    private readonly Dictionary<byte, byte[]> _pendingTargets = new();
    private bool _sendingTargets;

    // Fitness Machine Status opcodes.
    private const byte StatusReset = 0x01;
    private const byte StatusStoppedOrPaused = 0x02;
    private const byte StatusStoppedBySafetyKey = 0x03;
    private const byte StatusStartedOrResumed = 0x04;
    private const byte PauseParameter = 0x02;

    private TreadmillConfiguration _configuration = new();

    // The last full reading; packets split with the More Data flag only carry some fields.
    private TreadmillTelemetry? _lastTelemetry;

    // The last stop or pause the machine reported, so a stationary belt is not guessed from elapsed time.
    private TreadmillState? _machineState;

    // Consoles on the EQiSports app's firmware, recognised by their step count in flag bit 13, roll steps over at
    // 10,000 and elapsed time at 100 minutes; the app unwraps both.
    private readonly RolloverCounter _steps = new(10000);
    private readonly RolloverCounter _elapsedSeconds = new(6000);

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!await Client.ConnectAsync(FtmsService, deviceName, cancellationToken))
        {
            logger.LogError("Failed to connect to FTMS service");
            return false;
        }

        // Subscribe to critical characteristics. The Control Point must be indicating before it accepts commands.
        bool controlPointOk = await Client.SubscribeAsync(ControlPoint, HandleControlPointResponse, cancellationToken);
        bool treadmillDataOk = await Client.SubscribeAsync(TreadmillData, HandleTelemetryData, cancellationToken);

        if (!controlPointOk || !treadmillDataOk)
        {
            logger.LogError("Failed to subscribe to critical characteristics. Control Point: {CP}, Treadmill Data: {TD}",
                controlPointOk, treadmillDataOk);
            return false;
        }

        // Subscribe to optional characteristics
        await Client.SubscribeAsync(StatusPoint, HandleStatus, cancellationToken);
        await Client.SubscribeAsync(SupportedSpeed, null, cancellationToken);
        await Client.SubscribeAsync(SupportedIncline, null, cancellationToken);

        await ReadConfigurationAsync(cancellationToken);
        return true;
    }

    public override async Task DisconnectAsync()
    {
        _closing.Cancel();
        _lastTelemetry = null;
        _machineState = null;
        _configuration = new TreadmillConfiguration();
        await Client.DisconnectAsync();
    }

    /// <summary>Requests control. A machine that refuses it will refuse every command, so that fails the connection.</summary>
    public override async Task RequestControlAsync()
    {
        var result = await RunControlPointAsync(new byte[] { RequestControlOpcode });
        if (result is { } code && code != ResultSuccess)
            throw new InvalidOperationException($"The treadmill refused control (result {code:X2})");
    }

    /// <summary>Runs one Control Point procedure and returns whether the machine accepted it.</summary>
    protected async Task<bool> WriteControlPointAsync(byte[] payload) =>
        await RunControlPointAsync(payload) == ResultSuccess;

    /// <summary>
    /// Writes a Control Point request and waits for the machine's response to it, so procedures never overlap.
    /// Returns the result code, or null when the write failed, no response arrived in time, or the connection ended.
    /// A machine that does not respond is assumed to accept what it was sent.
    /// </summary>
    private async Task<byte?> RunControlPointAsync(byte[] payload)
    {
        var closing = _closing.Token;
        try
        {
            await _controlPointGate.WaitAsync(closing);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var response = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responsesUnanswered)
            {
                _pendingOpcode = payload[0];
                Volatile.Write(ref _pendingResponse, response);
            }

            // The Control Point only accepts acknowledged writes.
            if (!await Client.WriteAsync(ControlPoint, payload, withResponse: true, closing))
                return null;
            if (_responsesUnanswered)
                return ResultSuccess;

            try
            {
                var result = await response.Task.WaitAsync(ControlPointTimeout, closing);
                if (result != ResultSuccess)
                    logger.LogWarning("Treadmill rejected control point opcode {Opcode:X2} with result {Result:X2}", payload[0], result);
                return result;
            }
            catch (TimeoutException)
            {
                _responsesUnanswered = true;
                logger.LogWarning("Treadmill did not answer control point opcode {Opcode:X2}; no longer waiting for answers",
                    payload[0]);
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            Volatile.Write(ref _pendingResponse, null);
            _controlPointGate.Release();
        }
    }

    /// <summary>
    /// Sends a speed or incline target. While one is being sent, newer targets replace any still waiting, so a burst of
    /// changes ends at the latest value instead of queuing every step.
    /// </summary>
    private async Task SetTargetAsync(byte[] payload)
    {
        lock (_pendingTargets)
        {
            _pendingTargets[payload[0]] = payload;
            if (_sendingTargets)
                return;
            _sendingTargets = true;
        }

        while (true)
        {
            byte[] next;
            lock (_pendingTargets)
            {
                if (_pendingTargets.Count == 0)
                {
                    _sendingTargets = false;
                    return;
                }

                next = _pendingTargets.Values.First();
                _pendingTargets.Remove(next[0]);
            }

            await RunControlPointAsync(next);
        }
    }

    /// <summary>Drops targets that have not been sent; stopping or pausing supersedes them.</summary>
    private void ClearPendingTargets()
    {
        lock (_pendingTargets)
            _pendingTargets.Clear();
    }

    public override async Task StartAsync() =>
        await WriteControlPointAsync(new byte[] { 0x07 });

    public override async Task StopAsync()
    {
        ClearPendingTargets();
        await WriteControlPointAsync(new byte[] { 0x08, 0x01 });
    }

    public override async Task PauseAsync()
    {
        ClearPendingTargets();
        await WriteControlPointAsync(new byte[] { 0x08, 0x02 });
    }

    public override async Task SetSpeedAsync(decimal speed)
    {
        var clamped = Clamp(speed, _configuration.MinSpeed, _configuration.MaxSpeed, 0, ushort.MaxValue / 100m);
        logger.LogInformation("Setting speed: {Speed:F2} km/h ({SpeedMph:F2} mph)",
            clamped, clamped.ConvertKphToMph());

        ushort raw = (ushort)Math.Round(clamped * 100, MidpointRounding.AwayFromZero);
        var payload = new byte[]
        {
            SetTargetSpeedOpcode,
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF)
        };

        await SetTargetAsync(payload);
    }

    public override async Task SetInclineAsync(decimal incline)
    {
        var clamped = Clamp(incline, _configuration.MinIncline, _configuration.MaxIncline,
            short.MinValue / 10m, short.MaxValue / 10m);
        short raw = (short)Math.Round(clamped * 10, MidpointRounding.AwayFromZero); // FTMS uses 0.1% increments
        var payload = new byte[]
        {
            SetTargetInclineOpcode,
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF)
        };
        await SetTargetAsync(payload);
    }

    /// <summary>Clamps to the treadmill's supported range when it reported one, and always to what the field can encode.</summary>
    private static decimal Clamp(decimal value, decimal min, decimal max, decimal fieldMin, decimal fieldMax)
    {
        if (max > min)
            value = Math.Clamp(value, min, max);
        return Math.Clamp(value, fieldMin, fieldMax);
    }

    public override async Task SendCommandAsync(byte[] command) =>
        await WriteControlPointAsync(command);

    public override Task GetTreadmillConfigurationAsync() => ReadConfigurationAsync(CancellationToken.None);

    private async Task ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        var (minSpeed, maxSpeed, stepSpeed) = await ReadSupportedSpeedRangeAsync(cancellationToken);
        var (minIncline, maxIncline, stepIncline) = await ReadSupportedInclineRangeAsync(cancellationToken);
        _configuration = new TreadmillConfiguration
        {
            MinSpeed = minSpeed,
            MaxSpeed = maxSpeed,
            SpeedIncrement = stepSpeed,
            MinIncline = minIncline,
            MaxIncline = maxIncline,
            InclineIncrement = stepIncline,
            Commands = TreadmillCommands.All
        };
        RaiseConfiguration(_configuration);
    }

    /// <summary>
    /// Reads the treadmill's supported inclination range from the FTMS characteristic (0x2AD5).
    /// Returns the minimum, maximum, and step in percent (%).
    /// </summary>
    private async Task<(decimal Min, decimal Max, decimal Step)> ReadSupportedInclineRangeAsync(CancellationToken cancellationToken)
    {
        var data = await Client.ReadAsync(SupportedIncline, cancellationToken);
        if (data == null || data.Length < 6)
        {
            logger.LogWarning("Failed to read Supported Incline Range or data was invalid.");
            return (0, 0, 0);
        }

        // Min and max are sint16, the increment uint16, little endian, unit = 0.1%
        short minRaw = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0));
        short maxRaw = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2));
        ushort stepRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));

        // Convert from 0.1% to %
        decimal minPercent = Math.Round(minRaw / 10.0m, 1);
        decimal maxPercent = Math.Round(maxRaw / 10.0m, 1);
        decimal stepPercent = Math.Round(stepRaw / 10.0m, 1);

        logger.LogInformation(
            "Supported Incline Range: {Min:F1}%–{Max:F1}% (step {Step:F1}%)",
            minPercent, maxPercent, stepPercent
        );

        return (minPercent, maxPercent, stepPercent);
    }


    /// <summary>
    /// Reads the treadmill's supported speed range from the FTMS characteristic (0x2AD4).
    /// Returns the minimum, maximum, and increment in km/h.
    /// </summary>
    private async Task<(decimal Min, decimal Max, decimal Step)> ReadSupportedSpeedRangeAsync(CancellationToken cancellationToken)
    {
        var data = await Client.ReadAsync(SupportedSpeed, cancellationToken);
        if (data == null || data.Length < 6)
        {
            logger.LogWarning("Failed to read Supported Speed Range or data was invalid.");
            return (0, 0, 0);
        }

        // Log raw bytes received from treadmill
        logger.LogInformation("Raw FTMS Supported Speed data: [{Bytes}]", BitConverter.ToString(data));

        // FTMS uses little-endian uint16 fields, unit = 0.01 km/h
        decimal minKph = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0)) / 100m;
        decimal maxKph = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) / 100m;
        decimal stepKph = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) / 100m;

        logger.LogInformation(
            "Supported Speed Range: {MinKph:F2}–{MaxKph:F2} km/h ({MinMph:F2}–{MaxMph:F2} mph), step {StepKph:F2} km/h ({StepMph:F2} mph)",
            minKph, maxKph, minKph.ConvertKphToMph(), maxKph.ConvertKphToMph(), stepKph, stepKph.ConvertKphToMph()
        );

        return (minKph, maxKph, stepKph);
    }


    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var td = new TreadmillTelemetry();

        // Parse FTMS flags — contains both HasX and PosX values
        var flags = new FtmsTelemetryFlags(data);
        if (data.Length < flags.Length)
            throw new ArgumentException($"FTMS treadmill data is {data.Length} bytes but its flags need {flags.Length}.");

        // Instantaneous Speed (uint16, 0.01 km/h; absent when More Data is set)
        if (flags.HasInsSpeed)
        {
            td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled = true;
            td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value =
                BitConverter.ToUInt16(data, flags.PosInsSpeed) / 100.0m;
        }

        // Average Speed (uint16, 0.01 km/h)
        if (flags.HasAvgSpeed)
        {
            td.Values[TreadmillTelemetryProperty.AverageSpeed].Enabled = true;
            td.Values[TreadmillTelemetryProperty.AverageSpeed].Value =
                BitConverter.ToUInt16(data, flags.PosAvgSpeed) / 100.0m;
        }

        // Total Distance (uint24, meters)
        if (flags.HasTotDistance)
        {
            int distance = BitConverter.ToUInt16(data, flags.PosTotDistance);
            distance += data[flags.PosTotDistance + 2] << 16;
            td.Values[TreadmillTelemetryProperty.TotalDistance].Enabled = true;
            td.Values[TreadmillTelemetryProperty.TotalDistance].Value = distance;
        }

        // Incline (sint16, 0.1%) + Ramp Angle (sint16, 0.1°)
        if (flags.HasInclination)
        {
            SetSigned(td, TreadmillTelemetryProperty.Incline, BitConverter.ToInt16(data, flags.PosInclination), 10.0m);
            SetSigned(td, TreadmillTelemetryProperty.RampAngle, BitConverter.ToInt16(data, flags.PosInclination + 2), 10.0m);
        }

        // Positive Elevation Gain (uint16, 0.1 m), followed by Negative Elevation Gain
        if (flags.HasElevGain)
        {
            td.Values[TreadmillTelemetryProperty.ElevationGain].Enabled = true;
            td.Values[TreadmillTelemetryProperty.ElevationGain].Value =
                BitConverter.ToUInt16(data, flags.PosElevGain) / 10.0m;
        }

        // Instantaneous Pace (uint16, seconds per 500 m), published as seconds per km
        if (flags.HasInsPace)
        {
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Enabled = true;
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Value =
                BitConverter.ToUInt16(data, flags.PosInsPace) * 2m;
        }
        else if (td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled)
        {
            var speedKph = td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value;
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Enabled = true;

            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Value =
                speedKph > 0 ? 3600m / speedKph : 0;
        }

        // Average Pace (uint16, seconds per 500 m), published as seconds per km
        if (flags.HasAvgPace)
        {
            td.Values[TreadmillTelemetryProperty.AveragePace].Enabled = true;
            td.Values[TreadmillTelemetryProperty.AveragePace].Value =
                BitConverter.ToUInt16(data, flags.PosAvgPace) * 2m;
        }

        // Total Energy (uint16, kcal)
        if (flags.HasKcal && BitConverter.ToUInt16(data, flags.PosKcal) is var kcal and not EnergyNotAvailable)
        {
            td.Values[TreadmillTelemetryProperty.Calories].Enabled = true;
            td.Values[TreadmillTelemetryProperty.Calories].Value = kcal;
        }

        // Heart Rate (uint8, bpm)
        if (flags.HasHR)
        {
            td.Values[TreadmillTelemetryProperty.HeartRate].Enabled = true;
            td.Values[TreadmillTelemetryProperty.HeartRate].Value = data[flags.PosHR];
        }

        // MET (uint8, 0.1 MET as in every other FTMS machine data characteristic)
        if (flags.HasMET)
        {
            td.Values[TreadmillTelemetryProperty.MetabolicEquivalent].Enabled = true;
            td.Values[TreadmillTelemetryProperty.MetabolicEquivalent].Value = data[flags.PosMET] / 10.0m;
        }

        // Elapsed Time (s)
        if (flags.HasElapsedTime)
        {
            td.Values[TreadmillTelemetryProperty.ElapsedTime].Enabled = true;
            td.Values[TreadmillTelemetryProperty.ElapsedTime].Value =
                BitConverter.ToUInt16(data, flags.PosElapsedTime);
        }

        // Remaining Time (s)
        if (flags.HasRemainingTime)
        {
            td.Values[TreadmillTelemetryProperty.RemainingTime].Enabled = true;
            td.Values[TreadmillTelemetryProperty.RemainingTime].Value =
                BitConverter.ToUInt16(data, flags.PosRemainingTime);
        }

        // Force on Belt (sint16, N) + Power Output (sint16, W)
        if (flags.HasForceBelt)
        {
            SetSigned(td, TreadmillTelemetryProperty.ForceOnBelt, BitConverter.ToInt16(data, flags.PosForceBelt), 1m);
            SetSigned(td, TreadmillTelemetryProperty.Power, BitConverter.ToInt16(data, flags.PosForceBelt + 2), 1m);
        }

        // Step Count (uint24), a vendor extension in reserved flag bit 13
        if (flags.HasSteps)
        {
            td.Values[TreadmillTelemetryProperty.StepCount].Enabled = true;
            td.Values[TreadmillTelemetryProperty.StepCount].Value =
                BitConverter.ToUInt16(data, flags.PosSteps) + (data[flags.PosSteps + 2] << 16);
        }

        return td;
    }

    /// <summary>Publishes a signed field unless the machine sent it as 'Data Not Available'.</summary>
    private static void SetSigned(TreadmillTelemetry td, TreadmillTelemetryProperty property, short raw, decimal divisor)
    {
        if (raw == SignedNotAvailable)
            return;

        td.Values[property].Enabled = true;
        td.Values[property].Value = raw / divisor;
    }

    /// <summary>Whether the packet carried the property's field, even if only as 'Data Not Available'.</summary>
    private static bool Carries(FtmsTelemetryFlags flags, TreadmillTelemetryProperty property) => property switch
    {
        TreadmillTelemetryProperty.Calories => flags.HasKcal,
        TreadmillTelemetryProperty.Incline or TreadmillTelemetryProperty.RampAngle => flags.HasInclination,
        TreadmillTelemetryProperty.ForceOnBelt or TreadmillTelemetryProperty.Power => flags.HasForceBelt,
        _ => false
    };

    private void HandleTelemetryData(byte[] data)
    {
        TreadmillTelemetry telemetry;
        try
        {
            telemetry = TranslateData(data);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Dropped malformed FTMS treadmill data {Data}: {Reason}", Convert.ToHexString(data), ex.Message);
            return;
        }

        // Only a packet that carries speed is a fresh belt sample; carried-over speed is not.
        var hasSpeed = telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled;

        var flags = new FtmsTelemetryFlags(data);
        if (flags.HasSteps)
        {
            Unwrap(telemetry, TreadmillTelemetryProperty.StepCount, _steps);
            if (flags.HasElapsedTime)
                Unwrap(telemetry, TreadmillTelemetryProperty.ElapsedTime, _elapsedSeconds);
        }

        // Carry over fields this packet did not include. One it reported as unknown is not replaced by an old value.
        if (_lastTelemetry != null)
        {
            foreach (var (property, last) in _lastTelemetry.Values)
            {
                var current = telemetry.Values[property];
                if (Carries(flags, property))
                    continue;
                if (last.Enabled && !current.Enabled)
                {
                    current.Enabled = true;
                    current.Value = last.Value;
                }
            }
        }
        _lastTelemetry = telemetry;
        RaiseData(telemetry);

        if (!hasSpeed)
            return;

        // A moving belt is running. A stationary one keeps the machine's own stop or pause; failing that, it is paused
        // part-way through a session and stopped otherwise.
        var elapsed = telemetry.Values[TreadmillTelemetryProperty.ElapsedTime];
        var state = telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value > 0 ? TreadmillState.Running
            : _machineState ?? (elapsed.Enabled && elapsed.Value > 0 ? TreadmillState.Paused : TreadmillState.Stopped);
        if (state == TreadmillState.Stopped)
            MarkCountersStopped();
        RaiseState(state);
    }

    private static void Unwrap(TreadmillTelemetry telemetry, TreadmillTelemetryProperty property, RolloverCounter counter)
    {
        var value = telemetry.Values[property];
        if (value.Enabled)
            value.Value = counter.Update((int)value.Value);
    }

    private void MarkCountersStopped()
    {
        _steps.MarkStopped();
        _elapsedSeconds.MarkStopped();
    }

    private void HandleStatus(byte[] data)
    {
        if (data.Length == 0)
            return;

        var state = data[0] switch
        {
            StatusReset or StatusStoppedBySafetyKey => TreadmillState.Stopped,
            StatusStoppedOrPaused when data.Length >= 2 =>
                data[1] == PauseParameter ? TreadmillState.Paused : TreadmillState.Stopped,
            StatusStartedOrResumed => TreadmillState.Running,
            _ => (TreadmillState?)null
        };
        if (state == null)
            return;

        _machineState = state == TreadmillState.Running ? null : state;
        if (state == TreadmillState.Stopped)
            MarkCountersStopped();
        RaiseState(state.Value);
    }

    private void HandleControlPointResponse(byte[] data)
    {
        if (data.Length < 3 || data[0] != ResponseCode)
            return;

        var pending = Volatile.Read(ref _pendingResponse);
        if (pending != null && data[1] == _pendingOpcode)
            pending.TrySetResult(data[2]);
        else if (data[2] != ResultSuccess)
            logger.LogWarning("Treadmill rejected control point opcode {Opcode:X2} with result {Result:X2}", data[1], data[2]);
    }

    /// <summary>
    /// A console counter that rolls over at <paramref name="wrap"/>. A drop from the top tenth of the range, with no stop
    /// in between, is a rollover and adds one period; any other drop is a new workout counting from zero.
    /// </summary>
    private sealed class RolloverCounter(int wrap)
    {
        private int _last;
        private int _offset;
        private volatile bool _stopped;

        public int Update(int raw)
        {
            if (raw < _last)
            {
                _offset = !_stopped && _last >= wrap - wrap / 10 ? _offset + wrap : 0;
                _stopped = false;
            }

            _last = raw;
            return _offset + raw;
        }

        public void MarkStopped() => _stopped = true;
    }
}
