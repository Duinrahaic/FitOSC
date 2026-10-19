namespace FitOSC.Services.Treadmill;

public class TreadmillConfiguration
{
    public decimal MaxSpeed { get; init; } = 0;
    public decimal MinSpeed { get; init; } = 0;
    public decimal SpeedIncrement { get; init; } = 0;
    public decimal MaxIncline { get; init; } = 0;
    public decimal MinIncline { get; init; } = 0;
    public decimal InclineIncrement { get; init; } = 0;

    /// <summary>The commands FitOSC may send. One left out is controlled from the console, or not at all.</summary>
    public TreadmillCommands Commands { get; init; } = TreadmillCommands.None;

    /// <summary>Whether the treadmill takes incline commands and reported a range they can step through.</summary>
    public bool SupportsIncline =>
        Commands.HasFlag(TreadmillCommands.Incline) && MaxIncline > MinIncline && InclineIncrement > 0;

    /// <summary>Whether the treadmill takes the command; incline also needs a reported range.</summary>
    public bool Accepts(TreadmillCommands command) =>
        command == TreadmillCommands.Incline ? SupportsIncline : Commands.HasFlag(command);
}