namespace ExLlamaSharp.Engine;

/// <summary>
/// Parameters for a text completion / generation job.
/// </summary>
public sealed class CompletionRequest
{
    /// <summary>Raw prompt text (already template-formatted if chat).</summary>
    public required string Prompt { get; init; }

    /// <summary>Pre-tokenized prompt; when set, <see cref="Prompt"/> is ignored for native submit.</summary>
    public int[]? PromptTokens { get; init; }

    public int MaxNewTokens { get; init; } = 256;

    public float Temperature { get; init; } = 0.7f;

    public float TopP { get; init; } = 0.9f;

    public int TopK { get; init; } = 40;

    /// <summary>Higher values are scheduled sooner. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>Stop generation when this token id is produced; -1 = none.</summary>
    public int StopTokenId { get; init; } = -1;

    /// <summary>Stop strings (model-specific EOT / chat markers).</summary>
    public IReadOnlyList<string>? StopStrings { get; init; }

    /// <summary>
    /// When set, the EXL3 worker applies the loaded model's Hugging Face chat template
    /// instead of the Llama-3 prompt in <see cref="Prompt"/>.
    /// </summary>
    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    /// <summary>Optional LoRA adapter directory (PEFT) applied globally on the worker model.</summary>
    public string? AdapterPath { get; init; }

    /// <summary>LoRA scaling multiplier.</summary>
    public float AdapterScaling { get; init; } = 1f;

    /// <summary>OpenAI-style tool definitions (JSON); worker injects into the system prompt.</summary>
    public string? ToolsJson { get; init; }

    /// <summary>When set, instruct the model to emit JSON matching this schema.</summary>
    public string? JsonSchema { get; init; }

    /// <summary>Optional correlation id for cancel / logging.</summary>
    public Guid? JobId { get; init; }
}
