using ExLlamaSharp.Server.Services;
using ExLlamaSharp.Server.Services.Ui;

namespace ExLlamaSharp.Server;

public static class ExLlamaSharpServerServiceCollectionExtensions
{
    public static IServiceCollection AddExLlamaSharpCore(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpClient("github", client => client.Timeout = TimeSpan.FromSeconds(15));

        services.AddSingleton<LiveLogBuffer>();
        services.AddSingleton<KeyCacheService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AbTestRouter>();
        services.AddSingleton<TenantResolver>();
        services.AddSingleton<ContentModerationService>();
        services.AddSingleton<MetricsHistoryService>();
        services.AddSingleton<EmbeddingService>();
        services.AddSingleton<AboutService>();
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<EngineRuntimeVersionService>();
        services.AddSingleton<HealthService>();
        services.AddSingleton<WebhookService>();
        services.AddSingleton<ModelJobsService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<MultiGpuPlanner>();
        services.AddSingleton<ArchitectureDetector>();
        services.AddSingleton<LoraAdapterService>();
        services.AddSingleton<PythonModelTools>();
        services.AddSingleton<IGpuInventory, GpuInventoryService>();
        services.AddSingleton<EngineHostService>();
        services.AddHostedService(sp => sp.GetRequiredService<EngineHostService>());
        services.AddSingleton<AuditService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditService>());
        services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
        services.AddHostedService<DashboardBroadcastService>();

        return services;
    }
}