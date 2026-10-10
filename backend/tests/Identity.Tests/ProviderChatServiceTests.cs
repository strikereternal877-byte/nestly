using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Chat;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// The provider-facing chat reply view (task 193, provider-api's
/// <c>ChatController</c>/<c>IProviderChatService</c>) - the counterpart to
/// <c>Catalog.Tests.ChatServiceTests</c> for the customer side. Covers
/// get-or-create idempotency, send/list/pagination, and the ownership check:
/// only the booking's LIVE assignment (status Assigned or Accepted) may
/// touch its thread, using the exact same
/// <see cref="IBookingProviderAssignmentRepository.GetActiveByBookingAsync"/>
/// primitive <c>ProviderJobService</c> uses - not a booking-status re-check.
/// </summary>
public sealed class ProviderChatServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _otherProviderId;
    private readonly Guid _adminUserId = Guid.NewGuid();

    public ProviderChatServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        provider.ChangeStatus(ProviderStatus.Active);
        otherProvider.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        _otherProviderId = otherProvider.Id;
        context.AddRange(provider, otherProvider);
        context.SaveChanges();
    }

    private static ProviderChatService BuildService(NestlyDbContext context) => new(
        new ChatThreadRepository(context), new ChatMessageRepository(context), new BookingProviderAssignmentRepository(context));

    private static ChatService BuildCustomerChatService(NestlyDbContext context) => new(
        new ChatThreadRepository(context), new ChatMessageRepository(context),
        new BookingRepository(context), new SupportTicketRepository(context));

    private BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(context), context);

    private static Booking NewAwaitingFulfilmentBooking(Guid customerId)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    /// <summary>Seeds a booking Assigned (not yet Accepted) to <see cref="_providerId"/> - the weaker of the two LIVE statuses, to prove Assigned alone is enough (not just Accepted).</summary>
    private async Task<(Guid CustomerId, Guid BookingId)> SeedAssignedBookingAsync(NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        var assignResult = await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        return (customer.Id, booking.Id);
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_creates_a_thread_for_a_booking_this_provider_is_assigned_to()
    {
        Guid bookingId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
        }

        using var readContext = _database.CreateContext();
        var result = await BuildService(readContext).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);

        result.IsSuccess.Should().BeTrue("Assigned - not just Accepted - is a LIVE status");
        result.Value.ContextType.Should().Be(ChatContextType.Booking);
        result.Value.ContextId.Should().Be(bookingId);
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_is_idempotent_for_the_same_booking()
    {
        Guid bookingId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
        }

        Guid firstThreadId;
        using (var context = _database.CreateContext())
        {
            var first = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);
            firstThreadId = first.Value.Id;
        }

        using var secondContext = _database.CreateContext();
        var second = await BuildService(secondContext).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);

        second.Value.Id.Should().Be(firstThreadId, "a second request for the same booking must return the existing thread, not create a duplicate");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_rejects_a_provider_not_assigned_to_the_booking()
    {
        Guid bookingId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
        }

        using var readContext = _database.CreateContext();
        var result = await BuildService(readContext).GetOrCreateThreadAsync(_otherProviderId, ChatContextType.Booking, bookingId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Chat.BookingNotFound", "a booking that exists but is not this provider's must read exactly like one that does not exist (404-not-403)");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_rejects_a_booking_that_does_not_exist()
    {
        using var context = _database.CreateContext();
        var result = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Chat.BookingNotFound");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_rejects_a_support_ticket_context_providers_have_none()
    {
        using var context = _database.CreateContext();
        var result = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.SupportTicket, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Chat.BookingNotFound", "folded into the same NotFound rather than a distinguishable validation error");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_rejects_a_provider_whose_assignment_was_rejected()
    {
        Guid bookingId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
            var rejectResult = await CreateAssignmentService(context).RejectByProviderAsync(
                bookingId, _providerId, new RejectAssignmentRequest("Too far"));
            rejectResult.IsSuccess.Should().BeTrue();
        }

        using var readContext = _database.CreateContext();
        var result = await BuildService(readContext).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);

        result.IsSuccess.Should().BeFalse("a Rejected assignment is no longer the booking's LIVE one");
        result.Error.Code.Should().Be("Chat.BookingNotFound");
    }

    [Fact]
    public async Task SendMessageAsync_persists_a_provider_message_and_bumps_thread_recency()
    {
        Guid bookingId, threadId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
            var created = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);
            threadId = created.Value.Id;
        }

        using (var context = _database.CreateContext())
        {
            var sendResult = await BuildService(context).SendMessageAsync(_providerId, threadId, new SendChatMessageRequest("On my way!"));
            sendResult.IsSuccess.Should().BeTrue();
            sendResult.Value.SenderType.Should().Be(ChatSenderType.Provider);
            sendResult.Value.SenderId.Should().Be(_providerId);
            sendResult.Value.ReadAtUtc.Should().BeNull();
        }

        using var readContext = _database.CreateContext();
        var thread = await new ChatThreadRepository(readContext).GetByIdAsync(threadId);
        thread!.LastMessageAtUtc.Should().BeAfter(thread.CreatedAtUtc.AddSeconds(-1));
    }

    [Fact]
    public async Task SendMessageAsync_rejects_a_thread_for_a_booking_the_caller_is_not_assigned_to()
    {
        Guid bookingId, threadId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
            var created = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);
            threadId = created.Value.Id;
        }

        using var readContext = _database.CreateContext();
        var result = await BuildService(readContext).SendMessageAsync(_otherProviderId, threadId, new SendChatMessageRequest("Not mine."));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Chat.BookingNotFound");
    }

    [Fact]
    public async Task GetHistoryAsync_returns_messages_oldest_first_with_paging_metadata()
    {
        Guid bookingId, threadId;
        using (var context = _database.CreateContext())
        {
            (_, bookingId) = await SeedAssignedBookingAsync(context);
            var created = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);
            threadId = created.Value.Id;
        }

        using (var context = _database.CreateContext())
        {
            var service = BuildService(context);
            await service.SendMessageAsync(_providerId, threadId, new SendChatMessageRequest("First"));
            await service.SendMessageAsync(_providerId, threadId, new SendChatMessageRequest("Second"));
        }

        using var readContext = _database.CreateContext();
        var result = await BuildService(readContext).GetHistoryAsync(_providerId, threadId, page: 1, pageSize: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Messages.Should().HaveCount(2);
        result.Value.Messages[0].Body.Should().Be("First");
        result.Value.Messages[1].Body.Should().Be("Second");
    }

    [Fact]
    public async Task MarkReadAsync_sets_read_at_only_on_messages_not_sent_by_the_reading_provider()
    {
        Guid customerId, bookingId, threadId;
        using (var context = _database.CreateContext())
        {
            (customerId, bookingId) = await SeedAssignedBookingAsync(context);
            var created = await BuildService(context).GetOrCreateThreadAsync(_providerId, ChatContextType.Booking, bookingId);
            threadId = created.Value.Id;
        }

        using (var context = _database.CreateContext())
        {
            await BuildService(context).SendMessageAsync(_providerId, threadId, new SendChatMessageRequest("Provider says hi"));
            await BuildCustomerChatService(context).SendMessageAsync(customerId, threadId, new SendChatMessageRequest("Customer reply"));
        }

        using (var context = _database.CreateContext())
        {
            var markResult = await BuildService(context).MarkReadAsync(_providerId, threadId);
            markResult.IsSuccess.Should().BeTrue();
        }

        using var readContext = _database.CreateContext();
        var history = await BuildService(readContext).GetHistoryAsync(_providerId, threadId, 1, 50);

        history.Value.Messages.Single(m => m.SenderType == ChatSenderType.Provider).ReadAtUtc.Should().BeNull(
            "the provider's own message must not be marked read by their own mark-read call");
        history.Value.Messages.Single(m => m.SenderType == ChatSenderType.Customer).ReadAtUtc.Should().NotBeNull(
            "the customer's message, addressed to the provider, must be marked read");
    }

    public void Dispose() => _database.Dispose();
}
