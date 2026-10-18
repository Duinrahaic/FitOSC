namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public sealed class CommandedValue
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    private bool _pending;
    private byte? _lastReported;
    private long _lastChangedAt;

    public byte Value { get; private set; }

    public void Command(byte value)
    {
        Value = value;
        _pending = true;
        _lastChangedAt = Environment.TickCount64; // settling is measured from the command
    }

    public void Report(byte value)
    {
        var now = Environment.TickCount64;
        if (value != _lastReported)
        {
            _lastReported = value;
            _lastChangedAt = now;
        }

        if (!_pending || value == Value || now - _lastChangedAt >= SettleTime.TotalMilliseconds)
        {
            Value = value;
            _pending = false;
        }
    }

    public void Reset()
    {
        Value = 0;
        _pending = false;
        _lastReported = null;
    }
}
