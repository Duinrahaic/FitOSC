using System.Reflection;
using FitOSC.Platform.Windows;
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
using Serilog;
using Valve.VR;

namespace FitOSC.Services;

public static class ServiceExtensions
{
    public static IServiceCollection RegisterServices(this IServiceCollection services)
    {
        services.AddSingleton<ConfigurationService>();
        services.AddWindowsPlatform();
        services.AddSingleton<TreadmillManager>();
        services.AddSingleton<IOscService, OscService>();
        services.AddSingleton<AppStateService>();
        services.AddSingleton<AppLifecycleService>();
        services.AddHostedService(sp => sp.GetRequiredService<AppLifecycleService>());

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
        services.AddSingleton<OnboardingService>();

        // Register WebSocketService for telemetry broadcasting
        services.AddSingleton<WebSocketService>();
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketService>());

        // Register PulsoidService for heart rate monitoring
        services.AddSingleton<PulsoidService>();
        services.AddHostedService(sp => sp.GetRequiredService<PulsoidService>());

        // Register MidiService for MIDI input/output
        services.AddSingleton<MidiService>();
        services.AddHostedService(sp => sp.GetRequiredService<MidiService>());

        // Register UpdateCheckService for GitHub update checking
        services.AddSingleton(sp => new UpdateCheckService(
            sp.GetRequiredService<ILogger<UpdateCheckService>>(), GetApplicationVersion()));
        services.AddHostedService(sp => sp.GetRequiredService<UpdateCheckService>());

        return services;
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(Program).Assembly;
        // Try to get InformationalVersion first (supports semantic versioning with pre-release tags)
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (infoVersionAttr != null && !string.IsNullOrEmpty(infoVersionAttr.InformationalVersion))
        {
            var version = infoVersionAttr.InformationalVersion;
            // Strip build metadata (everything after '+')
            var plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
            {
                version = version.Substring(0, plusIndex);
            }
            return version;
        }

        // Fallback to numeric version
        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion != null ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}" : "0.0.0";
    }

    public static ILoggingBuilder RegisterLogger(this ILoggingBuilder builder, LogStreamService logStream)
    {
        builder
            .ClearProviders()
            .AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.Sink(new LogStreamSink(logStream))
                .CreateLogger());
        return builder;
    }
}
