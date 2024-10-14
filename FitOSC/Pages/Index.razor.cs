using Blazor.Bluetooth;
using FitOSC.Shared.Interfaces;
using FitOSC.Shared.Services;
using FitOSC.Shared.Components.UI;
using FitOSC.Shared.Config;
using FitOSC.Shared.Utilities;
using FitOSC.Shared.Extensions;
using Valve.VR;

namespace FitOSC.Pages;

public partial class Index : IDisposable
{
    private IDevice? _device;
    private IBluetoothRemoteGATTService? _treadmillService ;
    private IBluetoothRemoteGATTCharacteristic? _treadmillDataCharacteristic ;
    private IBluetoothRemoteGATTCharacteristic? _treadmillControlPoint;
    private IBluetoothRemoteGATTCharacteristic? _treadmillStatusCharacteristic;
    
    private SettingsModal? SettingsModal { get; set; }
    
    private FtmsData _liveData = new();
    private bool _walk;
    private bool _noDeviceMode;
    private decimal _trimSpeed = 0.8m; // Default to 80% of max speed

    private System.Timers.Timer? _noDeviceModeTimer = new(1000);
    private FitOscConfig _config = new();
    
    
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
               Osc.Start();
               Osc.OnOscMessageReceived += OnOscMessageReceived;
               Ovr.OnDataUpdateReceived += OnOvrDataUpdateReceived;
               _config = await ConfigService.GetConfig();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void OnOvrDataUpdateReceived(OpenVRDataEvent e)
    {
        if (_walk)
        {
            Osc.SetWalkingSpeed((_liveData.Speed / _config.UserMaxSpeed) * _trimSpeed);
            Osc.SetHorizontalSpeed((decimal)e.HorizontalAdjustment * (_liveData.Speed / _config.UserMaxSpeed) * _trimSpeed);
            
            if (e.Turn != OpenVRTurn.None)
            {
                Osc.SetTurningSpeed(e.Turn == OpenVRTurn.Left ? -0.2f : 0.2f);
            }
            else 
            {
                Osc.SetTurningSpeed(0);
            }
        }
        else
        {
            Osc.SetWalkingSpeed(0);
            Osc.SetTurningSpeed(0);
            Osc.SetHorizontalSpeed(0);
        }

        
    }

    private void SetTrimSpeed(decimal value)
    {
        _trimSpeed = Math.Clamp(value,0m,1m);
    }
    
    #region NoDeviceMode
    
    private void NoDeviceMode(){
        _noDeviceMode = true;
        _noDeviceModeTimer = new System.Timers.Timer(TimeSpan.FromSeconds(1).TotalMilliseconds);
        _liveData = new FtmsData();
        _noDeviceModeTimer.AutoReset = true;
        _noDeviceModeTimer.Elapsed +=  NoDeviceTimerUpdate;
        _noDeviceModeTimer.Start();
        StateHasChanged();
    }
   
    private async void NoDeviceTimerUpdate(object? sender, System.Timers.ElapsedEventArgs e)
    {
        _liveData.ElapsedTime++; // Increment duration every second
        // Calculate the distance for the current interval
        _liveData.Distance += DataConversion.ConvertSpeedToMetersPerSecond(_liveData.Speed);
        SetSpeed();
        await InvokeAsync(StateHasChanged);
    }
    
   
#endregion
   
    
    private async Task SetMaxSpeed(decimal s = 0)
    {
        var ms = Math.Clamp(s, _config.EquipmentMinSpeed, _config.EquipmentMaxSpeed);
        _config.UserMaxSpeed = ms;
        await ConfigService.SaveConfig(_config);
    }
    private async Task IncreaseMaxSpeed() => await SetMaxSpeed(_config.UserMaxSpeed + _config.IncrementAmount);
    private async Task DecreaseMaxSpeed() => await SetMaxSpeed(_config.UserMaxSpeed - _config.IncrementAmount);
    private void SetSpeed()
    {
        if(_config.UserMaxSpeed < _liveData.Speed)
        {
            if (_noDeviceMode)
            {
                _liveData.Speed = _config.UserMaxSpeed;
            }
            else
            {
                Task.Run(async () =>
                {
                    await _treadmillControlPoint.SetTargetSpeed(_config.UserMaxSpeed);
                    await InvokeAsync(StateHasChanged);
                });
            }
        }

    }
    private bool _increaseSpeed;
    private bool _decreaseSpeed;
    private DateTime _lastInteraction = DateTime.MinValue;
    private readonly TimeSpan _interactionTimeout = TimeSpan.FromSeconds(1);
    private async void OnOscMessageReceived(OscSubscriptionEvent e)
    {
        if (DateTime.Now - _lastInteraction <= _interactionTimeout) return;
        if(!e.Message.Address.Contains("TMC")) return;
        
        if (e.Message.Address.Contains("TMC_SpeedUp"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await IncreaseSpeed();
            _lastInteraction = DateTime.Now;
            _increaseSpeed = true;
            _decreaseSpeed = false;
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_SlowDown"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await DecreaseSpeed();
            _lastInteraction = DateTime.Now;
            _increaseSpeed = false;
            _decreaseSpeed = true;
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_Stop"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await Stop();
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_Start"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await Start();
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_Pause"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await Pause();
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_Reset"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            await SendReset();
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Message.Address.Contains("TMC_WalkingTrim"))
        {
            try
            {
                SetTrimSpeed(Convert.ToDecimal(e.Message.Arguments[0]));
                await InvokeAsync(StateHasChanged);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
                
        }
        else if (e.Message.Address.Contains("TMC_Walk"))
        {
            try
            {
                bool value = Convert.ToBoolean(e.Message.Arguments[0]);
                SetWalkingState(value);
                await InvokeAsync(StateHasChanged);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
                
        }
    }

 
    
    private async Task RequestDevice()
    {
        
        var options = new RequestDeviceOptions
        {
            Filters = [new Filter { Services = ["fitness_machine"] }]
        };
        
        try{
            _device = await Bt.RequestDevice(options);
        }
        catch(Exception ex)
        {
            _device = null;
            Console.WriteLine(ex.Message);
        }
        if (_device != null)
        {
            Console.WriteLine($"Device Name: {_device.Name}, Device Id: {_device.Id}");

            _device.OnAdvertisementReceived += DeviceOnOnAdvertisementReceived;
            await _device.Gatt.Connect();

            await Task.Delay(1000);

            try
            {
                _treadmillService = await _device.Gatt.GetPrimaryService(BluetoothServices.FitnessMachineService); // Fitness Machine Service
                _treadmillDataCharacteristic = await SubscribeToNotifications(BluetoothCharacteristic.TreadmillData, OnTreadmillDataChanged);
                _treadmillControlPoint = await SubscribeToNotifications(BluetoothCharacteristic.FitnessMachineControlPoint, OnControlPointChanged);
                _treadmillStatusCharacteristic = await SubscribeToNotifications(BluetoothCharacteristic.FitnessMachineStatus, OnTreadmillStatusChanged);

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    private async Task<IBluetoothRemoteGATTCharacteristic?> SubscribeToNotifications(string characteristicUuid, EventHandler<CharacteristicEventArgs> callback)
    {
        if (_treadmillService == null) return null;

        try
        {
            var characteristic = await _treadmillService.GetBluetoothCharacteristic(characteristicUuid);
            if (characteristic != null)
            {
                characteristic.OnRaiseCharacteristicValueChanged += callback;
                await characteristic.StartNotifications();
                return characteristic;
            }
        }
        catch
        {
            // Optionally handle or log the exception
        }
    
        return null; // Return null if there's an issue
    }
    private async Task Start() => await _treadmillControlPoint.Start();
    private async Task Stop() => await _treadmillControlPoint.Stop();
    private async Task Pause() => await _treadmillControlPoint.Pause();
    private async Task IncreaseSpeed()
    {
        if(_noDeviceMode == false) 
        {
            await _treadmillControlPoint.SetTargetSpeed(_liveData.Speed + _config.IncrementAmount,_config.UserMaxSpeed);
        }
        else
        {
            var s = _liveData.Speed + _config.IncrementAmount;
            if(s <= _config.UserMaxSpeed)
            {
                _liveData.Speed = s;
            }
        }
    }
    private async Task DecreaseSpeed()
    {
        if(_noDeviceMode == false) 
        {
            await _treadmillControlPoint.SetTargetSpeed(_liveData.Speed - _config.IncrementAmount,_config.UserMaxSpeed);
        }
        else
        {
            var s = _liveData.Speed - _config.IncrementAmount;
            if(s >= _config.EquipmentMinSpeed)
            {
                _liveData.Speed = s;
            }
        }
    }
    private async Task SendReset() => await _treadmillControlPoint.Reset();
    private void ToggleWalking(){
        SetWalkingState(!_walk);
    }
    private void SetWalkingState(bool state)
    {
        if(state == _walk) return;
        _walk = state;
        if (_walk)
        {
            Ovr.StartMonitoring();
        }
        else
        {
            Ovr.StopMonitoring();
            Osc.SetWalkingSpeed(0);
            Osc.SetTurningSpeed(0);
            Osc.SetHorizontalSpeed(0);
        }
        Osc.SetWakingState(_walk);
    }
    
    #region DeviceMode
    
    

    private void OnTreadmillStatusChanged(object? sender, CharacteristicEventArgs args)
    {
        byte[] value = args.Value;
        
        // The first byte of the notification contains the status event code.
        byte statusCode = value[0];
        
        switch (statusCode)
        {
            case 0x02: // Fitness Machine Stopped or Paused by the User
                // You may need to check additional control information in 'value' to distinguish between stopped or paused
                HandleStopOrPause(value);
                break;
            case 0x03: // Fitness Machine Stopped by Safety Key
                Console.WriteLine("Treadmill stopped by safety key.");
                break;
            case 0x04: // Fitness Machine Started or Resumed by the User
                Console.WriteLine("Treadmill is running.");
                break;
            default:
                Console.WriteLine("Unknown status.");
                break;
        }

        InvokeAsync(StateHasChanged);
    }
    private void HandleStopOrPause(byte[] value)
    {
        // Check additional control information in value[] (specific structure is dependent on your FTMS implementation)
        // For this example, assuming the second byte gives the control status (paused or stopped)
        var controlStatus = value[1];

        // Assuming any other value means stopped
        Console.WriteLine(controlStatus == 0x02 ? "Treadmill is paused." : "Treadmill is stopped."); // Assuming 0x01 means paused
    }
 
    
    private void OnTreadmillDataChanged(object? sender, CharacteristicEventArgs args) => HandleNotifications(args.Value);
    private void HandleNotifications(byte[] value)
    {
        _liveData = FtmsCommands.ReadFtmsData(value);
        InvokeAsync(StateHasChanged);
    }

    private void OnControlPointChanged(object? sender, CharacteristicEventArgs args) => HandleFtmsControlPoint(args.Value);
    private void HandleFtmsControlPoint(byte[] value)
    {
        byte opcode = value[0];
        byte result = value.Length > 1 ? value[1] : (byte)0;
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

    private void DeviceOnOnAdvertisementReceived(IBluetoothAdvertisingEvent obj)
    {
        Console.WriteLine($"Advertisement Received - RSSI: {obj.Name}");
    }

    private void DisconnectDevice()
    {

        _device?.Gatt.Disonnect();

        if (_device != null) _device.OnAdvertisementReceived -= DeviceOnOnAdvertisementReceived;
        if (_treadmillDataCharacteristic != null) _treadmillDataCharacteristic.OnRaiseCharacteristicValueChanged -= OnTreadmillDataChanged;
        if (_treadmillControlPoint != null) _treadmillControlPoint.OnRaiseCharacteristicValueChanged -= OnControlPointChanged;
        if (_treadmillStatusCharacteristic != null) _treadmillStatusCharacteristic.OnRaiseCharacteristicValueChanged -= OnTreadmillStatusChanged;
        
        _device = null;
        _noDeviceMode = false;
        _noDeviceModeTimer?.Dispose();
        _noDeviceModeTimer = null;

    }
    
    
    #endregion
    
    private async Task OpenSettings()
    {
        if (SettingsModal == null)
            return;
        await SettingsModal.OpenModal();
    }
    
    private async void OnSettingsClose()
    {
        _config = await ConfigService.GetConfig();
    }
    
    private void ReleaseUnmanagedResources()
    {
        if (_device != null)
        {
            _device.OnAdvertisementReceived -= DeviceOnOnAdvertisementReceived;
        }
        if (_treadmillDataCharacteristic != null)
        {
            _treadmillDataCharacteristic.OnRaiseCharacteristicValueChanged -= OnTreadmillDataChanged;
        }
        if (_treadmillControlPoint != null)
        {
            _treadmillControlPoint.OnRaiseCharacteristicValueChanged -= OnControlPointChanged;
        }
        if (_treadmillStatusCharacteristic != null)
        {
            _treadmillStatusCharacteristic.OnRaiseCharacteristicValueChanged -= OnTreadmillStatusChanged;
        }

        if (Osc != null)
        {
            Osc.OnOscMessageReceived -= OnOscMessageReceived;
        }

        if (Ovr != null)
        {
            Ovr.OnDataUpdateReceived -= OnOvrDataUpdateReceived;
        }
        
        _device = null;
        _treadmillService = null;
        _treadmillDataCharacteristic = null;
        _treadmillControlPoint = null;
        _treadmillStatusCharacteristic = null;
        SettingsModal = null;
    }
    
    protected virtual void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            _noDeviceModeTimer?.Dispose();
            Osc?.Dispose();
            Ovr?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Index()
    {
        Dispose(false);
    }
    
}