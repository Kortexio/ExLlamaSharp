namespace ExLlamaSharp.Server.Data.Entities;

public sealed class LoraAdapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BaseModelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Rank { get; set; } = 16;
    public double Alpha { get; set; } = 32;
    /// <summary>JSON array of target module names.</summary>
    public string? TargetModules { get; set; }
    public string TenantId { get; set; } = "default";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ModelRecord? BaseModel { get; set; }
}
