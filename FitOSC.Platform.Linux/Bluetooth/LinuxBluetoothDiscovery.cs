using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using global::Linux.Bluetooth;
using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

/// <summary>
/// BlueZ exposes cached device properties, not individual advertisement packets.
/// All signal callbacks only enqueue data; watch registration and release stay owned by this operation.
/// </summary>
internal static class LinuxBluetoothDiscovery
{
    internal sealed record Device(ObjectPath Path, string? Name, IReadOnlyList<Guid> Uuids);
    private sealed record Update(ObjectPath Path, PropertyChanges? Changes = null,
        long Generation = 0, bool Removed = false, bool Finished = false);

    internal static async Task<IReadOnlyList<Device>> ObserveAsync(
        LinuxBluetoothDiscoveryCoordinator coordinator, IAdapter1 adapter, TimeSpan duration,
        Func<Device, bool>? match, CancellationToken token)
    {
        var lease = await coordinator.AcquireAsync(adapter, token).ConfigureAwait(false);
        var events = Channel.CreateUnbounded<Update>(new UnboundedChannelOptions { SingleReader = true });
        var watches = new List<IDisposable>();
        var deviceWatches = new Dictionary<ObjectPath, (IDisposable Watch, long Generation)>();
        long generation = 0;
        var devices = new Dictionary<ObjectPath, Dictionary<string, object>>();
        // Preserve the union of services observed under each name, even if properties later change.
        var observed = new Dictionary<(ObjectPath Path, string? Name), HashSet<Guid>>();
        Exception? failure = null;
        try
        {
            var manager = LinuxBluetoothDiscoveryCoordinator.ObjectManager;
            watches.Add(await manager.WatchInterfacesAddedAsync(update =>
            {
                if (update.interfaces.ContainsKey("org.bluez.Device1"))
                    events.Writer.TryWrite(new Update(update.@object));
            }, ex => events.Writer.TryComplete(ex)).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            watches.Add(await manager.WatchInterfacesRemovedAsync(update =>
            {
                if (update.interfaces.Contains("org.bluez.Device1"))
                    events.Writer.TryWrite(new Update(update.@object, Removed: true));
                if (update.@object == adapter.ObjectPath && update.interfaces.Contains("org.bluez.Adapter1"))
                    events.Writer.TryComplete(new IOException("Bluetooth adapter was removed during discovery."));
            }, ex => events.Writer.TryComplete(ex)).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            watches.Add(await adapter.WatchPropertiesAsync(changes =>
            {
                if (changes.Changed.Any(pair => pair.Key == "Powered" && pair.Value is false)
                    || changes.Invalidated.Contains("Powered"))
                    events.Writer.TryComplete(new IOException("Bluetooth adapter lost power during discovery."));
            }).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            var snapshot = await manager.GetManagedObjectsAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var pair in snapshot)
            {
                if (pair.Value.ContainsKey("org.bluez.Device1"))
                    events.Writer.TryWrite(new Update(pair.Key));
            }
            using var timer = new Timer(_ => events.Writer.TryWrite(new Update(default, Finished: true)),
                null, duration, Timeout.InfiniteTimeSpan);
            while (true)
            {
                Update update;
                try { update = await events.Reader.ReadAsync(token).ConfigureAwait(false); }
                catch (ChannelClosedException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }
                if (update.Finished) return match is null
                    ? observed.Select(pair => new Device(pair.Key.Path, pair.Key.Name, pair.Value.ToArray())).ToArray()
                    : [];
                if (!update.Path.ToString().StartsWith(adapter.ObjectPath + "/", StringComparison.Ordinal)) continue;
                if (update.Removed)
                {
                    if (deviceWatches.TryGetValue(update.Path, out var registration))
                    {
                        registration.Watch.Dispose();
                        deviceWatches.Remove(update.Path);
                    }
                    devices.Remove(update.Path);
                    continue;
                }
                if (update.Changes.HasValue && (!deviceWatches.TryGetValue(update.Path, out var currentWatch)
                    || currentWatch.Generation != update.Generation)) continue;
                if (!deviceWatches.ContainsKey(update.Path))
                {
                    token.ThrowIfCancellationRequested();
                    var proxy = LinuxBluetoothDiscoveryCoordinator.Proxy<IDevice1>(update.Path);
                    var watchGeneration = ++generation;
                    var watch = await proxy.WatchPropertiesAsync(changes =>
                        events.Writer.TryWrite(new Update(update.Path, Changes: changes, Generation: watchGeneration))).ConfigureAwait(false);
                    deviceWatches.Add(update.Path, (watch, watchGeneration));
                    token.ThrowIfCancellationRequested();
                    Device1Properties properties;
                    try { properties = await proxy.GetAllAsync().ConfigureAwait(false); }
                    catch (DBusException ex) when (LinuxBluetoothErrors.IsObjectRemoved(ex)
                        || LinuxBluetoothErrors.IsDeviceInterfaceRemoved(ex))
                    {
                        watch.Dispose();
                        deviceWatches.Remove(update.Path);
                        devices.Remove(update.Path);
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    devices[update.Path] = new Dictionary<string, object>();
                    if (properties.Name is not null) devices[update.Path]["Name"] = properties.Name;
                    if (properties.UUIDs is not null) devices[update.Path]["UUIDs"] = properties.UUIDs;
                    if (properties.RSSI != 0) devices[update.Path]["RSSI"] = properties.RSSI;
                    devices[update.Path]["Connected"] = properties.Connected;
                }
                var current = devices[update.Path];
                // Added/snapshot events install watches; GetAll supplies the initial state.
                if (update.Changes is { } changes)
                {
                    foreach (var pair in changes.Changed) current[pair.Key] = pair.Value;
                    foreach (var property in changes.Invalidated) current.Remove(property);
                }
                var present = (current.TryGetValue("RSSI", out var r) && r is short rssi && rssi != 0)
                    || (current.TryGetValue("Connected", out var c) && c is true);
                if (!present) continue;
                var name = current.TryGetValue("Name", out var rawName) ? rawName as string : null;
                var uuids = current.TryGetValue("UUIDs", out var rawUuids) && rawUuids is string[] values
                    ? values.Select(value => Guid.TryParse(value, out var uuid) ? (Guid?)uuid : null)
                        .Where(uuid => uuid.HasValue).Select(uuid => uuid!.Value).ToArray()
                    : Array.Empty<Guid>();
                var device = new Device(update.Path, name, uuids);
                if (!observed.TryGetValue((device.Path, device.Name), out var services))
                    observed[(device.Path, device.Name)] = services = [];
                services.UnionWith(device.Uuids);
                if (match?.Invoke(device) == true) return [device];
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            events.Writer.TryComplete();
            var failures = new List<Exception>();
            foreach (var watch in deviceWatches.Values.Select(registration => registration.Watch).Concat(watches))
            {
                try { watch.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0)
            {
                if (failure is not null) failures.Insert(0, failure);
                throw new AggregateException("Linux BLE discovery cleanup failed.", failures);
            }
        }
    }
}
