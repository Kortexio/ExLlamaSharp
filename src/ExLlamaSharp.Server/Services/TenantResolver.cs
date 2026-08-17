using Microsoft.AspNetCore.Http;

namespace ExLlamaSharp.Server.Services;

public sealed class TenantResolver
{
    public const string HttpContextItemKey = "TenantId";
    public const string HeaderName = "X-Tenant-ID";
    public const string DefaultTenantId = "default";

    public string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(HttpContextItemKey, out var existing) && existing is string cached)
        {
            return cached;
        }

        var tenantId = DefaultTenantId;

        // 1) Path prefix: /t/{tenant}/...
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                tenantId = segments[1];
            }
        }

        // 2) Subdomain: acme.example.com → acme
        var host = context.Request.Host.Host;
        if (!string.IsNullOrWhiteSpace(host) && host.Contains('.', StringComparison.Ordinal))
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2
                && !parts[0].Equals("www", StringComparison.OrdinalIgnoreCase)
                && !parts[0].Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                tenantId = parts[0];
            }
        }

        // 3) Header overrides
        if (context.Request.Headers.TryGetValue(HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            tenantId = header.ToString().Trim();
        }

        context.Items[HttpContextItemKey] = tenantId;
        return tenantId;
    }

    public string GetCurrent(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(HttpContextItemKey, out var value) && value is string tenantId)
        {
            return tenantId;
        }

        return Resolve(context);
    }
}
