using System.Text;
using System.Text.Json;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;
using ExLlamaSharp.Server.Models;

namespace ExLlamaSharp.Server.OpenAi;

internal enum OpenAiSseKind
{
    Chat,
    Completion,
}

/// <summary>Writes OpenAI-shaped SSE frames for live or post-hoc (chunked) streams.</summary>
internal static class OpenAiSseWriter
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<CompletionResult> WriteLiveAsync(
        Stream stream,
        string id,
        long created,
        string model,
        OpenAiSseKind kind,
        IAsyncEnumerable<CompletionDelta> deltas,
        CancellationToken ct,
        bool parseToolCalls = false)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        if (kind == OpenAiSseKind.Chat)
        {
            await WriteDataAsync(writer, ChatChunk(id, created, model, role: "assistant", content: null, finish: null), ct)
                .ConfigureAwait(false);
        }

        var filter = new StreamingStopFilter();
        var acc = new StreamAccumulator();
        var bufferTools = parseToolCalls && kind == OpenAiSseKind.Chat;

        await foreach (var delta in deltas.WithCancellation(ct).ConfigureAwait(false))
        {
            acc.Apply(delta);
            var piece = filter.Push(delta.Text);
            if (piece.Length > 0)
            {
                acc.Text.Append(piece);
                if (!bufferTools)
                {
                    await WriteContentAsync(writer, kind, id, created, model, piece, ct).ConfigureAwait(false);
                }
            }

            if (delta.Eos || delta.Failed || delta.Cancelled || filter.Stopped)
            {
                break;
            }
        }

        var flushed = filter.Flush();
        if (flushed.Length > 0)
        {
            acc.Text.Append(flushed);
            if (!bufferTools)
            {
                await WriteContentAsync(writer, kind, id, created, model, flushed, ct).ConfigureAwait(false);
            }
        }

        var finish = acc.FinishReason;
        if (bufferTools && ToolCallParser.TryParse(acc.Text.ToString(), out var toolCalls, out var residual))
        {
            finish = "tool_calls";
            var deltasList = toolCalls.Select((t, i) => new ChatToolCallDelta
            {
                Index = i,
                Id = t.Id,
                Type = t.Type,
                Function = new ChatToolCallFunctionDelta
                {
                    Name = t.Function.Name,
                    Arguments = t.Function.Arguments,
                },
            }).ToList();
            await WriteDataAsync(writer, new ChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new ChatCompletionChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatCompletionDelta { ToolCalls = deltasList },
                        FinishReason = null,
                    },
                ],
            }, ct).ConfigureAwait(false);
            acc.Text.Clear();
            if (!string.IsNullOrEmpty(residual))
            {
                acc.Text.Append(residual);
            }
        }
        else if (bufferTools)
        {
            foreach (var piece in ChunkText(acc.Text.ToString(), 12))
            {
                await WriteContentAsync(writer, kind, id, created, model, piece, ct).ConfigureAwait(false);
            }
        }

        await WriteDoneAsync(writer, kind, id, created, model, finish, ct).ConfigureAwait(false);
        return acc.ToResult();
    }

    public static async Task WriteBufferedAsync(
        Stream stream,
        string id,
        long created,
        string model,
        OpenAiSseKind kind,
        string text,
        CancellationToken ct,
        bool parseToolCalls = false)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        if (kind == OpenAiSseKind.Chat)
        {
            await WriteDataAsync(writer, ChatChunk(id, created, model, role: "assistant", content: null, finish: null), ct)
                .ConfigureAwait(false);
        }

        var finish = "stop";
        if (parseToolCalls && kind == OpenAiSseKind.Chat && ToolCallParser.TryParse(text, out var toolCalls, out var residual))
        {
            finish = "tool_calls";
            var deltasList = toolCalls.Select((t, i) => new ChatToolCallDelta
            {
                Index = i,
                Id = t.Id,
                Type = t.Type,
                Function = new ChatToolCallFunctionDelta
                {
                    Name = t.Function.Name,
                    Arguments = t.Function.Arguments,
                },
            }).ToList();
            await WriteDataAsync(writer, new ChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new ChatCompletionChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatCompletionDelta { ToolCalls = deltasList },
                        FinishReason = null,
                    },
                ],
            }, ct).ConfigureAwait(false);
            text = residual ?? "";
        }

        foreach (var piece in ChunkText(text, 12))
        {
            ct.ThrowIfCancellationRequested();
            await WriteContentAsync(writer, kind, id, created, model, piece, ct).ConfigureAwait(false);
        }

        await WriteDoneAsync(writer, kind, id, created, model, finish, ct).ConfigureAwait(false);
    }

    private static async Task WriteContentAsync(
        StreamWriter writer,
        OpenAiSseKind kind,
        string id,
        long created,
        string model,
        string piece,
        CancellationToken ct)
    {
        if (kind == OpenAiSseKind.Chat)
        {
            await WriteDataAsync(writer, ChatChunk(id, created, model, role: null, content: piece, finish: null), ct)
                .ConfigureAwait(false);
            return;
        }

        await WriteDataAsync(writer, CompletionChunk(id, created, model, piece, finish: null), ct)
            .ConfigureAwait(false);
    }

    private static async Task WriteDoneAsync(
        StreamWriter writer,
        OpenAiSseKind kind,
        string id,
        long created,
        string model,
        string finish,
        CancellationToken ct)
    {
        if (kind == OpenAiSseKind.Chat)
        {
            await WriteDataAsync(writer, ChatChunk(id, created, model, role: null, content: null, finish: finish), ct)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteDataAsync(writer, CompletionChunk(id, created, model, text: "", finish: finish), ct)
                .ConfigureAwait(false);
        }

        await writer.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static object ChatChunk(string id, long created, string model, string? role, string? content, string? finish) =>
        new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatCompletionDelta { Role = role, Content = content },
                    FinishReason = finish,
                },
            ],
        };

    private static object CompletionChunk(string id, long created, string model, string text, string? finish) => new
    {
        id,
        @object = "text_completion",
        created,
        model,
        choices = new[]
        {
            new { index = 0, text, finish_reason = finish },
        },
    };

    private static async Task WriteDataAsync<T>(StreamWriter writer, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await writer.WriteAsync($"data: {json}\n\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> ChunkText(string text, int wordsPerChunk)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            yield return text;
            yield break;
        }

        for (var i = 0; i < words.Length; i += wordsPerChunk)
        {
            var slice = words.Skip(i).Take(wordsPerChunk);
            var piece = string.Join(' ', slice);
            if (i + wordsPerChunk < words.Length)
            {
                piece += " ";
            }

            yield return piece;
        }
    }

    private sealed class StreamAccumulator
    {
        public StringBuilder Text { get; } = new();
        public List<int> TokenIds { get; } = [];
        public int PromptTokens { get; private set; }
        public int CompletionTokens { get; private set; }
        public bool Failed { get; private set; }
        public bool Cancelled { get; private set; }
        public string? Error { get; private set; }
        public Guid JobId { get; private set; }
        public string FinishReason { get; private set; } = "stop";

        public void Apply(CompletionDelta delta)
        {
            JobId = delta.JobId;
            if (delta.PromptTokens > 0)
            {
                PromptTokens = delta.PromptTokens;
            }

            if (delta.CompletionTokens > 0)
            {
                CompletionTokens = delta.CompletionTokens;
            }

            if (delta.TokenIds.Length > 0)
            {
                TokenIds.AddRange(delta.TokenIds);
            }

            if (delta.Failed)
            {
                Failed = true;
                Error = delta.Error;
            }

            if (delta.Cancelled)
            {
                Cancelled = true;
                FinishReason = "cancelled";
            }
        }

        public CompletionResult ToResult() => new()
        {
            JobId = JobId,
            Text = Text.ToString(),
            TokenIds = TokenIds.ToArray(),
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens > 0 ? CompletionTokens : TokenIds.Count,
            Failed = Failed,
            Error = Error,
            Cancelled = Cancelled,
        };
    }
}
