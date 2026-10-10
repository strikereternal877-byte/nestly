using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderSupport;

/// <summary>Provider-facing support ticket workflow (Provider Management UX pass) - mirrors <c>ISupportTicketService</c>'s shape, identity-scoped by the caller's own <c>providerId</c>.</summary>
public interface IProviderSupportTicketService
{
    Task<Result<ProviderSupportTicketDetailResponse>> CreateAsync(Guid providerId, CreateProviderSupportTicketRequest request);

    /// <summary>All of the caller's tickets, newest first.</summary>
    Task<Result<IReadOnlyList<ProviderSupportTicketSummaryResponse>>> ListAsync(Guid providerId);

    /// <summary>Full detail including the comment thread.</summary>
    Task<Result<ProviderSupportTicketDetailResponse>> GetDetailAsync(Guid providerId, Guid ticketId);

    /// <summary>Appends a provider follow-up to the thread.</summary>
    Task<Result<ProviderSupportTicketDetailResponse>> AddCommentAsync(Guid providerId, Guid ticketId, AddProviderSupportTicketCommentRequest request);
}
