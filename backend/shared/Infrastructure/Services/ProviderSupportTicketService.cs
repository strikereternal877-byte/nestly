using Nestly.Application.ProviderSupport;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderSupportTicketService"/>
public class ProviderSupportTicketService : IProviderSupportTicketService
{
    private readonly IProviderSupportTicketRepository _repository;

    public ProviderSupportTicketService(IProviderSupportTicketRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<ProviderSupportTicketDetailResponse>> CreateAsync(Guid providerId, CreateProviderSupportTicketRequest request)
    {
        var ticket = new ProviderSupportTicket(Guid.NewGuid(), providerId, request.Category, request.Subject, request.Description);
        await _repository.AddAsync(ticket);
        return Result.Success(ToDetailResponse(ticket));
    }

    public async Task<Result<IReadOnlyList<ProviderSupportTicketSummaryResponse>>> ListAsync(Guid providerId)
    {
        var tickets = await _repository.ListByProviderAsync(providerId);
        IReadOnlyList<ProviderSupportTicketSummaryResponse> response = tickets.Select(ToSummary).ToList();
        return Result.Success(response);
    }

    public async Task<Result<ProviderSupportTicketDetailResponse>> GetDetailAsync(Guid providerId, Guid ticketId)
    {
        var ticket = await _repository.GetByIdAsync(ticketId);
        if (ticket is null || ticket.ProviderId != providerId)
        {
            return Error.NotFound("ProviderSupportTicket.NotFound", "The specified ticket does not exist.");
        }

        return Result.Success(ToDetailResponse(ticket));
    }

    public async Task<Result<ProviderSupportTicketDetailResponse>> AddCommentAsync(Guid providerId, Guid ticketId, AddProviderSupportTicketCommentRequest request)
    {
        var ticket = await _repository.GetByIdAsync(ticketId);
        if (ticket is null || ticket.ProviderId != providerId)
        {
            return Error.NotFound("ProviderSupportTicket.NotFound", "The specified ticket does not exist.");
        }

        ticket.AddComment(Guid.NewGuid(), ProviderSupportTicketCommentAuthorType.Provider, request.Comment);
        await _repository.UpdateAsync(ticket);
        return Result.Success(ToDetailResponse(ticket));
    }

    private static ProviderSupportTicketSummaryResponse ToSummary(ProviderSupportTicket ticket) => new(
        ticket.Id, ticket.Category, ticket.Subject, ticket.Status, ticket.CreatedAtUtc, ticket.UpdatedAtUtc);

    internal static ProviderSupportTicketDetailResponse ToDetailResponse(ProviderSupportTicket ticket) => new(
        ticket.Id,
        ticket.ProviderId,
        ticket.Category,
        ticket.Subject,
        ticket.Description,
        ticket.Status,
        ticket.ResolutionSummary,
        ticket.Comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ProviderSupportTicketCommentResponse(c.Id, c.AuthorType, c.Comment, c.CreatedAt))
            .ToList(),
        ticket.CreatedAtUtc,
        ticket.UpdatedAtUtc);
}
