namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public sealed class CommandedValue
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    // Commands come from the UI while reports come from notifications.
    private readonly object _gate = new();
    private bool _pending;
    private byte? _lastReported;
    private long _lastChangedAt;
    private byte _value;

    public byte Value
    {
        get
        {
            lock (_gate)
                return _value;
        }
    }

    public void Command(byte value)
    {
        lock (_gate)
        {
            _value = value;
            _pending = true;
            _lastChangedAt = Environment.TickCount64; // settling is measured from the command
        }
    }

    public void Report(byte value)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (value != _lastReported)
            {
                _lastReported = value;
                _lastChangedAt = now;
            }

            if (!_pending || value == _value || now - _lastChangedAt >= SettleTime.TotalMilliseconds)
            {
                _value = value;
                _pending = false;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _value = 0;
            _pending = false;
            _lastReported = null;
        }
    }
}
