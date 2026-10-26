using FitOSC.Models;

namespace FitOSC.Utilities.BLE;

public sealed class BluetoothClientFactory(ILoggerFactory loggerFactory)
{
    public BaseBluetoothClient Create(BluetoothConnectionSettings settings)
    {
        settings.Validate();
        return settings.Type switch
        {
            BluetoothConnectionType.Local => new WindowsBluetoothClient(loggerFactory.CreateLogger<WindowsBluetoothClient>()),
            BluetoothConnectionType.ESPHome => new ESPHomeBluetoothClient(settings, loggerFactory.CreateLogger<ESPHomeBluetoothClient>()),
            _ => throw new ArgumentException("Unsupported Bluetooth connection.")
        };
    }
}
