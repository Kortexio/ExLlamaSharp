using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Engine.Worker;

/// <summary>Owns the Python worker process and multiplexes jsonl-v2 stdin/stdout.</summary>
internal sealed class WorkerJsonlClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;
    private WorkerEngineOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Channel<WorkerEvent>> _streams = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _nextId = 1;
    private bool _disposed;
    private string? _startedCudaVisible;

    public WorkerJsonlClient(ILogger logger, WorkerEngineOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public WorkerEngineOptions Options
    {
        get => _options;
        set => _options = value ?? new WorkerEngineOptions();
    }

    public event Action<WorkerStats>? StatsReceived;

    public event Action<string, int>? LoadProgressReceived;

    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                return _process is not null && !_process.HasExited;
            }
        }
    }

    public int NextId() => Interlocked.Increment(ref _nextId);

    public Channel<WorkerEvent> OpenStream(int id)
    {
        var channel = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _streams[id] = channel;
        return channel;
    }

    public void CloseStream(int id)
    {
        if (_streams.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var desiredCuda = CudaDeviceEnvironment.Normalize(_options.CudaVisibleDevices, out _);
        lock (_gate)
        {
            if (_process is not null && !_process.HasExited)
            {
                if (string.Equals(_startedCudaVisible, desiredCuda, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        if (IsAlive)
        {
            _logger.LogInformation(
                "Recycling EXL3 worker (CUDA_VISIBLE_DEVICES {Old} -> {New})",
                _startedCudaVisible ?? "(none)",
                desiredCuda ?? "(default)");
            Stop();
        }

        var python = WorkerRuntimeLocator.ResolvePython(_options);
        var script = WorkerRuntimeLocator.ResolveWorkerScript(_options);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{script}\"",
            WorkingDirectory = Path.GetDirectoryName(script)
                ?? WorkerRuntimeLocator.FindRepoRoot()
                ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        WorkerRuntimeLocator.ConfigureProcessEnvironment(psi, python);
        CudaDeviceEnvironment.ApplyToProcess(psi, _options.CudaVisibleDevices, _logger);
        var cudaEnv = psi.Environment.TryGetValue("CUDA_VISIBLE_DEVICES", out var cudaVal) && !string.IsNullOrWhiteSpace(cudaVal)
            ? cudaVal
            : "(default)";
        _startedCudaVisible = desiredCuda;

        _logger.LogInformation(
            "Starting EXL3 worker: {Python} {Script} (CUDA_VISIBLE_DEVICES={Cuda})",
            python,
            script,
            cudaEnv);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException("Failed to start EXL3 Python worker.");
        }

        lock (_gate)
        {
            _process = proc;
            _stdin = proc.StandardInput;
            _stdout = proc.StandardOutput;
        }

        _ = Task.Run(() => DrainStderr(proc), CancellationToken.None);

        var readyLine = await proc.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            Stop();
            throw new InvalidOperationException("EXL3 worker produced no ready handshake.");
        }

        using var readyDoc = JsonDocument.Parse(readyLine);
        if (!readyDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            Stop();
            throw new InvalidOperationException($"EXL3 worker handshake failed: {readyLine}");
        }

        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    public async Task<JsonElement> SendControlAsync(object payload, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var id = NextId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
        {
            throw new InvalidOperationException("Failed to register worker control request.");
        }

        try
        {
            await WriteAsync(payload, id, cancellationToken).ConfigureAwait(false);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public async Task WriteAsync(object payload, int id, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

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

    public void Stop()
    {
        _startedCudaVisible = null;
        try
        {
            _pumpCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        FailAll(new InvalidOperationException("EXL3 worker stopped."));

        try
        {
            _stdin?.Dispose();
        }
        catch
        {
            // ignore
        }

        lock (_gate)
        {
            _stdin = null;
            _stdout = null;
        }

        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                proc.Dispose();
            }
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _writeLock.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        StreamReader? stdout;
        Process? proc;
        lock (_gate)
        {
            stdout = _stdout;
            proc = _process;
        }

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

                if (root.TryGetProperty("event", out var evName)
                    && string.Equals(evName.GetString(), "load_progress", StringComparison.OrdinalIgnoreCase))
                {
                    var phase = root.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "weights" : "weights";
                    var pct = 0;
                    if (root.TryGetProperty("progress_pct", out var pp) && pp.TryGetInt32(out var n))
                    {
                        pct = n;
                    }

                    LoadProgressReceived?.Invoke(phase, pct);
                    continue;
                }

                if (root.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
                {
                    if (root.TryGetProperty("stats", out var statsEl))
                    {
                        StatsReceived?.Invoke(WorkerStats.FromJson(statsEl));
                    }

                    foreach (var evEl in eventsEl.EnumerateArray())
                    {
                        DispatchEvent(WorkerEvent.FromJson(evEl));
                    }

                    continue;
                }

                if (WorkerEvent.TryReadId(root, out var id) && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root);
                    continue;
                }

                // Submit jobs open a stream channel for the same id. Worker _ok/_err control
                // replies are not wrapped in "events", so route failures onto the stream or the
                // HTTP client waits until timeout (408) instead of seeing prompt_too_long / etc.
                if (WorkerEvent.TryReadId(root, out id)
                    && _streams.ContainsKey(id)
                    && root.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    if (!okEl.GetBoolean())
                    {
                        var parsed = WorkerEvent.FromJson(root);
                        DispatchEvent(new WorkerEvent
                        {
                            Id = parsed.Id,
                            Ok = false,
                            Eos = true,
                            EosReason = "error",
                            Error = parsed.Error ?? "worker error",
                            PromptTokens = parsed.PromptTokens,
                            CompletionTokens = parsed.CompletionTokens,
                        });
                    }

                    continue;
                }

                if (root.TryGetProperty("stage", out _) && WorkerEvent.TryReadId(root, out _))
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
            FailAll(ex);
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

    private void FailAll(Exception ex)
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
}
