namespace ExLlamaSharp.Engine.Worker;

/// <summary>
/// .NET-side admission: keep Python's pending queue near empty so local priority wins.
/// </summary>
internal sealed class WorkerAdmissionQueue
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Func<int> _maxSeqs;
    private long _seq;
    private int _inFlight;
    private WorkerStats _stats;

    public WorkerAdmissionQueue(Func<int> maxSeqs)
    {
        _maxSeqs = maxSeqs;
    }

    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    public WorkerStats Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    public async Task AdmitAsync(int priority, CancellationToken cancellationToken)
    {
        Waiter? waiter = null;
        lock (_gate)
        {
            if (CanAdmitLocked() && _waiters.Count == 0)
            {
                _inFlight++;
                return;
            }

            waiter = new Waiter
            {
                Priority = priority,
                Seq = Interlocked.Increment(ref _seq),
            };
            _waiters.Add(waiter);
            _waiters.Sort(static (a, b) =>
            {
                var c = b.Priority.CompareTo(a.Priority);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
        }

        using var reg = cancellationToken.Register(() => waiter.Slot.TrySetCanceled(cancellationToken));
        try
        {
            await waiter.Slot.Task.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }

            throw;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            TryAdmitOneLocked();
        }
    }

    public void OnStats(WorkerStats stats)
    {
        lock (_gate)
        {
            _stats = stats;
            TryAdmitOneLocked();
        }
    }

    private int CapacityLocked()
    {
        var cap = Math.Max(1, _maxSeqs());
        if (_stats.Seen && _stats.MaxBatchSize > 0)
        {
            cap = Math.Min(cap, _stats.MaxBatchSize);
        }

        return cap;
    }

    private bool CanAdmitLocked()
    {
        if (_inFlight >= CapacityLocked())
        {
            return false;
        }

        if (!_stats.Seen)
        {
            return _inFlight < 1;
        }

        return _stats.Pending <= 1;
    }

    private void TryAdmitOneLocked()
    {
        if (_waiters.Count == 0 || !CanAdmitLocked())
        {
            return;
        }

        var next = _waiters[0];
        _waiters.RemoveAt(0);
        _inFlight++;
        if (!next.Slot.TrySetResult())
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            TryAdmitOneLocked();
        }
    }

    private sealed class Waiter
    {
        public int Priority { get; init; }
        public long Seq { get; init; }
        public TaskCompletionSource Slot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
