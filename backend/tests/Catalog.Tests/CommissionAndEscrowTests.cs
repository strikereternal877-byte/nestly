using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Serviceability;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 157 (commission setup/calculation, recorded at settlement)
/// and task 158 (escrow hold on confirmation, release on completion or
/// refund) end to end against a real (SQLite) database, on top of
/// CommissionCalculatorTests' pure-math coverage.
/// </summary>
public sealed class CommissionAndEscrowTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public CommissionAndEscrowTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static CommissionService BuildCommissionService(decimal defaultRate = 15m, Dictionary<string, decimal>? overrides = null) =>
        new(Options.Create(new CommissionOptions { DefaultRatePercentage = defaultRate, CategoryRateOverrides = overrides ?? new() }));

    private static EscrowService BuildEscrowService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new PlatformEscrowLedgerRepository(context));

    private static ProviderEarningLedgerService BuildProviderEarningLedgerService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new ProviderRepository(context), new ProviderEarningLedgerRepository(context));

    private static PaymentWebhookService BuildWebhookService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway, CommissionService? commissionService = null) =>
        new(
            new PaymentTransactionRepository(context), new BookingRepository(context), new ServiceRepository(context), gateway,
            commissionService ?? BuildCommissionService(), BuildEscrowService(context),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private static RefundService BuildRefundService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context)), BuildEscrowService(context), gateway, context);

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

    private sealed record Fixture(Customer Customer, Guid BookingId, Guid CategoryId, decimal Total);

    private async Task<Fixture> SeedBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
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

        return new Fixture(customer, created.Value.Id, category.Id, created.Value.Price.TotalPayable);
    }

    /// <summary>Drives a fresh booking through a successful payment, leaving it Confirmed with commission recorded and escrow held.</summary>
    private async Task<Fixture> SeedConfirmedPaidBookingAsync(IPaymentGateway gateway, decimal servicePrice, CommissionService? commissionService = null)
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, servicePrice);
        }

        using var context = _db.CreateContext();
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var webhookService = BuildWebhookService(context, gateway, commissionService);
        var paymentService = new PaymentService(paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway, webhookService);

        var order = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
        string payload = PaymentWebhookPayload.Build(order.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
        string signature = gateway.SignPayload(payload);
        var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(order.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
        callback.IsSuccess.Should().BeTrue();

        return fixture;
    }

    // --- Task 157: commission ------------------------------------------

    [Fact]
    public async Task Confirming_payment_records_commission_on_the_transaction_at_the_configured_default_rate()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));

        using var readContext = _db.CreateContext();
        var transaction = await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(fixture.BookingId);

        transaction!.Status.Should().Be(PaymentTransactionStatus.Success);
        transaction.CommissionRatePercentage.Should().Be(15m);
        transaction.CommissionAmount.Should().Be(150m); // 15% of 1000
    }

    [Fact]
    public async Task Confirming_payment_uses_a_per_category_override_rate_when_one_is_configured()
    {
        var gateway = BuildGateway();

        // Seed first so the category id is known before wiring the override.
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, 1000m);
        }

        var commissionService = BuildCommissionService(defaultRate: 15m, overrides: new()
        {
            [fixture.CategoryId.ToString()] = 20m
        });

        using var context = _db.CreateContext();
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var webhookService = BuildWebhookService(context, gateway, commissionService);
        var paymentService = new PaymentService(paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway, webhookService);

        var order = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
        string payload = PaymentWebhookPayload.Build(order.Value.GatewayOrderId, "ref", PaymentWebhookPayload.SuccessStatus);
        await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(order.Value.GatewayOrderId, "ref", PaymentWebhookPayload.SuccessStatus, gateway.SignPayload(payload)));

        using var readContext = _db.CreateContext();
        var transaction = await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(fixture.BookingId);

        transaction!.CommissionRatePercentage.Should().Be(20m, "the category override must win over the 15% default");
        transaction.CommissionAmount.Should().Be(200m); // 20% of 1000
    }

    // --- Task 158: escrow hold on confirmation ---------------------------

    [Fact]
    public async Task Confirming_payment_holds_the_full_amount_in_escrow()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m);

        using var readContext = _db.CreateContext();
        var escrowRepository = new PlatformEscrowLedgerRepository(readContext);
        var entries = await escrowRepository.ListByBookingAsync(fixture.BookingId);

        entries.Should().ContainSingle();
        entries[0].EntryType.Should().Be(EscrowEntryType.Hold);
        entries[0].Amount.Should().Be(fixture.Total);
        entries[0].SourceType.Should().Be(EscrowSourceType.PaymentConfirmed);

        var held = await BuildEscrowService(readContext).GetHeldBalanceAsync(fixture.BookingId);
        held.Should().Be(fixture.Total);
    }

    // --- Task 158: escrow release on booking completion -------------------

    [Fact]
    public async Task Completing_a_booking_releases_its_escrow_to_the_provider_net_of_commission()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));

        using (var lifecycleContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(lifecycleContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
            booking.TransitionTo(BookingStatus.Assigned);
            booking.TransitionTo(BookingStatus.InProgress);
            booking.TransitionTo(BookingStatus.Completed);
            await bookingRepository.UpdateAsync(booking);
        }

        using (var handlerContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(handlerContext);
            var transaction = await paymentRepository.GetByBookingIdAsync(fixture.BookingId);
            var handler = new EscrowReleaseOnCompletionHandler(
                paymentRepository, new BookingRepository(handlerContext), BuildEscrowService(handlerContext),
                BuildProviderEarningLedgerService(handlerContext), NullLogger<EscrowReleaseOnCompletionHandler>.Instance);

            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.InProgress, BookingStatus.Completed)),
                CancellationToken.None);

            transaction!.CommissionAmount.Should().Be(150m);
        }

        using var readContext = _db.CreateContext();
        var entries = await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId);
        entries.Should().HaveCount(2);
        var release = entries.Single(e => e.EntryType == EscrowEntryType.Release);
        release.SourceType.Should().Be(EscrowSourceType.BookingCompleted);
        release.Amount.Should().Be(1000m);
        release.CommissionAmount.Should().Be(150m);
        release.ProviderId.Should().BeNull("this booking was never assigned a provider (Booking.AssignedProviderId is null), so there is nobody to release to");

        var held = await BuildEscrowService(readContext).GetHeldBalanceAsync(fixture.BookingId);
        held.Should().Be(0m, "the full hold has been released");
    }

    /// <summary>
    /// Task 148/149a: once a booking has an assigned provider
    /// (<see cref="Booking.AssignedProviderId"/>, task 147), completing it
    /// must both release escrow to that specific provider (no longer a null
    /// placeholder) and credit their earning ledger with the net amount -
    /// the automatic-crediting hook <c>IProviderEarningLedgerService</c>'s doc
    /// comment anticipated.
    /// </summary>
    [Fact]
    public async Task Completing_an_assigned_bookings_job_releases_escrow_to_and_credits_the_assigned_provider()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));

        Guid providerId;
        using (var assignContext = _db.CreateContext())
        {
            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+91" + Guid.NewGuid().ToString("N")[..9]);
            provider.ChangeStatus(ProviderStatus.Active); // AssignAsync (task 147) only allows assigning an Active provider.
            providerId = provider.Id;
            assignContext.Add(provider);
            await assignContext.SaveChangesAsync();

            var bookingRepository = new BookingRepository(assignContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
            await bookingRepository.UpdateAsync(booking);

            var assignmentService = new BookingProviderAssignmentService(
                bookingRepository, new ProviderRepository(assignContext), new BookingProviderAssignmentRepository(assignContext));
            var assignResult = await assignmentService.AssignAsync(fixture.BookingId, Guid.NewGuid(), new AssignProviderRequest(providerId, ResponseDeadline: null));
            assignResult.IsSuccess.Should().BeTrue();
        }

        using (var lifecycleContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(lifecycleContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.InProgress);
            booking.TransitionTo(BookingStatus.Completed);
            await bookingRepository.UpdateAsync(booking);
        }

        using (var handlerContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(handlerContext);
            var handler = new EscrowReleaseOnCompletionHandler(
                paymentRepository, new BookingRepository(handlerContext), BuildEscrowService(handlerContext),
                BuildProviderEarningLedgerService(handlerContext), NullLogger<EscrowReleaseOnCompletionHandler>.Instance);

            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.InProgress, BookingStatus.Completed)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var release = (await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId))
            .Single(e => e.EntryType == EscrowEntryType.Release);
        release.ProviderId.Should().Be(providerId);
        release.CommissionAmount.Should().Be(150m);

        var summary = await BuildProviderEarningLedgerService(readContext).GetSummaryAsync(providerId);
        summary.IsSuccess.Should().BeTrue();
        summary.Value.CurrentBalance.Should().Be(850m, "the net amount released to the provider (1000 - 150 commission)");
        summary.Value.Entries.Should().ContainSingle(e => e.SourceType == ProviderEarningSourceType.JobCompletion && e.SourceReferenceId == fixture.BookingId);
    }

    [Fact]
    public async Task Releasing_escrow_twice_for_the_same_booking_is_a_no_op_the_second_time()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));

        using var context = _db.CreateContext();
        var escrowService = BuildEscrowService(context);
        var paymentRepository = new PaymentTransactionRepository(context);
        var transaction = await paymentRepository.GetByBookingIdAsync(fixture.BookingId);

        var first = await escrowService.ReleaseToProviderAsync(fixture.BookingId, transaction!.Id, providerId: null, transaction.CommissionAmount!.Value);
        first.Should().NotBeNull();
        first!.NetAmountToProvider.Should().Be(850m);

        var second = await escrowService.ReleaseToProviderAsync(fixture.BookingId, transaction.Id, providerId: null, transaction.CommissionAmount!.Value);
        second.Should().BeNull("nothing remains held after the first release");
    }

    // --- Task 158: refund path releases escrow without paying a provider --

    [Fact]
    public async Task A_full_refund_releases_the_bookings_escrow_hold_without_recording_a_provider_payout()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1001m); // avoid the .13 paisa sandbox-decline convention

        using (var cancelContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(cancelContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
            await bookingRepository.UpdateAsync(booking);
        }

        using (var refundContext = _db.CreateContext())
        {
            var result = await BuildRefundService(refundContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var entries = await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId);
        entries.Should().HaveCount(2);
        var release = entries.Single(e => e.EntryType == EscrowEntryType.Release);
        release.SourceType.Should().Be(EscrowSourceType.RefundIssued);
        release.Amount.Should().Be(fixture.Total);
        release.CommissionAmount.Should().BeNull("a refund releases escrow back out - it is never paid to a provider");
        release.ProviderId.Should().BeNull();

        var held = await BuildEscrowService(readContext).GetHeldBalanceAsync(fixture.BookingId);
        held.Should().Be(0m);
    }
}
