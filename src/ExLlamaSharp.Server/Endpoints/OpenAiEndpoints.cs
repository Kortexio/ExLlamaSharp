using System.Text.Json;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;
using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.OpenAi;
using ExLlamaSharp.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace ExLlamaSharp.Server.Endpoints;

public static class OpenAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = OpenAiSseWriter.JsonOptions;

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

        // Catch-all for unimplemented OpenAI routes â†’ 501 shaped error
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

        var timeoutCts = await CreateTimeoutCtsAsync(settingsService, http.RequestAborted).ConfigureAwait(false);
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
        try
        {
            EnsureEngineReady(engineHost);
        }
        catch (InvalidOperationException ex)
        {
            timeoutCts.Dispose();
            return OpenAiCompletionRunner.JsonError(ex.Message, "server_error", "engine_not_ready", StatusCodes.Status503ServiceUnavailable);
        }

        return await OpenAiCompletionRunner.RunAsync(
            http,
            engineHost.Engine,
            new OpenAiRunContext
            {
                EngineRequest = engineRequest,
                Stream = request.Stream,
                ModelId = modelId,
                Endpoint = "/v1/chat/completions",
                CompletionId = $"chatcmpl-{engineRequest.JobId:N}",
                SseKind = OpenAiSseKind.Chat,
                ToJson = (completed, created) => new ChatCompletionResponse
                {
                    Id = $"chatcmpl-{completed.JobId:N}",
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
                                Content = completed.Text,
                            },
                            FinishReason = completed.Cancelled ? "cancelled" : "stop",
                        },
                    ],
                    Usage = new UsageInfo
                    {
                        PromptTokens = completed.PromptTokens,
                        CompletionTokens = completed.CompletionTokens,
                        TotalTokens = completed.PromptTokens + completed.CompletionTokens,
                    },
                },
            },
            timeoutCts,
            started,
            rateLimiter,
            audit).ConfigureAwait(false);
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

        var timeoutCts = await CreateTimeoutCtsAsync(settingsService, http.RequestAborted).ConfigureAwait(false);
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
        try
        {
            EnsureEngineReady(engineHost);
        }
        catch (InvalidOperationException ex)
        {
            timeoutCts.Dispose();
            return OpenAiCompletionRunner.JsonError(ex.Message, "server_error", "engine_not_ready", StatusCodes.Status503ServiceUnavailable);
        }

        return await OpenAiCompletionRunner.RunAsync(
            http,
            engineHost.Engine,
            new OpenAiRunContext
            {
                EngineRequest = engineRequest,
                Stream = request.Stream,
                ModelId = modelId,
                Endpoint = "/v1/completions",
                CompletionId = $"cmpl-{engineRequest.JobId:N}",
                SseKind = OpenAiSseKind.Completion,
                ToJson = (completed, created) => new CompletionResponse
                {
                    Id = $"cmpl-{completed.JobId:N}",
                    Created = created,
                    Model = modelId,
                    Choices =
                    [
                        new CompletionChoice
                        {
                            Index = 0,
                            Text = completed.Text,
                            FinishReason = completed.Cancelled ? "cancelled" : "stop",
                        },
                    ],
                    Usage = new UsageInfo
                    {
                        PromptTokens = completed.PromptTokens,
                        CompletionTokens = completed.CompletionTokens,
                        TotalTokens = completed.PromptTokens + completed.CompletionTokens,
                    },
                },
            },
            timeoutCts,
            started,
            rateLimiter,
            audit).ConfigureAwait(false);
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

    /// <summary>API key priority 1 (highest) â†’ engine priority higher number scheduled sooner.</summary>
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
}
