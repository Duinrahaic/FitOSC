namespace FitOSC.Utilities.BLE.ESPHome;

/// <summary>
/// A recognised reason an ESPHome native API session could not be opened. The message
/// is safe to show to the user; it never contains the encryption key.
/// </summary>
public sealed class ESPHomeConnectionException(string message, Exception? innerException = null)
    : IOException(message, innerException);
