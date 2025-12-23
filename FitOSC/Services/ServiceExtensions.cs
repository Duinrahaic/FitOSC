using FitOSC.Models;
using FitOSC.Services;
using FitOSC.Services.Logger;
using FitOSC.Services.OSC;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Valve.VR;

namespace FitOSC.Services;

public static class ServiceExtension
{
    private static readonly WebViewSink SinkInstance = new WebViewSink();
    private static bool _sinkAttached = false;
    public static IServiceCollection RegisterServices(this IServiceCollection services)
    {
        services.AddSingleton<TreadmillManager>();
        services.AddSingleton<IOscService,OscService>();
        services.AddSingleton<AppStateService>();
        services.AddSingleton<OpenVRService>();
        return services;
    }
    
    public static ILoggingBuilder RegisterLogger(this ILoggingBuilder builder)
    {
        builder
            .ClearProviders()
            .AddSerilog( new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.Sink(SinkInstance)
                .CreateLogger());   
        return builder; 
    }
 
    public static void AttachWebViewConsole(this CoreWebView2 core)
    {
        if(_sinkAttached) return;
        _sinkAttached = true;
        SinkInstance.AttachWebView(core);
    }
}