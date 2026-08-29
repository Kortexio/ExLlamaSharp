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
        AbTestRouter abRouter,
        AppDbContext db,
        WebhookService webhooks)
    {
        if (request.Messages is null || request.Messages.Count == 0)
        {
            return Results.Json(
                ErrorResponse.Create("messages is required", code: "invalid_messages"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var unsupported = ValidateUnsupportedChatFields(request);
        if (unsupported is not null)
        {
            return unsupported;
        }

        if (!http.HasScope("chat") && !http.HasScope("completions"))
        {
            return Results.Json(
                ErrorResponse.Create("Scope 'chat' required.", "permission_error", "insufficient_scope"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var visionParts = CollectVisionParts(request.Messages);
        if (visionParts.Count > 0 && !engineHost.SupportsVision)
        {
            return Results.Json(
                ErrorResponse.Create(
                    "Multimodal image_url requires an EXL3 VLM with a loaded vision component "
                    + "(e.g. Qwen3-VL / Gemma VL). The current model is text-only.",
                    "invalid_request_error",
                    "vision_not_supported"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var jobId = Guid.NewGuid();
        string modelId;
        Guid? abTestId;
        string? abVariant;
        try
        {
            (modelId, abTestId, abVariant) = await ResolveModelWithAbAsync(
                    request.Model, jobId.ToString("N"), http, engineHost, abRouter, db, settingsService, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "ab_model_load_failed"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var messages = request.Messages.Select(m => new ChatMessage
        {
            Role = ParseRole(m.Role),
            Content = m.GetTextContent(),
            Name = m.Name,
            ToolCallId = m.ToolCallId,
        }).ToList();

        var prompt = ChatTemplate.Format(messages, addGenerationPrompt: true);
        var mod = await moderation.EvaluateAsync(prompt, http.RequestAborted).ConfigureAwait(false);
        if (!mod.Allowed)
        {
            return Results.Json(
                ErrorResponse.Create(mod.Message ?? "Content blocked by moderation.", "content_filter", "content_filter"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (mod.Matched)
        {
            http.Response.Headers["X-Moderation-Action"] = mod.Action ?? "flag";
            if (!string.IsNullOrWhiteSpace(mod.Category))
            {
                http.Response.Headers["X-Moderation-Category"] = mod.Category;
            }
        }

        string? toolsJson = null;
        if (request.Tools is { Count: > 0 })
        {
            toolsJson = JsonSerializer.Serialize(request.Tools, JsonOptions);
        }

        var toolChoiceHint = FormatToolChoice(request.ToolChoice);

        string? jsonSchema = null;
        if (request.ResponseFormat?.JsonSchema is JsonElement schemaEl
            && schemaEl.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            jsonSchema = schemaEl.GetRawText();
        }
        else if (string.Equals(request.ResponseFormat?.Type, "json_object", StringComparison.OrdinalIgnoreCase))
        {
            jsonSchema = """{"type":"object"}""";
        }

        string? adapterPath = null;
        if (http.Request.Headers.TryGetValue("X-Adapter-Id", out var adapterHeader)
            && Guid.TryParse(adapterHeader.ToString(), out var adapterGuid))
        {
            var adapter = await db.LoraAdapters.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == adapterGuid, http.RequestAborted)
                .ConfigureAwait(false);
            adapterPath = adapter?.Path;
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
            MinP = request.MinP ?? 0f,
            PresencePenalty = request.PresencePenalty ?? 0f,
            FrequencyPenalty = request.FrequencyPenalty ?? 0f,
            Seed = request.Seed,
            Priority = InvertPriority(http.GetPriority()),
            JobId = jobId,
            ToolsJson = toolsJson,
            ToolChoiceHint = toolChoiceHint,
            JsonSchema = jsonSchema,
            AdapterPath = adapterPath,
            ImageDataUrls = visionParts.Count > 0 ? visionParts : null,
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
                AbTestId = abTestId,
                AbVariant = abVariant,
                ParseToolCalls = request.Tools is { Count: > 0 },
                ToJson = (completed, created) => BuildChatCompletionJson(completed, created, modelId, request.Tools is { Count: > 0 }),
            },
            timeoutCts,
            started,
            rateLimiter,
            audit,
            webhooks,
            settingsService).ConfigureAwait(false);
    }

    private static object BuildChatCompletionJson(CompletionResult completed, long created, string modelId, bool parseTools)
    {
        List<ChatToolCall>? toolCalls = null;
        string? content = completed.Text;
        var finish = completed.Cancelled ? "cancelled" : "stop";
        if (parseTools && ToolCallParser.TryParse(completed.Text, out var parsed, out var residual))
        {
            toolCalls = parsed.ToList();
            content = residual;
            finish = "tool_calls";
        }

        return new ChatCompletionResponse
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
                        Content = content,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finish,
                },
            ],
            Usage = new UsageInfo
            {
                PromptTokens = completed.PromptTokens,
                CompletionTokens = completed.CompletionTokens,
                TotalTokens = completed.PromptTokens + completed.CompletionTokens,
            },
        };
    }

    private static IResult? ValidateUnsupportedChatFields(ChatCompletionRequest request)
    {
        if (request.N is > 1)
        {
            return Results.Json(
                ErrorResponse.Create("n > 1 is not supported.", "invalid_request_error", "n_not_supported"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.LogitBias is { Count: > 0 })
        {
            return Results.Json(
                ErrorResponse.Create("logit_bias is not supported.", "invalid_request_error", "logit_bias_not_supported"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Logprobs is true)
        {
            return Results.Json(
                ErrorResponse.Create("logprobs is not supported.", "invalid_request_error", "logprobs_not_supported"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static string? FormatToolChoice(JsonElement? toolChoice)
    {
        if (toolChoice is null || toolChoice.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var el = toolChoice.Value;
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("function", out var fn)
            && fn.TryGetProperty("name", out var name))
        {
            return "required:" + name.GetString();
        }

        return el.GetRawText();
    }

    private static List<string> CollectVisionParts(IEnumerable<ChatCompletionMessage> messages)
    {
        var urls = new List<string>();
        foreach (var m in messages)
        {
            if (m.Content is null || m.Content.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var el in m.Content.Value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object
                    || !el.TryGetProperty("type", out var type)
                    || type.GetString() != "image_url")
                {
                    continue;
                }

                if (el.TryGetProperty("image_url", out var imageUrl))
                {
                    var url = imageUrl.ValueKind == JsonValueKind.String
                        ? imageUrl.GetString()
                        : imageUrl.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        urls.Add(url!);
                    }
                }
            }
        }

        return urls;
    }

    private static async Task<IResult> CompletionsAsync(
        CompletionRequestDto request,
        HttpContext http,
        EngineHostService engineHost,
        SettingsService settingsService,
        ContentModerationService moderation,
        AuditService audit,
        RateLimiter rateLimiter,
        AbTestRouter abRouter,
        AppDbContext db,
        WebhookService webhooks)
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

        var jobId = Guid.NewGuid();
        string modelId;
        Guid? abTestId;
        string? abVariant;
        try
        {
            (modelId, abTestId, abVariant) = await ResolveModelWithAbAsync(
                    request.Model, jobId.ToString("N"), http, engineHost, abRouter, db, settingsService, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "ab_model_load_failed"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var timeoutCts = await CreateTimeoutCtsAsync(settingsService, http.RequestAborted).ConfigureAwait(false);
        var engineRequest = new CompletionRequest
        {
            Prompt = prompt,
            MaxNewTokens = request.MaxTokens ?? 256,
            Temperature = request.Temperature ?? 0.7f,
            TopP = request.TopP ?? 0.9f,
            TopK = request.TopK ?? 40,
            Priority = InvertPriority(http.GetPriority()),
            JobId = jobId,
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
                AbTestId = abTestId,
                AbVariant = abVariant,
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
            audit,
            webhooks,
            settingsService).ConfigureAwait(false);
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
        SettingsService settingsService,
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

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await embeddings.EmbedBatchAsync(inputs, http.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "embedding_backend_unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        http.Response.Headers["X-Embedding-Backend"] = embeddings.BackendName;

        var tenantFilter = await TenantScope.EffectiveFilterAsync(http, settingsService, http.RequestAborted)
            .ConfigureAwait(false);
        string modelId;
        try
        {
            modelId = await ResolveModelNameAsync(request.Model, engineHost, db, tenantFilter, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "invalid_request_error", "tenant_forbidden"),
                statusCode: StatusCodes.Status403Forbidden);
        }

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

    private static async Task<(string ModelId, Guid? AbTestId, string? AbVariant)> ResolveModelWithAbAsync(
        string? requested,
        string requestId,
        HttpContext http,
        EngineHostService engineHost,
        AbTestRouter abRouter,
        AppDbContext db,
        SettingsService settings,
        CancellationToken ct)
    {
        Guid? abTestId = null;
        if (http.Request.Headers.TryGetValue("X-Ab-Test-Id", out var header)
            && Guid.TryParse(header.ToString(), out var fromHeader))
        {
            abTestId = fromHeader;
        }
        else if (!string.IsNullOrWhiteSpace(requested)
                 && requested.StartsWith("ab:", StringComparison.OrdinalIgnoreCase)
                 && Guid.TryParse(requested.AsSpan(3), out var fromModel))
        {
            abTestId = fromModel;
        }

        var tenantFilter = await TenantScope.EffectiveFilterAsync(http, settings, ct).ConfigureAwait(false);

        if (abTestId is Guid id)
        {
            var route = await abRouter.RouteAsync(id, requestId, ct).ConfigureAwait(false);
            if (route is not null)
            {
                http.Response.Headers["X-Ab-Test-Id"] = route.AbTestId.ToString("N");
                http.Response.Headers["X-Ab-Variant"] = route.Variant;

                var abRecord = await db.Models.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == route.ModelId, ct)
                    .ConfigureAwait(false);
                if (abRecord is not null && !TenantScope.Matches(abRecord.TenantId, tenantFilter))
                {
                    throw new InvalidOperationException("A/B model is not visible to this tenant.");
                }

                try
                {
                    await engineHost.EnsureModelIdLoadedAsync(route.ModelId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"A/B variant {route.Variant} model could not be loaded: {ex.Message}", ex);
                }

                var name = abRecord?.Alias
                           ?? route.ModelId.ToString("N");
                return (name, route.AbTestId, route.Variant);
            }
        }

        var fallback = await ResolveModelNameAsync(requested, engineHost, db, tenantFilter, ct).ConfigureAwait(false);
        return (fallback, null, null);
    }

    private static async Task<string> ResolveModelNameAsync(
        string? requested,
        EngineHostService engineHost,
        AppDbContext db,
        string? tenantFilter,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            ModelRecord? record = null;
            if (Guid.TryParse(requested, out var guid))
            {
                record = await db.Models.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == guid, ct)
                    .ConfigureAwait(false);
            }

            record ??= await db.Models.AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.Alias != null && m.Alias.ToLower() == requested.ToLower(),
                    ct)
                .ConfigureAwait(false);

            if (record is not null)
            {
                if (!TenantScope.Matches(record.TenantId, tenantFilter))
                {
                    throw new InvalidOperationException("Model is not visible to this tenant.");
                }

                var loadedAlias = engineHost.LoadedModelId is Guid loadedId
                    ? (await db.Models.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.Id == loadedId, ct)
                        .ConfigureAwait(false))?.Alias
                    : null;

                var requestedName = record.Alias ?? record.Id.ToString("N");
                var differsFromLoaded =
                    engineHost.LoadedModelId != record.Id
                    && !string.Equals(loadedAlias, requested, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(engineHost.LoadedModelId?.ToString("N"), requested, StringComparison.OrdinalIgnoreCase);

                if (differsFromLoaded || !engineHost.IsLoaded)
                {
                    await engineHost.EnsureModelIdLoadedAsync(record.Id, ct).ConfigureAwait(false);
                }

                return requestedName;
            }

            return requested;
        }

        if (engineHost.LoadedModelId is Guid id)
        {
            var record = await db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, ct)
                .ConfigureAwait(false);
            if (record is not null && !TenantScope.Matches(record.TenantId, tenantFilter))
            {
                throw new InvalidOperationException("Loaded model is not visible to this tenant.");
            }

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
