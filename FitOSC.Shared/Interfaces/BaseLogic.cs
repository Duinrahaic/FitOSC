using Blazor.Bluetooth;
using FitOSC.Shared.Extensions;

namespace FitOSC.Shared.Interfaces;

public abstract class BaseLogic(IDevice device) : IDisposable
{
    protected virtual string LogicName { get; } = "Base Logic";
    private IBluetoothRemoteGATTService? Service { get; set; }
    protected virtual string ServiceUuid { get; } = "0x0000FE00_0000_1000_8000_00805F9B34FB";
    protected IBluetoothRemoteGATTCharacteristic? DataPoint { get; set; }
    protected virtual string DataPointUuid { get; } = "0x0000FE04_0000_1000_8000_00805F9B34FB";
    protected IBluetoothRemoteGATTCharacteristic? StatusPoint { get; set; }
    protected virtual string StatusPointUuid { get; } = "0x0000FE01_0000_1000_8000_00805F9B34FB";
    protected IBluetoothRemoteGATTCharacteristic? ControlPoint { get; set; }
    protected virtual string ControlPointUuid { get; } = "0x0000FE03_0000_1000_8000_00805F9B34FB";
    protected IBluetoothRemoteGATTCharacteristic? Features { get; set; }
    protected virtual string FeaturesUuid { get; } = "0x0000FE02_0000_1000_8000_00805F9B34FB";
    
    public event EventHandler<Session>? LastSessionDataReceived; 
    public event EventHandler<FtmsData>? DataReceived;
    public event EventHandler<TreadmillState>? TreadmillStateChanged;
    
    private readonly Session _session = new Session();
    protected IDevice Device { get; set; } = device;

    private TreadmillState _state = TreadmillState.Unknown;

    protected TreadmillState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            TreadmillStateChanged?.Invoke(this, _state);
        }
    }
    
    public virtual async Task Start() => await Task.CompletedTask;
    public virtual async Task Start(decimal startSpeed, decimal maxSpeed) => await Task.CompletedTask;

    public virtual async Task Stop()
    {
        if (State == TreadmillState.Running || State == TreadmillState.Paused)
        {
            LastSessionDataReceived?.Invoke(this, _session);
        }
        await Task.CompletedTask;
        
    }
    public virtual async Task Pause() => await Task.CompletedTask;
    public virtual async Task SetSpeed(decimal d, decimal maxSpeed) => await Task.CompletedTask;
    public virtual async Task Reset() => await Task.CompletedTask;
    protected virtual void ReadData(object? sender, CharacteristicEventArgs args)
    {
        var ftmsData = FtmsCommands.ReadFtmsData(args.Value);
        _session.Calories = ftmsData.Calories;
        _session.Distance = ftmsData.Distance;
        _session.ElapsedTime = ftmsData.ElapsedTime;
        DataReceived?.Invoke(this, ftmsData);
    }

    protected virtual void OnTreadmillFeaturesUpdate(object? sender, CharacteristicEventArgs args)
    {
        var features = FitnessMachineFeatures.Parse(args.Value);
    }

    
    
    public virtual async Task Connect()
    { 
        const int maxRetries = 3;
        const int connectionDelayMs = 1000; // Delay before next connection attempt
        const int connectionTimeoutMs = 5000; // Connection timeout per attempt

        try
        {
            // First, try connecting to the device with retries
            bool initiallyConnected = await device.AttemptConnectionAsync(maxRetries, connectionDelayMs, connectionTimeoutMs);
            if (!initiallyConnected)
            {
                Console.WriteLine("Failed to establish initial connection after multiple attempts.");
                throw new TimeoutException("Failed to connect to the device after multiple attempts.");
            }

            // Attempt to get primary Service
            Service = await Device.Gatt.GetPrimaryService(ServiceUuid);
            if (Service == null)
            {
                Console.WriteLine($"Could not find {LogicName} on the device.");
                throw new InvalidOperationException($"{LogicName} Service not found.");
            }

            // After obtaining the service, ensure the connection is still solid
            // Some devices may require a "reconnect" logic or a stable connection before reading characteristics
            bool reconnected = await device.AttemptConnectionAsync(maxRetries, connectionDelayMs, connectionTimeoutMs);
            if (!reconnected)
            {
                Console.WriteLine("Failed to reconnect to the device after obtaining the service.");
                throw new TimeoutException("Failed to reconnect to the device to access characteristics.");
            }
            
            // Initialize Characteristics
            await Initialize(maxRetries, connectionDelayMs, connectionTimeoutMs);

            Console.WriteLine("Device connected and characteristics initialized successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred during connection/initialization: {ex.Message}");
            Device?.Gatt?.Disonnect();

            // Reset all fields in case of an error
            Service = null;
            Features = null;
            DataPoint = null;
            ControlPoint = null;
            StatusPoint = null;

            throw; // Rethrow the exception after cleanup
        }
    }
    
    private async Task Initialize(int maxRetries, int delayMs, int timeoutMs)
    {
        if(Service == null)
        {
            throw new InvalidOperationException($"{LogicName} Service not found.");
        }
        
        // Get characteristics
        var characteristics = await Service.GetCharacteristics();
        if (characteristics == null || !characteristics.Any())
        {
            throw new InvalidOperationException($"No characteristics found in the {LogicName} Service.");
        }

        // Assign characteristics
        Features = characteristics.GetCharacteristic(FeaturesUuid);
        DataPoint = await characteristics.GetAndSubscribeToCharacteristic(DataPointUuid, ReadData);
        ControlPoint =
            await characteristics.GetAndSubscribeToCharacteristic(ControlPointUuid, HandleControlPoint);
        StatusPoint = await characteristics.GetAndSubscribeToCharacteristic(StatusPointUuid, HandleStatusChange);

        // Verify that critical characteristics are available
        if (Features == null || ControlPoint == null)
        {
            // If critical characteristics are missing, try reconnecting and retrying a few times
            for (int i = 0; i < maxRetries; i++)
            {
                Console.WriteLine("Critical characteristics not found. Retrying to reinitialize...");
                await Task.Delay(delayMs);

                // Attempt reconnection and re-fetching characteristics
                bool connected = await device.AttemptConnectionAsync(1, delayMs, timeoutMs);
                if (connected)
                {
                    characteristics = await Service.GetCharacteristics();
                    DataPoint = await characteristics.GetAndSubscribeToCharacteristic(DataPointUuid, ReadData);
                    ControlPoint =
                        await characteristics.GetAndSubscribeToCharacteristic(ControlPointUuid, HandleControlPoint);

                    if (DataPoint != null && ControlPoint != null)
                    {
                        break; // Successfully reinitialized critical characteristics
                    }
                }

                if (i == maxRetries - 1)
                {
                    throw new InvalidOperationException(
                        $"Unable to initialize critical {LogicName} characteristics after multiple retries.");
                }
            }
        }
    }

    
    
    public virtual async Task Disconnect()
    {
        if (ControlPoint != null)
        {
            await ControlPoint.UnsubscribeFromNotifications(HandleControlPoint);
            ControlPoint = null;
        }

        if (DataPoint != null)
        {
            await DataPoint.UnsubscribeFromNotifications(ReadData);
            DataPoint = null;
        }

        if (Features != null)
        {
            Features = null;
        }

        if (StatusPoint != null)
        {
            await StatusPoint.UnsubscribeFromNotifications(HandleStatusChange);
            StatusPoint = null;
        }

        try
        {
            await Device.Gatt.Disonnect();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    
    
    public virtual async Task<FitnessMachineFeatures> GetFeatures()
    {
        await Task.CompletedTask;
        return new FitnessMachineFeatures();
    }
    protected byte[] ConvertSpeed(decimal speed)
    {
        
        try
        {
            ushort speedInCmPerSecond = (ushort)(speed * 100);
            byte[] speedBytes = new byte[]
            {
                (byte)(speedInCmPerSecond & 0xFF),         // Lower byte of speed
                (byte)((speedInCmPerSecond >> 8) & 0xFF)   // Upper byte of speed
            };
            return speedBytes;
        }
        catch
        {
            //throw new Exception($"Failed to convert {speed} to byte array.");
        }
        return new byte[0];
    }
    
    protected byte[] ConvertSpeed(decimal speed, decimal maxSpeed)
    {
        try
        {
            if (speed >= maxSpeed) 
                return ConvertSpeed(maxSpeed);
            return ConvertSpeed(speed);
        }
        catch
        {
            //throw new Exception($"Failed to convert {speed} to byte array.");
        }
        return new byte[0];
    }
    
    
    
    protected virtual void HandleControlPoint(object? sender, CharacteristicEventArgs args)
    {
        byte opcode = args.Value[0];
        byte result = args.Value.Length > 1 ? args.Value[1] : (byte)0;
        string commandName = opcode switch
        {
            0x80 => "Request Control",
            0x81 => "Reset",
            0x82 => "Set Target Speed",
            0x83 => "Set Incline",
            0x84 => "Set Resistance Level",
            0x85 => "Start or Resume",
            0x86 => "Stop or Pause",
            0x87 => "Set Power",
            0x88 => "Set Heart Rate",
            0x8F => "General Error",
            _ => "Unknown"
        };

        Console.WriteLine($"> Received '{commandName}' response, Result: {result}");
    }
    protected virtual void HandleStatusChange(object? sender, CharacteristicEventArgs args)
    {
        byte[] value = args.Value;

        // The first byte of the notification contains the status event code.
        byte statusCode = value[0];

        switch (statusCode)
        {
            case 0x02: // Fitness Machine Stopped or Paused by the User
                State = value[1] == 0x02 ? TreadmillState.Paused : TreadmillState.Stopped;
                break;
            case 0x03: // Fitness Machine Stopped by Safety Key
                State = TreadmillState.StoppedSafety;
                break;
            case 0x04: // Fitness Machine Started or Resumed by the User
                State = TreadmillState.Running;
                break;
            default:
                State = TreadmillState.Unknown;
                break;
        }
    }
    
    
    
    public virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // TODO release managed resources here
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}