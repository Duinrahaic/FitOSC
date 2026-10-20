using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FitOSC.Models;
using FitOSC.Utilities.BLE.ESPHome;
using FitOSC.Utilities.BLE.ESPHome.Protocol;
using Google.Protobuf;

namespace FitOSC.Utilities.BLE;

/// <summary>BLE operations forwarded directly to an ESPHome active Bluetooth proxy.</summary>
public sealed class ESPHomeBluetoothClient : BaseBluetoothClient
{
    private readonly BluetoothConnectionSettings _settings;
    private readonly ILogger<ESPHomeBluetoothClient> _logger;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<ulong, DiscoveredDevice> _devices = new();
    private readonly ConcurrentDictionary<Guid, BluetoothGATTCharacteristic> _characteristics = new();
    private readonly ConcurrentDictionary<uint, Action<byte[]>> _subscriptions = new();
    private ESPHomeApiConnection? _session;
    private ulong _address;
    private string? _deviceName;
    private volatile bool _ready;
    private volatile bool _scanning;
    private TaskCompletionSource? _sought;
    private ulong _soughtAddress;
    private long _generation;

    private sealed record DiscoveredDevice(ulong Address, uint AddressType, string Name)
    {
        public string DisplayName => $"{Name} [{FormatAddress(Address)}]";
    }

    public ESPHomeBluetoothClient(BluetoothConnectionSettings settings, ILogger<ESPHomeBluetoothClient> logger)
    {
        _settings = settings with { };
        _logger = logger;
    }

    public override bool IsConnected => _ready && _session?.IsConnected == true;

    public override string? DeviceName => IsConnected ? _deviceName : null;

    /// <summary>Whether the encrypted API session to the relay is open, with or without a treadmill.</summary>
    public bool IsRelayConnected => _session?.IsConnected == true;

    private async Task<ESPHomeApiConnection> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (_session?.IsConnected == true)
            return _session;
        if (_session != null)
            await CloseSessionAsync();

        _devices.Clear();
        var session = new ESPHomeApiConnection();
        _session = session;
        session.PacketReceived += HandlePacket;
        session.ConnectionClosed += HandleSessionClosed;
        try
        {
            await session.ConnectAsync(_settings, cancellationToken);
            // ESPHome routes BLE replies through the advertisements subscriber.
            // Keep that subscription for the whole API session, not just scans.
            await session.SendAsync(ApiMessageType.SubscribeBluetoothLeAdvertisementsRequest,
                new SubscribeBluetoothLEAdvertisementsRequest { Flags = 1 }, cancellationToken);
            _logger.LogInformation("Connected directly to ESPHome proxy {Host}:{Port}", _settings.Host, _settings.Port);
            return session;
        }
        catch
        {
            await CloseSessionAsync();
            throw;
        }
    }

    public override async Task<IEnumerable<string>> ScanAsync(TimeSpan duration, Guid? serviceUuidFilter = null,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var session = await GetSessionAsync(cancellationToken);
            await ScanDevicesAsync(session, duration, null, cancellationToken);
            // Like local discovery, include named devices even if their service
            // UUID is only available after connecting.
            return _devices.Values.Select(device => device.DisplayName).OrderBy(name => name).ToList();
        }
        finally { _operations.Release(); }
    }

    /// <summary>Collects advertisements for <paramref name="duration"/>, or until <paramref name="sought"/> advertises.</summary>
    private async Task ScanDevicesAsync(ESPHomeApiConnection session, TimeSpan duration, ulong? sought,
        CancellationToken cancellationToken)
    {
        _devices.Clear();
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _soughtAddress = sought ?? 0;
        _sought = sought.HasValue ? found : null;
        _scanning = true;
        try
        {
            await Task.WhenAny(found.Task, Task.Delay(duration, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsConnected)
                throw new IOException("The ESPHome proxy disconnected during discovery.");
        }
        finally
        {
            _scanning = false;
            _sought = null;
        }
    }

    /// <summary>
    /// Connects through the relay to a device chosen from <see cref="ScanAsync"/>. Returns false, releasing the device
    /// but keeping the relay session, when it lacks <paramref name="serviceUuid"/>; throws with a reason the user can
    /// act on when the relay or the device cannot be reached.
    /// </summary>
    public override async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Select a treadmill discovered by the ESPHome proxy.");
        var selectedAddress = ParseSelectedAddress(deviceName);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            await DisconnectDeviceAsync();
            var session = await GetSessionAsync(cancellationToken);
            _devices.TryGetValue(selectedAddress, out var device);
            if (device == null)
            {
                await ScanDevicesAsync(session, TimeSpan.FromSeconds(10), selectedAddress, cancellationToken);
                _devices.TryGetValue(selectedAddress, out device);
            }
            if (device == null)
                throw new InvalidOperationException("The selected treadmill was not found near the proxy. Scan again with the treadmill powered on.");

            // A previous PC/API session can leave this BLE link alive on the
            // proxy. Reset it before discovering a fresh service database.
            await session.RequestAsync(ApiMessageType.BluetoothDeviceRequest,
                new BluetoothDeviceRequest { Address = device.Address, RequestType = BluetoothDeviceRequestType.Disconnect },
                ApiMessageType.BluetoothDeviceConnectionResponse, BluetoothDeviceConnectionResponse.Parser,
                response => response.Address == device.Address && !response.Connected,
                cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(5));
            _address = device.Address;
            _deviceName = device.DisplayName;
            var connected = await session.RequestAsync(ApiMessageType.BluetoothDeviceRequest,
                new BluetoothDeviceRequest
                {
                    Address = device.Address,
                    RequestType = BluetoothDeviceRequestType.ConnectV3WithoutCache,
                    HasAddressType = true,
                    AddressType = device.AddressType
                }, ApiMessageType.BluetoothDeviceConnectionResponse, BluetoothDeviceConnectionResponse.Parser,
                response => response.Address == device.Address, cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(20));
            if (!connected.Connected || connected.Error != 0)
                throw new IOException($"ESPHome could not connect to the treadmill (BLE error {connected.Error}). Check that another app is not connected and that a proxy connection slot is available.");

            var services = new List<BluetoothGATTService>();
            void CollectServices(ESPHomePacket packet)
            {
                if (packet.Type != ApiMessageType.BluetoothGattGetServicesResponse)
                    return;
                var response = BluetoothGATTGetServicesResponse.Parser.ParseFrom(packet.Payload);
                if (response.Address == device.Address)
                    services.AddRange(response.Services);
            }
            session.PacketReceived += CollectServices;
            try
            {
                await session.RequestAsync(ApiMessageType.BluetoothGattGetServicesRequest,
                    new BluetoothGATTGetServicesRequest { Address = device.Address },
                    ApiMessageType.BluetoothGattGetServicesDoneResponse, BluetoothGATTGetServicesDoneResponse.Parser,
                    response => response.Address == device.Address, packet => GetGattFailure(packet, device.Address),
                    cancellationToken, TimeSpan.FromSeconds(20));
            }
            finally { session.PacketReceived -= CollectServices; }

            var service = services.SingleOrDefault(candidate => DecodeUuid(candidate.Uuid, candidate.ShortUuid) == serviceUuid);
            if (service == null)
            {
                _logger.LogWarning("Service {ServiceUuid} not found on {Name}", serviceUuid, device.DisplayName);
                await DisconnectDeviceAsync();
                return false;
            }
            foreach (var characteristic in service.Characteristics)
                _characteristics[DecodeUuid(characteristic.Uuid, characteristic.ShortUuid)] = characteristic;
            if (!session.IsConnected || _address != device.Address)
                throw new IOException("The treadmill disconnected during service discovery.");
            _ready = true;
            return true;
        }
        catch
        {
            // Release the BLE link when the API is still reachable, then discard
            // the API session. A later connect also resets any surviving link.
            await CloseSessionAsync();
            throw;
        }
        finally { _operations.Release(); }
    }

    public override async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunGattAsync(async (session, address) =>
            {
                if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                    return false;
                if (callback == null)
                    return true;
                if ((characteristic.Properties & (16 | 32)) == 0)
                    return false;

                _subscriptions[characteristic.Handle] = callback;
                try
                {
                    await SetNotifyAsync(session, address, characteristic, true, cancellationToken);
                    return true;
                }
                catch
                {
                    _subscriptions.TryRemove(characteristic.Handle, out _);
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to subscribe to {Uuid}", characteristicUuid);
            return false;
        }
    }

    public override async Task UnsubscribeAsync(Guid characteristicUuid) =>
        await RunGattAsync(async (session, address) =>
        {
            if (_characteristics.TryGetValue(characteristicUuid, out var characteristic) &&
                _subscriptions.ContainsKey(characteristic.Handle))
            {
                await SetNotifyAsync(session, address, characteristic, false, CancellationToken.None);
                _subscriptions.TryRemove(characteristic.Handle, out _);
            }
            return true;
        }, CancellationToken.None);

    private static async Task SetNotifyAsync(ESPHomeApiConnection session, ulong address,
        BluetoothGATTCharacteristic characteristic, bool enable, CancellationToken cancellationToken)
    {
        var descriptor = characteristic.Descriptors.SingleOrDefault(candidate =>
            DecodeUuid(candidate.Uuid, candidate.ShortUuid) == Guid.Parse("00002902-0000-1000-8000-00805f9b34fb"))
            ?? throw new InvalidOperationException("The treadmill characteristic has no notification configuration descriptor.");
        Task<BluetoothGATTNotifyResponse> RegisterAsync() => session.RequestAsync(ApiMessageType.BluetoothGattNotifyRequest,
            new BluetoothGATTNotifyRequest { Address = address, Handle = characteristic.Handle, Enable = enable },
            ApiMessageType.BluetoothGattNotifyResponse, BluetoothGATTNotifyResponse.Parser,
            response => response.Address == address && response.Handle == characteristic.Handle,
            packet => GetGattFailure(packet, address, characteristic.Handle), cancellationToken);

        if (enable)
            await RegisterAsync();
        // ESPHome registers the local notification handler only. The API client
        // must enable Notify (0x0001) or Indicate (0x0002) on the peripheral CCCD.
        var value = enable ? ((characteristic.Properties & 16) != 0 ? (byte)1 : (byte)2) : (byte)0;
        await session.RequestAsync(ApiMessageType.BluetoothGattWriteDescriptorRequest,
            new BluetoothGATTWriteDescriptorRequest
            {
                Address = address, Handle = descriptor.Handle, Data = ByteString.CopyFrom(new byte[] { value, 0 })
            }, ApiMessageType.BluetoothGattWriteResponse, BluetoothGATTWriteResponse.Parser,
            response => response.Address == address && response.Handle == descriptor.Handle,
            packet => GetGattFailure(packet, address, descriptor.Handle), cancellationToken);
        if (!enable)
            await RegisterAsync();
    }

    /// <summary>
    /// Sends one write and waits for the relay's result. A write that fails or times out is never sent again; a
    /// timeout also closes the relay session. ESPHome reports a result for writes without response too, once sent.
    /// </summary>
    public override async Task<bool> WriteAsync(Guid characteristicUuid, byte[] command, bool withResponse = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunGattAsync(async (session, address) =>
            {
                var characteristic = GetCharacteristic(characteristicUuid);
                if ((characteristic.Properties & (4 | 8)) == 0)
                    throw new InvalidOperationException("The Bluetooth characteristic is not writable.");
                // A characteristic without write-without-response only takes acknowledged writes. A GATT write
                // result does not mean the treadmill accepted the application-level command.
                await session.RequestAsync(ApiMessageType.BluetoothGattWriteRequest,
                    new BluetoothGATTWriteRequest
                    {
                        Address = address, Handle = characteristic.Handle,
                        Response = withResponse || (characteristic.Properties & 4) == 0,
                        Data = ByteString.CopyFrom(command)
                    }, ApiMessageType.BluetoothGattWriteResponse, BluetoothGATTWriteResponse.Parser,
                    response => response.Address == address && response.Handle == characteristic.Handle,
                    packet => GetGattFailure(packet, address, characteristic.Handle), cancellationToken);
                return true;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to write {Command} to {Uuid}", Convert.ToHexString(command), characteristicUuid);
            return false;
        }
    }

    public override async Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunGattAsync<byte[]?>(async (session, address) =>
            {
                // Drivers may read optional range characteristics.
                if (!_characteristics.TryGetValue(characteristicUuid, out var characteristic))
                    return null;
                var response = await session.RequestAsync(ApiMessageType.BluetoothGattReadRequest,
                    new BluetoothGATTReadRequest { Address = address, Handle = characteristic.Handle },
                    ApiMessageType.BluetoothGattReadResponse, BluetoothGATTReadResponse.Parser,
                    response => response.Address == address && response.Handle == characteristic.Handle,
                    packet => GetGattFailure(packet, address, characteristic.Handle), cancellationToken);
                return response.Data.ToByteArray();
            }, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read {Uuid}", characteristicUuid);
            return null;
        }
    }

    private async Task<T> RunGattAsync<T>(Func<ESPHomeApiConnection, ulong, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _generation);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected || generation != Volatile.Read(ref _generation))
                throw new IOException("The treadmill is disconnected. Reconnect before sending commands.");
            return await operation(_session!, _address);
        }
        finally { _operations.Release(); }
    }

    private BluetoothGATTCharacteristic GetCharacteristic(Guid uuid) =>
        _characteristics.TryGetValue(uuid, out var characteristic) ? characteristic :
            throw new InvalidOperationException($"Bluetooth characteristic {uuid} was not found.");

    private static Exception? GetGattFailure(ESPHomePacket packet, ulong address, uint? handle = null)
    {
        if (packet.Type == ApiMessageType.BluetoothGattErrorResponse)
        {
            var error = BluetoothGATTErrorResponse.Parser.ParseFrom(packet.Payload);
            if (error.Address == address && (!handle.HasValue || error.Handle == handle.Value))
                return new IOException($"ESPHome BLE operation failed (handle {error.Handle}, error {error.Error}).");
        }
        if (packet.Type == ApiMessageType.BluetoothDeviceConnectionResponse)
        {
            var state = BluetoothDeviceConnectionResponse.Parser.ParseFrom(packet.Payload);
            if (state.Address == address && !state.Connected)
                return new IOException($"The treadmill's Bluetooth connection was lost (error {state.Error}).");
        }
        return null;
    }

    private void HandlePacket(ESPHomePacket packet)
    {
        switch (packet.Type)
        {
            case ApiMessageType.BluetoothLeRawAdvertisementsResponse when _scanning:
                foreach (var advertisement in BluetoothLERawAdvertisementsResponse.Parser.ParseFrom(packet.Payload).Advertisements)
                {
                    var name = ReadAdvertisedName(advertisement.Data.Span);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    _devices[advertisement.Address] = new DiscoveredDevice(advertisement.Address, advertisement.AddressType, name);
                    if (advertisement.Address == _soughtAddress)
                        _sought?.TrySetResult();
                }
                break;
            case ApiMessageType.BluetoothGattNotifyDataResponse:
                var data = BluetoothGATTNotifyDataResponse.Parser.ParseFrom(packet.Payload);
                if (data.Address == _address && _subscriptions.TryGetValue(data.Handle, out var callback))
                {
                    try { callback(data.Data.ToByteArray()); }
                    catch (Exception ex) { _logger.LogError(ex, "Error handling treadmill telemetry from ESPHome"); }
                }
                break;
            case ApiMessageType.BluetoothDeviceConnectionResponse:
                var state = BluetoothDeviceConnectionResponse.Parser.ParseFrom(packet.Payload);
                if (state.Address == _address && !state.Connected)
                    ClearDevice();
                break;
        }
    }

    private static string? ReadAdvertisedName(ReadOnlySpan<byte> data)
    {
        string? shortName = null;
        for (var offset = 0; offset < data.Length;)
        {
            var length = data[offset++];
            if (length == 0 || offset + length > data.Length)
                break;
            var type = data[offset];
            if (type == 9)
                return Encoding.UTF8.GetString(data.Slice(offset + 1, length - 1)).TrimEnd('\0');
            if (type == 8)
                shortName = Encoding.UTF8.GetString(data.Slice(offset + 1, length - 1)).TrimEnd('\0');
            offset += length;
        }
        return shortName;
    }

    private static Guid DecodeUuid(IList<ulong> words, uint shortUuid)
    {
        if (shortUuid != 0)
            return Guid.Parse($"{shortUuid:x8}-0000-1000-8000-00805f9b34fb");
        var hex = $"{words.ElementAtOrDefault(0):x16}{words.ElementAtOrDefault(1):x16}";
        return Guid.ParseExact(hex, "N");
    }

    private static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Reverse().Select(index => ((address >> (index * 8)) & 255).ToString("X2", CultureInfo.InvariantCulture)));

    private static ulong ParseSelectedAddress(string deviceName)
    {
        var start = deviceName.LastIndexOf(" [", StringComparison.Ordinal);
        if (start >= 0 && deviceName.EndsWith(']'))
        {
            var address = deviceName[(start + 2)..^1].Replace(":", "");
            if (address.Length == 12 && ulong.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        throw new ArgumentException("Scan with the ESPHome proxy and select a device that includes its Bluetooth address.");
    }

    private void HandleSessionClosed(Exception error)
    {
        _logger.LogWarning("ESPHome proxy connection closed: {Reason}", error.Message);
        ClearDevice();
    }

    private void ClearDevice()
    {
        var wasReady = _ready;
        _ready = false;
        Interlocked.Increment(ref _generation);
        _address = 0;
        _characteristics.Clear();
        _subscriptions.Clear();
        if (wasReady)
            RaiseConnectionLost();
    }

    private async Task DisconnectDeviceAsync()
    {
        var address = _address;
        ClearDevice();
        if (address != 0 && _session?.IsConnected == true)
        {
            await _session.RequestAsync(ApiMessageType.BluetoothDeviceRequest,
                new BluetoothDeviceRequest { Address = address, RequestType = BluetoothDeviceRequestType.Disconnect },
                ApiMessageType.BluetoothDeviceConnectionResponse, BluetoothDeviceConnectionResponse.Parser,
                response => response.Address == address && !response.Connected,
                timeout: TimeSpan.FromSeconds(5));
        }
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        if (session == null)
            return;
        session.PacketReceived -= HandlePacket;
        session.ConnectionClosed -= HandleSessionClosed;
        try { await DisconnectDeviceAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not release the proxy's Bluetooth link before closing"); }
        finally
        {
            _session = null;
            ClearDevice();
            await session.DisposeAsync();
        }
    }

    public override async Task DisconnectAsync()
    {
        await _operations.WaitAsync();
        try
        {
            try { await DisconnectDeviceAsync(); }
            finally { await CloseSessionAsync(); }
        }
        finally { _operations.Release(); }
    }

    public override Task<string> GetDeviceInfoAsync() => Task.FromResult(
        $"ESPHome proxy: {_settings.Host}:{_settings.Port}\nFirmware: {_session?.DeviceInfo?.EsphomeVersion ?? "not connected"}\n" +
        $"Treadmill: {(IsConnected ? _deviceName : "not connected")}\nCharacteristics: {_characteristics.Count}");

    // _operations is not disposed: a driver's late command after disposal then reports a disconnect instead of throwing
    // ObjectDisposedException. Disconnecting already closed the relay session.
}
