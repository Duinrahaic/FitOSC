using FitOSC.Models;

namespace FitOSC.Utilities.BLE.ESPHome;

public static class ESPHomeRelayProbe
{
    /// <summary>
    /// Opens a temporary encrypted API session with the same handshake, API version, and
    /// Bluetooth proxy checks a treadmill connection uses, then closes it. It never scans,
    /// connects to, or writes to a Bluetooth device.
    /// </summary>
    public static async Task ProbeAsync(BluetoothConnectionSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Type != BluetoothConnectionType.ESPHome)
            throw new ArgumentException("Select ESPHome relay to check a relay connection.");
        await using var connection = new ESPHomeApiConnection();
        await connection.ConnectAsync(settings, cancellationToken);
    }
}
