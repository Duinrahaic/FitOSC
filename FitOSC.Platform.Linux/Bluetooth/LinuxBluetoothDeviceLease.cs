using System.Collections.Concurrent;
using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

/// <summary>
/// BlueZ owns one notification session per caller and characteristic. Connection.System clients
/// therefore need exclusive device ownership, including disconnect and notification cleanup.
/// Scanning does not acquire this lease and different devices remain independent.
/// </summary>
internal sealed class LinuxBluetoothDeviceLease : IDisposable
{
    private static readonly ConcurrentDictionary<ObjectPath, LinuxBluetoothDeviceLease> OwnedDevices = new();
    private readonly ObjectPath _path;
    private readonly object _state = new();
    private bool _released;
    private Func<Task>? _cleanup;

    private LinuxBluetoothDeviceLease(ObjectPath path) => _path = path;

    internal static async Task<LinuxBluetoothDeviceLease> AcquireAsync(ObjectPath path)
    {
        var mine = new LinuxBluetoothDeviceLease(path);
        while (!OwnedDevices.TryAdd(path, mine))
        {
            if (!OwnedDevices.TryGetValue(path, out var orphan)) continue;
            Func<Task>? cleanup;
            lock (orphan._state) cleanup = orphan._cleanup;
            if (cleanup is null)
                throw new InvalidOperationException($"Bluetooth device {path} is already owned by another FitOSC client.");
            if (!OwnedDevices.TryUpdate(path, mine, orphan)) continue;
            try { await cleanup().ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (orphan._state)
                {
                    if (!orphan._released) OwnedDevices.TryUpdate(path, orphan, mine);
                    else mine.Dispose();
                }
                throw new InvalidOperationException($"Bluetooth device {path} still has cleanup from a disposed FitOSC client.", ex);
            }
            break;
        }
        return mine;
    }

    internal void Abandon(Func<Task> cleanup)
    {
        lock (_state)
            if (!_released) _cleanup = cleanup;
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_released) return;
            _released = true;
            _cleanup = null;
            OwnedDevices.TryRemove(new KeyValuePair<ObjectPath, LinuxBluetoothDeviceLease>(_path, this));
        }
    }
}
