using Blazor.Bluetooth;
using FitOSC.Shared.Interfaces;

namespace FitOSC.Shared.Extensions;

public static class BluetoothExtensions
{
    public static bool HasCharacteristic(this List<IBluetoothRemoteGATTCharacteristic> characteristics, string characteristicUuid)
    {
        try
        {
            return characteristics.Any(x => Compare(characteristicUuid, x.Uuid));
        }
        catch
        {
            // ignore;
        }

        return false;
    }
    public static IBluetoothRemoteGATTCharacteristic? GetCharacteristic(this List<IBluetoothRemoteGATTCharacteristic> characteristics, string characteristicUuid)
    {
        try
        {
            var characteristic = characteristics.FirstOrDefault(x => Compare(characteristicUuid, x.Uuid));
            return characteristic;
        }catch
        {
            // ignore;
        }

        return null;
    }
    public static async Task<IBluetoothRemoteGATTCharacteristic?> GetAndSubscribeToCharacteristic(this List<IBluetoothRemoteGATTCharacteristic> characteristics, string characteristicUuid, EventHandler<CharacteristicEventArgs> callback)
    {
        try
        {
            var characteristic = characteristics.FirstOrDefault(x => Compare(characteristicUuid, x.Uuid));
            
            if (characteristic != null)
            {
                await characteristic.SubscribeToNotifications(callback);
            }

            return characteristic;
        }catch(Exception exception)
        {
            // ignore;
        }

        return null;
    }
    
    private static bool Compare(string characteristicUuid, string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            Console.WriteLine($"Invalid uuid: {uuid ?? "null"}");
            return false;
        }

        if (string.IsNullOrEmpty(characteristicUuid) || characteristicUuid.Length < 4)
        {
            Console.WriteLine($"Invalid characteristicUuid: {characteristicUuid ?? "null"}");
            return false;
        }

        var extractedUuidSegment = uuid.Length >= 8 ? uuid.Substring(4, 4) : uuid;
        Console.WriteLine($"Comparing: '{characteristicUuid}' with extracted segment '{extractedUuidSegment}'");

        return string.Equals(characteristicUuid, extractedUuidSegment, StringComparison.OrdinalIgnoreCase);
    }

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
    
    public static async Task SubscribeToNotifications(this IBluetoothRemoteGATTCharacteristic characteristic,  EventHandler<CharacteristicEventArgs> callback)
    {
        characteristic.OnRaiseCharacteristicValueChanged += callback;
        await characteristic.StartNotifications();
    }
    public static async Task UnsubscribeFromNotifications(this IBluetoothRemoteGATTCharacteristic characteristic,  EventHandler<CharacteristicEventArgs> callback)
    {
        characteristic.OnRaiseCharacteristicValueChanged -= callback;
        await characteristic.StopNotifications();
    }
    
    
    public static async Task<bool> AttemptConnectionAsync(this IDevice device, int maxRetries, int delayMs, int timeoutMs)
    {
        int attempt = 0;
        while (attempt < maxRetries)
        {
            attempt++;
            try
            {
                Console.WriteLine($"Attempting connection (attempt {attempt}/{maxRetries})...");
                var connectTask = device.Gatt.Connect();
                var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

                if (completedTask == connectTask)
                {
                    Console.WriteLine("Connected successfully.");
                    return true;
                }
                else
                {
                    Console.WriteLine($"Connection attempt {attempt} timed out. Retrying...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection attempt {attempt} failed: {ex.Message}");
            }

            if (attempt < maxRetries)
            {
                await Task.Delay(delayMs);
            }
        }

        return false;
    }
    
    public static async Task<BaseLogic?> IdentifyLogic(IDevice device)
    {
        const int maxRetries = 3;
        const int connectionDelayMs = 1000; // Delay before next connection attempt
        const int connectionTimeoutMs = 5000; // Connection timeout per attempt
        
        try
        {
            bool initiallyConnected = await device.AttemptConnectionAsync(maxRetries, connectionDelayMs, connectionTimeoutMs);
            if (!initiallyConnected)
            {
                Console.WriteLine("Failed to establish initial connection after multiple attempts.");
                throw new TimeoutException("Failed to connect to the device after multiple attempts.");
            }

            List<string> services = BluetoothServices.GetServices();
            foreach (var serviceUuid in services)
            {
                var service = await device.Gatt.GetPrimaryService(serviceUuid);
                if (service == null)
                {
                    Console.WriteLine($"Service '{serviceUuid}' not found.");
                    continue;
                }

                var characteristics = await service.GetCharacteristics();
                if (characteristics == null || characteristics.Count == 0)
                {
                    Console.WriteLine($"No characteristics found for service '{serviceUuid}'.");
                    continue;
                }

                if(characteristics.HasCharacteristic("fe01"))
                {
                    return new WalkingPadLogic(device);
                }

                if(characteristics.HasCharacteristic("2acd"))
                {
                    return new GenericLogic(device);
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}

