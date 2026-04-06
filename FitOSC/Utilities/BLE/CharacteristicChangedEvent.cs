namespace FitOSC.Utilities.BLE;

public class CharacteristicChangedEvent : EventArgs
{
    public byte[] Value { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; }
}