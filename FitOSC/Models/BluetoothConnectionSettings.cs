namespace FitOSC.Models;

public enum BluetoothConnectionType
{
    Local,
    ESPHome
}

public sealed record BluetoothConnectionSettings
{
    public BluetoothConnectionType Type { get; set; } = BluetoothConnectionType.Local;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6053;
    public string EncryptionKey { get; set; } = "";

    public void Validate()
    {
        if (!Enum.IsDefined(Type))
            throw new ArgumentException("Select a supported Bluetooth connection.");
        if (Type == BluetoothConnectionType.Local)
            return;
        if (Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter the ESPHome proxy's hostname or IP address, without a URL or port.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("The ESPHome API port must be between 1 and 65535.");

        byte[] key;
        try { key = Convert.FromBase64String(EncryptionKey.Trim()); }
        catch (FormatException) { throw new ArgumentException("Enter the ESPHome API encryption key from your firmware configuration."); }
        try
        {
            if (key.Length != 32)
                throw new ArgumentException("The ESPHome API encryption key must be a base64-encoded 32-byte key.");
            if (key.All(value => value == 0))
                throw new ArgumentException("ESPHome reserves the all-zero key. Use the API encryption key configured on your proxy.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }
}
