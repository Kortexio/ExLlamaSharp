namespace ExLlamaSharp.Engine;

/// <summary>
/// Role of a chat turn for template formatting.
/// </summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// Single message in a chat conversation.
/// </summary>
public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Content { get; init; }
    public string? Name { get; init; }
    public string? ToolCallId { get; init; }
}
