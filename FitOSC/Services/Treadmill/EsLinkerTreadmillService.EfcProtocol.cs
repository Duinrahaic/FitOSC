namespace FitOSC.Services.Treadmill;

public partial class EsLinkerTreadmillService
{
    /// <summary>
    /// Experimental: Evaluating driver
    /// </summary>
    private sealed class EfcProtocol : EqiProtocol
    {
        private const byte CommandHeader = 0xA1;
        private const byte ReplyHeader = 0x1A;

        private const byte SpeedCommand = 0x01;
        private const byte InclineCommand = 0x02;
        private const byte ControlCommand = 0x03;
        private const byte DeviceInfoCommand = 0x05;

        private const byte StatusReply = 0x01;
        private const byte WorkoutReply = 0x02;
        private const byte DeviceInfoReply = 0x05;
        private const int StatusLength = 9;
        private const int WorkoutLength = 12;
        private const int DeviceInfoLength = 12;

        // The app sends 01 ahead of every speed and incline target.
        private const byte TargetPrefix = 0x01;
        private const byte StartControl = 0x01; // also resumes
        private const byte PauseControl = 0x03;
        private const byte StopControl = 0x05;

        private const byte ImperialFlag = 0x80;
        private const byte StatusMask = 0x1F;
        private const byte IdleAfterWorkout = 6;
        private const int ReplyOverhead = 3; // header, command, length

        // Status codes the app reports as treadmill faults.
        private static readonly byte[] FaultCodes = [17, 18, 20, 21, 23, 25];

        private byte? _statusCode;

        // The app always offers pause on these treadmills. Calories arrive in units of 0.1 kcal.
        public EfcProtocol(EsLinkerTreadmillService service) : base(service, caloriesWrap: 10000, caloriesPerKcal: 10)
        {
            PauseSupported = true;
        }

        public override string Name => "EQiSports EFC";

        // As the app does once notifications are on; the treadmill then streams status and workout frames by itself.
        public override async Task OnConnectedAsync()
        {
            PublishConfiguration();
            await Write(Frame(DeviceInfoCommand));
        }

        // EFC has no handshake or keep-alive.
        public override Task PollAsync() => Task.CompletedTask;

        protected override byte[] StartFrame(bool resume) => Frame(ControlCommand, StartControl);

        protected override byte[] PauseFrame() => Frame(ControlCommand, PauseControl);

        protected override byte[] StopFrame() => Frame(ControlCommand, StopControl);

        protected override byte[] SpeedFrame(byte raw) => Frame(SpeedCommand, TargetPrefix, raw);

        protected override byte[] InclineFrame(byte percent) => Frame(InclineCommand, TargetPrefix, percent);

        public override void OnNotification(byte[] data)
        {
            // The app reads replies by command and declared length and does not check a trailing XOR, so neither does this.
            if (data.Length < ReplyOverhead || data[0] != ReplyHeader || data.Length < data[2] + ReplyOverhead)
            {
                Logger.LogDebug("Ignoring malformed EFC frame {Frame}", Convert.ToHexString(data));
                return;
            }

            switch (data[1])
            {
                case StatusReply when data[2] == StatusLength:
                    HandleStatusFrame(data);
                    break;
                case WorkoutReply when data[2] == WorkoutLength:
                    HandleWorkout(data);
                    break;
                case DeviceInfoReply when data[2] == DeviceInfoLength:
                    Logger.LogInformation("EFC treadmill manufacturer {Manufacturer}, model {Model}, firmware {Firmware}",
                        Convert.ToHexString(data, 3, 2), Convert.ToHexString(data, 5, 2), Convert.ToHexString(data, 7, 2));
                    break;
                default:
                    Logger.LogDebug("Ignoring unrecognised EFC frame {Frame}", Convert.ToHexString(data));
                    break;
            }
        }

        // Bytes 3-6: max and min speed (tenths of the unit), max and min incline (whole percent); 7-8: current speed and
        // incline; 9: bit 7 set in imperial mode, bits 0-4 the state or a fault code.
        private void HandleStatusFrame(byte[] data)
        {
            SetUnit((data[9] & ImperialFlag) != 0);
            if (!TrySetRanges(minSpeed: data[4], maxSpeed: data[3], minIncline: data[6], maxIncline: data[5]))
                Logger.LogDebug("Ignoring EFC speed range in {Frame}", Convert.ToHexString(data));

            SetSpeedRaw(data[7]);
            SetInclineRaw(data[8]);

            var code = (byte)(data[9] & StatusMask);
            if (code != _statusCode)
            {
                _statusCode = code;
                if (FaultCodes.Contains(code))
                    Logger.LogWarning("EFC treadmill reported fault {Code}", code);

                // Fault codes are not states, so they map to Unknown as in the app.
                HandleStatus(code == IdleAfterWorkout ? EqiStatus.Idle : (EqiStatus)code);
            }

            PublishData();
        }

        // Bytes 3-4: elapsed seconds; 5-6: distance in metres, or thousandths of a mile in imperial mode; 7-8: calories;
        // 9-10: steps; 11: hand-grip heart rate.
        private void HandleWorkout(byte[] data)
        {
            var distance = ReadBigEndian(data, 5, 2);
            UpdateWorkout(
                elapsedSeconds: ReadBigEndian(data, 3, 2),
                distanceMeters: Imperial ? (int)(distance * KilometresPerMile) : distance,
                calories: ReadBigEndian(data, 7, 2),
                steps: ReadBigEndian(data, 9, 2),
                heartRate: data[11]);
        }

        private static byte[] Frame(byte command, params byte[] data)
        {
            byte[] body = [CommandHeader, command, (byte)data.Length, .. data];
            return [.. body, Xor(body)];
        }
    }
}
