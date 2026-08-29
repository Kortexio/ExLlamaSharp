using System.Text.Json;
using ExLlamaSharp.Server.Models;

namespace ExLlamaSharp.Server.OpenAi;

/// <summary>Parses model text that contains an OpenAI-style tool_calls JSON object.</summary>
public static class ToolCallParser
{
    public static bool TryParse(string? text, out IReadOnlyList<ChatToolCall> toolCalls, out string? residualContent)
    {
        toolCalls = Array.Empty<ChatToolCall>();
        residualContent = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (TryParseObject(trimmed, out toolCalls, out residualContent))
        {
            return toolCalls.Count > 0;
        }

        // Fenced ```json ... ```
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterFence = trimmed[(fenceStart + 3)..];
            if (afterFence.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                afterFence = afterFence[4..];
            }

            var fenceEnd = afterFence.IndexOf("```", StringComparison.Ordinal);
            if (fenceEnd > 0)
            {
                var inner = afterFence[..fenceEnd].Trim();
                if (TryParseObject(inner, out toolCalls, out _))
                {
                    residualContent = null;
                    return toolCalls.Count > 0;
                }
            }
        }

        // Embedded object with "tool_calls"
        var idx = trimmed.IndexOf("\"tool_calls\"", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var brace = trimmed.LastIndexOf('{', idx);
            if (brace >= 0)
            {
                var slice = ExtractJsonObject(trimmed, brace);
                if (slice is not null && TryParseObject(slice, out toolCalls, out _))
                {
                    residualContent = null;
                    return toolCalls.Count > 0;
                }
            }
        }

        return false;
    }

    private static bool TryParseObject(string json, out IReadOnlyList<ChatToolCall> toolCalls, out string? residual)
    {
        toolCalls = Array.Empty<ChatToolCall>();
        residual = json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("tool_calls", out var arr)
                || arr.ValueKind != JsonValueKind.Array
                || arr.GetArrayLength() == 0)
            {
                return false;
            }

            var list = new List<ChatToolCall>();
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                id = string.IsNullOrWhiteSpace(id) ? $"call_{i + 1}" : id;
                var type = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "function";
                if (!el.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string args = "{}";
                if (fn.TryGetProperty("arguments", out var argsEl))
                {
                    args = argsEl.ValueKind == JsonValueKind.String
                        ? argsEl.GetString() ?? "{}"
                        : argsEl.GetRawText();
                }

                list.Add(new ChatToolCall
                {
                    Id = id!,
                    Type = type ?? "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = name!,
                        Arguments = args,
                    },
                });
                i++;
            }

            if (list.Count == 0)
            {
                return false;
            }

            toolCalls = list;
            residual = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }
}
