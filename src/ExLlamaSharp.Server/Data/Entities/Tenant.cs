namespace ExLlamaSharp.Server.Data.Entities;

public sealed class Tenant
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Subdomain { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Active { get; set; } = true;

    public TenantQuota? Quota { get; set; }
}
