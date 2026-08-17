using ExLlamaSharp.Chat;

namespace ExLlamaSharp.Engine.Worker;

internal static class WorkerSubmitPayload
{
    public static object FromRequest(CompletionRequest request)
    {
        var stops = BuildStopList(request);
        if (request.Messages is { Count: > 0 })
        {
            return new
            {
                cmd = "submit",
                messages = request.Messages.Select(m => new
                {
                    role = RoleWire(m.Role),
                    content = m.Content ?? "",
                }),
                max_new_tokens = request.MaxNewTokens,
                temperature = request.Temperature,
                top_p = request.TopP,
                top_k = request.TopK,
                stop = stops,
            };
        }

        return new
        {
            cmd = "submit",
            prompt = request.Prompt,
            max_new_tokens = request.MaxNewTokens,
            temperature = request.Temperature,
            top_p = request.TopP,
            top_k = request.TopK,
            stop = stops,
        };
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
