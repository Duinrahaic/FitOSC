using System.Threading.Channels;

namespace FitOSC.Services.Logger;

public sealed class LogStreamService
{
    public const int Capacity = 500;

    private readonly object _gate = new();
    private readonly Queue<LogStreamEntry> _history = new(Capacity);
    private readonly HashSet<Subscription> _subscriptions = new();
    private long _sequence;

    public void Append(DateTimeOffset timestamp, LogStreamLevel level, string source, string message)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            var entry = new LogStreamEntry(++_sequence, timestamp, level, source, message);
            if (_history.Count == Capacity)
                _history.Dequeue();
            _history.Enqueue(entry);

            foreach (var subscription in _subscriptions)
                subscription.Channel.Writer.TryWrite(entry);
        }
    }

    // History and live delivery share one ordered queue, registered atomically with append.
    // Slow subscribers retain only the newest Capacity pending entries.
    public Subscription Subscribe()
    {
        lock (_gate)
        {
            var subscription = new Subscription(this);
            foreach (var entry in _history)
                subscription.Channel.Writer.TryWrite(entry);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            subscription.Channel.Writer.TryComplete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly LogStreamService _owner;
        internal Channel<LogStreamEntry> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<LogStreamEntry>(new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        internal Subscription(LogStreamService owner) => _owner = owner;

        public ChannelReader<LogStreamEntry> Reader => Channel.Reader;

        public void Dispose() => _owner.Unsubscribe(this);
    }
}
