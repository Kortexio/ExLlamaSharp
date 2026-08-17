using System.IO.Compression;
using System.Text.Json;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

public sealed class BackupService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IServiceScopeFactory scopeFactory,
        SettingsService settings,
        ILogger<BackupService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    public string BackupDirectory
    {
        get
        {
            var dataRoot = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ExLlamaSharp");
            }

            var dir = Path.Combine(dataRoot, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<BackupHistory> ExportAsync(string? destinationPath = null, string kind = "manual", CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payload = new BackupPayload
        {
            ExportedAt = DateTime.UtcNow,
            Settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            Users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            ApiKeys = await db.ApiKeys.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            Tenants = await db.Tenants.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            TenantQuotas = await db.TenantQuotas.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            Models = await db.Models.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            ModerationRules = await db.ModerationRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            AbTests = await db.AbTests.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        };

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var zipPath = destinationPath ?? Path.Combine(BackupDirectory, $"{kind}_{stamp}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await using (var zipStream = File.Create(zipPath))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("backup.json");
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(json).ConfigureAwait(false);
        }

        var info = new FileInfo(zipPath);
        var history = new BackupHistory
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Path = zipPath,
            SizeBytes = info.Length,
            Kind = kind,
        };

        db.BackupHistory.Add(history);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RotateAsync(keep: 7, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Backup written to {Path} ({Bytes} bytes)", zipPath, info.Length);
        return history;
    }

    public async Task ImportAsync(string zipOrJsonPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipOrJsonPath);
        if (!File.Exists(zipOrJsonPath))
        {
            throw new FileNotFoundException("Backup file not found", zipOrJsonPath);
        }

        string json;
        if (zipOrJsonPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(zipOrJsonPath);
            var entry = archive.GetEntry("backup.json")
                ?? throw new InvalidOperationException("backup.json missing from archive");
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            json = await File.ReadAllTextAsync(zipOrJsonPath, cancellationToken).ConfigureAwait(false);
        }

        var payload = JsonSerializer.Deserialize<BackupPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Invalid backup payload");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (payload.Settings is not null)
        {
            var existing = await db.Settings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                db.Settings.Add(payload.Settings);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(payload.Settings);
                existing.Id = 1;
            }
        }

        ReplaceRange(db.ModerationRules, await db.ModerationRules.ToListAsync(cancellationToken).ConfigureAwait(false), payload.ModerationRules);
        ReplaceRange(db.Users, await db.Users.ToListAsync(cancellationToken).ConfigureAwait(false), payload.Users);
        ReplaceRange(db.ApiKeys, await db.ApiKeys.ToListAsync(cancellationToken).ConfigureAwait(false), payload.ApiKeys);
        ReplaceRange(db.Models, await db.Models.ToListAsync(cancellationToken).ConfigureAwait(false), payload.Models);
        ReplaceRange(db.AbTests, await db.AbTests.ToListAsync(cancellationToken).ConfigureAwait(false), payload.AbTests);
        if (payload.Tenants is { Count: > 0 })
        {
            ReplaceRange(db.TenantQuotas, await db.TenantQuotas.ToListAsync(cancellationToken).ConfigureAwait(false), payload.TenantQuotas);
            ReplaceRange(db.Tenants, await db.Tenants.ToListAsync(cancellationToken).ConfigureAwait(false), payload.Tenants);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _settings.InvalidateCache();

        _logger.LogInformation("Backup restored from {Path}", zipOrJsonPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var settings = await _settings.GetAsync(stoppingToken).ConfigureAwait(false);
                var schedule = settings.AutoBackupSchedule?.ToLowerInvariant() ?? "disabled";
                if (schedule is "disabled" or "")
                {
                    continue;
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var last = await db.BackupHistory
                    .AsNoTracking()
                    .Where(b => b.Kind == "scheduled")
                    .OrderByDescending(b => b.Timestamp)
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                var due = schedule switch
                {
                    "daily" => last is null || last.Timestamp <= DateTime.UtcNow.AddDays(-1),
                    "weekly" => last is null || last.Timestamp <= DateTime.UtcNow.AddDays(-7),
                    _ => false,
                };

                if (due)
                {
                    await ExportAsync(kind: "scheduled", cancellationToken: stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled backup failed");
            }
        }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutting down
        }
    }

    private async Task RotateAsync(int keep, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var old = await db.BackupHistory
            .OrderByDescending(b => b.Timestamp)
            .Skip(keep)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in old)
        {
            try
            {
                if (File.Exists(item.Path))
                {
                    File.Delete(item.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old backup {Path}", item.Path);
            }

            db.BackupHistory.Remove(item);
        }

        if (old.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ReplaceRange<T>(DbSet<T> set, IEnumerable<T> existing, List<T>? incoming)
        where T : class
    {
        set.RemoveRange(existing);
        if (incoming is { Count: > 0 })
        {
            set.AddRange(incoming);
        }
    }

    private sealed class BackupPayload
    {
        public DateTime ExportedAt { get; set; }
        public AppSettings? Settings { get; set; }
        public List<User>? Users { get; set; }
        public List<ApiKey>? ApiKeys { get; set; }
        public List<Tenant>? Tenants { get; set; }
        public List<TenantQuota>? TenantQuotas { get; set; }
        public List<ModelRecord>? Models { get; set; }
        public List<ModerationRule>? ModerationRules { get; set; }
        public List<AbTest>? AbTests { get; set; }
    }
}
