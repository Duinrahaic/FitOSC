using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;
using Serilog.Core;
using Serilog.Events;

namespace FitOSC.Services.Logger;

public class WebViewSink() : ILogEventSink
{
    private CoreWebView2? _webView;
    private readonly ConcurrentQueue<LogEvent> _buffer = new();
    private volatile bool _isReady = false;
    private const int MaxBufferSize = 500; // Prevent unbounded memory growth

    public void AttachWebView(CoreWebView2 webView)
    {
        _webView = webView;
        _isReady = true;

        // Flush buffered logs
        while (_buffer.TryDequeue(out var logEvent))
        {
            EmitToWebView(logEvent);
        }
    }
    
    private void EmitToWebView(LogEvent logEvent)
    {
        if (_webView == null) return;

        string level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "debug",
            LogEventLevel.Debug => "debug",
            LogEventLevel.Information => "info",
            LogEventLevel.Warning => "warn",
            LogEventLevel.Error => "error",
            LogEventLevel.Fatal => "error",
            _ => "log"
        };

        string message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += $" | Exception: {logEvent.Exception}";

        string source = "General";
        if (logEvent.Properties.TryGetValue("SourceContext", out var context))
        {
            var fullName = context.ToString().Trim('"');
            source = fullName.Split('.').Last();
        }

        // Serialized as a JSON string so line breaks, such as in stack traces, cannot break the script and drop the line.
        var line = System.Text.Json.JsonSerializer.Serialize($"{logEvent.Timestamp:HH:mm:ss.fff} [{source}] {message}");
        string script = $"console.{level}({line});";

        _ = _webView.ExecuteScriptAsync(script);
    }
    
    public void Emit(LogEvent logEvent)
    {
        if (_isReady && _webView != null)
        {
            EmitToWebView(logEvent);
        }
        else
        {
            // Drop oldest logs if buffer is full to prevent unbounded memory growth
            while (_buffer.Count >= MaxBufferSize)
            {
                _buffer.TryDequeue(out _);
            }
            _buffer.Enqueue(logEvent);
        }
    }
}

