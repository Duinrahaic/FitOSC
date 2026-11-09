using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.Bowflex, "Bowflex", "Bowflex treadmills (speed and incline, no control)", Experimental = true,
    AdvertisedServices = ["b5c78780-cad7-11e5-b9f8-0002a5d5c51b", "edff9e80-cad7-11e5-ab63-0002a5d5c51b"])]
public class BowflexTreadmillService(ILogger<BowflexTreadmillService> logger, AppStateService appState,
    BaseBluetoothClient client) : TreadmillService(appState, client)
{
    // Zwift writes to the first service on models whose name contains "116" or "226", and the second otherwise.
    private static readonly Guid[] Services =
    [
        Guid.Parse("b5c78780-cad7-11e5-b9f8-0002a5d5c51b"),
        Guid.Parse("edff9e80-cad7-11e5-ab63-0002a5d5c51b")
    ];

    private static readonly Guid[] StatusCharacteristics =
    [
        Guid.Parse("4e349c00-999e-11e3-b341-0002a5d5c51b"),
        Guid.Parse("e3f9af20-2674-11e3-879e-0002a5d5c51b")
    ];

    private static readonly Guid StreamStartCharacteristic = Guid.Parse("6be8f580-9803-11e3-ab03-0002a5d5c51b");

    private static readonly Guid[] StreamContinuationCharacteristics =
    [
        Guid.Parse("a46a4a80-9803-11e3-8f3c-0002a5d5c51b"),
        Guid.Parse("b8066ec0-9803-11e3-8346-0002a5d5c51b"),
        Guid.Parse("d57cda20-9803-11e3-8426-0002a5d5c51b")
    ];

    private static readonly Guid CommandCharacteristic = Guid.Parse("1717b3c0-9803-11e3-90e1-0002a5d5c51b");

    private static readonly byte[] StartStreamingCommand = [0x07, 0x00, 0xD0, 0x00, 0x1F, 0x0A, 0x00];

    private const byte SpeedTag = (byte)'a';
    private const byte InclineTag = (byte)'b';
    private const byte StateTag = (byte)'|';
    private const byte BeltStopped = 0x04;

    // Same divisor as Zwift: 0.01 mph -> km/h.
    private const decimal HundredthsMphPerKph = 62.14m;

    // First stream packet: data starts at byte 6 and is read up to byte 19; continuations carry data in bytes 1-19.
    private const int StartPacketDataOffset = 6;
    private const int StartPacketLastIndex = 19;
    private const int ContinuationLastIndex = 19;
    private const int RecordLengthBias = 5;

    // Value length per tag, from Zwift's table; tags at or above the last entry use the last entry.
    private static readonly byte[] TagValueLengths =
    [
        0x01, 0x01, 0x04, 0x04, 0x02, 0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x14, 0x14, 0x02,
        0x02, 0x02, 0x02, 0x02, 0x04, 0x02, 0x02, 0x02, 0x02, 0x01, 0x14, 0x08, 0x02, 0x01, 0x02, 0x04,
        0x04, 0x01, 0x01, 0x01, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x01, 0x02, 0x02, 0x14,
        0x02, 0x01, 0x02, 0x02, 0x04, 0x01, 0x04, 0x04, 0x01, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x01,
        0x01, 0x02, 0x01, 0x02, 0x01, 0x01, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01, 0x02, 0x02, 0x01, 0x02,
        0x02, 0x01, 0x01, 0x01, 0x02, 0x01, 0x02, 0x02, 0x01, 0x04, 0x01, 0x02, 0x01, 0x01, 0x02, 0x01,
        0x02, 0x02, 0x01, 0x02, 0x01, 0x01, 0x04, 0x02, 0x02, 0x02, 0x01, 0x01, 0x01, 0x02, 0x02, 0x02,
        0x02, 0x01, 0x02, 0x01, 0x02, 0x02, 0x04, 0x01, 0x01, 0x02, 0x02, 0x01, 0x01, 0x02, 0x02, 0x01,
        0x01, 0x01, 0x01, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
    ];

    private readonly object _streamLock = new();
    private byte[] _record = [];
    private int _recordFill;
    private byte _sequence;
    private bool _streamingRequested;
    private decimal _speed;
    private decimal _incline;

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!await ConnectToServiceAsync(deviceName, cancellationToken))
        {
            logger.LogError("No Bowflex treadmill service found on {Device}", deviceName);
            return false;
        }

        if (!await Client.SubscribeAsync(StreamStartCharacteristic, OnStreamStart))
        {
            logger.LogError("Failed to subscribe to Bowflex stream characteristic");
            return false;
        }

        foreach (var characteristic in StreamContinuationCharacteristics)
            await Client.SubscribeAsync(characteristic, OnStreamContinuation);

        var hasStatus = false;
        foreach (var characteristic in StatusCharacteristics)
            hasStatus |= await Client.SubscribeAsync(characteristic, OnStatus);

        if (!hasStatus)
        {
            logger.LogError("Failed to subscribe to a Bowflex status characteristic; streaming cannot start");
            return false;
        }

        return true;
    }

    private async Task<bool> ConnectToServiceAsync(string deviceName, CancellationToken cancellationToken)
    {
        foreach (var service in Services)
        {
            if (await Client.ConnectAsync(service, deviceName, cancellationToken))
                return true;
        }
        return false;
    }

    public override async Task DisconnectAsync()
    {
        lock (_streamLock)
        {
            _record = [];
            _recordFill = 0;
            _streamingRequested = false;
            _speed = 0;
            _incline = 0;
        }
        await Client.DisconnectAsync();
    }

    // Zwift starts the stream once, after the first status notification.
    private void OnStatus(byte[] data)
    {
        lock (_streamLock)
        {
            if (_streamingRequested)
                return;
            _streamingRequested = true;
        }

        logger.LogInformation("Starting Bowflex data stream");
        _ = Client.WriteAsync(CommandCharacteristic, StartStreamingCommand);
    }

    private void OnStreamStart(byte[] packet)
    {
        if (packet.Length < 2)
            return;

        byte[]? complete;
        lock (_streamLock)
        {
            _sequence = packet[0];
            _record = new byte[Math.Max(packet[1] - RecordLengthBias, 0)];
            _recordFill = 0;
            complete = Append(packet, StartPacketDataOffset, StartPacketLastIndex);
        }

        if (complete != null)
            HandleRecord(complete);
    }

    private void OnStreamContinuation(byte[] packet)
    {
        if (packet.Length == 0)
            return;

        byte[]? complete;
        lock (_streamLock)
        {
            if (packet[0] != _sequence || _record.Length == 0)
                return;
            complete = Append(packet, 1, ContinuationLastIndex);
        }

        if (complete != null)
            HandleRecord(complete);
    }

    /// <summary>Appends packet bytes to the record; returns the record once it is full.</summary>
    private byte[]? Append(byte[] packet, int start, int lastIndex)
    {
        for (var i = start; i <= lastIndex && i < packet.Length && _recordFill < _record.Length; i++)
            _record[_recordFill++] = packet[i];

        if (_record.Length == 0 || _recordFill < _record.Length)
            return null;

        var complete = _record;
        _record = [];
        return complete;
    }

    private void HandleRecord(byte[] record)
    {
        TreadmillTelemetry telemetry;
        bool running;
        lock (_streamLock)
        {
            telemetry = TranslateData(record);
            running = _speed > 0;
        }

        RaiseData(telemetry);
        RaiseState(running ? TreadmillState.Running : TreadmillState.Stopped);
    }

    /// <summary>Parses a reassembled stream record.</summary>
    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var tag = data[i];
            switch (tag)
            {
                case SpeedTag when i + 2 < data.Length:
                    _speed = Math.Round(BitConverter.ToUInt16(data, i + 1) / HundredthsMphPerKph, 2);
                    i += 3;
                    break;
                case InclineTag when i + 1 < data.Length:
                    _incline = data[i + 1];
                    i += 2;
                    break;
                case StateTag when i + 1 < data.Length:
                    if (data[i + 1] == BeltStopped)
                        _speed = 0;
                    i += 2;
                    break;
                case SpeedTag or InclineTag or StateTag:
                    i = data.Length;
                    break;
                default:
                    i += TagValueLengths[Math.Min(tag, TagValueLengths.Length - 1)] + 1;
                    break;
            }
        }

        var td = new TreadmillTelemetry();
        Set(td, TreadmillTelemetryProperty.InstantaneousSpeed, _speed);
        Set(td, TreadmillTelemetryProperty.InstantaneousPace, _speed > 0 ? 3600m / _speed : 0);
        Set(td, TreadmillTelemetryProperty.Incline, _incline);
        return td;
    }

    // Zwift only reads from Bowflex treadmills; there is no control protocol.
    public override Task RequestControlAsync() => Task.CompletedTask;
    public override Task StartAsync() => Task.CompletedTask;
    public override Task PauseAsync() => Task.CompletedTask;
    public override Task StopAsync() => Task.CompletedTask;
    public override Task SetSpeedAsync(decimal speed) => Task.CompletedTask;
    public override Task SetInclineAsync(decimal incline) => Task.CompletedTask;
    public override Task SendCommandAsync(byte[] command) => Client.WriteAsync(CommandCharacteristic, command);
    public override Task GetTreadmillConfigurationAsync() => Task.CompletedTask;

    private static void Set(TreadmillTelemetry td, TreadmillTelemetryProperty property, decimal value)
    {
        td.Values[property].Enabled = true;
        td.Values[property].Value = value;
    }
}
