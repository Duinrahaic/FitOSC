namespace FitOSC.Utilities.BLE;

/// <summary>
/// Creates Bluetooth clients for the current platform.
/// </summary>
public interface IBluetoothClientFactory
{
    IBluetoothClient Create();
}
