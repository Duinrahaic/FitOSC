using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace FitOSC.Utilities.BLE
{
    /// <summary>
    /// Abstract base class for Bluetooth client implementations.
    /// Provides a common contract for platform-specific BLE backends.
    /// </summary>
    public abstract class BaseBluetoothClient : IAsyncDisposable
    {
        protected BaseBluetoothClient()
        {
            AppDomain.CurrentDomain.ProcessExit += async (s, e)=> await DisposeAsync(); 
        }
        
        /// <summary>
        /// Whether the client is currently connected to a device.
        /// </summary>
        public abstract bool IsConnected { get; }

        /// <summary>
        /// Connects to a BLE device providing the given service UUID.
        /// Optionally filters by device name.
        /// </summary>
        public abstract Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null);

        /// <summary>
        /// Subscribes to a given characteristic and invokes the callback on notifications.
        /// </summary>
        public abstract Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback);

        /// <summary>
        /// Unsubscribes from a characteristic.
        /// </summary>
        public abstract Task UnsubscribeAsync(Guid characteristicUuid);

        /// <summary>
        /// Queues a write command to a given characteristic.
        /// </summary>
        public abstract Task WriteAsync(Guid characteristicUuid, byte[] command);
        
        /// <summary>
        /// Reads the current value of a given characteristic.
        /// </summary>
        /// <param name="characteristicUuid">The characteristic UUID to read from.</param>
        /// <returns>The raw byte array read from the characteristic.</returns>
        public abstract Task<byte[]?> ReadAsync(Guid characteristicUuid);
        
        /// <summary>
        /// Disconnects from the current device and cleans up resources.
        /// </summary>
        public abstract Task DisconnectAsync();

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
