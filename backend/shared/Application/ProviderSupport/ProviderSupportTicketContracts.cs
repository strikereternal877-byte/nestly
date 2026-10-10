using Nestly.Domain;

namespace Nestly.Application.ProviderSupport;

/// <summary>Provider-facing support ticket workflow contracts (Provider Management UX pass) - mirrors <c>Nestly.Application.Support.SupportTicketContracts</c>'s shape, scoped to a provider instead of a customer.</summary>
public sealed record CreateProviderSupportTicketRequest(ProviderSupportTicketCategory Category, string Subject, string Description);

public sealed record AddProviderSupportTicketCommentRequest(string Comment);

public sealed record ProviderSupportTicketSummaryResponse(
    Guid Id,
    ProviderSupportTicketCategory Category,
    string Subject,
    ProviderSupportTicketStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ProviderSupportTicketCommentResponse(Guid Id, ProviderSupportTicketCommentAuthorType AuthorType, string Comment, DateTime CreatedAt);

public sealed record ProviderSupportTicketDetailResponse(
    Guid Id,
    Guid ProviderId,
    ProviderSupportTicketCategory Category,
    string Subject,
    string Description,
    ProviderSupportTicketStatus Status,
    string? ResolutionSummary,
    IReadOnlyList<ProviderSupportTicketCommentResponse> Comments,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
