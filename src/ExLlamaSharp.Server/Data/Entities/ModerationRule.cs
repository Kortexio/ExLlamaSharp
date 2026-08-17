namespace ExLlamaSharp.Server.Data.Entities;

public sealed class ModerationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Pattern { get; set; } = string.Empty;
    /// <summary>block | warn | flag</summary>
    public string Action { get; set; } = "block";
    /// <summary>offensive | dangerous | pii | custom</summary>
    public string Category { get; set; } = "offensive";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
