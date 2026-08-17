namespace ExLlamaSharp.Server.Data.Entities;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Endpoint { get; set; } = string.Empty;
    public Guid? KeyId { get; set; }
    public Guid? UserId { get; set; }
    public string TenantId { get; set; } = "default";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public int StatusCode { get; set; }
    public Guid? AbTestId { get; set; }
    /// <summary>A | B</summary>
    public string? ModelVariant { get; set; }
    public Guid? LoraAdapterId { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}
