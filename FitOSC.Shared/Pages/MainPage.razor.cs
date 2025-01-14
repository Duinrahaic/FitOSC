using System.Formats.Tar;
using System.Timers;
using Blazor.Bluetooth;
using FitOSC.Shared.Components.UI;
using FitOSC.Shared.Config;
using FitOSC.Shared.Extensions;
using FitOSC.Shared.Interfaces;
using FitOSC.Shared.Services;
using FitOSC.Shared.Utilities;
using Microsoft.AspNetCore.Components;
using Valve.VR;
using RequestDeviceOptions = Blazor.Bluetooth.RequestDeviceOptions;
using Timer = System.Timers.Timer;

namespace FitOSC.Shared.Pages;

public sealed partial class MainPage : IDisposable
{
    private FitOscConfig _config = new();

    private BaseLogic? _ftmsLogic = null;
    private Session? _lastSession = null;
    private OpenVRDataEvent? _vrData = null;

    private FtmsData _liveData = new();
    private bool _noDeviceMode;
    private Timer? _noDeviceModeTimer = new(1000);
    private decimal _trimSpeed = 0.8m;
    private bool _walk;
    private SettingsModal? SettingsModal { get; set; }
    private TestModal? TestModal { get; set; }
    private InfoPanel? InfoPanel { get; set; }

    [Parameter] public EventCallback<bool> OnWalkingStateChanged { get; set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            try
            {
                if (!OperatingSystem.IsBrowser())
                {
                    Osc.Start();
                    Osc.OnOscMessageReceived += OnOscMessageReceived;
                }

                _config = await LocalStorage.GetConfig();
                _lastSession = await LocalStorage.GetLastSession();
            }
            catch
            {
                // ignored
            }
    }

    #region OpenVR
    public void OnOvrDataUpdateReceived(OpenVRDataEvent e) => _vrData = e;

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

        _vrData = null;
        SettingsModal = null;
        TestModal = null;
        _noDeviceModeTimer?.Dispose();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            _noDeviceModeTimer?.Dispose();
            Osc?.Dispose();
        }
    }

    ~MainPage()
    {
        Dispose(false);
    }

    #region NoDeviceMode

    private void NoDeviceMode()
    {
        _noDeviceMode = true;
        _noDeviceModeTimer = new Timer(TimeSpan.FromSeconds(1).TotalMilliseconds);
        _liveData = new FtmsData();
        _noDeviceModeTimer.AutoReset = true;
        _noDeviceModeTimer.Elapsed += NoDeviceTimerUpdate;
        _noDeviceModeTimer.Start();
        _mainViewOption = MainViewOption.Run;
        StateHasChanged();
    }

    private async void NoDeviceTimerUpdate(object? sender, ElapsedEventArgs e)
    {
        _liveData.ElapsedTime++; // Increment duration every second
        // Calculate the distance for the current interval
        _liveData.Distance += DataConversion.ConvertSpeedToMetersPerSecond(_liveData.Speed);
        SetSpeed();
        await InvokeAsync(StateHasChanged);
    }

    private void SetSpeed()
    {
        if (_config.UserMaxSpeed < _liveData.Speed)
        {
            if (_noDeviceMode)
                _liveData.Speed = _config.UserMaxSpeed;
            else
                Task.Run(async () =>
                {
                    await (_ftmsLogic?.SetSpeed(_config.UserMaxSpeed, _config.UserMaxSpeed) ?? Task.CompletedTask);
                    await InvokeAsync(StateHasChanged);
                });
        }
    }

    #endregion

    # region DeviceMode

    private void OnTreadmillStateChanged(object? sender, TreadmillState state)
    {
        InvokeAsync(StateHasChanged);
    }
    
    DateTime _lastSpeedUpdate = DateTime.MinValue;
    TimeSpan _udpateInterval = TimeSpan.FromSeconds(1);
    private void OnTreadmillDataChanged(object? sender, FtmsData data)
    {
        _liveData = data;
        if (DateTime.Now - _lastSpeedUpdate >= _udpateInterval)
        {
            _lastSpeedUpdate = DateTime.Now;
            InvokeAsync(StateHasChanged);
            if (_walk)
            {
                Osc.SetWalkingSpeed(_liveData.Speed / _config.UserMaxSpeed * _trimSpeed);
                Osc.SetHorizontalSpeed(
                    (decimal)(_vrData ?? new() ).HorizontalAdjustment 
                        * (_liveData.Speed / _config.UserMaxSpeed) * _trimSpeed);

                if ((_vrData ?? new() ).Turn != OpenVRTurn.None)
                    Osc.SetTurningSpeed((_vrData ?? new() ).Turn == OpenVRTurn.Left ? -0.2f : 0.2f);
                else
                    Osc.SetTurningSpeed(0);
            }
            else
            {
                Osc.SetWalkingSpeed(0);
                Osc.SetTurningSpeed(0);
                Osc.SetHorizontalSpeed(0);
            }
        }
        

        
    }

    private async void DisconnectDevice()
    {
        if (_ftmsLogic != null)
        {
            _ftmsLogic.DataReceived -= OnTreadmillDataChanged;
            await _ftmsLogic.Disconnect();
            _ftmsLogic.Dispose();
            _ftmsLogic = null;
        }

        _noDeviceMode = false;
        _noDeviceModeTimer?.Dispose();
        _noDeviceModeTimer = null;
        _mainViewOption = MainViewOption.Select;
        await InvokeAsync(StateHasChanged);
    }

    #endregion

    #region OSC

    private DateTime _lastInteraction = DateTime.MinValue;
    private readonly TimeSpan _interactionTimeout = TimeSpan.FromSeconds(1);

    private async void OnOscMessageReceived(OscSubscriptionEvent e)
    {
        if (DateTime.Now - _lastInteraction <= _interactionTimeout) return;
        if (!e.Message.Address.Contains("TMC")) return;

        if (e.Message.Address.Contains("TMC_SpeedUp"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            _lastInteraction = DateTime.Now;
            if (InfoPanel == null) return;
            await InfoPanel.IncreaseSpeed();
        }
        else if (e.Message.Address.Contains("TMC_SlowDown"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            _lastInteraction = DateTime.Now;
            if (InfoPanel == null) return;
            await InfoPanel.DecreaseSpeed();
        }
        else if (e.Message.Address.Contains("TMC_Stop"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            if (InfoPanel == null) return;
            await InfoPanel.Stop();
        }
        else if (e.Message.Address.Contains("TMC_Start"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            if (InfoPanel == null) return;
            await InfoPanel.Start();
        }
        else if (e.Message.Address.Contains("TMC_Pause"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            if (InfoPanel == null) return;
            await InfoPanel.Pause();
        }
        else if (e.Message.Address.Contains("TMC_Reset"))
        {
            var value = Convert.ToBoolean(e.Message.Arguments[0]);
            if (value == false) return;
            if (InfoPanel == null) return;
            await InfoPanel.SendReset();
        }
        else if (e.Message.Address.Contains("TMC_WalkingTrim"))
        {
            try
            {
                if (InfoPanel == null) return;
                InfoPanel.SetTrimSpeed(Convert.ToDecimal(e.Message.Arguments[0]));
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        else if (e.Message.Address.Contains("TMC_Walk"))
        {
            try
            {
                var value = Convert.ToBoolean(e.Message.Arguments[0]);
                SetWalkingState(value);
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    #endregion

    #region General

    private enum MainViewOption
    {
        Select,
        Check,
        Run
    }
    private MainViewOption _mainViewOption = MainViewOption.Select;    
    
    private InterfaceChecklist? _checklist = new();

    private async Task RequestDevice()
    {
        _mainViewOption = MainViewOption.Select;
        _checklist = new InterfaceChecklist();
        Console.WriteLine("Requesting Device");
        var options = new RequestDeviceOptions
        {
            Filters = [new Filter { Services = ["00001826-0000-1000-8000-00805f9b34fb"] }]
            //OptionalServices = BluetoothServices.GetServices().Select(x=>x.ToLower()).ToList(),
            //AcceptAllDevices = true
        };
        IDevice? device = null;
        try
        {
            device = await Bt.RequestDevice(options);
        }
        catch (Exception ex)
        {
            _checklist = null;
            device = null;
            Console.WriteLine(ex.Message);
        }

        if (device != null && _checklist != null)
        {
            _mainViewOption = MainViewOption.Check;
            StateHasChanged();
            Console.WriteLine($"Device Name: {device.Name}, Device Id: {device.Id}");
            try
            {
                _ftmsLogic = await BluetoothExtensions.IdentifyLogic(device);
                if (null == _ftmsLogic) throw new Exception("Device not supported");
                _checklist.IsIdentified = true;
                _checklist.InterfaceType = _ftmsLogic.GetType();
                _ftmsLogic.DataReceived += OnTreadmillDataChanged;
                _ftmsLogic.TreadmillStateChanged += OnTreadmillStateChanged;
                _ftmsLogic.LastSessionDataReceived += OnLastSessionDataReceived;
                await _ftmsLogic.Connect();
                _checklist.Initialized = true;
                _mainViewOption = MainViewOption.Run;
            }
            catch (Exception ex)
            {
                device = null;
                _mainViewOption = MainViewOption.Select;
                _ftmsLogic?.Dispose();
                Console.WriteLine(ex.Message);
                await Task.Delay(1000);
                _checklist = null;
                
            }
        }
    }

    private async void OnLastSessionDataReceived(object? sender, Session e)
    {
        await LocalStorage.SetLastSession(e);
        _lastSession = await LocalStorage.GetLastSession();
    }

    private void SetWalkingState(bool state)
    {
        if (state == _walk) return;
        _walk = state;
        if (!_walk)
        {
            Osc.SetWalkingSpeed(0);
            Osc.SetTurningSpeed(0);
            Osc.SetHorizontalSpeed(0);
        }

        Osc.SetWakingState(_walk);
        OnWalkingStateChanged.InvokeAsync(_walk);
    }

    #endregion
}