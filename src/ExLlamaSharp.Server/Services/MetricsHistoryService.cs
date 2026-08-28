using System.Collections.Concurrent;

namespace ExLlamaSharp.Server.Services;

/// <summary>In-memory ring buffers for Metrics charts (TPS + latency from audit).</summary>
public sealed class MetricsHistoryService
{
    private const int Capacity = 120;
    private readonly object _gate = new();
    private readonly Queue<double> _tps = new();
    private readonly Queue<double> _latency = new();
    private readonly ConcurrentQueue<long> _recentLatencyMs = new();

    public IReadOnlyList<double> TpsSeries
    {
        get
        {
            lock (_gate)
            {
                return _tps.ToArray();
            }
        }
    }

    public IReadOnlyList<double> LatencySeries
    {
        get
        {
            lock (_gate)
            {
                return _latency.ToArray();
            }
        }
    }

    public double LatencyP50Ms
    {
        get
        {
            var arr = SnapshotLatency();
            if (arr.Length == 0)
            {
                return 0;
            }

            Array.Sort(arr);
            return arr[arr.Length / 2];
        }
    }

    public double LatencyP95Ms
    {
        get
        {
            var arr = SnapshotLatency();
            if (arr.Length == 0)
            {
                return 0;
            }

            Array.Sort(arr);
            var idx = Math.Clamp((int)(arr.Length * 0.95), 0, arr.Length - 1);
            return arr[idx];
        }
    }

    public void RecordTps(double tps)
    {
        lock (_gate)
        {
            _tps.Enqueue(tps);
            while (_tps.Count > Capacity)
            {
                _tps.Dequeue();
            }
        }
    }

    public void RecordLatencyMs(long durationMs)
    {
        if (durationMs < 0)
        {
            return;
        }

        _recentLatencyMs.Enqueue(durationMs);
        while (_recentLatencyMs.Count > 500 && _recentLatencyMs.TryDequeue(out _))
        {
        }

        lock (_gate)
        {
            _latency.Enqueue(durationMs);
            while (_latency.Count > Capacity)
            {
                _latency.Dequeue();
            }
        }
    }

    private long[] SnapshotLatency() => _recentLatencyMs.ToArray();
}
