using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatCompletionMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>OpenAI alias for max_tokens (generation length).</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    /// <summary>Ollama-style options. <c>num_predict</c> = reply length; <c>num_ctx</c> = context window (KV).</summary>
    [JsonPropertyName("options")]
    public OllamaStyleOptions? Options { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("min_p")]
    public float? MinP { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("logit_bias")]
    public Dictionary<string, float>? LogitBias { get; set; }

    [JsonPropertyName("logprobs")]
    public bool? Logprobs { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("response_format")]
    public ResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("tools")]
    public List<ChatTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }
}

public sealed class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    public string GetTextContent()
    {
        if (Content is null || Content.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (Content.Value.ValueKind == JsonValueKind.String)
        {
            return Content.Value.GetString() ?? string.Empty;
        }

        if (Content.Value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var el in Content.Value.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("type", out var type)
                    && type.GetString() == "text"
                    && el.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }

            return string.Join('\n', parts);
        }

        return Content.Value.ToString();
    }
}

public sealed class ResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("json_schema")]
    public JsonElement? JsonSchema { get; set; }
}

public sealed class ChatTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatToolFunction? Function { get; set; }
}

public sealed class ChatToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary>
/// Extra sampling fields nested under OpenAI request <c>options</c> (Ollama-compatible).
/// OpenAI top-level fields always win when both are set.
/// <list type="bullet">
/// <item><c>num_predict</c> — max tokens to generate (same role as <c>max_tokens</c>).</item>
/// <item><c>num_ctx</c> — context / KV size; applied at model load, not mid-request.</item>
/// <item><c>repeat_penalty</c> — alias for <c>frequency_penalty</c> when that field is omitted.</item>
/// </list>
/// </summary>
public sealed class OllamaStyleOptions
{
    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }

    [JsonPropertyName("num_ctx")]
    public int? NumCtx { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("min_p")]
    public float? MinP { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    /// <summary>Ollama alias; used as <c>frequency_penalty</c> when that is omitted.</summary>
    [JsonPropertyName("repeat_penalty")]
    public float? RepeatPenalty { get; set; }
}
