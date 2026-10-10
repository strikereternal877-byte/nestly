using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
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
        new(
            new ProviderRepository(context), new ProviderEarningLedgerRepository(context),
            new BookingRepository(context), new PaymentTransactionRepository(context), new ProviderPayoutRepository(context));

    private static PaymentWebhookService BuildWebhookService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway, CommissionService? commissionService = null) =>
        new(new PaymentTransactionRepository(context),
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            new BookingRepository(context),
            new ServiceRepository(context),
            gateway,
            commissionService ?? BuildCommissionService(),
            BuildEscrowService(context),
            context,
            new NoOpMetricsService(),
            NullLogger<PaymentWebhookService>.Instance);

    private static RefundService BuildRefundService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context), BuildEscrowService(context),
            new ProviderEarningLedgerRepository(context), TestServices.ProviderEarningLedgerService(context),
            gateway, context, NullLogger<RefundService>.Instance);

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

    private sealed record Fixture(Customer Customer, Guid BookingId, Guid CategoryId, decimal Total);

    private async Task<Fixture> SeedBookingAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice, decimal walletCreditToApply = 0m)
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

        if (walletCreditToApply > 0)
        {
            await new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(customer.Id, walletCreditToApply, WalletSourceType.PromotionalCredit, null, "Test wallet credit");
        }

        var request = new BookingSummaryRequest(
            service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, [],
            ApplyWalletCredit: walletCreditToApply > 0);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        return new Fixture(customer, created.Value.Id, category.Id, created.Value.Price.TotalPayable);
    }

    /// <summary>Drives a fresh booking through a successful payment, leaving it Confirmed with commission recorded and escrow held.</summary>
    private async Task<Fixture> SeedConfirmedPaidBookingAsync(
        SandboxPaymentGateway gateway, decimal servicePrice, CommissionService? commissionService = null, decimal walletCreditToApply = 0m)
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, servicePrice, walletCreditToApply);
        }

        // A fully wallet-covered booking has nothing left to charge, so task
        // 331 confirms it with no PaymentTransaction at all - there is no
        // gateway round trip to make here.
        if (fixture.Total <= 0)
        {
            return fixture;
        }

        using var context = _db.CreateContext();
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var webhookService = BuildWebhookService(context, gateway, commissionService);
        var paymentService = new PaymentService(paymentRepository,
            bookingRepository,
            gateway,
            (ISandboxPaymentSimulator)gateway,
            webhookService,
            new AlwaysEligibleProviderSearchStub(),
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            new RecurringBookingOccurrenceRepository(context),
            null!);

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
        var paymentService = new PaymentService(paymentRepository,
            bookingRepository,
            gateway,
            (ISandboxPaymentSimulator)gateway,
            webhookService,
            new AlwaysEligibleProviderSearchStub(),
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            new RecurringBookingOccurrenceRepository(context),
            null!);

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
                bookingRepository, new ProviderRepository(assignContext), new ServiceRepository(assignContext),
                new BookingProviderAssignmentRepository(assignContext), new ProviderScheduleConflictService(assignContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(assignContext), assignContext);
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

    /// <summary>
    /// Regression coverage for the escrow release race. EscrowService.
    /// ReleaseToProviderAsync's own held&lt;=0 check (a read) and the entry it
    /// inserts (a write) are not atomic, so two genuinely concurrent
    /// completions of the same booking can both read "held > 0" before
    /// either has written - by the time both reach the insert, the race is
    /// already decided and no in-memory check can catch it. Racing
    /// Task.WhenAll over Task.WhenAll's own scheduling was tried first and
    /// found unreliable (the fast in-memory SQLite path resolves each
    /// task's read-then-write before the other gets a chance to interleave,
    /// so it passed even with the fix reverted - a false negative, not
    /// evidence of safety). Racing two independently-read entries straight
    /// at IPlatformEscrowLedgerRepository.TryAddCompletionReleaseAsync
    /// instead removes that timing dependency entirely: it is exactly what
    /// two callers who both already passed their own held&lt;=0 check produce,
    /// and it is the database constraint alone - not scheduling luck - that
    /// must decide which one wins.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_completions_of_the_same_booking_release_escrow_exactly_once()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));

        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var context = _db.CreateContext();
            var entry = new PlatformEscrowLedger(
                Guid.NewGuid(), fixture.BookingId, EscrowEntryType.Release, 1000m, 1000m,
                EscrowSourceType.BookingCompleted, sourceReferenceId: null,
                "Booking completed - escrow released to provider net of commission.");
            return await new PlatformEscrowLedgerRepository(context).TryAddCompletionReleaseAsync(entry);
        });

        var results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(r => r, "exactly one of the two racing completions may insert a BookingCompleted release for the same booking - the database constraint, not an in-memory check, must be what decides");

        using var finalContext = _db.CreateContext();
        var releaseEntries = (await new PlatformEscrowLedgerRepository(finalContext).ListByBookingAsync(fixture.BookingId))
            .Where(e => e.EntryType == EscrowEntryType.Release)
            .ToList();
        releaseEntries.Should().ContainSingle("the racing completion must not double-release the platform escrow ledger for one booking");
    }

    // --- Late-cancellation fee retained as platform revenue, not stranded in escrow ---

    /// <summary>
    /// Regression coverage for the stranded-cancellation-fee gap: before
    /// this fix, RefundService.ReleaseForRefundAsync only ever released
    /// outcome.RefundAmount from escrow - the fee CancellationFeeCalculator
    /// withholds from it stayed "held" forever, since the booking is now
    /// terminal (Cancelled, never Completed) and nothing else was ever
    /// going to claim it. Not a lost-money bug (the customer was never
    /// refunded that share either), but a permanently misstated escrow
    /// liability with no ledger recognition of it as revenue.
    /// </summary>
    [Fact]
    public async Task Cancelling_late_releases_the_withheld_fee_from_escrow_as_retained_revenue()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m);

        // Mirrors SeedBookingAsync's own slot construction (futureDate =
        // UtcNow + 3 days, "Morning" window starting 09:00) - TestServices.
        // Clock() pins the business timezone to UTC, so the slot's UTC start
        // is exactly that local time with no offset.
        var slotStartUtc = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToDateTime(TimeOnly.MinValue).AddHours(9);
        // 2 hours out: inside the default 4-hour free-cancellation window,
        // so the default 20% late fee applies.
        var timeProvider = new FakeTimeProvider(slotStartUtc.AddHours(-2));

        using (var context = _db.CreateContext())
        {
            var result = await TestServices.CancellationService(context, gateway, timeProvider)
                .CancelAsync(fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));

            result.IsSuccess.Should().BeTrue();
            result.Value.WithinFreeCancellationWindow.Should().BeFalse();
            result.Value.CancellationFeeAmount.Should().Be(200m, "20% of the 1000 the booking was funded by");
            result.Value.RefundAmount.Should().Be(800m);
        }

        using var readContext = _db.CreateContext();
        var entries = await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId);
        entries.Should().HaveCount(3, "one Hold at payment, one RefundIssued release for the 800 refund, one CancellationFeeRetained release for the 200 fee");

        var feeRelease = entries.Single(e => e.SourceType == EscrowSourceType.CancellationFeeRetained);
        feeRelease.EntryType.Should().Be(EscrowEntryType.Release);
        feeRelease.Amount.Should().Be(200m);

        (await BuildEscrowService(readContext).GetHeldBalanceAsync(fixture.BookingId)).Should().Be(0m, "the fee and the refund together account for the entire 1000 held - nothing should remain stranded");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _now;
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

    // --- Wallet-funded share reaching the provider (the forward-path counterpart to the refund clawback below) ---

    private static WalletCreditEscrowHoldOnConfirmationHandler BuildWalletCreditEscrowHoldHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context, CommissionService? commissionService = null) =>
        new(new BookingRepository(context), new ServiceRepository(context), commissionService ?? BuildCommissionService(), BuildEscrowService(context));

    /// <summary>
    /// Regression coverage for the wallet-funded provider-payout gap: before
    /// this fix, a fully wallet-covered booking had no PaymentTransaction at
    /// all (task 331), so EscrowReleaseOnCompletionHandler's own "nothing
    /// payable, designed outcome" branch swallowed it along with the
    /// genuinely-free AMC/100%-off-coupon cases - a provider who completed
    /// such a job was credited literally nothing for it.
    /// </summary>
    [Fact]
    public async Task Completing_a_fully_wallet_covered_booking_credits_the_provider_net_of_commission()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m), walletCreditToApply: 1000m);

        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(fixture.BookingId);
            booking!.Status.Should().Be(BookingStatus.Confirmed, "a fully wallet-covered booking is confirmed with nothing left to pay (task 331)");

            await BuildWalletCreditEscrowHoldHandler(context).Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.Initiated, BookingStatus.Confirmed)),
                CancellationToken.None);
        }

        using (var readAfterHoldContext = _db.CreateContext())
        {
            var entries = await new PlatformEscrowLedgerRepository(readAfterHoldContext).ListByBookingAsync(fixture.BookingId);
            entries.Should().ContainSingle();
            entries[0].SourceType.Should().Be(EscrowSourceType.WalletCreditConfirmed);
            entries[0].Amount.Should().Be(1000m);

            var booking = await new BookingRepository(readAfterHoldContext).GetByIdAsync(fixture.BookingId);
            booking!.WalletCreditCommissionAmountSnapshot.Should().Be(150m, "15% of the 1000 the wallet funded");
        }

        Guid providerId = await AssignProviderAndCompleteAsync(fixture.BookingId);

        using var readContext = _db.CreateContext();
        var summary = await BuildProviderEarningLedgerService(readContext).GetSummaryAsync(providerId);
        summary.Value.CurrentBalance.Should().Be(850m, "the provider must be paid net of commission for the wallet-funded job, not zero");
        summary.Value.Entries.Should().ContainSingle(e => e.SourceType == ProviderEarningSourceType.JobCompletion && e.SourceReferenceId == fixture.BookingId);
    }

    /// <summary>The partial-wallet half of the same fix: the provider must be paid for BOTH the gateway slice and the wallet slice, net of their combined commission.</summary>
    [Fact]
    public async Task Completing_a_part_wallet_booking_credits_the_provider_for_both_the_gateway_and_wallet_shares()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m), walletCreditToApply: 300m);

        using (var context = _db.CreateContext())
        {
            await BuildWalletCreditEscrowHoldHandler(context).Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(fixture.BookingId, BookingStatus.PaymentPending, BookingStatus.Confirmed)),
                CancellationToken.None);
        }

        using (var readAfterHoldContext = _db.CreateContext())
        {
            var entries = await new PlatformEscrowLedgerRepository(readAfterHoldContext).ListByBookingAsync(fixture.BookingId);
            entries.Should().HaveCount(2, "one Hold for the 700 gateway payment, one Hold for the 300 wallet credit");
            entries.Should().Contain(e => e.SourceType == EscrowSourceType.PaymentConfirmed && e.Amount == 700m);
            entries.Should().Contain(e => e.SourceType == EscrowSourceType.WalletCreditConfirmed && e.Amount == 300m);

            var held = await BuildEscrowService(readAfterHoldContext).GetHeldBalanceAsync(fixture.BookingId);
            held.Should().Be(1000m);
        }

        Guid providerId = await AssignProviderAndCompleteAsync(fixture.BookingId);

        using var readContext = _db.CreateContext();
        var summary = await BuildProviderEarningLedgerService(readContext).GetSummaryAsync(providerId);
        summary.Value.CurrentBalance.Should().Be(850m, "1000 total funded minus 15% (150) combined commission on the whole amount, gateway share and wallet share alike");

        var release = (await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId))
            .Single(e => e.EntryType == EscrowEntryType.Release);
        release.Amount.Should().Be(1000m, "the release covers everything held, regardless of which source funded it");
        release.CommissionAmount.Should().Be(150m);
    }

    // --- Refund clawback: a Completed booking's payout already left escrow ---

    /// <summary>
    /// Regression coverage for the escrow/provider-payout leak: before this
    /// fix, refunding a Completed booking found nothing left held in escrow
    /// (already released to the provider on completion - see
    /// <see cref="EscrowReleaseOnCompletionHandler"/>) and silently stopped
    /// there. The customer got their money back while the provider kept the
    /// full payout for a job the platform is now treating as unpaid for,
    /// with zero ledger trace of the loss.
    /// </summary>
    [Fact]
    public async Task Fully_refunding_a_completed_booking_claws_back_the_providers_full_job_completion_earning()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));
        Guid providerId = await AssignProviderAndCompleteAsync(fixture.BookingId);

        using (var readAfterCompletionContext = _db.CreateContext())
        {
            var summary = await BuildProviderEarningLedgerService(readAfterCompletionContext).GetSummaryAsync(providerId);
            summary.Value.CurrentBalance.Should().Be(850m, "sanity check: the provider was credited net of the 15% commission before any refund");
        }

        using (var refundContext = _db.CreateContext())
        {
            var result = await BuildRefundService(refundContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "Dispute upheld - service not delivered");
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var summaryAfterRefund = await BuildProviderEarningLedgerService(readContext).GetSummaryAsync(providerId);
        summaryAfterRefund.Value.CurrentBalance.Should().Be(0m, "the full net credit must be clawed back when the whole booking is refunded");

        var clawback = summaryAfterRefund.Value.Entries.Single(e => e.SourceType == ProviderEarningSourceType.JobCompletionClawback);
        clawback.EntryType.Should().Be(ProviderEarningEntryType.Debit);
        clawback.Amount.Should().Be(850m);
        clawback.SourceReferenceId.Should().Be(fixture.BookingId);

        // Escrow itself has nothing left to release again - already released
        // to the provider on completion, unaffected by the new clawback path.
        var escrowEntries = await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId);
        escrowEntries.Should().HaveCount(2, "one Hold at payment, one Release at completion - the refund adds no third escrow entry");
    }

    /// <summary>The proportional half of the same fix: a partial refund claws back only its matching share, not the whole credit.</summary>
    [Fact]
    public async Task Partially_refunding_a_completed_booking_claws_back_a_proportional_share_of_the_providers_earning()
    {
        var gateway = BuildGateway();
        var fixture = await SeedConfirmedPaidBookingAsync(gateway, servicePrice: 1000m, BuildCommissionService(defaultRate: 15m));
        Guid providerId = await AssignProviderAndCompleteAsync(fixture.BookingId);

        using (var refundContext = _db.CreateContext())
        {
            // 300 of the original 1000 (30%) refunded as partial goodwill.
            var result = await BuildRefundService(refundContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, 300m, "Partial goodwill refund");
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var summary = await BuildProviderEarningLedgerService(readContext).GetSummaryAsync(providerId);
        summary.Value.CurrentBalance.Should().Be(595m, "850 credited minus 30% of it (255) clawed back to match the 30% refund");

        var clawback = summary.Value.Entries.Single(e => e.SourceType == ProviderEarningSourceType.JobCompletionClawback);
        clawback.Amount.Should().Be(255m);
    }

    /// <summary>Shared setup for the refund-clawback tests: assigns an active provider, completes the job, and returns the provider's id.</summary>
    private async Task<Guid> AssignProviderAndCompleteAsync(Guid bookingId)
    {
        Guid providerId;
        using (var assignContext = _db.CreateContext())
        {
            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+91" + Guid.NewGuid().ToString("N")[..9]);
            provider.ChangeStatus(ProviderStatus.Active);
            providerId = provider.Id;
            assignContext.Add(provider);
            await assignContext.SaveChangesAsync();

            var bookingRepository = new BookingRepository(assignContext);
            var booking = await bookingRepository.GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
            await bookingRepository.UpdateAsync(booking);

            var assignmentService = new BookingProviderAssignmentService(
                bookingRepository, new ProviderRepository(assignContext), new ServiceRepository(assignContext),
                new BookingProviderAssignmentRepository(assignContext), new ProviderScheduleConflictService(assignContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(assignContext), assignContext);
            var assignResult = await assignmentService.AssignAsync(bookingId, Guid.NewGuid(), new AssignProviderRequest(providerId, ResponseDeadline: null));
            assignResult.IsSuccess.Should().BeTrue();
        }

        using (var lifecycleContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(lifecycleContext);
            var booking = await bookingRepository.GetByIdAsync(bookingId);
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
                    new BookingStatusChangedEvent(bookingId, BookingStatus.InProgress, BookingStatus.Completed)),
                CancellationToken.None);
        }

        return providerId;
    }
}
