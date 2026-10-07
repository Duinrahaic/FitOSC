using FitOSC.Platform.Windows.Bluetooth;
using FitOSC.Platform.Windows.Midi;
using FitOSC.Services.Midi;
using FitOSC.Utilities.BLE;

namespace FitOSC.Platform.Windows;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Windows implementations of the platform services FitOSC.Core depends on.
    /// </summary>
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IBluetoothClientFactory, WindowsBluetoothClientFactory>();
        services.AddSingleton<IMidiOutput, NAudioMidiOutput>();
        return services;
    }
}
