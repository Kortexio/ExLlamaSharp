using ExLlamaSharp.Chat;

namespace ExLlamaSharp.Engine.Worker;

internal static class WorkerSubmitPayload
{
    public static object FromRequest(CompletionRequest request)
    {
        var stops = BuildStopList(request);
        var toolsHint = BuildToolsHint(request);
        if (request.Messages is { Count: > 0 })
        {
            var messages = request.Messages.Select(m => new
            {
                role = RoleWire(m.Role),
                content = m.Content ?? "",
                tool_call_id = m.ToolCallId,
            }).ToList<object>();

            if (!string.IsNullOrWhiteSpace(toolsHint))
            {
                messages.Insert(0, new { role = "system", content = toolsHint, tool_call_id = (string?)null });
            }

            return new
            {
                cmd = "submit",
                messages,
                max_new_tokens = request.MaxNewTokens,
                temperature = request.Temperature,
                top_p = request.TopP,
                top_k = request.TopK,
                min_p = request.MinP,
                presence_penalty = request.PresencePenalty,
                frequency_penalty = request.FrequencyPenalty,
                seed = request.Seed,
                stop = stops,
                adapter_path = request.AdapterPath,
                adapter_scaling = request.AdapterScaling,
                images = request.ImageDataUrls,
            };
        }

        var prompt = request.Prompt;
        if (!string.IsNullOrWhiteSpace(toolsHint))
        {
            prompt = toolsHint + "\n\n" + prompt;
        }

        return new
        {
            cmd = "submit",
            prompt,
            max_new_tokens = request.MaxNewTokens,
            temperature = request.Temperature,
            top_p = request.TopP,
            top_k = request.TopK,
            min_p = request.MinP,
            presence_penalty = request.PresencePenalty,
            frequency_penalty = request.FrequencyPenalty,
            seed = request.Seed,
            stop = stops,
            adapter_path = request.AdapterPath,
            adapter_scaling = request.AdapterScaling,
            images = request.ImageDataUrls,
        };
    }

    private static string? BuildToolsHint(CompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ToolsJson) && string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.ToolsJson))
        {
            sb.AppendLine("You may call tools by replying with a JSON object:");
            sb.AppendLine("""{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"NAME","arguments":"{}"}}]}""");
            if (!string.IsNullOrWhiteSpace(request.ToolChoiceHint))
            {
                var choice = request.ToolChoiceHint.Trim();
                if (string.Equals(choice, "none", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("Do NOT call tools; answer the user directly.");
                }
                else if (choice.StartsWith("required:", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("You MUST call tool: " + choice["required:".Length..].Trim());
                }
                else
                {
                    sb.AppendLine("tool_choice=" + choice);
                }
            }

            sb.AppendLine("Available tools JSON:");
            sb.AppendLine(request.ToolsJson);
        }

        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            sb.AppendLine("Respond with JSON only matching this schema:");
            sb.AppendLine(request.JsonSchema);
        }

        return sb.ToString().Trim();
    }

    private static string RoleWire(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    private static List<object> BuildStopList(CompletionRequest request)
    {
        var stops = new List<object>();
        if (request.StopTokenId >= 0)
        {
            stops.Add(request.StopTokenId);
        }

        foreach (var s in ChatTemplate.DefaultStopStrings)
        {
            stops.Add(s);
        }

        if (request.StopStrings is { Count: > 0 })
        {
            foreach (var s in request.StopStrings)
            {
                if (!string.IsNullOrEmpty(s))
                {
                    stops.Add(s);
                }
            }
        }

        return stops;
    }
}
