using FitOSC.Platform.Windows.Bluetooth;
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
        return services;
    }
}
