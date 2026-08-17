using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    public IReadOnlyList<string> GetInputTexts()
    {
        if (Input is null || Input.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (Input.Value.ValueKind == JsonValueKind.String)
        {
            return [Input.Value.GetString() ?? string.Empty];
        }

        if (Input.Value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var el in Input.Value.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    list.Add(el.GetString() ?? string.Empty);
                }
                else if (el.ValueKind == JsonValueKind.Number)
                {
                    list.Add(el.GetRawText());
                }
            }

            return list;
        }

        return [Input.Value.ToString()];
    }
}

public sealed class EmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public required List<EmbeddingData> Data { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("usage")]
    public EmbeddingUsage? Usage { get; init; }
}

public sealed class EmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "embedding";

    [JsonPropertyName("embedding")]
    public required float[] Embedding { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }
}

public sealed class EmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}
