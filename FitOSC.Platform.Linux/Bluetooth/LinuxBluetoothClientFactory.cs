using FitOSC.Utilities.BLE;
using Microsoft.Extensions.Logging;

namespace FitOSC.Platform.Linux.Bluetooth;

public sealed class LinuxBluetoothClientFactory(
    ILoggerFactory loggerFactory,
    LinuxBluetoothDiscoveryCoordinator discovery) : IBluetoothClientFactory
{
    public IBluetoothClient Create() =>
        new LinuxBluetoothClient(loggerFactory.CreateLogger<LinuxBluetoothClient>(), discovery);
}
