using Blazor.Bluetooth;
using FitOSC.Shared.Extensions;

namespace FitOSC.Shared.Interfaces;

public class GenericLogic(IDevice device) : BaseLogic(device)
{
    protected override string LogicName { get; } = "Generic FTMS Logic";
    protected override string ServiceUuid { get; } = BluetoothServices.FTMS;
    protected override string FeaturesUuid { get; } = "2acc";
    protected override string DataPointUuid { get; } = "2acd";
    protected override string ControlPointUuid { get; } = "2ad9";
    protected override string StatusPointUuid { get; } = "2ada";

    public override async Task Reset() =>
        await (ControlPoint?.ExecuteCommand(0x01) ?? Task.CompletedTask);

    public override async Task Start() =>
        await (ControlPoint?.ExecuteCommand(0x07) ?? Task.CompletedTask);

    public override async Task Start(decimal startSpeed, decimal maxSpeed)
    {
        await Start();
        do
        {
            await Task.Delay(4000);
        }while (State != TreadmillState.Running);
        await SetSpeed(startSpeed, maxSpeed);
    }

    public override async Task Stop()
    {
        await base.Stop();
        if (State == TreadmillState.Running || State == TreadmillState.Paused)
        {
            await (ControlPoint?.ExecuteCommand(0x08, 0x01) ?? Task.CompletedTask);
        }
    }

    public override async Task Pause() =>
        await (ControlPoint?.ExecuteCommand(0x08, 0x02) ?? Task.CompletedTask);

    public override async Task SetSpeed(decimal speed, decimal maxSpeed) =>
        await (ControlPoint?.ExecuteCommand(0x02, ConvertSpeed(speed, maxSpeed)) ?? Task.CompletedTask);
    
    public override async Task<FitnessMachineFeatures?> GetFeatures()
    {
        if (Features == null)
        {
            return null;
        }

        var data = await Features.ReadValue();
        return FitnessMachineFeatures.Parse(data);
    }

    public override async Task Disconnect()
    {
        if (ControlPoint != null)
        {
            await ControlPoint.UnsubscribeFromNotifications(HandleControlPoint);
        }

        if (DataPoint != null)
        {
            await DataPoint.UnsubscribeFromNotifications(ReadData);
        }

        if (Features != null)
        {
            await Features.UnsubscribeFromNotifications(OnTreadmillFeaturesUpdate);
        }

        if (StatusPoint != null)
        {
            await StatusPoint.UnsubscribeFromNotifications(HandleStatusChange);
        }

        try
        {
            await Device.Gatt.Disonnect();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    protected override void HandleControlPoint(object? sender, CharacteristicEventArgs args)
    {
        byte opcode = args.Value[0];
        byte result = args.Value.Length > 1 ? args.Value[1] : (byte)0;
        string commandName = opcode switch
        {
            0x80 => "Request Control",
            0x81 => "Reset",
            0x82 => "Set Target Speed",
            0x83 => "Set Incline",
            0x84 => "Set Resistance Level",
            0x85 => "Start or Resume",
            0x86 => "Stop or Pause",
            0x87 => "Set Power",
            0x88 => "Set Heart Rate",
            0x8F => "General Error",
            _ => "Unknown"
        };

        Console.WriteLine($"> Received '{commandName}' response, Result: {result}");
    }

    protected override void HandleStatusChange(object? sender, CharacteristicEventArgs args)
    {
        byte[] value = args.Value;

        // The first byte of the notification contains the status event code.
        byte statusCode = value[0];

        switch (statusCode)
        {
            case 0x02: // Fitness Machine Stopped or Paused by the User
                State = value[1] == 0x02 ? TreadmillState.Paused : TreadmillState.Stopped;
                break;
            case 0x03: // Fitness Machine Stopped by Safety Key
                State = TreadmillState.StoppedSafety;
                break;
            case 0x04: // Fitness Machine Started or Resumed by the User
                State = TreadmillState.Running;
                break;
            default:
                State = TreadmillState.Unknown;
                break;
        }
    }

    public override async void Dispose(bool disposing)
    {
        if (disposing)
        {
            await Disconnect();
            base.Dispose(disposing);
        }
    }
}