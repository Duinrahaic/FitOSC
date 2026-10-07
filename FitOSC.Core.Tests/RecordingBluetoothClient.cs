using FitOSC.Utilities.BLE;

namespace FitOSC.Core.Tests;

internal sealed class RecordingBluetoothClient : IBluetoothClient
{
    public bool IsConnected => false;
    public List<(Guid Characteristic, byte[] Command)> Writes { get; } = new();

    public Task WriteAsync(Guid characteristicUuid, byte[] command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add((characteristicUuid, command.ToArray()));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Fail immediately if a unit test accidentally starts device discovery or a real connection path.
    public Task<IReadOnlyList<BluetoothAdvertisement>> ScanAsync(TimeSpan duration) => throw new NotSupportedException();
    public Task<bool> ConnectAsync(Guid serviceUuid, string? deviceName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SubscribeAsync(Guid characteristicUuid, Action<byte[]>? callback, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UnsubscribeAsync(Guid characteristicUuid) => throw new NotSupportedException();
    public Task<byte[]?> ReadAsync(Guid characteristicUuid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DisconnectAsync() => throw new NotSupportedException();
    public Task<string> GetDeviceInfoAsync() => throw new NotSupportedException();
}
