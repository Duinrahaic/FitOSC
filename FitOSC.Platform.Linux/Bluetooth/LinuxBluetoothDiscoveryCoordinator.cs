using System.Collections.Concurrent;
using global::Linux.Bluetooth;
using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

/// <summary>
/// Serializes discovery for the process-shared Connection.System caller identity.
/// The gates deliberately outlive clients and DI containers; the connection is not ours to dispose.
/// </summary>
public sealed class LinuxBluetoothDiscoveryCoordinator
{
    private static readonly ConcurrentDictionary<ObjectPath, AdapterDiscovery> Adapters = new();

    internal static IObjectManager ObjectManager =>
        Connection.System.CreateProxy<IObjectManager>("org.bluez", "/");

    internal static T Proxy<T>(ObjectPath path) where T : IDBusObject =>
        Connection.System.CreateProxy<T>("org.bluez", path);

    internal async Task<IAdapter1> GetAdapterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var objects = await ObjectManager.GetManagedObjectsAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var path = objects.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Where(pair => pair.Value.TryGetValue("org.bluez.Adapter1", out var properties)
                && properties.TryGetValue("Powered", out var powered) && powered is true)
            .Select(pair => (ObjectPath?)pair.Key).FirstOrDefault();
        return path.HasValue ? Proxy<IAdapter1>(path.Value)
            : throw new InvalidOperationException("No powered Linux Bluetooth adapter is available.");
    }

    internal async Task<DiscoveryLease> AcquireAsync(IAdapter1 adapter, CancellationToken token)
    {
        var state = Adapters.GetOrAdd(adapter.ObjectPath, _ => new AdapterDiscovery());
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try { await state.ReleaseAsync(adapter).ConfigureAwait(false); }
        catch { state.Gate.Release(); throw; }
        var lease = new DiscoveryLease(adapter, state);
        try
        {
            token.ThrowIfCancellationRequested();
            // Record ownership before mutation so an uncertain reply still triggers awaited rollback.
            state.FilterOwned = true;
            await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object> { ["Transport"] = "le" })
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            state.DiscoveryOwned = true;
            await adapter.StartDiscoveryAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch (Exception failure)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    internal sealed class AdapterDiscovery
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal bool FilterOwned { get; set; }
        internal bool DiscoveryOwned { get; set; }

        internal async Task ReleaseAsync(IAdapter1 adapter)
        {
            var failures = new List<Exception>();
            if (DiscoveryOwned)
            {
                try { await adapter.StopDiscoveryAsync().ConfigureAwait(false); DiscoveryOwned = false; }
                catch (DBusException ex) when (LinuxBluetoothErrors.IsObjectRemoved(ex)
                    || LinuxBluetoothErrors.IsNoDiscoveryStarted(ex)) { DiscoveryOwned = false; }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (FilterOwned)
            {
                try
                {
                    await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>()).ConfigureAwait(false);
                    FilterOwned = false;
                }
                catch (DBusException ex) when (LinuxBluetoothErrors.IsObjectRemoved(ex)) { FilterOwned = false; }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count != 0) throw new AggregateException("Linux BLE discovery release failed.", failures);
        }
    }

    internal sealed class DiscoveryLease(IAdapter1 adapter, AdapterDiscovery state) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { await state.ReleaseAsync(adapter).ConfigureAwait(false); }
            finally { state.Gate.Release(); }
        }
    }
}
