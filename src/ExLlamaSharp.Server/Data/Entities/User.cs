namespace ExLlamaSharp.Server.Data.Entities;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    /// <summary>admin | operator | viewer</summary>
    public string Role { get; set; } = "viewer";
    public string TenantId { get; set; } = "default";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActive { get; set; }

    public Tenant? Tenant { get; set; }
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
