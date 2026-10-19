using Serilog.Core;
using Serilog.Events;

namespace FitOSC.Services.Logger;

public sealed class LogStreamSink(LogStreamService stream) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var source = "General";
        if (logEvent.Properties.TryGetValue("SourceContext", out var context))
        {
            var fullName = context is ScalarValue { Value: string name }
                ? name
                : context.ToString().Trim('"');
            source = fullName[(fullName.LastIndexOf('.') + 1)..];
        }

        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += $" | Exception: {logEvent.Exception}";

        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => LogStreamLevel.Verbose,
            LogEventLevel.Debug => LogStreamLevel.Debug,
            LogEventLevel.Information => LogStreamLevel.Information,
            LogEventLevel.Warning => LogStreamLevel.Warning,
            LogEventLevel.Error => LogStreamLevel.Error,
            LogEventLevel.Fatal => LogStreamLevel.Fatal,
            _ => throw new ArgumentOutOfRangeException(nameof(logEvent), logEvent.Level, "Unknown log level.")
        };

        stream.Append(logEvent.Timestamp, level, source, message);
    }
}
