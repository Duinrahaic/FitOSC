using System.Text;
using FitOSC.Services.Midi;

namespace FitOSC.Platform.Midi;

/// <summary>MIDI output through RtMidi's WinMM or ALSA backend.</summary>
public sealed class RtMidiOutput : IMidiOutput
{
    private readonly object _gate = new();
    private IntPtr _device;
    private bool _disposed;

    public bool IsOpen
    {
        get { lock (_gate) return _device != IntPtr.Zero; }
    }

    public IReadOnlyList<string> GetDeviceNames()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var device = CreateDevice();
            try
            {
                var names = new List<string>();
                var count = GetPortCount(device);
                for (uint i = 0; i < count; i++)
                {
                    var name = GetPortName(device, i);
                    // RtMidi returns an empty name when a port vanishes during enumeration.
                    if (name.Length != 0) names.Add(name);
                }
                return names;
            }
            finally { RtMidiNative.rtmidi_out_free(device); }
        }
    }

    public bool Open(string deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseDevice();
            // ok is sticky in 6.0.0: each open gets a fresh wrapper.
            var device = CreateDevice();
            try
            {
                var count = GetPortCount(device);
                for (uint i = 0; i < count; i++)
                {
                    var name = GetPortName(device, i);
                    if (name.Length == 0 || !string.Equals(name, deviceName, StringComparison.Ordinal)) continue;
                    RtMidiNative.rtmidi_open_port(device, i, "FitOSC");
                    RtMidiNative.Check(device, $"open output port {i} ({name})");
                    _device = device;
                    device = IntPtr.Zero;
                    return true;
                }
                return false;
            }
            finally
            {
                if (device != IntPtr.Zero) RtMidiNative.rtmidi_out_free(device);
            }
        }
    }

    public void SendControlChange(int channel, int controller, int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(controller);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(controller, 127);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 127);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device == IntPtr.Zero) return;
            const string operation = "send a MIDI Control Change message";
            RtMidiNative.Check(_device, operation);
            byte[] message = [(byte)(0xB0 | (channel - 1)), (byte)controller, (byte)value];
            var result = RtMidiNative.rtmidi_out_send_message(_device, message, message.Length);
            RtMidiNative.Check(_device, operation);
            if (result != 0) throw RtMidiNative.Failure(operation);
        }
    }

    public void Close()
    {
        lock (_gate) CloseDevice();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseDevice();
        }
    }

    private void CloseDevice()
    {
        var device = _device;
        _device = IntPtr.Zero;
        // free invokes the backend destructor, which closes the port.
        if (device != IntPtr.Zero) RtMidiNative.rtmidi_out_free(device);
    }

    private static IntPtr CreateDevice()
    {
        var device = RtMidiNative.rtmidi_out_create(0 /* UNSPECIFIED */, "FitOSC");
        try
        {
            RtMidiNative.Check(device, "create a MIDI output client");
            return device;
        }
        catch
        {
            if (device != IntPtr.Zero) RtMidiNative.rtmidi_out_free(device);
            throw;
        }
    }

    private static uint GetPortCount(IntPtr device)
    {
        var count = RtMidiNative.rtmidi_get_port_count(device);
        RtMidiNative.Check(device, "enumerate MIDI output ports");
        return count;
    }

    private static string GetPortName(IntPtr device, uint index)
    {
        var operation = $"read the name of MIDI output port {index}";
        var length = 0;
        var result = RtMidiNative.rtmidi_get_port_name(device, index, null, ref length);
        RtMidiNative.Check(device, operation);
        if (result != 0 || length < 1) throw RtMidiNative.Failure(operation);
        var buffer = new byte[length];
        result = RtMidiNative.rtmidi_get_port_name(device, index, buffer, ref length);
        RtMidiNative.Check(device, operation);
        // snprintf returns the untruncated byte count, excluding its terminator.
        if (result < 0 || result >= buffer.Length) throw RtMidiNative.Failure($"{operation} (invalid or changed name length)");
        return Encoding.UTF8.GetString(buffer, 0, result);
    }
}
