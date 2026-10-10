using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A provider's support ticket (Provider Management UX pass: provider-web had
/// no way for a provider to reach Glavyx support at all). Mirrors
/// <see cref="SupportTicket"/>'s shape - the aggregate root for its own
/// comment thread, one consistency boundary - deliberately kept as a
/// separate aggregate rather than reusing <see cref="SupportTicket"/>
/// directly: that aggregate's <c>CustomerId</c> is a required, non-nullable
/// field threaded through the admin console, dispute resolution and every
/// customer-facing notification trigger, so retrofitting it to also mean
/// "or a provider" would be a breaking, invasive change to an already
/// heavily-referenced module for a need this much smaller aggregate already
/// satisfies cleanly.
/// </summary>
public class ProviderSupportTicket : Entity<Guid>
{
    private readonly List<ProviderSupportTicketComment> _comments = [];

    public Guid ProviderId { get; private set; }
    public ProviderSupportTicketCategory Category { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public ProviderSupportTicketStatus Status { get; private set; }
    public string? ResolutionSummary { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public IReadOnlyList<ProviderSupportTicketComment> Comments => _comments;

    protected ProviderSupportTicket() { }

    public ProviderSupportTicket(Guid id, Guid providerId, ProviderSupportTicketCategory category, string subject, string description)
        : base(id)
    {
        ProviderId = providerId;
        Category = category;
        Subject = string.IsNullOrWhiteSpace(subject)
            ? throw new ArgumentException("Subject is required.", nameof(subject))
            : subject;
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Description is required.", nameof(description))
            : description;
        Status = ProviderSupportTicketStatus.Open;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    /// <summary>Appends a reply to the thread without otherwise changing ticket status - callers (the service layer) decide whether a status change should accompany it.</summary>
    public ProviderSupportTicketComment AddComment(Guid id, ProviderSupportTicketCommentAuthorType authorType, string comment)
    {
        var entry = new ProviderSupportTicketComment(id, Id, authorType, comment);
        _comments.Add(entry);
        UpdatedAtUtc = DateTime.UtcNow;
        return entry;
    }

    public void ChangeStatus(ProviderSupportTicketStatus newStatus, string? resolutionSummary = null)
    {
        if (!ProviderSupportTicketLifecycle.IsValidTransition(Status, newStatus))
        {
            throw new InvalidOperationException($"Cannot transition a provider support ticket from {Status} to {newStatus}.");
        }

        Status = newStatus;
        if (resolutionSummary is not null)
        {
            ResolutionSummary = resolutionSummary;
        }

        UpdatedAtUtc = DateTime.UtcNow;
    }
}
