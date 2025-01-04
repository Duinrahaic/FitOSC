using Blazor.Bluetooth;
using FitOSC.Shared.Interfaces;
using FitOSC.Shared.Services;
using FitOSC.Shared.Components.UI;
using FitOSC.Shared.Config;
using FitOSC.Shared.Utilities;
using FitOSC.Shared.Extensions;
using FitOSC.Shared.Interfaces.GenericPad;
using Valve.VR;

namespace FitOSC.Pages;

public partial class Index : IDisposable
{
    private SettingsModal? SettingsModal { get; set; }
    private TestModal? TestModal { get; set; }
    private InfoPanel? InfoPanel { get; set; }

    private BaseLogic? _ftmsLogic = null;
    
    private FtmsData _liveData = new();
    private bool _walk;
    private bool _noDeviceMode;
    private decimal _trimSpeed = 0.8m;
    private System.Timers.Timer? _noDeviceModeTimer = new(1000);
    private FitOscConfig _config = new();
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                if (!OperatingSystem.IsBrowser())
                {
                    Osc.Start();
                    Osc.OnOscMessageReceived += OnOscMessageReceived;
                    Ovr.OnDataUpdateReceived += OnOvrDataUpdateReceived;
                }

               _config = await LocalStorage.GetConfig();
            }
            catch
            {
                // ignored
            }
        }
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
                    await (_ftmsLogic?.SetSpeed(_config.UserMaxSpeed,_config.UserMaxSpeed) ?? Task.CompletedTask);
                    await InvokeAsync(StateHasChanged);
                });
            }
        }

    }
    
   
#endregion

# region DeviceMode
    private void OnTreadmillStateChanged(object? sender, TreadmillState state)
    {
        InvokeAsync(StateHasChanged);
    }
    private void OnTreadmillDataChanged(object? sender, FtmsData data)
    {
        _liveData = data;
        InvokeAsync(StateHasChanged);
    }

    private async void DisconnectDevice()
    {
        if(_ftmsLogic != null)
        {
            _ftmsLogic.DataReceived -= OnTreadmillDataChanged;
            await _ftmsLogic.Disconnect();
            _ftmsLogic.Dispose();
            _ftmsLogic = null;
        }
            
        _noDeviceMode = false;
        _noDeviceModeTimer?.Dispose();
        _noDeviceModeTimer = null;
        await Task.Delay(3000);
    }
#endregion

#region OSC
    
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
            _lastInteraction = DateTime.Now;
            if(InfoPanel == null) return;
            await InfoPanel.IncreaseSpeed();
        }
        else if (e.Message.Address.Contains("TMC_SlowDown"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            _lastInteraction = DateTime.Now;
            if(InfoPanel == null) return;
            await InfoPanel.DecreaseSpeed();
        }
        else if (e.Message.Address.Contains("TMC_Stop"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            if(InfoPanel == null) return;
            await InfoPanel.Stop();
        }
        else if (e.Message.Address.Contains("TMC_Start"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            if(InfoPanel == null) return;
            await InfoPanel.Start();
        }
        else if (e.Message.Address.Contains("TMC_Pause"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            if(InfoPanel == null) return;
            await InfoPanel.Pause();
            
        }
        else if (e.Message.Address.Contains("TMC_Reset"))
        {
            bool value = Convert.ToBoolean(e.Message.Arguments[0]);
            if(value == false) return;
            if(InfoPanel == null) return;
            await InfoPanel.SendReset();
        }
        else if (e.Message.Address.Contains("TMC_WalkingTrim"))
        {
            try
            {            
                if(InfoPanel == null) return;
                InfoPanel.SetTrimSpeed(Convert.ToDecimal(e.Message.Arguments[0]));
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


#endregion

#region OpenVR

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

#endregion

#region General
    private async Task RequestDevice()
    {
            
        var options = new RequestDeviceOptions
        {
            Filters = [new Filter { Services = ["fitness_machine"] }]
        };
        IDevice? device = null;
        try{
            device = await Bt.RequestDevice(options);
        }
        catch(Exception ex)
        {
            device = null;
            Console.WriteLine(ex.Message);
        }
        if (device != null)
        {
            Console.WriteLine($"Device Name: {device.Name}, Device Id: {device.Id}");
            try
            {        
                _ftmsLogic = new GenericLogic(device);
                _ftmsLogic.DataReceived += OnTreadmillDataChanged;
                _ftmsLogic.TreadmillStateChanged += OnTreadmillStateChanged;
                await _ftmsLogic.Connect();
            }
            catch (Exception ex)
            {
                device = null;
                _ftmsLogic?.Dispose();
                Console.WriteLine(ex.Message);
            }
        }
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

#endregion
    
    private async Task OpenSettings()
    {
        if (SettingsModal == null)
            return;
        await SettingsModal.OpenModal();
    }
    
    private async void OnSettingsClose()
    {
        _config = await LocalStorage.GetConfig();
    }

    private async Task OpenTest()
    {
        if (TestModal == null)
            return;
        await TestModal.OpenModal();
    }
    
    private void ReleaseUnmanagedResources()
    {
        if (_ftmsLogic != null)
        {
            _ftmsLogic.DataReceived -= OnTreadmillDataChanged;
            _ftmsLogic.TreadmillStateChanged -= OnTreadmillStateChanged;
            _ftmsLogic.Dispose();
        }
 

        if (Osc != null)
        {
            Osc.OnOscMessageReceived -= OnOscMessageReceived;
        }

        if (Ovr != null)
        {
            Ovr.OnDataUpdateReceived -= OnOvrDataUpdateReceived;
        }
        
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