namespace ExLlamaSharp.Server.Data.Entities;

public sealed class AbTest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid ModelAId { get; set; }
    public Guid ModelBId { get; set; }
    /// <summary>Fraction routed to Model A (0.5 = 50/50).</summary>
    public double SplitRatio { get; set; } = 0.5;
    public bool Active { get; set; } = true;
    public string TenantId { get; set; } = "default";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ModelRecord? ModelA { get; set; }
    public ModelRecord? ModelB { get; set; }
}
