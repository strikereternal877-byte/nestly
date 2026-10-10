using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Application.Slots;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 240: BookingExpirySweepJob only sweeps PaymentPending bookings
/// past the configured expiry window, transitions them to
/// <see cref="BookingStatus.Expired"/>, and releases the slot seat each one
/// was holding - a booking still within the window, or in any other status,
/// must be left untouched.
///
/// Uses a hand-written <see cref="FakeSlotAvailabilityService"/> rather than
/// the real <c>SlotAvailabilityService</c> - ReleaseSlotAsync's own capacity
/// math is already covered by task 135c's tests and
/// <c>CancellationServiceTests</c>; what this suite proves is that the job
/// itself calls it exactly once per swept booking and never for a skipped one.
/// </summary>
public sealed class BookingExpirySweepJobTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingExpirySweepJobTests(TestDatabase db) => _db = db;

    private sealed class FakeSlotAvailabilityService : ISlotAvailabilityService
    {
        public List<(Guid SlotWindowId, DateOnly Date)> Released { get; } = [];

        public Task ReleaseSlotAsync(Guid slotWindowId, DateOnly date)
        {
            Released.Add((slotWindowId, date));
            return Task.CompletedTask;
        }

        public Task<Result<SlotAvailabilityResponse>> GetAvailableSlotsAsync(Guid serviceId, Guid localityId, DateOnly date) =>
            throw new NotImplementedException();

        public Task<Result<SlotRangeResponse>> GetAvailableSlotsRangeAsync(Guid serviceId, Guid localityId, DateOnly from, DateOnly to) =>
            throw new NotImplementedException();

        public Task<Result<SlotRevalidationResponse>> RevalidateSlotAsync(Guid serviceId, Guid localityId, Guid slotWindowId, DateOnly date) =>
            throw new NotImplementedException();

        public Task<Result> ReserveSlotAsync(Guid slotWindowId, DateOnly date) =>
            throw new NotImplementedException();
    }

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

    private static Booking NewPaymentPendingBooking(Guid customerId, Guid slotWindowId)
    {
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(slotWindowId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        var booking = new Booking(Guid.NewGuid(), customerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);
        booking.TransitionTo(BookingStatus.PaymentPending);
        return booking;
    }

    [Fact]
    public async Task SweepAsync_expires_a_stale_PaymentPending_booking_and_releases_its_slot()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var stale = NewPaymentPendingBooking(customer.Id, slotWindowId);

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);

            var repository = new BookingRepository(context);
            await repository.AddAsync(stale);

            // Backdate past the 20-minute default expiry window - CreatedAtUtc
            // has no public setter (deliberately: it's set once, at
            // construction, same as every other snapshot timestamp in this
            // codebase), so this is the only way to simulate "created a while
            // ago" without sleeping the test.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE booking SET created_at_utc = {DateTime.UtcNow.AddMinutes(-30)} WHERE id = {stale.Id}");
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(new BookingRepository(context),
                new UnpaidBookingReleaseService(new BookingRepository(context), slotService, new WalletService(new WalletLedgerRepository(context), context), new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System)),
                Options.Create(new BookingExpiryOptions()),
                Options.Create(new RecurringBookingOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(stale.Id);
            reloaded!.Status.Should().Be(BookingStatus.Expired);
            reloaded.StatusHistory.Last().Reason.Should().Be("Payment was not completed within the expiry window.");
        }

        slotService.Released.Should().ContainSingle(r => r.SlotWindowId == slotWindowId);
    }

    [Fact]
    public async Task SweepAsync_leaves_a_recent_PaymentPending_booking_untouched()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var recent = NewPaymentPendingBooking(customer.Id, slotWindowId);

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);
            await new BookingRepository(context).AddAsync(recent);
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(new BookingRepository(context),
                new UnpaidBookingReleaseService(new BookingRepository(context), slotService, new WalletService(new WalletLedgerRepository(context), context), new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System)),
                Options.Create(new BookingExpiryOptions()),
                Options.Create(new RecurringBookingOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(recent.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentPending, "it was created well within the expiry window");
        }

        slotService.Released.Should().BeEmpty();
    }

    /// <summary>
    /// Minimal real <see cref="RecurringBookingPlan"/> to satisfy
    /// <see cref="Booking.RecurringBookingPlanId"/>'s real FK - same geography
    /// chain <c>RecurringBookingSchedulerServiceTests.Seed</c> builds, trimmed
    /// to what a plan row itself needs (no <see cref="SlotWindowRule"/>: the
    /// scheduler is never involved in these tests, only the sweep job).
    /// </summary>
    private static async Task<Guid> SeedRecurringPlanAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid customerId)
    {
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var address = new CustomerAddress(
            Guid.NewGuid(), customerId, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

        context.Add(address);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.SlotWindows.Add(window);

        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), customerId, service.Id, city.Id, locality.Id, address.Id, window.Id,
            quantity: 1, RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday, recurrenceDayOfMonth: null,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)), endDate: null, occurrenceCount: 52);
        await new RecurringBookingPlanRepository(context).AddAsync(plan);

        return plan.Id;
    }

    /// <summary>
    /// Task (recurring payment-timing fix): a recurring-generated occurrence
    /// must not be measured against the one-off checkout window
    /// (<see cref="BookingExpiryOptions.ExpiryMinutes"/>, 20 minutes by
    /// default) - it is created unattended, days ahead of the visit, so
    /// nobody is watching a checkout screen for it. Backdated 30 minutes -
    /// past the one-off cutoff, but nowhere near the default 24-hour
    /// <see cref="RecurringBookingOptions.PaymentWindowHours"/> - it must
    /// survive the sweep untouched.
    /// </summary>
    [Fact]
    public async Task SweepAsync_leaves_a_recurring_occurrence_untouched_within_its_own_longer_payment_window()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        Guid planId;
        Booking occurrence;

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);
            planId = await SeedRecurringPlanAsync(context, customer.Id);
        }

        using (var context = _db.CreateContext())
        {
            var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
            var slot = new SlotSnapshot(slotWindowId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
            occurrence = new Booking(
                Guid.NewGuid(), customer.Id, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price,
                recurringBookingPlanId: planId);
            occurrence.TransitionTo(BookingStatus.PaymentPending);

            await new BookingRepository(context).AddAsync(occurrence);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE booking SET created_at_utc = {DateTime.UtcNow.AddMinutes(-30)} WHERE id = {occurrence.Id}");
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(new BookingRepository(context),
                new UnpaidBookingReleaseService(new BookingRepository(context), slotService, new WalletService(new WalletLedgerRepository(context), context), new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System)),
                Options.Create(new BookingExpiryOptions()),
                Options.Create(new RecurringBookingOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentPending,
                "30 minutes is past the one-off checkout window but nowhere near a recurring occurrence's own, longer, payment window");
        }

        slotService.Released.Should().BeEmpty();
    }

    /// <summary>
    /// The other half of the same fix: the longer window is still a real
    /// deadline, not "recurring bookings never expire". Backdated past the
    /// default 24-hour <see cref="RecurringBookingOptions.PaymentWindowHours"/>,
    /// the occurrence must still expire and release its slot exactly like a
    /// one-off booking would.
    /// </summary>
    [Fact]
    public async Task SweepAsync_expires_a_recurring_occurrence_once_past_its_own_longer_payment_window()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        Guid planId;
        Booking occurrence;

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);
            planId = await SeedRecurringPlanAsync(context, customer.Id);
        }

        using (var context = _db.CreateContext())
        {
            var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
            var slot = new SlotSnapshot(slotWindowId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
            occurrence = new Booking(
                Guid.NewGuid(), customer.Id, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price,
                recurringBookingPlanId: planId);
            occurrence.TransitionTo(BookingStatus.PaymentPending);

            await new BookingRepository(context).AddAsync(occurrence);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE booking SET created_at_utc = {DateTime.UtcNow.AddHours(-25)} WHERE id = {occurrence.Id}");
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(new BookingRepository(context),
                new UnpaidBookingReleaseService(new BookingRepository(context), slotService, new WalletService(new WalletLedgerRepository(context), context), new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System)),
                Options.Create(new BookingExpiryOptions()),
                Options.Create(new RecurringBookingOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.Expired, "25 hours is past the default 24-hour recurring payment window");
        }

        slotService.Released.Should().ContainSingle(r => r.SlotWindowId == slotWindowId);
    }

    /// <summary>
    /// Regression coverage for the wallet/coupon leak: before this fix, a
    /// PaymentPending booking that expired unpaid released only its slot -
    /// the wallet balance debited at checkout (BookingService.CreateAsync)
    /// and the coupon redemption reserved alongside it were both left
    /// permanently consumed for an order that never actually happened.
    /// </summary>
    [Fact]
    public async Task SweepAsync_on_expiry_reverses_the_wallet_debit_and_releases_the_reserved_coupon()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        string couponCode = "PART" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid customerId;
        Guid bookingId;
        Guid couponId;
        Guid slotWindowId;

        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
            customerId = customer.Id;
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
            var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 2000m);
            var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            slotWindowId = window.Id;
            var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);
            var coupon = new Coupon(
                Guid.NewGuid(), couponCode, "Ten percent off", CouponDiscountType.Percentage, 10m,
                maxDiscountAmount: null, minOrderAmount: 0m,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30),
                usageLimitTotal: null, usageLimitPerCustomer: null,
                applicableCategoryId: null, CouponCustomerSegment.All);
            couponId = coupon.Id;

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
            context.Add(coupon);
            context.SaveChanges();

            await new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(customer.Id, 500m, WalletSourceType.PromotionalCredit, null, "Test wallet credit");

            var request = new BookingSummaryRequest(
                service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, [],
                CouponCode: couponCode, ApplyWalletCredit: true);
            var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
            created.IsSuccess.Should().BeTrue();
            created.Value.Status.Should().Be(BookingStatus.PaymentPending, "a 10% discount plus 500 wallet credit on a 2000 service still leaves something payable");
            bookingId = created.Value.Id;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE booking SET created_at_utc = {DateTime.UtcNow.AddMinutes(-30)} WHERE id = {bookingId}");
        }

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(new BookingRepository(context),
                new UnpaidBookingReleaseService(new BookingRepository(context), new SlotAvailabilityService(
                    new ServiceabilityRepository(context),
                    new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                    new SlotWindowRepository(context),
                    new SlotBlackoutRepository(context),
                    new SlotBookingPolicyRepository(context),
                    new SlotCapacityRepository(context),
                    TestServices.Clock()), new WalletService(new WalletLedgerRepository(context), context), new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System)),
                Options.Create(new BookingExpiryOptions()),
                Options.Create(new RecurringBookingOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(bookingId);
            reloaded!.Status.Should().Be(BookingStatus.Expired);

            (await new WalletService(new WalletLedgerRepository(context), context).GetBalanceAsync(customerId))
                .Value.Balance.Should().Be(500m, "the debit taken at checkout must be credited back once the order never happened");

            var coupon = await new CouponRepository(context).GetByIdAsync(couponId);
            coupon!.RedemptionCount.Should().Be(0, "the booking that reserved this redemption never actually paid, so its usage must be given back");
            (await new CouponRedemptionRepository(context).CountByCouponAndCustomerAsync(couponId, customerId))
                .Should().Be(0, "the redemption record for a booking that never happened should not remain");
        }
    }
}
