using System.Net;
using ExLlamaSharp.Server.Models;
using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Auth;

/// <summary>
/// Enforces per-key RPM/TPM via <see cref="RateLimiter"/>. Returns 429 + Retry-After when denied.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public RateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, RateLimiter rateLimiter)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/v1") && !path.StartsWithSegments("/api/v1"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Skip anonymous routes already allowed by auth
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/about", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var keyId = context.GetKeyId();
        if (keyId is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var rpm = context.GetRpm();
        var tpm = context.GetTpm();
        var estimatedTokens = EstimateTokens(context);

        if (!rateLimiter.TryAcquire(keyId.Value.ToString("N"), rpm, tpm, estimatedTokens, out var retryAfter))
        {
            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfter.ToString();
            await context.Response.WriteAsJsonAsync(
                    ErrorResponse.Create(
                        $"Rate limit exceeded. Retry after {retryAfter} seconds.",
                        "rate_limit_error",
                        "rate_limit_exceeded"))
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static int EstimateTokens(HttpContext context)
    {
        // Cheap estimate: Content-Length / 4 when present.
        if (context.Request.ContentLength is long len && len > 0)
        {
            return (int)Math.Clamp(len / 4, 1, 32_000);
        }

        return 256;
    }
}
