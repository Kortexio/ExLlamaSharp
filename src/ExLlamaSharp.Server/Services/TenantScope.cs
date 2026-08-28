using ExLlamaSharp.Server.Auth;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace ExLlamaSharp.Server.Services;

/// <summary>Resolves whether multi-tenancy filtering should apply for the current request.</summary>
public static class TenantScope
{
    public static async Task<string?> EffectiveFilterAsync(
        HttpContext http,
        SettingsService settings,
        CancellationToken cancellationToken = default)
    {
        var cfg = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!cfg.MultiTenancyEnabled)
        {
            return null;
        }

        var tenantId = http.GetTenantId();
        return string.IsNullOrWhiteSpace(tenantId) ? TenantResolver.DefaultTenantId : tenantId;
    }

    public static bool Matches(string entityTenantId, string? filterTenantId) =>
        filterTenantId is null
        || string.Equals(entityTenantId, filterTenantId, StringComparison.OrdinalIgnoreCase);
}
