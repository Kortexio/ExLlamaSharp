using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class CompletionRequestDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("prompt")]
    public JsonElement? Prompt { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("echo")]
    public bool? Echo { get; set; }

    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    public string GetPromptText()
    {
        if (Prompt is null || Prompt.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (Prompt.Value.ValueKind == JsonValueKind.String)
        {
            return Prompt.Value.GetString() ?? string.Empty;
        }

        if (Prompt.Value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var el in Prompt.Value.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    parts.Add(el.GetString() ?? string.Empty);
                }
            }

            return string.Join('\n', parts);
        }

        return Prompt.Value.ToString();
    }
}

public sealed class CompletionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "text_completion";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("choices")]
    public required List<CompletionChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; init; }
}

public sealed class CompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}
