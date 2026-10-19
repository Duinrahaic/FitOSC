using FitOSC.Platform.Linux.Bluetooth;
using FitOSC.Utilities.BLE;
using Microsoft.Extensions.DependencyInjection;

namespace FitOSC.Platform.Linux;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<LinuxBluetoothDiscoveryCoordinator>();
        services.AddSingleton<IBluetoothClientFactory, LinuxBluetoothClientFactory>();
        return services;
    }
}
