using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;
using InTheHand.Bluetooth;
using Microsoft.Extensions.Logging;

namespace FitOSC.Utilities.BLE
{
    public class BluetoothClient : BaseBluetoothClient, IAsyncDisposable
    {
        private BluetoothDevice? _device;
        private GattService? _service;

        private readonly Dictionary<Guid, GattCharacteristic> _characteristics = new();
        private readonly Dictionary<Guid, EventHandler<GattCharacteristicValueChangedEventArgs>> _subscriptions = new();
        
        public override bool IsConnected => _device != null;

        private readonly Channel<(Guid characteristicUuid, byte[] command)> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogger _logger;
        private readonly Task _backgroundThread;

        public BluetoothClient(ILogger logger)
        {
            this._logger = logger;
            _channel = Channel.CreateUnbounded<(Guid, byte[])>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            // Start background loop
            _backgroundThread = Task.Run(CommandLoop);
        }
        
        /// <summary>
        ///  Ensure commands are sent sequentially in the background.
        /// </summary>
        private async Task CommandLoop()
        {
            try
            {
                await foreach (var (uuid, command) in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    if (!_characteristics.TryGetValue(uuid, out var characteristic))
                    {
                        _logger.LogWarning("Characteristic {Uuid} not found for write.", uuid);
                        continue;
                    }

                    try
                    {
                        await characteristic.WriteValueWithoutResponseAsync(command);
                        _logger.LogInformation("Sent {Command} to {Uuid}", BitConverter.ToString(command), uuid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error writing to {Uuid}", uuid);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
        
        public override async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null)
        {
            Stopwatch sw = Stopwatch.StartNew();
            sw.Start();
            var options = new RequestDeviceOptions();
            options.Filters.Add(new BluetoothLEScanFilter
            {
                Services = { serviceUuid },
            });
            

            _logger.LogInformation("Scanning for devices...");
            if (!string.IsNullOrEmpty(deviceName))
            {
                options.Filters.Add(new BluetoothLEScanFilter()
                {
                    Name = deviceName!
                });
                _logger.LogInformation("Scanning for device {DeviceName}", deviceName);
            }
            var devices = await Bluetooth.ScanForDevicesAsync(options);
            sw.Stop();
            _logger.LogInformation("Scan completed in {ElapsedMilliseconds} ms", sw.ElapsedMilliseconds);
            if (devices.Count == 0)
            {
                _logger.LogWarning("No devices found.");
                return false;
            }

            _device = devices.First();
            _logger.LogInformation("Selected device: {Name} ({Id})", _device.Name, _device.Id);

            await _device.Gatt.ConnectAsync();

            _service = await _device.Gatt.GetPrimaryServiceAsync(serviceUuid);
            if (_service == null)
            {
                _logger.LogWarning("Service {ServiceUuid} not found on device.", serviceUuid);
                return false;
            }

            return true;
        }
        
        

        /// <summary>
        /// Subscribe to a characteristic and pass notifications to a callback.
        /// </summary>
        public override async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback)
        {
            if (_service == null)
            {
                _logger.LogWarning("Cannot subscribe, service not initialized.");
                return false;
            }

            if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
            {
                characteristic = await _service.GetCharacteristicAsync(characteristicUuid);

                _characteristics[characteristicUuid] = characteristic;
            }

            EventHandler<GattCharacteristicValueChangedEventArgs> handler = (s, args) =>
            {
                if (args.Value != null && callback != null) callback(args.Value);
            };

            characteristic.CharacteristicValueChanged += handler;
            await characteristic.StartNotificationsAsync();

            _subscriptions[characteristicUuid] = handler;

            _logger.LogInformation("Subscribed to {CharacteristicUuid}", characteristicUuid);
            return true;
        }

        /// <summary>
        /// Unsubscribe from a characteristic.
        /// </summary>
        public override async Task UnsubscribeAsync(Guid characteristicUuid)
        {
            if (_characteristics.TryGetValue(characteristicUuid, out var characteristic) &&
                _subscriptions.TryGetValue(characteristicUuid, out var handler))
            {
                characteristic.CharacteristicValueChanged -= handler;
                await characteristic.StopNotificationsAsync();
                _subscriptions.Remove(characteristicUuid);
                _logger.LogInformation("Unsubscribed from {CharacteristicUuid}", characteristicUuid);
            }
        }

        /// <summary>
        /// Queue a command to write to a characteristic.
        /// </summary>
        public override async Task WriteAsync(Guid characteristicUuid, byte[] command)
        { 
            await _channel.Writer.WriteAsync((characteristicUuid, command), _cts.Token);
        }

        public override async Task<byte[]?> ReadAsync(Guid characteristicUuid)
        {
            throw new NotImplementedException();
            await Task.CompletedTask;
        }
        

        public override async Task DisconnectAsync()
        {
            try
            {
                foreach (var kvp in _subscriptions)
                {
                    if (_characteristics.TryGetValue(kvp.Key, out var ch))
                        ch.CharacteristicValueChanged -= kvp.Value;
                }

                foreach (var ch in _characteristics.Values)
                {
                    try { await ch.StopNotificationsAsync(); }
                    catch
                    {
                        // ignored
                    }
                }

                _subscriptions.Clear();
                _characteristics.Clear();

                if (_device != null)
                {
                    _device.Gatt.Disconnect();
                    _logger.LogInformation("Disconnected from {Name}", _device.Name);
                    _device = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while disconnecting");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _channel.Writer.TryComplete();
            try
            {
                await _backgroundThread;
            }
            catch (OperationCanceledException) { }
            _device?.Gatt.Disconnect();
            _cts.Dispose();
 
        }
    }
}
