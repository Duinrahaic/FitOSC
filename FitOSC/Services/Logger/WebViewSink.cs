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

        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string script = $"console.{level}(\"[{source}] {escaped}\");";

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
            _buffer.Enqueue(logEvent);
        }
    }
}

