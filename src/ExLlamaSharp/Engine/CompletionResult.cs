namespace ExLlamaSharp.Engine;

/// <summary>
/// Result of a completed (or cancelled/failed) generation job.
/// </summary>
public sealed class CompletionResult
{
    public required Guid JobId { get; init; }

    public required string Text { get; init; }

    public required int[] TokenIds { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public bool Cancelled { get; init; }

    public bool Failed { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }
}
