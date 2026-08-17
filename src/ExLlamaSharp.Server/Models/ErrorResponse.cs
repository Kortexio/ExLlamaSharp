using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public required ErrorBody Error { get; init; }

    public static ErrorResponse Create(string message, string type = "invalid_request_error", string? code = null) =>
        new()
        {
            Error = new ErrorBody
            {
                Message = message,
                Type = type,
                Code = code,
            },
        };
}

public sealed class ErrorBody
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}
