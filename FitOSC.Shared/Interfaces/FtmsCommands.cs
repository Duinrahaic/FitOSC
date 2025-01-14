using Blazor.Bluetooth;

namespace FitOSC.Shared.Interfaces;

public static class FtmsCommands
{
    private static string TranslateCommand(byte[] command)
    {
        if (command == null || command.Length == 0)
            return "Unknown Command";

        var opcode = command[0]; // First byte is the opcode
        var values = command.Skip(1).ToArray(); // Remaining bytes are the values

        switch (opcode)
        {
            case 0x01:
                return "Reset";
            case 0x02:
                return values.Length > 0 ? $"Set Target Speed to {values[0]}" : "Set Target Speed";
            case 0x07:
                return "Start";
            case 0x08:
                if (values.Length > 0)
                    switch (values[0])
                    {
                        case 0x01:
                            return "Stop";
                        case 0x02:
                            return "Pause";
                        default:
                            return "Unknown Stop/Pause Command";
                    }

                return "Unknown Stop/Pause Command";
            case 0x80:
                return "Request Control";
            default:
                return "Unknown Command";
        }
    }


    public static async Task ExecuteCommand(this IBluetoothRemoteGATTCharacteristic? characteristic, byte[] payload)
    {
        if (characteristic == null) return;

        try
        {
            await characteristic.WriteValueWithResponse(payload);
        }
        catch
        {
            //throw new Exception($"Failed to execute {TranslateCommand(command)} command.");
        }
    }


    public static async Task ExecuteCommand(this IBluetoothRemoteGATTCharacteristic? characteristic, byte opcode,
        params byte[] values)
    {
        if (characteristic == null) return;

        await characteristic.RequestControl();

        // Combine opcode and the variable number of values into a single byte array
        var command = new byte[1 + values.Length];
        command[0] = opcode;
        Array.Copy(values, 0, command, 1, values.Length);

        try
        {
            await characteristic.WriteValueWithResponse(command);
        }
        catch
        {
            //throw new Exception($"Failed to execute {TranslateCommand(command)} command.");
        }
    }

    private static async Task RequestControl(this IBluetoothRemoteGATTCharacteristic controlPoint)
    {
        byte[] requestControlCommand = { 0x80 };
        try
        {
            await controlPoint.WriteValueWithResponse(requestControlCommand);
        }
        catch
        {
            //throw new Exception($"Failed to execute request control.");
        }
    }

    public static async Task Start(this IBluetoothRemoteGATTCharacteristic? controlPoint)
    {
        await controlPoint.ExecuteCommand(0x07);
    }

    public static async Task Stop(this IBluetoothRemoteGATTCharacteristic? controlPoint)
    {
        await controlPoint.ExecuteCommand(0x08, 0x01);
    }

    public static async Task Pause(this IBluetoothRemoteGATTCharacteristic? controlPoint)
    {
        await controlPoint.ExecuteCommand(0x08, 0x02);
    }

    public static async Task Reset(this IBluetoothRemoteGATTCharacteristic? controlPoint)
    {
        await controlPoint.ExecuteCommand(0x01);
    }

    public static async Task SetTargetSpeed(this IBluetoothRemoteGATTCharacteristic? controlPoint, decimal speed,
        decimal maxSpeed)
    {
        await controlPoint.ExecuteCommand(0x02, ConvertSpeed(speed, maxSpeed));
    }

    public static async Task SetTargetSpeed(this IBluetoothRemoteGATTCharacteristic? controlPoint, decimal speed)
    {
        await controlPoint.ExecuteCommand(0x02, ConvertSpeed(speed));
    }

    private static byte[] ConvertSpeed(decimal speed, decimal maxSpeed)
    {
        try
        {
            if (speed >= maxSpeed)
                return ConvertSpeed(maxSpeed);
            return ConvertSpeed(speed);
        }
        catch
        {
            //throw new Exception($"Failed to convert {speed} to byte array.");
        }

        return new byte[0];
    }

    private static byte[] ConvertSpeed(decimal speed)
    {
        try
        {
            var speedInCmPerSecond = (ushort)(speed * 100);
            byte[] speedBytes =
            {
                (byte)(speedInCmPerSecond & 0xFF), // Lower byte of speed
                (byte)((speedInCmPerSecond >> 8) & 0xFF) // Upper byte of speed
            };
            return speedBytes;
        }
        catch
        {
            //throw new Exception($"Failed to convert {speed} to byte array.");
        }

        return new byte[0];
    }

    public static FtmsData ReadFtmsData(byte[] value)
    {
        var treadmillData = new FtmsData();
        // Flags are in the first 2 bytes (little-endian)
        var flags = BitConverter.ToUInt16(value, 0);
        var nextPosition = 4; // 2 octets for flags, 2 for instant speed

        int posAvgSpeed = -1, posTotDistance = -1, posInclination = -1;
        int posElevGain = -1, posInsPace = -1, posAvgPace = -1;
        int posKcal = -1, posHR = -1, posMET = -1;
        int posElapsedTime = -1, posRemainingTime = -1, posForceBelt = -1;
        var posStepCount = -1; // New variable for step count

        // Determine the positions of each field based on the flags
        if ((flags & (1 << 1)) != 0)
        {
            posAvgSpeed = nextPosition;
            nextPosition += 2;
        }

        if ((flags & (1 << 2)) != 0)
        {
            posTotDistance = nextPosition;
            nextPosition += 3;
        }

        if ((flags & (1 << 3)) != 0)
        {
            posInclination = nextPosition;
            nextPosition += 4;
        }

        if ((flags & (1 << 4)) != 0)
        {
            posElevGain = nextPosition;
            nextPosition += 4;
        }

        if ((flags & (1 << 5)) != 0)
        {
            posInsPace = nextPosition;
            nextPosition += 1;
        }

        if ((flags & (1 << 6)) != 0)
        {
            posAvgPace = nextPosition;
            nextPosition += 1;
        }

        if ((flags & (1 << 7)) != 0)
        {
            posKcal = nextPosition;
            nextPosition += 5;
        }

        if ((flags & (1 << 8)) != 0)
        {
            posHR = nextPosition;
            nextPosition += 1;
        }

        if ((flags & (1 << 9)) != 0)
        {
            posMET = nextPosition;
            nextPosition += 1;
        }

        if ((flags & (1 << 10)) != 0)
        {
            posElapsedTime = nextPosition;
            nextPosition += 2;
        }

        if ((flags & (1 << 11)) != 0)
        {
            posRemainingTime = nextPosition;
            nextPosition += 2;
        }

        if ((flags & (1 << 12)) != 0)
        {
            posForceBelt = nextPosition;
            nextPosition += 4;
        }

        if ((flags & (1 << 13)) != 0)
        {
            posStepCount = nextPosition;
            nextPosition += 2;
        } // Step count flag

        // Instantaneous speed
        treadmillData.Speed = (decimal)(BitConverter.ToUInt16(value, 2) / 100.0);

        // Distance
        if (posTotDistance != -1)
        {
            int distance = BitConverter.ToUInt16(value, posTotDistance);
            var distanceComplement = value[posTotDistance + 2] << 16;
            distance += distanceComplement;
            treadmillData.Distance = distance;
        }

        // Inclination
        if (posInclination != -1) treadmillData.Inclination = BitConverter.ToInt16(value, posInclination) / 10.0;

        // Calories
        if (posKcal != -1) treadmillData.Calories = BitConverter.ToUInt16(value, posKcal);

        // Heart Rate
        if (posHR != -1) treadmillData.HeartRate = value[posHR];

        // Elapsed Time
        if (posElapsedTime != -1) treadmillData.ElapsedTime = BitConverter.ToUInt16(value, posElapsedTime);

        // Remaining Time
        if (posRemainingTime != -1) treadmillData.RemainingTime = BitConverter.ToUInt16(value, posRemainingTime);

        // Elevation Gain
        if (posElevGain != -1) treadmillData.ElevGain = BitConverter.ToInt16(value, posElevGain) / 10.0;

        // Steps (Newly added logic)
        if (posStepCount != -1) treadmillData.Steps = BitConverter.ToUInt16(value, posStepCount);

        // Instantaneous Pace
        if (posInsPace != -1) treadmillData.InsPace = BitConverter.ToUInt16(value, posInsPace);

        // Average Pace
        if (posAvgPace != -1) treadmillData.AvgPace = BitConverter.ToUInt16(value, posAvgPace);

        // Return the populated DataPoint object
        return treadmillData;
    }
}