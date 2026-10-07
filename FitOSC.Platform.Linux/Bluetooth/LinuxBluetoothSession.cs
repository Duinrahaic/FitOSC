using System.Collections.Concurrent;
using System.Threading.Channels;
using global::Linux.Bluetooth;
using Microsoft.Extensions.Logging;
using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

internal sealed class LinuxBluetoothSession(
    IDevice1 device, IAdapter1 adapter, LinuxBluetoothDeviceLease deviceLease, ILogger logger)
{
    private readonly object _state = new();
    private readonly List<IDisposable> _watches = [];
    private readonly ConcurrentDictionary<ObjectPath, byte> _removedCharacteristics = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool? _connected;
    private bool? _resolved;
    private bool _everConnected;
    private bool _everResolved;
    private bool _lost;
    private (string Reason, Exception? Failure)? _loss;
    private long _generation;
    private volatile bool _isConnected;
    private volatile bool _deviceRemoved;

    internal IDevice1 Device { get; } = device;
    internal IGattService1? Service { get; set; }
    internal Dictionary<Guid, Characteristic> Characteristics { get; } = [];
    internal bool DisconnectNeeded { get; set; }
    internal bool IsConnected => _isConnected;
    internal Task<bool> Ready => _ready.Task;
    internal long Generation => Interlocked.Read(ref _generation);
    internal bool IsLost { get { lock (_state) return _lost; } }
    internal bool IsCharacteristicRemoved(ObjectPath path) => _deviceRemoved || _removedCharacteristics.ContainsKey(path);

    internal async Task InitializeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = await Connection.System.ResolveServiceOwnerAsync("org.bluez").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _watches.Add(await Connection.System.ResolveServiceOwnerAsync("org.bluez",
            change => { if (change.NewOwner != owner) LoseOwner(); },
            ex => Invalidate("BlueZ owner watch failed.", ex)).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        if (owner is null || await Connection.System.ResolveServiceOwnerAsync("org.bluez").ConfigureAwait(false) != owner)
            LoseOwner();
        token.ThrowIfCancellationRequested();
        _watches.Add(await Device.WatchPropertiesAsync(changes =>
        {
            lock (_state)
            {
                foreach (var pair in changes.Changed)
                {
                    if (pair.Key == "Connected" && pair.Value is bool connected) _connected = connected;
                    if (pair.Key == "ServicesResolved" && pair.Value is bool resolved) _resolved = resolved;
                }
                if (changes.Invalidated.Contains("Connected") || changes.Invalidated.Contains("ServicesResolved"))
                    Lose("Bluetooth connection properties were invalidated.");
                Evaluate();
            }
        }).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        _watches.Add(await LinuxBluetoothDiscoveryCoordinator.ObjectManager.WatchInterfacesRemovedAsync(update =>
        {
            if (update.interfaces.Contains("org.bluez.GattCharacteristic1")
                && update.@object.ToString().StartsWith(Device.ObjectPath + "/", StringComparison.Ordinal))
                _removedCharacteristics.TryAdd(update.@object, 0);
            if (update.@object == Device.ObjectPath && update.interfaces.Contains("org.bluez.Device1"))
            {
                _deviceRemoved = true;
                Invalidate("Bluetooth device was removed.");
            }
            if (update.@object == adapter.ObjectPath && update.interfaces.Contains("org.bluez.Adapter1"))
            {
                _deviceRemoved = true;
                Invalidate("Bluetooth device or adapter was removed.");
            }
        }, ex => Invalidate("Bluetooth object watch failed.", ex)).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        _watches.Add(await adapter.WatchPropertiesAsync(changes =>
        {
            if (changes.Changed.Any(pair => pair.Key == "Powered" && pair.Value is false)
                || changes.Invalidated.Contains("Powered"))
                Invalidate("Bluetooth adapter lost power.");
        }).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        var powered = await adapter.GetPoweredAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!powered) Invalidate("Bluetooth adapter is not powered.");
        var properties = await Device.GetAllAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_state)
        {
            // Signals registered before the read take precedence over the initial snapshot.
            _connected ??= properties.Connected;
            _resolved ??= properties.ServicesResolved;
            Evaluate();
        }
    }

    private void Evaluate()
    {
        if (_lost) return;
        if ((_everConnected && _connected == false) || (_everResolved && _resolved == false))
            Lose("Bluetooth device disconnected or its services became unresolved.");
        else if (_connected == true && _resolved == true) _ready.TrySetResult(true);
        _everConnected |= _connected == true;
        _everResolved |= _resolved == true;
    }

    private void Lose(string reason, Exception? failure = null)
    {
        if (_lost) return;
        _lost = true;
        _isConnected = false;
        Interlocked.Increment(ref _generation);
        _ready.TrySetResult(false);
        _loss = (reason, failure);
    }

    private void LoseOwner()
    {
        _deviceRemoved = true;
        Invalidate("BlueZ service owner changed.");
    }

    private void LogLoss()
    {
        (string Reason, Exception? Failure)? loss;
        lock (_state) { loss = _loss; _loss = null; }
        if (loss is { } value)
            logger.LogWarning(value.Failure, "{Reason} Device: {Device}", value.Reason, Device.ObjectPath);
    }

    private void Invalidate(string reason, Exception? failure = null)
    {
        lock (_state) Lose(reason, failure);
    }

    internal bool Publish()
    {
        lock (_state)
        {
            if (_lost || _connected != true || _resolved != true) return false;
            _isConnected = true;
            return true;
        }
    }

    internal void InvalidateCallbacks()
    {
        lock (_state)
        {
            _lost = true;
            _isConnected = false;
            Interlocked.Increment(ref _generation);
            _ready.TrySetCanceled();
        }
    }

    internal async Task CleanupAsync()
    {
        InvalidateCallbacks();
        foreach (var characteristic in Characteristics.Values) characteristic.Subscription?.Suppress();
        var failures = new List<Exception>();
        try { LogLoss(); }
        catch (Exception ex) { failures.Add(ex); }
        foreach (var characteristic in Characteristics.Values)
        {
            if (characteristic.Subscription is not { } subscription) continue;
            try { await subscription.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (DisconnectNeeded)
        {
            try { await Device.DisconnectAsync().ConfigureAwait(false); DisconnectNeeded = false; }
            catch (DBusException ex) when (LinuxBluetoothErrors.IsObjectRemoved(ex))
            {
                DisconnectNeeded = false;
                try { logger.LogDebug(ex, "Device {Device} was removed before disconnect.", Device.ObjectPath); }
                catch (Exception logging) { failures.Add(logging); }
            }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.NotConnected")
            {
                DisconnectNeeded = false;
                try { logger.LogDebug("Device {Device} is already disconnected.", Device.ObjectPath); }
                catch (Exception logging) { failures.Add(logging); }
            }
            catch (Exception ex)
            {
                if (_deviceRemoved) DisconnectNeeded = false;
                failures.Add(ex);
            }
        }
        foreach (var watch in _watches.ToArray())
        {
            try { watch.Dispose(); _watches.Remove(watch); }
            catch (Exception ex) { failures.Add(ex); }
        }
        // Keep failed local watches as well as remote ownership reachable for the next owner.
        // Remote flags can be clear while only local cleanup remains; no new session starts yet.
        if (failures.Count != 0) throw new AggregateException("Linux BLE session cleanup failed.", failures);
        Characteristics.Clear();
        Service = null;
        deviceLease.Dispose();
    }

    internal void Abandon() => deviceLease.Abandon(CleanupAsync);

    internal sealed class Characteristic(IGattCharacteristic1 proxy, string[] flags)
    {
        internal IGattCharacteristic1 Proxy { get; } = proxy;
        internal string[] Flags { get; } = flags;
        internal Subscription? Subscription { get; set; }
    }

    /// <summary>Owns both the property watch and the successful StartNotify session.</summary>
    internal sealed class Subscription
    {
        private readonly Channel<byte[]> _values = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly LinuxBluetoothSession _session;
        private readonly IGattCharacteristic1 _characteristic;
        private readonly long _generation;
        private readonly Task _dispatch;
        private readonly ILogger _logger;
        private bool _enabled;

        internal Subscription(LinuxBluetoothSession session, IGattCharacteristic1 characteristic, Action<byte[]> callback, ILogger logger)
        {
            _session = session;
            _characteristic = characteristic;
            _logger = logger;
            _generation = session.Generation;
            _dispatch = Task.Run(async () =>
            {
                await foreach (var value in _values.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (!session.IsConnected || session.Generation != _generation || !Volatile.Read(ref _enabled)) continue;
                    // Application code executes on this owned dispatcher, outside signal/state locks.
                    try { callback(value); }
                    catch (Exception ex) { logger.LogError(ex, "BLE notification callback failed for {Characteristic}.", characteristic.ObjectPath); }
                }
            });
        }
        internal IDisposable? Watch { get; set; }
        internal bool NotifyOwned { get; set; }

        internal void OnProperties(PropertyChanges changes)
        {
            if (!Volatile.Read(ref _enabled) || _session.Generation != _generation) return;
            foreach (var pair in changes.Changed)
                if (pair.Key == "Value" && pair.Value is byte[] value) _values.Writer.TryWrite(value.ToArray());
        }

        internal void Enable() => Volatile.Write(ref _enabled, true);
        internal void Suppress() => Volatile.Write(ref _enabled, false);

        internal async Task ReleaseAsync()
        {
            Suppress();
            var failures = new List<Exception>();
            if (NotifyOwned)
            {
                try { await _characteristic.StopNotifyAsync().ConfigureAwait(false); NotifyOwned = false; }
                catch (DBusException ex) when (LinuxBluetoothErrors.IsObjectRemoved(ex)
                    || LinuxBluetoothErrors.IsNoNotifySession(ex))
                {
                    NotifyOwned = false;
                    try { _logger.LogDebug(ex, "Notification session for {Characteristic} has ended.", _characteristic.ObjectPath); }
                    catch (Exception logging) { failures.Add(logging); }
                }
                catch (Exception ex)
                {
                    if (_session.IsCharacteristicRemoved(_characteristic.ObjectPath)) NotifyOwned = false;
                    failures.Add(ex);
                }
            }
            if (Watch is { } watch)
            {
                try { watch.Dispose(); Watch = null; }
                catch (Exception ex) { failures.Add(ex); }
            }
            _values.Writer.TryComplete();
            await _dispatch.ConfigureAwait(false);
            if (failures.Count != 0) throw new AggregateException("Linux BLE notification release failed.", failures);
        }
    }
}
