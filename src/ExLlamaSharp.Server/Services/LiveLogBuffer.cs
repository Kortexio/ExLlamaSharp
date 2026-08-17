using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ExLlamaSharp.Server.Services;

public sealed record LiveLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception = null);

/// <summary>
/// In-memory ring buffer + fan-out channel for real-time server logs (UI / SSE).
/// </summary>
public sealed class LiveLogBuffer
{
    private readonly ConcurrentQueue<LiveLogEntry> _ring = new();
    private readonly object _gate = new();
    private readonly List<Channel<LiveLogEntry>> _subscribers = [];
    private readonly int _capacity;

    public LiveLogBuffer(int capacity = 2000)
    {
        _capacity = Math.Max(100, capacity);
    }

    public void Write(LiveLogEntry entry)
    {
        _ring.Enqueue(entry);
        while (_ring.Count > _capacity && _ring.TryDequeue(out _)) { }

        lock (_gate)
        {
            foreach (var ch in _subscribers.ToArray())
            {
                if (!ch.Writer.TryWrite(entry))
                {
                    // slow consumer — drop
                }
            }
        }
    }

    public IReadOnlyList<LiveLogEntry> Snapshot(int max = 500)
    {
        var arr = _ring.ToArray();
        if (arr.Length <= max)
        {
            return arr;
        }

        return arr.AsSpan(arr.Length - max).ToArray();
    }

    public ChannelReader<LiveLogEntry> Subscribe(CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<LiveLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        lock (_gate)
        {
            _subscribers.Add(ch);
        }

        ct.Register(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(ch);
            }

            ch.Writer.TryComplete();
        });

        return ch.Reader;
    }
}

public sealed class LiveLogLoggerProvider : ILoggerProvider
{
    private readonly LiveLogBuffer _buffer;

    public LiveLogLoggerProvider(LiveLogBuffer buffer) => _buffer = buffer;

    public ILogger CreateLogger(string categoryName) => new LiveLogLogger(categoryName, _buffer);

    public void Dispose() { }

    private sealed class LiveLogLogger(string category, LiveLogBuffer buffer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            buffer.Write(new LiveLogEntry(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                category,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
