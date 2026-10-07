using System.Text;
using FitOSC.Utilities.BLE;
using global::Linux.Bluetooth;
using global::Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Logging;
using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

/// <summary>A single-device BlueZ client. Issued D-Bus operations are always drained before returning.</summary>
public sealed class LinuxBluetoothClient(
    ILogger<LinuxBluetoothClient> logger, LinuxBluetoothDiscoveryCoordinator discovery) : IBluetoothClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private CancellationTokenSource _work = new();
    private volatile bool _closing;
    private bool _disconnecting;
    private Task? _disconnectTask;
    private Task? _disposeTask;
    private LinuxBluetoothSession? _session;

    public bool IsConnected => !_closing && Volatile.Read(ref _session)?.IsConnected == true;

    public async Task<IReadOnlyList<BluetoothAdvertisement>> ScanAsync(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(duration));
        using var operation = await EnterAsync(default).ConfigureAwait(false);
        await CleanLostSessionAsync().ConfigureAwait(false);
        var adapter = await discovery.GetAdapterAsync(operation.Token).ConfigureAwait(false);
        var devices = await LinuxBluetoothDiscovery.ObserveAsync(discovery, adapter, duration, null, operation.Token)
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        return devices.Where(device => !string.IsNullOrEmpty(device.Name))
            .GroupBy(device => device.Name!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new BluetoothAdvertisement(group.Key, group.SelectMany(device => device.Uuids).Distinct().ToArray()))
            .ToArray();
    }

    public async Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterAsync(cancellationToken).ConfigureAwait(false);
        await CleanupSessionAsync().ConfigureAwait(false);
        var token = operation.Token;
        try
        {
            var adapter = await discovery.GetAdapterAsync(token).ConfigureAwait(false);
            var devices = await LinuxBluetoothDiscovery.ObserveAsync(discovery, adapter, TimeSpan.FromSeconds(10),
                device => (!string.IsNullOrEmpty(deviceName) && device.Name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true)
                    || device.Uuids.Contains(serviceUuid), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (devices.Count == 0) return false;
            var device = LinuxBluetoothDiscoveryCoordinator.Proxy<IDevice1>(devices[0].Path);
            var deviceLease = await LinuxBluetoothDeviceLease.AcquireAsync(device.ObjectPath).ConfigureAwait(false);
            var session = new LinuxBluetoothSession(device, adapter, deviceLease, logger);
            Volatile.Write(ref _session, session);
            await session.InitializeAsync(token).ConfigureAwait(false);
            await ConnectDeviceAsync(session, token).ConfigureAwait(false);
            if (!await session.Ready.WaitAsync(token).ConfigureAwait(false))
                throw new IOException("Bluetooth device was lost before service resolution completed.");
            token.ThrowIfCancellationRequested();
            session.Service = await ResolveServiceAsync(session, serviceUuid, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (session.Service is null || !session.Publish())
            {
                await CleanupSessionAsync().ConfigureAwait(false);
                return false;
            }
            logger.LogInformation("Connected to Linux BLE device {Device}, service {Service}.", session.Device.ObjectPath, serviceUuid);
            return true;
        }
        catch (Exception failure)
        {
            try { await CleanupSessionAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            if (failure is OperationCanceledException) throw;
            if ((failure is DBusException dbus && LinuxBluetoothErrors.IsOperationFailure(dbus)) || failure is IOException)
            {
                logger.LogWarning(failure, "Linux BLE connection failed for service {Service}.", serviceUuid);
                token.ThrowIfCancellationRequested();
                return false;
            }
            throw;
        }
    }

    private async Task ConnectDeviceAsync(LinuxBluetoothSession session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (session.IsLost) throw new IOException("Bluetooth device was lost before connecting.");
        session.DisconnectNeeded = true;
        var connect = session.Device.ConnectAsync();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => canceled.TrySetResult());
        // BlueZ Disconnect explicitly cancels a pending Connect. Both issued tasks remain owned and awaited.
        var winner = await Task.WhenAny(connect, canceled.Task).ConfigureAwait(false);
        if (winner == canceled.Task && !connect.IsCompleted)
        {
            Exception? disconnectFailure = null;
            try { await session.Device.DisconnectAsync().ConfigureAwait(false); }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.NotConnected")
            {
                logger.LogDebug("Pending connection to {Device} was already disconnected.", session.Device.ObjectPath);
            }
            catch (Exception ex) { disconnectFailure = ex; }
            var connected = false;
            Exception? connectFailure = null;
            try { await connect.ConfigureAwait(false); connected = true; }
            catch (Exception ex) { connectFailure = ex; }
            session.DisconnectNeeded = connected || disconnectFailure is not null;
            if (disconnectFailure is not null)
            {
                var failures = new List<Exception> { new OperationCanceledException(token), disconnectFailure };
                if (connectFailure is not null) failures.Add(connectFailure);
                throw new AggregateException("Pending BlueZ connection cancellation failed.", failures);
            }
            if (connectFailure is not null)
            {
                if (connectFailure is not DBusException)
                    throw new AggregateException(new OperationCanceledException(token), connectFailure);
                logger.LogDebug(connectFailure, "Canceled BlueZ Connect finished for {Device}.", session.Device.ObjectPath);
            }
            token.ThrowIfCancellationRequested();
        }
        await connect.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public async Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetConnectedSessionAsync().ConfigureAwait(false);
        if (session is null) return false;
        var token = operation.Token;
        LinuxBluetoothSession.Characteristic? characteristic = null;
        try
        {
            characteristic = await ResolveAsync(session, characteristicUuid, token).ConfigureAwait(false);
            if (characteristic is null) return false;
            token.ThrowIfCancellationRequested();
            if (callback is null) return session.IsConnected;
            if (characteristic.Subscription is { } previous)
            {
                await previous.ReleaseAsync().ConfigureAwait(false);
                characteristic.Subscription = null;
            }
            token.ThrowIfCancellationRequested();
            if (!session.IsConnected) throw new IOException("Device disconnected before notification setup.");
            if (!characteristic.Flags.Contains("notify") && !characteristic.Flags.Contains("indicate")) return false;
            var subscription = new LinuxBluetoothSession.Subscription(session, characteristic.Proxy, callback, logger);
            characteristic.Subscription = subscription;
            subscription.Watch = await characteristic.Proxy.WatchPropertiesAsync(subscription.OnProperties).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await characteristic.Proxy.StartNotifyAsync().ConfigureAwait(false);
            subscription.NotifyOwned = true;
            token.ThrowIfCancellationRequested();
            if (!session.IsConnected) throw new IOException("Device disconnected while enabling notifications.");
            subscription.Enable();
            return true;
        }
        catch (Exception failure)
        {
            if (characteristic?.Subscription is { } subscription)
            {
                try { await subscription.ReleaseAsync().ConfigureAwait(false); characteristic.Subscription = null; }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            }
            if (failure is OperationCanceledException) throw;
            if ((failure is DBusException dbus && LinuxBluetoothErrors.IsOperationFailure(dbus)) || failure is IOException)
            {
                logger.LogWarning(failure, "Linux BLE subscription failed for {Characteristic}.", characteristicUuid);
                token.ThrowIfCancellationRequested();
                return false;
            }
            throw;
        }
    }

    public async Task UnsubscribeAsync(Guid characteristicUuid)
    {
        using var operation = await EnterAsync(default).ConfigureAwait(false);
        await CleanLostSessionAsync().ConfigureAwait(false);
        if (_session?.Characteristics.TryGetValue(characteristicUuid, out var characteristic) == true)
        {
            if (characteristic.Subscription is { } subscription)
            {
                await subscription.ReleaseAsync().ConfigureAwait(false);
                characteristic.Subscription = null;
            }
        }
    }

    public async Task WriteAsync(Guid characteristicUuid, byte[] command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var operation = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetConnectedSessionAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("No device connected.");
        var characteristic = await ResolveAsync(session, characteristicUuid, operation.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Characteristic {characteristicUuid} was not found.");
        var type = characteristic.Flags.Contains("write-without-response") ? "command"
            : characteristic.Flags.Contains("write") ? "request"
            : throw new InvalidOperationException($"Characteristic {characteristicUuid} does not support writes.");
        operation.Token.ThrowIfCancellationRequested();
        if (!session.IsConnected) throw new IOException("Device disconnected before the write.");
        await characteristic.Proxy.WriteValueAsync(command, new Dictionary<string, object> { ["type"] = type }).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        if (!session.IsConnected) throw new IOException("Device disconnected while writing the command.");
    }

    public async Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetConnectedSessionAsync().ConfigureAwait(false);
        if (session is null || !session.Characteristics.TryGetValue(characteristicUuid, out var characteristic)) return null;
        operation.Token.ThrowIfCancellationRequested();
        try
        {
            var value = await characteristic.Proxy.ReadValueAsync(new Dictionary<string, object>()).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return session.IsConnected ? value : null;
        }
        catch (DBusException ex) when (IsReadFailure(ex.ErrorName))
        {
            logger.LogWarning(ex, "Linux BLE read failed for {Characteristic}.", characteristicUuid);
            operation.Token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static bool IsReadFailure(string name) => name is "org.bluez.Error.Failed"
        or "org.bluez.Error.InProgress" or "org.bluez.Error.NotPermitted" or "org.bluez.Error.NotAuthorized"
        or "org.bluez.Error.InvalidOffset" or "org.bluez.Error.NotSupported";

    public async Task<string> GetDeviceInfoAsync()
    {
        using var operation = await EnterAsync(default).ConfigureAwait(false);
        var session = await GetConnectedSessionAsync().ConfigureAwait(false);
        if (session is null) return "No device connected";
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        var properties = await session.Device.GetAllAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var info = new StringBuilder()
            .AppendLine($"Name: {properties.Name}").AppendLine($"Alias: {properties.Alias}")
            .AppendLine($"Address: {properties.Address}").AppendLine($"Address type: {properties.AddressType}")
            .AppendLine($"Device path: {session.Device.ObjectPath}")
            .AppendLine($"Connected: {properties.Connected}").AppendLine($"Services resolved: {properties.ServicesResolved}")
            .AppendLine($"UUIDs: {string.Join(", ", properties.UUIDs ?? [])}");
        var services = await session.Device.GetServicesAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        foreach (var service in services)
        {
            var serviceProperties = await service.GetAllAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            info.AppendLine($"Service: {serviceProperties.UUID} ({service.ObjectPath})");
            var characteristics = await service.GetCharacteristicsAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var characteristic in characteristics)
            {
                var characteristicProperties = await characteristic.GetAllAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                info.AppendLine($"  Characteristic: {characteristicProperties.UUID}; flags: {string.Join(", ", characteristicProperties.Flags ?? [])}");
            }
        }
        return info.ToString();
    }

    private static async Task<IGattService1?> ResolveServiceAsync(
        LinuxBluetoothSession session, Guid uuid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var services = await session.Device.GetServicesAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        foreach (var service in services)
        {
            if (session.IsLost) throw new IOException("Device disconnected while resolving its service.");
            var properties = await service.GetAllAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (properties.Device == session.Device.ObjectPath && Guid.TryParse(properties.UUID, out var candidate) && candidate == uuid)
                return service;
        }
        return null;
    }

    private static async Task<LinuxBluetoothSession.Characteristic?> ResolveAsync(
        LinuxBluetoothSession session, Guid uuid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (session.Characteristics.TryGetValue(uuid, out var cached)) return cached;
        if (session.Service is null) return null;
        var proxies = await session.Service.GetCharacteristicsAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        foreach (var proxy in proxies)
        {
            if (!session.IsConnected) throw new IOException("Device disconnected while resolving a characteristic.");
            var properties = await proxy.GetAllAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (properties.Service != session.Service.ObjectPath || !Guid.TryParse(properties.UUID, out var candidate) || candidate != uuid) continue;
            var characteristic = new LinuxBluetoothSession.Characteristic(proxy, properties.Flags ?? []);
            session.Characteristics.Add(uuid, characteristic);
            return characteristic;
        }
        return null;
    }

    private async Task CleanLostSessionAsync()
    {
        if (_session is { IsConnected: false }) await CleanupSessionAsync().ConfigureAwait(false);
    }

    private async Task<LinuxBluetoothSession?> GetConnectedSessionAsync()
    {
        await CleanLostSessionAsync().ConfigureAwait(false);
        return _session;
    }

    private async Task CleanupSessionAsync()
    {
        if (_session is not { } session) return;
        await session.CleanupAsync().ConfigureAwait(false);
        Volatile.Write(ref _session, null);
    }

    private async Task<Operation> EnterAsync(CancellationToken token)
    {
        CancellationTokenSource linked;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_disconnecting) throw new InvalidOperationException("Bluetooth client is disconnecting.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(_work.Token, token);
        }
        var acquired = false;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                linked.Token.ThrowIfCancellationRequested();
            }
            return new Operation(_gate, linked);
        }
        catch
        {
            if (acquired) _gate.Release();
            linked.Dispose();
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is not null) return _disposeTask;
            if (_disconnectTask is { IsCompleted: false }) return _disconnectTask;
            _disconnecting = true;
            Volatile.Read(ref _session)?.InvalidateCallbacks();
            _work.Cancel();
            _disconnectTask = DisconnectCoreAsync();
            return _disconnectTask;
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CleanupSessionAsync().ConfigureAwait(false); }
        finally
        {
            lock (_lifecycle)
            {
                _disconnecting = false;
                if (!_closing) _work = new CancellationTokenSource();
            }
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is { IsCompleted: false } or { IsCompletedSuccessfully: true }) return new ValueTask(_disposeTask);
            _closing = true;
            Volatile.Read(ref _session)?.InvalidateCallbacks();
            _work.Cancel();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CleanupSessionAsync().ConfigureAwait(false); }
        catch
        {
            if (_session is { } session)
            {
                session.Abandon();
                Volatile.Write(ref _session, null);
            }
            throw;
        }
        finally { _gate.Release(); }
        // Waiters may still be unwinding cancellation; synchronization primitives stay valid for them.
    }

    private sealed class Operation(SemaphoreSlim gate, CancellationTokenSource cancellation) : IDisposable
    {
        internal CancellationToken Token => cancellation.Token;
        public void Dispose() { cancellation.Dispose(); gate.Release(); }
    }
}
