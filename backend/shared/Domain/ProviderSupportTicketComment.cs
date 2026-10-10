using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>Who wrote a <see cref="ProviderSupportTicketComment"/> - mirrors <see cref="SupportTicketCommentAuthorType"/>, scoped to this ticket's two possible authors.</summary>
public enum ProviderSupportTicketCommentAuthorType
{
    Provider,
    Admin
}

/// <summary>A single reply in a provider support ticket's thread. Owned by its parent <see cref="ProviderSupportTicket"/> - see that aggregate's <c>AddComment</c>.</summary>
public class ProviderSupportTicketComment : Entity<Guid>
{
    public Guid ProviderSupportTicketId { get; private set; }
    public ProviderSupportTicketCommentAuthorType AuthorType { get; private set; }
    public string Comment { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }

    protected ProviderSupportTicketComment() { }

    public ProviderSupportTicketComment(Guid id, Guid providerSupportTicketId, ProviderSupportTicketCommentAuthorType authorType, string comment)
        : base(id)
    {
        ProviderSupportTicketId = providerSupportTicketId;
        AuthorType = authorType;
        Comment = string.IsNullOrWhiteSpace(comment)
            ? throw new ArgumentException("Comment text is required.", nameof(comment))
            : comment;
        CreatedAt = DateTime.UtcNow;
    }
}
