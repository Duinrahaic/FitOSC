using FitOSC.Utilities.BLE;

namespace FitOSC.Platform.Windows.Bluetooth;

/// <summary>
/// Creates WinRT-based Bluetooth LE clients.
/// </summary>
public class WindowsBluetoothClientFactory(ILoggerFactory loggerFactory) : IBluetoothClientFactory
{
    public IBluetoothClient Create() => new WindowsBluetoothClient(loggerFactory.CreateLogger<WindowsBluetoothClient>());
}
