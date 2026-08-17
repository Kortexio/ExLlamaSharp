using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// LoRA adapter registry against <see cref="AppDbContext"/>.
/// Upload currently records metadata + saves bytes under ProgramData adapters folder (stub load path).
/// </summary>
public sealed class LoraAdapterService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LoraAdapterService> _logger;

    public LoraAdapterService(IServiceScopeFactory scopeFactory, ILogger<LoraAdapterService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string AdaptersDirectory
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

            var dir = Path.Combine(dataRoot, "adapters");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<IReadOnlyList<LoraAdapter>> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.LoraAdapters.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(a => a.TenantId == tenantId);
        }

        return await query.OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoraAdapter?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LoraAdapters.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoraAdapter> UploadStubAsync(
        Guid baseModelId,
        string name,
        Stream content,
        string? fileName = null,
        string tenantId = "default",
        int rank = 16,
        double alpha = 32,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        var id = Guid.NewGuid();
        var safeName = string.Join("_", (fileName ?? name).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = id.ToString("N");
        }

        var targetPath = Path.Combine(AdaptersDirectory, $"{id:N}_{safeName}");
        await using (var fs = File.Create(targetPath))
        {
            await content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        var entity = new LoraAdapter
        {
            Id = id,
            BaseModelId = baseModelId,
            Name = name.Trim(),
            Path = targetPath,
            Rank = rank,
            Alpha = alpha,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId,
            CreatedAt = DateTime.UtcNow,
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LoraAdapters.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("LoRA adapter stub registered {Id} → {Path}", entity.Id, entity.Path);
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.LoraAdapters.FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        try
        {
            if (File.Exists(entity.Path))
            {
                File.Delete(entity.Path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete adapter file {Path}", entity.Path);
        }

        db.LoraAdapters.Remove(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
