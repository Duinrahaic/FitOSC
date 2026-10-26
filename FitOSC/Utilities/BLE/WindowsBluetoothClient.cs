using System;
using System.Collections.Concurrent;
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
        private GattSession? _gattSession;
        private GattDeviceService? _service;
        private BluetoothLEAdvertisementWatcher? _watcher;

        // Addresses of treadmills connected before, by name, shared by every client so a reconnect skips the scan.
        private static readonly ConcurrentDictionary<string, ulong> KnownAddresses = new(StringComparer.OrdinalIgnoreCase);

        // Bounds connecting by a known address, so a treadmill that is off still leaves time for the scan.
        private static readonly TimeSpan KnownAddressTimeout = TimeSpan.FromSeconds(10);

        // Commands and polling write from other threads while connect and teardown change these.
        private readonly ConcurrentDictionary<Guid, GattCharacteristic> _characteristics = new();
        private readonly ConcurrentDictionary<Guid, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>> _subscriptions = new();

        // Services other than the connected one that own cached characteristics; released at teardown.
        private readonly List<GattDeviceService> _otherServices = new();

        private static readonly TimeSpan UnsubscribeTimeout = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

        private Guid _lastServiceUuid;

        // How long the current link has been usable, for diagnosing drops.
        private readonly Stopwatch _linkAge = new();
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private bool _isDisconnecting;

        public override bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

        public override string? DeviceName => _device?.Name;

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

        public override async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName, CancellationToken cancellationToken)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                // Release anything left from a previous session, including one that dropped on its own.
                await TearDownAsync();

                _lastServiceUuid = serviceUuid;
                var sw = Stopwatch.StartNew();

                // A treadmill connected before is opened by its address, skipping the scan: Windows links up as soon as
                // it advertises. The scan remains for a first connect, or when the address no longer answers.
                if (!string.IsNullOrEmpty(deviceName) && KnownAddresses.TryGetValue(deviceName.Trim(), out var known))
                {
                    if (await TryOpenKnownAsync(known, serviceUuid, cancellationToken))
                    {
                        logger.LogInformation("Connected to {Name} by its known address in {Elapsed} ms", deviceName,
                            sw.ElapsedMilliseconds);
                        return true;
                    }

                    logger.LogInformation("{Name} did not answer at its known address; scanning", deviceName);
                }

                try
                {
                    var address = await ScanForDeviceAsync(serviceUuid, deviceName, cancellationToken);
                    logger.LogInformation("Scan completed in {Elapsed} ms", sw.ElapsedMilliseconds);

                    if (!await OpenAsync(address, serviceUuid, cancellationToken))
                        return false;

                    if (!string.IsNullOrEmpty(deviceName))
                        KnownAddresses[deviceName.Trim()] = address;
                    return true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("BLE scan timed out after {Elapsed} ms", sw.ElapsedMilliseconds);
                    await TearDownAsync();
                    return false;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("BLE connection cancelled after {Elapsed} ms", sw.ElapsedMilliseconds);
                    await TearDownAsync();
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to connect after {Elapsed} ms", sw.ElapsedMilliseconds);
                    await TearDownAsync();
                    return false;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Opens a remembered address within <see cref="KnownAddressTimeout"/>. Returns false, leaving nothing open,
        /// when it does not answer in time or fails; only the caller cancelling is thrown.
        /// </summary>
        private async Task<bool> TryOpenKnownAsync(ulong address, Guid serviceUuid, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KnownAddressTimeout);
            try
            {
                return await OpenAsync(address, serviceUuid, timeout.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Opening known address {Addr} failed", address);
                await TearDownAsync();
                return false;
            }
            catch (OperationCanceledException)
            {
                await TearDownAsync();
                throw;
            }
        }

        /// <summary>
        /// Logs every service the device exposes, with each characteristic and its properties, so a device that lacks
        /// the expected service shows what it offers instead.
        /// </summary>
        private async Task LogExposedServicesAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
        {
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (services.Status != GattCommunicationStatus.Success)
            {
                logger.LogWarning("Could not list the services on {Name}: {Status}", device.Name, services.Status);
                return;
            }

            var description = new System.Text.StringBuilder();
            try
            {
                foreach (var service in services.Services)
                {
                    description.Append($"\n  {service.Uuid}");
                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                        .AsTask(cancellationToken);
                    if (characteristics.Status != GattCommunicationStatus.Success)
                    {
                        description.Append($" (characteristics unavailable: {characteristics.Status})");
                        continue;
                    }

                    foreach (var characteristic in characteristics.Characteristics)
                        description.Append($"\n    {characteristic.Uuid} [{characteristic.CharacteristicProperties}]");
                }
            }
            finally
            {
                foreach (var service in services.Services)
                    service.Dispose();
            }

            logger.LogInformation("{Name} exposes {Count} services:{Services}", device.Name, services.Services.Count,
                description.ToString());
        }

        /// <summary>Scans until a matching device advertises and returns its address; throws on timeout or cancellation.</summary>
        private async Task<ulong> ScanForDeviceAsync(Guid serviceUuid, string? deviceName, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);

            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += (sender, args) =>
            {
                try
                {
                    string name = args.Advertisement.LocalName;

                    // A chosen device must match by name; any device offering the service is only taken when none
                    // was chosen, so a neighbouring treadmill with the same service is never connected instead.
                    bool nameMatch = !string.IsNullOrEmpty(deviceName) &&
                                     string.Equals(name?.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase);

                    bool serviceMatch = string.IsNullOrEmpty(deviceName) &&
                                        args.Advertisement.ServiceUuids.Contains(serviceUuid);

                    if (nameMatch || serviceMatch)
                    {
                        logger.LogInformation(
                            "Discovered device {Name} ({Addr}), match={MatchType}",
                            string.IsNullOrEmpty(name) ? "<no name>" : name,
                            args.BluetoothAddress,
                            nameMatch ? "Name" : "Service");

                        tcs.TrySetResult(args.BluetoothAddress);
                        sender.Stop();
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
                using var scanTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                scanTimeout.CancelAfter(ScanTimeout);
                await using var registration = scanTimeout.Token.Register(() => tcs.TrySetCanceled(scanTimeout.Token));
                return await tcs.Task;
            }
            finally
            {
                StopWatcher();
            }
        }

        /// <summary>
        /// Opens the device at an address and its service. A session that keeps the link up is held for the connection,
        /// so Windows neither drops a quiet link nor lingers on one after teardown. Returns false, leaving nothing open,
        /// when the device or service is missing.
        /// </summary>
        private async Task<bool> OpenAsync(ulong address, Guid serviceUuid, CancellationToken cancellationToken)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken);
            if (_device == null)
            {
                logger.LogWarning("Could not connect to device.");
                return false;
            }

            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken);
            _gattSession.SessionStatusChanged += GattSession_SessionStatusChanged;
            _gattSession.MaxPduSizeChanged += GattSession_MaxPduSizeChanged;
            _gattSession.MaintainConnection = true;
            logger.LogInformation("GATT session for {Name} ({Addr:X}): status {Status}, keep-alive {Maintain}, can maintain {CanMaintain}",
                _device.Name, address, _gattSession.SessionStatus, _gattSession.MaintainConnection, _gattSession.CanMaintainConnection);

            // Windows answers from its GATT cache first, which is empty for a device it has not fully
            // discovered yet, so a miss asks the device itself.
            var result = await _device.GetGattServicesForUuidAsync(serviceUuid).AsTask(cancellationToken);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
            {
                logger.LogWarning("Service {ServiceUuid} not found on {Name} (status {Status})", serviceUuid, _device.Name,
                    result.Status);
                await LogExposedServicesAsync(_device, cancellationToken);
                await TearDownAsync();
                return false;
            }

            _service = result.Services[0];
            _linkAge.Restart();
            logger.LogInformation("Connected to {Name} ({Addr:X}) with service {ServiceUuid}; link status {Status}",
                _device.Name, address, serviceUuid, _device.ConnectionStatus);
            return true;
        }

        private void StopWatcher()
        {
            if (_watcher == null)
                return;

            try
            {
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    _watcher.Stop();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error stopping BLE watcher");
            }
            _watcher = null;
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            // Only a drop of the established session counts; teardown and half-made connections are not losses.
            var ignored = sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected ? null
                : sender.BluetoothAddress != _device?.BluetoothAddress ? "another device"
                : _isDisconnecting ? "teardown"
                : _service == null ? "still connecting"
                : null;

            logger.LogInformation("Link status {Status} for {Name} ({Addr:X}) after {Age:F1} s{Ignored}", sender.ConnectionStatus,
                sender.Name, sender.BluetoothAddress, _linkAge.Elapsed.TotalSeconds, ignored == null ? "" : $"; ignored ({ignored})");

            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || ignored != null)
                return;

            logger.LogWarning("Device disconnected: {Name} after {Age:F1} s connected", sender.Name, _linkAge.Elapsed.TotalSeconds);
            RaiseConnectionLost();
        }

        private void GattSession_SessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args) =>
            logger.LogInformation("GATT session status {Status} (error {Error}) after {Age:F1} s", args.Status, args.Error,
                _linkAge.Elapsed.TotalSeconds);

        private void GattSession_MaxPduSizeChanged(GattSession sender, object args) =>
            logger.LogInformation("GATT max PDU size is now {Size}", sender.MaxPduSize);

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

        public override async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback,
            CancellationToken cancellationToken = default)
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
                    result = await _service.GetCharacteristicsForUuidAsync(characteristicUuid).AsTask(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get characteristic {Uuid}. Service may be in invalid state. Attempting to refresh service...", characteristicUuid);

                    // Try to re-acquire the service
                    if (_device != null && await RefreshServiceAsync())
                    {
                        try
                        {
                            result = await _service!.GetCharacteristicsForUuidAsync(characteristicUuid).AsTask(cancellationToken);
                        }
                        catch (Exception ex2)
                        {
                            logger.LogError(ex2, "Failed to get characteristic {Uuid} after service refresh. Connection may be unstable.", characteristicUuid);
                            return false;
                        }
                    }
                    else
                    {
                        logger.LogError("Failed to refresh service for characteristic {Uuid}", characteristicUuid);
                        return false;
                    }
                }

                if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
                {
                    characteristic = result.Characteristics[0];
                }
                else
                {
                    // Some vendor characteristics live outside the service used to connect.
                    var other = await FindCharacteristicInOtherServicesAsync(characteristicUuid, cancellationToken);
                    if (other == null)
                    {
                        logger.LogWarning("Characteristic {Uuid} not found. Status: {Status}", characteristicUuid, result.Status);
                        return false;
                    }

                    characteristic = other;
                }

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
                        try
                        {
                            callback(data);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error handling notification from {Uuid}: {Data}", characteristicUuid, Convert.ToHexString(data));
                        }
                    };

                characteristic.ValueChanged += handler;
                _subscriptions[characteristicUuid] = handler;

                // Check if characteristic supports Notify or Indicate using HasFlag
                if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);

                    if (status != GattCommunicationStatus.Success)
                    {
                        logger.LogWarning("Failed to subscribe to {Uuid}: {Status}", characteristicUuid, status);
                        characteristic.ValueChanged -= handler;
                        _subscriptions.TryRemove(characteristicUuid, out _);
                        return false;
                    }

                    logger.LogDebug("Subscribed to {Uuid} (Notify)", characteristicUuid);
                }
                else if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(cancellationToken);

                    if (status != GattCommunicationStatus.Success)
                    {
                        logger.LogWarning("Failed to subscribe to {Uuid}: {Status}", characteristicUuid, status);
                        characteristic.ValueChanged -= handler;
                        _subscriptions.TryRemove(characteristicUuid, out _);
                        return false;
                    }

                    logger.LogDebug("Subscribed to {Uuid} (Indicate)", characteristicUuid);
                }
                else
                {
                    logger.LogWarning("Characteristic {Uuid} does not support notifications or indications.", characteristicUuid);
                    characteristic.ValueChanged -= handler;
                    _subscriptions.TryRemove(characteristicUuid, out _);
                    return false;
                }
            }
            else
            {
                logger.LogDebug("Characteristic {Uuid} cached without callback", characteristicUuid);
            }

            return true;
        }

        /// <summary>
        /// Searches the connected device's services, other than the one used to connect, for a characteristic.
        /// The service that owns it is kept until teardown; every other enumerated service is released.
        /// </summary>
        private async Task<GattCharacteristic?> FindCharacteristicInOtherServicesAsync(Guid characteristicUuid,
            CancellationToken cancellationToken)
        {
            if (_device == null)
                return null;

            var services = await _device.GetGattServicesAsync().AsTask(cancellationToken);
            if (services.Status != GattCommunicationStatus.Success)
            {
                logger.LogWarning("Failed to enumerate GATT services: {Status}", services.Status);
                return null;
            }

            GattCharacteristic? found = null;
            GattDeviceService? owner = null;
            try
            {
                foreach (var service in services.Services)
                {
                    if (service.Uuid == _lastServiceUuid)
                        continue;

                    var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid).AsTask(cancellationToken);
                    if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
                    {
                        logger.LogDebug("Found characteristic {Uuid} in service {ServiceUuid}", characteristicUuid, service.Uuid);
                        found = result.Characteristics[0];
                        owner = service;
                        lock (_otherServices)
                            _otherServices.Add(service);
                        break;
                    }
                }
            }
            finally
            {
                // Released even when the search fails part-way.
                foreach (var service in services.Services)
                {
                    if (service != owner)
                        service.Dispose();
                }
            }

            return found;
        }

        public override async Task UnsubscribeAsync(Guid characteristicUuid)
        {
            if (_characteristics.TryGetValue(characteristicUuid, out var characteristic) &&
                _subscriptions.TryGetValue(characteristicUuid, out var handler))
            {
                characteristic.ValueChanged -= handler;
                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);

                _subscriptions.TryRemove(characteristicUuid, out _);
                logger.LogInformation("Unsubscribed from {Uuid}", characteristicUuid);
            }
        }

        public bool IsCharacteristicConnected(Guid characteristicUuid)
        {
            return _characteristics.ContainsKey(characteristicUuid);
        }

        public override async Task<string> GetDeviceInfoAsync()
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

            // These are separate handles from the session's own service, so release them once described.
            try
            {
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
            }
            finally
            {
                foreach (var service in servicesResult.Services)
                    service.Dispose();
            }

            return output.ToString();
        }

        public override async Task<bool> WriteAsync(Guid characteristicUuid, byte[] command, bool withResponse = false,
            CancellationToken cancellationToken = default)
        {
            const int maxRetries = 3;
            var retryDelay = TimeSpan.FromMilliseconds(100);

            try
            {
                for (var attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        var service = _service;
                        if (service == null)
                            throw new InvalidOperationException("Not connected to a service.");

                        if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                        {
                            var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid).AsTask(cancellationToken);
                            if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                                throw new InvalidOperationException($"Characteristic {characteristicUuid} not found.");

                            characteristic = result.Characteristics[0];
                            _characteristics[characteristicUuid] = characteristic;
                        }

                        var writeOption = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
                        var status = await characteristic.WriteValueAsync(command.AsBuffer(), writeOption).AsTask(cancellationToken);
                        if (status == GattCommunicationStatus.Success)
                        {
                            logger.LogDebug("Wrote {Command} to {Uuid}", BitConverter.ToString(command), characteristicUuid);
                            return true;
                        }

                        logger.LogWarning("Failed to write to {Uuid}: {Status} (attempt {Attempt}/{Max})",
                            characteristicUuid, status, attempt, maxRetries);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error writing to {Uuid} (attempt {Attempt}/{Max})", characteristicUuid, attempt, maxRetries);
                    }

                    if (attempt < maxRetries)
                        await Task.Delay(retryDelay, cancellationToken);
                }

                logger.LogError("Failed to write to {Uuid} after {Attempts} attempts", characteristicUuid, maxRetries);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public override async Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default)
        {
            if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                return null;

            var result = await characteristic.ReadValueAsync().AsTask(cancellationToken);
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
                await TearDownAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Releases the watcher, subscriptions, service and device. Callers must hold the connection lock.
        /// </summary>
        private async Task TearDownAsync()
        {
            StopWatcher();

            if (_device == null)
                return;

            _isDisconnecting = true;
            try
            {
                logger.LogInformation("Disconnecting from device after {Age:F1} s (link status {Status})...",
                    _linkAge.Elapsed.TotalSeconds, _device.ConnectionStatus);

                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;

                // Clearing notifications needs a live link; after a drop only the handlers are detached.
                var linkUp = IsConnected;
                var unsubscribeTasks = new List<Task>();
                foreach (var kvp in _subscriptions)
                {
                    if (!_characteristics.TryGetValue(kvp.Key, out var characteristic))
                        continue;

                    try
                    {
                        characteristic.ValueChanged -= kvp.Value;
                        if (linkUp)
                            unsubscribeTasks.Add(characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None).AsTask());
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error unsubscribing from characteristic {Uuid}", kvp.Key);
                    }
                }

                if (unsubscribeTasks.Count > 0)
                {
                    try
                    {
                        // A link that stalls must not hold teardown, and with it the next connection, indefinitely.
                        await Task.WhenAll(unsubscribeTasks).WaitAsync(UnsubscribeTimeout).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error during bulk unsubscribe");
                    }
                }

                _subscriptions.Clear();
                _characteristics.Clear();

                lock (_otherServices)
                {
                    foreach (var service in _otherServices)
                        service.Dispose();
                    _otherServices.Clear();
                }

                try
                {
                    _service?.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing GATT service");
                }
                _service = null;

                // Releasing the session lets Windows close the link now rather than when it next collects garbage.
                try
                {
                    if (_gattSession != null)
                    {
                        _gattSession.SessionStatusChanged -= GattSession_SessionStatusChanged;
                        _gattSession.MaxPduSizeChanged -= GattSession_MaxPduSizeChanged;
                        _gattSession.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing GATT session");
                }
                _gattSession = null;

                try
                {
                    _device.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing BLE device");
                }
                _device = null;
                _linkAge.Reset();

                logger.LogInformation("Disconnected successfully.");
            }
            finally
            {
                _isDisconnecting = false;
            }
        }

        protected override async Task OnDispose()
        {
            await DisconnectAsync();
            _connectionLock.Dispose();
        }
    }
}
