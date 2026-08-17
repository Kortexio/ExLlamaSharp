namespace ExLlamaSharp.Server.Data.Entities;

public sealed class BackupHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>manual | scheduled</summary>
    public string Kind { get; set; } = "manual";
}
