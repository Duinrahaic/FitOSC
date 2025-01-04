using Blazor.Bluetooth;

namespace FitOSC.Shared.Interfaces.WalkingPad;

public class WalkingPadLogic(IDevice device) : BaseLogic(device)
{
    /**
     * Big thank you to my friend Raphiiko for providing the details for communicating the WalkingPad via Bluetooth.
     * 
     * The payload will be as follows:
     * |static|static|opcode|param|crc|static|
     * 
     * Notes:
     * 
     * - Speed is always in Km/h
     * - Speed is a decimal number, so it will be multiplied by 10 and converted to an integer.
     * - The CRC is calculated by adding all bytes and then adding the sum of all bytes.
     */

    protected override string LogicName { get; } = "WalkingPad Logic";

    protected override string ServiceUuid { get; } = BluetoothServices.WalkingMachine;
    protected override string FeaturesUuid { get; } = string.Empty; // None identified
    protected override string DataPointUuid { get; } = "fe01";
    protected override string ControlPointUuid { get; } = "fe02";
    protected override string StatusPointUuid { get; } = string.Empty; // None identified

    protected override void HandleControlPoint(object? sender, CharacteristicEventArgs args)
    {
        var opcode = args.Value[0];
        var result = args.Value.Length > 1 ? args.Value[1] : (byte)0;
        var commandName = opcode switch
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
        var value = args.Value;

        // The first byte of the notification contains the status event code.
        var statusCode = value[0];

        State = statusCode switch
        {
            0x02 => // Fitness Machine Stopped or Paused by the User
                value[1] == 0x02 ? TreadmillState.Paused : TreadmillState.Stopped,
            0x03 => // Fitness Machine Stopped by Safety Key
                TreadmillState.StoppedSafety,
            0x04 => // Fitness Machine Started or Resumed by the User
                TreadmillState.Running,
            _ => TreadmillState.Unknown
        };
    }

    public override async Task Start(decimal startSpeed, decimal maxSpeed)
    {
        await SetMode(WalkingPadMode.Manual);
        await SetSpeed(startSpeed, maxSpeed); // setting speed starts walking pad
    }

    public override async Task Stop()
    {
        await base.Stop();
        await SetSpeed(0, 0); // setting speed to 0 stops walking pad
        await SetMode(WalkingPadMode.Standby);
    }

    public override async Task SetSpeed(decimal speed, decimal maxSpeed)
    {
        var payload = WalkingPadExtensions.GeneratePayload(1, (int)DelegateSpeed(speed, maxSpeed));
        await (ControlPoint?.ExecuteCommand(payload) ?? Task.CompletedTask);
    }

    private async Task SetMode(WalkingPadMode mode)
    {
        var payload = WalkingPadExtensions.GeneratePayload(2, (int)mode);
        await (ControlPoint?.ExecuteCommand(payload) ?? Task.CompletedTask);
    }


    private decimal DelegateSpeed(decimal speed, decimal maxSpeed)
    {
        if (speed >= maxSpeed)
            return maxSpeed * 10;
        return speed * 10;
    }

    protected override async void Dispose(bool disposing)
    {
        if (disposing)
        {
            await Disconnect();
            base.Dispose(disposing);
        }
    }
}