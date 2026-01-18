using FitOSC.Models;
using FitOSC.Services;
using FitOSC.Services.Configuration;
using FitOSC.Services.Debug;
using FitOSC.Services.History;
using FitOSC.Services.Logger;
using FitOSC.Services.OSC;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using FitOSC.Services.Midi;
using FitOSC.Services.Pulsoid;
using FitOSC.Services.VRChat;
using FitOSC.Services.WebSocket;
using FitOSC.Utilities.BLE;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Valve.VR;

namespace FitOSC.Services;

public static class ServiceExtensions
{
    private static readonly WebViewSink SinkInstance = new WebViewSink();
    private static int _sinkAttached = 0; // 0 = false, 1 = true (for Interlocked operations)
    public static IServiceCollection RegisterServices(this IServiceCollection services)
    {
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<WindowsBluetoothClient>(sp =>
            new WindowsBluetoothClient(sp.GetRequiredService<ILogger<WindowsBluetoothClient>>()));
        services.AddSingleton<TreadmillManager>();
        services.AddSingleton<IOscService,OscService>();
        services.AddSingleton<AppStateService>();

        // Register OpenVRService as both singleton (for injection) and hosted service (for auto-start)
        services.AddSingleton<OpenVRService>();
        services.AddHostedService(sp => sp.GetRequiredService<OpenVRService>());

        // Register VRChatLocomotionService as both singleton (for injection) and hosted service (for auto-start)
        services.AddSingleton<VRChatLocomotionService>();
        services.AddHostedService(sp => sp.GetRequiredService<VRChatLocomotionService>());

        // Register VRChatParameterHandlerService as both singleton (for injection) and hosted service (for auto-start)
        services.AddSingleton<VRChatParameterHandlerService>();
        services.AddHostedService(sp => sp.GetRequiredService<VRChatParameterHandlerService>());

        services.AddSingleton<DebugConsoleService>();
        services.AddSingleton<HistoryService>();

        // Register WebSocketService for telemetry broadcasting
        services.AddSingleton<WebSocketService>();
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketService>());

        // Register PulsoidService for heart rate monitoring
        services.AddSingleton<PulsoidService>();
        services.AddHostedService(sp => sp.GetRequiredService<PulsoidService>());

        // Register MidiService for MIDI input/output
        services.AddSingleton<MidiService>();
        services.AddHostedService(sp => sp.GetRequiredService<MidiService>());

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
        // Thread-safe check-then-act using Interlocked
        if (Interlocked.CompareExchange(ref _sinkAttached, 1, 0) == 0)
        {
            SinkInstance.AttachWebView(core);
        }
    }
}