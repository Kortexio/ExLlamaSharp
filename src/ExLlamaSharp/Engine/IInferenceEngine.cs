namespace ExLlamaSharp.Engine;

/// <summary>
/// Abstraction over native ExLlamaSharp engine or the deterministic mock.
/// </summary>
public interface IInferenceEngine : IAsyncDisposable, IDisposable
{
    /// <summary>True when running without <c>exllamasharp.dll</c> / CUDA.</summary>
    bool IsMock { get; }

    /// <summary>Whether a model is currently loaded.</summary>
    bool IsLoaded { get; }

    /// <summary>Whether the scheduler loop is running.</summary>
    bool IsRunning { get; }

    Task LoadAsync(string modelPath, CancellationToken cancellationToken = default);

    Task UnloadAsync(CancellationToken cancellationToken = default);

    void Start();

    void Stop();

    Task<CompletionResult> SubmitAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Cancel a previously submitted job by id. Returns false if unknown.</summary>
    bool Cancel(Guid jobId);

    EngineMetrics GetMetrics();

    int[] Tokenize(string text);

    string Detokenize(ReadOnlySpan<int> tokens);
}
