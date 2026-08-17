using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Tokenizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExLlamaSharp.Engine;

/// <summary>
/// Real EXL3 inference via a Python worker that loads local <c>third_party/exllamav3</c>
/// (official CUDA kernels). Not a mock.
/// </summary>
public sealed class ExLlamaV3WorkerEngine : IInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;
    private readonly SimpleTokenizer _fallbackTokenizer = new();
    private readonly object _gate = new();
    private readonly object _admitGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Channel<WorkerEvent>> _streams = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<Guid, int> _jobToWorkerId = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _jobs = new();
    private readonly List<AdmissionWaiter> _waiters = [];

    private WorkerEngineOptions _options;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _nextId = 1;
    private long _admitSeq;
    private int _inFlight;
    private WorkerStats _stats;
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
    }

    public WorkerEngineOptions Options
    {
        get => _options;
        set => _options = value ?? new WorkerEngineOptions();
    }

    public bool IsMock => false;
    public bool IsLoaded => _loaded;
    public bool IsRunning => _running;
    public bool SupportsStreaming => true;

    /// <summary>True when a suitable Python + worker script can be located.</summary>
    public static bool IsAvailable(string? repoRoot = null)
    {
        try
        {
            return TryResolvePython(out _) && TryResolveWorkerScript(repoRoot, out _);
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksLikeExl3Directory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var configPath = Path.Combine(path, "config.json");
        var hasSafetensors = Directory.EnumerateFiles(path, "*.safetensors").Any();
        var hasTokenizer = File.Exists(Path.Combine(path, "tokenizer.json"));

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                if (json.Contains("exl3", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("\"quant_method\"", StringComparison.OrdinalIgnoreCase) &&
                    json.Contains("exl3", StringComparison.OrdinalIgnoreCase))
                {
                    return hasSafetensors || hasTokenizer;
                }

                using var doc = JsonDocument.Parse(json);
                if (ContainsExl3(doc.RootElement))
                {
                    return true;
                }
            }
            catch
            {
                // fall through
            }
        }

        return hasSafetensors && hasTokenizer && File.Exists(configPath);
    }

    private static bool ContainsExl3(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name.Contains("quant", StringComparison.OrdinalIgnoreCase) &&
                        p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }

                    if (ContainsExl3(p.Value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray())
                {
                    if (ContainsExl3(i))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                return el.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

    public async Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

        var maxTokens = Math.Max(256, _options.MaxBatchedTokens);
        var maxSeqs = Math.Max(1, _options.MaxNumSeqs);
        var maxChunk = Math.Max(1, _options.MaxChunkSize);
        var resp = await SendControlAsync(new
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

        if (_process is null || _process.HasExited)
        {
            _loaded = false;
            _modelPath = null;
            return;
        }

        try
        {
            await SendControlAsync(new { cmd = "unload" }, cancellationToken).ConfigureAwait(false);
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

    public void Stop()
    {
        _running = false;
    }

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
        var text = ChatTemplate.StripSpecialTokens(sb.ToString());
        return new CompletionResult
        {
            JobId = last?.JobId ?? jobId,
            Text = text,
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
        Channel<WorkerEvent>? channel = null;
        var filter = new StreamingStopFilter();
        try
        {
            await AdmitAsync(request.Priority, cts.Token).ConfigureAwait(false);
            admitted = true;

            workerId = Interlocked.Increment(ref _nextId);
            channel = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            _streams[workerId] = channel;
            _jobToWorkerId[jobId] = workerId;

            var stops = BuildStopList(request);
            object payload = request.Messages is { Count: > 0 }
                ? new
                {
                    cmd = "submit",
                    messages = request.Messages.Select(m => new
                    {
                        role = RoleWire(m.Role),
                        content = m.Content ?? "",
                    }),
                    max_new_tokens = request.MaxNewTokens,
                    temperature = request.Temperature,
                    top_p = request.TopP,
                    top_k = request.TopK,
                    stop = stops,
                }
                : new
                {
                    cmd = "submit",
                    prompt = request.Prompt,
                    max_new_tokens = request.MaxNewTokens,
                    temperature = request.Temperature,
                    top_p = request.TopP,
                    top_k = request.TopK,
                    stop = stops,
                };

            await WriteLineAsync(payload, workerId, cts.Token).ConfigureAwait(false);

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
            if (channel is not null)
            {
                _streams.TryRemove(workerId, out _);
                channel.Writer.TryComplete();
            }

            _jobToWorkerId.TryRemove(jobId, out _);

            if (workerId != 0)
            {
                try
                {
                    await WriteLineAsync(new { cmd = "cancel" }, workerId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Worker cancel write failed for {JobId}", jobId);
                }
            }

            if (admitted)
            {
                ReleaseSlot();
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
            if (_jobs.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                if (_jobToWorkerId.TryGetValue(jobId, out var workerId))
                {
                    _ = WriteLineAsync(new { cmd = "cancel" }, workerId, CancellationToken.None);
                }

                return true;
            }
        }

        return false;
    }

    public EngineMetrics GetMetrics()
    {
        int waiting;
        lock (_admitGate)
        {
            waiting = _waiters.Count;
        }

        return new EngineMetrics
        {
            TotalPromptTokens = Interlocked.Read(ref _promptTokens),
            TotalGeneratedTokens = Interlocked.Read(ref _generatedTokens),
            NumJobsWaiting = waiting,
            NumJobsRunning = _stats.Active,
            NumJobsSwapped = _stats.Pending,
            NumJobsFinished = Interlocked.Read(ref _finished),
            NumPagesFree = _stats.FreePages,
            TokensPerSecond = _lastTps,
            IsMock = false,
        };
    }

    public int[] Tokenize(string text)
    {
        if (!_loaded || _process is null || _process.HasExited)
        {
            return _fallbackTokenizer.Encode(text);
        }

        try
        {
            var resp = SendControlAsync(new { cmd = "tokenize", text }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (resp.GetProperty("ok").GetBoolean())
            {
                return ReadIntArray(resp, "tokens");
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
        if (!_loaded || _process is null || _process.HasExited)
        {
            return _fallbackTokenizer.Decode(tokens);
        }

        try
        {
            var arr = tokens.ToArray();
            var resp = SendControlAsync(new { cmd = "detokenize", tokens = arr }, CancellationToken.None)
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
        KillWorker();
        _writeLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AdmitAsync(int priority, CancellationToken cancellationToken)
    {
        AdmissionWaiter? waiter = null;
        lock (_admitGate)
        {
            if (CanAdmitLocked() && _waiters.Count == 0)
            {
                _inFlight++;
                return;
            }

            waiter = new AdmissionWaiter
            {
                Priority = priority,
                Seq = Interlocked.Increment(ref _admitSeq),
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
            lock (_admitGate)
            {
                _waiters.Remove(waiter);
            }

            throw;
        }
    }

    private void ReleaseSlot()
    {
        lock (_admitGate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            TryAdmitOneLocked();
        }
    }

    private void OnStats(WorkerStats stats)
    {
        lock (_admitGate)
        {
            _stats = stats;
            TryAdmitOneLocked();
        }
    }

    private int CapacityLocked()
    {
        var cap = Math.Max(1, _options.MaxNumSeqs);
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

        // Before the first stats packet, pipeline a single job so we do not dump
        // the whole .NET queue into Python FIFO pending.
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

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        string python;
        if (!string.IsNullOrWhiteSpace(_options.PythonPath) && File.Exists(_options.PythonPath))
        {
            python = _options.PythonPath;
        }
        else if (!TryResolvePython(out python!))
        {
            throw new InvalidOperationException(
                "Python not found. Run packaging/Setup-Exl3Python.ps1 or set EXLLAMASHARP_PYTHON.");
        }

        string script;
        if (!string.IsNullOrWhiteSpace(_options.WorkerScript) && File.Exists(_options.WorkerScript))
        {
            script = _options.WorkerScript;
        }
        else if (!TryResolveWorkerScript(null, out script!))
        {
            throw new InvalidOperationException("tools/exl3_worker/worker.py not found.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{script}\"",
            WorkingDirectory = FindRepoRoot() ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["EXL3_BC_DSA"] = "0";
        PrependNativeSearchPath(psi, python);
        TryAddDonorExtPath(psi, python);

        _logger.LogInformation("Starting EXL3 worker: {Python} {Script}", python, script);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException("Failed to start EXL3 Python worker.");
        }

        _process = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;

        _ = Task.Run(() => DrainStderr(proc), CancellationToken.None);

        var readyLine = await _stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            KillWorker();
            throw new InvalidOperationException("EXL3 worker produced no ready handshake.");
        }

        using var readyDoc = JsonDocument.Parse(readyLine);
        if (!readyDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            KillWorker();
            throw new InvalidOperationException($"EXL3 worker handshake failed: {readyLine}");
        }

        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var stdout = _stdout;
        var proc = _process;
        if (stdout is null || proc is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (proc.HasExited)
                {
                    throw new InvalidOperationException($"EXL3 worker exited with code {proc.ExitCode}.");
                }

                var line = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException("EXL3 worker closed stdout.");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Ignoring non-JSON worker line");
                    continue;
                }

                if (root.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
                {
                    if (root.TryGetProperty("stats", out var statsEl))
                    {
                        OnStats(WorkerStats.FromJson(statsEl));
                    }

                    foreach (var evEl in eventsEl.EnumerateArray())
                    {
                        DispatchEvent(WorkerEvent.FromJson(evEl));
                    }

                    continue;
                }

                if (TryReadId(root, out var id) && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root);
                    continue;
                }

                if (root.TryGetProperty("stage", out _) && TryReadId(root, out _))
                {
                    DispatchEvent(WorkerEvent.FromJson(root));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EXL3 worker pump failed");
            FailAllWaiters(ex);
        }
    }

    private void DispatchEvent(WorkerEvent ev)
    {
        if (!_streams.TryGetValue(ev.Id, out var ch))
        {
            return;
        }

        ch.Writer.TryWrite(ev);
        if (ev.Eos || !ev.Ok)
        {
            ch.Writer.TryComplete();
            _streams.TryRemove(ev.Id, out _);
        }
    }

    private void FailAllWaiters(Exception ex)
    {
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(ex);
        }

        _pending.Clear();
        foreach (var kv in _streams)
        {
            kv.Value.Writer.TryComplete(ex);
        }

        _streams.Clear();
    }

    private void DrainStderr(Process proc)
    {
        try
        {
            while (!proc.HasExited)
            {
                var line = proc.StandardError.ReadLine();
                if (line is null)
                {
                    break;
                }

                _logger.LogInformation("[exl3_worker] {Line}", line);
            }
        }
        catch
        {
            // process exited
        }
    }

    private async Task<JsonElement> SendControlAsync(object payload, CancellationToken cancellationToken)
    {
        await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
        {
            throw new InvalidOperationException("Failed to register worker control request.");
        }

        try
        {
            await WriteLineAsync(payload, id, cancellationToken).ConfigureAwait(false);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task WriteLineAsync(object payload, int id, CancellationToken cancellationToken)
    {
        await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        node["id"] = id;
        var line = node.ToJsonString();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter stdin;
            lock (_gate)
            {
                if (_process is null || _stdin is null || _process.HasExited)
                {
                    throw new InvalidOperationException("EXL3 worker is not running.");
                }

                stdin = _stdin;
            }

            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void KillWorker()
    {
        try
        {
            _pumpCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        FailAllWaiters(new InvalidOperationException("EXL3 worker stopped."));

        try
        {
            _stdin?.Dispose();
        }
        catch
        {
            // ignore
        }

        _stdin = null;
        _stdout = null;

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        try
        {
            _pumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;
    }

    private static bool TryReadId(JsonElement root, out int id)
    {
        id = 0;
        if (!root.TryGetProperty("id", out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out id))
        {
            return true;
        }

        return el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out id);
    }

    private static int[] ReadIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<int>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            {
                list.Add(v);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Windows Service (LocalSystem) has a minimal PATH. Prepend venv Scripts
    /// (ninja) and torch/lib (cublas/cudart) so <c>exllamav3_ext.pyd</c> can load.
    /// </summary>
    private static void PrependNativeSearchPath(ProcessStartInfo psi, string python)
    {
        var extras = new List<string>();
        try
        {
            var scripts = Path.GetDirectoryName(python);
            if (!string.IsNullOrWhiteSpace(scripts) && Directory.Exists(scripts))
            {
                extras.Add(scripts);
                var venv = Path.GetDirectoryName(scripts);
                if (!string.IsNullOrWhiteSpace(venv))
                {
                    var torchLib = Path.Combine(venv, "Lib", "site-packages", "torch", "lib");
                    if (Directory.Exists(torchLib))
                    {
                        extras.Add(torchLib);
                    }
                }
            }

            var cuda = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrWhiteSpace(cuda))
            {
                var cudaBin = Path.Combine(cuda, "bin");
                if (Directory.Exists(cudaBin))
                {
                    extras.Add(cudaBin);
                }
            }
        }
        catch
        {
            // best-effort
        }

        if (extras.Count == 0)
        {
            return;
        }

        var current = "";
        if (psi.Environment.TryGetValue("Path", out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            current = existing;
        }
        else
        {
            current = Environment.GetEnvironmentVariable("Path") ?? "";
        }

        psi.Environment["Path"] = string.Join(";", extras.Concat(new[] { current }).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// If the product venv has the PyPI source package (no <c>exllamav3_ext.pyd</c>),
    /// put a donor site-packages (repo <c>.venv-exl3</c>) on PYTHONPATH so the
    /// prebuilt CUDA extension can be imported.
    /// </summary>
    private static void TryAddDonorExtPath(ProcessStartInfo psi, string python)
    {
        try
        {
            var scripts = Path.GetDirectoryName(python);
            var venv = scripts is null ? null : Path.GetDirectoryName(scripts);
            if (string.IsNullOrWhiteSpace(venv))
            {
                return;
            }

            var localPyd = Path.Combine(venv, "Lib", "site-packages", "exllamav3_ext.cp312-win_amd64.pyd");
            if (File.Exists(localPyd))
            {
                return;
            }

            var donors = new List<string>();
            var repo = FindRepoRoot();
            if (repo is not null)
            {
                donors.Add(Path.Combine(repo, ".venv-exl3", "Lib", "site-packages"));
            }

            var programDataDonor = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp", "exl3-ext-donor.txt");
            if (File.Exists(programDataDonor))
            {
                var line = File.ReadAllText(programDataDonor).Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    donors.Add(line);
                }
            }

            foreach (var site in donors.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var donorPyd = Path.Combine(site, "exllamav3_ext.cp312-win_amd64.pyd");
                if (!File.Exists(donorPyd))
                {
                    continue;
                }

                psi.Environment.TryGetValue("PYTHONPATH", out var existing);
                psi.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                    ? site
                    : site + Path.PathSeparator + existing;
                return;
            }
        }
        catch
        {
            // best-effort
        }
    }

    public static bool TryResolvePython(out string python)
    {
        python = "";
        var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            python = env;
            return true;
        }

        foreach (var cfg in EnumerateRuntimeConfigPaths())
        {
            if (TryReadPythonFromRuntimeConfig(cfg, out python))
            {
                return true;
            }
        }

        var candidates = new List<string>();
        foreach (var dir in EnumerateAppDirs())
        {
            candidates.Add(Path.Combine(dir, "venv", "Scripts", "python.exe"));
        }

        var root = FindRepoRoot();
        if (root is not null)
        {
            candidates.Add(Path.Combine(root, ".venv-exl3", "Scripts", "python.exe"));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp", "venv", "Scripts", "python.exe"));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
            {
                python = c;
                return true;
            }
        }

        foreach (var name in new[] { "py", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = name == "py" ? "-3 -c \"import sys; print(sys.executable)\"" : "-c \"import sys; print(sys.executable)\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }

                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (p.ExitCode == 0 && File.Exists(output))
                {
                    python = output;
                    return true;
                }
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    internal static bool TryResolveWorkerScript(string? repoRoot, out string script)
    {
        script = "";
        var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_WORKER_SCRIPT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            script = env;
            return true;
        }

        var root = repoRoot ?? FindRepoRoot();
        if (root is not null)
        {
            var path = Path.Combine(root, "tools", "exl3_worker", "worker.py");
            if (File.Exists(path))
            {
                script = path;
                return true;
            }
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "exl3_worker", "worker.py");
        if (File.Exists(beside))
        {
            script = beside;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateAppDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(d);
                seen.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        Add(AppContext.BaseDirectory);
        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(proc))
            {
                Add(Path.GetDirectoryName(proc));
            }
        }
        catch
        {
            // ignore
        }

        return seen;
    }

    private static IEnumerable<string> EnumerateRuntimeConfigPaths()
    {
        foreach (var dir in EnumerateAppDirs())
        {
            yield return Path.Combine(dir, "exl3-runtime.json");
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp", "exl3-runtime.json");
    }

    private static bool TryReadPythonFromRuntimeConfig(string cfgPath, out string python)
    {
        python = "";
        try
        {
            if (!File.Exists(cfgPath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (doc.RootElement.TryGetProperty("python", out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                var path = p.GetString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    python = path;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                var dir = new DirectoryInfo(start);
                for (var i = 0; i < 8 && dir is not null; i++)
                {
                    var marker = Path.Combine(dir.FullName, "tools", "exl3_worker", "worker.py");
                    var exl3 = Path.Combine(dir.FullName, "third_party", "exllamav3");
                    if (File.Exists(marker) || Directory.Exists(exl3))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string RoleWire(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    private static List<object> BuildStopList(CompletionRequest request)
    {
        var stops = new List<object>();
        if (request.StopTokenId >= 0)
        {
            stops.Add(request.StopTokenId);
        }

        foreach (var s in ChatTemplate.DefaultStopStrings)
        {
            stops.Add(s);
        }

        if (request.StopStrings is { Count: > 0 })
        {
            foreach (var s in request.StopStrings)
            {
                if (!string.IsNullOrEmpty(s))
                {
                    stops.Add(s);
                }
            }
        }

        return stops;
    }

    private sealed class AdmissionWaiter
    {
        public int Priority { get; init; }
        public long Seq { get; init; }
        public TaskCompletionSource Slot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly struct WorkerStats
    {
        public bool Seen { get; init; }
        public int Active { get; init; }
        public int Pending { get; init; }
        public int FreePages { get; init; }
        public int MaxBatchSize { get; init; }

        public static WorkerStats FromJson(JsonElement el)
        {
            return new WorkerStats
            {
                Seen = true,
                Active = ReadInt(el, "active"),
                Pending = ReadInt(el, "pending"),
                FreePages = ReadInt(el, "free_pages"),
                MaxBatchSize = ReadInt(el, "max_batch_size"),
            };
        }

        private static int ReadInt(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
            {
                return v;
            }

            return 0;
        }
    }

    private sealed class WorkerEvent
    {
        public int Id { get; init; }
        public string Stage { get; init; } = "";
        public string Text { get; init; } = "";
        public int[] TokenIds { get; init; } = [];
        public bool Eos { get; init; }
        public bool Ok { get; init; } = true;
        public string? EosReason { get; init; }
        public string? Error { get; init; }
        public int PromptTokens { get; init; }
        public int CompletionTokens { get; init; }
        public double TokensPerSecond { get; init; }

        public static WorkerEvent FromJson(JsonElement el)
        {
            TryReadId(el, out var id);
            var ok = true;
            if (el.TryGetProperty("ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                ok = okEl.GetBoolean();
            }

            var completion = 0;
            if (el.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv))
            {
                completion = ctv;
            }
            else if (el.TryGetProperty("new_tokens", out var nt) && nt.TryGetInt32(out var ntv))
            {
                completion = ntv;
            }

            return new WorkerEvent
            {
                Id = id,
                Stage = el.TryGetProperty("stage", out var st) ? st.GetString() ?? "" : "",
                Text = el.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
                TokenIds = ReadIntArray(el, "token_ids"),
                Eos = el.TryGetProperty("eos", out var eos) && eos.ValueKind == JsonValueKind.True,
                Ok = ok,
                EosReason = el.TryGetProperty("eos_reason", out var er) ? er.GetString() : null,
                Error = el.TryGetProperty("error", out var err) ? err.GetString() : null,
                PromptTokens = el.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : 0,
                CompletionTokens = completion,
                TokensPerSecond = el.TryGetProperty("tokens_per_second", out var tps) && tps.ValueKind == JsonValueKind.Number
                    ? tps.GetDouble()
                    : 0,
            };
        }
    }
}
