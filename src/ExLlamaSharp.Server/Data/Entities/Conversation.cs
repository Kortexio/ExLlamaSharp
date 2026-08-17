namespace ExLlamaSharp.Server.Data.Entities;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New chat";
    public Guid? UserId { get; set; }
    public string TenantId { get; set; } = "default";
    public Guid? ModelId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public Tenant? Tenant { get; set; }
    public ModelRecord? Model { get; set; }
    public ICollection<ConversationMessage> Messages { get; set; } = new List<ConversationMessage>();
}
