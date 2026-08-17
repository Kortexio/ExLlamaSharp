using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class TokenizeRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("add_special_tokens")]
    public bool? AddSpecialTokens { get; set; }
}

public sealed class TokenizeResponse
{
    [JsonPropertyName("tokens")]
    public required int[] Tokens { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed class DetokenizeRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("tokens")]
    public int[] Tokens { get; set; } = [];
}

public sealed class DetokenizeResponse
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
