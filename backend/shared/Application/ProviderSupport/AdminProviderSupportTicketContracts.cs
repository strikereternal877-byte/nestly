using Nestly.Domain;

namespace Nestly.Application.ProviderSupport;

/// <summary>The admin ticket workflow over provider support tickets - mirrors <c>Nestly.Application.Support.AdminSupportTicketContracts</c>, scaled to this module's smaller shape (no assignment/escalation/dispute).</summary>
public sealed record AdminProviderSupportTicketCriteria(
    Guid? ProviderId,
    ProviderSupportTicketCategory? Category,
    ProviderSupportTicketStatus? Status,
    DateTime? FromUtc,
    DateTime? ToUtc);

public sealed record AdminProviderSupportTicketSearchRequest(
    Guid? ProviderId,
    ProviderSupportTicketCategory? Category,
    ProviderSupportTicketStatus? Status,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page = 1,
    int PageSize = 20)
{
    public AdminProviderSupportTicketCriteria ToCriteria() => new(ProviderId, Category, Status, FromUtc, ToUtc);
}

/// <summary>One ticket joined with the provider's display name the admin list shows - the ticket itself is never denormalized, only read alongside it, same convention as <c>AdminSupportTicketRow</c>.</summary>
public sealed record AdminProviderSupportTicketRow(ProviderSupportTicket Ticket, string ProviderName);

public sealed record AdminProviderSupportTicketSearchResult(IReadOnlyList<AdminProviderSupportTicketRow> Rows, int TotalCount);

public sealed record AdminProviderSupportTicketSummaryResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderName,
    ProviderSupportTicketCategory Category,
    string Subject,
    ProviderSupportTicketStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record AdminProviderSupportTicketSearchResponse(
    IReadOnlyList<AdminProviderSupportTicketSummaryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record AdminProviderSupportTicketDetailResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderName,
    ProviderSupportTicketCategory Category,
    string Subject,
    string Description,
    ProviderSupportTicketStatus Status,
    string? ResolutionSummary,
    IReadOnlyList<ProviderSupportTicketCommentResponse> Comments,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ResolveProviderSupportTicketRequest(string ResolutionSummary);
