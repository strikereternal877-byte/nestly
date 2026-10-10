using Nestly.Application.Notifications;
using Nestly.Application.ProviderSupport;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IAdminProviderSupportTicketService"/>
public class AdminProviderSupportTicketService : IAdminProviderSupportTicketService
{
    private readonly IProviderSupportTicketRepository _repository;
    private readonly IProviderNotificationPublisher _notificationPublisher;

    public AdminProviderSupportTicketService(IProviderSupportTicketRepository repository, IProviderNotificationPublisher notificationPublisher)
    {
        _repository = repository;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<Result<AdminProviderSupportTicketSearchResponse>> SearchAsync(AdminProviderSupportTicketSearchRequest request)
    {
        var result = await _repository.SearchAsync(request.ToCriteria(), request.Page, request.PageSize);

        var items = result.Rows.Select(row => new AdminProviderSupportTicketSummaryResponse(
            row.Ticket.Id, row.Ticket.ProviderId, row.ProviderName, row.Ticket.Category, row.Ticket.Subject,
            row.Ticket.Status, row.Ticket.CreatedAtUtc, row.Ticket.UpdatedAtUtc)).ToList();

        return new AdminProviderSupportTicketSearchResponse(items, result.TotalCount, request.Page, request.PageSize);
    }

    public async Task<Result<AdminProviderSupportTicketDetailResponse>> GetDetailAsync(Guid ticketId)
    {
        var row = await _repository.GetAdminRowByIdAsync(ticketId);
        if (row is null)
        {
            return Error.NotFound("ProviderSupportTicket.NotFound", "The specified ticket does not exist.");
        }

        return ToDetailResponse(row);
    }

    public async Task<Result<AdminProviderSupportTicketDetailResponse>> ReplyAsync(Guid ticketId, AddProviderSupportTicketCommentRequest request)
    {
        var ticket = await _repository.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            return Error.NotFound("ProviderSupportTicket.NotFound", "The specified ticket does not exist.");
        }

        ticket.AddComment(Guid.NewGuid(), ProviderSupportTicketCommentAuthorType.Admin, request.Comment);

        // A ticket an admin has replied to is no longer merely "open" -
        // mirrors how a human support queue works: silence means open,
        // a reply means someone is on it.
        if (ticket.Status == ProviderSupportTicketStatus.Open)
        {
            ticket.ChangeStatus(ProviderSupportTicketStatus.InProgress);
        }

        await _repository.UpdateAsync(ticket);

        await _notificationPublisher.NotifyAsync(
            ticket.ProviderId,
            ProviderNotificationType.SupportTicketReply,
            "Support replied",
            $"New reply on your ticket \"{ticket.Subject}\".",
            deepLinkPath: $"/support/{ticket.Id}");

        var row = await _repository.GetAdminRowByIdAsync(ticketId);
        return ToDetailResponse(row!);
    }

    public async Task<Result<AdminProviderSupportTicketDetailResponse>> ResolveAsync(Guid ticketId, ResolveProviderSupportTicketRequest request)
    {
        var ticket = await _repository.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            return Error.NotFound("ProviderSupportTicket.NotFound", "The specified ticket does not exist.");
        }

        try
        {
            ticket.ChangeStatus(ProviderSupportTicketStatus.Resolved, request.ResolutionSummary);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderSupportTicket.InvalidTransition", ex.Message);
        }

        await _repository.UpdateAsync(ticket);

        await _notificationPublisher.NotifyAsync(
            ticket.ProviderId,
            ProviderNotificationType.SupportTicketReply,
            "Ticket resolved",
            $"Your ticket \"{ticket.Subject}\" was marked resolved: {request.ResolutionSummary}",
            deepLinkPath: $"/support/{ticket.Id}");

        var row = await _repository.GetAdminRowByIdAsync(ticketId);
        return ToDetailResponse(row!);
    }

    private static AdminProviderSupportTicketDetailResponse ToDetailResponse(AdminProviderSupportTicketRow row) => new(
        row.Ticket.Id,
        row.Ticket.ProviderId,
        row.ProviderName,
        row.Ticket.Category,
        row.Ticket.Subject,
        row.Ticket.Description,
        row.Ticket.Status,
        row.Ticket.ResolutionSummary,
        row.Ticket.Comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ProviderSupportTicketCommentResponse(c.Id, c.AuthorType, c.Comment, c.CreatedAt))
            .ToList(),
        row.Ticket.CreatedAtUtc,
        row.Ticket.UpdatedAtUtc);
}
