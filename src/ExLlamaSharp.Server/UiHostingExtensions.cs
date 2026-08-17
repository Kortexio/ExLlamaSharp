using System.Net.Http.Headers;
using ExLlamaSharp.Server.Components;
using ExLlamaSharp.Server.Hubs;
using ExLlamaSharp.Server.Services;
using ExLlamaSharp.Server.Services.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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

        services.AddSingleton<OnboardingState>();
        services.AddSingleton<GpuInfoService>();
        services.AddSingleton<VramFitService>();
        services.AddScoped<ModelInventoryService>();
        services.AddSingleton<HuggingFaceCatalogService>();
        services.AddHttpClient("huggingface", client =>
        {
            client.BaseAddress = new Uri("https://huggingface.co/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("local-api", (sp, client) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var key = cfg["ExLlamaSharp:AdminApiKey"] ?? "sk-exllamasharp-dev";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        });
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
