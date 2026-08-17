using System.Text;
using System.Text.Json;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;
using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace ExLlamaSharp.Server.Endpoints;

public static class OpenAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/chat/completions", ChatCompletionsAsync);
        v1.MapPost("/completions", CompletionsAsync);
        v1.MapGet("/models", ListModelsAsync);
        v1.MapGet("/models/{id}", GetModelAsync);
        v1.MapPost("/embeddings", EmbeddingsAsync);
        v1.MapPost("/tokenize", TokenizeAsync);
        v1.MapPost("/detokenize", DetokenizeAsync);
        v1.MapGet("/metrics", MetricsJsonAsync);

        // Catch-all for unimplemented OpenAI routes → 501 shaped error
        v1.Map("{**path}", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
            return Results.Json(
                ErrorResponse.Create(
                    $"OpenAI endpoint not implemented: {ctx.Request.Method} {ctx.Request.Path}",
                    "not_implemented_error",
                    "not_implemented"),
                statusCode: StatusCodes.Status501NotImplemented);
        });

        return app;
    }

    private static async Task<IResult> ChatCompletionsAsync(
        ChatCompletionRequest request,
        HttpContext http,
        EngineHostService engineHost,
        SettingsService settingsService,
        ContentModerationService moderation,
        AuditService audit,
        RateLimiter rateLimiter,
        AppDbContext db)
    {
        if (request.Messages is null || request.Messages.Count == 0)
        {
            return Results.Json(
                ErrorResponse.Create("messages is required", code: "invalid_messages"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!http.HasScope("chat") && !http.HasScope("completions"))
        {
            return Results.Json(
                ErrorResponse.Create("Scope 'chat' required.", "permission_error", "insufficient_scope"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var modelId = await ResolveModelNameAsync(request.Model, engineHost, db, http.RequestAborted)
            .ConfigureAwait(false);

        var messages = request.Messages.Select(m => new ChatMessage
        {
            Role = ParseRole(m.Role),
            Content = m.GetTextContent(),
            Name = m.Name,
        }).ToList();

        var prompt = ChatTemplate.Format(messages, addGenerationPrompt: true);
        var mod = await moderation.EvaluateAsync(prompt, http.RequestAborted).ConfigureAwait(false);
        if (!mod.Allowed)
        {
            return Results.Json(
                ErrorResponse.Create(mod.Message ?? "Content blocked by moderation.", "content_filter", "content_filter"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var timeoutCts = await CreateTimeoutCtsAsync(settingsService, http.RequestAborted).ConfigureAwait(false);
        var ct = timeoutCts.Token;

        var engineRequest = new CompletionRequest
        {
            Prompt = prompt,
            Messages = messages,
            StopStrings = ReadStopStrings(request.Stop),
            MaxNewTokens = request.MaxTokens ?? 256,
            Temperature = request.Temperature ?? 0.7f,
            TopP = request.TopP ?? 0.9f,
            TopK = request.TopK ?? 40,
            Priority = InvertPriority(http.GetPriority()),
            JobId = Guid.NewGuid(),
        };

        var started = DateTime.UtcNow;
        CompletionResult result;
        try
        {
            EnsureEngineReady(engineHost);
            result = await engineHost.Engine.SubmitAsync(engineRequest, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "engine_not_ready"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                ErrorResponse.Create("Request timed out or cancelled.", "timeout_error", "timeout"),
                statusCode: StatusCodes.Status408RequestTimeout);
        }

        RecordUsage(http, rateLimiter, audit, "/v1/chat/completions", result, started);

        var completionId = $"chatcmpl-{result.JobId:N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (request.Stream)
        {
            return Results.Stream(async stream =>
            {
                await WriteSseChatStreamAsync(stream, completionId, created, modelId, result.Text, http.RequestAborted)
                    .ConfigureAwait(false);
            }, "text/event-stream");
        }

        return Results.Json(new ChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = modelId,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = new ChatCompletionResponseMessage
                    {
                        Role = "assistant",
                        Content = result.Text,
                    },
                    FinishReason = result.Cancelled ? "cancelled" : "stop",
                },
            ],
            Usage = new UsageInfo
            {
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.PromptTokens + result.CompletionTokens,
            },
        }, JsonOptions);
    }

    private static async Task<IResult> CompletionsAsync(
        CompletionRequestDto request,
        HttpContext http,
        EngineHostService engineHost,
        SettingsService settingsService,
        ContentModerationService moderation,
        AuditService audit,
        RateLimiter rateLimiter,
        AppDbContext db)
    {
        if (!http.HasScope("completions") && !http.HasScope("chat"))
        {
            return Results.Json(
                ErrorResponse.Create("Scope 'completions' required.", "permission_error", "insufficient_scope"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var prompt = request.GetPromptText();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.Json(
                ErrorResponse.Create("prompt is required", code: "invalid_prompt"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var mod = await moderation.EvaluateAsync(prompt, http.RequestAborted).ConfigureAwait(false);
        if (!mod.Allowed)
        {
            return Results.Json(
                ErrorResponse.Create(mod.Message ?? "Content blocked by moderation.", "content_filter", "content_filter"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var modelId = await ResolveModelNameAsync(request.Model, engineHost, db, http.RequestAborted)
            .ConfigureAwait(false);

        using var timeoutCts = await CreateTimeoutCtsAsync(settingsService, http.RequestAborted).ConfigureAwait(false);
        var engineRequest = new CompletionRequest
        {
            Prompt = prompt,
            MaxNewTokens = request.MaxTokens ?? 256,
            Temperature = request.Temperature ?? 0.7f,
            TopP = request.TopP ?? 0.9f,
            TopK = request.TopK ?? 40,
            Priority = InvertPriority(http.GetPriority()),
            JobId = Guid.NewGuid(),
        };

        var started = DateTime.UtcNow;
        CompletionResult result;
        try
        {
            EnsureEngineReady(engineHost);
            result = await engineHost.Engine.SubmitAsync(engineRequest, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "engine_not_ready"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                ErrorResponse.Create("Request timed out or cancelled.", "timeout_error", "timeout"),
                statusCode: StatusCodes.Status408RequestTimeout);
        }

        RecordUsage(http, rateLimiter, audit, "/v1/completions", result, started);

        var id = $"cmpl-{result.JobId:N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (request.Stream)
        {
            return Results.Stream(async stream =>
            {
                await WriteSseCompletionStreamAsync(stream, id, created, modelId, result.Text, http.RequestAborted)
                    .ConfigureAwait(false);
            }, "text/event-stream");
        }

        return Results.Json(new CompletionResponse
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new CompletionChoice
                {
                    Index = 0,
                    Text = result.Text,
                    FinishReason = result.Cancelled ? "cancelled" : "stop",
                },
            ],
            Usage = new UsageInfo
            {
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.PromptTokens + result.CompletionTokens,
            },
        }, JsonOptions);
    }

    private static async Task<IResult> ListModelsAsync(AppDbContext db, EngineHostService engineHost, CancellationToken ct)
    {
        var models = await db.Models.AsNoTracking().OrderBy(m => m.Alias ?? m.Path).ToListAsync(ct).ConfigureAwait(false);
        var data = new List<ModelObject>();

        foreach (var m in models)
        {
            data.Add(new ModelObject
            {
                Id = m.Alias ?? m.Id.ToString("N"),
                Created = new DateTimeOffset(m.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
            });
        }

        if (data.Count == 0 && engineHost.IsLoaded)
        {
            data.Add(new ModelObject
            {
                Id = engineHost.LoadedModelId?.ToString("N") ?? "default",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        return Results.Json(new ModelsListResponse { Data = data }, JsonOptions);
    }

    private static async Task<IResult> GetModelAsync(string id, AppDbContext db, CancellationToken ct)
    {
        ModelRecord? model = null;
        if (Guid.TryParse(id, out var guid))
        {
            model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == guid, ct).ConfigureAwait(false);
        }

        model ??= await db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Alias == id, ct)
            .ConfigureAwait(false);

        if (model is null)
        {
            return Results.Json(
                ErrorResponse.Create($"Model '{id}' not found.", code: "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new ModelObject
        {
            Id = model.Alias ?? model.Id.ToString("N"),
            Created = new DateTimeOffset(model.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
        }, JsonOptions);
    }

    private static async Task<IResult> EmbeddingsAsync(
        EmbeddingRequest request,
        HttpContext http,
        EmbeddingService embeddings,
        EngineHostService engineHost,
        AppDbContext db)
    {
        if (!http.HasScope("embeddings") && !http.HasScope("admin"))
        {
            return Results.Json(
                ErrorResponse.Create("Scope 'embeddings' required.", "permission_error", "insufficient_scope"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var inputs = request.GetInputTexts();
        if (inputs.Count == 0)
        {
            return Results.Json(
                ErrorResponse.Create("input is required", code: "invalid_input"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var vectors = await embeddings.EmbedBatchAsync(inputs, http.RequestAborted).ConfigureAwait(false);
        var modelId = await ResolveModelNameAsync(request.Model, engineHost, db, http.RequestAborted)
            .ConfigureAwait(false);

        var data = new List<EmbeddingData>(vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            data.Add(new EmbeddingData { Index = i, Embedding = vectors[i] });
        }

        var promptTokens = inputs.Sum(t => Math.Max(1, t.Length / 4));
        return Results.Json(new EmbeddingResponse
        {
            Model = modelId,
            Data = data,
            Usage = new EmbeddingUsage
            {
                PromptTokens = promptTokens,
                TotalTokens = promptTokens,
            },
        }, JsonOptions);
    }

    private static Task<IResult> TokenizeAsync(
        TokenizeRequest request,
        EngineHostService engineHost)
    {
        if (string.IsNullOrEmpty(request.Prompt))
        {
            return Task.FromResult<IResult>(Results.Json(
                ErrorResponse.Create("prompt is required", code: "invalid_prompt"),
                statusCode: StatusCodes.Status400BadRequest));
        }

        try
        {
            var tokens = engineHost.Engine.Tokenize(request.Prompt);
            return Task.FromResult<IResult>(Results.Json(new TokenizeResponse
            {
                Tokens = tokens,
                Count = tokens.Length,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult<IResult>(Results.Json(
                ErrorResponse.Create(ex.Message, "server_error"),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static Task<IResult> DetokenizeAsync(
        DetokenizeRequest request,
        EngineHostService engineHost)
    {
        try
        {
            var text = engineHost.Engine.Detokenize(request.Tokens);
            return Task.FromResult<IResult>(Results.Json(new DetokenizeResponse { Text = text }, JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult<IResult>(Results.Json(
                ErrorResponse.Create(ex.Message, "server_error"),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static Task<IResult> MetricsJsonAsync(EngineHostService engineHost)
    {
        try
        {
            var m = engineHost.Engine.GetMetrics();
            return Task.FromResult<IResult>(Results.Json(new MetricsJsonResponse
            {
                TotalPromptTokens = m.TotalPromptTokens,
                TotalGeneratedTokens = m.TotalGeneratedTokens,
                NumJobsWaiting = m.NumJobsWaiting,
                NumJobsRunning = m.NumJobsRunning,
                NumJobsSwapped = m.NumJobsSwapped,
                NumJobsFinished = m.NumJobsFinished,
                NumPagesUsed = m.NumPagesUsed,
                NumPagesFree = m.NumPagesFree,
                TokensPerSecond = m.TokensPerSecond,
                LastStepMs = m.LastStepMs,
                StepCount = m.StepCount,
                IsMock = m.IsMock,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult<IResult>(Results.Json(
                ErrorResponse.Create(ex.Message, "server_error"),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static void EnsureEngineReady(EngineHostService engineHost)
    {
        if (!engineHost.IsLoaded || !engineHost.IsRunning)
        {
            throw new InvalidOperationException("No model loaded. Use POST /api/v1/models/load first.");
        }
    }

    private static async Task<string> ResolveModelNameAsync(
        string? requested,
        EngineHostService engineHost,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        if (engineHost.LoadedModelId is Guid id)
        {
            var record = await db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, ct)
                .ConfigureAwait(false);
            if (record?.Alias is not null)
            {
                return record.Alias;
            }

            return id.ToString("N");
        }

        return "default";
    }

    private static List<string> ReadStopStrings(JsonElement? stop)
    {
        var list = new List<string>();
        if (stop is not { } el)
        {
            return list;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                list.Add(s);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        list.Add(s);
                    }
                }
            }
        }

        return list;
    }

    private static ChatRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>API key priority 1 (highest) → engine priority higher number scheduled sooner.</summary>
    private static int InvertPriority(int apiKeyPriority) => Math.Clamp(11 - apiKeyPriority, 1, 10);

    private static async Task<CancellationTokenSource> CreateTimeoutCtsAsync(
        SettingsService settingsService,
        CancellationToken requestAborted)
    {
        var settings = await settingsService.GetAsync(requestAborted).ConfigureAwait(false);
        var seconds = Math.Clamp(settings.RequestTimeoutSeconds, 1, 3600);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private static void RecordUsage(
        HttpContext http,
        RateLimiter rateLimiter,
        AuditService audit,
        string endpoint,
        CompletionResult result,
        DateTime started)
    {
        var keyId = http.GetKeyId();
        if (keyId is Guid id)
        {
            rateLimiter.RecordTokens(id.ToString("N"), result.PromptTokens + result.CompletionTokens);
        }

        audit.Enqueue(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Endpoint = endpoint,
            KeyId = keyId,
            TenantId = http.GetTenantId(),
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            StatusCode = 200,
            DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            Error = result.Failed ? result.Error : null,
        });
    }

    private static async Task WriteSseChatStreamAsync(
        Stream stream,
        string id,
        long created,
        string model,
        string text,
        CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);

        var roleChunk = new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatCompletionDelta { Role = "assistant" },
                    FinishReason = null,
                },
            ],
        };
        await WriteSseDataAsync(writer, roleChunk, ct).ConfigureAwait(false);

        foreach (var piece in ChunkText(text, 12))
        {
            ct.ThrowIfCancellationRequested();
            var chunk = new ChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new ChatCompletionChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatCompletionDelta { Content = piece },
                        FinishReason = null,
                    },
                ],
            };
            await WriteSseDataAsync(writer, chunk, ct).ConfigureAwait(false);
        }

        var doneChunk = new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatCompletionDelta(),
                    FinishReason = "stop",
                },
            ],
        };
        await WriteSseDataAsync(writer, doneChunk, ct).ConfigureAwait(false);
        await writer.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseCompletionStreamAsync(
        Stream stream,
        string id,
        long created,
        string model,
        string text,
        CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);

        foreach (var piece in ChunkText(text, 12))
        {
            ct.ThrowIfCancellationRequested();
            var payload = new
            {
                id,
                @object = "text_completion",
                created,
                model,
                choices = new[]
                {
                    new { index = 0, text = piece, finish_reason = (string?)null },
                },
            };
            await WriteSseDataAsync(writer, payload, ct).ConfigureAwait(false);
        }

        var final = new
        {
            id,
            @object = "text_completion",
            created,
            model,
            choices = new[]
            {
                new { index = 0, text = "", finish_reason = (string?)"stop" },
            },
        };
        await WriteSseDataAsync(writer, final, ct).ConfigureAwait(false);
        await writer.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseDataAsync<T>(StreamWriter writer, T payload, CancellationToken ct)
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
}
