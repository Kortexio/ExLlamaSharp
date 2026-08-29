using System.Collections.Concurrent;

namespace ExLlamaSharp.Server.Services;

public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);

    public bool TryAcquire(string keyId, int rpm, int tpm, int estimatedTokens, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        retryAfterSeconds = 0;

        if (rpm <= 0 && tpm <= 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = _windows.GetOrAdd(keyId, _ => new WindowState());

        lock (state)
        {
            Prune(state.RequestTimestamps, now, 60_000);
            PruneTokens(state.TokenEvents, now, 60_000);

            if (rpm > 0 && state.RequestTimestamps.Count >= rpm)
            {
                retryAfterSeconds = ComputeRetryAfter(state.RequestTimestamps.Peek(), now, 60_000);
                return false;
            }

            // Soft check against actual tokens already recorded (post-completion).
            // Do not enqueue the estimate — RecordTokens settles real usage once.
            var tokensUsed = state.TokenEvents.Sum(e => e.Tokens);
            if (tpm > 0 && tokensUsed + Math.Max(0, estimatedTokens) > tpm)
            {
                retryAfterSeconds = state.TokenEvents.Count > 0
                    ? ComputeRetryAfter(state.TokenEvents.Peek().TimestampMs, now, 60_000)
                    : 60;
                return false;
            }

            state.RequestTimestamps.Enqueue(now);
            return true;
        }
    }

    public void RecordTokens(string keyId, int tokens)
    {
        if (tokens <= 0 || string.IsNullOrWhiteSpace(keyId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = _windows.GetOrAdd(keyId, _ => new WindowState());
        lock (state)
        {
            state.TokenEvents.Enqueue(new TokenEvent(now, tokens));
        }
    }

    public void Reset(string keyId) => _windows.TryRemove(keyId, out _);

    private static void Prune(Queue<long> timestamps, long now, long windowMs)
    {
        while (timestamps.Count > 0 && now - timestamps.Peek() > windowMs)
        {
            timestamps.Dequeue();
        }
    }

    private static void PruneTokens(Queue<TokenEvent> events, long now, long windowMs)
    {
        while (events.Count > 0 && now - events.Peek().TimestampMs > windowMs)
        {
            events.Dequeue();
        }
    }

    private static int ComputeRetryAfter(long oldestMs, long nowMs, long windowMs)
    {
        var waitMs = windowMs - (nowMs - oldestMs);
        return Math.Max(1, (int)Math.Ceiling(waitMs / 1000.0));
    }

    private sealed class WindowState
    {
        public Queue<long> RequestTimestamps { get; } = new();
        public Queue<TokenEvent> TokenEvents { get; } = new();
    }

    private readonly record struct TokenEvent(long TimestampMs, int Tokens);
}
