using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine.Worker;
using ExLlamaSharp.Tokenizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExLlamaSharp.Engine;

/// <summary>
/// Real EXL3 inference via a Python worker (official CUDA kernels).
/// Orchestrates admission, jsonl transport, and token streaming.
/// </summary>
public sealed class ExLlamaV3WorkerEngine : IInferenceEngine
{
    private readonly ILogger _logger;
    private readonly SimpleTokenizer _fallbackTokenizer = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _jobs = new();
    private readonly ConcurrentDictionary<Guid, int> _jobToWorkerId = new();
    private readonly WorkerAdmissionQueue _admission;
    private readonly WorkerJsonlClient _client;

    private WorkerEngineOptions _options;
    private bool _loaded;
    private bool _running;
    private bool _disposed;
    private string? _modelPath;
    private long _promptTokens;
    private long _generatedTokens;
    private long _finished;
    private double _lastTps;

    public ExLlamaV3WorkerEngine(ILogger? logger = null, WorkerEngineOptions? options = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new WorkerEngineOptions();
        _admission = new WorkerAdmissionQueue(() => Math.Max(1, _options.MaxNumSeqs));
        _client = new WorkerJsonlClient(_logger, _options);
        _client.StatsReceived += _admission.OnStats;
    }

    public WorkerEngineOptions Options
    {
        get => _options;
        set
        {
            _options = value ?? new WorkerEngineOptions();
            _client.Options = _options;
        }
    }

    public bool IsMock => false;
    public bool IsLoaded => _loaded;
    public bool IsRunning => _running;
    public bool SupportsStreaming => true;

    public static bool IsAvailable(string? repoRoot = null) =>
        WorkerRuntimeLocator.IsAvailable(repoRoot);

    public static bool LooksLikeExl3Directory(string path) =>
        Exl3ModelLayout.LooksLikeExl3Directory(path);

    public static bool TryResolvePython(out string python) =>
        WorkerRuntimeLocator.TryResolvePython(out python);

    public async Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        await _client.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var maxTokens = Math.Max(256, _options.MaxBatchedTokens);
        var maxSeqs = Math.Max(1, _options.MaxNumSeqs);
        var maxChunk = Math.Max(1, _options.MaxChunkSize);
        var resp = await _client.SendControlAsync(new
        {
            cmd = "load",
            path = Path.GetFullPath(modelPath),
            max_num_tokens = maxTokens,
            max_batch_size = maxSeqs,
            max_chunk_size = maxChunk,
        }, cancellationToken).ConfigureAwait(false);

        if (!resp.GetProperty("ok").GetBoolean())
        {
            var err = resp.TryGetProperty("error", out var e) ? e.GetString() : "load failed";
            throw new InvalidOperationException(err);
        }

        lock (_gate)
        {
            _modelPath = modelPath;
            _loaded = true;
        }

        _logger.LogInformation(
            "ExLlamaV3WorkerEngine loaded {Path} (max_num_tokens={MaxTokens} max_batch_size={MaxSeqs} max_chunk_size={MaxChunk})",
            modelPath,
            maxTokens,
            maxSeqs,
            maxChunk);
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_client.IsAlive)
        {
            _loaded = false;
            _modelPath = null;
            return;
        }

        try
        {
            await _client.SendControlAsync(new { cmd = "unload" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker unload failed");
        }

        _loaded = false;
        _modelPath = null;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded)
        {
            throw new InvalidOperationException("Load a model before Start().");
        }

        _running = true;
    }

    public void Stop() => _running = false;

    public async Task<CompletionResult> SubmitAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var jobId = request.JobId ?? Guid.NewGuid();
        var sb = new StringBuilder();
        var tokens = new List<int>();
        var filter = new StreamingStopFilter();
        CompletionDelta? last = null;

        await foreach (var delta in SubmitStreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var piece = filter.Push(delta.Text);
            sb.Append(piece);
            if (delta.TokenIds.Length > 0)
            {
                tokens.AddRange(delta.TokenIds);
            }

            last = delta;
            if (delta.Eos || delta.Failed || delta.Cancelled || filter.Stopped)
            {
                break;
            }
        }

        sb.Append(filter.Flush());
        return new CompletionResult
        {
            JobId = last?.JobId ?? jobId,
            Text = ChatTemplate.StripSpecialTokens(sb.ToString()),
            TokenIds = tokens.ToArray(),
            PromptTokens = last?.PromptTokens ?? 0,
            CompletionTokens = last?.CompletionTokens > 0 ? last.CompletionTokens : tokens.Count,
            Failed = last?.Failed == true,
            Error = last?.Error,
            Cancelled = last?.Cancelled == true || cancellationToken.IsCancellationRequested,
            Duration = DateTime.UtcNow - started,
        };
    }

    public async IAsyncEnumerable<CompletionDelta> SubmitStreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (!_loaded)
        {
            throw new InvalidOperationException("Model is not loaded.");
        }

        if (!_running)
        {
            throw new InvalidOperationException("Engine is not running. Call Start() first.");
        }

        var jobId = request.JobId ?? Guid.NewGuid();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _jobs[jobId] = cts;
        }

        var workerId = 0;
        var admitted = false;
        var filter = new StreamingStopFilter();
        try
        {
            await _admission.AdmitAsync(request.Priority, cts.Token).ConfigureAwait(false);
            admitted = true;

            workerId = _client.NextId();
            var channel = _client.OpenStream(workerId);
            _jobToWorkerId[jobId] = workerId;

            await _client.WriteAsync(WorkerSubmitPayload.FromRequest(request), workerId, cts.Token)
                .ConfigureAwait(false);

            var promptTokens = 0;
            var completionTokens = 0;
            await foreach (var ev in channel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                if (ev.PromptTokens > 0)
                {
                    promptTokens = ev.PromptTokens;
                }

                if (ev.CompletionTokens > 0)
                {
                    completionTokens = ev.CompletionTokens;
                }

                if (ev.TokensPerSecond > 0)
                {
                    _lastTps = ev.TokensPerSecond;
                }

                var piece = filter.Push(ev.Text);
                var eos = ev.Eos || !ev.Ok || filter.Stopped;
                if (eos)
                {
                    piece += filter.Flush();
                }

                if (piece.Length == 0 && !eos)
                {
                    continue;
                }

                yield return new CompletionDelta
                {
                    JobId = jobId,
                    Text = piece,
                    TokenIds = ev.TokenIds,
                    Eos = eos,
                    EosReason = ev.EosReason,
                    Stage = ev.Stage,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    Failed = !ev.Ok,
                    Error = ev.Error,
                    TokensPerSecond = ev.TokensPerSecond,
                };

                if (eos)
                {
                    Interlocked.Add(ref _promptTokens, promptTokens);
                    Interlocked.Add(ref _generatedTokens, completionTokens > 0 ? completionTokens : 0);
                    Interlocked.Increment(ref _finished);
                    yield break;
                }
            }
        }
        finally
        {
            if (workerId != 0)
            {
                _client.CloseStream(workerId);
                _jobToWorkerId.TryRemove(jobId, out _);
                try
                {
                    await _client.WriteAsync(new { cmd = "cancel" }, workerId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Worker cancel write failed for {JobId}", jobId);
                }
            }

            if (admitted)
            {
                _admission.Release();
            }

            lock (_gate)
            {
                _jobs.Remove(jobId);
            }

            cts.Dispose();
        }
    }

    public bool Cancel(Guid jobId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var cts))
            {
                return false;
            }

            cts.Cancel();
            if (_jobToWorkerId.TryGetValue(jobId, out var workerId))
            {
                _ = _client.WriteAsync(new { cmd = "cancel" }, workerId, CancellationToken.None);
            }

            return true;
        }
    }

    public EngineMetrics GetMetrics()
    {
        var stats = _admission.Stats;
        return new EngineMetrics
        {
            TotalPromptTokens = Interlocked.Read(ref _promptTokens),
            TotalGeneratedTokens = Interlocked.Read(ref _generatedTokens),
            NumJobsWaiting = _admission.WaitingCount,
            NumJobsRunning = stats.Active,
            NumJobsSwapped = stats.Pending,
            NumJobsFinished = Interlocked.Read(ref _finished),
            NumPagesFree = stats.FreePages,
            TokensPerSecond = _lastTps,
            IsMock = false,
        };
    }

    public int[] Tokenize(string text)
    {
        if (!_loaded || !_client.IsAlive)
        {
            return _fallbackTokenizer.Encode(text);
        }

        try
        {
            var resp = _client.SendControlAsync(new { cmd = "tokenize", text }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (resp.GetProperty("ok").GetBoolean())
            {
                return WorkerEvent.ReadIntArray(resp, "tokens");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker tokenize failed; using fallback");
        }

        return _fallbackTokenizer.Encode(text);
    }

    public string Detokenize(ReadOnlySpan<int> tokens)
    {
        if (!_loaded || !_client.IsAlive)
        {
            return _fallbackTokenizer.Decode(tokens);
        }

        try
        {
            var resp = _client.SendControlAsync(new { cmd = "detokenize", tokens = tokens.ToArray() }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (resp.GetProperty("ok").GetBoolean() && resp.TryGetProperty("text", out var t))
            {
                return t.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker detokenize failed; using fallback");
        }

        return _fallbackTokenizer.Decode(tokens);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _client.StatsReceived -= _admission.OnStats;
        _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
