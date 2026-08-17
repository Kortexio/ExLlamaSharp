using System.Text;
using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Chat;

/// <summary>
/// Formats chat messages using the Llama 3 / 3.1 instruct chat template.
/// </summary>
public static class ChatTemplate
{
    private const string BeginOfText = "<|begin_of_text|>";
    private const string StartHeader = "<|start_header_id|>";
    private const string EndHeader = "<|end_header_id|>";
    private const string Eot = "<|eot_id|>";

    /// <summary>
    /// Format messages for Llama 3 instruct. When <paramref name="addGenerationPrompt"/>
    /// is true, appends the assistant header so the model continues as assistant.
    /// </summary>
    public static string Format(
        IReadOnlyList<ChatMessage> messages,
        bool addGenerationPrompt = true)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var sb = new StringBuilder(messages.Count * 128);
        sb.Append(BeginOfText);

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            var role = RoleName(message.Role);
            sb.Append(StartHeader).Append(role).Append(EndHeader)
                .Append("\n\n")
                .Append(message.Content)
                .Append(Eot);
        }

        if (addGenerationPrompt)
        {
            sb.Append(StartHeader).Append("assistant").Append(EndHeader).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>Convenience overload for a single user turn with optional system prompt.</summary>
    public static string FormatUser(
        string userMessage,
        string? systemPrompt = null,
        bool addGenerationPrompt = true)
    {
        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage { Role = ChatRole.System, Content = systemPrompt });
        }

        messages.Add(new ChatMessage { Role = ChatRole.User, Content = userMessage });
        return Format(messages, addGenerationPrompt);
    }

    public static readonly string[] DefaultStopStrings =
    [
        "<|eot_id|>",
        "<|eom_id|>",
        "<|end_of_text|>",
        "<|start_header_id|>",
        "<|end_header_id|>",
        "<|im_end|>",
        "<|im_start|>",
        "</s>",
        "<end_of_turn>",
        "<eos>",
    ];

    /// <summary>Cut leaked chat-control tokens from model output.</summary>
    public static string StripSpecialTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var cut = text.Length;
        foreach (var marker in DefaultStopStrings)
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && i < cut)
            {
                cut = i;
            }
        }

        return text[..cut].TrimEnd();
    }

    internal static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };
}
