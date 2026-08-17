using System.Text.Json;

namespace ExLlamaSharp.Engine.Worker;

internal readonly struct WorkerStats
{
    public bool Seen { get; init; }
    public int Active { get; init; }
    public int Pending { get; init; }
    public int FreePages { get; init; }
    public int MaxBatchSize { get; init; }

    public static WorkerStats FromJson(JsonElement el) => new()
    {
        Seen = true,
        Active = ReadInt(el, "active"),
        Pending = ReadInt(el, "pending"),
        FreePages = ReadInt(el, "free_pages"),
        MaxBatchSize = ReadInt(el, "max_batch_size"),
    };

    private static int ReadInt(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
        {
            return v;
        }

        return 0;
    }
}

internal sealed class WorkerEvent
{
    public int Id { get; init; }
    public string Stage { get; init; } = "";
    public string Text { get; init; } = "";
    public int[] TokenIds { get; init; } = [];
    public bool Eos { get; init; }
    public bool Ok { get; init; } = true;
    public string? EosReason { get; init; }
    public string? Error { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double TokensPerSecond { get; init; }

    public static WorkerEvent FromJson(JsonElement el)
    {
        TryReadId(el, out var id);
        var ok = true;
        if (el.TryGetProperty("ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            ok = okEl.GetBoolean();
        }

        var completion = 0;
        if (el.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctv))
        {
            completion = ctv;
        }
        else if (el.TryGetProperty("new_tokens", out var nt) && nt.TryGetInt32(out var ntv))
        {
            completion = ntv;
        }

        return new WorkerEvent
        {
            Id = id,
            Stage = el.TryGetProperty("stage", out var st) ? st.GetString() ?? "" : "",
            Text = el.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
            TokenIds = ReadIntArray(el, "token_ids"),
            Eos = el.TryGetProperty("eos", out var eos) && eos.ValueKind == JsonValueKind.True,
            Ok = ok,
            EosReason = el.TryGetProperty("eos_reason", out var er) ? er.GetString() : null,
            Error = el.TryGetProperty("error", out var err) ? err.GetString() : null,
            PromptTokens = el.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : 0,
            CompletionTokens = completion,
            TokensPerSecond = el.TryGetProperty("tokens_per_second", out var tps) && tps.ValueKind == JsonValueKind.Number
                ? tps.GetDouble()
                : 0,
        };
    }

    public static bool TryReadId(JsonElement root, out int id)
    {
        id = 0;
        if (!root.TryGetProperty("id", out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out id))
        {
            return true;
        }

        return el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out id);
    }

    public static int[] ReadIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<int>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            {
                list.Add(v);
            }
        }

        return list.ToArray();
    }
}
