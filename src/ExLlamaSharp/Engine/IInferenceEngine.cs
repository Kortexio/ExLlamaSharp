using System.Runtime.CompilerServices;

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

    /// <summary>
    /// True when the loaded model has a working ExLlamaV3 vision component
    /// (multimodal <c>image_url</c> chat).
    /// </summary>
    bool SupportsVision => false;

    /// <summary>
    /// True when <see cref="SubmitStreamAsync"/> yields tokens as they are produced
    /// rather than a single terminal delta.
    /// </summary>
    bool SupportsStreaming => false;

    Task LoadAsync(string modelPath, CancellationToken cancellationToken = default);

    Task UnloadAsync(CancellationToken cancellationToken = default);

    void Start();

    void Stop();

    Task<CompletionResult> SubmitAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Default: run <see cref="SubmitAsync"/> and yield one terminal delta.
    /// Streaming engines override this.
    /// </summary>
    async IAsyncEnumerable<CompletionDelta> SubmitStreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        yield return CompletionDelta.FromResult(result);
    }

    /// <summary>Cancel a previously submitted job by id. Returns false if unknown.</summary>
    bool Cancel(Guid jobId);

    EngineMetrics GetMetrics();

    int[] Tokenize(string text);

    string Detokenize(ReadOnlySpan<int> tokens);
}
