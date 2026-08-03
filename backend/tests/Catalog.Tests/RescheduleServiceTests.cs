using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Reschedules;
using Nestly.Application.Serviceability;
using Nestly.Application.Slots;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 82a-d (eligibility window, count limits, slot revalidation, fee impact) and 83 (reschedule API/service).</summary>
public sealed class RescheduleServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RescheduleServiceTests(TestDatabase db) => _db = db;

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
            context, new NoOpMetricsService(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentWebhookService>.Instance);

    private static ISlotAvailabilityService BuildSlotAvailabilityService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new SlotAvailabilityService(
            new ServiceabilityRepository(context),
            new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
            new SlotWindowRepository(context),
            new SlotBlackoutRepository(context),
            new SlotBookingPolicyRepository(context),
            new SlotCapacityRepository(context),
            TimeProvider.System);

    private static RescheduleService BuildRescheduleService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider timeProvider, ReschedulePolicyOptions? policy = null) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            BuildSlotAvailabilityService(context),
            new BookingRescheduleRepository(context),
            timeProvider,
            Options.Create(policy ?? new ReschedulePolicyOptions()));

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total, Guid LocalityId, Guid NewSlotWindowId, DateOnly NewSlotDate, DateTime SlotStartUtc);

    /// <summary>A freshly created, fully paid booking (Confirmed) with its slot far in the future, plus a second slot window available on a later date to reschedule into.</summary>
    private async Task<Fixture> SeedPaidBookingAsync(IPaymentGateway gateway, decimal servicePrice = 1000m)
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

            var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
            var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
            created.IsSuccess.Should().BeTrue();
            bookingId = created.Value.Id;
            total = created.Value.Price.TotalPayable;
            localityId = locality.Id;
            newWindowId = newWindow.Id;
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

        var slotStartUtc = futureDate.ToDateTime(TimeOnly.MinValue).Add(slotStart);
        return new Fixture(customer, bookingId, total, localityId, newWindowId, newDate, slotStartUtc);
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
