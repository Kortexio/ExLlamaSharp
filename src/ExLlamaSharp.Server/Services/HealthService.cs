using System.Text.Json;
using ExLlamaSharp.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

public sealed class HealthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EngineHostService _engineHost;
    private readonly SettingsService _settings;
    private readonly ILogger<HealthService> _logger;

    public HealthService(
        IServiceScopeFactory scopeFactory,
        EngineHostService engineHost,
        SettingsService settings,
        ILogger<HealthService> logger)
    {
        _scopeFactory = scopeFactory;
        _engineHost = engineHost;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HealthReport> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var database = await CheckDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var engine = CheckEngine();
        var inference = CheckInference();
        var disk = await CheckDiskAsync(cancellationToken).ConfigureAwait(false);

        var components = new[] { database, engine, inference, disk };
        var overall = components.All(c => c.Status == "healthy")
            ? "healthy"
            : components.Any(c => c.Status == "unhealthy")
                ? "unhealthy"
                : "degraded";

        return new HealthReport
        {
            Status = overall,
            Timestamp = DateTime.UtcNow,
            Components = new Dictionary<string, ComponentHealth>(StringComparer.OrdinalIgnoreCase)
            {
                ["database"] = database,
                ["engine"] = engine,
                ["inference"] = inference,
                ["disk"] = disk,
            },
        };
    }

    public async Task<string> GetHealthJsonAsync(CancellationToken cancellationToken = default)
    {
        var report = await GetHealthAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        var report = await GetHealthAsync(cancellationToken).ConfigureAwait(false);
        if (report.Components["database"].Status != "healthy")
        {
            return false;
        }

        if (ProductionRuntime.IsSessionZero() && !ProductionRuntime.IsHeadless)
        {
            return report.Status is "healthy" or "degraded";
        }

        return _engineHost.IsLoaded && _engineHost.IsRunning && !_engineHost.Engine.IsMock;
    }

    private async Task<ComponentHealth> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var canConnect = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return new ComponentHealth
            {
                Status = canConnect ? "healthy" : "unhealthy",
                Detail = canConnect ? "SQLite reachable" : "Cannot connect",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database health check failed");
            return new ComponentHealth { Status = "unhealthy", Detail = ex.Message };
        }
    }

    private ComponentHealth CheckEngine()
    {
        try
        {
            var engine = _engineHost.Engine;
            var session0 = ProductionRuntime.IsSessionZero();
            var lastError = _engineHost.LastLoadError;
            var mock = engine.IsMock;
            var status = mock || (session0 && !ProductionRuntime.IsHeadless)
                ? "unhealthy"
                : "healthy";
            var detail = mock
                ? "MockEngine is not allowed in production"
                : session0 && !ProductionRuntime.IsHeadless
                    ? "Session 0 / LocalSystem — GPU load is refused. Use Tray (desktop) or a GPU service account (headless)."
                    : string.IsNullOrWhiteSpace(lastError)
                        ? engine.GetType().Name
                        : lastError;
            return new ComponentHealth
            {
                Status = status,
                Detail = detail,
                Data = new Dictionary<string, object?>
                {
                    ["isMock"] = mock,
                    ["isLoaded"] = engine.IsLoaded,
                    ["isRunning"] = engine.IsRunning,
                    ["isLoading"] = _engineHost.IsLoading,
                    ["sessionZero"] = session0,
                    ["hostMode"] = ProductionRuntime.ReadHostMode(),
                    ["lastError"] = lastError,
                },
            };
        }
        catch (Exception ex)
        {
            return new ComponentHealth { Status = "unhealthy", Detail = ex.Message };
        }
    }

    private ComponentHealth CheckInference()
    {
        try
        {
            if (!_engineHost.IsLoaded)
            {
                return new ComponentHealth
                {
                    Status = "degraded",
                    Detail = "No model loaded",
                };
            }

            var metrics = _engineHost.Engine.GetMetrics();
            return new ComponentHealth
            {
                Status = "healthy",
                Detail = "Metrics OK",
                Data = new Dictionary<string, object?>
                {
                    ["tokensPerSecond"] = metrics.TokensPerSecond,
                    ["jobsWaiting"] = metrics.NumJobsWaiting,
                    ["jobsRunning"] = metrics.NumJobsRunning,
                },
            };
        }
        catch (Exception ex)
        {
            return new ComponentHealth { Status = "unhealthy", Detail = ex.Message };
        }
    }

    private async Task<ComponentHealth> CheckDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var path = string.IsNullOrWhiteSpace(settings.ModelsPath)
                ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"
                : settings.ModelsPath;

            Directory.CreateDirectory(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

            return new ComponentHealth
            {
                Status = freeGb < 5 ? "unhealthy" : freeGb < 20 ? "degraded" : "healthy",
                Detail = $"{freeGb:F1} GB free on {drive.Name}",
                Data = new Dictionary<string, object?>
                {
                    ["freeGb"] = Math.Round(freeGb, 2),
                    ["totalGb"] = Math.Round(drive.TotalSize / (1024.0 * 1024.0 * 1024.0), 2),
                    ["modelsPath"] = path,
                },
            };
        }
        catch (Exception ex)
        {
            return new ComponentHealth { Status = "unhealthy", Detail = ex.Message };
        }
    }
}

public sealed class HealthReport
{
    public string Status { get; init; } = "unknown";
    public DateTime Timestamp { get; init; }
    public Dictionary<string, ComponentHealth> Components { get; init; } = new();
}

public sealed class ComponentHealth
{
    public string Status { get; init; } = "unknown";
    public string? Detail { get; init; }
    public Dictionary<string, object?>? Data { get; init; }
}
