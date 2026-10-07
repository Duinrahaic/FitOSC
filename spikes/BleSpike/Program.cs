// Phase 0 BLE spike (Linux only): checks that BlueZ via Linux.Bluetooth can do what FitOSC needs
// from FTMS treadmills and WalkingPads: find the device, subscribe to telemetry, write a command,
// read a value, and reconnect after a disconnect.
//
//   dotnet run -- "<device name fragment>" ftms
//   dotnet run -- "<device name fragment>" walkingpad

using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;

if (args.Length < 2 || (args[1] != "ftms" && args[1] != "walkingpad"))
{
    Console.WriteLine("Usage: BleSpike \"<device name fragment>\" ftms|walkingpad");
    return 1;
}

var nameFragment = args[0];
var isFtms = args[1] == "ftms";
var serviceUuid = isFtms ? "00001826-0000-1000-8000-00805f9b34fb" : "0000ffe0-0000-1000-8000-00805f9b34fb";
var dataUuid = isFtms ? "00002acd-0000-1000-8000-00805f9b34fb" : "0000ffe1-0000-1000-8000-00805f9b34fb";
const string controlPointUuid = "00002ad9-0000-1000-8000-00805f9b34fb";
const string supportedSpeedUuid = "00002ad4-0000-1000-8000-00805f9b34fb";
var timeout = TimeSpan.FromSeconds(15);

var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
if (adapter == null)
{
    Console.WriteLine("FAIL: no Bluetooth adapter found");
    return 1;
}

Console.WriteLine($"Adapter: {adapter.Name}. Scanning for \"{nameFragment}\"...");
var device = await FindDeviceAsync(adapter, nameFragment, timeout);
if (device == null)
{
    Console.WriteLine("FAIL: device not found");
    return 1;
}

var firstRun = await RunSessionAsync(device, "first connection");
await device.DisconnectAsync();
Console.WriteLine("Disconnected. Reconnecting in 3 s...");
await Task.Delay(3000);
var secondRun = await RunSessionAsync(device, "reconnection");
await device.DisconnectAsync();

Console.WriteLine();
Console.WriteLine($"Result: first connection {(firstRun ? "PASS" : "FAIL")}, reconnection {(secondRun ? "PASS" : "FAIL")}");
return firstRun && secondRun ? 0 : 1;

async Task<bool> RunSessionAsync(Device target, string label)
{
    Console.WriteLine($"--- {label} ---");
    await target.ConnectAsync();
    await target.WaitForPropertyValueAsync("Connected", true, timeout);
    await target.WaitForPropertyValueAsync("ServicesResolved", true, timeout);

    var service = await target.GetServiceAsync(serviceUuid);
    if (service == null)
    {
        Console.WriteLine($"FAIL: service {serviceUuid} not found");
        return false;
    }

    var data = await service.GetCharacteristicAsync(dataUuid);
    if (data == null)
    {
        Console.WriteLine($"FAIL: characteristic {dataUuid} not found");
        return false;
    }

    var notifications = 0;
    byte[]? lastValue = null;
    data.Value += (_, e) =>
    {
        notifications++;
        lastValue = e.Value;
        return Task.CompletedTask;
    };

    if (isFtms)
    {
        var supportedSpeed = await service.GetCharacteristicAsync(supportedSpeedUuid);
        var speedRange = supportedSpeed == null ? null : await supportedSpeed.ReadValueAsync(timeout);
        Console.WriteLine($"Supported speed range (0x2AD4): {(speedRange == null ? "not available" : Convert.ToHexString(speedRange))}");

        var controlPoint = await service.GetCharacteristicAsync(controlPointUuid);
        if (controlPoint == null)
        {
            Console.WriteLine("FAIL: control point not found");
            return false;
        }

        var responses = 0;
        controlPoint.Value += (_, e) =>
        {
            responses++;
            Console.WriteLine($"Control point indication: {Convert.ToHexString(e.Value)}");
            return Task.CompletedTask;
        };

        // Give BlueZ a moment to enable indications before writing.
        await Task.Delay(500);
        var flags = await controlPoint.GetFlagsAsync();
        Console.WriteLine($"Control point flags: {string.Join(", ", flags)}");
        await controlPoint.WriteValueAsync([0x00], new Dictionary<string, object>()); // Request Control
        await Task.Delay(2000);
        Console.WriteLine($"Request Control indications received: {responses}");
    }

    Console.WriteLine("Collecting telemetry for 10 s (start the belt to see changing values)...");
    await Task.Delay(TimeSpan.FromSeconds(10));
    Console.WriteLine($"Telemetry notifications: {notifications}, last value: {(lastValue == null ? "none" : Convert.ToHexString(lastValue))}");
    return notifications > 0;
}

static async Task<Device?> FindDeviceAsync(Adapter adapter, string fragment, TimeSpan timeout)
{
    var found = new TaskCompletionSource<Device>(TaskCreationOptions.RunContinuationsAsynchronously);
    adapter.DeviceFound += async (_, e) =>
    {
        var properties = await e.Device.GetAllAsync();
        var name = properties.Name ?? properties.Alias ?? string.Empty;
        if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Found {name} ({properties.Address}), advertised services: {string.Join(", ", properties.UUIDs ?? [])}");
            found.TrySetResult(e.Device);
        }
    };

    await adapter.StartDiscoveryAsync();
    var completed = await Task.WhenAny(found.Task, Task.Delay(timeout));
    await adapter.StopDiscoveryAsync();
    return completed == found.Task ? found.Task.Result : null;
}
