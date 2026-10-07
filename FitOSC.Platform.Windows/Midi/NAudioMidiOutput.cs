using FitOSC.Services.Midi;
using NAudio.Midi;

namespace FitOSC.Platform.Windows.Midi;

/// <summary>
/// MIDI output through the Windows multimedia API (winmm) using NAudio.
/// </summary>
public class NAudioMidiOutput : IMidiOutput
{
    private MidiOut? _midiOut;

    public bool IsOpen => _midiOut != null;

    public IReadOnlyList<string> GetDeviceNames()
    {
        var names = new List<string>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            names.Add(MidiOut.DeviceInfo(i).ProductName);
        }
        return names;
    }

    public bool Open(string deviceName)
    {
        Close();

        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            if (MidiOut.DeviceInfo(i).ProductName == deviceName)
            {
                _midiOut = new MidiOut(i);
                return true;
            }
        }

        return false;
    }

    public void SendControlChange(int channel, int controller, int value)
    {
        if (_midiOut == null) return;

        var message = new ControlChangeEvent(0, channel, (MidiController)controller, value);
        _midiOut.Send(message.GetAsShortMessage());
    }

    public void Close()
    {
        _midiOut?.Dispose();
        _midiOut = null;
    }

    public void Dispose() => Close();
}
