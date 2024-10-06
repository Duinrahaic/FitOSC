using Blazor.Bluetooth;

namespace FitOSC.Shared.Extensions;

public static class BluetoothExtensions
{
    public static async Task<IBluetoothRemoteGATTCharacteristic?> GetBluetoothCharacteristic(this IBluetoothRemoteGATTService service, string characteristicUuid)
    {
        string sanitizedUuid = characteristicUuid.ToLower().Replace(" ", "_");
        try
        {
            var characteristic = await service.GetCharacteristic(sanitizedUuid);
            return characteristic;
        }
        catch
        {
            // ignore;
        }

        return null;
    }
}