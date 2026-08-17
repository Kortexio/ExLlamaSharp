using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Endpoints;

namespace ExLlamaSharp.Server;

/// <summary>
/// Wiring helpers for Program / WebApplication host.
/// Call <see cref="UseExLlamaSharpApi"/> then <see cref="MapExLlamaSharpEndpoints"/>.
/// </summary>
public static class EndpointExtensions
{
    /// <summary>Registers API-key auth and rate-limit middleware (order matters).</summary>
    public static IApplicationBuilder UseExLlamaSharpApi(this IApplicationBuilder app)
    {
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseMiddleware<RateLimitMiddleware>();
        return app;
    }

    /// <summary>Maps OpenAI-compatible /v1 and Admin /api/v1 (+ /health, /ready, /metrics).</summary>
    public static IEndpointRouteBuilder MapExLlamaSharpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenAiEndpoints();
        endpoints.MapAdminEndpoints();
        return endpoints;
    }
}
