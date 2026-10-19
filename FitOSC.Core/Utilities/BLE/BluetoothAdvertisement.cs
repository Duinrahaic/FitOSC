namespace FitOSC.Utilities.BLE;

/// <summary>
/// A named device seen during a Bluetooth LE scan and the service UUIDs it advertised.
/// </summary>
public sealed record BluetoothAdvertisement(string Name, IReadOnlyList<Guid> ServiceUuids);
