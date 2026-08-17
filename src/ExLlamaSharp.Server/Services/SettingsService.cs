using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExLlamaSharp.Server.Services;

public sealed class SettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private AppSettings? _cached;

    public SettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.Settings.AsNoTracking().FirstAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _cached = Clone(settings);
            return Clone(_cached);
        }
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.Settings.FirstAsync(cancellationToken).ConfigureAwait(false);
        mutate(settings);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = Clone(settings);
        lock (_gate)
        {
            _cached = Clone(snapshot);
        }

        return snapshot;
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        Id = s.Id,
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
}
