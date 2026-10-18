namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public static class KingSmithProtocol
{
    public const byte CommandHeader = 0xF7;
    public const byte ResponseHeader = 0xF8;
    public const byte Trailer = 0xFD;

    public const byte StatusCommand = 0xA2;
    public const byte ParameterCommand = 0xA6;

    private const byte SetSpeedKey = 0x01;
    private const byte SetModeKey = 0x02;
    private const byte StartPauseKey = 0x04;

    public const int StatusFrameLength = 20;
    private const int ParameterMaxSpeedIndex = 7;

    /// <summary>Requests a status (A2) response.</summary>
    public static byte[] QueryStatus() => Command(StatusCommand, 0x00, 0x00);

    /// <summary>Requests a parameter (A6) response.</summary>
    public static byte[] QueryParameters() => Command(ParameterCommand, 0x00, 0x00, 0x00, 0x00, 0x00);

    /// <summary>Sets the belt speed in 0.1 km/h steps. Only honoured in <see cref="KingSmithMode.Manual"/>.</summary>
    public static byte[] SetSpeed(decimal kph) =>
        Command(StatusCommand, SetSpeedKey, (byte)Math.Round(kph * 10, MidpointRounding.AwayFromZero));

    public static byte[] SetMode(KingSmithMode mode) => Command(StatusCommand, SetModeKey, (byte)mode);

    /// <summary>Toggles the belt between running and paused.</summary>
    public static byte[] StartOrPause() => Command(StatusCommand, StartPauseKey, 0x01);

    private static byte[] Command(byte command, params byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        frame[0] = CommandHeader;
        frame[1] = command;
        payload.CopyTo(frame, 2);
        frame[^2] = Checksum(frame);
        frame[^1] = Trailer;
        return frame;
    }

    /// <summary>Low byte of the sum of bytes between the header and the checksum.</summary>
    public static byte Checksum(byte[] frame)
    {
        var sum = 0;
        for (var i = 1; i < frame.Length - 2; i++)
            sum += frame[i];
        return (byte)(sum & 0xFF);
    }

    public static bool IsValidResponse(byte[] frame) =>
        frame.Length >= 4 &&
        frame[0] == ResponseHeader &&
        frame[^1] == Trailer &&
        frame[^2] == Checksum(frame);

    public static KingSmithStatus? ParseStatus(byte[] frame)
    {
        if (frame.Length != StatusFrameLength || frame[1] != StatusCommand)
            return null;

        return new KingSmithStatus(
            BeltState: (KingSmithBeltState)frame[2],
            SpeedKph: frame[3] / 10m,
            Mode: (KingSmithMode)frame[4],
            ElapsedSeconds: ReadUInt24(frame, 5),
            DistanceMeters: ReadUInt24(frame, 8) * 10,
            Steps: ReadUInt24(frame, 11));
    }

    /// <summary>Maximum belt speed reported in a parameter (A6) response, or null if absent.</summary>
    public static decimal? ParseMaxSpeed(byte[] frame)
    {
        if (frame[1] != ParameterCommand || frame.Length <= ParameterMaxSpeedIndex + 2)
            return null;

        var raw = frame[ParameterMaxSpeedIndex];
        return raw > 0 ? raw / 10m : null;
    }

    private static int ReadUInt24(byte[] data, int offset) =>
        (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
}

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public class KingSmithFrameReader
{
    // Longer than any response; bytes that never form a frame are discarded past this point.
    private const int MaxFrameLength = 64;

    private readonly List<byte> _buffer = new();

    public IEnumerable<byte[]> Append(byte[] chunk)
    {
        foreach (var b in chunk)
        {
            _buffer.Add(b);
            if (b == KingSmithProtocol.Trailer && TakeFrame() is { } frame)
                yield return frame;

            if (_buffer.Count > MaxFrameLength)
                DropToNextHeader();
        }
    }

    /// <summary>Returns the oldest complete frame ending at the last byte, discarding everything before it.</summary>
    private byte[]? TakeFrame()
    {
        for (var start = 0; start < _buffer.Count - 1; start++)
        {
            if (_buffer[start] != KingSmithProtocol.ResponseHeader)
                continue;

            var candidate = _buffer.GetRange(start, _buffer.Count - start).ToArray();
            if (!KingSmithProtocol.IsValidResponse(candidate) || !IsKnownShape(candidate))
                continue;

            _buffer.Clear();
            return candidate;
        }
        return null;
    }

    private static bool IsKnownShape(byte[] frame) => frame[1] switch
    {
        KingSmithProtocol.StatusCommand => frame.Length == KingSmithProtocol.StatusFrameLength,
        KingSmithProtocol.ParameterCommand => true,
        _ => false
    };

    private void DropToNextHeader()
    {
        var next = _buffer.IndexOf(KingSmithProtocol.ResponseHeader, 1);
        if (next < 0)
            _buffer.Clear();
        else
            _buffer.RemoveRange(0, next);
    }

    public void Reset() => _buffer.Clear();
}

public record KingSmithStatus(
    KingSmithBeltState BeltState,
    decimal SpeedKph,
    KingSmithMode Mode,
    int ElapsedSeconds,
    int DistanceMeters,
    int Steps);

public enum KingSmithBeltState : byte
{
    Ready = 0,
    Running = 1,
    Paused = 2,
    Stopped = 3,
    Fault = 4,
    Sleep = 5,
}

public enum KingSmithMode : byte
{
    Auto = 0,
    Manual = 1,
    Standby = 2,
}
