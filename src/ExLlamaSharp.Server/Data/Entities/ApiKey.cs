namespace ExLlamaSharp.Server.Data.Entities;

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    /// <summary>Comma-separated scopes: chat,completions,embeddings,admin</summary>
    public string Scopes { get; set; } = "chat,completions";
    public int Rpm { get; set; } = 60;
    public int Tpm { get; set; } = 100_000;
    /// <summary>1 (highest) to 10 (lowest).</summary>
    public int Priority { get; set; } = 5;
    public decimal CostPerMillionTokens { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public string TenantId { get; set; } = "default";
    public Guid? UserId { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
