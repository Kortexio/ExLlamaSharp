using System.Text.Json;
using ExLlamaSharp.Server.Models;

namespace ExLlamaSharp.Server.OpenAi;

public static class ToolCallValidator
{
    public const string ToolsModeHeader = "X-ExLlamaSharp-Tools-Mode";
    public const string StructuredOutputHeader = "X-ExLlamaSharp-Structured-Output";
    public const string ToolsModePromptParse = "prompt_parse";
    public const string ToolsModeConstrained = "constrained";
    public const string StructuredOutputPromptOnly = "prompt_only";
    public const string StructuredOutputConstrained = "constrained";

    public readonly record struct ToolChoiceRequirement(bool RequiresTool, string? RequiredFunctionName);

    public static ToolChoiceRequirement ParseToolChoiceRequirement(JsonElement? toolChoice, bool hasTools)
    {
        if (!hasTools)
        {
            return default;
        }

        if (toolChoice is null || toolChoice.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        var el = toolChoice.Value;
        if (el.ValueKind == JsonValueKind.String
            && string.Equals(el.GetString(), "required", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolChoiceRequirement(true, null);
        }

        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("type", out var typeEl)
            && string.Equals(typeEl.GetString(), "function", StringComparison.OrdinalIgnoreCase)
            && el.TryGetProperty("function", out var fn)
            && fn.TryGetProperty("name", out var nameEl))
        {
            return new ToolChoiceRequirement(true, nameEl.GetString());
        }

        return default;
    }

    public static HashSet<string> CollectToolFunctionNames(IReadOnlyList<ChatTool>? tools)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (tools is null)
        {
            return set;
        }

        foreach (var t in tools)
        {
            var name = t.Function?.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                set.Add(name);
            }
        }

        return set;
    }

    public static string? ValidateToolResponse(string? text, HashSet<string> allowed, ToolChoiceRequirement req)
    {
        if (!ToolCallParser.TryParse(text, out var calls, out _))
        {
            return req.RequiresTool ? "Model did not emit valid tool_calls JSON." : null;
        }

        return ValidateParsedCalls(calls, allowed, req);
    }

    public static string? ValidateParsedCalls(
        IReadOnlyList<ChatToolCall> calls,
        HashSet<string> allowed,
        ToolChoiceRequirement req)
    {
        if (calls.Count == 0)
        {
            return req.RequiresTool ? "Model did not emit tool_calls." : null;
        }

        foreach (var c in calls)
        {
            var name = c.Function?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Tool call missing function name.";
            }

            if (allowed.Count > 0 && !allowed.Contains(name))
            {
                return $"Unknown tool function '{name}'.";
            }

            if (!string.IsNullOrWhiteSpace(req.RequiredFunctionName)
                && !string.Equals(name, req.RequiredFunctionName, StringComparison.Ordinal))
            {
                return $"tool_choice required function '{req.RequiredFunctionName}' but model called '{name}'.";
            }
        }

        return null;
    }
}