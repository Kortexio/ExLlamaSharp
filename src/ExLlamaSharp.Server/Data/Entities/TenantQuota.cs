namespace ExLlamaSharp.Server.Data.Entities;

public sealed class TenantQuota
{
    public string TenantId { get; set; } = string.Empty;
    public int MaxUsers { get; set; } = 50;
    public int MaxKeys { get; set; } = 100;
    public int MaxModels { get; set; } = 20;
    public double MaxStorageGb { get; set; } = 500;
    public int RequestsPerHour { get; set; } = 10_000;
    public long TokensPerMonth { get; set; } = 100_000_000;

    public Tenant? Tenant { get; set; }
}
