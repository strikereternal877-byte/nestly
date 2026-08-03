using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 72a-d: coupon validation rules, validity window, usage/redemption cap enforcement, and applicability rules.</summary>
public sealed class CouponServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public CouponServiceTests(TestDatabase db) => _db = db;

    private static CouponService BuildCouponService(Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider? timeProvider = null) =>
        new(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), timeProvider ?? TimeProvider.System);

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = BuildCouponService(context);
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

    private sealed record Fixture(Customer Customer, City City, CustomerAddress Address, Locality Locality, Service Service, Category Category, SlotWindow Window, DateOnly Date);

    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice = 1000m)
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

        return new Fixture(customer, city, address, locality, service, category, window, futureDate);
    }

    private static Coupon FlatCoupon(
        string code = "SAVE100", decimal discountValue = 100m, decimal minOrderAmount = 0m,
        int? usageLimitTotal = null, int? usageLimitPerCustomer = 1, Guid? categoryId = null,
        CouponCustomerSegment segment = CouponCustomerSegment.All, DateTime? validFrom = null, DateTime? validTo = null) =>
        new(
            Guid.NewGuid(), code, "Test coupon", CouponDiscountType.Flat, discountValue, maxDiscountAmount: null,
            minOrderAmount, validFrom ?? DateTime.UtcNow.AddDays(-1), validTo ?? DateTime.UtcNow.AddDays(30),
            usageLimitTotal, usageLimitPerCustomer, categoryId, segment);

    [Fact]
    public async Task ValidateAsync_returns_NotFound_for_an_unknown_code()
    {
        using var context = _db.CreateContext();
        var result = await BuildCouponService(context).ValidateAsync(Guid.NewGuid(), "NOPE", Guid.NewGuid(), 1000m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.NotFound");
    }

    [Fact]
    public async Task ValidateAsync_rejects_a_coupon_outside_its_validity_window()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            seedContext.Coupons.Add(FlatCoupon("EXPIRED", validFrom: DateTime.UtcNow.AddDays(-30), validTo: DateTime.UtcNow.AddDays(-1)));
            seedContext.Coupons.Add(FlatCoupon("FUTURE", validFrom: DateTime.UtcNow.AddDays(10), validTo: DateTime.UtcNow.AddDays(20)));
            seedContext.SaveChanges();
        }

        using var context = _db.CreateContext();
        var service = BuildCouponService(context);

        var expired = await service.ValidateAsync(fixture.Customer.Id, "EXPIRED", fixture.Category.Id, 1000m);
        expired.IsFailure.Should().BeTrue();
        expired.Error.Code.Should().Be("Coupon.NotActive");

        var future = await service.ValidateAsync(fixture.Customer.Id, "FUTURE", fixture.Category.Id, 1000m);
        future.IsFailure.Should().BeTrue();
        future.Error.Code.Should().Be("Coupon.NotActive");
    }

    [Fact]
    public async Task ValidateAsync_enforces_the_minimum_order_amount()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            seedContext.Coupons.Add(FlatCoupon("MIN500", minOrderAmount: 500m));
            seedContext.SaveChanges();
        }

        using var context = _db.CreateContext();
        var result = await BuildCouponService(context).ValidateAsync(fixture.Customer.Id, "MIN500", fixture.Category.Id, orderAmount: 200m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.MinOrderAmountNotMet");
    }

    [Fact]
    public async Task ValidateAsync_rejects_a_coupon_not_applicable_to_the_selected_category()
    {
        Fixture fixture;
        Guid otherCategoryId = Guid.NewGuid();
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            seedContext.Add(new Category(otherCategoryId, "Repairs", "repairs-" + Guid.NewGuid(), "desc"));
            seedContext.Coupons.Add(FlatCoupon("CATONLY", categoryId: otherCategoryId));
            seedContext.SaveChanges();
        }

        using var context = _db.CreateContext();
        var result = await BuildCouponService(context).ValidateAsync(fixture.Customer.Id, "CATONLY", fixture.Category.Id, 1000m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.CategoryNotApplicable");
    }

    [Fact]
    public async Task ValidateAsync_enforces_first_booking_only_segment()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            seedContext.Coupons.Add(FlatCoupon("FIRSTONLY", segment: CouponCustomerSegment.FirstBookingOnly));
            seedContext.SaveChanges();
        }

        using (var firstCheckContext = _db.CreateContext())
        {
            var result = await BuildCouponService(firstCheckContext).ValidateAsync(fixture.Customer.Id, "FIRSTONLY", fixture.Category.Id, 1000m);
            result.IsSuccess.Should().BeTrue("a customer with no prior booking is eligible for a first-booking-only coupon");
        }

        // Customer places an unrelated booking, becoming a repeat customer.
        using (var bookContext = _db.CreateContext())
        {
            var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.Date, 1, []);
            var created = await BuildBookingService(bookContext).CreateAsync(fixture.Customer.Id, request);
            created.IsSuccess.Should().BeTrue();
        }

        using var secondCheckContext = _db.CreateContext();
        var secondResult = await BuildCouponService(secondCheckContext).ValidateAsync(fixture.Customer.Id, "FIRSTONLY", fixture.Category.Id, 1000m);
        secondResult.IsFailure.Should().BeTrue();
        secondResult.Error.Code.Should().Be("Coupon.FirstBookingOnly");
    }

    [Fact]
    public async Task ValidateAsync_enforces_per_customer_usage_cap()
    {
        Fixture fixture;
        Guid couponId;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            var coupon = FlatCoupon("ONEUSE", usageLimitPerCustomer: 1);
            couponId = coupon.Id;
            seedContext.Coupons.Add(coupon);
            seedContext.SaveChanges();
        }

        // A real, prior booking to satisfy CouponRedemption's foreign key -
        // unrelated to the coupon under test other than sharing the customer.
        Guid priorBookingId;
        using (var bookContext = _db.CreateContext())
        {
            var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.Date, 1, []);
            var created = await BuildBookingService(bookContext).CreateAsync(fixture.Customer.Id, request);
            created.IsSuccess.Should().BeTrue();
            priorBookingId = created.Value.Id;
        }

        using (var redemptionContext = _db.CreateContext())
        {
            redemptionContext.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), couponId, fixture.Customer.Id, priorBookingId, 100m));
            redemptionContext.SaveChanges();
        }

        using var context = _db.CreateContext();
        var result = await BuildCouponService(context).ValidateAsync(fixture.Customer.Id, "ONEUSE", fixture.Category.Id, 1000m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.AlreadyUsedByCustomer");
    }

    [Fact]
    public async Task ValidateAsync_reports_the_overall_usage_cap_as_reached()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
            var coupon = FlatCoupon("CAPPED", usageLimitTotal: 1);
            seedContext.Coupons.Add(coupon);
            seedContext.SaveChanges();

            // Simulate the cap already having been fully consumed.
            (await new CouponRepository(seedContext).TryReserveRedemptionAsync(coupon.Id)).Should().BeTrue();
        }

        using var context = _db.CreateContext();
        var result = await BuildCouponService(context).ValidateAsync(fixture.Customer.Id, "CAPPED", fixture.Category.Id, 1000m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.UsageLimitReached");
    }

    [Fact]
    public async Task TryReserveRedemptionAsync_never_lets_concurrent_reservations_exceed_the_global_cap()
    {
        Guid couponId;
        using (var seedContext = _db.CreateContext())
        {
            var coupon = FlatCoupon("RACE", usageLimitTotal: 3, usageLimitPerCustomer: null);
            couponId = coupon.Id;
            seedContext.Coupons.Add(coupon);
            seedContext.SaveChanges();
        }

        // Five sequential reservation attempts against a cap of 3 - modelling
        // the interleaving a real concurrent race would produce (see
        // BookingConcurrencyTests' doc comment on why this repo's SQLite
        // fixture tests races as deterministic interleavings rather than
        // literal parallel tasks). The atomic conditional UPDATE behind this
        // must let exactly 3 succeed no matter the order.
        int succeeded = 0;
        for (int i = 0; i < 5; i++)
        {
            using var context = _db.CreateContext();
            if (await new CouponRepository(context).TryReserveRedemptionAsync(couponId))
            {
                succeeded++;
            }
        }

        succeeded.Should().Be(3, "the global usage cap must never be exceeded, regardless of how many attempts race for it");

        using var readContext = _db.CreateContext();
        var final = await new CouponRepository(readContext).GetByIdAsync(couponId);
        final!.RedemptionCount.Should().Be(3);
    }

    [Fact]
    public async Task Applying_a_coupon_at_booking_creation_reduces_the_final_payable_and_records_a_redemption()
    {
        Fixture fixture;
        Guid couponId;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext, servicePrice: 1000m);
            var coupon = FlatCoupon("DISCOUNT100", discountValue: 100m);
            couponId = coupon.Id;
            seedContext.Coupons.Add(coupon);
            seedContext.SaveChanges();
        }

        using var context = _db.CreateContext();
        var request = new BookingSummaryRequest(
            fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.Date, 1, [], CouponCode: "DISCOUNT100");
        var created = await BuildBookingService(context).CreateAsync(fixture.Customer.Id, request);

        created.IsSuccess.Should().BeTrue();
        created.Value.CouponCode.Should().Be("DISCOUNT100");
        created.Value.CouponDiscountAmount.Should().Be(100m);
        created.Value.FinalPayable.Should().Be(900m);
        // Booking.TotalPayableSnapshot (feeding both Price.TotalPayable and
        // FinalPayable on the persisted detail) stores the single amount
        // actually charged - the discounted one. The pre-discount vs.
        // discounted "was / now" distinction is only available on the live
        // BookingSummaryResponse preview, before the booking is created.
        created.Value.Price.TotalPayable.Should().Be(900m, "the persisted booking snapshot records the amount actually charged");

        using var readContext = _db.CreateContext();
        var coupon2 = await new CouponRepository(readContext).GetByIdAsync(couponId);
        coupon2!.RedemptionCount.Should().Be(1);
    }
}
