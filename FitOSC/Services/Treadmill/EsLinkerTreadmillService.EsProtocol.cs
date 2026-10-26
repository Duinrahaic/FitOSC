namespace FitOSC.Services.Treadmill;

public partial class EsLinkerTreadmillService
{
    /// <summary>
    /// The ES Linker protocol, over the <c>fff0</c> service.
    /// </summary>
    private sealed class EsProtocol(EsLinkerTreadmillService service)
        : EqiProtocol(service, caloriesWrap: 100000, caloriesPerKcal: 100)
    {
        private const byte Header = 0xA9;

        private const byte SetSpeedCommand = 0x01;
        private const byte HeartbeatCommand = 0x02;
        private const byte ErrorCommand = 0x03;
        private const byte SetInclineCommand = 0x04;
        private const byte PairingCommand = 0x08;
        private const byte StatusCommand = 0x09;
        private const byte SpeedRangeCommand = 0x0A;
        private const byte PauseSupportQuery = 0x9E;
        private const byte KeepAliveCommand = 0xA0;
        private const byte ControlCommand = 0xA3;
        private const byte SpeedChangedCommand = 0xE0;
        private const byte InclineChangedCommand = 0xE1;
        private const byte UnitCommand = 0xF2;
        private const byte PauseSupportReply = 0xF3;
        private const byte CurrentSpeedCommand = 0xF4;
        private const byte RunningCommand = 0xFE;

        private const byte HandshakeLength = 0x01;
        private const byte ChallengeLength = 0x04;
        private const byte StopControl = 0x00;
        private const byte StartControl = 0x01;
        private const byte ResumeControl = 0x02;
        private const byte PauseControl = 0x03;
        private const byte ImperialUnit = 0x02;

        private const int ShortHeartbeatLength = 17;
        private const int LongHeartbeatLength = 18;
        private const int FrameOverhead = 4; // header, command, length, XOR

        // The app repeats the handshake every three seconds until the treadmill sends its challenge.
        private const int HandshakeEveryTicks = 3;

        private bool _paired;
        private int _handshakeTicks;
        private byte[]? _lastChallenge;
        private byte[]? _lastPairingCode;
        private bool _unitKnown;
        private bool _rangeKnown;

        public override string Name => "ES Linker";

        public override async Task OnConnectedAsync()
        {
            PublishConfiguration();
            await Write(UnitQuery());
        }

        // Handshake until paired, then echo state and speed back as a keep-alive.
        public override Task PollAsync()
        {
            if (_paired)
                return Write(Frame(KeepAliveCommand, (byte)Status, SpeedRaw, 0x00));
            return ++_handshakeTicks % HandshakeEveryTicks == 0 ? Write(HandshakeFrame()) : Task.CompletedTask;
        }

        protected override byte[] StartFrame(bool resume) => Frame(ControlCommand, resume ? ResumeControl : StartControl);

        protected override byte[] PauseFrame() => Frame(ControlCommand, PauseControl);

        protected override byte[] StopFrame() => Frame(ControlCommand, StopControl);

        protected override byte[] SpeedFrame(byte raw) => Frame(SetSpeedCommand, raw);

        protected override byte[] InclineFrame(byte percent) => Frame(SetInclineCommand, percent);

        public override void OnNotification(byte[] data)
        {
            // Every frame declares its payload length; drop anything truncated or padded.
            if (data.Length < FrameOverhead || data[0] != Header || data.Length != data[2] + FrameOverhead)
            {
                Logger.LogDebug("Ignoring malformed ES Linker frame {Frame}", Convert.ToHexString(data));
                return;
            }

            switch (data[1])
            {
                case PairingCommand when data[2] == HandshakeLength:
                    // The treadmill answered the handshake; keep handshaking until it sends a pairing challenge.
                    if (!_paired)
                        _ = Write(HandshakeFrame());
                    break;
                case PairingCommand when data[2] == ChallengeLength:
                    HandleChallenge(data);
                    break;
                case HeartbeatCommand when data.Length is ShortHeartbeatLength or LongHeartbeatLength:
                    HandleHeartbeat(data);
                    break;
                case StatusCommand when data[2] >= 1:
                    HandleStatus((EqiStatus)data[3]);
                    break;
                case RunningCommand:
                    HandleStatus(EqiStatus.Running);
                    break;
                case CurrentSpeedCommand or SpeedChangedCommand when data[2] >= 1:
                    SetSpeedRaw(data[3]);
                    PublishData();
                    break;
                case InclineChangedCommand when data[2] >= 1:
                    SetInclineRaw(data[3]);
                    PublishData();
                    break;
                case UnitCommand when data[2] >= 1:
                    HandleUnit(data);
                    break;
                case SpeedRangeCommand when data[2] >= 2:
                    HandleSpeedRange(data);
                    break;
                case PauseSupportReply when data[2] >= 1:
                    PauseSupported = data[3] == 1;
                    Logger.LogInformation("ES Linker pause supported: {Supported}", PauseSupported);
                    PublishConfiguration();
                    break;
                case ErrorCommand:
                    Logger.LogWarning("ES Linker treadmill reported error {Frame}", Convert.ToHexString(data));
                    break;
                default:
                    Logger.LogDebug("Ignoring unrecognised ES Linker frame {Frame}", Convert.ToHexString(data));
                    break;
            }
        }

        private void HandleChallenge(byte[] data)
        {
            // The treadmill repeats its challenge until it is answered; repeat the same answer in case ours was lost.
            if (_lastChallenge == null || !_lastChallenge.AsSpan().SequenceEqual(data))
            {
                _lastChallenge = data;
                _lastPairingCode = Frame(PairingCommand, PairingCode(data[^2]));
                Logger.LogInformation("ES Linker treadmill paired");
            }

            _paired = true;
            _ = Write(_lastPairingCode!);
        }

        private void HandleHeartbeat(byte[] data)
        {
            if (!HasValidHeartbeatXor(data))
                return;

            if (!_unitKnown)
                _ = Write(UnitQuery());
            if (!_rangeKnown)
                _ = Write(Frame(SpeedRangeCommand, (byte)Random.Shared.Next(255)));

            // Calories arrive in units of 10 cal.
            UpdateWorkout(
                elapsedSeconds: ReadBigEndian(data, 12, 2),
                distanceMeters: ReadBigEndian(data, 7, 2),
                calories: ReadBigEndian(data, 4, 3),
                steps: ReadBigEndian(data, 9, 3));
        }

        // Some consoles append the current speed and incline to the unit reply.
        private void HandleUnit(byte[] data)
        {
            _unitKnown = true;
            SetUnit(data[3] == ImperialUnit);

            if (data.Length > 6)
            {
                SetSpeedRaw(data[4]);
                SetInclineRaw(data[5]);
                PublishData();
            }
        }

        // Speed range in bytes 3-4; consoles with incline control append its range (whole percent) in bytes 5-6.
        private void HandleSpeedRange(byte[] data)
        {
            _rangeKnown = true;
            var (minIncline, maxIncline) = data.Length > 7 ? (data[5], data[6]) : ((byte)0, (byte)0);
            if (!TrySetRanges(data[3], data[4], minIncline, maxIncline))
            {
                Logger.LogWarning("Ignoring ES Linker speed range {Frame}", Convert.ToHexString(data));
                return;
            }

            _ = Write(Frame(PauseSupportQuery, 0xFE));
        }

        private static byte[] UnitQuery() => Frame(UnitCommand, 0x2F);

        private static byte[] HandshakeFrame() => Frame(PairingCommand, (byte)Random.Shared.Next(255));

        /// <summary>
        /// The pairing reply: two random nibbles, their sum and their product, in an order chosen by the challenge byte.
        /// </summary>
        private static byte[] PairingCode(byte challenge)
        {
            var a = (byte)Random.Shared.Next(1, 16);
            var b = (byte)Random.Shared.Next(1, 16);
            var sum = (byte)(a + b);
            var product = (byte)(a * b);
            return (challenge % 6) switch
            {
                0 => [a, b, sum, product],
                1 => [a, b, product, sum],
                2 => [sum, a, b, product],
                3 => [product, a, b, sum],
                4 => [sum, product, a, b],
                _ => [product, sum, a, b]
            };
        }

        private static byte[] Frame(byte command, params byte[] data)
        {
            byte[] body = [Header, command, (byte)data.Length, .. data];
            return [.. body, Xor(body)];
        }

        // Matches the EQI app, whose heartbeat checksum leaves out the byte before the XOR.
        private static bool HasValidHeartbeatXor(byte[] frame) => Xor(frame.AsSpan(0, frame.Length - 2)) == frame[^1];
    }
}
