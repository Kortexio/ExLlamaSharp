using System.Text;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Endpoints;

public static partial class AdminEndpoints
{
    private static async Task<IResult> HealthAsync(HealthService health, CancellationToken ct)
    {
        var report = await health.GetHealthAsync(ct).ConfigureAwait(false);
        var code = report.Status == "unhealthy"
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;
        return Results.Json(report, statusCode: code);
    }

    private static async Task<IResult> ReadyAsync(HealthService health, EngineHostService engine, CancellationToken ct)
    {
        var ready = await health.IsReadyAsync(ct).ConfigureAwait(false);
        var body = new ReadyResponse
        {
            Ready = ready,
            ModelLoaded = engine.IsLoaded,
            EngineRunning = engine.IsRunning,
        };
        return Results.Json(body, JsonOptions, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult PrometheusMetricsAsync(EngineHostService engineHost, AuditService audit)
    {
        Engine.EngineMetrics m;
        try
        {
            m = engineHost.Engine.GetMetrics();
        }
        catch
        {
            m = new Engine.EngineMetrics();
        }

        var sb = new StringBuilder();
        sb.AppendLine("# HELP exllamasharp_prompt_tokens_total Total prompt tokens processed");
        sb.AppendLine("# TYPE exllamasharp_prompt_tokens_total counter");
        sb.AppendLine($"exllamasharp_prompt_tokens_total {m.TotalPromptTokens}");
        sb.AppendLine("# HELP exllamasharp_generated_tokens_total Total generated tokens");
        sb.AppendLine("# TYPE exllamasharp_generated_tokens_total counter");
        sb.AppendLine($"exllamasharp_generated_tokens_total {m.TotalGeneratedTokens}");
        sb.AppendLine("# HELP exllamasharp_jobs_waiting Jobs waiting in queue");
        sb.AppendLine("# TYPE exllamasharp_jobs_waiting gauge");
        sb.AppendLine($"exllamasharp_jobs_waiting {m.NumJobsWaiting}");
        sb.AppendLine("# HELP exllamasharp_jobs_running Jobs currently running");
        sb.AppendLine("# TYPE exllamasharp_jobs_running gauge");
        sb.AppendLine($"exllamasharp_jobs_running {m.NumJobsRunning}");
        sb.AppendLine("# HELP exllamasharp_tokens_per_second Approximate decode throughput");
        sb.AppendLine("# TYPE exllamasharp_tokens_per_second gauge");
        sb.AppendLine($"exllamasharp_tokens_per_second {m.TokensPerSecond}");
        sb.AppendLine("# HELP exllamasharp_pages_used KV pages used");
        sb.AppendLine("# TYPE exllamasharp_pages_used gauge");
        sb.AppendLine($"exllamasharp_pages_used {m.NumPagesUsed}");
        sb.AppendLine("# HELP exllamasharp_is_mock Whether mock engine is active");
        sb.AppendLine("# TYPE exllamasharp_is_mock gauge");
        sb.AppendLine($"exllamasharp_is_mock {(m.IsMock ? 1 : 0)}");
        sb.AppendLine("# HELP exllamasharp_audit_dropped_total Audit log entries dropped (queue full)");
        sb.AppendLine("# TYPE exllamasharp_audit_dropped_total counter");
        sb.AppendLine($"exllamasharp_audit_dropped_total {audit.DroppedEntryCount}");

        return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }
}