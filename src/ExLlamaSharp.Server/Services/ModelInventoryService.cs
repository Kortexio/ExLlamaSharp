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
            var dirty = false;
            if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(alias))
            {
                existing.Alias = alias;
                dirty = true;
            }

            // Older installs often have SizeGb=0 — refresh when missing or path drifted.
            if (existing.SizeGb < 0.05 || !string.Equals(existing.Path, full, StringComparison.OrdinalIgnoreCase))
            {
                existing.Path = full;
                existing.SizeGb = MeasureSizeGb(full);
                existing.QuantMode ??= InferQuant(full);
                dirty = true;
            }

            if (dirty)
            {
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

                var full = Path.GetFullPath(dir);
                var before = await _db.Models.AnyAsync(
                        m => m.Path == full || m.Path == dir,
                        ct)
                    .ConfigureAwait(false);
                await EnsureRecordAsync(full, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), ct)
                    .ConfigureAwait(false);
                if (!before)
                {
                    added++;
                }
            }
        }

        // Refresh sizes for every registered folder (fixes 0.0 GB in My Models).
        await RefreshAllSizesAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_engine.LoadedModelPath)
            && !_engine.LoadedModelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(_engine.LoadedModelPath))
        {
            await EnsureRecordAsync(
                    _engine.LoadedModelPath,
                    Path.GetFileName(_engine.LoadedModelPath.TrimEnd(Path.DirectorySeparatorChar)),
                    ct)
                .ConfigureAwait(false);
        }

        return added;
    }

    public async Task RefreshAllSizesAsync(CancellationToken ct = default)
    {
        var models = await _db.Models.ToListAsync(ct).ConfigureAwait(false);
        var dirty = false;
        foreach (var m in models)
        {
            if (string.IsNullOrWhiteSpace(m.Path) || !Directory.Exists(m.Path))
            {
                continue;
            }

            var size = MeasureSizeGb(m.Path);
            if (Math.Abs(m.SizeGb - size) > 0.01)
            {
                m.SizeGb = size;
                dirty = true;
            }

            m.QuantMode ??= InferQuant(m.Path);
        }

        if (dirty)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<ModelRecord?> DeleteAsync(Guid id, bool deleteFiles, CancellationToken ct = default)
    {
        var model = await _db.Models.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_engine.LoadedModelPath)
            && string.Equals(
                Path.GetFullPath(_engine.LoadedModelPath.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetFullPath(model.Path.TrimEnd(Path.DirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase))
        {
            await _engine.UnloadAsync(ct).ConfigureAwait(false);
        }

        var path = model.Path;
        _db.Models.Remove(model);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (deleteFiles && !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Model removed from library, but files could not be deleted: {ex.Message}", ex);
            }
        }

        return model;
    }

    public async Task<ModelRecord> SetAliasAsync(Guid id, string alias, CancellationToken ct = default)
    {
        var model = await _db.Models.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Model not found");
        model.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return model;
    }

    /// <summary>Rename the on-disk folder (under models root) and update Path/Alias.</summary>
    public async Task<ModelRecord> RenameFolderAsync(Guid id, string newFolderName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newFolderName);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (newFolderName.Contains(c))
            {
                throw new InvalidOperationException("Invalid folder name.");
            }
        }

        var model = await _db.Models.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Model not found");

        if (!Directory.Exists(model.Path))
        {
            throw new InvalidOperationException("Model folder not found on disk.");
        }

        var parent = Path.GetDirectoryName(model.Path.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidOperationException("Cannot resolve parent folder.");
        var dest = Path.Combine(parent, newFolderName.Trim());
        if (Directory.Exists(dest))
        {
            throw new InvalidOperationException("A folder with that name already exists.");
        }

        var loaded = !string.IsNullOrWhiteSpace(_engine.LoadedModelPath)
            && string.Equals(
                Path.GetFullPath(_engine.LoadedModelPath.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetFullPath(model.Path.TrimEnd(Path.DirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase);
        if (loaded)
        {
            await _engine.UnloadAsync(ct).ConfigureAwait(false);
        }

        Directory.Move(model.Path, dest);
        model.Path = Path.GetFullPath(dest);
        if (string.IsNullOrWhiteSpace(model.Alias))
        {
            model.Alias = newFolderName.Trim();
        }

        model.SizeGb = MeasureSizeGb(model.Path);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return model;
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
