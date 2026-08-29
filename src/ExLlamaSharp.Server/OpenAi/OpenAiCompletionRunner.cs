using ExLlamaSharp.Engine;
using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.OpenAi;

internal sealed class OpenAiRunContext
{
    public required CompletionRequest EngineRequest { get; init; }
    public required bool Stream { get; init; }
    public required string ModelId { get; init; }
    public required string Endpoint { get; init; }
    public required string CompletionId { get; init; }
    public required OpenAiSseKind SseKind { get; init; }
    public required Func<CompletionResult, long, object> ToJson { get; init; }
    public Guid? AbTestId { get; init; }
    public string? AbVariant { get; init; }
    public bool ParseToolCalls { get; init; }
}

/// <summary>Shared stream / non-stream execution for OpenAI completion endpoints.</summary>
internal static class OpenAiCompletionRunner
{
    public static async Task<IResult> RunAsync(
        HttpContext http,
        IInferenceEngine engine,
        OpenAiRunContext run,
        CancellationTokenSource timeoutCts,
        DateTime started,
        RateLimiter rateLimiter,
        AuditService audit,
        WebhookService? webhooks = null,
        SettingsService? settings = null)
    {
        var ct = timeoutCts.Token;
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        http.Response.Headers["X-ExLlamaSharp-Engine"] = engine.IsMock ? "mock" : "worker";

        if (run.Stream)
        {
            return Results.Stream(async stream =>
            {
                using (timeoutCts)
                {
                    CompletionResult? usage = null;
                    try
                    {
                        if (engine.SupportsStreaming)
                        {
                            usage = await OpenAiSseWriter.WriteLiveAsync(
                                    stream,
                                    run.CompletionId,
                                    created,
                                    run.ModelId,
                                    run.SseKind,
                                    engine.SubmitStreamAsync(run.EngineRequest, ct),
                                    ct,
                                    run.ParseToolCalls)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            var result = await engine.SubmitAsync(run.EngineRequest, ct).ConfigureAwait(false);
                            usage = result;
                            if (!result.Failed)
                            {
                                await OpenAiSseWriter.WriteBufferedAsync(
                                        stream,
                                        run.CompletionId,
                                        created,
                                        run.ModelId,
                                        run.SseKind,
                                        result.Text,
                                        http.RequestAborted,
                                        run.ParseToolCalls)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        usage ??= new CompletionResult
                        {
                            JobId = run.EngineRequest.JobId ?? Guid.Empty,
                            Text = "",
                            TokenIds = [],
                            Cancelled = true,
                            Duration = DateTime.UtcNow - started,
                        };
                    }

                    if (usage is not null)
                    {
                        await RecordUsageAsync(http, rateLimiter, audit, run, usage, started, webhooks, settings)
                            .ConfigureAwait(false);
                    }
                }
            }, "text/event-stream");
        }

        try
        {
            CompletionResult completed;
            try
            {
                completed = await engine.SubmitAsync(run.EngineRequest, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return JsonError(ex.Message, "server_error", "engine_not_ready", StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException)
            {
                return JsonError("Request timed out or cancelled.", "timeout_error", "timeout", StatusCodes.Status408RequestTimeout);
            }

            if (completed.Failed)
            {
                await RecordUsageAsync(http, rateLimiter, audit, run, completed, started, webhooks, settings)
                    .ConfigureAwait(false);
                _ = webhooks?.SendAsync("completion.failed", new
                {
                    endpoint = run.Endpoint,
                    model = run.ModelId,
                    error = completed.Error,
                }, CancellationToken.None);
                return JsonError(completed.Error ?? "Inference failed.", "server_error", "inference_failed", StatusCodes.Status502BadGateway);
            }

            await RecordUsageAsync(http, rateLimiter, audit, run, completed, started, webhooks, settings)
                .ConfigureAwait(false);
            return Results.Json(run.ToJson(completed, created), OpenAiSseWriter.JsonOptions);
        }
        finally
        {
            timeoutCts.Dispose();
        }
    }

    public static IResult JsonError(string message, string type, string code, int status) =>
        Results.Json(ErrorResponse.Create(message, type, code), statusCode: status);

    private static async Task RecordUsageAsync(
        HttpContext http,
        RateLimiter rateLimiter,
        AuditService audit,
        OpenAiRunContext run,
        CompletionResult result,
        DateTime started,
        WebhookService? webhooks,
        SettingsService? settings)
    {
        var keyId = http.GetKeyId();
        if (keyId is Guid id)
        {
            rateLimiter.RecordTokens(id.ToString("N"), result.PromptTokens + result.CompletionTokens);
        }

        decimal cost = 0;
        if (settings is not null)
        {
            try
            {
                var s = await settings.GetAsync().ConfigureAwait(false);
                var perM = s.EstimatedCostPerMillionTokens;
                if (perM > 0)
                {
                    cost = (decimal)(result.PromptTokens + result.CompletionTokens) / 1_000_000m * perM;
                }
            }
            catch
            {
                // ignore cost calc
            }
        }

        audit.Enqueue(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Endpoint = run.Endpoint,
            KeyId = keyId,
            TenantId = http.GetTenantId(),
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            StatusCode = result.Failed ? 502 : 200,
            DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            Error = result.Failed ? result.Error : null,
            AbTestId = run.AbTestId,
            EstimatedCost = cost,
        });

        if (webhooks is not null && !result.Failed && !result.Cancelled)
        {
            _ = webhooks.SendAsync("completion.succeeded", new
            {
                endpoint = run.Endpoint,
                model = run.ModelId,
                prompt_tokens = result.PromptTokens,
                completion_tokens = result.CompletionTokens,
                ab_test_id = run.AbTestId,
                ab_variant = run.AbVariant,
            }, CancellationToken.None);
        }
    }
}
