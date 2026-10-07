namespace FitOSC.Utilities.BLE;

/// <summary>
/// A Bluetooth LE GATT client for one device at a time.
/// Each platform provides its own implementation.
/// </summary>
public interface IBluetoothClient : IAsyncDisposable
{
    /// <summary>
    /// Whether the client is currently connected to a device.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Scans for nearby Bluetooth LE devices that advertise a name.
    /// </summary>
    Task<IReadOnlyList<BluetoothAdvertisement>> ScanAsync(TimeSpan duration);

    /// <summary>
    /// Connects to a BLE device providing the given service UUID.
    /// Optionally filters by device name.
    /// </summary>
    Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to a given characteristic and invokes the callback on notifications.
    /// A null callback only caches the characteristic for later reads and writes.
    /// </summary>
    Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribes from a characteristic.
    /// </summary>
    Task UnsubscribeAsync(Guid characteristicUuid);

    /// <summary>
    /// Writes a command to a given characteristic.
    /// </summary>
    Task WriteAsync(Guid characteristicUuid, byte[] command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current value of a given characteristic.
    /// </summary>
    /// <returns>The raw bytes, or null if the read failed.</returns>
    Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the current device and cleans up resources.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Describes the services and characteristics of the connected device.
    /// </summary>
    Task<string> GetDeviceInfoAsync();
}
