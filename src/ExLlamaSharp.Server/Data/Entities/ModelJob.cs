namespace ExLlamaSharp.Server.Data.Entities;

public sealed class ModelJob
{
    public Guid JobId { get; set; } = Guid.NewGuid();
    /// <summary>pull | quantize | import</summary>
    public string Type { get; set; } = "pull";
    /// <summary>pending | running | downloading | completed | failed | cancelled</summary>
    public string Status { get; set; } = "pending";
    public double ProgressPct { get; set; }
    public int? EtaSeconds { get; set; }
    public string? Error { get; set; }
    public Guid? ModelId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ModelRecord? Model { get; set; }
}
