using System.Runtime;

namespace ExLlamaSharp.Performance;

/// <summary>
/// Helpers for tuning GC latency on the inference / HTTP hot path.
/// </summary>
public static class GcTuning
{
    /// <summary>
    /// Enable <see cref="GCLatencyMode.SustainedLowLatency"/> so Gen2 collections
    /// are deferred while the process serves traffic. Returns the previous mode.
    /// </summary>
    public static GCLatencyMode EnableSustainedLowLatency()
    {
        var previous = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        return previous;
    }

    /// <summary>
    /// Restore interactive (balanced) latency mode.
    /// </summary>
    public static void RestoreInteractive()
    {
        GCSettings.LatencyMode = GCLatencyMode.Interactive;
    }

    /// <summary>
    /// Restore a previously saved latency mode.
    /// </summary>
    public static void Restore(GCLatencyMode previous)
    {
        GCSettings.LatencyMode = previous;
    }

    /// <summary>
    /// Optional warm-up: force a compacting Gen2 collection before accepting load.
    /// </summary>
    public static void WarmUpHeap()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>
    /// Scope that enables sustained low latency for its lifetime.
    /// </summary>
    public static IDisposable BeginSustainedLowLatencyScope()
        => new LatencyScope(EnableSustainedLowLatency());

    private sealed class LatencyScope(GCLatencyMode previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Restore(previous);
        }
    }
}
