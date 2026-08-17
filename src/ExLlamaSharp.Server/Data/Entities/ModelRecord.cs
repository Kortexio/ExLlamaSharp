namespace ExLlamaSharp.Server.Data.Entities;

public sealed class ModelRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public string? Alias { get; set; }
    public string? Architecture { get; set; }
    public double SizeGb { get; set; }
    public int ContextLength { get; set; }
    /// <summary>JSON modelfile: from, template, parameters, system, stop.</summary>
    public string? ModelfileJson { get; set; }
    /// <summary>exl3 | exl2 | int8 | fp8 | awq | gptq | fp16</summary>
    public string? QuantMode { get; set; }
    /// <summary>JSON device placement for multi-GPU.</summary>
    public string? DevicePlacementJson { get; set; }
    public string TenantId { get; set; } = "default";
    public bool Shared { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
