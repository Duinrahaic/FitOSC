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

        public override bool IsConnected => _device != null;

        public void EnableAutoReconnect(bool enable = true) => _autoReconnect = enable;

        public override async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null)
        {
            _lastServiceUuid = serviceUuid;
            _lastDeviceName = deviceName;

            var tcs = new TaskCompletionSource<ulong>();
            var sw = Stopwatch.StartNew();

            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += (s, args) =>
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
                    _watcher.Stop();
                }
            };

            logger.LogInformation("Starting fast BLE scan...");
            _watcher.Start();

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using (cts.Token.Register(() => tcs.TrySetCanceled())) { }

                ulong btAddress = await tcs.Task;
                sw.Stop();
                logger.LogInformation("Scan completed in {Elapsed} ms", sw.ElapsedMilliseconds);

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
                    return false;
                }

                _service = result.Services[0];
                logger.LogInformation("Connected to {Name} with service {ServiceUuid}", _device.Name, serviceUuid);
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "Failed to connect after {Elapsed} ms", sw.ElapsedMilliseconds);
                return false;
            }
        }

        private async void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                logger.LogWarning("Device disconnected: {Name}", sender.Name);

                if (!_autoReconnect) return;
                logger.LogInformation("Attempting reconnect...");
                await Task.Delay(2000); // small backoff
                await ReconnectAsync();
            }
        }

        private async Task<bool> ReconnectAsync()
        {
            if (_lastServiceUuid != Guid.Empty) return await ConnectAsync(_lastServiceUuid, _lastDeviceName);
            logger.LogWarning("No previous connection parameters stored, cannot reconnect.");
            return false;

        }

        public override async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback)
        {
            if (_service == null)
            {
                logger.LogWarning("No active service.");
                return false;
            }

            if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
            {
                var result = await _service.GetCharacteristicsForUuidAsync(characteristicUuid);
                if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                {
                    logger.LogWarning("Characteristic {Uuid} not found.", characteristicUuid);
                    return false;
                }

                characteristic = result.Characteristics[0];
                _characteristics[characteristicUuid] = characteristic;
            }

            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
                (ch, args) =>
                {
                    if (args.CharacteristicValue == null || callback == null) return;
                    var reader = DataReader.FromBuffer(args.CharacteristicValue);
                    var data = new byte[args.CharacteristicValue.Length];
                    reader.ReadBytes(data);
                    callback(data);
                };

            characteristic.ValueChanged += handler;
            _subscriptions[characteristicUuid] = handler;

            if (characteristic.CharacteristicProperties == GattCharacteristicProperties.Notify)
            {
                var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                if (status != GattCommunicationStatus.Success)
                {
                    logger.LogWarning("Failed to subscribe to {Uuid}: {Status}", characteristicUuid, status);
                    return false;
                }

                logger.LogInformation("Subscribed to {Uuid}", characteristicUuid);
            }
            else
            {
                logger.LogWarning("Characteristic {Uuid} does not support notifications.", characteristicUuid);
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

        public override async Task WriteAsync(Guid characteristicUuid, byte[] command)
        {
            try
            {
                if (_service == null)
                    throw new InvalidOperationException("Not connected to a service.");

                if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                {
                    var result = await _service.GetCharacteristicsForUuidAsync(characteristicUuid);
                    if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                        throw new InvalidOperationException($"Characteristic {characteristicUuid} not found.");

                    characteristic = result.Characteristics[0];
                    _characteristics[characteristicUuid] = characteristic;
                }

                var buffer = command.AsBuffer();
                var status = await characteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);

                if (status == GattCommunicationStatus.Success)
                    logger.LogInformation("Wrote {Command} to {Uuid}", BitConverter.ToString(command), characteristicUuid);
                else
                    logger.LogWarning("Failed to write to {Uuid}: {Status}", characteristicUuid, status);
            }
            catch(Exception ex)
            {
                logger.LogError(ex, "Failed to write to {Uuid}", characteristicUuid);
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
        
        public override Task DisconnectAsync()
        {
            foreach (var kvp in _subscriptions)
            {
                if (_characteristics.TryGetValue(kvp.Key, out var ch))
                    ch.ValueChanged -= kvp.Value;
            }

            _subscriptions.Clear();
            _characteristics.Clear();

            if (_device != null)
                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;

            _service?.Dispose();
            _service = null;

            _device?.Dispose();
            _device = null;

            logger.LogInformation("Disconnected.");
            return Task.CompletedTask;
        }

        protected override Task OnDispose()
        {
            _service?.Dispose();
            _device?.Dispose();
            return Task.CompletedTask;
        }
    }
}
