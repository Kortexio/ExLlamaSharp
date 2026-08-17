namespace ExLlamaSharp.Server.Data.Entities;

public sealed class ModelLibraryEntry
{
    public string Id { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Comma-separated tags.</summary>
    public string? Tags { get; set; }
    public string? Architecture { get; set; }
    public string? Parameters { get; set; }
    public int ContextLength { get; set; }
    public string? License { get; set; }
    /// <summary>Comma-separated recommended EXL3 bit widths, e.g. 4.0,3.5</summary>
    public string? RecommendedBits { get; set; }
}
