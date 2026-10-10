using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Reschedules;
using Nestly.Application.Serviceability;
using Nestly.Application.Settings;
using Nestly.Application.Slots;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 82a-d (eligibility window, count limits, slot revalidation, fee impact) and 83 (reschedule API/service).</summary>
public sealed class RescheduleServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RescheduleServiceTests(TestDatabase db) => _db = db;

    /// <summary>
    /// Reschedule policy with late-fee collection switched on. Production's default is off, so the tests that are about
    /// collection ask for it explicitly - and this builder does by default - while the switch-off tests pass a plain policy.
    /// </summary>
    private static ReschedulePolicyOptions CollectingPolicy() => new() { CollectLateFeeFromWallet = true };

    /// <summary>Gives the customer wallet balance to pay a late-reschedule fee out of.</summary>
    private async Task FundWalletAsync(Guid customerId, decimal amount)
    {
        using var context = _db.CreateContext();
        await TestServices.Wallet(context).CreditAsync(customerId, amount, WalletSourceType.ManualAdjustment, null, "Test funding");
    }

    private async Task<decimal> WalletBalanceAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await TestServices.Wallet(context).GetBalanceAsync(customerId)).Value.Balance;
    }

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
        new(paymentRepository,
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            bookingRepository,
            new ServiceRepository(context),
            gateway,
            new CommissionService(Options.Create(new CommissionOptions())),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context,
            new NoOpMetricsService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentWebhookService>.Instance);

    private static ISlotAvailabilityService BuildSlotAvailabilityService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new SlotAvailabilityService(
            new ServiceabilityRepository(context),
            new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
            new SlotWindowRepository(context),
            new SlotBlackoutRepository(context),
            new SlotBookingPolicyRepository(context),
            new SlotCapacityRepository(context),
            TestServices.Clock());

    private static RescheduleService BuildRescheduleService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider timeProvider, ReschedulePolicyOptions? policy = null,
        CancellationPolicyOptions? cancellationPolicy = null, IBookingPolicyProvider? policies = null) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            BuildSlotAvailabilityService(context),
            new BookingRescheduleRepository(context),
            new BookingProviderAssignmentRepository(context),
            new ProviderScheduleConflictService(context, TestServices.Occupancy()),
            context,
            TestServices.Clock(timeProvider),
            timeProvider,
            policies ?? TestServices.Policies(cancellationPolicy, policy ?? CollectingPolicy()), TestServices.ProviderNotificationPublisher(context), TestServices.PlanReservations(context), TestServices.Wallet(context), TestServices.Escrow(context), Microsoft.Extensions.Logging.Abstractions.NullLogger<RescheduleService>.Instance);

    private static CancellationService BuildCancellationService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway, TimeProvider timeProvider, CancellationPolicyOptions? policy = null) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            TestServices.RefundService(context, gateway),
            new BookingCancellationRepository(context),
            new BookingProviderAssignmentRepository(context),
            BuildSlotAvailabilityService(context),
            new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
            new CustomerSubscriptionRepository(context),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            TestServices.Clock(timeProvider),
            timeProvider,
            TestServices.Policies(policy), TestServices.ProviderNotificationPublisher(context), new BookingRescheduleRepository(context));

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total, Guid LocalityId, Guid NewSlotWindowId, DateOnly NewSlotDate, DateTime SlotStartUtc);

    /// <summary>
    /// A freshly created, fully paid booking (Confirmed) with its slot far in
    /// the future, plus a second slot window available on a later date to
    /// reschedule into. <paramref name="walletCreditToApply"/> funds part (or
    /// all) of it from the customer's wallet instead of the gateway; when it
    /// covers the whole price there is no gateway round trip at all, because
    /// task 331 confirms such a booking without a payment.
    /// </summary>
    private async Task<Fixture> SeedPaidBookingAsync(
        SandboxPaymentGateway gateway, decimal servicePrice = 1000m, decimal walletCreditToApply = 0m)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        var newDate = futureDate.AddDays(2);
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        Customer customer;
        Guid bookingId, localityId, newWindowId;
        decimal total;
        var slotStart = TimeSpan.FromHours(9);

        using (var context = _db.CreateContext())
        {
            customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
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
            var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", slotStart, TimeSpan.FromHours(13));
            var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);
            var newWindow = new SlotWindow(Guid.NewGuid(), city.Id, "Afternoon", TimeSpan.FromHours(14), TimeSpan.FromHours(18));
            var newRule = new SlotWindowRule(Guid.NewGuid(), newWindow.Id, newDate.DayOfWeek);

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
            context.SlotWindows.Add(newWindow);
            context.SlotWindowRules.Add(newRule);
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
            bookingId = created.Value.Id;
            total = created.Value.Price.TotalPayable;
            localityId = locality.Id;
            newWindowId = newWindow.Id;
        }

        var slotStartUtcValue = futureDate.ToDateTime(TimeOnly.MinValue).Add(slotStart);

        // A fully wallet-covered booking has nothing left to charge, so task
        // 331 confirms it with no PaymentTransaction at all - there is no
        // gateway round trip to make here.
        if (total <= 0)
        {
            return new Fixture(customer, bookingId, total, localityId, newWindowId, newDate, slotStartUtcValue);
        }

        string gatewayOrderId;
        using (var orderContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(orderContext);
            var bookingRepository = new BookingRepository(orderContext);
            var paymentService = new PaymentService(paymentRepository,
            bookingRepository,
            gateway,
            (ISandboxPaymentSimulator)gateway,
            BuildWebhookService(paymentRepository, bookingRepository, orderContext, gateway),
            new AlwaysEligibleProviderSearchStub(),
            new PaymentGroupRepository(orderContext),
            new RecurringBookingPlanRepository(orderContext),
            new RecurringBookingOccurrenceRepository(orderContext),
            null!);
            var order = await paymentService.CreateOrderAsync(customer.Id, new CreatePaymentOrderRequest(bookingId, null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

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

        return new Fixture(customer, bookingId, total, localityId, newWindowId, newDate, slotStartUtcValue);
    }

    [Fact]
    public async Task GetEligibilityAsync_is_eligible_well_before_the_slot()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1001m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeTrue();
        result.Value.ReschedulesUsed.Should().Be(0);
    }

    [Fact]
    public async Task GetEligibilityAsync_blocks_reschedule_once_the_window_has_expired()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1002m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-1)); // policy default MinHoursBeforeSlot = 2

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse();
        result.Value.IneligibilityReason.Should().Contain("expired");
    }

    [Fact]
    public async Task GetEligibilityAsync_names_the_status_the_way_the_customer_sees_it()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1002m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using (var setupContext = _db.CreateContext())
        {
            var repository = new BookingRepository(setupContext);
            var booking = await repository.GetByIdAsync(fixture.BookingId);
            foreach (var step in new[] { BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.InProgress })
            {
                booking!.TransitionTo(step, "test");
            }

            await repository.UpdateAsync(booking!);
        }

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        result.Value.IsEligible.Should().BeFalse();
        result.Value.IneligibilityReason.Should().Contain("Service in Progress").And.NotContain("InProgress");
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_updates_the_booking_slot_and_records_history()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1003m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId, new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));

        result.IsSuccess.Should().BeTrue();
        result.Value.NewSlot.SlotWindowId.Should().Be(fixture.NewSlotWindowId);
        result.Value.NewSlot.Date.Should().Be(fixture.NewSlotDate);
        result.Value.IsLate.Should().BeFalse();
        result.Value.FeeAmount.Should().Be(0m);
        result.Value.ReschedulesUsed.Should().Be(1);
        result.Value.BookingStatus.Should().Be(BookingStatus.AwaitingFulfilment);

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.SlotWindowId.Should().Be(fixture.NewSlotWindowId);
        booking.SlotDate.Should().Be(fixture.NewSlotDate);
        booking.StatusHistory.Should().Contain(h => h.ToStatus == BookingStatus.Rescheduled);

        var history = await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId);
        history.Should().HaveCount(1);
        history[0].Reason.Should().Be("Need a different day");
    }

    /// <summary>
    /// Regression coverage for the reschedule fee-bypass exploit: a customer
    /// inside the late-cancellation-fee window reschedules to a slot far
    /// enough out to look free, then immediately cancels. Before this fix,
    /// CancellationService.ComputeOutcomeAsync judged "time until slot"
    /// against the booking's CURRENT slot only - which Reschedule had just
    /// overwritten - so the fee genuinely owed on the slot given up vanished
    /// and the cancellation came back fully refunded.
    ///
    /// FALSIFIABILITY: without Booking.LockedCancellationFeeSnapshot flooring
    /// the fee, this cancellation computes against the new slot (2 days away,
    /// far outside the 4-hour free-cancellation window), returning
    /// WithinFreeWindow=true, fee=0, refund=1000 - not the 200/800 asserted
    /// below.
    /// </summary>
    [Fact]
    public async Task Reschedule_then_immediate_cancel_still_charges_the_cancellation_fee_the_original_slot_owed()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, servicePrice: 1000m);

        // 3 hours before the original slot: inside the 4-hour cancellation
        // free window (a fee would apply right now) and inside the 6-hour
        // reschedule late-fee threshold, but past the 2-hour reschedule hard
        // block - so the reschedule itself is allowed to go through.
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 150m);

        using (var rescheduleContext = _db.CreateContext())
        {
            var rescheduleResult = await BuildRescheduleService(rescheduleContext, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));
            rescheduleResult.IsSuccess.Should().BeTrue();
        }

        using (var cancelContext = _db.CreateContext())
        {
            var cancelResult = await BuildCancellationService(cancelContext, gateway, timeProvider).CancelAsync(
                fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));

            cancelResult.IsSuccess.Should().BeTrue();
            cancelResult.Value.WithinFreeCancellationWindow.Should().BeFalse(
                "the reschedule moved the slot far away, but the fee already owed on the slot given up must survive the move");
            cancelResult.Value.CancellationFeeBeforeCredit.Should().Be(200m, "20% of the 1000 the booking was funded by - the fee the original, near slot already owed");
            cancelResult.Value.RescheduleFeeCredited.Should().Be(100m, "the 10% late-reschedule fee already paid out of the wallet counts against it rather than being charged on top");
            cancelResult.Value.CancellationFeeAmount.Should().Be(100m, "what is still retained: the 200 owed, less the 100 already paid");
            cancelResult.Value.RefundAmount.Should().Be(900m);
        }
    }

    /// <summary>
    /// The other side of the same fix: a reschedule made from well outside
    /// any fee window locks in nothing, so a legitimate customer rescheduling
    /// far in advance is never penalized for a fee that was never actually
    /// owed in the first place.
    /// </summary>
    [Fact]
    public async Task Reschedule_made_well_before_the_slot_locks_in_no_cancellation_fee_floor()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, servicePrice: 1000m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using (var rescheduleContext = _db.CreateContext())
        {
            var rescheduleResult = await BuildRescheduleService(rescheduleContext, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));
            rescheduleResult.IsSuccess.Should().BeTrue();
        }

        using (var cancelContext = _db.CreateContext())
        {
            var cancelResult = await BuildCancellationService(cancelContext, gateway, timeProvider).CancelAsync(
                fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));

            cancelResult.IsSuccess.Should().BeTrue();
            cancelResult.Value.WithinFreeCancellationWindow.Should().BeTrue("no fee was ever owed on the slot given up, so none should be locked in");
            cancelResult.Value.CancellationFeeAmount.Should().Be(0m);
            cancelResult.Value.RefundAmount.Should().Be(1000m);
        }
    }

    /// <summary>
    /// Task 364. The late-reschedule fee is a percentage of what the booking
    /// is funded by, and a part-wallet booking is funded from two sources: the
    /// gateway <c>PaymentTransaction</c> and the wallet balance it consumed at
    /// checkout. Reading the payment alone (as ResolvePayableAmountAsync did
    /// before) charged the percentage against the gateway half only, so the
    /// recorded fee was understated by exactly the wallet-funded share - the
    /// same defect class task 356 fixed in CancellationService/RefundService,
    /// and the reason both now compute their base through
    /// <see cref="RefundAllocationCalculator"/>.
    ///
    /// FALSIFIABILITY: computing the fee off the gateway payment alone gives
    /// 70 (10% of the 700 charged), not 100 (10% of the 1000 the booking
    /// actually cost), and fails this test.
    /// </summary>
    [Fact]
    public async Task ConfirmRescheduleAsync_charges_the_late_fee_on_the_wallet_funded_share_too()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, servicePrice: 1000m, walletCreditToApply: 300m);
        fixture.Total.Should().Be(700m, "the wallet covered 300 of the 1000 at checkout");
        await FundWalletAsync(fixture.Customer.Id, 150m);

        // 3 hours out: inside the 6-hour late-fee threshold, still outside the
        // 2-hour hard block, so the reschedule succeeds AND carries a fee.
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId,
            new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Something came up"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsLate.Should().BeTrue();
        result.Value.FeeAmount.Should().Be(100m,
            "the 10% late fee applies to the whole 1000 the booking was funded by - 700 through the gateway plus the 300 the wallet covered");

        using var readContext = _db.CreateContext();
        var history = await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId);
        history.Should().HaveCount(1);
        history[0].FeeAmount.Should().Be(100m, "the history row records the same fee the response reported");
        history[0].IsLate.Should().BeTrue();
    }

    /// <summary>
    /// Task 364, the end of the same range: a booking whose wallet balance
    /// covered the entire price has no <c>PaymentTransaction</c> at all (task
    /// 331 confirms it without one), so reading the payment alone reported a
    /// zero base and therefore no fee whatsoever on a booking the customer had
    /// genuinely paid 1000 for.
    ///
    /// FALSIFIABILITY: the old gateway-only base gives 0 here, not 100.
    /// </summary>
    [Fact]
    public async Task ConfirmRescheduleAsync_charges_the_late_fee_on_a_fully_wallet_covered_booking()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, servicePrice: 1000m, walletCreditToApply: 1500m);
        fixture.Total.Should().Be(0m, "the wallet covered the entire price");

        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId,
            new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Something came up"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsLate.Should().BeTrue();
        result.Value.FeeAmount.Should().Be(100m,
            "the wallet balance the booking consumed is what it was funded by, and 10% of it is the late fee");
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_rejects_a_slot_window_that_does_not_exist_on_the_requested_date()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1004m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId, new RescheduleBookingRequest(fixture.LocalityId, Guid.NewGuid(), fixture.NewSlotDate, null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Reschedule.SlotNotAvailable");
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_stops_once_the_reschedule_count_limit_is_reached()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1005m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));
        var policy = new ReschedulePolicyOptions { MaxReschedulesPerBooking = 1 };

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRescheduleService(firstContext, timeProvider, policy).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId, new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "First reschedule"));
            first.IsSuccess.Should().BeTrue();
        }

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider, policy).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        result.Value.IsEligible.Should().BeFalse();
        result.Value.IneligibilityReason.Should().Contain("maximum");
    }

    // --- Task 290: rescheduling an Assigned booking must not silently keep
    // (or silently drop) the provider - it must check the new slot. ---

    private static BookingProviderAssignmentService BuildAssignmentService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), TestServices.ProviderNotificationPublisher(context), context);

    private static Provider SeedProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var provider = new Provider(
            Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual,
            "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        context.SaveChanges();
        return provider;
    }

    /// <summary>Walks the booking to AwaitingFulfilment and assigns <paramref name="providerId"/> via the real assignment service, exactly the way task 147's admin flow does.</summary>
    private static async Task AssignProviderAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid bookingId, Guid providerId)
    {
        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.AwaitingFulfilment, "test");
        await new BookingRepository(context).UpdateAsync(booking);

        var result = await BuildAssignmentService(context).AssignAsync(
            bookingId, Guid.NewGuid(), new AssignProviderRequest(providerId, ResponseDeadline: null));
        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>A second, minimal booking occupying the given provider's entire slot window on <paramref name="date"/> - the conflict test's collision.</summary>
    private static void SeedConflictingAssignment(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid providerId, DateOnly date, TimeSpan startTime, TimeSpan endTime)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Other Customer", CustomerStatus.Active);
        context.Add(customer);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot("Other Customer", customer.Mobile),
            null,
            new AddressSnapshot("Home", "1 Other St", null, null, "560002", "Bengaluru", "Karnataka", 12.95m, 77.6m, "Other", "9000000001"),
            new SlotSnapshot(Guid.NewGuid(), date, "Conflict window", startTime, endTime),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m));
        foreach (var step in new[] { BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned })
        {
            booking.TransitionTo(step, "test");
        }

        context.Add(booking);
        context.SaveChanges();

        var assignment = new BookingProviderAssignment(Guid.NewGuid(), booking.Id, providerId, BookingAssignedByType.System, null, null);
        assignment.Accept();
        context.Add(assignment);
        context.SaveChanges();
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_keeps_the_assigned_provider_when_the_new_slot_is_still_free_for_them()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2001m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));

            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);

        booking!.Status.Should().Be(BookingStatus.Assigned, "the provider was free at the new slot, so the reschedule kept them assigned rather than falling back to AwaitingFulfilment");
        booking.AssignedProviderId.Should().Be(providerId);

        var activeAssignment = await new BookingProviderAssignmentRepository(readContext).GetActiveByBookingAsync(fixture.BookingId);
        activeAssignment.Should().NotBeNull();
        activeAssignment!.ProviderId.Should().Be(providerId);
        activeAssignment.Status.Should().Be(BookingProviderAssignmentStatus.Assigned, "the original assignment row survives untouched when the reschedule keeps the same provider");
    }

    /// <summary>
    /// The same "keep the professional" path as the test above, but with the
    /// domain-event dispatch that the production API processes actually have
    /// wired up. Booking.Reschedule leaves the booking at AwaitingFulfilment,
    /// and saving that dispatches BookingStatusChangedEvent to
    /// ProviderAutoAssignmentHandler, which is an in-process handler and
    /// promotes the booking straight back to Assigned before RescheduleService
    /// gets to reconcile the assignment. The test above never saw that because
    /// a bare test context dispatches nothing, so the reconcile always found
    /// AwaitingFulfilment and its unconditional TransitionTo(Assigned) was
    /// legal. In the real stack it was not: BookingLifecycle has no
    /// Assigned -> Assigned self-edge, so it threw InvalidOperationException,
    /// which is not a DbUpdateException and so escaped the reconcile's catch
    /// as an HTTP 500 - with the slot move already persisted and the old slot
    /// never released. <see cref="PromoteToAssignedPublisher"/> stands in for
    /// the auto-assigner so this stays a fast SQLite test.
    /// </summary>
    [Fact]
    public async Task ConfirmRescheduleAsync_succeeds_when_auto_assignment_already_promoted_the_booking_back_to_Assigned()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2001m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        var autoAssigner = new PromoteToAssignedPublisher(fixture.BookingId);
        using (var context = _db.CreateContext(new DomainEventDispatchInterceptor(autoAssigner)))
        {
            autoAssigner.Context = context;

            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));

            autoAssigner.Promoted.Should().BeTrue("the test is only meaningful if the stand-in auto-assigner actually ran");
            result.IsSuccess.Should().BeTrue("the reschedule must not fail just because the auto-assigner already restored the Assigned status");
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);

        booking!.Status.Should().Be(BookingStatus.Assigned);
        booking.AssignedProviderId.Should().Be(providerId);
        booking.SlotDate.Should().Be(fixture.NewSlotDate, "the slot move must still have been applied");
    }

    /// <summary>
    /// Stands in for ProviderAutoAssignmentHandler: the moment the booking
    /// under test lands on AwaitingFulfilment, promote it straight back to
    /// Assigned, which is exactly what the real handler does in-process when
    /// an eligible provider exists. Guarded so the promotion's own
    /// BookingStatusChangedEvent does not re-enter.
    /// </summary>
    private sealed class PromoteToAssignedPublisher : MediatR.IPublisher
    {
        private readonly Guid _bookingId;

        public PromoteToAssignedPublisher(Guid bookingId) => _bookingId = bookingId;

        /// <summary>Set once the context exists - the interceptor has to be constructed before it.</summary>
        public NestlyDbContext? Context { get; set; }

        public bool Promoted { get; private set; }

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Publish((MediatR.INotification)notification, cancellationToken);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : MediatR.INotification
        {
            if (Promoted
                || notification is not DomainEventNotification<BookingStatusChangedEvent> statusChanged
                || statusChanged.DomainEvent.BookingId != _bookingId
                || statusChanged.DomainEvent.ToStatus != BookingStatus.AwaitingFulfilment)
            {
                return Task.CompletedTask;
            }

            // The real handler assigns a provider and transitions the booking;
            // AssignedProviderId is already set here (Booking.Reschedule does
            // not clear it), so only the status promotion is reproduced - and
            // on the tracked instance, because that is the very object
            // RescheduleService goes on to reconcile.
            var booking = Context!.ChangeTracker.Entries<Booking>()
                .Select(entry => entry.Entity)
                .SingleOrDefault(candidate => candidate.Id == _bookingId);
            if (booking is null || booking.Status != BookingStatus.AwaitingFulfilment)
            {
                return Task.CompletedTask;
            }

            Promoted = true;
            booking.TransitionTo(BookingStatus.Assigned, "Auto-assigned after reschedule (test stand-in).");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The core of task 290: before this fix, Booking.Reschedule moved the
    /// slot while leaving AssignedProviderId and the live assignment row
    /// untouched, with nothing checking whether the provider was even free
    /// at the new time - so a reschedule could silently slide a booking on
    /// top of the same provider's other job.
    /// </summary>
    [Fact]
    public async Task ConfirmRescheduleAsync_drops_the_assigned_provider_when_the_new_slot_now_conflicts_with_another_job()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2002m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);

            // Occupies the provider's entire day on the target reschedule
            // date (the new slot window is 14:00-18:00, see SeedPaidBookingAsync) -
            // any reschedule into that window now collides.
            SeedConflictingAssignment(context, providerId, fixture.NewSlotDate, TimeSpan.FromHours(0), TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        }

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));

            result.IsSuccess.Should().BeTrue("the slot move itself must still succeed even though the provider has to be dropped");
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);

        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment, "the provider now conflicts with another job, so the booking needs reassignment rather than silently double-booking them");
        booking.AssignedProviderId.Should().BeNull();

        var activeAssignment = await new BookingProviderAssignmentRepository(readContext).GetActiveByBookingAsync(fixture.BookingId);
        activeAssignment.Should().BeNull("the original assignment was withdrawn, not left live alongside a cleared display field");
    }

    // ---- What a customer is told, and what the professional is told -------------------------------

    private async Task<DateTime> CurrentSlotStartAsync(Guid bookingId)
    {
        using var context = _db.CreateContext();
        var booking = context.Bookings.Single(b => b.Id == bookingId);
        return await Task.FromResult(booking.SlotDate.ToDateTime(TimeOnly.FromTimeSpan(booking.SlotStartTimeSnapshot)));
    }

    private List<ProviderNotification> ProviderNotifications(Guid providerId)
    {
        using var context = _db.CreateContext();
        return context.ProviderNotifications.Where(n => n.ProviderId == providerId).ToList();
    }

    [Fact]
    public async Task GetEligibilityAsync_says_when_a_reschedule_stops_being_free_and_when_none_is_allowed()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1000m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));
        var slotStart = await CurrentSlotStartAsync(fixture.BookingId);

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        var eligibility = result.Value;
        eligibility.IsEligible.Should().BeTrue();
        eligibility.LateFeeThresholdHours.Should().Be(6m);
        eligibility.LateRescheduleFeePercentage.Should().Be(10m);
        eligibility.FreeRescheduleEndsAt.Should().Be(slotStart.AddHours(-6), "a reschedule stops being free this many hours before the slot");
        eligibility.LastRescheduleAt.Should().Be(slotStart.AddHours(-2), "after this none is allowed at all");
        eligibility.IsLateNow.Should().BeFalse();
        eligibility.LateFeeIfRescheduledNow.Should().Be(0m);
        eligibility.FeeBasisAmount.Should().Be(fixture.Total);
        eligibility.CancellationFeeLockedIn.Should().Be(0m, "nothing is locked in by moving a booking well ahead");
    }

    [Fact]
    public async Task GetEligibilityAsync_inside_the_late_window_says_what_a_reschedule_now_would_record_and_lock_in()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1000m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId);

        var eligibility = result.Value;
        eligibility.IsEligible.Should().BeTrue("3 hours is still more than the 2-hour cut-off");
        eligibility.IsLateNow.Should().BeTrue();
        eligibility.LateFeeIfRescheduledNow.Should().Be(RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 10m).FeeAmount);
        eligibility.LateFeeIfRescheduledNow.Should().BeGreaterThan(0m);
        eligibility.CancellationFeeLockedIn.Should().Be(CancellationFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 4m, 20m).FeeAmount,
            "moving a booking cannot erase the cancellation fee that already applies to the slot given up");
        eligibility.CancellationFeeLockedIn.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_late_reports_the_fee_what_it_was_charged_on_and_what_it_locked_in()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1000m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        var slotStart = await CurrentSlotStartAsync(fixture.BookingId);
        await FundWalletAsync(fixture.Customer.Id, 150m);

        using var context = _db.CreateContext();
        var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId,
            new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));

        result.IsSuccess.Should().BeTrue();
        var outcome = result.Value;
        outcome.IsLate.Should().BeTrue();
        outcome.FeeAmount.Should().Be(RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 10m).FeeAmount);
        outcome.FeeCollected.Should().Be(outcome.FeeAmount, "a late fee is really taken from the customer's wallet now");
        outcome.FeeBasisAmount.Should().Be(fixture.Total);
        outcome.LateFeeThresholdHours.Should().Be(6m);
        outcome.LateRescheduleFeePercentage.Should().Be(10m);
        outcome.FreeRescheduleEndedAt.Should().Be(slotStart.AddHours(-6), "explained against the slot that was given up");
        outcome.CancellationFeeLockedIn.Should().Be(CancellationFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 4m, 20m).FeeAmount);
        outcome.Professional.Should().Be(ProfessionalAfterReschedule.NoneAssigned, "nobody was on the job yet");
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_that_keeps_the_professional_says_so_and_tells_them_the_new_time()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2003m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        outcome.Professional.Should().Be(ProfessionalAfterReschedule.Kept);

        var sent = ProviderNotifications(providerId);
        var moved = sent.Should().ContainSingle(n => n.Type == ProviderNotificationType.JobRescheduled).Subject;
        moved.Body.Should().Contain("still assigned to you").And.Contain(fixture.NewSlotDate.ToString("d MMM"));
        moved.DeepLinkPath.Should().Be($"/jobs/{fixture.BookingId}");
        sent.Should().NotContain(n => n.Type == ProviderNotificationType.JobUnassigned);
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_that_cannot_keep_the_professional_says_so_and_tells_them_the_job_is_no_longer_theirs()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2004m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
            SeedConflictingAssignment(context, providerId, fixture.NewSlotDate, TimeSpan.FromHours(0), TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        }

        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        outcome.Professional.Should().Be(ProfessionalAfterReschedule.Released);

        var sent = ProviderNotifications(providerId);
        var removed = sent.Should().ContainSingle(n => n.Type == ProviderNotificationType.JobUnassigned).Subject;
        removed.Body.Should().Contain("no longer assigned to you");
        removed.DeepLinkPath.Should().Be("/jobs", "the job is not theirs any more, so there is no job page to open");
        sent.Should().NotContain(n => n.Type == ProviderNotificationType.JobRescheduled);
    }

    /// <summary>
    /// A recurring plan (someone else's) that holds <paramref name="providerId"/> in the fixture's new slot window on its
    /// new date: the plan is active, starts that day, and the professional served its last visit.
    /// </summary>
    private async Task SeedPlanHoldingProviderAtNewSlotAsync(Fixture fixture, Guid providerId)
    {
        using var context = _db.CreateContext();
        var fixtureBooking = context.Bookings.Single(b => b.Id == fixture.BookingId);
        var serviceId = context.Set<BookingItem>().Single(i => i.BookingId == fixture.BookingId).ServiceId;
        var locality = context.Localities.Single(l => l.Id == fixture.LocalityId);
        var cityId = context.Zones.Single(z => z.Id == locality.ZoneId).CityId;
        var addressId = fixtureBooking.SourceAddressId!.Value;

        var owner = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Plan Owner", CustomerStatus.Active);
        context.Add(owner);
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), owner.Id, serviceId, cityId, fixture.LocalityId, addressId, fixture.NewSlotWindowId, 1,
            RecurringBookingRecurrenceFrequency.Daily, null, null, fixture.NewSlotDate, endDate: null, occurrenceCount: null);
        context.Add(plan);

        var history = new Booking(
            Guid.NewGuid(), owner.Id, new CustomerSnapshot("Plan Owner", owner.Mobile), null,
            new AddressSnapshot("Home", "9 Plan St", null, null, "560003", "Bengaluru", "Karnataka", 12.9m, 77.6m, "Plan Owner", "9000000002"),
            new SlotSnapshot(fixture.NewSlotWindowId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "Afternoon", TimeSpan.FromHours(14), TimeSpan.FromHours(18)),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m),
            recurringBookingPlanId: plan.Id);
        history.AddItem(Guid.NewGuid(), serviceId, "Deep Clean", "deep-clean", 500m, 1);
        history.TransitionTo(BookingStatus.PaymentPending);
        history.TransitionTo(BookingStatus.Confirmed);
        history.AssignProvider(providerId);
        context.Add(history);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_customers_reschedule_into_a_time_another_plan_holds_the_professional_for_lets_them_go()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2005m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        await SeedPlanHoldingProviderAtNewSlotAsync(fixture, providerId);

        using var context2 = _db.CreateContext();
        var result = await BuildRescheduleService(context2, timeProvider).ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId,
            new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Professional.Should().Be(ProfessionalAfterReschedule.Released, "that time is spoken for by a recurring plan, and a customer's reschedule is not an admin override");
        ProviderNotifications(providerId).Should().ContainSingle(n => n.Type == ProviderNotificationType.JobUnassigned);
    }

    [Fact]
    public async Task An_admins_reschedule_into_a_time_another_plan_holds_the_professional_for_keeps_them()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2006m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        await SeedPlanHoldingProviderAtNewSlotAsync(fixture, providerId);

        using var context2 = _db.CreateContext();
        var result = await BuildRescheduleService(context2, timeProvider).AdminRescheduleAsync(
            fixture.BookingId, new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Ops decision"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Professional.Should().Be(ProfessionalAfterReschedule.Kept, "an admin can override a reservation, as with manual assignment");
        ProviderNotifications(providerId).Should().ContainSingle(n => n.Type == ProviderNotificationType.JobRescheduled);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The collection switch (ReschedulePolicy:CollectLateFeeFromWallet / Settings -> Reschedule). Off is the production default.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_collection_switched_off_a_late_reschedule_records_the_fee_but_takes_nothing_and_needs_no_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2040m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        // No wallet funding at all: with collection off an empty wallet must not matter.

        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider, new ReschedulePolicyOptions()).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));
            result.IsSuccess.Should().BeTrue("a customer with an empty wallet is not turned away while collection is off");
            outcome = result.Value;
        }

        outcome.IsLate.Should().BeTrue();
        outcome.FeeAmount.Should().BeGreaterThan(0m, "the lateness and the fee under the policy are still recorded");
        outcome.FeeCollected.Should().Be(0m);

        using var readContext = _db.CreateContext();
        var history = (await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId)).Single();
        history.FeeAmount.Should().Be(outcome.FeeAmount);
        history.FeeCollectedAmount.Should().Be(0m);
        (await TestServices.Wallet(readContext).GetLedgerAsync(fixture.Customer.Id)).Value
            .Should().NotContain(e => e.SourceType == WalletSourceType.RescheduleFee);
        (await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId))
            .Should().NotContain(e => e.SourceType == EscrowSourceType.RescheduleFeeCollected);
    }

    [Fact]
    public async Task With_collection_switched_off_the_eligibility_says_so_and_asks_nothing_of_the_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2041m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));

        using var context = _db.CreateContext();
        var off = (await BuildRescheduleService(context, timeProvider, new ReschedulePolicyOptions())
            .GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;

        off.IsLateNow.Should().BeTrue();
        off.LateFeeIsCollected.Should().BeFalse();
        off.LateFeeShortfall.Should().Be(0m, "no wallet is needed, so none can be short");
        off.WalletBalance.Should().Be(0m);

        var on = (await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;
        on.LateFeeIsCollected.Should().BeTrue();
        on.LateFeeShortfall.Should().BeGreaterThan(0m, "the same booking with collection on and an empty wallet is short");
    }

    [Fact]
    public async Task A_fee_collected_while_the_switch_was_on_is_still_credited_after_it_is_switched_off()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2042m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 500m);

        using (var context = _db.CreateContext())
        {
            (await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"))).IsSuccess.Should().BeTrue();
        }

        // Switching collection off changes what happens from now on; it does not un-pay what was paid.
        using var cancelContext = _db.CreateContext();
        var cancelled = await BuildCancellationService(cancelContext, gateway, timeProvider).CancelAsync(
            fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));

        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.RescheduleFeeCredited.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task A_late_reschedule_takes_the_fee_from_the_wallet_and_books_it_as_platform_revenue()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2020m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 500m);

        decimal heldBefore;
        using (var context = _db.CreateContext())
        {
            heldBefore = await TestServices.Escrow(context).GetHeldBalanceAsync(fixture.BookingId);
        }

        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        decimal fee = RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 10m).FeeAmount;
        fee.Should().BeGreaterThan(0m);
        outcome.FeeCollected.Should().Be(fee);
        (await WalletBalanceAsync(fixture.Customer.Id)).Should().Be(500m - fee);

        using var readContext = _db.CreateContext();
        var history = (await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId)).Single();
        history.FeeCollectedAmount.Should().Be(fee);

        var ledger = (await TestServices.Wallet(readContext).GetLedgerAsync(fixture.Customer.Id)).Value;
        var debit = ledger.Should().ContainSingle(e => e.SourceType == WalletSourceType.RescheduleFee).Subject;
        debit.EntryType.Should().Be(WalletEntryType.Debit);
        debit.Amount.Should().Be(fee);
        debit.SourceReferenceId.Should().Be(history.Id, "the wallet row points at the reschedule it paid for");

        var escrow = (await new PlatformEscrowLedgerRepository(readContext).ListByBookingAsync(fixture.BookingId))
            .Where(e => e.SourceType == EscrowSourceType.RescheduleFeeCollected)
            .ToList();
        escrow.Should().HaveCount(2, "a hold and a release of the same amount");
        escrow.Single(e => e.EntryType == EscrowEntryType.Hold).Amount.Should().Be(fee);
        escrow.Single(e => e.EntryType == EscrowEntryType.Release).Amount.Should().Be(fee);
        (await TestServices.Escrow(readContext).GetHeldBalanceAsync(fixture.BookingId)).Should().Be(
            heldBefore, "the fee is platform revenue, so what completion pays the provider out of is untouched");
    }

    [Fact]
    public async Task A_late_reschedule_is_refused_when_the_wallet_cannot_cover_the_fee_and_nothing_moves()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2021m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 40m);
        decimal fee = RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 10m).FeeAmount;

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Reschedule.LateFeeWalletShort");
            result.Error.Message.Should().Contain((fee - 40m).ToString("0.00"), "it says exactly how much to add");
        }

        using var readContext = _db.CreateContext();
        (await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId))!.SlotDate.Should().NotBe(fixture.NewSlotDate, "the booking did not move");
        (await new BookingRescheduleRepository(readContext).CountByBookingAsync(fixture.BookingId)).Should().Be(0);
        (await WalletBalanceAsync(fixture.Customer.Id)).Should().Be(40m);
        var booked = await new SlotCapacityRepository(readContext).GetBookedCountsAsync([fixture.NewSlotWindowId], fixture.NewSlotDate);
        booked.GetValueOrDefault(fixture.NewSlotWindowId, 0).Should().Be(0, "no seat was left held on the slot they could not afford to move to");
    }

    [Fact]
    public async Task A_reschedule_that_is_not_late_costs_nothing_and_needs_no_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2022m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Plans changed"));
            result.IsSuccess.Should().BeTrue();
            result.Value.IsLate.Should().BeFalse();
            result.Value.FeeCollected.Should().Be(0m);
        }

        using var readContext = _db.CreateContext();
        (await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId)).Single().FeeCollectedAmount.Should().Be(0m);
        (await TestServices.Wallet(readContext).GetLedgerAsync(fixture.Customer.Id)).Value
            .Should().NotContain(e => e.SourceType == WalletSourceType.RescheduleFee);
    }

    [Fact]
    public async Task An_admins_late_reschedule_never_charges_the_customer()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2023m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 500m);

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider).AdminRescheduleAsync(
                fixture.BookingId, new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Ops decision"));
            result.IsSuccess.Should().BeTrue();
            result.Value.FeeCollected.Should().Be(0m);
        }

        (await WalletBalanceAsync(fixture.Customer.Id)).Should().Be(500m, "an admin moving a booking is not the customer paying for lateness");
        using var readContext = _db.CreateContext();
        var history = (await new BookingRescheduleRepository(readContext).ListByBookingAsync(fixture.BookingId)).Single();
        history.IsLate.Should().BeTrue("the lateness is still recorded");
        history.FeeCollectedAmount.Should().Be(0m);
    }

    [Fact]
    public async Task GetEligibilityAsync_says_whether_the_wallet_covers_a_late_fee()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2024m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        decimal fee = RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 10m).FeeAmount;

        using (var context = _db.CreateContext())
        {
            var empty = (await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;
            empty.IsLateNow.Should().BeTrue();
            empty.WalletBalance.Should().Be(0m);
            empty.LateFeeShortfall.Should().Be(fee, "nothing in the wallet, so all of it is missing");
        }

        await FundWalletAsync(fixture.Customer.Id, 30m);
        using (var context = _db.CreateContext())
        {
            var partial = (await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;
            partial.WalletBalance.Should().Be(30m);
            partial.LateFeeShortfall.Should().Be(fee - 30m);
        }

        await FundWalletAsync(fixture.Customer.Id, 500m);
        using (var context = _db.CreateContext())
        {
            var covered = (await BuildRescheduleService(context, timeProvider).GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;
            covered.LateFeeShortfall.Should().Be(0m);
        }
    }

    [Fact]
    public async Task A_late_fee_taken_for_a_reschedule_that_then_fails_to_save_goes_back_to_the_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2025m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 500m);

        using (var context = _db.CreateContext())
        {
            var service = new RescheduleService(
                new ThrowingBookingRepository(new BookingRepository(context), fixture.BookingId),
                new PaymentTransactionRepository(context),
                new RefundTransactionRepository(context),
                BuildSlotAvailabilityService(context),
                new BookingRescheduleRepository(context),
                new BookingProviderAssignmentRepository(context),
                new ProviderScheduleConflictService(context, TestServices.Occupancy()),
                context,
                TestServices.Clock(timeProvider),
                timeProvider,
                TestServices.Policies(reschedule: CollectingPolicy()), TestServices.ProviderNotificationPublisher(context), TestServices.PlanReservations(context),
                TestServices.Wallet(context), TestServices.Escrow(context),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RescheduleService>.Instance);

            var act = async () => await service.ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        (await WalletBalanceAsync(fixture.Customer.Id)).Should().Be(500m, "the customer is not charged for a move that did not happen");
        using var readContext = _db.CreateContext();
        var ledger = (await TestServices.Wallet(readContext).GetLedgerAsync(fixture.Customer.Id)).Value;
        ledger.Should().Contain(e => e.SourceType == WalletSourceType.RescheduleFee && e.EntryType == WalletEntryType.Debit);
        ledger.Should().Contain(e => e.SourceType == WalletSourceType.RescheduleFeeReversal && e.EntryType == WalletEntryType.Credit);
        (await new BookingRescheduleRepository(readContext).CountByBookingAsync(fixture.BookingId)).Should().Be(0);
    }

    [Fact]
    public async Task A_late_reschedule_fee_larger_than_the_cancellation_fee_is_credited_only_up_to_that_fee()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2026m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddHours(-3));
        await FundWalletAsync(fixture.Customer.Id, 1000m);
        // Late reschedule 30% against a 20% cancellation fee: more is paid at reschedule than cancelling would ever cost.
        var reschedulePolicy = new ReschedulePolicyOptions { LateRescheduleFeePercentage = 30m, CollectLateFeeFromWallet = true };
        decimal paid = RescheduleFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 6m, 30m).FeeAmount;
        decimal cancelFee = CancellationFeeCalculator.Compute(fixture.Total, TimeSpan.FromHours(3), 4m, 20m).FeeAmount;
        paid.Should().BeGreaterThan(cancelFee);

        using (var context = _db.CreateContext())
        {
            var result = await BuildRescheduleService(context, timeProvider, reschedulePolicy).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Running late"));
            result.IsSuccess.Should().BeTrue();
            result.Value.FeeCollected.Should().Be(paid);
        }

        using var cancelContext = _db.CreateContext();
        var cancelled = await BuildCancellationService(cancelContext, gateway, timeProvider).CancelAsync(
            fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));

        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.CancellationFeeBeforeCredit.Should().Be(cancelFee);
        cancelled.Value.RescheduleFeeCredited.Should().Be(cancelFee, "the credit stops at the cancellation fee itself");
        cancelled.Value.CancellationFeeAmount.Should().Be(0m, "nothing further is retained");
        cancelled.Value.RefundAmount.Should().Be(fixture.Total, "the whole payment comes back");
        (await WalletBalanceAsync(fixture.Customer.Id)).Should().Be(1000m - paid, "the part paid beyond the cancellation fee is not handed back");
    }

    [Fact]
    public async Task Cancelling_tells_the_assigned_professional_the_booking_is_off_and_who_called_it_off()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2027m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        using (var context = _db.CreateContext())
        {
            var cancelled = await BuildCancellationService(context, gateway, timeProvider).CancelAsync(
                fixture.Customer.Id, fixture.BookingId, new CancelBookingRequest("Changed my mind"));
            cancelled.IsSuccess.Should().BeTrue();
        }

        var told = ProviderNotifications(providerId).Should().ContainSingle(n => n.Type == ProviderNotificationType.JobCancelled).Subject;
        told.Title.Should().Be("Job cancelled");
        told.Body.Should().Contain("cancelled by the customer").And.Contain("taken off your schedule");
        told.Body.Should().NotContain("Changed my mind", "the customer's reason is not the professional's business");
        told.DeepLinkPath.Should().Be("/jobs", "there is no job page left to open");
    }

    [Fact]
    public async Task An_admins_cancellation_tells_the_professional_it_was_Glavyx_not_the_customer()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2028m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, providerId);
        }

        using (var context = _db.CreateContext())
        {
            var cancelled = await BuildCancellationService(context, gateway, timeProvider).AdminCancelAsync(fixture.BookingId, "Ops decision");
            cancelled.IsSuccess.Should().BeTrue();
        }

        ProviderNotifications(providerId).Should().ContainSingle(n => n.Type == ProviderNotificationType.JobCancelled)
            .Subject.Body.Should().Contain("cancelled by Glavyx");
    }

    [Fact]
    public async Task A_policy_an_admin_saved_in_Settings_is_what_the_next_reschedule_enforces()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2030m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        // Five days out is nowhere near late under the configured 6 hours - but the admin has made it 10 days.
        var store = new InMemorySettingStore();
        store.Set(SystemSettingGroups.Reschedule, "{\"minHoursBeforeSlot\":2,\"maxReschedulesPerBooking\":2,\"lateFeeThresholdHours\":240,\"lateRescheduleFeePercentage\":10}", adminEdited: true);
        var provider = new BookingPolicyProvider(
            store, Options.Create(new CancellationPolicyOptions()), Options.Create(new ReschedulePolicyOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingPolicyProvider>.Instance);

        using var context2 = _db.CreateContext();
        var eligibility = (await BuildRescheduleService(context2, timeProvider, policies: provider)
            .GetEligibilityAsync(fixture.Customer.Id, fixture.BookingId)).Value;

        eligibility.LateFeeThresholdHours.Should().Be(240m, "the admin's saved value, not the configured 6");
        eligibility.IsLateNow.Should().BeTrue("five days out is inside the admin's ten-day window");
    }

    /// <summary>A settings store holding at most a row per group, for tests that need the provider's behaviour without the real table's other groups.</summary>
    private sealed class InMemorySettingStore : ISystemSettingRepository
    {
        private readonly Dictionary<string, SystemSetting> _rows = new();

        public void Set(string group, string json, bool adminEdited)
        {
            var row = new SystemSetting(Guid.NewGuid(), group, json);
            if (adminEdited)
            {
                row.UpdateValue(json, Guid.NewGuid());
            }

            _rows[group] = row;
        }

        public Task<SystemSetting?> GetByGroupKeyAsync(string groupKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.GetValueOrDefault(groupKey));

        public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemSetting>>(_rows.Values.ToList());

        public Task UpdateAsync(SystemSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// What the real ProviderAutoAssignmentHandler does when a rescheduled booking reaches AwaitingFulfilment, reduced to
    /// its effect: it hands the job to <c>assignTo</c> through the real assignment service, which supersedes any live row,
    /// moves the booking to Assigned and sends the new assignee the "New job offer" notification - from inside the
    /// dispatch, as the real handler does. Which professional it picks is the part the handler's own tests cover.
    /// </summary>
    private sealed class AutoAssignerStandIn : MediatR.IPublisher
    {
        private readonly Guid _bookingId;
        private readonly Guid _assignTo;

        public AutoAssignerStandIn(Guid bookingId, Guid assignTo)
        {
            _bookingId = bookingId;
            _assignTo = assignTo;
        }

        /// <summary>Set once the context exists - the interceptor has to be constructed before it.</summary>
        public NestlyDbContext? Context { get; set; }

        public bool Ran { get; private set; }

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Publish((MediatR.INotification)notification, cancellationToken);

        public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : MediatR.INotification
        {
            if (Ran
                || notification is not DomainEventNotification<BookingStatusChangedEvent> statusChanged
                || statusChanged.DomainEvent.BookingId != _bookingId
                || statusChanged.DomainEvent.FromStatus != BookingStatus.Rescheduled
                || statusChanged.DomainEvent.ToStatus != BookingStatus.AwaitingFulfilment)
            {
                return;
            }

            Ran = true;
            var result = await BuildAssignmentService(Context!).AssignBySystemAsync(_bookingId, _assignTo);
            result.IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_tells_the_original_professional_when_the_auto_assigner_hands_the_job_to_somebody_else()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2010m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid onTheJob, takesOver;
        using (var context = _db.CreateContext())
        {
            onTheJob = SeedProvider(context).Id;
            takesOver = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, onTheJob);
        }

        var assigner = new AutoAssignerStandIn(fixture.BookingId, takesOver);
        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext(new DomainEventDispatchInterceptor(assigner)))
        {
            assigner.Context = context;
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));
            assigner.Ran.Should().BeTrue("the test is only meaningful if the stand-in auto-assigner actually ran");
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        outcome.Professional.Should().Be(ProfessionalAfterReschedule.Released, "the job is somebody else's now, so the customer must not be told their professional stayed");

        var told = ProviderNotifications(onTheJob);
        told.Should().ContainSingle(n => n.Type == ProviderNotificationType.JobUnassigned, "nothing else would tell the professional the job was taken from them")
            .Subject.Body.Should().Contain("no longer assigned to you");
        told.Should().NotContain(n => n.Type == ProviderNotificationType.JobRescheduled);
        ProviderNotifications(takesOver).Should().ContainSingle(n => n.Type == ProviderNotificationType.JobOffered, "the assigner's own offer is how they hear of the job")
            .And.NotContain(n => n.Type == ProviderNotificationType.JobRescheduled, "a 'job rescheduled' message about a job that was never theirs would be wrong");

        using var readContext = _db.CreateContext();
        (await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId))!.AssignedProviderId.Should().Be(takesOver);
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_sends_no_second_message_when_the_auto_assigner_re_offers_the_job_to_the_same_professional()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2011m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid onTheJob;
        using (var context = _db.CreateContext())
        {
            onTheJob = SeedProvider(context).Id;
            await AssignProviderAsync(context, fixture.BookingId, onTheJob);
        }

        var assigner = new AutoAssignerStandIn(fixture.BookingId, onTheJob);
        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext(new DomainEventDispatchInterceptor(assigner)))
        {
            assigner.Context = context;
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));
            assigner.Ran.Should().BeTrue();
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        outcome.Professional.Should().Be(ProfessionalAfterReschedule.Kept);

        var told = ProviderNotifications(onTheJob);
        told.Count(n => n.Type == ProviderNotificationType.JobOffered).Should().Be(2, "the original offer, then the assigner's re-offer that carries the new time");
        told.Should().NotContain(n => n.Type == ProviderNotificationType.JobRescheduled, "a second message about the same change is noise");
    }

    [Fact]
    public async Task ConfirmRescheduleAsync_does_not_tell_whoever_the_auto_assigner_picks_that_a_job_they_never_had_was_rescheduled()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 2012m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        Guid picked;
        using (var context = _db.CreateContext())
        {
            picked = SeedProvider(context).Id;
        }

        // Nobody was on the booking before the reschedule.
        var assigner = new AutoAssignerStandIn(fixture.BookingId, picked);
        RescheduleOutcomeResponse outcome;
        using (var context = _db.CreateContext(new DomainEventDispatchInterceptor(assigner)))
        {
            assigner.Context = context;
            var result = await BuildRescheduleService(context, timeProvider).ConfirmRescheduleAsync(
                fixture.Customer.Id, fixture.BookingId,
                new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Different day"));
            assigner.Ran.Should().BeTrue();
            result.IsSuccess.Should().BeTrue();
            outcome = result.Value;
        }

        outcome.Professional.Should().Be(ProfessionalAfterReschedule.NoneAssigned);
        var told = ProviderNotifications(picked);
        told.Should().ContainSingle(n => n.Type == ProviderNotificationType.JobOffered);
        told.Should().NotContain(n => n.Type == ProviderNotificationType.JobRescheduled, "they were offered a new job, not told about a change to one they had");
    }

    /// <summary>
    /// Regression coverage for the orphaned-slot-capacity gap: the new
    /// slot's reservation (ISlotAvailabilityService.ReserveSlotAsync) is its
    /// own atomic conditional UPDATE, committed independently of the
    /// ambient DbContext - it is not rolled back just because the booking
    /// write that was meant to follow it never lands. Before this fix, a
    /// booking write failure here (a DB error, a timeout) left that
    /// reservation permanently in place with no booking to justify it - the
    /// new slot's capacity counter would eventually make it look full when
    /// it was not.
    /// </summary>
    [Fact]
    public async Task ConfirmRescheduleAsync_releases_the_new_slots_reservation_if_the_booking_write_fails()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, servicePrice: 1000m);
        var timeProvider = new FakeTimeProvider(fixture.SlotStartUtc.AddDays(-5));

        using (var capacityContext = _db.CreateContext())
        {
            // ReserveSlotAsync is a genuine no-op (Result.Success with no
            // write at all) for a window with no configured cap
            // (SlotWindow.MaxBookingsPerSlot null) - SeedPaidBookingAsync's
            // target window has none by default, so this test needs a real
            // cap to actually exercise SlotCapacityRepository's counter at
            // all, orphaned or not.
            var windowRepository = new SlotWindowRepository(capacityContext);
            var window = await windowRepository.GetByIdAsync(fixture.NewSlotWindowId);
            window!.SetCapacity(5);
            await windowRepository.UpdateAsync(window);
        }

        using var context = _db.CreateContext();

        var countsBefore = await new SlotCapacityRepository(context).GetBookedCountsAsync([fixture.NewSlotWindowId], fixture.NewSlotDate);
        countsBefore.GetValueOrDefault(fixture.NewSlotWindowId, 0).Should().Be(0, "nobody has booked the target slot yet");

        var throwingRepository = new ThrowingBookingRepository(new BookingRepository(context), fixture.BookingId);
        var service = new RescheduleService(
            throwingRepository,
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            BuildSlotAvailabilityService(context),
            new BookingRescheduleRepository(context),
            new BookingProviderAssignmentRepository(context),
            new ProviderScheduleConflictService(context, TestServices.Occupancy()),
            context,
            TestServices.Clock(timeProvider),
            timeProvider,
            TestServices.Policies(), TestServices.ProviderNotificationPublisher(context), TestServices.PlanReservations(context), TestServices.Wallet(context), TestServices.Escrow(context), Microsoft.Extensions.Logging.Abstractions.NullLogger<RescheduleService>.Instance);

        var act = async () => await service.ConfirmRescheduleAsync(
            fixture.Customer.Id, fixture.BookingId,
            new RescheduleBookingRequest(fixture.LocalityId, fixture.NewSlotWindowId, fixture.NewSlotDate, "Need a different day"));

        await act.Should().ThrowAsync<InvalidOperationException>("the simulated write failure must propagate, not be silently swallowed");

        var countsAfter = await new SlotCapacityRepository(context).GetBookedCountsAsync([fixture.NewSlotWindowId], fixture.NewSlotDate);
        countsAfter.GetValueOrDefault(fixture.NewSlotWindowId, 0).Should().Be(0, "the reservation taken before the failed write must be released, not left orphaned");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// A real <see cref="BookingRepository"/> with one booking rigged to fail
    /// on write, so the test exercises the service's own compensating logic
    /// against real persistence rather than an entirely fake repository -
    /// mirrors BookingFulfilmentPromotionJobTests' identical-purpose double.
    /// </summary>
    private sealed class ThrowingBookingRepository(IBookingRepository inner, Guid failingBookingId) : IBookingRepository
    {
        public Task UpdateAsync(Booking booking) =>
            booking.Id == failingBookingId
                ? throw new InvalidOperationException("Simulated write failure.")
                : inner.UpdateAsync(booking);

        public void DiscardChanges(Booking booking) => inner.DiscardChanges(booking);

        public Task AddAsync(Booking booking) => inner.AddAsync(booking);
        public Task<bool> TryAddAsync(Booking booking) => inner.TryAddAsync(booking);
        public Task<Booking?> GetByIdempotencyKeyAsync(Guid customerId, string idempotencyKey) => inner.GetByIdempotencyKeyAsync(customerId, idempotencyKey);
        public Task<Booking?> GetByIdAsync(Guid id) => inner.GetByIdAsync(id);
        public Task<IReadOnlyList<Booking>> ListByCustomerAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses) => inner.ListByCustomerAsync(customerId, statuses);
        public Task<(IReadOnlyList<Booking> Rows, int TotalCount)> ListByCustomerPagedAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses, int page, int pageSize) => inner.ListByCustomerPagedAsync(customerId, statuses, page, pageSize);
        public Task<BookingSearchResult> SearchAsync(BookingSearchFilter filter) => inner.SearchAsync(filter);
        public Task<IReadOnlyList<Booking>> ListByAssignedProviderAsync(Guid providerId) => inner.ListByAssignedProviderAsync(providerId);
        public Task<IReadOnlyList<Booking>> ListByRecurringPlanAsync(Guid recurringBookingPlanId) => inner.ListByRecurringPlanAsync(recurringBookingPlanId);
        public Task<IReadOnlyList<PlanVisitSummary>> ListVisitSummariesByPlansAsync(IReadOnlyCollection<Guid> planIds, DateOnly fromDate) => inner.ListVisitSummariesByPlansAsync(planIds, fromDate);
        public Task<int> CountCompletedByCustomerAsync(Guid customerId, Guid excludingBookingId) => inner.CountCompletedByCustomerAsync(customerId, excludingBookingId);
        public Task<int> CountCompletedByAssignedProviderAsync(Guid providerId, Guid excludingBookingId) => inner.CountCompletedByAssignedProviderAsync(providerId, excludingBookingId);
        public Task<IReadOnlyList<Booking>> ListStalePaymentPendingAsync(DateTime olderThanUtc, DateTime recurringOlderThanUtc) => inner.ListStalePaymentPendingAsync(olderThanUtc, recurringOlderThanUtc);
        public Task<IReadOnlyList<Booking>> ListRecurringPaymentPendingAsync() => inner.ListRecurringPaymentPendingAsync();
        public Task<IReadOnlyList<Booking>> ListConfirmedDueForFulfilmentAsync(DateOnly onOrAfterSlotDate, DateOnly onOrBeforeSlotDate, int skip, int take) => inner.ListConfirmedDueForFulfilmentAsync(onOrAfterSlotDate, onOrBeforeSlotDate, skip, take);
        public Task<IReadOnlyList<Booking>> ListSummariesByIdsAsync(IReadOnlyCollection<Guid> ids) => inner.ListSummariesByIdsAsync(ids);
        public Task<IReadOnlyList<Guid>> ListServiceIdsEverBookedAsync() => inner.ListServiceIdsEverBookedAsync();
        public Task<IReadOnlyDictionary<Guid, string>> ListServiceNamesByIdsAsync(IReadOnlyCollection<Guid> bookingIds) => inner.ListServiceNamesByIdsAsync(bookingIds);
        public Task<(IReadOnlyList<Booking> Rows, int TotalCount)> ListUnassignedAtRiskAsync(int page, int pageSize) => inner.ListUnassignedAtRiskAsync(page, pageSize);
        public Task<IReadOnlyList<Booking>> ListAwaitingPaymentAsync() => inner.ListAwaitingPaymentAsync();
        public Task<IReadOnlyList<Booking>> ListForFulfilmentBoardAsync(DateOnly date) => inner.ListForFulfilmentBoardAsync(date);
    }
}
