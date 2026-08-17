using System.Security.Cryptography;
using System.Text;
using ExLlamaSharp.Tokenizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExLlamaSharp.Engine;

/// <summary>
/// Deterministic mock engine for Windows hosts without CUDA / native DLL.
/// Generates a plausible token stream derived from the prompt hash.
/// </summary>
public sealed class MockEngine : IInferenceEngine
{
    private readonly ILogger _logger;
    private readonly SimpleTokenizer _tokenizer = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _jobs = new();

    private long _promptTokens;
    private long _generatedTokens;
    private long _finished;
    private long _stepCount;
    private string? _modelPath;
    private bool _running;
    private bool _disposed;

    public MockEngine(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsMock => true;
    public bool IsLoaded => _modelPath is not null;
    public bool IsRunning => _running;

    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        lock (_gate)
        {
            _modelPath = modelPath;
        }

        _logger.LogInformation("MockEngine loaded path {ModelPath}", modelPath);
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            _modelPath = null;
        }

        return Task.CompletedTask;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (!IsLoaded)
        {
            throw new InvalidOperationException("Model is not loaded.");
        }

        if (!_running)
        {
            throw new InvalidOperationException("Engine is not running. Call Start() first.");
        }

        var jobId = request.JobId ?? Guid.NewGuid();
        var promptTokens = request.PromptTokens ?? _tokenizer.Encode(request.Prompt);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_gate)
        {
            _jobs[jobId] = cts;
            _promptTokens += promptTokens.Length;
        }

        var started = DateTime.UtcNow;
        try
        {
            var (text, tokenIds) = await GenerateAsync(
                request.Prompt,
                promptTokens,
                request.MaxNewTokens,
                cts.Token).ConfigureAwait(false);

            Interlocked.Add(ref _generatedTokens, tokenIds.Length);
            Interlocked.Increment(ref _finished);
            Interlocked.Increment(ref _stepCount);

            return new CompletionResult
            {
                JobId = jobId,
                Text = text,
                TokenIds = tokenIds,
                PromptTokens = promptTokens.Length,
                CompletionTokens = tokenIds.Length,
                Duration = DateTime.UtcNow - started,
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
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
                return true;
            }
        }

        return false;
    }

    public EngineMetrics GetMetrics()
    {
        int waiting;
        lock (_gate)
        {
            waiting = _jobs.Count;
        }

        return new EngineMetrics
        {
            TotalPromptTokens = Interlocked.Read(ref _promptTokens),
            TotalGeneratedTokens = Interlocked.Read(ref _generatedTokens),
            NumJobsWaiting = waiting,
            NumJobsRunning = waiting > 0 ? 1 : 0,
            NumJobsFinished = Interlocked.Read(ref _finished),
            NumPagesUsed = 0,
            NumPagesFree = 0,
            TokensPerSecond = 80.0,
            LastStepMs = 1.0,
            StepCount = Interlocked.Read(ref _stepCount),
            IsMock = true,
        };
    }

    public int[] Tokenize(string text) => _tokenizer.Encode(text);

    public string Detokenize(ReadOnlySpan<int> tokens) => _tokenizer.Decode(tokens);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        lock (_gate)
        {
            foreach (var cts in _jobs.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _jobs.Clear();
            _modelPath = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(string Text, int[] TokenIds)> GenerateAsync(
        string prompt,
        int[] promptTokens,
        int maxNewTokens,
        CancellationToken cancellationToken)
    {
        // Simulate modest TTFT + decode latency.
        await Task.Delay(12, cancellationToken).ConfigureAwait(false);

        var seed = DeterministicSeed(prompt);
        var rng = new Random(seed);
        var count = Math.Clamp(maxNewTokens, 1, 512);
        var tokens = new int[count];
        var words = BuildVocabulary(prompt);

        var sb = new StringBuilder(count * 6);
        sb.Append("[mock] ");

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Mix prompt token influence with deterministic random ids.
            var baseId = promptTokens.Length > 0
                ? promptTokens[i % promptTokens.Length]
                : 1000;
            tokens[i] = unchecked(baseId * 31 + rng.Next(1, 50_000));

            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(words[rng.Next(words.Length)]);

            if ((i & 7) == 7)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        return (sb.ToString(), tokens);
    }

    private static int DeterministicSeed(string prompt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return BitConverter.ToInt32(hash, 0);
    }

    private static string[] BuildVocabulary(string prompt)
    {
        var pieces = prompt
            .Split([' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (pieces.Length == 0)
        {
            return ["hello", "world", "from", "mock", "engine"];
        }

        // Echo-ish vocabulary so completions feel related to the prompt.
        var list = new List<string>(pieces.Length + 8);
        list.AddRange(pieces.Take(32));
        list.AddRange(["the", "a", "and", "to", "of", "is", "in", "that"]);
        return list.ToArray();
    }
}
