using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using FitOSC.Services.OSC;
using FitOSC.Services.VRChat;
using Microsoft.JSInterop;
using Valve.VR;

namespace FitOSC.Services.Debug;

/// <summary>
/// Debug service that exposes methods to the browser console for testing
/// </summary>
public class DebugConsoleService(
    ILogger<DebugConsoleService> logger,
    AppStateService appState,
    TreadmillManager treadmill,
    IOscService osc,
    OpenVRService openVr,
    VRChatLocomotionService locomotionService)
{
    // FTMS and WalkingPad service UUIDs for driver detection
    private static readonly Guid FtmsServiceUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    private static readonly Guid WalkingPadServiceUuid = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");

    [JSInvokable("Debug_ScanTreadmills")]
    public async Task<string> ScanTreadmills(int durationSeconds = 5)
    {
        logger.LogInformation("Debug: Scanning for treadmills for {Duration} seconds", durationSeconds);

        var scanDuration = TimeSpan.FromSeconds(durationSeconds);
        var deviceInfo = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();

        // Use Windows BLE API directly to capture service UUIDs
        var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active
        };

        watcher.Received += (sender, args) =>
        {
            try
            {
                string name = args.Advertisement.LocalName;
                if (string.IsNullOrEmpty(name)) return;

                if (!deviceInfo.ContainsKey(name))
                {
                    deviceInfo[name] = new System.Collections.Generic.List<string>();
                }

                // Check for FTMS service
                if (args.Advertisement.ServiceUuids.Contains(FtmsServiceUuid) && !deviceInfo[name].Contains("FTMS"))
                {
                    deviceInfo[name].Add("FTMS");
                }

                // Check for WalkingPad service
                if (args.Advertisement.ServiceUuids.Contains(WalkingPadServiceUuid) && !deviceInfo[name].Contains("WalkingPad"))
                {
                    deviceInfo[name].Add("WalkingPad");
                }

                // If device doesn't advertise any treadmill services but has a name, still track it
                if (deviceInfo[name].Count == 0 && !deviceInfo[name].Contains("Unknown"))
                {
                    deviceInfo[name].Add("Unknown");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error processing BLE advertisement during scan");
            }
        };

        watcher.Start();
        await Task.Delay(scanDuration);
        watcher.Stop();

        // Format output
        var output = new System.Text.StringBuilder();
        var treadmillDevices = deviceInfo.Where(kvp => kvp.Value.Any(v => v != "Unknown")).ToList();

        output.AppendLine($"\n🔍 Treadmill Scan Results ({treadmillDevices.Count} devices found):");
        output.AppendLine("═══════════════════════════════════════════════");

        if (treadmillDevices.Count == 0)
        {
            output.AppendLine("No treadmills found. Make sure your treadmill is powered on and in pairing mode.");
        }
        else
        {
            foreach (var kvp in treadmillDevices.OrderBy(x => x.Key))
            {
                var drivers = string.Join(", ", kvp.Value);
                output.AppendLine($"  📱 {kvp.Key,-30} → Driver: {drivers}");
            }
            output.AppendLine("\nTo connect, use: FitOSC.connectTreadmill('DeviceName', 'DriverType')");
        }

        var result = output.ToString();
        logger.LogInformation("Scan complete: {Result}", result);
        return result;
    }

    [JSInvokable("Debug_GetConnectedDeviceInfo")]
    public async Task<string> GetConnectedDeviceInfo()
    {
        logger.LogInformation("Debug: Getting connected device info");
        return await treadmill.GetDeviceInfoAsync();
    }

    [JSInvokable("Debug_GetWalkingInfo")]
    public string GetWalkingInfo()
    {
        logger.LogInformation("Debug: Getting walking system info");
        return locomotionService.GetWalkingDebugInfo();
    }

    [JSInvokable("Debug_ConnectTreadmill")]
    public async Task ConnectTreadmill(string deviceName = "", string type = "FTMS")
    {
        logger.LogInformation("Debug: Connecting to treadmill - Device: {Device}, Type: {Type}", deviceName, type);
        var treadmillType = Enum.Parse<TreadmillType>(type, ignoreCase: true);
        await treadmill.ConnectAsync(deviceName, treadmillType);
    }

    [JSInvokable("Debug_DisconnectTreadmill")]
    public async Task DisconnectTreadmill()
    {
        logger.LogInformation("Debug: Disconnecting treadmill");
        await treadmill.DisconnectAsync();
    }

    [JSInvokable("Debug_SetSpeed")]
    public async Task SetSpeed(double speed)
    {
        logger.LogInformation("Debug: Setting speed to {Speed} km/h", speed);
        await treadmill.SetSpeedAsync((decimal)speed);
    }

    [JSInvokable("Debug_SetIncline")]
    public async Task SetIncline(double incline)
    {
        logger.LogInformation("Debug: Setting incline to {Incline}%", incline);
        await treadmill.SetInclineAsync((decimal)incline);
    }

    [JSInvokable("Debug_StartTreadmill")]
    public async Task StartTreadmill()
    {
        logger.LogInformation("Debug: Starting treadmill");
        await treadmill.StartAsync();
    }

    [JSInvokable("Debug_StopTreadmill")]
    public async Task StopTreadmill()
    {
        logger.LogInformation("Debug: Stopping treadmill");
        await treadmill.StopAsync();
    }

    [JSInvokable("Debug_SetBluetoothStatus")]
    public void SetBluetoothStatus(string status)
    {
        logger.LogInformation("Debug: Setting Bluetooth status to {Status}", status);
        var connectionStatus = Enum.Parse<ConnectionStatus>(status, ignoreCase: true);
        appState.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, connectionStatus);
    }

    [JSInvokable("Debug_SetWalkingMode")]
    public void SetWalkingMode(string mode)
    {
        logger.LogInformation("Debug: Setting walking mode to {Mode}", mode);
        var walkingMode = Enum.Parse<WalkingMode>(mode, ignoreCase: true);
        appState.SetWalkingMode(walkingMode);
    }

    [JSInvokable("Debug_SendOSC")]
    public void SendOSC(string address, string value)
    {
        logger.LogInformation("Debug: Sending OSC - Address: {Address}, Value: {Value}", address, value);

        // Try to parse as float, fallback to string
        if (float.TryParse(value, out var floatValue))
        {
            osc.SendMessage(address, floatValue);
        }
        else
        {
            osc.SendMessage(address, value);
        }
    }

    [JSInvokable("Debug_RestartOSC")]
    public void RestartOSC()
    {
        logger.LogInformation("Debug: Restarting OSC service");
        osc.RestartService();
    }

    [JSInvokable("Debug_ClearOSC")]
    public void ClearOSC()
    {
        logger.LogInformation("Debug: Clearing all OSC endpoints to default values");
        osc.SendMessage("/input/Vertical", 0.0f);
        osc.SendMessage("/input/LookHorizontal", 0.0f);
    }

    [JSInvokable("Debug_ReconnectVR")]
    public void ReconnectVR()
    {
        logger.LogInformation("Debug: Reconnecting to SteamVR");
        openVr.StartMonitoring();
    }

    [JSInvokable("Debug_DisconnectVR")]
    public void DisconnectVR()
    {
        logger.LogInformation("Debug: Disconnecting from SteamVR");
        openVr.StopMonitoring();
    }

    [JSInvokable("Debug_GetAppState")]
    public string GetAppState()
    {
        var state = appState.GetCurrentAppStateInfo();
        return System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [JSInvokable("Debug_Help")]
    public string Help()
    {
        return @"
FitOSC Debug Console Commands:
================================

Treadmill Control:
------------------
FitOSC.scanTreadmills(5)                        // Scan for treadmills (optional: duration in seconds, default 5)
FitOSC.getDeviceInfo()                          // Show services and characteristics of connected device
FitOSC.connectTreadmill('DeviceName', 'FTMS')  // Connect to treadmill
FitOSC.disconnectTreadmill()                    // Disconnect
FitOSC.startTreadmill()                         // Start workout
FitOSC.stopTreadmill()                          // Stop workout
FitOSC.setSpeed(5.0)                            // Set speed (km/h)
FitOSC.setIncline(2.5)                          // Set incline (%)

State Management:
-----------------
FitOSC.setBluetoothStatus('Connected')          // Set BT status (Disconnected/Connecting/Connected/Error)
FitOSC.setWalkingMode('Dynamic')                // Set walking mode (Disabled/Dynamic/Override)
FitOSC.getAppState()                            // Get current app state as JSON

OSC:
----
FitOSC.sendOSC('/input/Vertical', '1.0')        // Send OSC message
FitOSC.restartOSC()                             // Restart OSC service (rediscover VRChat)
FitOSC.clearOSC()                               // Reset all OSC endpoints to default (0.0)

VR:
---
FitOSC.reconnectVR()                            // Reconnect to SteamVR
FitOSC.disconnectVR()                           // Disconnect from SteamVR

Walking:
--------
FitOSC.getWalkingInfo()                         // Show walking mode, VR tracking, counter-steering, and output values

Other:
------
FitOSC.help()                                   // Show this help
        ";
    }
}
