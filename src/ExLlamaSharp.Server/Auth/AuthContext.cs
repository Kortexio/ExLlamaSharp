using System.Security.Cryptography;
using System.Text;

namespace ExLlamaSharp.Server.Auth;

public static class AuthContextKeys
{
    public const string KeyId = "KeyId";
    public const string Scopes = "Scopes";
    public const string Priority = "Priority";
    public const string TenantId = "TenantId";
    public const string Rpm = "Rpm";
    public const string Tpm = "Tpm";
    public const string KeyHash = "KeyHash";
    public const string IsAdmin = "IsAdmin";
}

public static class ApiKeyHasher
{
    public static string Hash(string rawKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class AuthHttpContextExtensions
{
    public static Guid? GetKeyId(this HttpContext ctx) =>
        ctx.Items.TryGetValue(AuthContextKeys.KeyId, out var v) && v is Guid id ? id : null;

    public static string GetTenantId(this HttpContext ctx) =>
        ctx.Items.TryGetValue(AuthContextKeys.TenantId, out var v) && v is string s ? s : "default";

    public static int GetPriority(this HttpContext ctx) =>
        ctx.Items.TryGetValue(AuthContextKeys.Priority, out var v) && v is int p ? p : 5;

    public static int GetRpm(this HttpContext ctx) =>
        ctx.Items.TryGetValue(AuthContextKeys.Rpm, out var v) && v is int n ? n : 60;

    public static int GetTpm(this HttpContext ctx) =>
        ctx.Items.TryGetValue(AuthContextKeys.Tpm, out var v) && v is int n ? n : 100_000;

    public static IReadOnlyList<string> GetScopes(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(AuthContextKeys.Scopes, out var v) && v is string[] scopes)
        {
            return scopes;
        }

        return [];
    }

    public static bool HasScope(this HttpContext ctx, string scope) =>
        ctx.GetScopes().Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "admin", StringComparison.OrdinalIgnoreCase));
}
