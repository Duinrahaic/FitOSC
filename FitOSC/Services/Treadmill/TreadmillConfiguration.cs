namespace FitOSC.Services.Treadmill;

public class TreadmillConfiguration
{
    public decimal MaxSpeed { get; init; } = 0;
    public decimal MinSpeed { get; init; } = 0;
    public decimal SpeedIncrement { get; init; } = 0;
    public decimal MaxIncline { get; init; } = 0;
    public decimal MinIncline { get; init; } = 0;
    public decimal InclineIncrement { get; init; } = 0;
}