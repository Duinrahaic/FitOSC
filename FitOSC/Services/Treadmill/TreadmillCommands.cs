namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[Flags]
public enum TreadmillCommands
{
    None = 0,
    Start = 1 << 0,
    Pause = 1 << 1,
    Stop = 1 << 2,
    Speed = 1 << 3,
    Incline = 1 << 4,
    All = Start | Pause | Stop | Speed | Incline
}
