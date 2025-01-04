using System.Windows.Forms;
using Blazor.Bluetooth;
using FitOSC.Shared.Interfaces;
using FitOSC.Shared.Interfaces.GenericPad;
using FitOSC.Shared.Interfaces.WalkingPad;
using FitOSC.Shared.Utilities;

namespace FitOSC.Shared.Extensions;

public static class BluetoothExtensions
{
    public static bool HasCharacteristic(this List<IBluetoothRemoteGATTCharacteristic> characteristics,
        string characteristicUuid)
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

    public static IBluetoothRemoteGATTCharacteristic? GetCharacteristic(
        this List<IBluetoothRemoteGATTCharacteristic> characteristics, string characteristicUuid)
    {
        try
        {
            var characteristic = characteristics.FirstOrDefault(x => Compare(characteristicUuid, x.Uuid));
            return characteristic;
        }
        catch
        {
            // ignore;
        }

        return null;
    }

    public static async Task<IBluetoothRemoteGATTCharacteristic?> GetAndSubscribeToCharacteristic(
        this List<IBluetoothRemoteGATTCharacteristic> characteristics, string characteristicUuid,
        EventHandler<CharacteristicEventArgs> callback)
    {
        try
        {
            var characteristic = characteristics.FirstOrDefault(x => Compare(characteristicUuid, x.Uuid));

            if (characteristic != null) await characteristic.SubscribeToNotifications(callback);

            return characteristic;
        }
        catch (Exception exception)
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

        var extractedUuidSegment = SegmentUUID(uuid);
        Console.WriteLine($"Comparing: '{characteristicUuid}' with extracted segment '{extractedUuidSegment}'");

        return string.Equals(characteristicUuid, extractedUuidSegment, StringComparison.OrdinalIgnoreCase);
    }

    public static string SegmentUUID(string uuid)
    {
        return uuid.Length >= 8 ? uuid.Substring(4, 4) : uuid;
    }


    public static async Task<IBluetoothRemoteGATTCharacteristic?> GetBluetoothCharacteristic(
        this IBluetoothRemoteGATTService service, string characteristicUuid)
    {
        var sanitizedUuid = characteristicUuid.ToLower().Replace(" ", "_");
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

    public static async Task SubscribeToNotifications(this IBluetoothRemoteGATTCharacteristic characteristic,
        EventHandler<CharacteristicEventArgs> callback)
    {
        characteristic.OnRaiseCharacteristicValueChanged += callback;
        await characteristic.StartNotifications();
    }

    public static async Task UnsubscribeFromNotifications(this IBluetoothRemoteGATTCharacteristic characteristic,
        EventHandler<CharacteristicEventArgs> callback)
    {
        try
        {
            characteristic.OnRaiseCharacteristicValueChanged -= callback;
            await characteristic.StopNotifications();
        }
        catch
        {
            // ignore
        }
    }


    public static async Task<bool> AttemptConnectionAsync(this IDevice device, int maxRetries, int delayMs,
        int timeoutMs)
    {
        var attempt = 0;
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

                Console.WriteLine($"Connection attempt {attempt} timed out. Retrying...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection attempt {attempt} failed: {ex.Message}");
            }

            if (attempt < maxRetries) await Task.Delay(delayMs);
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
            var initiallyConnected =
                await device.AttemptConnectionAsync(maxRetries, connectionDelayMs, connectionTimeoutMs);
            if (!initiallyConnected)
            {
                Console.WriteLine("Failed to establish initial connection after multiple attempts.");
                throw new TimeoutException("Failed to connect to the device after multiple attempts.");
            }
            
            var services = BluetoothServices.GetServices();
            services.Reverse();
            foreach (var uuid in services)
            {
                
                IBluetoothRemoteGATTService? service = null;
                try
                {
                    service = await device.Gatt.GetPrimaryService(uuid);
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex);
                }
                if (service == null)
                {
                    continue;
                }

                var characteristics = await service.GetCharacteristics();
                if (characteristics == null || characteristics.Count == 0)
                {
                    Console.WriteLine($"No characteristics found for service '{uuid}'.");
                    continue;
                }

                if (characteristics.HasCharacteristic("fe01")) return new WalkingPadLogic(device);

                if (characteristics.HasCharacteristic("2acd")) return new GenericLogic(device);
            }
            
        }
        catch(Exception ex)
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBox.Show(ex.ToString(), "FitOSC Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            }

        }

        return null;
    }
}