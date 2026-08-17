using System.Runtime.InteropServices;
using System.Text;
using ExLlamaSharp.Native;
using ExLlamaSharp.Tokenizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static ExLlamaSharp.Native.NativeMethods;

namespace ExLlamaSharp.Engine;

/// <summary>
/// Managed wrapper over <c>exllamasharp.dll</c>. Falls back to <see cref="MockEngine"/>
/// when the native library cannot be loaded (typical on Windows without CUDA builds).
/// </summary>
public sealed class ExLlamaEngine : IInferenceEngine
{
    private readonly ILogger _logger;
    private readonly SimpleTokenizer _fallbackTokenizer = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ExLlamaJobHandle> _jobs = new();

    private ExLlamaEngineHandle? _handle;
    private MockEngine? _mock;
    private bool _loaded;
    private bool _running;
    private bool _disposed;
    private bool _usingMock;

    private ExLlamaEngine(ILogger logger, bool forceMock)
    {
        _logger = logger;

        string? error = null;
        if (forceMock || !TryCreateNative(out _handle, out error))
        {
            _usingMock = true;
            _mock = new MockEngine(logger);
            _logger.LogWarning(
                "Using MockEngine fallback. Native load reason: {Reason}",
                forceMock ? "forced" : error ?? "unknown");
        }
    }

    /// <summary>
    /// Create an engine. Tries native DLL first; uses mock when unavailable.
    /// </summary>
    public static ExLlamaEngine Create(ILogger? logger = null, bool forceMock = false)
        => new(logger ?? NullLogger.Instance, forceMock);

    public bool IsMock => _usingMock;
    public bool IsLoaded => _usingMock ? _mock!.IsLoaded : _loaded;
    public bool IsRunning => _usingMock ? _mock!.IsRunning : _running;

    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (_usingMock)
        {
            return _mock!.LoadAsync(modelPath, cancellationToken);
        }

        ThrowIfNativeFailed(EngineLoad(_handle!.DangerousGetHandle(), modelPath), nameof(EngineLoad));
        _loaded = true;
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_usingMock)
        {
            return _mock!.UnloadAsync(cancellationToken);
        }

        if (_running)
        {
            Stop();
        }

        ThrowIfNativeFailed(EngineUnload(_handle!.DangerousGetHandle()), nameof(EngineUnload));
        _loaded = false;
        return Task.CompletedTask;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_usingMock)
        {
            _mock!.Start();
            return;
        }

        if (!_loaded)
        {
            throw new InvalidOperationException("Load a model before Start().");
        }

        ThrowIfNativeFailed(EngineStart(_handle!.DangerousGetHandle()), nameof(EngineStart));
        _running = true;
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        if (_usingMock)
        {
            _mock!.Stop();
            return;
        }

        if (!_running || _handle is null || _handle.IsInvalid)
        {
            _running = false;
            return;
        }

        var status = EngineStop(_handle.DangerousGetHandle());
        _running = false;
        if (status != ExlStatus.Ok)
        {
            _logger.LogWarning("exl_engine_stop returned {Status}: {Error}", status, LastError());
        }
    }

    public async Task<CompletionResult> SubmitAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (_usingMock)
        {
            return await _mock!.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!_loaded)
        {
            throw new InvalidOperationException("Model is not loaded.");
        }

        if (!_running)
        {
            throw new InvalidOperationException("Engine is not running. Call Start() first.");
        }

        var jobId = request.JobId ?? Guid.NewGuid();
        var promptTokens = request.PromptTokens ?? Tokenize(request.Prompt);
        var started = DateTime.UtcNow;
        var jobHandle = SubmitNativeJob(request, promptTokens);

        lock (_gate)
        {
            _jobs[jobId] = jobHandle;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = JobState(jobHandle.DangerousGetHandle());
                if (state is ExlJobState.Finished or ExlJobState.Cancelled or ExlJobState.Failed)
                {
                    var tokens = ReadJobTokens(jobHandle);
                    var text = tokens.Length > 0 ? Detokenize(tokens) : string.Empty;
                    var cancelled = state == ExlJobState.Cancelled || cancellationToken.IsCancellationRequested;
                    var failed = state == ExlJobState.Failed;

                    return new CompletionResult
                    {
                        JobId = jobId,
                        Text = text,
                        TokenIds = tokens,
                        PromptTokens = promptTokens.Length,
                        CompletionTokens = tokens.Length,
                        Cancelled = cancelled,
                        Failed = failed,
                        Error = failed ? LastError() : null,
                        Duration = DateTime.UtcNow - started,
                    };
                }

                await Task.Delay(8, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Cancel(jobId);
            return new CompletionResult
            {
                JobId = jobId,
                Text = string.Empty,
                TokenIds = [],
                PromptTokens = promptTokens.Length,
                CompletionTokens = 0,
                Cancelled = true,
                Duration = DateTime.UtcNow - started,
            };
        }
        finally
        {
            lock (_gate)
            {
                _jobs.Remove(jobId);
            }

            jobHandle.Dispose();
        }
    }

    public bool Cancel(Guid jobId)
    {
        if (_usingMock)
        {
            return _mock!.Cancel(jobId);
        }

        ExLlamaJobHandle? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job))
            {
                return false;
            }
        }

        var status = JobCancel(_handle!.DangerousGetHandle(), job.DangerousGetHandle());
        return status == ExlStatus.Ok;
    }

    public EngineMetrics GetMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_usingMock)
        {
            return _mock!.GetMetrics();
        }

        ThrowIfNativeFailed(
            NativeMethods.EngineMetrics(_handle!.DangerousGetHandle(), out var m),
            "exl_engine_metrics");

        return new EngineMetrics
        {
            TotalPromptTokens = m.TotalPromptTokens,
            TotalGeneratedTokens = m.TotalGeneratedTokens,
            NumJobsWaiting = m.NumJobsWaiting,
            NumJobsRunning = m.NumJobsRunning,
            NumJobsSwapped = m.NumJobsSwapped,
            NumJobsFinished = m.NumJobsFinished,
            NumPagesUsed = m.NumPagesUsed,
            NumPagesFree = m.NumPagesFree,
            TokensPerSecond = m.TokensPerSecond,
            LastStepMs = m.LastStepMs,
            StepCount = m.StepCount,
            IsMock = false,
        };
    }

    public int[] Tokenize(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        if (_usingMock || !_loaded)
        {
            return _fallbackTokenizer.Encode(text);
        }

        var capacity = Math.Max(64, text.Length * 2 + 16);
        var buffer = new int[capacity];

        unsafe
        {
            fixed (int* p = buffer)
            {
                var count = capacity;
                var status = NativeMethods.Tokenize(_handle!.DangerousGetHandle(), text, p, ref count);
                if (status == ExlStatus.ErrInvalidArg && count > capacity)
                {
                    buffer = new int[count];
                    fixed (int* p2 = buffer)
                    {
                        status = NativeMethods.Tokenize(_handle.DangerousGetHandle(), text, p2, ref count);
                    }
                }

                ThrowIfNativeFailed(status, nameof(NativeMethods.Tokenize));
                return buffer.AsSpan(0, count).ToArray();
            }
        }
    }

    public string Detokenize(ReadOnlySpan<int> tokens)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tokens.IsEmpty)
        {
            return string.Empty;
        }

        if (_usingMock || !_loaded)
        {
            return _fallbackTokenizer.Decode(tokens);
        }

        var nbytes = Math.Max(256, tokens.Length * 8 + 32);
        var utf8 = new byte[nbytes];

        unsafe
        {
            fixed (int* pTokens = tokens)
            fixed (byte* pOut = utf8)
            {
                var n = nbytes;
                var status = NativeMethods.Detokenize(
                    _handle!.DangerousGetHandle(),
                    pTokens,
                    tokens.Length,
                    pOut,
                    ref n);

                if (status == ExlStatus.ErrInvalidArg && n > nbytes)
                {
                    utf8 = new byte[n];
                    fixed (byte* pOut2 = utf8)
                    {
                        status = NativeMethods.Detokenize(
                            _handle.DangerousGetHandle(),
                            pTokens,
                            tokens.Length,
                            pOut2,
                            ref n);
                    }
                }

                ThrowIfNativeFailed(status, nameof(NativeMethods.Detokenize));
                return Encoding.UTF8.GetString(utf8, 0, n);
            }
        }
    }

    /// <summary>Drive a single scheduler step when the background thread is not running.</summary>
    public void Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_usingMock)
        {
            return;
        }

        ThrowIfNativeFailed(EngineStep(_handle!.DangerousGetHandle()), nameof(EngineStep));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch
        {
            // best-effort
        }

        lock (_gate)
        {
            foreach (var job in _jobs.Values)
            {
                job.Dispose();
            }

            _jobs.Clear();
        }

        _handle?.Dispose();
        _handle = null;
        _mock?.Dispose();
        _mock = null;
        _loaded = false;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private ExLlamaJobHandle SubmitNativeJob(CompletionRequest request, int[] promptTokens)
    {
        unsafe
        {
            fixed (int* pTokens = promptTokens)
            {
                var parameters = new ExlJobParams
                {
                    PromptTokens = pTokens,
                    PromptLength = promptTokens.Length,
                    MaxNewTokens = request.MaxNewTokens,
                    Temperature = request.Temperature,
                    TopP = request.TopP,
                    TopK = request.TopK,
                    Priority = request.Priority,
                    StopTokenId = request.StopTokenId,
                    UserData = null,
                };

                ThrowIfNativeFailed(
                    JobSubmit(_handle!.DangerousGetHandle(), &parameters, out var jobPtr),
                    nameof(JobSubmit));

                return new ExLlamaJobHandle(jobPtr);
            }
        }
    }

    private static int[] ReadJobTokens(ExLlamaJobHandle job)
    {
        var capacity = 1024;
        var buffer = new int[capacity];

        unsafe
        {
            fixed (int* p = buffer)
            {
                var count = capacity;
                var status = JobTokens(job.DangerousGetHandle(), p, ref count);
                if (status == ExlStatus.ErrInvalidArg && count > capacity)
                {
                    buffer = new int[count];
                    fixed (int* p2 = buffer)
                    {
                        status = JobTokens(job.DangerousGetHandle(), p2, ref count);
                    }
                }

                if (status != ExlStatus.Ok)
                {
                    return [];
                }

                return buffer.AsSpan(0, count).ToArray();
            }
        }
    }

    private static bool TryCreateNative(out ExLlamaEngineHandle? handle, out string? error)
    {
        handle = null;
        error = null;

        try
        {
            var cfg = new ExlEngineConfig();
            EngineConfigInit(ref cfg);

            unsafe
            {
                ExlEngineConfig local = cfg;
                var ptr = EngineCreate(&local);
                if (ptr == nint.Zero)
                {
                    error = SafeLastError() ?? "exl_engine_create returned null";
                    return false;
                }

                handle = new ExLlamaEngineHandle(ptr, ownsHandle: true);
                return true;
            }
        }
        catch (DllNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? SafeLastError()
    {
        try
        {
            return LastError();
        }
        catch
        {
            return null;
        }
    }

    private static void ThrowIfNativeFailed(ExlStatus status, string api)
    {
        if (status == ExlStatus.Ok)
        {
            return;
        }

        var msg = SafeLastError() ?? status.ToString();
        throw new InvalidOperationException($"{api} failed ({status}): {msg}");
    }
}
