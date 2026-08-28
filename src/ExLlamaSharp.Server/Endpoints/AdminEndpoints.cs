using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace ExLlamaSharp.Server.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Public / ops outside /api/v1 auth where noted
        app.MapGet("/health", HealthAsync);
        app.MapGet("/ready", ReadyAsync);
        app.MapGet("/metrics", PrometheusMetricsAsync);

        var api = app.MapGroup("/api/v1");

        api.MapGet("/settings", GetSettingsAsync);
        api.MapPost("/settings", PostSettingsAsync);
        api.MapPatch("/settings", PatchSettingsAsync);

        api.MapGet("/models/library", GetModelLibraryAsync);
        api.MapGet("/models/library/search", SearchLibraryAsync);
        api.MapPost("/models/library", PostModelLibraryAsync);

        api.MapPost("/models/load", LoadModelAsync);
        api.MapPost("/models/unload", UnloadModelAsync);
        api.MapPost("/models/pull", PullModelAsync);
        api.MapPost("/models/quantize", QuantizeModelAsync);
        api.MapPost("/models/import", ImportModelAsync);
        api.MapPost("/models/alias", AliasModelAsync);

        api.MapGet("/models/{id:guid}/modelfile", GetModelfileAsync);
        api.MapPut("/models/{id:guid}/modelfile", PutModelfileAsync);
        api.MapGet("/models/jobs/{job_id:guid}", GetModelJobAsync);

        api.MapGet("/jobs", ListJobsAsync);
        api.MapPost("/jobs/{id:guid}/cancel", CancelJobAsync);

        api.MapGet("/keys", ListKeysAsync);
        api.MapPost("/keys", CreateKeyAsync);
        api.MapDelete("/keys/{id:guid}", DeleteKeyAsync);

        api.MapGet("/users", ListUsersAsync);
        api.MapPost("/users", CreateUserAsync);
        api.MapPatch("/users/{id:guid}", PatchUserAsync);
        api.MapDelete("/users/{id:guid}", DeleteUserAsync);

        api.MapGet("/moderation/rules", ListModerationRulesAsync);
        api.MapPost("/moderation/rules", CreateModerationRuleAsync);
        api.MapDelete("/moderation/rules/{id:guid}", DeleteModerationRuleAsync);

        api.MapGet("/about", AboutAsync);

        api.MapGet("/logs/stream", LogsStreamAsync);

        api.MapPost("/backup", BackupAsync);
        api.MapPost("/backup/restore", RestoreAsync);
        api.MapPost("/restart", RestartAsync);

        api.MapGet("/ab", ListAbTestsAsync);
        api.MapGet("/ab/{id:guid}", GetAbTestAsync);
        api.MapPost("/ab", CreateAbTestAsync);
        api.MapPost("/ab/vote", VoteAbTestAsync);

        api.MapGet("/tenants", ListTenantsAsync);
        api.MapGet("/tenants/{id}", GetTenantAsync);
        api.MapPost("/tenants", CreateTenantAsync);

        api.MapGet("/adapters", ListAdaptersAsync);
        api.MapGet("/adapters/{id:guid}", GetAdapterAsync);
        api.MapPost("/adapters", CreateAdapterAsync);
        api.MapDelete("/adapters/{id:guid}", DeleteAdapterAsync);

        return app;
    }

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

    private static IResult PrometheusMetricsAsync(EngineHostService engineHost)
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

        return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static async Task<IResult> GetSettingsAsync(SettingsService settings, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        return Results.Json(ToSettingsDto(s), JsonOptions);
    }

    private static async Task<IResult> PostSettingsAsync(SettingsDto body, SettingsService settings, CancellationToken ct)
    {
        var updated = await settings.UpdateAsync(s => ApplySettings(s, body, replace: true), ct).ConfigureAwait(false);
        return Results.Json(ToSettingsDto(updated), JsonOptions);
    }

    private static async Task<IResult> PatchSettingsAsync(SettingsDto body, SettingsService settings, CancellationToken ct)
    {
        var updated = await settings.UpdateAsync(s => ApplySettings(s, body, replace: false), ct).ConfigureAwait(false);
        return Results.Json(ToSettingsDto(updated), JsonOptions);
    }

    private static async Task<IResult> SearchLibraryAsync(
        HuggingFaceCatalogService hf,
        string? q,
        CancellationToken ct)
    {
        var hits = await hf.SearchAsync(q ?? "exl3", 40, ct).ConfigureAwait(false);
        return Results.Json(new
        {
            query = string.IsNullOrWhiteSpace(q) ? "exl3" : q,
            results = hits.Select(h => new
            {
                repo_id = h.RepoId,
                display_name = h.DisplayName,
                downloads = h.Downloads,
                pipeline_tag = h.PipelineTag,
                tags = h.Tags,
                parameters = h.ParameterLabel,
                size_bytes = h.SizeBytes,
                size_label = h.SizeBytes is > 0 ? HuggingFaceCatalogService.FormatBytes(h.SizeBytes.Value) : null,
            }),
        }, JsonOptions);
    }

    private static async Task<IResult> GetModelLibraryAsync(
        HttpContext http,
        AppDbContext db,
        SettingsService settings,
        CancellationToken ct)
    {
        var filter = await TenantScope.EffectiveFilterAsync(http, settings, ct).ConfigureAwait(false);
        var catalog = await db.ModelLibrary.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var installedQuery = db.Models.AsNoTracking().AsQueryable();
        if (filter is not null)
        {
            installedQuery = installedQuery.Where(m => m.TenantId == filter || m.Shared);
        }

        var installed = await installedQuery.ToListAsync(ct).ConfigureAwait(false);

        return Results.Json(new
        {
            catalog = catalog.Select(ToLibraryDto),
            installed = installed.Select(ToModelRecordDto),
        }, JsonOptions);
    }

    private static async Task<IResult> PostModelLibraryAsync(
        ModelLibraryRegisterRequest body,
        AppDbContext db,
        SettingsService settings,
        CancellationToken ct)
    {
        if (body.Scan)
        {
            var cfg = await settings.GetAsync(ct).ConfigureAwait(false);
            var root = string.IsNullOrWhiteSpace(body.Path) ? cfg.ModelsPath : body.Path!;
            var added = new List<ModelRecordDto>();
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var exists = await db.Models.AnyAsync(m => m.Path == dir, ct).ConfigureAwait(false);
                    if (exists)
                    {
                        continue;
                    }

                    var record = new ModelRecord
                    {
                        Path = dir,
                        Alias = body.Alias ?? Path.GetFileName(dir),
                        CreatedAt = DateTime.UtcNow,
                    };
                    db.Models.Add(record);
                    added.Add(ToModelRecordDto(record));
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return Results.Json(new { scanned = root, added }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(body.Path))
        {
            return Results.Json(
                ErrorResponse.Create("path is required when scan=false", code: "invalid_path"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var model = new ModelRecord
        {
            Path = body.Path,
            Alias = body.Alias,
            CreatedAt = DateTime.UtcNow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToModelRecordDto(model), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> LoadModelAsync(
        ModelLoadRequest body,
        AppDbContext db,
        EngineHostService engine,
        ModelInventoryService inventory,
        CancellationToken ct)
    {
        ModelRecord? record = null;
        if (body.ModelId is Guid id)
        {
            record = await db.Models.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(body.Alias))
        {
            record = await db.Models.FirstOrDefaultAsync(m => m.Alias == body.Alias, ct).ConfigureAwait(false);
        }

        var path = body.Path ?? record?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.Json(
                ErrorResponse.Create("model_id, alias, or path required", code: "invalid_model"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        record ??= await inventory.EnsureRecordAsync(path, body.Alias, ct).ConfigureAwait(false);
        try
        {
            await engine.LoadAsync(path, record.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Json(
                ErrorResponse.Create(ex.Message, "server_error", "model_load_failed"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new
        {
            loaded = true,
            path,
            model_id = record.Id,
        }, JsonOptions);
    }

    private static async Task<IResult> UnloadModelAsync(EngineHostService engine, CancellationToken ct)
    {
        await engine.UnloadAsync(ct).ConfigureAwait(false);
        return Results.Json(new { unloaded = true }, JsonOptions);
    }

    private static async Task<IResult> PullModelAsync(ModelPullRequest body, ModelJobsService jobs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.RepoId))
        {
            return Results.Json(
                ErrorResponse.Create("repo_id is required", code: "invalid_repo"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var job = await jobs.EnqueuePullAsync(body.RepoId, body.Branch, body.Quantize == true, ct).ConfigureAwait(false);
        return Results.Json(new JobCreatedResponse { JobId = job.JobId }, JsonOptions, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> QuantizeModelAsync(ModelQuantizeRequest body, ModelJobsService jobs, CancellationToken ct)
    {
        var job = await jobs.EnqueueQuantizeAsync(body.ModelId, body.Bits ?? 4.0, ct).ConfigureAwait(false);
        return Results.Json(new JobCreatedResponse { JobId = job.JobId }, JsonOptions, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ImportModelAsync(ModelImportRequest body, ModelJobsService jobs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SourcePath))
        {
            return Results.Json(
                ErrorResponse.Create("source_path is required", code: "invalid_path"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var job = await jobs.EnqueueImportAsync(body.SourcePath, ct).ConfigureAwait(false);
        return Results.Json(new JobCreatedResponse { JobId = job.JobId }, JsonOptions, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> AliasModelAsync(ModelAliasRequest body, AppDbContext db, CancellationToken ct)
    {
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == body.ModelId, ct).ConfigureAwait(false);
        if (model is null)
        {
            return Results.Json(
                ErrorResponse.Create("Model not found", code: "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        model.Alias = body.Alias;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToModelRecordDto(model), JsonOptions);
    }

    private static async Task<IResult> GetModelfileAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (model is null)
        {
            return Results.Json(
                ErrorResponse.Create("Model not found", code: "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new ModelfileDto
        {
            Json = model.ModelfileJson,
            Content = model.ModelfileJson ?? string.Empty,
        }, JsonOptions);
    }

    private static async Task<IResult> PutModelfileAsync(Guid id, ModelfileDto body, AppDbContext db, CancellationToken ct)
    {
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (model is null)
        {
            return Results.Json(
                ErrorResponse.Create("Model not found", code: "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        model.ModelfileJson = body.Json ?? body.Content;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(new ModelfileDto
        {
            Json = model.ModelfileJson,
            Content = model.ModelfileJson ?? string.Empty,
        }, JsonOptions);
    }

    private static async Task<IResult> GetModelJobAsync(Guid job_id, ModelJobsService jobs, CancellationToken ct)
    {
        var job = await jobs.GetAsync(job_id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return Results.Json(
                ErrorResponse.Create("Job not found", code: "job_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(ToJobDto(job, jobs), JsonOptions);
    }

    private static async Task<IResult> ListJobsAsync(ModelJobsService jobs, CancellationToken ct)
    {
        var list = await jobs.ListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = list.Select(j => ToJobDto(j, jobs)) }, JsonOptions);
    }

    private static async Task<IResult> CancelJobAsync(Guid id, ModelJobsService jobs, EngineHostService engine, CancellationToken ct)
    {
        var cancelled = await jobs.CancelAsync(id, ct).ConfigureAwait(false);
        if (!cancelled && engine.IsLoaded)
        {
            try
            {
                cancelled = engine.Engine.Cancel(id);
            }
            catch
            {
                cancelled = false;
            }
        }

        return cancelled
            ? Results.Json(new { cancelled = true, job_id = id }, JsonOptions)
            : Results.Json(
                ErrorResponse.Create("Job not found or already finished", code: "job_not_found"),
                statusCode: StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListKeysAsync(
        HttpContext http,
        AppDbContext db,
        SettingsService settings,
        CancellationToken ct)
    {
        var filter = await TenantScope.EffectiveFilterAsync(http, settings, ct).ConfigureAwait(false);
        var query = db.ApiKeys.AsNoTracking().AsQueryable();
        if (filter is not null)
        {
            query = query.Where(k => k.TenantId == filter);
        }

        var keys = await query.OrderBy(k => k.Name).ToListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = keys.Select(ToKeyDto) }, JsonOptions);
    }

    private static async Task<IResult> CreateKeyAsync(
        CreateApiKeyRequest body,
        AppDbContext db,
        KeyCacheService keyCache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Json(
                ErrorResponse.Create("name is required", code: "invalid_name"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var raw = "sk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hash = ApiKeyHasher.Hash(raw);
        var entity = new ApiKey
        {
            Name = body.Name,
            KeyHash = hash,
            KeyPrefix = raw[..Math.Min(8, raw.Length)],
            Scopes = body.Scopes ?? "chat,completions",
            Rpm = body.Rpm ?? 60,
            Tpm = body.Tpm ?? 100_000,
            Priority = Math.Clamp(body.Priority ?? 5, 1, 10),
            CostPerMillionTokens = body.CostPerMillionTokens ?? 0,
            ExpiresAt = body.ExpiresAt,
            UserId = body.UserId,
            TenantId = body.TenantId ?? "default",
        };

        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        keyCache.Invalidate(hash);

        var dto = ToKeyDto(entity) with { Key = raw };
        return Results.Json(dto, JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteKeyAsync(
        Guid id,
        AppDbContext db,
        KeyCacheService keyCache,
        CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct).ConfigureAwait(false);
        if (key is null)
        {
            return Results.Json(
                ErrorResponse.Create("Key not found", code: "key_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        key.Revoked = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        keyCache.Invalidate(key.KeyHash);
        return Results.Json(new { deleted = true, id }, JsonOptions);
    }

    private static async Task<IResult> ListUsersAsync(AppDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = users.Select(ToUserDto) }, JsonOptions);
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest body, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        {
            return Results.Json(
                ErrorResponse.Create("username and password required", code: "invalid_user"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var exists = await db.Users.AnyAsync(u => u.Username == body.Username, ct).ConfigureAwait(false);
        if (exists)
        {
            return Results.Json(
                ErrorResponse.Create("Username already exists", code: "user_exists"),
                statusCode: StatusCodes.Status409Conflict);
        }

        var user = new User
        {
            Username = body.Username,
            PasswordHash = HashPassword(body.Password),
            Role = body.Role,
            TenantId = body.TenantId ?? "default",
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToUserDto(user), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PatchUserAsync(Guid id, PatchUserRequest body, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Json(
                ErrorResponse.Create("User not found", code: "user_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!string.IsNullOrWhiteSpace(body.Password))
        {
            user.PasswordHash = HashPassword(body.Password);
        }

        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            user.Role = body.Role;
        }

        if (!string.IsNullOrWhiteSpace(body.TenantId))
        {
            user.TenantId = body.TenantId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToUserDto(user), JsonOptions);
    }

    private static async Task<IResult> DeleteUserAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Json(
                ErrorResponse.Create("User not found", code: "user_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(new { deleted = true, id }, JsonOptions);
    }

    private static async Task<IResult> ListModerationRulesAsync(AppDbContext db, CancellationToken ct)
    {
        var rules = await db.ModerationRules.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = rules.Select(ToModerationDto) }, JsonOptions);
    }

    private static async Task<IResult> CreateModerationRuleAsync(
        ModerationRuleRequest body,
        AppDbContext db,
        ContentModerationService moderation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Pattern))
        {
            return Results.Json(
                ErrorResponse.Create("pattern is required", code: "invalid_pattern"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rule = new ModerationRule
        {
            Pattern = body.Pattern,
            Action = body.Action,
            Category = body.Category,
            Enabled = body.Enabled,
            CreatedAt = DateTime.UtcNow,
        };
        db.ModerationRules.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        moderation.InvalidateCache();
        return Results.Json(ToModerationDto(rule), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteModerationRuleAsync(
        Guid id,
        AppDbContext db,
        ContentModerationService moderation,
        CancellationToken ct)
    {
        var rule = await db.ModerationRules.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (rule is null)
        {
            return Results.Json(
                ErrorResponse.Create("Rule not found", code: "rule_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        db.ModerationRules.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        moderation.InvalidateCache();
        return Results.Json(new { deleted = true, id }, JsonOptions);
    }

    private static IResult AboutAsync(AboutService about)
    {
        var info = about.GetAbout();
        return Results.Json(new AboutResponse
        {
            Version = info.Version,
            Build = info.BuildDate?.ToString("O"),
            Runtime = info.Runtime.FrameworkDescription,
            Os = info.Runtime.Os,
            Engine = info.Engine.Name,
            IsMock = info.Engine.IsMock,
            Cuda = info.Gpu.ComputeCapability,
            Gpus = info.Gpu.Available
                ?
                [
                    new GpuInfoDto
                    {
                        Index = 0,
                        Name = info.Gpu.Name ?? "unknown",
                        VramTotalMb = info.Gpu.VramTotalMb is double mb ? (long)mb : null,
                    },
                ]
                : [],
        }, JsonOptions);
    }

    private static async Task LogsStreamAsync(HttpContext http, LiveLogBuffer buffer)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";

        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(false));

        foreach (var entry in buffer.Snapshot(100))
        {
            var hist = JsonSerializer.Serialize(new
            {
                timestamp = entry.Timestamp,
                level = entry.Level,
                category = entry.Category,
                message = entry.Message,
                exception = entry.Exception,
            }, JsonOptions);
            await writer.WriteAsync($"data: {hist}\n\n").ConfigureAwait(false);
        }

        await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);

        var reader = buffer.Subscribe(http.RequestAborted);
        await foreach (var entry in reader.ReadAllAsync(http.RequestAborted))
        {
            var payload = JsonSerializer.Serialize(new
            {
                timestamp = entry.Timestamp,
                level = entry.Level,
                category = entry.Category,
                message = entry.Message,
                exception = entry.Exception,
            }, JsonOptions);
            await writer.WriteAsync($"data: {payload}\n\n").ConfigureAwait(false);
            await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> BackupAsync(BackupRequest? body, BackupService backup, CancellationToken ct)
    {
        var history = await backup.ExportAsync(body?.Path, kind: "manual", cancellationToken: ct).ConfigureAwait(false);
        return Results.Json(new BackupResponse
        {
            Path = history.Path,
            CreatedAt = history.Timestamp,
            SizeBytes = history.SizeBytes,
        }, JsonOptions);
    }

    private static async Task<IResult> RestoreAsync(RestoreRequest body, BackupService backup, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
        {
            return Results.Json(
                ErrorResponse.Create("path is required", code: "invalid_path"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        await backup.ImportAsync(body.Path, ct).ConfigureAwait(false);
        return Results.Json(new { restored = true, path = body.Path }, JsonOptions);
    }

    private static IResult RestartAsync(IHostApplicationLifetime lifetime)
    {
        // Soft restart signal — host process should recycle via Windows Service / systemd.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            lifetime.StopApplication();
        });

        return Results.Json(new { restarting = true }, JsonOptions);
    }

    private static async Task<IResult> ListAbTestsAsync(AppDbContext db, CancellationToken ct)
    {
        var tests = await db.AbTests.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = tests.Select(ToAbTestDto) }, JsonOptions);
    }

    private static async Task<IResult> GetAbTestAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var test = await db.AbTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (test is null)
        {
            return Results.Json(
                ErrorResponse.Create("A/B test not found", code: "ab_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(ToAbTestDto(test), JsonOptions);
    }

    private static async Task<IResult> CreateAbTestAsync(CreateAbTestRequest body, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Json(
                ErrorResponse.Create("name is required", code: "invalid_name"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.ModelAId == Guid.Empty || body.ModelBId == Guid.Empty)
        {
            return Results.Json(
                ErrorResponse.Create("model_a_id and model_b_id are required", code: "invalid_models"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var aExists = await db.Models.AnyAsync(m => m.Id == body.ModelAId, ct).ConfigureAwait(false);
        var bExists = await db.Models.AnyAsync(m => m.Id == body.ModelBId, ct).ConfigureAwait(false);
        if (!aExists || !bExists)
        {
            return Results.Json(
                ErrorResponse.Create("One or both models were not found in the library", code: "model_not_found"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var test = new AbTest
        {
            Name = body.Name.Trim(),
            ModelAId = body.ModelAId,
            ModelBId = body.ModelBId,
            SplitRatio = Math.Clamp(body.SplitRatio ?? 0.5, 0, 1),
            Active = body.Active ?? true,
            TenantId = string.IsNullOrWhiteSpace(body.TenantId) ? "default" : body.TenantId.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.AbTests.Add(test);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToAbTestDto(test), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> VoteAbTestAsync(
        AbVoteRequest body,
        AbTestRouter router,
        AppDbContext db,
        CancellationToken ct)
    {
        var test = await db.AbTests.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == body.AbTestId, ct)
            .ConfigureAwait(false);
        if (test is null)
        {
            return Results.Json(
                ErrorResponse.Create("A/B test not found", code: "ab_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        var requestId = string.IsNullOrWhiteSpace(body.RequestId)
            ? Guid.NewGuid().ToString("N")
            : body.RequestId.Trim();
        var route = router.Route(test, requestId);

        return Results.Json(new
        {
            ok = true,
            ab_test_id = route.AbTestId,
            request_id = requestId,
            variant = route.Variant,
            model_id = route.ModelId,
            preferred = string.IsNullOrWhiteSpace(body.Preferred) ? null : body.Preferred.Trim(),
        }, JsonOptions);
    }

    private static async Task<IResult> ListTenantsAsync(AppDbContext db, CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
        return Results.Json(new { data = tenants.Select(ToTenantDto) }, JsonOptions);
    }

    private static async Task<IResult> GetTenantAsync(string id, AppDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return Results.Json(
                ErrorResponse.Create("Tenant not found", code: "tenant_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(ToTenantDto(tenant), JsonOptions);
    }

    private static async Task<IResult> CreateTenantAsync(CreateTenantRequest body, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Id) || string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Json(
                ErrorResponse.Create("id and name are required", code: "invalid_tenant"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var id = body.Id.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Id == id, ct).ConfigureAwait(false))
        {
            return Results.Json(
                ErrorResponse.Create("Tenant id already exists", code: "tenant_exists"),
                statusCode: StatusCodes.Status409Conflict);
        }

        var tenant = new Tenant
        {
            Id = id,
            Name = body.Name.Trim(),
            Subdomain = string.IsNullOrWhiteSpace(body.Subdomain) ? null : body.Subdomain.Trim(),
            Active = body.Active ?? true,
            CreatedAt = DateTime.UtcNow,
            Quota = new TenantQuota { TenantId = id },
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Json(ToTenantDto(tenant), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAdaptersAsync(
        HttpContext http,
        LoraAdapterService adapters,
        SettingsService settings,
        string? tenant_id,
        CancellationToken ct)
    {
        var filter = tenant_id ?? await TenantScope.EffectiveFilterAsync(http, settings, ct).ConfigureAwait(false);
        var list = await adapters.ListAsync(filter, ct).ConfigureAwait(false);
        return Results.Json(new { data = list.Select(ToAdapterDto) }, JsonOptions);
    }

    private static async Task<IResult> GetAdapterAsync(Guid id, LoraAdapterService adapters, CancellationToken ct)
    {
        var adapter = await adapters.GetAsync(id, ct).ConfigureAwait(false);
        if (adapter is null)
        {
            return Results.Json(
                ErrorResponse.Create("Adapter not found", code: "adapter_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(ToAdapterDto(adapter), JsonOptions);
    }

    private static async Task<IResult> CreateAdapterAsync(
        CreateAdapterRequest body,
        LoraAdapterService adapters,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Json(
                ErrorResponse.Create("name is required", code: "invalid_name"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.BaseModelId == Guid.Empty
            || !await db.Models.AnyAsync(m => m.Id == body.BaseModelId, ct).ConfigureAwait(false))
        {
            return Results.Json(
                ErrorResponse.Create("base_model_id must reference an installed model", code: "model_not_found"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var path = string.IsNullOrWhiteSpace(body.Path)
            ? Path.Combine(adapters.AdaptersDirectory, $"{Guid.NewGuid():N}_{SanitizeFileName(body.Name)}")
            : body.Path.Trim();

        var entity = await adapters.RegisterAsync(
                body.BaseModelId,
                body.Name,
                path,
                body.TenantId ?? "default",
                body.Rank ?? 16,
                body.Alpha ?? 32,
                ct)
            .ConfigureAwait(false);

        return Results.Json(ToAdapterDto(entity), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteAdapterAsync(Guid id, LoraAdapterService adapters, CancellationToken ct)
    {
        var deleted = await adapters.DeleteAsync(id, ct).ConfigureAwait(false);
        if (!deleted)
        {
            return Results.Json(
                ErrorResponse.Create("Adapter not found", code: "adapter_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { deleted = true, id }, JsonOptions);
    }

    // --- mapping helpers ---

    private static string SanitizeFileName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "adapter" : safe;
    }

    private static AbTestDto ToAbTestDto(AbTest t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        ModelAId = t.ModelAId,
        ModelBId = t.ModelBId,
        SplitRatio = t.SplitRatio,
        Active = t.Active,
        TenantId = t.TenantId,
        CreatedAt = t.CreatedAt,
    };

    private static TenantDto ToTenantDto(Tenant t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Subdomain = t.Subdomain,
        Active = t.Active,
        CreatedAt = t.CreatedAt,
    };

    private static AdapterDto ToAdapterDto(LoraAdapter a) => new()
    {
        Id = a.Id,
        BaseModelId = a.BaseModelId,
        Name = a.Name,
        Path = a.Path,
        Rank = a.Rank,
        Alpha = a.Alpha,
        TenantId = a.TenantId,
        CreatedAt = a.CreatedAt,
    };

    private static SettingsDto ToSettingsDto(AppSettings s) => new()
    {
        BindAddress = s.BindAddress,
        Port = s.Port,
        Cors = s.Cors,
        TlsCertPath = s.TlsCertPath,
        MaxNumSeqs = s.MaxNumSeqs,
        MaxChunkSize = s.MaxChunkSize,
        MaxBatchedTokens = s.MaxBatchedTokens,
        GpuMemoryUtilization = s.GpuMemoryUtilization,
        RequestTimeoutSeconds = s.RequestTimeoutSeconds,
        LoadModelOnStartup = s.LoadModelOnStartup,
        LastLoadedModelId = s.LastLoadedModelId,
        AutoBackupSchedule = s.AutoBackupSchedule,
        WebhookUrl = s.WebhookUrl,
        WebhookSecret = s.WebhookSecret,
        ContentModerationEnabled = s.ContentModerationEnabled,
        MultiTenancyEnabled = s.MultiTenancyEnabled,
        ShowAdvancedMetrics = s.ShowAdvancedMetrics,
        CudaVisibleDevices = s.CudaVisibleDevices,
        ParallelismMode = s.ParallelismMode,
        SpeculativeEnabled = s.SpeculativeEnabled,
        DraftModelId = s.DraftModelId,
        DraftK = s.DraftK,
        ModelsPath = s.ModelsPath,
    };

    private static void ApplySettings(AppSettings s, SettingsDto body, bool replace)
    {
        if (body.BindAddress is not null) s.BindAddress = body.BindAddress;
        if (body.Port is int port) s.Port = port;
        if (body.Cors is not null) s.Cors = body.Cors;
        if (body.TlsCertPath is not null || replace) s.TlsCertPath = body.TlsCertPath;
        if (body.MaxNumSeqs is int mns) s.MaxNumSeqs = mns;
        if (body.MaxChunkSize is int mcs) s.MaxChunkSize = mcs;
        if (body.MaxBatchedTokens is int mbt) s.MaxBatchedTokens = mbt;
        if (body.GpuMemoryUtilization is double gpu) s.GpuMemoryUtilization = gpu;
        if (body.RequestTimeoutSeconds is int rts) s.RequestTimeoutSeconds = rts;
        if (body.LoadModelOnStartup is bool lms) s.LoadModelOnStartup = lms;
        if (body.LastLoadedModelId is not null || replace) s.LastLoadedModelId = body.LastLoadedModelId;
        if (body.AutoBackupSchedule is not null) s.AutoBackupSchedule = body.AutoBackupSchedule;
        if (body.WebhookUrl is not null || replace) s.WebhookUrl = body.WebhookUrl;
        if (body.WebhookSecret is not null || replace) s.WebhookSecret = body.WebhookSecret;
        if (body.ContentModerationEnabled is bool cme) s.ContentModerationEnabled = cme;
        if (body.MultiTenancyEnabled is bool mte) s.MultiTenancyEnabled = mte;
        if (body.ShowAdvancedMetrics is bool sam) s.ShowAdvancedMetrics = sam;
        if (body.CudaVisibleDevices is not null) s.CudaVisibleDevices = body.CudaVisibleDevices;
        if (body.ParallelismMode is not null) s.ParallelismMode = body.ParallelismMode;
        if (body.SpeculativeEnabled is bool se) s.SpeculativeEnabled = se;
        if (body.DraftModelId is not null || replace) s.DraftModelId = body.DraftModelId;
        if (body.DraftK is int dk) s.DraftK = dk;
        if (body.ModelsPath is not null) s.ModelsPath = body.ModelsPath;
    }

    private static ModelLibraryEntryDto ToLibraryDto(ModelLibraryEntry e) => new()
    {
        Id = e.Id,
        RepoId = e.RepoId,
        Branch = e.Branch,
        DisplayName = e.DisplayName,
        Description = e.Description,
        Tags = e.Tags,
        Architecture = e.Architecture,
        Parameters = e.Parameters,
        ContextLength = e.ContextLength,
        License = e.License,
        RecommendedBits = e.RecommendedBits,
    };

    private static ModelRecordDto ToModelRecordDto(ModelRecord m) => new()
    {
        Id = m.Id,
        Path = m.Path,
        Alias = m.Alias,
        Architecture = m.Architecture,
        SizeGb = m.SizeGb,
        ContextLength = m.ContextLength,
        QuantMode = m.QuantMode,
        TenantId = m.TenantId,
        Shared = m.Shared,
        CreatedAt = m.CreatedAt,
    };

    private static JobDto ToJobDto(ModelJob j, ModelJobsService jobs)
    {
        jobs.TryGetPullMeta(j.JobId, out var meta);
        string? sizeLabel = null;
        if (meta is not null)
        {
            sizeLabel = meta.BytesTotal > 0
                ? j.Status is "completed"
                    ? HuggingFaceCatalogService.FormatBytes(meta.BytesTotal)
                    : $"{HuggingFaceCatalogService.FormatBytes(meta.BytesDownloaded)} / {HuggingFaceCatalogService.FormatBytes(meta.BytesTotal)}"
                : meta.BytesDownloaded > 0
                    ? HuggingFaceCatalogService.FormatBytes(meta.BytesDownloaded)
                    : null;
        }

        return new JobDto
        {
            JobId = j.JobId,
            Type = j.Type,
            Status = j.Status,
            ProgressPct = j.ProgressPct,
            EtaSeconds = j.EtaSeconds,
            Error = j.Error,
            ParameterLabel = meta?.ParameterLabel,
            BytesDownloaded = meta?.BytesDownloaded,
            BytesTotal = meta?.BytesTotal,
            SizeLabel = sizeLabel,
            ModelId = j.ModelId,
            CreatedAt = j.CreatedAt,
            UpdatedAt = j.UpdatedAt,
        };
    }

    private static ApiKeyDto ToKeyDto(ApiKey k) => new()
    {
        Id = k.Id,
        Name = k.Name,
        KeyPrefix = k.KeyPrefix,
        Scopes = k.Scopes,
        Rpm = k.Rpm,
        Tpm = k.Tpm,
        Priority = k.Priority,
        CostPerMillionTokens = k.CostPerMillionTokens,
        ExpiresAt = k.ExpiresAt,
        Revoked = k.Revoked,
        TenantId = k.TenantId,
        UserId = k.UserId,
    };

    private static UserDto ToUserDto(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Role = u.Role,
        TenantId = u.TenantId,
        CreatedAt = u.CreatedAt,
        LastActive = u.LastActive,
    };

    private static ModerationRuleDto ToModerationDto(ModerationRule r) => new()
    {
        Id = r.Id,
        Pattern = r.Pattern,
        Action = r.Action,
        Category = r.Category,
        Enabled = r.Enabled,
        CreatedAt = r.CreatedAt,
    };

    private static string HashPassword(string password) => PasswordHasher.Hash(password);
}
