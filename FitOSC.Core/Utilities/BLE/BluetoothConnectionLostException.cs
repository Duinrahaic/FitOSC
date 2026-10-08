namespace FitOSC.Utilities.BLE;

/// <summary>A Bluetooth operation could not complete because its device session was lost.</summary>
public sealed class BluetoothConnectionLostException : IOException
{
    public BluetoothConnectionLostException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
