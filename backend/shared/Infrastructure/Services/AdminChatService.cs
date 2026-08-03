using Microsoft.EntityFrameworkCore;
using Nestly.Application.Bookings;
using Nestly.Application.Chat;
using Nestly.Application.Support;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Admin support-console chat (task 193). Not ownership-scoped to a single
/// customer - see <see cref="IAdminChatService"/>'s doc comment; the
/// "chat.read" permission gate lives in the controller (and, for the hub, in
/// <c>ChatHub.CanAccessAsync</c>), not here.
/// </summary>
public class AdminChatService : IAdminChatService
{
    private readonly IChatThreadRepository _threadRepository;
    private readonly IChatMessageRepository _messageRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly ISupportTicketRepository _supportTicketRepository;

    public AdminChatService(
        IChatThreadRepository threadRepository,
        IChatMessageRepository messageRepository,
        IBookingRepository bookingRepository,
        ISupportTicketRepository supportTicketRepository)
    {
        _threadRepository = threadRepository;
        _messageRepository = messageRepository;
        _bookingRepository = bookingRepository;
        _supportTicketRepository = supportTicketRepository;
    }

    public async Task<Result<ChatThreadResponse>> GetOrCreateThreadAsync(ChatContextType contextType, Guid contextId)
    {
        var existsError = await ValidateContextExistsAsync(contextType, contextId);
        if (existsError is not null)
        {
            return existsError;
        }

        var thread = await _threadRepository.GetByContextAsync(contextType, contextId);
        if (thread is null)
        {
            thread = new ChatThread(Guid.NewGuid(), contextType, contextId);
            try
            {
                await _threadRepository.AddAsync(thread);
            }
            catch (DbUpdateException)
            {
                thread = await _threadRepository.GetByContextAsync(contextType, contextId)
                    ?? throw new InvalidOperationException("Chat thread creation conflicted but no thread was found on refetch.");
            }
        }

        return Result.Success(ToThreadResponse(thread));
    }

    public async Task<Result<ChatMessageResponse>> ReplyAsync(Guid adminUserId, Guid threadId, SendChatMessageRequest request)
    {
        var thread = await _threadRepository.GetByIdAsync(threadId);
        if (thread is null)
        {
            return Error.NotFound("Chat.ThreadNotFound", "The specified chat thread does not exist.");
        }

        var message = new ChatMessage(
            Guid.NewGuid(), threadId, thread.ContextType, thread.ContextId, adminUserId, ChatSenderType.Admin, request.Body);
        await _messageRepository.AddAsync(message);

        thread.TouchLastMessage(message.SentAtUtc);
        await _threadRepository.UpdateAsync(thread);

        return Result.Success(ToMessageResponse(message));
    }

    public async Task<Result<ChatMessagePageResult>> GetHistoryAsync(Guid threadId, int page, int pageSize)
    {
        var thread = await _threadRepository.GetByIdAsync(threadId);
        if (thread is null)
        {
            return Error.NotFound("Chat.ThreadNotFound", "The specified chat thread does not exist.");
        }

        var (messages, totalCount) = await _messageRepository.ListByThreadAsync(threadId, page, pageSize);
        return Result.Success(new ChatMessagePageResult(messages.Select(ToMessageResponse).ToList(), totalCount, page, pageSize));
    }

    public async Task<Result> MarkReadAsync(Guid threadId)
    {
        var thread = await _threadRepository.GetByIdAsync(threadId);
        if (thread is null)
        {
            return Result.Failure(Error.NotFound("Chat.ThreadNotFound", "The specified chat thread does not exist."));
        }

        // The reader here is "the admin side" collectively, not one admin
        // user id - MarkThreadReadAsync excludes messages sent by the
        // passed-in id, so Guid.Empty (never a real sender id, since every
        // ChatMessage requires a non-empty SenderId) reads every
        // customer/provider-authored message as read without needing to
        // track which specific admin viewed the thread.
        await _messageRepository.MarkThreadReadAsync(threadId, Guid.Empty, DateTime.UtcNow);
        return Result.Success();
    }

    private async Task<Error?> ValidateContextExistsAsync(ChatContextType contextType, Guid contextId)
    {
        switch (contextType)
        {
            case ChatContextType.Booking:
                if (await _bookingRepository.GetByIdAsync(contextId) is null)
                {
                    return Error.NotFound("Chat.BookingNotFound", "The specified booking does not exist.");
                }

                break;

            case ChatContextType.SupportTicket:
                if (await _supportTicketRepository.GetByIdAsync(contextId) is null)
                {
                    return Error.NotFound("Chat.SupportTicketNotFound", "The specified support ticket does not exist.");
                }

                break;

            default:
                return Error.Validation("Chat.InvalidContextType", "Unsupported chat context type.");
        }

        return null;
    }

    private static ChatThreadResponse ToThreadResponse(ChatThread thread) => new(
        thread.Id, thread.ContextType, thread.ContextId, thread.CreatedAtUtc, thread.LastMessageAtUtc);

    private static ChatMessageResponse ToMessageResponse(ChatMessage message) => new(
        message.Id, message.ThreadId, message.SenderId, message.SenderType, message.Body, message.SentAtUtc, message.ReadAtUtc);
}
