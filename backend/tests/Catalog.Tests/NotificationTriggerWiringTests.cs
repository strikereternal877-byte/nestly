using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Identity;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Application.Support;
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
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context)),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

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
                TimeProvider.System),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private static BookingNotificationTriggerHandler BuildBookingHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new CustomerRepository(context),
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new BookingCancellationRepository(context),
            new RefundTransactionRepository(context),
            new DeviceTokenRepository(context),
            BuildDispatchService(context),
            NullLogger<BookingNotificationTriggerHandler>.Instance);

    private static SupportTicketNotificationTriggerHandler BuildTicketHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new CustomerRepository(context), new SupportTicketRepository(context), new DeviceTokenRepository(context),
            BuildDispatchService(context), NullLogger<SupportTicketNotificationTriggerHandler>.Instance);

    private static NotificationDispatchService BuildDispatchService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), new SandboxNotificationProvider(NullLogger<SandboxNotificationProvider>.Instance),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(context),
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
                BuildWebhookService(paymentRepository, bookingRepository, orderContext, gateway));
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
            var otpService = new OtpService(context, otpProvider);
            await otpService.GenerateAsync(mobile, OtpPurpose.Registration);
        }

        string otpCode = System.Text.RegularExpressions.Regex.Match(otpProvider.LastSmsMessage!, @"\d{6}").Value;

        using (var context = _db.CreateContext())
        {
            var registrationService = new CustomerRegistrationService(
                new CustomerRepository(context), new CustomerAuthIdentityRepository(context), new OtpService(context, otpProvider),
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
