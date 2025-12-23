using System.Buffers.Binary;
using FitOSC.Models;
using FitOSC.Pages.Components;
using FitOSC.Services.State;
using FitOSC.Utilities;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

public class FTMSTreadmillService(ILogger<FTMSTreadmillService> logger, AppStateService appState) 
    : TreadmillService(appState, new WindowsBluetoothClient(logger))
{
    private static readonly Guid FtmsService     = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ControlPoint    = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");
    private static readonly Guid TreadmillData   = Guid.Parse("00002acd-0000-1000-8000-00805f9b34fb");
    private static readonly Guid StatusPoint     = Guid.Parse("00002ada-0000-1000-8000-00805f9b34fb");
    private static readonly Guid SupportedSpeed  = Guid.Parse("00002ad4-0000-1000-8000-00805f9b34fb");  
    private static readonly Guid SupportedIncline= Guid.Parse("00002ad5-0000-1000-8000-00805f9b34fb");  

    public override async Task ConnectAsync(string deviceName)
    {
        if (!await Client.ConnectAsync(FtmsService, deviceName))
            return;
        await Client.SubscribeAsync(ControlPoint, null); 
        await Client.SubscribeAsync(TreadmillData, d => RaiseData(TranslateData(d)));
        await Client.SubscribeAsync(StatusPoint, HandleStatus);
        await Client.SubscribeAsync(SupportedSpeed, null);
        await Client.SubscribeAsync(SupportedIncline, null);
        await GetTreadmillConfigurationAsync();
        logger.LogInformation($"Buffering before sending control request...");
        await RequestControlAsync();
    }

    public override async Task DisconnectAsync() => await Client.DisconnectAsync();

    public override async Task RequestControlAsync()
    {
        await Client.WriteAsync(ControlPoint, new byte[] { 0x00 });
    }
    
    public override async Task StartAsync() =>
        await Client.WriteAsync(ControlPoint, new byte[] { 0x07 });

    public override async Task StopAsync() =>
        await Client.WriteAsync(ControlPoint, new byte[] { 0x08, 0x01 });

    public override async Task PauseAsync() =>
        await Client.WriteAsync(ControlPoint, new byte[] { 0x08, 0x02 });

    public override async Task SetSpeedAsync(decimal speed)
    {
        Console.WriteLine($"Speed: {speed} km/h ({speed.ConvertKphToMph()} mph)");
        ushort raw = (ushort)Math.Round(speed * 100, MidpointRounding.AwayFromZero);
        var payload = new byte[]
        {
            0x02, // Set Target Speed
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF)
        };
        PayloadToSpeed(payload);
        await Client.WriteAsync(ControlPoint, payload);
    }

    private void PayloadToSpeed(byte[] payload)
    {
        if (payload.Length >= 3 && payload[0] == 0x02)
        {
            
            ushort raw = (ushort)(payload[1] | (payload[2] << 8));
            decimal speedKph = raw / 100m;
            decimal speedMph = speedKph.ConvertKphToMph();

            logger.LogInformation(
                "FTMS Payload: [{0}] → {1:F2} km/h ({2:F2} mph)",
                BitConverter.ToString(payload),
                speedKph,
                speedMph
            );
        }
        else
        {
            logger.LogWarning("Unexpected FTMS payload: [{0}]", BitConverter.ToString(payload));
        }
    }

    public override async Task SetInclineAsync(decimal incline)
    {
        short raw = (short)(incline * 10); // FTMS uses 0.1% increments
        var payload = new byte[]
        {
            0x03, // Set Incline
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF)
        };
        await Client.WriteAsync(ControlPoint, payload);
    }
    
    public override async Task SendCommandAsync(byte[] command) =>
        await Client.WriteAsync(ControlPoint, command);

    public override async Task GetTreadmillConfigurationAsync()
    {
        var (minSpeed, maxSpeed, stepSpeed) = await ReadSupportedSpeedRangeAsync();
        var (minIncline, maxIncline, stepIncline) = await ReadSupportedInclineRangeAsync();
        RaiseConfiguration(new()
        {
            MinSpeed = minSpeed,
            MaxSpeed = maxSpeed,
            SpeedIncrement = stepSpeed,
            MinIncline = minIncline,
            MaxIncline = maxIncline,
            InclineIncrement = stepIncline
        });
    }
    
    /// <summary>
    /// Reads the treadmill's supported inclination range from the FTMS characteristic (0x2AD5).
    /// Returns the minimum, maximum, and step in percent (%).
    /// </summary>
    private async Task<(decimal Min, decimal Max, decimal Step)> ReadSupportedInclineRangeAsync()
    {
 
        var data = await Client.ReadAsync(SupportedIncline);
        if (data == null || data.Length < 6)
        {
            logger.LogWarning("Failed to read Supported Incline Range or data was invalid.");
            return (0, 0, 0);
        }

        // Each field = int16, little endian, unit = 0.1%
        short minRaw  = (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0));
        short maxRaw  = (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        short stepRaw = (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));

        // Convert from 0.1% to %
        decimal minPercent  = Math.Round(minRaw / 10.0m, 1);
        decimal maxPercent  = Math.Round(maxRaw / 10.0m, 1);
        decimal stepPercent = Math.Round(stepRaw / 10.0m, 1);

        logger.LogInformation(
            "Supported Incline Range: {Min:F1}%–{Max:F1}% (step {Step:F1}%)",
            minPercent, maxPercent, stepPercent
        );

        return (minPercent, maxPercent, stepPercent);
    }

    
    /// <summary>
    /// Reads the treadmill's supported speed range from the FTMS characteristic.
    /// Returns the minimum, maximum, and increment in km/h.
    /// </summary>
    private async Task<(decimal Min, decimal Max, decimal Step)> ReadSupportedSpeedRangeAsync()
    {
        var data = await Client.ReadAsync(SupportedSpeed);
        if (data == null || data.Length < 6)
        {
            logger.LogWarning("Failed to read Supported Speed Range or data was invalid.");
            return (0, 0, 0);
        }

        // FTMS uses little-endian format, unit = 0.01 m/s
        ushort minRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0));
        ushort maxRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        ushort stepRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));

        // Convert m/s → km/h (×3.6)
        decimal minKph = Math.Round(minRaw * 0.01m , 2);
        decimal maxKph = Math.Round(maxRaw * 0.01m , 2);
        decimal stepKph = Math.Round(stepRaw * 0.01m, 2);

        logger.LogInformation(
            "Supported Speed Range: {Min:F2}–{Max:F2} km/h (step {Step:F2} km/h)",
            minKph, maxKph, stepKph
        );

        return (minKph, maxKph, stepKph);
    }

    
    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        var td = new TreadmillTelemetry();

        // Parse FTMS flags — contains both HasX and PosX values
        var flags = new FtmsTelemetryFlags(data);

        // Instantaneous Speed (always present, at offset 2)
        td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled = true;
        ushort rawSpeed = BitConverter.ToUInt16(data, 2); // 2 bytes, little endian
        decimal kph = rawSpeed / 100.0m; // resolution = 0.01 kph
        decimal speed = Math.Round(kph, 2, MidpointRounding.AwayFromZero);
        td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value = speed;

        Console.WriteLine($"Instantaneous Speed: {speed:F2} km/h ({speed / 1.609344m:F2} mph)");

        // Average Speed
        if (flags.HasAvgSpeed && flags.PosAvgSpeed != -1)
        {
            td.Values[TreadmillTelemetryProperty.AverageSpeed].Enabled = true;
            td.Values[TreadmillTelemetryProperty.AverageSpeed].Value =
                BitConverter.ToUInt16(data, flags.PosAvgSpeed) / 100.0m;
        }

        // Total Distance (24 bits, 0.1 m)
        if (flags.HasTotDistance && flags.PosTotDistance != -1)
        {
            int distance = BitConverter.ToUInt16(data, flags.PosTotDistance);
            distance += data[flags.PosTotDistance + 2] << 16;
            td.Values[TreadmillTelemetryProperty.TotalDistance].Enabled = true;
            td.Values[TreadmillTelemetryProperty.TotalDistance].Value = distance;
        }

        // Incline (int16, 0.1%) + Ramp Angle (int16, 0.1%)
        if (flags.HasInclination && flags.PosInclination != -1)
        {
            td.Values[TreadmillTelemetryProperty.Incline].Enabled = true;
            td.Values[TreadmillTelemetryProperty.Incline].Value =
                BitConverter.ToInt16(data, flags.PosInclination) / 10.0m;

            td.Values[TreadmillTelemetryProperty.RampAngle].Enabled = true;
            td.Values[TreadmillTelemetryProperty.RampAngle].Value =
                BitConverter.ToInt16(data, flags.PosInclination + 2) / 10.0m;
        }

        // Elevation Gain (int16, meters)
        if (flags.HasElevGain && flags.PosElevGain != -1)
        {
            td.Values[TreadmillTelemetryProperty.ElevationGain].Enabled = true;
            td.Values[TreadmillTelemetryProperty.ElevationGain].Value =
                BitConverter.ToInt16(data, flags.PosElevGain);
        }

        // Instantaneous Pace (uint16, 0.1 s/m)
        if (flags.HasInsPace && flags.PosInsPace != -1)
        {
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Enabled = true;
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Value =
                BitConverter.ToUInt16(data, flags.PosInsPace);
        }
        else if (td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled)
        {
            var speedKph = td.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value;
            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Enabled = true;

            td.Values[TreadmillTelemetryProperty.InstantaneousPace].Value =
                speedKph > 0 ? 3600m / speedKph : 0;
        }

        // Average Pace (uint16, 0.1 s/m)
        if (flags.HasAvgPace && flags.PosAvgPace != -1)
        {
            td.Values[TreadmillTelemetryProperty.AveragePace].Enabled = true;
            td.Values[TreadmillTelemetryProperty.AveragePace].Value =
                BitConverter.ToUInt16(data, flags.PosAvgPace);
        }

        // Calories (kcal)
        if (flags.HasKcal && flags.PosKcal != -1)
        {
            td.Values[TreadmillTelemetryProperty.Calories].Enabled = true;
            td.Values[TreadmillTelemetryProperty.Calories].Value =
                BitConverter.ToUInt16(data, flags.PosKcal);
        }

        // Heart Rate (bpm)
        if (flags.HasHR && flags.PosHR != -1)
        {
            td.Values[TreadmillTelemetryProperty.HeartRate].Enabled = true;
            td.Values[TreadmillTelemetryProperty.HeartRate].Value = data[flags.PosHR];
        }

        // MET (scaled by 100)
        if (flags.HasMET && flags.PosMET != -1)
        {
            td.Values[TreadmillTelemetryProperty.MetabolicEquivalent].Enabled = true;
            td.Values[TreadmillTelemetryProperty.MetabolicEquivalent].Value =
                BitConverter.ToUInt16(data, flags.PosMET) / 100.0m;
        }

        // Elapsed Time (s)
        if (flags.HasElapsedTime && flags.PosElapsedTime != -1)
        {
            td.Values[TreadmillTelemetryProperty.ElapsedTime].Enabled = true;
            td.Values[TreadmillTelemetryProperty.ElapsedTime].Value =
                BitConverter.ToUInt16(data, flags.PosElapsedTime);
        }

        // Remaining Time (s)
        if (flags.HasRemainingTime && flags.PosRemainingTime != -1)
        {
            td.Values[TreadmillTelemetryProperty.RemainingTime].Enabled = true;
            td.Values[TreadmillTelemetryProperty.RemainingTime].Value =
                BitConverter.ToUInt16(data, flags.PosRemainingTime);
        }

        // Force on Belt (int16, N) + Power (uint16, W)
        if (flags.HasForceBelt && flags.PosForceBelt != -1)
        {
            td.Values[TreadmillTelemetryProperty.ForceOnBelt].Enabled = true;
            td.Values[TreadmillTelemetryProperty.ForceOnBelt].Value =
                BitConverter.ToUInt16(data, flags.PosForceBelt);

            td.Values[TreadmillTelemetryProperty.Power].Enabled = true;
            td.Values[TreadmillTelemetryProperty.Power].Value =
                BitConverter.ToUInt16(data, flags.PosForceBelt + 2);
        }

        // Step Count
        if (flags.HasStepCount && flags.PosStepCount != -1)
        {
            td.Values[TreadmillTelemetryProperty.StepCount].Enabled = true;
            td.Values[TreadmillTelemetryProperty.StepCount].Value =
                BitConverter.ToUInt16(data, flags.PosStepCount);
        }

        return td;
    }
    
    private void HandleStatus(byte[] data)
    {
        if (data.Length == 0) return;

        var state = data[0] switch
        {
            0x02 => TreadmillState.Paused,
            0x03 => TreadmillState.Stopped,
            0x04 => TreadmillState.Running,
            _    => TreadmillState.Unknown
        };

        RaiseState(state);
    }
}