namespace FitOSC.Utilities.BLE
{
    /// <summary>
    /// Abstract base class for Bluetooth client implementations.
    /// Provides a common contract for platform-specific BLE backends. The owner disposes it.
    /// </summary>
    public abstract class BaseBluetoothClient : IAsyncDisposable
    {
        /// <summary>
        /// Whether the client is currently connected to a device.
        /// </summary>
        public abstract bool IsConnected { get; }

        /// <summary>
        /// Name reported by the connected device, or null when not connected.
        /// </summary>
        public abstract string? DeviceName { get; }

        /// <summary>
        /// The name to show people for a device name from <see cref="ScanAsync"/> or <see cref="DeviceName"/>.
        /// Clients that tell same-named devices apart append the address as " [AA:BB:…]"; this leaves it out.
        /// </summary>
        public static string Label(string deviceName)
        {
            var start = deviceName.LastIndexOf(" [", StringComparison.Ordinal);
            return start > 0 && deviceName.EndsWith(']') ? deviceName[..start] : deviceName;
        }

        /// <summary>
        /// Raised when an established connection drops without <see cref="DisconnectAsync"/> being called.
        /// </summary>
        public event Action? ConnectionLost;

        protected void RaiseConnectionLost() => ConnectionLost?.Invoke();

        /// <summary>
        /// Connects to a BLE device providing the given service UUID.
        /// Optionally filters by device name.
        /// </summary>
        public abstract Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName, CancellationToken cancellationToken);

        /// <summary>
        /// Lists the names of nearby devices, for choosing one to pass to <see cref="ConnectAsync"/>.
        /// </summary>
        /// <param name="duration">How long to listen for advertisements.</param>
        /// <param name="serviceUuidFilter">Optional service UUID; devices advertising other services are left out.</param>
        public abstract Task<IEnumerable<string>> ScanAsync(TimeSpan duration, Guid? serviceUuidFilter = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribes to a given characteristic and invokes the callback on notifications.
        /// A null callback only resolves and caches the characteristic.
        /// </summary>
        public abstract Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribes from a characteristic.
        /// </summary>
        public abstract Task UnsubscribeAsync(Guid characteristicUuid);

        /// <summary>
        /// Queues a write command to a given characteristic.
        /// </summary>
        /// <param name="characteristicUuid">The characteristic UUID to write to.</param>
        /// <param name="command">The raw bytes to write.</param>
        /// <param name="withResponse">Whether to use an acknowledged GATT write instead of write-without-response.</param>
        /// <returns>Whether the write reached the device; false once <paramref name="cancellationToken"/> is cancelled.</returns>
        public abstract Task<bool> WriteAsync(Guid characteristicUuid, byte[] command, bool withResponse = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads the current value of a given characteristic.
        /// </summary>
        /// <param name="characteristicUuid">The characteristic UUID to read from.</param>
        /// <returns>The raw byte array read from the characteristic.</returns>
        public abstract Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnects from the current device and cleans up resources.
        /// </summary>
        public abstract Task DisconnectAsync();

        /// <summary>
        /// Describes the connected device and its GATT services, for debugging.
        /// </summary>
        public abstract Task<string> GetDeviceInfoAsync();

        /// <summary>
        /// Dispose logic for async cleanup.
        /// Calls DisconnectAsync and then calls OnDispose for subclass-specific cleanup.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            await OnDispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Allows subclasses to implement additional cleanup.
        /// </summary>
        protected virtual Task OnDispose() => Task.CompletedTask;
    }
}
