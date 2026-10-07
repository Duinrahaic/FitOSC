namespace FitOSC.Services.Logger;

public enum LogStreamLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}

public sealed record LogStreamEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogStreamLevel Level,
    string Source,
    string Message);
