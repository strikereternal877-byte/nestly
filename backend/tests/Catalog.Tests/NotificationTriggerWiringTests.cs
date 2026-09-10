using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Identity;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Application.Support;
using Nestly.BuildingBlocks.Privacy;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 88a-g: notification trigger wiring for welcome, booking confirmed, payment success/failure, cancellation, reschedule, refund, and ticket updates.</summary>
public sealed class NotificationTriggerWiringTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public NotificationTriggerWiringTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context), new InMemoryCacheService()),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)),
            new WalletService(new WalletLedgerRepository(context), context),
        new ServiceabilityRepository(context),
        TestServices.BookingOptions());

        return new BookingService(
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            couponService,
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);
    }

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private static BookingNotificationTriggerHandler BuildBookingHandler(
        Nestly.Infrastructure.Persistence.NestlyDbContext context,
        IOptionsMonitor<FulfilmentNotificationOptions>? fulfilmentOptions = null) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new BookingCancellationRepository(context),
            new RefundTransactionRepository(context),
            new ProviderRepository(context),
            BuildDispatchService(context),
            TestServices.IntentCoordinator(context),
            fulfilmentOptions ?? TestServices.FulfilmentNotifications(),
            NullLogger<BookingNotificationTriggerHandler>.Instance);

    private static SupportTicketNotificationTriggerHandler BuildTicketHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new CustomerRepository(context), new SupportTicketRepository(context), new DeviceTokenRepository(context),
            BuildDispatchService(context), TestServices.IntentCoordinator(context),
            NullLogger<SupportTicketNotificationTriggerHandler>.Instance);

    private static NotificationDispatchService BuildDispatchService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), new SandboxNotificationProvider(NullLogger<SandboxNotificationProvider>.Instance),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(context),
            new DeviceTokenRepository(context), new CustomerRepository(context), new ProviderRepository(context),
            new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total);

    private async Task<(Fixture Fixture, string GatewayOrderId)> SeedPendingPaymentBookingAsync(IPaymentGateway gateway, decimal servicePrice = 1000m)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        Guid bookingId;
        decimal total;
        Customer customer;

        using (var context = _db.CreateContext())
        {
            customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active, $"asha-{Guid.NewGuid():N}@example.com");
            var address = new CustomerAddress(
                Guid.NewGuid(), customer.Id, "Home", "221B Baker Street", null, null,
                pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
            var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
            var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
            var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
            var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
            var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
            address.LinkToGeography(pincode.Id, locality.Id);
            var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
            var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", servicePrice);
            var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

            context.Add(customer);
            context.Add(address);
            context.States.Add(state);
            context.Cities.Add(city);
            context.Zones.Add(zone);
            context.Pincodes.Add(pincode);
            context.Localities.Add(locality);
            context.Add(category);
            context.Add(service);
            context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
            context.SlotWindows.Add(window);
            context.SlotWindowRules.Add(rule);
            context.SaveChanges();

            var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
            var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
            created.IsSuccess.Should().BeTrue();
            bookingId = created.Value.Id;
            total = created.Value.Price.TotalPayable;
        }

        string gatewayOrderId;
        using (var orderContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(orderContext);
            var bookingRepository = new BookingRepository(orderContext);
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, orderContext, gateway),
                new AlwaysEligibleProviderSearchStub());
            var order = await paymentService.CreateOrderAsync(customer.Id, new CreatePaymentOrderRequest(bookingId, null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        return (new Fixture(customer, bookingId, total), gatewayOrderId);
    }

    /// <summary>Captures the OTP SMS so the test can read the plaintext code back out - OtpService only ever persists a hash.</summary>
    private sealed class OtpCapturingNotificationProvider : INotificationProvider
    {
        public string? LastSmsMessage { get; private set; }

        public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
        {
            LastSmsMessage = message;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task RegisterAsync_dispatches_a_welcome_notification()
    {
        var mobile = "9" + Guid.NewGuid().ToString("N")[..9];
        var otpProvider = new OtpCapturingNotificationProvider();
        Guid customerId;

        using (var context = _db.CreateContext())
        {
            var otpService = new OtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" }));
            await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        }

        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        using (var context = _db.CreateContext())
        {
            var registrationService = new CustomerRegistrationService(
                new CustomerRepository(context), new CustomerAuthIdentityRepository(context), new OtpService(context, otpProvider, Options.Create(new OtpOptions { Pepper = "test-only-otp-pepper-not-for-production-abc123" })),
                BuildDispatchService(context), new ReferralRepository(context), new ReferralProgramConfigRepository(context),
                NullLogger<CustomerRegistrationService>.Instance, Options.Create(new AccountOptions()));

            var result = await registrationService.RegisterAsync(new RegisterCustomerRequest(mobile, otpCode, "Asha Rao", $"asha-{Guid.NewGuid():N}@example.com", null, true));
            result.IsSuccess.Should().BeTrue();
            customerId = result.Value.Id;
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
        notifications.Should().Contain(n => n.EventType == NotificationEventType.Welcome && n.Channel == NotificationChannel.Sms);
        notifications.Should().Contain(n => n.EventType == NotificationEventType.Welcome && n.Channel == NotificationChannel.Email);
    }

    [Fact]
    public async Task Payment_success_dispatches_both_BookingConfirmed_and_PaymentSuccess()
    {
        var gateway = BuildGateway();
        var (fixture, gatewayOrderId) = await SeedPendingPaymentBookingAsync(gateway, 1101m);

        using (var callbackContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(callbackContext);
            var bookingRepository = new BookingRepository(callbackContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, callbackContext, gateway);
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();
        }

        using (var handlerContext = _db.CreateContext())
        {
            var handler = BuildBookingHandler(handlerContext);
            await handler.Handle(new DomainEventNotification<BookingStatusChangedEvent>(new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.PaymentPending, BookingStatus.Confirmed)), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(fixture.Customer.Id);
        notifications.Should().Contain(n => n.EventType == NotificationEventType.BookingConfirmed);
        notifications.Should().Contain(n => n.EventType == NotificationEventType.PaymentSuccess);
        notifications.Should().OnlyContain(n => n.Status == NotificationDeliveryStatus.Sent);
    }

    /// <summary>
    /// Task 359, the counterpart to the test above. Task 331 gave a booking
    /// with nothing payable - an AMC entitlement redemption, a fully
    /// wallet-covered checkout, a discount that takes the total to zero - its
    /// own path straight to Confirmed with no payment behind it. Keyed on
    /// status alone, that transition owed the customer a "your payment was
    /// successful" message describing a charge that never happened.
    ///
    /// <para>
    /// Driven from a real <see cref="Booking"/> rather than a hand-built event,
    /// so it covers the whole chain the fix runs through: the price snapshot,
    /// the event the transition raises, and the plan drawn from it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(659)]
    public void A_confirmed_booking_owes_a_payment_message_only_when_something_was_actually_charged(int totalPayable)
    {
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, totalPayable);
        var booking = new Booking(Guid.NewGuid(), Guid.NewGuid(), new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);

        booking.ClearDomainEvents();
        booking.TransitionTo(BookingStatus.Confirmed);

        var statusChanged = booking.DomainEvents.OfType<BookingStatusChangedEvent>().Single();
        statusChanged.AnythingPayable.Should().Be(totalPayable > 0);

        var planned = NotificationIntentPlanner.Plan(statusChanged);

        planned.Should().Contain(
            NotificationEventType.BookingConfirmed,
            "the booking is confirmed either way - that is the fact the customer needs");
        planned.Should().HaveCount(
            totalPayable > 0 ? 2 : 1,
            "a zero-payable booking is owed the confirmation and nothing else");

        if (totalPayable > 0)
        {
            planned.Should().Contain(NotificationEventType.PaymentSuccess);
        }
        else
        {
            planned.Should().NotContain(
                NotificationEventType.PaymentSuccess,
                "nothing was ever charged, so there is no payment to report as successful");
        }
    }

    /// <summary>
    /// The status-keyed table itself is untouched by task 359 - the
    /// "was anything charged" question is about the booking, not the status,
    /// and every other consumer of this mapping must keep seeing Confirmed's
    /// full pair.
    /// </summary>
    [Fact]
    public void The_shared_status_table_still_maps_Confirmed_to_both_messages()
    {
        NotificationIntentPlanner.EventTypesFor(BookingStatus.Confirmed).Should().BeEquivalentTo(
            [NotificationEventType.BookingConfirmed, NotificationEventType.PaymentSuccess]);
    }

    [Fact]
    public async Task Payment_failure_dispatches_PaymentFailed_only()
    {
        var gateway = BuildGateway();
        var (fixture, gatewayOrderId) = await SeedPendingPaymentBookingAsync(gateway, 1102m);

        using (var callbackContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(callbackContext);
            var bookingRepository = new BookingRepository(callbackContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, callbackContext, gateway);
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.FailedStatus);
            string signature = gateway.SignPayload(payload);
            await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.FailedStatus, signature));
        }

        using (var handlerContext = _db.CreateContext())
        {
            var handler = BuildBookingHandler(handlerContext);
            await handler.Handle(new DomainEventNotification<BookingStatusChangedEvent>(new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.PaymentPending, BookingStatus.PaymentFailed)), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(fixture.Customer.Id);
        notifications.Where(n => n.EventType == NotificationEventType.PaymentFailed).Should().HaveCount(2, "one per channel - SMS and email");
        notifications.Should().NotContain(n => n.EventType == NotificationEventType.BookingConfirmed);
    }

    [Fact]
    public async Task Cancellation_dispatches_BookingCancelled()
    {
        Guid customerId;
        Guid bookingId;
        using (var context = _db.CreateContext())
        {
            customerId = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active).Id;
            var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
            var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
            var customer = new Customer(customerId, "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
            var booking = new Booking(
                Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
                new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
                new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
                new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
            booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);
            booking.TransitionTo(BookingStatus.PaymentPending);
            booking.TransitionTo(BookingStatus.CancelledByCustomer, "Changed my mind");

            context.Add(customer);
            context.Add(category);
            context.Add(service);
            context.Add(booking);
            context.SaveChanges();
            bookingId = booking.Id;
        }

        using (var handlerContext = _db.CreateContext())
        {
            var handler = BuildBookingHandler(handlerContext);
            await handler.Handle(new DomainEventNotification<BookingStatusChangedEvent>(new BookingStatusChangedEvent(bookingId, BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer)), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
        notifications.Should().ContainSingle(n => n.EventType == NotificationEventType.BookingCancelled);
    }

    [Fact]
    public async Task SupportTicket_status_change_dispatches_SupportTicketUpdate()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active, $"asha-{Guid.NewGuid():N}@example.com");
            context.Add(customer);
            context.SaveChanges();
            customerId = customer.Id;
        }

        Guid ticketId;
        using (var context = _db.CreateContext())
        {
            var ticket = new SupportTicket(Guid.NewGuid(), customerId, null, SupportTicketCategory.GeneralInquiry, "Question", "desc");
            await new SupportTicketRepository(context).AddAsync(ticket);
            ticketId = ticket.Id;
        }

        using (var context = _db.CreateContext())
        {
            var repository = new SupportTicketRepository(context);
            var ticket = await repository.GetByIdAsync(ticketId);
            ticket!.ChangeStatus(SupportTicketStatus.InProgress);
            await repository.UpdateAsync(ticket);

            var handler = BuildTicketHandler(context);
            await handler.Handle(
                new DomainEventNotification<SupportTicketStatusChangedEvent>(new SupportTicketStatusChangedEvent(ticketId, customerId, SupportTicketStatus.Open, SupportTicketStatus.InProgress)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
        notifications.Where(n => n.EventType == NotificationEventType.SupportTicketUpdate).Should().HaveCount(2, "one per channel - SMS and email");
    }

    // --- Task 276: the fulfilment half of the lifecycle ---
    //
    // Every case below drives the handler with the BookingStatusChangedEvent
    // that Booking.TransitionTo raises for the transition under test, which is
    // the same object MediatR delivers in production. Two channels are always
    // in play (the seeded customer has both a mobile and an email, and no
    // device tokens), so "fired exactly once" reads as exactly two
    // notification_event rows - one per channel - and a double-send shows up
    // as four.
    //
    // SQLite/PostgreSQL divergence: TestDatabase runs EnsureCreated on
    // in-memory SQLite and never applies migrations, so the notification_template
    // rows that 20260807104500_SeedFulfilmentNotificationTemplates inserts on a
    // real database are absent here. The templates reach these tests through
    // FakeNotificationTemplateRepository, which reads the same
    // NotificationTemplateSeedData.BuildDefaults the migration inserts from -
    // one source, two consumers, so a template added to one and not the other
    // cannot pass both. What is genuinely not covered here is the migration's
    // own INSERT running against Postgres.

    /// <summary>
    /// Seeds a booking walked to <paramref name="target"/> through the real
    /// lifecycle, with a provider assigned, and returns the customer/booking
    /// ids and the status it came from - the handler is driven with a
    /// (from, to) pair, and inventing one that the lifecycle cannot produce
    /// would test a transition that never happens.
    /// </summary>
    /// <summary>Provider.Phone is uniquely indexed, so every seeded provider needs its own - the fixture's database is shared by every test in this class.</summary>
    private static int _providerPhoneSequence;

    private async Task<(Guid CustomerId, Guid BookingId, BookingStatus From, string ProviderDisplayName, string ProviderPhone)> SeedFulfilmentBookingAsync(BookingStatus target)
    {
        var path = new List<BookingStatus>
        {
            BookingStatus.PaymentPending,
            BookingStatus.Confirmed,
            BookingStatus.AwaitingFulfilment,
            BookingStatus.Assigned
        };

        if (target is BookingStatus.ProviderEnRoute or BookingStatus.ProviderArrived)
        {
            path.Add(BookingStatus.ProviderEnRoute);
        }

        if (target == BookingStatus.ProviderArrived)
        {
            path.Add(BookingStatus.ProviderArrived);
        }

        if (target is BookingStatus.InProgress or BookingStatus.Completed)
        {
            path.Add(BookingStatus.InProgress);
        }

        if (target == BookingStatus.Completed)
        {
            path.Add(BookingStatus.Completed);
        }

        path[^1].Should().Be(target, "the walk has to end on the status under test, so the from-status below is the real predecessor");

        using var context = _db.CreateContext();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active, $"asha-{Guid.NewGuid():N}@example.com");
        string providerPhone = "+9198765" + Interlocked.Increment(ref _providerPhoneSequence).ToString("D5");
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi Kumar", ProviderType.Individual, providerPhone);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
        var booking = new Booking(
            Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
            new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);

        foreach (var status in path)
        {
            booking.TransitionTo(status);
        }

        booking.AssignProvider(provider.Id);

        context.Add(customer);
        context.Add(provider);
        context.Add(category);
        context.Add(service);
        context.Add(booking);
        await context.SaveChangesAsync();

        return (customer.Id, booking.Id, path[^2], provider.DisplayName, providerPhone);
    }

    private async Task<IReadOnlyList<NotificationEvent>> HandleAndReadAsync(
        Guid customerId, Guid bookingId, BookingStatus from, BookingStatus to,
        IOptionsMonitor<FulfilmentNotificationOptions>? fulfilmentOptions = null)
    {
        using (var handlerContext = _db.CreateContext())
        {
            var handler = BuildBookingHandler(handlerContext, fulfilmentOptions);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(new BookingStatusChangedEvent(bookingId, from, to)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        return await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
    }

    // Assigned is absent from every theory below since task 295: reaching it
    // is an offer, not an acceptance, and it now dispatches nothing at all.
    // The acceptance-driven ProviderAssigned has its own section further down.
    [Theory]
    [InlineData(BookingStatus.ProviderEnRoute, NotificationEventType.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived, NotificationEventType.ProviderArrived)]
    [InlineData(BookingStatus.InProgress, NotificationEventType.JobStarted)]
    [InlineData(BookingStatus.Completed, NotificationEventType.JobCompleted)]
    public async Task Each_fulfilment_transition_dispatches_its_own_event_exactly_once(BookingStatus target, NotificationEventType expected)
    {
        var (customerId, bookingId, from, _, _) = await SeedFulfilmentBookingAsync(target);

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, target);

        notifications.Where(n => n.EventType == expected).Should().HaveCount(2, "one per channel - SMS and email - and no more");
        notifications.Should().OnlyContain(n => n.EventType == expected, "a fulfilment transition dispatches exactly one event type");
        notifications.Should().OnlyContain(n => n.Status == NotificationDeliveryStatus.Sent);
    }

    [Fact]
    public async Task Fulfilment_notifications_name_the_assigned_provider()
    {
        var (customerId, bookingId, from, providerName, _) = await SeedFulfilmentBookingAsync(BookingStatus.ProviderEnRoute);

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, BookingStatus.ProviderEnRoute);

        notifications.Should().OnlyContain(n => n.PayloadJson!.Contains(providerName));
    }

    /// <summary>
    /// The provider's phone reaches templates already masked. Asserted on the
    /// persisted payload because that is the same dictionary the renderer
    /// substitutes from - a raw number here is a raw number one
    /// admin template edit away from an SMS, since template bodies are
    /// editable at runtime.
    /// </summary>
    [Fact]
    public async Task Provider_mobile_reaches_templates_masked_and_never_raw()
    {
        var (customerId, bookingId, from, _, providerPhone) = await SeedFulfilmentBookingAsync(BookingStatus.ProviderArrived);

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, BookingStatus.ProviderArrived);

        notifications.Should().NotBeEmpty();
        notifications.Should().OnlyContain(n => !n.PayloadJson!.Contains(providerPhone));
        notifications.Should().OnlyContain(n => n.PayloadJson!.Contains(ContactMasking.Mask(providerPhone)));
    }

    [Theory]
    [InlineData(BookingStatus.ProviderEnRoute, NotificationEventType.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived, NotificationEventType.ProviderArrived)]
    [InlineData(BookingStatus.InProgress, NotificationEventType.JobStarted)]
    [InlineData(BookingStatus.Completed, NotificationEventType.JobCompleted)]
    public async Task Muting_one_fulfilment_event_suppresses_it(BookingStatus target, NotificationEventType muted)
    {
        var (customerId, bookingId, from, _, _) = await SeedFulfilmentBookingAsync(target);

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, target, MuteOnly(muted));

        notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(BookingStatus.ProviderEnRoute, NotificationEventType.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived, NotificationEventType.ProviderArrived)]
    [InlineData(BookingStatus.InProgress, NotificationEventType.JobStarted)]
    [InlineData(BookingStatus.Completed, NotificationEventType.JobCompleted)]
    public async Task Muting_the_other_fulfilment_events_leaves_this_one_alone(BookingStatus target, NotificationEventType expected)
    {
        var (customerId, bookingId, from, _, _) = await SeedFulfilmentBookingAsync(target);

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, target, MuteAllExcept(expected));

        notifications.Where(n => n.EventType == expected).Should().HaveCount(2, "muting the other four must not touch this one");
    }

    /// <summary>
    /// The mute is scoped to the five fulfilment events. Muting every one of
    /// them must leave the money-and-cancellation notifications alone -
    /// FulfilmentNotificationOptions is deliberately not a general
    /// "notifications off" switch.
    /// </summary>
    [Fact]
    public async Task Muting_every_fulfilment_event_does_not_mute_the_cancellation_notification()
    {
        var (customerId, bookingId, _, _, _) = await SeedFulfilmentBookingAsync(BookingStatus.Assigned);
        var allMuted = TestServices.FulfilmentNotifications(false, false, false, false, false, false);

        var notifications = await HandleAndReadAsync(
            customerId, bookingId, BookingStatus.Assigned, BookingStatus.CancelledByCustomer, allMuted);

        notifications.Should().OnlyContain(n => n.EventType == NotificationEventType.BookingCancelled);
        notifications.Should().HaveCount(2, "one per channel");
    }

    /// <summary>
    /// A provider rejecting the job walks the booking Assigned -&gt;
    /// AwaitingFulfilment. That is a fulfilment-half transition immediately
    /// adjacent to two that do notify, and it must stay silent - the customer
    /// cannot act on "your booking is looking for someone again".
    /// </summary>
    [Fact]
    public async Task A_provider_rejection_back_to_AwaitingFulfilment_stays_silent()
    {
        var (customerId, bookingId, _, _, _) = await SeedFulfilmentBookingAsync(BookingStatus.Assigned);

        var notifications = await HandleAndReadAsync(
            customerId, bookingId, BookingStatus.Assigned, BookingStatus.AwaitingFulfilment);

        notifications.Should().BeEmpty();
    }

    private static IOptionsMonitor<FulfilmentNotificationOptions> MuteOnly(NotificationEventType eventType) =>
        TestServices.FulfilmentNotifications(
            providerAssigned: eventType != NotificationEventType.ProviderAssigned,
            providerEnRoute: eventType != NotificationEventType.ProviderEnRoute,
            providerArrived: eventType != NotificationEventType.ProviderArrived,
            jobStarted: eventType != NotificationEventType.JobStarted,
            jobCompleted: eventType != NotificationEventType.JobCompleted,
            providerChanged: eventType != NotificationEventType.ProviderChanged);

    private static IOptionsMonitor<FulfilmentNotificationOptions> MuteAllExcept(NotificationEventType eventType) =>
        TestServices.FulfilmentNotifications(
            providerAssigned: eventType == NotificationEventType.ProviderAssigned,
            providerEnRoute: eventType == NotificationEventType.ProviderEnRoute,
            providerArrived: eventType == NotificationEventType.ProviderArrived,
            jobStarted: eventType == NotificationEventType.JobStarted,
            jobCompleted: eventType == NotificationEventType.JobCompleted,
            providerChanged: eventType == NotificationEventType.ProviderChanged);

    // --- Task 295: who is coming, and when the customer is told ---
    //
    // The rule these pin: ProviderAssigned fires on acceptance and nowhere
    // else, and a change of an *accepted* professional gets its own template
    // rather than a second ProviderAssigned. Same two-channels-per-dispatch
    // arithmetic as the task 276 block above - two rows means "once".

    /// <summary>
    /// Defect (a). AwaitingFulfilment -&gt; Assigned is where
    /// <c>BookingProviderAssignmentService.AssignAsync</c> records an *offer*.
    /// Telling the customer "Rajesh is coming" here is a guess about what
    /// Rajesh will say.
    /// </summary>
    [Fact]
    public async Task An_offer_alone_tells_the_customer_nothing()
    {
        var (customerId, bookingId, from, _, _) = await SeedFulfilmentBookingAsync(BookingStatus.Assigned);
        from.Should().Be(BookingStatus.AwaitingFulfilment, "the offer-time transition is the one under test");

        var notifications = await HandleAndReadAsync(customerId, bookingId, from, BookingStatus.Assigned);

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Acceptance_dispatches_exactly_one_ProviderAssigned_naming_the_accepting_provider()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();

        var notifications = await HandleAcceptanceAsync(seed, seed.FirstProviderId);

        notifications.Where(n => n.EventType == NotificationEventType.ProviderAssigned)
            .Should().HaveCount(2, "one per channel - SMS and email - and no more");
        notifications.Should().OnlyContain(n => n.EventType == NotificationEventType.ProviderAssigned);
        notifications.Should().OnlyContain(n => n.PayloadJson!.Contains(seed.FirstProviderName));
        notifications.Should().OnlyContain(n => n.Status == NotificationDeliveryStatus.Sent);
    }

    /// <summary>
    /// The contradictory pair defect (a) produced: offer to one provider,
    /// rejection, offer to another, and the customer used to be told twice
    /// that two different people were coming. Only the acceptance speaks now,
    /// so the whole sequence yields one name - the right one.
    /// </summary>
    [Fact]
    public async Task An_offer_rejected_and_re_offered_never_names_two_professionals()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();

        await HandleAndReadAsync(seed.CustomerId, seed.BookingId, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned);
        await HandleAndReadAsync(seed.CustomerId, seed.BookingId, BookingStatus.Assigned, BookingStatus.AwaitingFulfilment);
        await HandleAndReadAsync(seed.CustomerId, seed.BookingId, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned);
        await SetAssignedProviderAsync(seed.BookingId, seed.SecondProviderId);

        var notifications = await HandleAcceptanceAsync(seed, seed.SecondProviderId);

        notifications.Where(n => n.EventType == NotificationEventType.ProviderAssigned)
            .Should().HaveCount(2, "the two offers and the rejection say nothing; only the acceptance does");
        notifications.Should().OnlyContain(n => n.PayloadJson!.Contains(seed.SecondProviderName));
        notifications.Should().NotContain(n => n.PayloadJson!.Contains(seed.FirstProviderName));
    }

    /// <summary>
    /// Defect (b). The swap moves no booking status, so nothing in the
    /// <c>BookingStatusChangedEvent</c> stream could carry it - the customer
    /// used to be told nothing and would greet the wrong person at the door.
    /// </summary>
    [Fact]
    public async Task Replacing_an_accepted_professional_sends_the_distinct_ProviderChanged_notification()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();
        await HandleAcceptanceAsync(seed, seed.FirstProviderId);
        await SetAssignedProviderAsync(seed.BookingId, seed.SecondProviderId);

        var notifications = await HandleProviderChangedAsync(seed, previousAccepted: true);

        notifications.Where(n => n.EventType == NotificationEventType.ProviderChanged)
            .Should().HaveCount(2, "one per channel - SMS and email - and no more");
        notifications.Where(n => n.EventType == NotificationEventType.ProviderAssigned)
            .Should().HaveCount(2, "only the original acceptance - a change must never re-send ProviderAssigned");
        notifications.Where(n => n.EventType == NotificationEventType.ProviderChanged)
            .Should().OnlyContain(n => n.PayloadJson!.Contains(seed.FirstProviderName), "the message corrects the name the customer already has");
        notifications.Should().OnlyContain(n => n.Status == NotificationDeliveryStatus.Sent);
    }

    /// <summary>
    /// The other half of the acceptance-only rule: an offer nobody accepted
    /// was never announced, so replacing it is not a change the customer can
    /// have noticed. Telling them their professional changed would name
    /// somebody they never heard of.
    /// </summary>
    [Fact]
    public async Task Replacing_an_offer_that_was_never_accepted_stays_silent()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();

        var notifications = await HandleProviderChangedAsync(seed, previousAccepted: false);

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Muting_ProviderAssigned_suppresses_the_acceptance_notification()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();

        var notifications = await HandleAcceptanceAsync(seed, seed.FirstProviderId, MuteOnly(NotificationEventType.ProviderAssigned));

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Muting_ProviderChanged_suppresses_the_change_notification()
    {
        var seed = await SeedBookingWithTwoProvidersAsync();

        var notifications = await HandleProviderChangedAsync(seed, previousAccepted: true, MuteOnly(NotificationEventType.ProviderChanged));

        notifications.Should().BeEmpty();
    }

    private sealed record TwoProviderSeed(
        Guid CustomerId, Guid BookingId, Guid FirstProviderId, string FirstProviderName, Guid SecondProviderId, string SecondProviderName);

    /// <summary>
    /// A booking walked to Assigned with the first provider in the display
    /// field - the state an offer leaves behind - plus a second provider for
    /// the rejection/reassignment cases. Distinct display names, because every
    /// assertion here is about which of the two the customer was told.
    /// </summary>
    private async Task<TwoProviderSeed> SeedBookingWithTwoProvidersAsync()
    {
        using var context = _db.CreateContext();

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active, $"asha-{Guid.NewGuid():N}@example.com");
        var first = NewProvider("Rajesh Nair");
        var second = NewProvider("Meera Iyer");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
        var booking = new Booking(
            Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
            new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.AssignProvider(first.Id);

        context.AddRange(customer, first, second, category, service, booking);
        await context.SaveChangesAsync();

        return new TwoProviderSeed(customer.Id, booking.Id, first.Id, first.DisplayName, second.Id, second.DisplayName);
    }

    private static Provider NewProvider(string displayName) =>
        new(Guid.NewGuid(), displayName, displayName, ProviderType.Individual, "+9198765" + Interlocked.Increment(ref _providerPhoneSequence).ToString("D5"));

    /// <summary>Moves the denormalized display field on, exactly as <c>AssignAsync</c> does when it supersedes an assignment.</summary>
    private async Task SetAssignedProviderAsync(Guid bookingId, Guid providerId)
    {
        using var context = _db.CreateContext();
        var repository = new BookingRepository(context);
        var booking = await repository.GetByIdAsync(bookingId);
        booking!.AssignProvider(providerId);
        await repository.UpdateAsync(booking);
    }

    /// <summary>
    /// Drives the handler with the acceptance event
    /// <c>BookingProviderAssignment.Accept</c> raises. No assignment row is
    /// needed: the handler takes the provider from the event rather than
    /// re-reading the row, which is what makes it correct for a booking whose
    /// display field has already moved to the next candidate.
    /// </summary>
    private async Task<IReadOnlyList<NotificationEvent>> HandleAcceptanceAsync(
        TwoProviderSeed seed, Guid acceptingProviderId, IOptionsMonitor<FulfilmentNotificationOptions>? fulfilmentOptions = null)
    {
        using (var handlerContext = _db.CreateContext())
        {
            await BuildBookingHandler(handlerContext, fulfilmentOptions).Handle(
                new DomainEventNotification<ProviderAssignmentAcceptedEvent>(
                    new ProviderAssignmentAcceptedEvent(Guid.NewGuid(), seed.BookingId, acceptingProviderId, DateTime.UtcNow)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        return await new NotificationEventRepository(readContext).ListByCustomerAsync(seed.CustomerId);
    }

    private async Task<IReadOnlyList<NotificationEvent>> HandleProviderChangedAsync(
        TwoProviderSeed seed, bool previousAccepted, IOptionsMonitor<FulfilmentNotificationOptions>? fulfilmentOptions = null)
    {
        using (var handlerContext = _db.CreateContext())
        {
            await BuildBookingHandler(handlerContext, fulfilmentOptions).Handle(
                new DomainEventNotification<BookingProviderChangedEvent>(
                    new BookingProviderChangedEvent(
                        seed.BookingId, Guid.NewGuid(), seed.FirstProviderId, seed.SecondProviderId, previousAccepted)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        return await new NotificationEventRepository(readContext).ListByCustomerAsync(seed.CustomerId);
    }

    [Fact]
    public async Task A_transition_with_no_configured_trigger_dispatches_nothing()
    {
        Guid customerId;
        Guid bookingId;
        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
            var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
            var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
            var booking = new Booking(
                Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
                new AddressSnapshot("Home", "221B", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Asha Rao", "9876543210"),
                new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
                new PriceSnapshot(999m, 1, 999m, 0, 0, 999m, 0, 0, 0, 999m));
            booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);
            booking.TransitionTo(BookingStatus.PaymentPending);
            booking.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);

            context.Add(customer);
            context.Add(category);
            context.Add(service);
            context.Add(booking);
            context.SaveChanges();
            customerId = customer.Id;
            bookingId = booking.Id;
        }

        using (var handlerContext = _db.CreateContext())
        {
            var handler = BuildBookingHandler(handlerContext);
            await handler.Handle(new DomainEventNotification<BookingStatusChangedEvent>(new BookingStatusChangedEvent(bookingId, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment)), CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var notifications = await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
        notifications.Should().BeEmpty();
    }
}
