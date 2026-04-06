using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace FitOSC.Utilities.BLE
{
    public class WindowsBluetoothClient(ILogger logger) : BaseBluetoothClient
    {
        private BluetoothLEDevice? _device;
        private GattDeviceService? _service;
        private BluetoothLEAdvertisementWatcher? _watcher;

        private readonly Dictionary<Guid, GattCharacteristic> _characteristics = new();
        private readonly Dictionary<Guid, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>> _subscriptions = new();

        private Guid _lastServiceUuid;
        private string? _lastDeviceName;
        private bool _autoReconnect = true;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private bool _isDisconnecting;

        public override bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

        public void EnableAutoReconnect(bool enable = true) => _autoReconnect = enable;

        /// <summary>
        /// Scans for all available Bluetooth LE devices and returns their names.
        /// </summary>
        /// <param name="scanDuration">How long to scan for devices</param>
        /// <param name="serviceUuidFilter">Optional service UUID to filter devices by</param>
        /// <returns>List of discovered device names</returns>
        public async Task<IEnumerable<string>> ScanAsync(TimeSpan scanDuration, Guid? serviceUuidFilter = null)
        {
            var discoveredDevices = new HashSet<string>();
            var tcs = new TaskCompletionSource<bool>();

            BluetoothLEAdvertisementWatcher? scanWatcher = null;

            try
            {
                scanWatcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };

                scanWatcher.Received += (sender, args) =>
                {
                    try
                    {
                        string name = args.Advertisement.LocalName;

                        // If service filter is specified, prefer devices that advertise it
                        // but also include devices that don't advertise any services
                        // (many devices only expose services after connection)
                        if (serviceUuidFilter.HasValue)
                        {
                            bool hasServiceUuids = args.Advertisement.ServiceUuids.Count > 0;
                            bool matchesService = args.Advertisement.ServiceUuids.Contains(serviceUuidFilter.Value);

                            // Skip only if device advertises services but doesn't match our filter
                            if (hasServiceUuids && !matchesService)
                            {
                                return;
                            }
                        }

                        if (!string.IsNullOrEmpty(name) && !discoveredDevices.Contains(name))
                        {
                            discoveredDevices.Add(name);
                            logger.LogDebug("Discovered device: {Name} ({Address})", name, args.BluetoothAddress);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error processing BLE advertisement");
                    }
                };

                logger.LogInformation("Starting BLE scan for {Duration} seconds{Filter}...",
                    scanDuration.TotalSeconds,
                    serviceUuidFilter.HasValue ? $" (filtering by service {serviceUuidFilter.Value})" : "");
                scanWatcher.Start();

                // Wait for the scan duration
                await Task.Delay(scanDuration);

                // Stop the watcher
                scanWatcher.Stop();
                logger.LogInformation("BLE scan completed. Found {Count} devices.", discoveredDevices.Count);

                return discoveredDevices.OrderBy(d => d).ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during BLE scan");
                return Enumerable.Empty<string>();
            }
            finally
            {
                if (scanWatcher != null)
                {
                    try
                    {
                        if (scanWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                        {
                            scanWatcher.Stop();
                        }
                    }
                    catch { }
                }
            }
        }

        public override async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null)
        {
            await _connectionLock.WaitAsync();
            try
            {
                // Disconnect existing connection if any
                if (IsConnected)
                {
                    logger.LogInformation("Disconnecting existing connection before reconnecting...");
                    _connectionLock.Release();
                    await DisconnectAsync();
                    await _connectionLock.WaitAsync();
                }

                _lastServiceUuid = serviceUuid;
                _lastDeviceName = deviceName;
                _autoReconnect = true; // Re-enable auto-reconnect for new connection

                var tcs = new TaskCompletionSource<ulong>();
                var sw = Stopwatch.StartNew();

                // Clean up any existing watcher
                if (_watcher != null)
                {
                    try
                    {
                        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                        {
                            _watcher.Stop();
                        }
                    }
                    catch { }
                    _watcher = null;
                }

                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };

                _watcher.Received += (sender, args) =>
                {
                    try
                    {
                        string name = args.Advertisement.LocalName;

                        bool nameMatch = !string.IsNullOrEmpty(deviceName) &&
                                         !string.IsNullOrEmpty(name) &&
                                         name.Contains(deviceName, StringComparison.OrdinalIgnoreCase);

                        bool serviceMatch = args.Advertisement.ServiceUuids.Contains(serviceUuid);

                        if (nameMatch || serviceMatch)
                        {
                            logger.LogInformation(
                                "Discovered device {Name} ({Addr}), match={MatchType}",
                                string.IsNullOrEmpty(name) ? "<no name>" : name,
                                args.BluetoothAddress,
                                nameMatch ? "Name" : "Service");

                            tcs.TrySetResult(args.BluetoothAddress);

                            // Use sender parameter instead of _watcher to avoid null reference
                            if (sender is BluetoothLEAdvertisementWatcher watcher)
                            {
                                watcher.Stop();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error in BLE advertisement received handler");
                    }
                };

                logger.LogInformation("Starting BLE scan for service {ServiceUuid}, device name filter: {DeviceName}...",
                    serviceUuid, deviceName ?? "<any>");
                _watcher.Start();

                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await using (cts.Token.Register(() => tcs.TrySetCanceled())) { }

                    ulong btAddress = await tcs.Task;
                    sw.Stop();
                    logger.LogInformation("Scan completed in {Elapsed} ms", sw.ElapsedMilliseconds);

                    // Stop and cleanup watcher after successful scan
                    _watcher.Stop();
                    _watcher = null;

                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(btAddress);
                    if (_device == null)
                    {
                        logger.LogWarning("Could not connect to device.");
                        return false;
                    }

                    _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

                    var result = await _device.GetGattServicesForUuidAsync(serviceUuid);
                    if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                    {
                        logger.LogWarning("Service {ServiceUuid} not found on {Name}", serviceUuid, _device.Name);
                        await CleanupFailedConnectionAsync();
                        return false;
                    }

                    _service = result.Services[0];
                    logger.LogInformation("Connected to {Name} with service {ServiceUuid}", _device.Name, serviceUuid);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    logger.LogWarning("BLE scan timed out after {Elapsed} ms", sw.ElapsedMilliseconds);
                    await CleanupFailedConnectionAsync();
                    return false;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    logger.LogError(ex, "Failed to connect after {Elapsed} ms", sw.ElapsedMilliseconds);
                    await CleanupFailedConnectionAsync();
                    return false;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task CleanupFailedConnectionAsync()
        {
            try
            {
                if (_watcher != null)
                {
                    if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    {
                        _watcher.Stop();
                    }
                    _watcher = null;
                }

                if (_device != null)
                {
                    _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                    _device.Dispose();
                    _device = null;
                }

                _service?.Dispose();
                _service = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during failed connection cleanup");
            }
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                logger.LogWarning("Device disconnected: {Name}", sender.Name);

                if (!_autoReconnect || _isDisconnecting)
                {
                    logger.LogInformation("Auto-reconnect disabled or intentional disconnect, not attempting reconnection.");
                    return;
                }

                // Fire and forget reconnection on background thread
                _ = Task.Run(async () =>
                {
                    try
                    {
                        logger.LogInformation("Attempting reconnect in 2 seconds...");
                        await Task.Delay(2000);
                        await ReconnectAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during auto-reconnect");
                    }
                });
            }
        }

        private async Task<bool> ReconnectAsync()
        {
            if (_lastServiceUuid == Guid.Empty)
            {
                logger.LogWarning("No previous connection parameters stored, cannot reconnect.");
                return false;
            }

            logger.LogInformation("Reconnecting to {DeviceName}...", _lastDeviceName ?? "<unknown>");
            return await ConnectAsync(_lastServiceUuid, _lastDeviceName);
        }

        private async Task<bool> RefreshServiceAsync()
        {
            try
            {
                if (_device == null || _lastServiceUuid == Guid.Empty)
                {
                    logger.LogWarning("Cannot refresh service: device or service UUID not available.");
                    return false;
                }

                logger.LogInformation("Refreshing GATT service {ServiceUuid}...", _lastServiceUuid);

                // Dispose old service
                _service?.Dispose();
                _service = null;
                _characteristics.Clear();

                // Re-acquire service from device
                var result = await _device.GetGattServicesForUuidAsync(_lastServiceUuid);
                if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                {
                    logger.LogWarning("Failed to refresh service {ServiceUuid}: {Status}", _lastServiceUuid, result.Status);
                    return false;
                }

                _service = result.Services[0];
                logger.LogInformation("Service refreshed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error refreshing service");
                return false;
            }
        }

        public override async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback)
        {
            if (_service == null)
            {
                logger.LogWarning("No active service.");
                return false;
            }

            // Validate device is still connected
            if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                logger.LogWarning("Device not connected, cannot subscribe to characteristic {Uuid}", characteristicUuid);
                return false;
            }

            if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
            {
                // Check if service is available
                if (_service == null)
                {
                    logger.LogError("Service is null, cannot get characteristic {Uuid}", characteristicUuid);
                    return false;
                }

                GattCharacteristicsResult result;
                try
                {
                    result = await _service.GetCharacteristicsForUuidAsync(characteristicUuid);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get characteristic {Uuid}. Service may be in invalid state. Attempting to refresh service...", characteristicUuid);

                    // Try to re-acquire the service
                    if (_device != null && await RefreshServiceAsync())
                    {
                        try
                        {
                            result = await _service!.GetCharacteristicsForUuidAsync(characteristicUuid);
                        }
                        catch (Exception ex2)
                        {
                            logger.LogError(ex2, "Failed to get characteristic {Uuid} after service refresh. Connection may be unstable.", characteristicUuid);

                            // Service is in a bad state, trigger cleanup and reconnect
                            _ = Task.Run(async () =>
                            {
                                await DisconnectAsync();
                                if (_autoReconnect)
                                {
                                    await Task.Delay(2000); // Brief delay before reconnect
                                    await ReconnectAsync();
                                }
                            });

                            return false;
                        }
                    }
                    else
                    {
                        logger.LogError("Failed to refresh service for characteristic {Uuid}. Triggering reconnect...", characteristicUuid);

                        // Service refresh failed, trigger cleanup and reconnect
                        _ = Task.Run(async () =>
                        {
                            await DisconnectAsync();
                            if (_autoReconnect)
                            {
                                await Task.Delay(2000); // Brief delay before reconnect
                                await ReconnectAsync();
                            }
                        });

                        return false;
                    }
                }

                if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                {
                    logger.LogWarning("Characteristic {Uuid} not found. Status: {Status}", characteristicUuid, result.Status);
                    return false;
                }

                characteristic = result.Characteristics[0];
                _characteristics[characteristicUuid] = characteristic;
            }

            // Only subscribe if callback is provided
            if (callback != null)
            {
                TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
                    (ch, args) =>
                    {
                        if (args.CharacteristicValue == null) return;
                        var reader = DataReader.FromBuffer(args.CharacteristicValue);
                        var data = new byte[args.CharacteristicValue.Length];
                        reader.ReadBytes(data);
                        callback(data);
                    };

                characteristic.ValueChanged += handler;
                _subscriptions[characteristicUuid] = handler;

                // Check if characteristic supports Notify or Indicate using HasFlag
                if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);

                    if (status != GattCommunicationStatus.Success)
                    {
                        logger.LogWarning("Failed to subscribe to {Uuid}: {Status}", characteristicUuid, status);
                        characteristic.ValueChanged -= handler;
                        _subscriptions.Remove(characteristicUuid);
                        return false;
                    }

                    logger.LogDebug("Subscribed to {Uuid} (Notify)", characteristicUuid);
                }
                else if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Indicate);

                    if (status != GattCommunicationStatus.Success)
                    {
                        logger.LogWarning("Failed to subscribe to {Uuid}: {Status}", characteristicUuid, status);
                        characteristic.ValueChanged -= handler;
                        _subscriptions.Remove(characteristicUuid);
                        return false;
                    }

                    logger.LogDebug("Subscribed to {Uuid} (Indicate)", characteristicUuid);
                }
                else
                {
                    logger.LogWarning("Characteristic {Uuid} does not support notifications or indications.", characteristicUuid);
                    characteristic.ValueChanged -= handler;
                    _subscriptions.Remove(characteristicUuid);
                    return false;
                }
            }
            else
            {
                logger.LogDebug("Characteristic {Uuid} cached without callback", characteristicUuid);
            }

            return true;
        }

        public override async Task UnsubscribeAsync(Guid characteristicUuid)
        {
            if (_characteristics.TryGetValue(characteristicUuid, out var characteristic) &&
                _subscriptions.TryGetValue(characteristicUuid, out var handler))
            {
                characteristic.ValueChanged -= handler;
                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);

                _subscriptions.Remove(characteristicUuid);
                logger.LogInformation("Unsubscribed from {Uuid}", characteristicUuid);
            }
        }

        public bool IsCharacteristicConnected(Guid characteristicUuid)
        {
            return _characteristics.ContainsKey(characteristicUuid);
        }

        public async Task<string> GetDeviceInfo()
        {
            if (_device == null || _service == null)
            {
                return "No device connected";
            }

            var output = new System.Text.StringBuilder();
            output.AppendLine($"\n🔍 Connected Device Information");
            output.AppendLine("═══════════════════════════════════════════════");
            output.AppendLine($"Device Name: {_device.Name}");
            output.AppendLine($"Address: {_device.BluetoothAddress:X}");
            output.AppendLine($"Connection Status: {_device.ConnectionStatus}");
            output.AppendLine();

            // Get all services
            var servicesResult = await _device.GetGattServicesAsync();
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                return output.ToString() + "Error: Could not retrieve services";
            }

            output.AppendLine($"Services Found: {servicesResult.Services.Count}");
            output.AppendLine("─────────────────────────────────────────────");

            foreach (var service in servicesResult.Services)
            {
                output.AppendLine($"\n📦 Service UUID: {service.Uuid}");

                // Get characteristics for this service
                var charResult = await service.GetCharacteristicsAsync();
                if (charResult.Status == GattCommunicationStatus.Success)
                {
                    output.AppendLine($"   Characteristics: {charResult.Characteristics.Count}");

                    foreach (var characteristic in charResult.Characteristics)
                    {
                        output.AppendLine($"   ├─ {characteristic.Uuid}");
                        output.AppendLine($"   │  Properties: {characteristic.CharacteristicProperties}");
                    }
                }
            }

            return output.ToString();
        }

        public override async Task WriteAsync(Guid characteristicUuid, byte[] command)
        {
            const int maxRetries = 3;
            int attemptCount = 0;
            Exception? lastException = null;

            while (attemptCount < maxRetries)
            {
                try
                {
                    attemptCount++;

                    if (_service == null)
                    {
                        if (attemptCount < maxRetries)
                        {
                            logger.LogWarning("Not connected to service, retrying... (Attempt {Attempt}/{Max})", attemptCount, maxRetries);
                            await Task.Delay(100); // Brief delay before retry
                            continue;
                        }
                        throw new InvalidOperationException("Not connected to a service.");
                    }

                    if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                    {
                        var result = await _service.GetCharacteristicsForUuidAsync(characteristicUuid);
                        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                        {
                            if (attemptCount < maxRetries)
                            {
                                logger.LogWarning("Characteristic {Uuid} not found, retrying... (Attempt {Attempt}/{Max})",
                                    characteristicUuid, attemptCount, maxRetries);
                                await Task.Delay(100);
                                continue;
                            }
                            throw new InvalidOperationException($"Characteristic {characteristicUuid} not found.");
                        }

                        characteristic = result.Characteristics[0];
                        _characteristics[characteristicUuid] = characteristic;
                    }

                    var buffer = command.AsBuffer();
                    var status = await characteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);

                    if (status == GattCommunicationStatus.Success)
                    {
                        logger.LogDebug("Wrote {Command} to {Uuid}", BitConverter.ToString(command), characteristicUuid);
                        return; // Success - exit the method
                    }
                    else
                    {
                        if (attemptCount < maxRetries)
                        {
                            logger.LogWarning("Failed to write to {Uuid}: {Status}, retrying... (Attempt {Attempt}/{Max})",
                                characteristicUuid, status, attemptCount, maxRetries);
                            await Task.Delay(100);
                            continue;
                        }
                        logger.LogWarning("Failed to write to {Uuid}: {Status}", characteristicUuid, status);
                        return; // Exit after max retries
                    }
                }
                catch(Exception ex)
                {
                    lastException = ex;
                    if (attemptCount < maxRetries)
                    {
                        logger.LogWarning(ex, "Error writing to {Uuid}, retrying... (Attempt {Attempt}/{Max})",
                            characteristicUuid, attemptCount, maxRetries);
                        await Task.Delay(100);
                    }
                    else
                    {
                        logger.LogError(ex, "Failed to write to {Uuid} after {Attempts} attempts", characteristicUuid, maxRetries);
                    }
                }
            }
        }
        
        public override async Task<byte[]?> ReadAsync(Guid characteristicUuid)
        {
            if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                return null;

            var result = await characteristic.ReadValueAsync();
            if (result.Status != GattCommunicationStatus.Success)
                return null;

            var reader = DataReader.FromBuffer(result.Value);
            var buffer = new byte[result.Value.Length];
            reader.ReadBytes(buffer);
            return buffer;
        }
        
        public override async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_isDisconnecting || _device == null)
                {
                    return;
                }

                _isDisconnecting = true;
                _autoReconnect = false; // Prevent reconnection during intentional disconnect

                logger.LogInformation("Disconnecting from device...");

                // Stop and dispose watcher if active
                if (_watcher != null)
                {
                    try
                    {
                        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                        {
                            _watcher.Stop();
                        }
                        _watcher = null;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error stopping BLE watcher");
                    }
                }

                // Unsubscribe from all characteristics
                var unsubscribeTasks = new List<Task>();
                foreach (var kvp in _subscriptions.ToList())
                {
                    if (_characteristics.TryGetValue(kvp.Key, out var characteristic))
                    {
                        try
                        {
                            characteristic.ValueChanged -= kvp.Value;
                            unsubscribeTasks.Add(characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None).AsTask());
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error unsubscribing from characteristic {Uuid}", kvp.Key);
                        }
                    }
                }

                // Wait for all unsubscribe operations with timeout
                if (unsubscribeTasks.Any())
                {
                    try
                    {
                        await Task.WhenAll(unsubscribeTasks).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error during bulk unsubscribe");
                    }
                }

                _subscriptions.Clear();
                _characteristics.Clear();

                // Unregister connection status handler
                if (_device != null)
                {
                    try
                    {
                        _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error unregistering connection status handler");
                    }
                }

                // Dispose service
                if (_service != null)
                {
                    try
                    {
                        _service.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error disposing GATT service");
                    }
                    _service = null;
                }

                // Dispose device
                if (_device != null)
                {
                    try
                    {
                        _device.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error disposing BLE device");
                    }
                    _device = null;
                }

                logger.LogInformation("Disconnected successfully.");
            }
            finally
            {
                _isDisconnecting = false;
                _connectionLock.Release();
            }
        }

        protected override async Task OnDispose()
        {
            await DisconnectAsync();
            _connectionLock.Dispose();
        }
    }
}
