using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.Rsc, "Running Speed & Cadence", "Treadmills and footpods that report speed and distance (no control)", Experimental = true,
    AdvertisedServices = ["00001814-0000-1000-8000-00805f9b34fb"])]
public class RscTreadmillService(ILogger<RscTreadmillService> logger, AppStateService appState)
    : TreadmillService(appState, new WindowsBluetoothClient(logger))
{
    private static readonly Guid RscService = Guid.Parse("00001814-0000-1000-8000-00805f9b34fb");
    private static readonly Guid RscMeasurement = Guid.Parse("00002a53-0000-1000-8000-00805f9b34fb");

    private const byte StrideLengthPresent = 0x01;
    private const byte TotalDistancePresent = 0x02;
    private const int MinimumLength = 4;

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!await Client.ConnectAsync(RscService, deviceName, cancellationToken))
        {
            logger.LogError("Failed to connect to Running Speed and Cadence service");
            return false;
        }

        if (!await Client.SubscribeAsync(RscMeasurement, OnMeasurement))
        {
            logger.LogError("Failed to subscribe to RSC Measurement");
            return false;
        }

        return true;
    }

    // RSC has no belt state, so a moving sensor is reported as running.
    private void OnMeasurement(byte[] data)
    {
        if (data.Length < MinimumLength)
            return;

        var telemetry = TranslateData(data);
        RaiseData(telemetry);
        RaiseState(telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value > 0
            ? TreadmillState.Running
            : TreadmillState.Stopped);
    }

    public override Task DisconnectAsync() => Client.DisconnectAsync();

    // RSC is a sensor profile with no control point.
    public override Task RequestControlAsync() => Task.CompletedTask;
    public override Task StartAsync() => Task.CompletedTask;
    public override Task PauseAsync() => Task.CompletedTask;
    public override Task StopAsync() => Task.CompletedTask;
    public override Task SetSpeedAsync(decimal speed) => Task.CompletedTask;
    public override Task SetInclineAsync(decimal incline) => Task.CompletedTask;
    public override Task SendCommandAsync(byte[] command) => Task.CompletedTask;
    public override Task GetTreadmillConfigurationAsync() => Task.CompletedTask;

    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var td = new TreadmillTelemetry();
        if (data.Length < MinimumLength)
            return td;

        // Instantaneous speed is uint16 in 1/256 m/s.
        var speed = Math.Round((data[1] | (data[2] << 8)) / 256m * 3.6m, 2);
        Set(td, TreadmillTelemetryProperty.InstantaneousSpeed, speed);
        Set(td, TreadmillTelemetryProperty.InstantaneousPace, speed > 0 ? 3600m / speed : 0);

        var flags = data[0];
        var offset = MinimumLength;
        if ((flags & StrideLengthPresent) != 0)
            offset += 2;

        // Total distance is uint32 in 1/10 m.
        if ((flags & TotalDistancePresent) != 0 && data.Length >= offset + 4)
            Set(td, TreadmillTelemetryProperty.TotalDistance, BitConverter.ToUInt32(data, offset) / 10m);

        return td;
    }

    private static void Set(TreadmillTelemetry td, TreadmillTelemetryProperty property, decimal value)
    {
        td.Values[property].Enabled = true;
        td.Values[property].Value = value;
    }
}
