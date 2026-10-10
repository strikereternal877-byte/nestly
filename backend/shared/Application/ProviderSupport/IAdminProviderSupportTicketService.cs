using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderSupport;

/// <summary>The admin workflow over provider support tickets: search across every provider, view a thread, reply, and resolve.</summary>
public interface IAdminProviderSupportTicketService
{
    Task<Result<AdminProviderSupportTicketSearchResponse>> SearchAsync(AdminProviderSupportTicketSearchRequest request);

    Task<Result<AdminProviderSupportTicketDetailResponse>> GetDetailAsync(Guid ticketId);

    /// <summary>Appends an admin reply to the thread. Moves a still-Open ticket to InProgress - a ticket an admin has replied to is no longer merely "open."</summary>
    Task<Result<AdminProviderSupportTicketDetailResponse>> ReplyAsync(Guid ticketId, AddProviderSupportTicketCommentRequest request);

    Task<Result<AdminProviderSupportTicketDetailResponse>> ResolveAsync(Guid ticketId, ResolveProviderSupportTicketRequest request);
}
