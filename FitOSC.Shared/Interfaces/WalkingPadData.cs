namespace FitOSC.Shared.Interfaces;

public class WalkingPadData
{
    public int Id { get; set; } = 0;
    public int State { get; set; } = 0;
    public decimal Speed { get; set; } = 0;
    public WalkingPadMode Mode { get; set; } = WalkingPadMode.Standby;
    public int Duration { get; set; } = 0;
    public int Steps { get; set; } = 0;
    public decimal LastKnownSpeed { get; set; } = 0;
}