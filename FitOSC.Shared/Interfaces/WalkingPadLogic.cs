using Blazor.Bluetooth;

namespace FitOSC.Shared.Interfaces;

public class WalkingPadLogic(IDevice device) : BaseLogic(device)
{
    /** 
     *  Big thank you to my friend Raphiiko for providing the details for communicating the WalkingPad via Bluetooth.
     */
    
    protected override string LogicName { get; } = "WalkingPad Logic";
    protected override string ServiceUuid { get; } = BluetoothServices.WalkingMachine;
    protected override string FeaturesUuid { get; } = string.Empty; // None identified
    protected override string DataPointUuid { get; } = "fe01";
    protected override string ControlPointUuid { get; } = "fe02";
    protected override string StatusPointUuid { get; } = String.Empty; // None identified
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

    public override async Task Start(decimal startSpeed, decimal maxSpeed)
    {
        do
        {
            await Task.Delay(4000);
        }while (State != TreadmillState.Running);
        await SetSpeed(startSpeed, maxSpeed); // setting speed starts walking pad
    }

    public override async Task Stop()
    {
        await base.Stop();
        await SetSpeed(0, 0); // setting speed to 0 stops walking pad
    }
    
    public override async Task SetSpeed(decimal speed, decimal maxSpeed) =>         
        await (ControlPoint?.ExecuteCommand(0x02, ConvertSpeed(speed, maxSpeed)) ?? Task.CompletedTask);
    
    protected override async void Dispose(bool disposing)
    {
        if (disposing)
        {
            await Disconnect();
            base.Dispose(disposing);
        }
    }
}