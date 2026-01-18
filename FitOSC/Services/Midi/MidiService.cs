using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using NAudio.Midi;

namespace FitOSC.Services.Midi;

/// <summary>
/// Service for sending telemetry data as MIDI CC messages.
/// </summary>
public class MidiService : IHostedService, IDisposable
{
    private readonly ILogger<MidiService> _logger;
    private readonly ConfigurationService _configService;
    private readonly AppStateService _appStateService;

    private MidiOut? _midiOut;

    private bool _enabled;
    private int _outputDeviceIndex = -1;
    private int _channel = 1;
    private int _speedCC = 1;
    private int _heartRateCC = 2;
    private int _inclineCC = 3;

    private int _lastSpeedValue = -1;
    private int _lastHeartRateValue = -1;
    private int _lastInclineValue = -1;

    public MidiService(
        ILogger<MidiService> logger,
        ConfigurationService configService,
        AppStateService appStateService)
    {
        _logger = logger;
        _configService = configService;
        _appStateService = appStateService;

        LoadConfiguration();
    }

    /// <summary>
    /// Whether MIDI output is connected.
    /// </summary>
    public bool IsConnected => _midiOut != null;

    /// <summary>
    /// Whether MIDI is enabled in configuration.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Get list of available MIDI output devices.
    /// </summary>
    public static List<MidiDeviceInfo> GetOutputDevices()
    {
        var devices = new List<MidiDeviceInfo>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            var caps = MidiOut.DeviceInfo(i);
            devices.Add(new MidiDeviceInfo
            {
                Index = i,
                Name = caps.ProductName
            });
        }
        return devices;
    }

    private void LoadConfiguration()
    {
        var config = _configService.GetConfiguration();
        _enabled = config.Midi.Enabled;
        _channel = Math.Clamp(config.Midi.Channel, 1, 16);
        _speedCC = Math.Clamp(config.Midi.SpeedCC, 0, 127);
        _heartRateCC = Math.Clamp(config.Midi.HeartRateCC, 0, 127);
        _inclineCC = Math.Clamp(config.Midi.InclineCC, 0, 127);

        // Find device index by name
        _outputDeviceIndex = -1;

        if (!string.IsNullOrEmpty(config.Midi.OutputDeviceName))
        {
            var outputDevices = GetOutputDevices();
            var device = outputDevices.FirstOrDefault(d => d.Name == config.Midi.OutputDeviceName);
            if (device != null)
            {
                _outputDeviceIndex = device.Index;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfiguration();

        if (!_enabled)
        {
            _logger.LogInformation("MIDI service is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("MIDI service starting...");
        Connect();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MIDI service stopping...");
        Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connect to configured MIDI output device.
    /// </summary>
    public void Connect()
    {
        Disconnect();
        LoadConfiguration();

        if (!_enabled)
        {
            return;
        }

        if (_outputDeviceIndex >= 0 && _outputDeviceIndex < MidiOut.NumberOfDevices)
        {
            try
            {
                _midiOut = new MidiOut(_outputDeviceIndex);
                _logger.LogInformation("Connected to MIDI output: {Device}", MidiOut.DeviceInfo(_outputDeviceIndex).ProductName);

                // Subscribe to state updates for sending telemetry
                _appStateService.AppStateUpdated += OnAppStateUpdated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to MIDI output device {Index}", _outputDeviceIndex);
                _midiOut = null;
            }
        }
    }

    /// <summary>
    /// Disconnect from MIDI device.
    /// </summary>
    public void Disconnect()
    {
        _appStateService.AppStateUpdated -= OnAppStateUpdated;

        if (_midiOut != null)
        {
            try
            {
                _midiOut.Dispose();
            }
            catch { /* Ignore */ }
            _midiOut = null;
        }

        _lastSpeedValue = -1;
        _lastHeartRateValue = -1;
        _lastInclineValue = -1;
    }

    /// <summary>
    /// Reconnect to MIDI device (useful after configuration changes).
    /// </summary>
    public void Reconnect()
    {
        LoadConfiguration();

        if (!_enabled)
        {
            Disconnect();
            return;
        }

        Connect();
    }

    /// <summary>
    /// Send a MIDI Control Change message.
    /// </summary>
    public void SendCC(int controller, int value)
    {
        if (_midiOut == null) return;

        var ccValue = Math.Clamp(value, 0, 127);
        var ccNumber = Math.Clamp(controller, 0, 127);
        var channel = Math.Clamp(_channel - 1, 0, 15); // MIDI channels are 0-indexed internally

        try
        {
            var message = new ControlChangeEvent(0, channel + 1, (MidiController)ccNumber, ccValue);
            _midiOut.Send(message.GetAsShortMessage());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MIDI CC {Controller}={Value}", controller, value);
        }
    }

    /// <summary>
    /// Send a MIDI Note On message.
    /// </summary>
    public void SendNoteOn(int note, int velocity = 127)
    {
        if (_midiOut == null) return;

        var noteNum = Math.Clamp(note, 0, 127);
        var vel = Math.Clamp(velocity, 0, 127);
        var channel = Math.Clamp(_channel - 1, 0, 15);

        try
        {
            var message = new NoteOnEvent(0, channel + 1, noteNum, vel, 0);
            _midiOut.Send(message.GetAsShortMessage());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MIDI Note On {Note}", note);
        }
    }

    /// <summary>
    /// Send a MIDI Note Off message.
    /// </summary>
    public void SendNoteOff(int note)
    {
        if (_midiOut == null) return;

        var noteNum = Math.Clamp(note, 0, 127);
        var channel = Math.Clamp(_channel - 1, 0, 15);

        try
        {
            var message = new NoteEvent(0, channel + 1, MidiCommandCode.NoteOff, noteNum, 0);
            _midiOut.Send(message.GetAsShortMessage());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MIDI Note Off {Note}", note);
        }
    }

    private void OnAppStateUpdated(AppStateInfo state)
    {
        if (_midiOut == null) return;

        // Send speed as CC (0-127 mapped from 0-15 km/h typical range)
        var speedValue = (int)Math.Clamp(state.CurrentSpeed * 8.5m, 0, 127); // ~15 km/h = 127
        if (speedValue != _lastSpeedValue)
        {
            SendCC(_speedCC, speedValue);
            _lastSpeedValue = speedValue;
        }

        // Send incline as CC (0-127 mapped from 0-15% typical range)
        var inclineValue = (int)Math.Clamp(state.CurrentIncline * 8.5m, 0, 127); // ~15% = 127
        if (inclineValue != _lastInclineValue)
        {
            SendCC(_inclineCC, inclineValue);
            _lastInclineValue = inclineValue;
        }

        // Send heart rate as CC (0-127 mapped from ~40-200 bpm)
        // Uses CurrentHeartRate which automatically prefers Pulsoid when connected
        var hrValue = (int)Math.Clamp((state.CurrentHeartRate - 40) * 0.79375, 0, 127);
        if (hrValue != _lastHeartRateValue)
        {
            SendCC(_heartRateCC, hrValue);
            _lastHeartRateValue = hrValue;
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}

/// <summary>
/// Information about a MIDI device.
/// </summary>
public class MidiDeviceInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
}
