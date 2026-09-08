using ExLlamaSharp.Server.Components;
using ExLlamaSharp.Server.Hubs;
using ExLlamaSharp.Server.Services;
using ExLlamaSharp.Server.Services.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace ExLlamaSharp.Server;

/// <summary>
/// Optional wiring helpers for the Blazor admin UI.
/// Call from WebApplication Program when migrating off the Worker host:
/// <code>
/// builder.Services.AddExLlamaSharpUi();
/// ...
/// app.MapExLlamaSharpUi();
/// </code>
/// </summary>
public static class UiHostingExtensions
{
    public static IServiceCollection AddExLlamaSharpUi(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
        {
            // Model load / long admin ops should not drop the Blazor circuit immediately.
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
            options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(5);
            options.DetailedErrors = true;
        });

        services.AddHttpContextAccessor();
        services.AddSingleton<OnboardingState>();
        services.AddSingleton<GpuInfoService>();
        services.AddSingleton<VramFitService>();
        services.AddScoped<ModelInventoryService>();
        services.AddScoped<AdminUiSession>();
        services.AddScoped<CircuitHandler, AdminUiCircuitHandler>();
        services.AddTransient<LocalApiAuthHandler>();
        services.AddSingleton<HuggingFaceCatalogService>();
        services.AddHttpClient("huggingface", client =>
        {
            client.BaseAddress = new Uri("https://huggingface.co/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("local-api", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            .AddHttpMessageHandler<LocalApiAuthHandler>();
        services.AddSignalR();

        return services;
    }

    public static WebApplication MapExLlamaSharpUi(this WebApplication app)
    {
        app.UseAntiforgery();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapHub<DashboardHub>("/hubs/dashboard");
        return app;
    }
}
