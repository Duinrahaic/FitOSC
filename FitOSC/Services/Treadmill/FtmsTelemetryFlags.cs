namespace FitOSC.Services.Treadmill;

public class FtmsTelemetryFlags
{
    // ---- Presence (bool flags) ----
    public bool HasInsSpeed { get; init; }
    public bool HasAvgSpeed { get; init; }
    public bool HasTotDistance { get; init; }
    public bool HasInclination { get; init; }
    public bool HasElevGain { get; init; }
    public bool HasInsPace { get; init; }
    public bool HasAvgPace { get; init; }
    public bool HasKcal { get; init; }
    public bool HasHR { get; init; }
    public bool HasMET { get; init; }
    public bool HasElapsedTime { get; init; }
    public bool HasRemainingTime { get; init; }
    public bool HasForceBelt { get; init; }

    // ---- Bit positions  ----
    // Bit 0 is "More Data": when set, Instantaneous Speed is omitted. Bits 13-15 are reserved.
    public const int BitMoreData = 0;
    public const int BitAvgSpeed = 1;
    public const int BitTotDistance = 2;
    public const int BitInclination = 3;
    public const int BitElevGain = 4;
    public const int BitInsPace = 5;
    public const int BitAvgPace = 6;
    public const int BitKcal = 7;
    public const int BitHR = 8;
    public const int BitMET = 9;
    public const int BitElapsedTime = 10;
    public const int BitRemainingTime = 11;
    public const int BitForceBelt = 12;

    // ---- Calculated byte offsets in payload ----
    public int PosInsSpeed { get; private set; } = -1;
    public int PosAvgSpeed { get; private set; } = -1;
    public int PosTotDistance { get; private set; } = -1;
    public int PosInclination { get; private set; } = -1;
    public int PosElevGain { get; private set; } = -1;
    public int PosInsPace { get; private set; } = -1;
    public int PosAvgPace { get; private set; } = -1;
    public int PosKcal { get; private set; } = -1;
    public int PosHR { get; private set; } = -1;
    public int PosMET { get; private set; } = -1;
    public int PosElapsedTime { get; private set; } = -1;
    public int PosRemainingTime { get; private set; } = -1;
    public int PosForceBelt { get; private set; } = -1;

    /// <summary>Number of bytes the flags say the packet contains.</summary>
    public int Length { get; }

    public FtmsTelemetryFlags(byte[] data)
    {
        if (data == null || data.Length < 2)
            throw new ArgumentException("Invalid FTMS data — must contain at least 2 bytes for flags.");

        ushort flags = BitConverter.ToUInt16(data, 0);
        int pos = 2; // after flags

        // Assign presence flags
        HasInsSpeed = (flags & (1 << BitMoreData)) == 0;
        HasAvgSpeed = (flags & (1 << BitAvgSpeed)) != 0;
        HasTotDistance = (flags & (1 << BitTotDistance)) != 0;
        HasInclination = (flags & (1 << BitInclination)) != 0;
        HasElevGain = (flags & (1 << BitElevGain)) != 0;
        HasInsPace = (flags & (1 << BitInsPace)) != 0;
        HasAvgPace = (flags & (1 << BitAvgPace)) != 0;
        HasKcal = (flags & (1 << BitKcal)) != 0;
        HasHR = (flags & (1 << BitHR)) != 0;
        HasMET = (flags & (1 << BitMET)) != 0;
        HasElapsedTime = (flags & (1 << BitElapsedTime)) != 0;
        HasRemainingTime = (flags & (1 << BitRemainingTime)) != 0;
        HasForceBelt = (flags & (1 << BitForceBelt)) != 0;

        // Calculate and assign byte positions
        PosInsSpeed = HasInsSpeed ? pos : -1; pos += HasInsSpeed ? 2 : 0; // uint16
        PosAvgSpeed = HasAvgSpeed ? pos : -1; pos += HasAvgSpeed ? 2 : 0; // uint16
        PosTotDistance = HasTotDistance ? pos : -1; pos += HasTotDistance ? 3 : 0; // uint24
        PosInclination = HasInclination ? pos : -1; pos += HasInclination ? 4 : 0; // sint16 incline + sint16 ramp angle
        PosElevGain = HasElevGain ? pos : -1; pos += HasElevGain ? 4 : 0; // uint16 positive + uint16 negative
        PosInsPace = HasInsPace ? pos : -1; pos += HasInsPace ? 2 : 0; // uint16
        PosAvgPace = HasAvgPace ? pos : -1; pos += HasAvgPace ? 2 : 0; // uint16
        PosKcal = HasKcal ? pos : -1; pos += HasKcal ? 5 : 0; // uint16 total + uint16 per hour + uint8 per minute
        PosHR = HasHR ? pos : -1; pos += HasHR ? 1 : 0; // uint8
        PosMET = HasMET ? pos : -1; pos += HasMET ? 1 : 0; // uint8
        PosElapsedTime = HasElapsedTime ? pos : -1; pos += HasElapsedTime ? 2 : 0; // uint16
        PosRemainingTime = HasRemainingTime ? pos : -1; pos += HasRemainingTime ? 2 : 0; // uint16
        PosForceBelt = HasForceBelt ? pos : -1; pos += HasForceBelt ? 4 : 0; // sint16 force + sint16 power
        Length = pos;
    }
    // ---- Debugging convenience ----
    public override string ToString()
    {
        return $"FTMS Telemetry Flags:\n" +
               $"InsSpeed={HasInsSpeed} (pos {PosInsSpeed}), AvgSpeed={HasAvgSpeed} (pos {PosAvgSpeed}), TotDistance={HasTotDistance} (pos {PosTotDistance}), " +
               $"Inclination={HasInclination} (pos {PosInclination}), ElevGain={HasElevGain} (pos {PosElevGain}),\n" +
               $"InsPace={HasInsPace} (pos {PosInsPace}), AvgPace={HasAvgPace} (pos {PosAvgPace}), " +
               $"Kcal={HasKcal} (pos {PosKcal}), HR={HasHR} (pos {PosHR}), MET={HasMET} (pos {PosMET}),\n" +
               $"ElapsedTime={HasElapsedTime} (pos {PosElapsedTime}), RemainingTime={HasRemainingTime} (pos {PosRemainingTime}), " +
               $"ForceBelt={HasForceBelt} (pos {PosForceBelt}), Length={Length}";
    }
}
