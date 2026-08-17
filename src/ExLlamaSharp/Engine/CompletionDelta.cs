namespace ExLlamaSharp.Engine;

/// <summary>
/// Incremental generation event. Engines that do not stream yield a single
/// terminal delta via the default <see cref="IInferenceEngine.SubmitStreamAsync"/> implementation.
/// </summary>
public sealed class CompletionDelta
{
    public required Guid JobId { get; init; }

    /// <summary>Newly decoded text for this step (may be empty).</summary>
    public string Text { get; init; } = "";

    public int[] TokenIds { get; init; } = [];

    public bool Eos { get; init; }

    public string? EosReason { get; init; }

    public string? Stage { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public bool Failed { get; init; }

    public string? Error { get; init; }

    public bool Cancelled { get; init; }

    public double TokensPerSecond { get; init; }

    public static CompletionDelta FromResult(CompletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CompletionDelta
        {
            JobId = result.JobId,
            Text = result.Text,
            TokenIds = result.TokenIds,
            Eos = true,
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            Failed = result.Failed,
            Error = result.Error,
            Cancelled = result.Cancelled,
        };
    }
}
