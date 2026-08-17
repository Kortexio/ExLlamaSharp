using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Keeps <see cref="ModelRecord"/> in sync with folders on disk and the currently loaded engine path.
/// </summary>
public sealed class ModelInventoryService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly EngineHostService _engine;

    public ModelInventoryService(AppDbContext db, SettingsService settings, EngineHostService engine)
    {
        _db = db;
        _settings = settings;
        _engine = engine;
    }

    public async Task<ModelRecord> EnsureRecordAsync(string path, string? alias = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);

        var existing = await _db.Models
            .FirstOrDefaultAsync(m => m.Path == full || m.Path == path, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(alias))
            {
                existing.Alias = alias;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return existing;
        }

        var rec = new ModelRecord
        {
            Path = full,
            Alias = string.IsNullOrWhiteSpace(alias) ? Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) : alias,
            QuantMode = InferQuant(full),
            SizeGb = MeasureSizeGb(full),
            TenantId = "default",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Models.Add(rec);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rec;
    }

    public async Task<int> SyncFromDiskAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
        var added = 0;

        if (!string.IsNullOrWhiteSpace(settings.ModelsPath) && Directory.Exists(settings.ModelsPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(settings.ModelsPath))
            {
                if (!File.Exists(Path.Combine(dir, "config.json")))
                {
                    continue;
                }

                var before = await _db.Models.AnyAsync(m => m.Path == dir, ct).ConfigureAwait(false);
                await EnsureRecordAsync(dir, Path.GetFileName(dir), ct).ConfigureAwait(false);
                if (!before)
                {
                    added++;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_engine.LoadedModelPath)
            && !_engine.LoadedModelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(_engine.LoadedModelPath))
        {
            await EnsureRecordAsync(_engine.LoadedModelPath, Path.GetFileName(_engine.LoadedModelPath.TrimEnd(Path.DirectorySeparatorChar)), ct)
                .ConfigureAwait(false);
        }

        return added;
    }

    public static string InferQuant(string path)
    {
        var name = path.Replace('\\', '/').ToLowerInvariant();
        if (name.Contains("exl3")) return "exl3";
        if (name.Contains("exl2")) return "exl2";
        if (name.Contains("awq")) return "awq";
        if (name.Contains("gptq")) return "gptq";
        if (name.Contains("fp8")) return "fp8";
        if (name.Contains("int8")) return "int8";
        return "exl3";
    }

    public static double MeasureSizeGb(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // skip locked files
                }
            }

            return bytes / (1024d * 1024d * 1024d);
        }
        catch
        {
            return 0;
        }
    }
}
