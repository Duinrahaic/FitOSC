namespace FitOSC.Services.Midi;

/// <summary>
/// A MIDI output port. Each platform provides its own implementation.
/// </summary>
public interface IMidiOutput : IDisposable
{
    /// <summary>
    /// Whether an output device is open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Names of the available MIDI output devices.
    /// </summary>
    IReadOnlyList<string> GetDeviceNames();

    /// <summary>
    /// Opens the output device with the given name, closing any open device first.
    /// </summary>
    /// <returns>False if no device has that name.</returns>
    bool Open(string deviceName);

    /// <summary>
    /// Sends a Control Change message.
    /// </summary>
    /// <param name="channel">MIDI channel, 1 to 16.</param>
    /// <param name="controller">Controller number, 0 to 127.</param>
    /// <param name="value">Controller value, 0 to 127.</param>
    void SendControlChange(int channel, int controller, int value);

    /// <summary>
    /// Closes the open output device, if any.
    /// </summary>
    void Close();
}
