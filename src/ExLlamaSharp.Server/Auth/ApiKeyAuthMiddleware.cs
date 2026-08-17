using System.Net;
using System.Text;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Auth;

/// <summary>
/// Validates Bearer API keys for /v1 and admin-scoped keys (or Basic later) for /api/v1.
/// Sets HttpContext items: KeyId, Scopes, Priority, TenantId.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private static readonly PathString OpenAiPrefix = new("/v1");
    private static readonly PathString AdminPrefix = new("/api/v1");

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, KeyCacheService keyCache)
    {
        var path = context.Request.Path;

        // Public health / about / prometheus text metrics
        if (IsAnonymous(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var isOpenAi = path.StartsWithSegments(OpenAiPrefix, out _);
        var isAdmin = path.StartsWithSegments(AdminPrefix, out _);

        if (!isOpenAi && !isAdmin)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var rawKey = ExtractBearerOrBasic(context.Request);
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            await WriteUnauthorizedAsync(context, "Missing API key. Use Authorization: Bearer <key>.").ConfigureAwait(false);
            return;
        }

        var hash = ApiKeyHasher.Hash(rawKey);
        var apiKey = await keyCache.GetAsync(hash, context.RequestAborted).ConfigureAwait(false);
        if (apiKey is null)
        {
            _logger.LogWarning("Invalid API key attempt on {Path}", path);
            await WriteUnauthorizedAsync(context, "Invalid API key.").ConfigureAwait(false);
            return;
        }

        var scopes = apiKey.Scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        context.Items[AuthContextKeys.KeyId] = apiKey.Id;
        context.Items[AuthContextKeys.Scopes] = scopes;
        context.Items[AuthContextKeys.Priority] = apiKey.Priority;
        context.Items[AuthContextKeys.TenantId] = apiKey.TenantId;
        context.Items[AuthContextKeys.Rpm] = apiKey.Rpm;
        context.Items[AuthContextKeys.Tpm] = apiKey.Tpm;
        context.Items[AuthContextKeys.KeyHash] = hash;

        var hasAdmin = scopes.Any(s => string.Equals(s, "admin", StringComparison.OrdinalIgnoreCase));
        context.Items[AuthContextKeys.IsAdmin] = hasAdmin;

        if (isAdmin && !hasAdmin)
        {
            await WriteForbiddenAsync(context, "Admin scope required.").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsAnonymous(PathString path)
    {
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/about", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? ExtractBearerOrBasic(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            // Optional cookie for admin UI later
            if (request.Cookies.TryGetValue("exllamasharp_key", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            {
                return cookie;
            }

            return null;
        }

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // Placeholder for UI Basic auth: username:password where password is the API key
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var idx = decoded.IndexOf(':');
                return idx >= 0 ? decoded[(idx + 1)..] : decoded;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(ErrorResponse.Create(message, "authentication_error", "invalid_api_key"))
            .ConfigureAwait(false);
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        await context.Response.WriteAsJsonAsync(ErrorResponse.Create(message, "permission_error", "insufficient_scope"))
            .ConfigureAwait(false);
    }
}
