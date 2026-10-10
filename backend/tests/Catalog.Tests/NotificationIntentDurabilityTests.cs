using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.Chat;
using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 294: notifications are no longer at-most-once.
///
/// <para>
/// The defect these cover is invisible on a happy path - every notification
/// this system has ever sent was sent by the in-process handler, and the
/// handler works. What did not exist was anything left behind when it
/// <i>didn't</i>: docs/ARCHITECTURE.md's "DOMAIN EVENT DISPATCH AND DELIVERY"
/// section spelled out that a process death between the commit and the send
/// lost the message permanently, with nothing to retry from. So these tests
/// are deliberately written from the failure side - they never let the
/// in-process path run, or they run it twice, or they run out its retries -
/// because the guarantee only means something in exactly those cases.
/// </para>
/// </summary>
public sealed class NotificationIntentDurabilityTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public NotificationIntentDurabilityTests(TestDatabase db) => _db = db;

    /// <summary>
    /// A context wired the way production is wired for the half of task 294
    /// that has to happen inside the transaction. <c>TestDatabase</c>'s default
    /// context deliberately omits it, so a test that wants intents written has
    /// to say so - which keeps these tests honest about which side of the
    /// mechanism they are exercising.
    /// </summary>
    private NestlyDbContext CreateIntentWritingContext() =>
        _db.CreateContext(new NotificationIntentInterceptor());

    private static NotificationDispatchService BuildDispatchService(NestlyDbContext context) =>
        new(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
            new SandboxNotificationProvider(NullLogger<SandboxNotificationProvider>.Instance, new FakeHostEnvironment()),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            new NotificationEventRepository(context),
            new DeviceTokenRepository(context), new CustomerRepository(context), new ProviderRepository(context),
            new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

    private static BookingNotificationTriggerHandler BuildBookingHandler(
        NestlyDbContext context, INotificationIntentCoordinator coordinator) =>
        new(new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new PaymentGroupRepository(context),
            new BookingCancellationRepository(context),
            new RefundTransactionRepository(context),
            new ProviderRepository(context),
            BuildDispatchService(context),
            coordinator,
            TestServices.FulfilmentNotifications(),
            NullLogger<BookingNotificationTriggerHandler>.Instance);

    private static SupportTicketNotificationTriggerHandler BuildTicketHandler(
        NestlyDbContext context, INotificationIntentCoordinator coordinator) =>
        new(
            new CustomerRepository(context), new SupportTicketRepository(context), new DeviceTokenRepository(context),
            BuildDispatchService(context), coordinator, NullLogger<SupportTicketNotificationTriggerHandler>.Instance);

    private static SubscriptionNotificationTriggerHandler BuildSubscriptionHandler(
        NestlyDbContext context, INotificationIntentCoordinator coordinator) =>
        new(
            new CustomerRepository(context), new DeviceTokenRepository(context),
            BuildDispatchService(context), coordinator, NullLogger<SubscriptionNotificationTriggerHandler>.Instance);

    private static ChatNotificationTriggerHandler BuildChatHandler(
        NestlyDbContext context, INotificationIntentCoordinator coordinator, bool recipientOnline = false) =>
        new(
            new BookingRepository(context), new SupportTicketRepository(context), new CustomerRepository(context),
            new DeviceTokenRepository(context), new StubPresenceTracker(recipientOnline),
            BuildDispatchService(context), coordinator, NullLogger<ChatNotificationTriggerHandler>.Instance);

    /// <summary>
    /// The sweep as production runs it, with the grace period collapsed:
    /// waiting two real minutes in a unit test would only measure the clock.
    /// Every other bound is left at its production default so the retry
    /// arithmetic under test is the real arithmetic.
    /// </summary>
    private static NotificationIntentSweepJob BuildSweepJob(
        NestlyDbContext context,
        INotificationIntentCoordinator coordinator,
        IEnumerable<INotificationTriggerHandler> handlers,
        NotificationIntentOptions? options = null) =>
        new(
            new NotificationIntentRepository(context),
            coordinator,
            handlers,
            TestServices.Monitor(options ?? ZeroGraceOptions()),
            TimeProvider.System,
            NullLogger<NotificationIntentSweepJob>.Instance);

    private static NotificationIntentOptions ZeroGraceOptions(int maxAttempts = 5) =>
        new() { GraceSeconds = 10, LeaseSeconds = 300, MaxAttempts = maxAttempts, BatchSize = 100 };

    private sealed class StubPresenceTracker(bool online) : IChatPresenceTracker
    {
        public Task MarkOnlineAsync(Guid userId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkOfflineAsync(Guid userId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(online);
    }

    // --- Seeding -----------------------------------------------------------

    private static Customer NewCustomer() =>
        new(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active, $"asha-{Guid.NewGuid():N}@example.com");

    private static Booking NewBooking(Customer customer, Guid serviceId, string serviceSlug)
    {
        var booking = new Booking(
            Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
            new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
        booking.AddItem(Guid.NewGuid(), serviceId, "Deep Clean", serviceSlug, 999m, 1);
        return booking;
    }

    /// <summary>
    /// Seeds a booking sitting at PaymentPending, with its domain events
    /// already drained, so a test can perform exactly one interesting
    /// transition and look at exactly the intents that transition produced.
    /// </summary>
    private async Task<(Customer Customer, Guid BookingId)> SeedPaymentPendingBookingAsync()
    {
        var customer = NewCustomer();
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var slug = "deep-clean-" + Guid.NewGuid();
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", slug, "desc", 999m);
        var booking = NewBooking(customer, service.Id, slug);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.ClearDomainEvents();

        using var context = _db.CreateContext();
        context.Add(customer);
        context.Add(category);
        context.Add(service);
        context.Add(booking);
        await context.SaveChangesAsync();

        return (customer, booking.Id);
    }

    private async Task<IReadOnlyList<NotificationIntent>> IntentsForAsync(Guid domainEventId)
    {
        using var context = _db.CreateContext();
        return await context.NotificationIntents
            .AsNoTracking()
            .Where(intent => intent.DomainEventId == domainEventId)
            .OrderBy(intent => intent.EventType)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<NotificationEvent>> NotificationsForAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return await new NotificationEventRepository(context).ListByCustomerAsync(customerId);
    }

    // --- 1. The intent commits with the state change it justifies ----------

    /// <summary>
    /// The property the whole feature rests on: the row saying "this customer
    /// is owed a message" is written by the <i>same</i> <c>SaveChanges</c> as
    /// the status change that warrants it. If it were written afterwards there
    /// would still be a window - a smaller one, but the same bug.
    /// </summary>
    [Fact]
    public async Task Transitioning_a_booking_writes_its_notification_intents_in_the_same_SaveChanges()
    {
        var (_, bookingId) = await SeedPaymentPendingBookingAsync();

        Guid domainEventId;
        using (var context = CreateIntentWritingContext())
        {
            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.Confirmed);
            domainEventId = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single().EventId;

            // Nothing has been written yet - the intent must not exist until
            // the transition it belongs to does.
            (await context.NotificationIntents.AsNoTracking().CountAsync(i => i.DomainEventId == domainEventId))
                .Should().Be(0);

            await context.SaveChangesAsync();
        }

        var intents = await IntentsForAsync(domainEventId);

        intents.Select(i => i.EventType).Should().BeEquivalentTo(
            new[] { NotificationEventType.BookingConfirmed, NotificationEventType.PaymentSuccess },
            "a confirmed booking owes the customer both 'confirmed' and 'payment received'");
        intents.Should().OnlyContain(i => i.Status == NotificationIntentStatus.Pending);
        intents.Should().OnlyContain(i => i.AttemptCount == 0);
        intents.Should().OnlyContain(i => i.DomainEventType == nameof(BookingStatusChangedEvent));
    }

    /// <summary>
    /// The other half of "atomic": a transaction that rolls back must take the
    /// intents with it. Otherwise the sweep would cheerfully announce a
    /// confirmation that never happened - a failure mode strictly worse than
    /// the silence this feature replaces.
    /// </summary>
    [Fact]
    public async Task Rolling_back_the_transaction_discards_the_intent_with_the_state_change()
    {
        var (_, bookingId) = await SeedPaymentPendingBookingAsync();

        Guid domainEventId;
        using (var context = CreateIntentWritingContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.Confirmed);
            domainEventId = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single().EventId;
            await context.SaveChangesAsync();

            await transaction.RollbackAsync();
        }

        (await IntentsForAsync(domainEventId)).Should().BeEmpty();

        using var readContext = _db.CreateContext();
        (await readContext.Bookings.AsNoTracking().FirstAsync(b => b.Id == bookingId)).Status
            .Should().Be(BookingStatus.PaymentPending, "the state change rolled back, so its notification must have too");
    }

    /// <summary>
    /// <b>The "all four, or it is a half-truth" test.</b> Task 294's brief is
    /// explicit that landing this on three of the four handler families is a
    /// failed task, and the two ways it could silently end up covering three
    /// are both checked here: the planner not planning a family's event (no
    /// durable row is ever written for it), and no handler owning it (a row is
    /// written that the sweep can never deliver, so it is retried to
    /// Abandoned). Every event either has a complete path or fails this.
    /// </summary>
    [Fact]
    public void Every_notification_warranting_event_is_planned_and_owned_by_exactly_one_handler()
    {
        var customerId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        (IDomainEvent Event, NotificationEventType Expected)[] cases =
        [
            (new BookingStatusChangedEvent(bookingId, BookingStatus.PaymentPending, BookingStatus.PaymentFailed), NotificationEventType.PaymentFailed),
            (new BookingStatusChangedEvent(bookingId, BookingStatus.Assigned, BookingStatus.Completed), NotificationEventType.JobCompleted),
            (new ProviderAssignmentAcceptedEvent(Guid.NewGuid(), bookingId, Guid.NewGuid(), DateTime.UtcNow), NotificationEventType.ProviderAssigned),
            (new BookingProviderChangedEvent(bookingId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PreviousAssignmentAccepted: true), NotificationEventType.ProviderChanged),
            (new ChatMessageSentEvent(Guid.NewGuid(), Guid.NewGuid(), ChatContextType.Booking, bookingId, Guid.NewGuid(), ChatSenderType.Admin, "hi", DateTime.UtcNow), NotificationEventType.NewChatMessage),
            (new SupportTicketStatusChangedEvent(Guid.NewGuid(), customerId, SupportTicketStatus.Open, SupportTicketStatus.Resolved), NotificationEventType.SupportTicketUpdate),
            (new SubscriptionRenewedEvent(Guid.NewGuid(), customerId), NotificationEventType.SubscriptionRenewed),
            (new SubscriptionExpiringSoonEvent(Guid.NewGuid(), customerId), NotificationEventType.SubscriptionExpiringSoon),
            (new SubscriptionPaymentFailedEvent(Guid.NewGuid(), customerId, IsFinal: true), NotificationEventType.SubscriptionPaymentFailed)
        ];

        using var context = _db.CreateContext();
        var coordinator = TestServices.IntentCoordinator(context);
        INotificationTriggerHandler[] handlers =
        [
            BuildBookingHandler(context, coordinator),
            BuildChatHandler(context, coordinator),
            BuildTicketHandler(context, coordinator),
            BuildSubscriptionHandler(context, coordinator)
        ];

        foreach (var (domainEvent, expected) in cases)
        {
            NotificationIntentPlanner.Plan(domainEvent).Should().Contain(
                expected, "{0} owes the customer a {1}", domainEvent.GetType().Name, expected);

            handlers.Count(handler => handler.CanHandle(domainEvent.GetType())).Should().Be(
                1, "exactly one handler must own {0}, or the sweep has nothing to hand it to", domainEvent.GetType().Name);

            // The type has to survive the database round trip too - the sweep
            // resolves it from the name stored on the row.
            NotificationIntentPlanner.ResolveEventType(domainEvent.GetType().Name)
                .Should().Be(domainEvent.GetType());
        }
    }

    // --- 2. The sweep delivers what the in-process path never did ----------

    /// <summary>
    /// The scenario the whole task exists for: the transaction committed and
    /// the process died before dispatching. Nothing here ever invokes the
    /// in-process handler - the sweep is the only thing that runs, and the
    /// customer still gets told.
    /// </summary>
    [Fact]
    public async Task Sweep_delivers_an_intent_the_in_process_handler_never_ran()
    {
        var (customer, bookingId) = await SeedPaymentPendingBookingAsync();

        Guid domainEventId;
        using (var context = CreateIntentWritingContext())
        {
            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.PaymentFailed);
            domainEventId = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single().EventId;
            await context.SaveChangesAsync();
        }

        (await NotificationsForAsync(customer.Id)).Should().BeEmpty("the in-process handler was never invoked");

        // Backdate past the grace period, the same way a real intent ages
        // while nobody is sending it.
        await BackdateAsync(domainEventId, TimeSpan.FromMinutes(10));

        int dispatched;
        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            var handler = BuildBookingHandler(context, coordinator);
            dispatched = await BuildSweepJob(context, coordinator, [handler]).SweepAsync();
        }

        dispatched.Should().Be(1);
        (await NotificationsForAsync(customer.Id)).Should()
            .Contain(n => n.EventType == NotificationEventType.PaymentFailed, "the sweep is the retry path the architecture doc requires");
        (await IntentsForAsync(domainEventId)).Should().OnlyContain(i => i.Status == NotificationIntentStatus.Delivered);
    }

    /// <summary>
    /// The sweep must reach the handler that owns the event and no other.
    /// Covered here on a second family so the routing is not accidentally
    /// booking-shaped.
    /// </summary>
    [Fact]
    public async Task Sweep_delivers_a_support_ticket_intent_the_in_process_handler_never_ran()
    {
        var customer = NewCustomer();
        Guid ticketId;
        using (var context = _db.CreateContext())
        {
            var ticket = new SupportTicket(Guid.NewGuid(), customer.Id, null, SupportTicketCategory.GeneralInquiry, "Question", "desc");
            context.Add(customer);
            context.Add(ticket);
            await context.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        Guid domainEventId;
        using (var context = CreateIntentWritingContext())
        {
            var ticket = await context.SupportTickets.FirstAsync(t => t.Id == ticketId);
            ticket.ChangeStatus(SupportTicketStatus.InProgress);
            domainEventId = ticket.DomainEvents.OfType<SupportTicketStatusChangedEvent>().Single().EventId;
            await context.SaveChangesAsync();
        }

        await BackdateAsync(domainEventId, TimeSpan.FromMinutes(10));

        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            var handler = BuildTicketHandler(context, coordinator);
            (await BuildSweepJob(context, coordinator, [handler]).SweepAsync()).Should().Be(1);
        }

        (await NotificationsForAsync(customer.Id)).Should()
            .Contain(n => n.EventType == NotificationEventType.SupportTicketUpdate);
    }

    // --- 3. The sweep does not re-send what was already delivered ----------

    /// <summary>
    /// The idempotency rule. The in-process handler runs normally and the
    /// sweep runs afterwards over the same intents; the customer must be told
    /// once, not twice. Without the claim, "at-least-once with a retry" would
    /// simply mean "everybody gets everything twice", which is its own kind of
    /// broken.
    /// </summary>
    [Fact]
    public async Task Sweep_does_not_re_send_what_the_in_process_path_already_delivered()
    {
        var (customer, bookingId) = await SeedPaymentPendingBookingAsync();

        BookingStatusChangedEvent statusChanged;
        using (var context = CreateIntentWritingContext())
        {
            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.Confirmed);
            statusChanged = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single();
            await context.SaveChangesAsync();
        }

        // The in-process fast path, exactly as DomainEventDispatchInterceptor
        // would invoke it.
        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            await BuildBookingHandler(context, coordinator)
                .Handle(new DomainEventNotification<BookingStatusChangedEvent>(statusChanged), CancellationToken.None);
        }

        var afterInProcess = await NotificationsForAsync(customer.Id);
        afterInProcess.Should().NotBeEmpty();
        (await IntentsForAsync(statusChanged.EventId)).Should().OnlyContain(i => i.Status == NotificationIntentStatus.Delivered);

        await BackdateAsync(statusChanged.EventId, TimeSpan.FromMinutes(10));

        int dispatched;
        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            var handler = BuildBookingHandler(context, coordinator);
            dispatched = await BuildSweepJob(context, coordinator, [handler]).SweepAsync();
        }

        dispatched.Should().Be(0, "a delivered intent is not even a sweep candidate");
        (await NotificationsForAsync(customer.Id)).Should().HaveCount(
            afterInProcess.Count, "the customer must not be told the same thing twice");
    }

    /// <summary>
    /// The same rule one level down, without the sweep: two coordinators - two
    /// app instances - racing for one intent. Exactly one may send. This is
    /// the assertion that pins the claim to a conditional UPDATE rather than a
    /// read-then-write.
    /// </summary>
    [Fact]
    public async Task Two_instances_racing_for_one_intent_produce_one_send()
    {
        var (customer, bookingId) = await SeedPaymentPendingBookingAsync();

        BookingStatusChangedEvent statusChanged;
        using (var context = CreateIntentWritingContext())
        {
            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.Confirmed);
            statusChanged = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single();
            await context.SaveChangesAsync();
        }

        var sends = 0;

        using (var first = _db.CreateContext())
        using (var second = _db.CreateContext())
        {
            var firstCoordinator = TestServices.IntentCoordinator(first, ZeroGraceOptions());
            var secondCoordinator = TestServices.IntentCoordinator(second, ZeroGraceOptions());

            await firstCoordinator.DeliverAsync(
                statusChanged, NotificationEventType.BookingConfirmed, _ => { sends++; return Task.CompletedTask; });
            await secondCoordinator.DeliverAsync(
                statusChanged, NotificationEventType.BookingConfirmed, _ => { sends++; return Task.CompletedTask; });
        }

        sends.Should().Be(1);
        (await NotificationsForAsync(customer.Id)).Should().BeEmpty("neither closure actually dispatched anything");
    }

    // --- 4. The retry bound has a terminal state --------------------------

    /// <summary>
    /// An intent that can never succeed must stop being retried. Without a
    /// terminal state it is selected by every sweep for the rest of the
    /// system's life, and - worse - the fact that a customer was owed
    /// something and never got it stays invisible, buried in a pending row
    /// indistinguishable from one that is about to work.
    /// </summary>
    [Fact]
    public async Task An_intent_that_exhausts_its_attempts_becomes_terminal_and_stops_being_swept()
    {
        var (_, bookingId) = await SeedPaymentPendingBookingAsync();

        Guid domainEventId;
        using (var context = CreateIntentWritingContext())
        {
            var booking = await context.Bookings.FirstAsync(b => b.Id == bookingId);
            booking.TransitionTo(BookingStatus.PaymentFailed);
            domainEventId = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single().EventId;
            await context.SaveChangesAsync();
        }

        await BackdateAsync(domainEventId, TimeSpan.FromMinutes(10));

        var options = ZeroGraceOptions(maxAttempts: 2);

        // Burn both attempts on a send that always fails.
        for (var attempt = 0; attempt < options.MaxAttempts; attempt++)
        {
            using var context = _db.CreateContext();
            var coordinator = TestServices.IntentCoordinator(context, options);
            var failing = new AlwaysFailingTriggerHandler();
            var swept = await BuildSweepJob(context, coordinator, [failing], options).SweepAsync();
            swept.Should().Be(0, "a failing handler dispatched nothing");
            failing.Invocations.Should().Be(1, "attempt {0} should have been offered exactly once", attempt + 1);
        }

        (await IntentsForAsync(domainEventId)).Should().OnlyContain(
            i => i.Status == NotificationIntentStatus.Pending && i.AttemptCount == options.MaxAttempts,
            "the bound is reached but nothing has declared it terminal yet");

        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, options);
            var failing = new AlwaysFailingTriggerHandler();
            (await BuildSweepJob(context, coordinator, [failing], options).SweepAsync()).Should().Be(0);
            failing.Invocations.Should().Be(0, "an exhausted intent must not be offered to a handler again");
        }

        var abandoned = await IntentsForAsync(domainEventId);
        abandoned.Should().OnlyContain(i => i.Status == NotificationIntentStatus.Abandoned);
        abandoned.Should().OnlyContain(i => i.Resolution != null);
    }

    /// <summary>
    /// A handler that stands in for "the dependency is down". Reports how many
    /// times the sweep actually offered it work, which is what distinguishes a
    /// retry bound from a sweep that quietly stopped selecting the row for
    /// some other reason.
    /// </summary>
    private sealed class AlwaysFailingTriggerHandler : INotificationTriggerHandler
    {
        public int Invocations { get; private set; }

        public bool CanHandle(Type domainEventType) => domainEventType == typeof(BookingStatusChangedEvent);

        public Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("Simulated dispatch failure.");
        }
    }

    // --- 5. Deliberate silence is recorded, not retried -------------------

    /// <summary>
    /// A recipient who is online gets no push - that is task 194's rule, not a
    /// failure - and the intent has to say so. If a deliberate silence left
    /// the row pending, every online chat recipient would generate a sweep
    /// candidate that retried until it was abandoned, and the Abandoned state
    /// would stop meaning anything.
    /// </summary>
    [Fact]
    public async Task A_deliberately_withheld_notification_is_resolved_as_skipped_not_left_pending()
    {
        var customer = NewCustomer();
        Guid ticketId;
        Guid threadId;
        using (var context = _db.CreateContext())
        {
            var ticket = new SupportTicket(Guid.NewGuid(), customer.Id, null, SupportTicketCategory.GeneralInquiry, "Question", "desc");
            var thread = new ChatThread(Guid.NewGuid(), ChatContextType.SupportTicket, ticket.Id);
            context.Add(customer);
            context.Add(ticket);
            context.Add(thread);
            await context.SaveChangesAsync();
            ticketId = ticket.Id;
            threadId = thread.Id;
        }

        ChatMessageSentEvent messageSent;
        using (var context = CreateIntentWritingContext())
        {
            var message = new ChatMessage(
                Guid.NewGuid(), threadId, ChatContextType.SupportTicket, ticketId,
                Guid.NewGuid(), ChatSenderType.Admin, "We are on it.");
            messageSent = message.DomainEvents.OfType<ChatMessageSentEvent>().Single();
            context.Add(message);
            await context.SaveChangesAsync();
        }

        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            await BuildChatHandler(context, coordinator, recipientOnline: true)
                .Handle(new DomainEventNotification<ChatMessageSentEvent>(messageSent), CancellationToken.None);
        }

        var intents = await IntentsForAsync(messageSent.EventId);
        intents.Should().ContainSingle().Which.Status.Should().Be(NotificationIntentStatus.Skipped);
        (await NotificationsForAsync(customer.Id)).Should().BeEmpty();

        await BackdateAsync(messageSent.EventId, TimeSpan.FromMinutes(10));

        using (var context = _db.CreateContext())
        {
            var coordinator = TestServices.IntentCoordinator(context, ZeroGraceOptions());
            var handler = BuildChatHandler(context, coordinator);
            (await BuildSweepJob(context, coordinator, [handler]).SweepAsync())
                .Should().Be(0, "a skipped intent is terminal");
        }
    }

    // --- 6. The dedupe key survives the round trip ------------------------

    /// <summary>
    /// The join between the two delivery paths is the dedupe key, and the
    /// sweep rebuilds it from a deserialized event. If the event's identity
    /// did not survive being written down - which is precisely what happens
    /// when <c>DomainEvent.EventId</c> is get-only rather than <c>init</c> -
    /// the sweep would compute a key the in-process path never wrote, find no
    /// row, fail open, and re-send every notification it touched. That failure
    /// is silent and this assertion is the only thing standing in front of it.
    /// </summary>
    [Fact]
    public void A_serialized_domain_event_keeps_its_identity_and_therefore_its_dedupe_key()
    {
        var original = new BookingStatusChangedEvent(Guid.NewGuid(), BookingStatus.PaymentPending, BookingStatus.Confirmed);

        var payload = DomainEventPayloadSerializer.Serialize(original);
        var eventType = NotificationIntentPlanner.ResolveEventType(nameof(BookingStatusChangedEvent));
        eventType.Should().NotBeNull();

        var rehydrated = DomainEventPayloadSerializer.Deserialize(payload, eventType!);

        rehydrated.Should().BeOfType<BookingStatusChangedEvent>();
        rehydrated!.EventId.Should().Be(original.EventId);
        NotificationIntent.BuildDedupeKey(rehydrated.EventId, NotificationEventType.BookingConfirmed)
            .Should().Be(NotificationIntent.BuildDedupeKey(original.EventId, NotificationEventType.BookingConfirmed));
    }

    /// <summary>
    /// Two occurrences of the same business fact must not collide. A booking
    /// can be rescheduled twice, and a key built from the booking id and the
    /// message type would silently swallow the second notification as a
    /// duplicate - which is why the key is built from the event's identity.
    /// </summary>
    [Fact]
    public void Two_occurrences_of_the_same_transition_get_different_keys()
    {
        var bookingId = Guid.NewGuid();
        var first = new BookingStatusChangedEvent(bookingId, BookingStatus.Confirmed, BookingStatus.Rescheduled);
        var second = new BookingStatusChangedEvent(bookingId, BookingStatus.Confirmed, BookingStatus.Rescheduled);

        NotificationIntent.BuildDedupeKey(first.EventId, NotificationEventType.BookingRescheduled)
            .Should().NotBe(NotificationIntent.BuildDedupeKey(second.EventId, NotificationEventType.BookingRescheduled));
    }

    // --- Helpers ----------------------------------------------------------

    /// <summary>
    /// Ages an intent past the sweep's grace period. Time travel rather than
    /// waiting: the grace period is a real production value and the test is
    /// about what happens after it, not about how long it is.
    /// </summary>
    private async Task BackdateAsync(Guid domainEventId, TimeSpan age)
    {
        using var context = _db.CreateContext();
        var cutoff = DateTime.UtcNow - age;
        await context.NotificationIntents
            .Where(intent => intent.DomainEventId == domainEventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(intent => intent.CreatedAtUtc, cutoff));
    }
}
