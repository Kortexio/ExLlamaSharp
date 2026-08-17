using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class ModelsListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public required List<ModelObject> Data { get; init; }
}

public sealed class ModelObject
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; init; } = "exllamasharp";
}
