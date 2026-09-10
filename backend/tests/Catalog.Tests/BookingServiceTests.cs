using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 58-61: booking creation orchestration, snapshot persistence, customer APIs, status mapping.</summary>
public sealed class BookingServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingServiceTests(TestDatabase db) => _db = db;

    private BookingService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(
            new CouponRepository(context),
            new CouponRedemptionRepository(context),
            new BookingRepository(context),
            TimeProvider.System);

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

    /// <summary>
    /// Builds a BookingService wired to the *real* provider matching +
    /// eligibility chain (not <see cref="AlwaysEligibleProviderSearchStub"/>),
    /// mirroring PaymentServiceTests.BuildEligibleProviderSearchService so the
    /// two gates are proven against the identical composition. Used only by
    /// the provider-availability-gate tests below - every other test in this
    /// class keeps using the stub, since who-can-take-the-job is not what
    /// they mean to cover.
    /// </summary>
    private static BookingService BuildServiceWithRealEligibility(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(
            new CouponRepository(context),
            new CouponRedemptionRepository(context),
            new BookingRepository(context),
            TimeProvider.System);

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

        var eligibleProviderSearchService = new EligibleProviderSearchService(
            new ProviderMatchingService(
                new BookingRepository(context),
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions())),
            new ProviderAssignmentEligibilityService(
                new BookingRepository(context),
                new ProviderAvailabilityWindowRepository(context),
                new ProviderBlackoutDateRepository(context),
                new ProviderCapacityRepository(context),
                new ProviderScheduleConflictService(context, TestServices.Occupancy()),
                TravelFeasibilityFactory.Sandbox(context),
                context));

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
            eligibleProviderSearchService,
            context);
    }

    /// <summary>Same shape as PaymentServiceTests' identical helper - an Active provider whose skill/service-area/availability cover the fixture's booking exactly.</summary>
    private static Provider AddActiveEligibleProvider(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid categoryId, Guid cityId, DayOfWeek dayOfWeek, decimal lat, decimal lng)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        provider.UpdateLocation(lat, lng);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, categoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, cityId));
        context.Add(new ProviderAvailabilityWindow(Guid.NewGuid(), provider.Id, dayOfWeek, TimeSpan.FromHours(8), TimeSpan.FromHours(18)));
        return provider;
    }

    private sealed record Fixture(
        Customer Customer, CustomerAddress Address, City City, Pincode Pincode,
        Locality Locality, Service Service, ServiceAddOn AddOn, SlotWindow Window, DateOnly Date);

    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
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
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Sofa Cleaning", 150m);
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
        context.Add(addOn);
        context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SlotWindows.Add(window);
        context.SlotWindowRules.Add(rule);
        context.SaveChanges();

        return new Fixture(customer, address, city, pincode, locality, service, addOn, window, futureDate);
    }

    private static BookingSummaryRequest RequestFor(Fixture f, IReadOnlyList<AddOnSelection>? addOns = null) => new(
        f.Service.Id, f.City.Id, f.Address.Id, f.Locality.Id, f.Window.Id, f.Date, Quantity: 1, addOns ?? []);

    [Fact]
    public async Task CreateAsync_persists_a_booking_in_PaymentPending_with_a_two_entry_timeline()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using var createContext = _db.CreateContext();
        var created = await BuildService(createContext).CreateAsync(
            fixture.Customer.Id, RequestFor(fixture, [new AddOnSelection(fixture.AddOn.Id, 2)]));

        created.IsSuccess.Should().BeTrue();
        created.Value.Status.Should().Be(BookingStatus.PaymentPending);
        created.Value.StatusLabel.Should().Be("Awaiting Payment");
        created.Value.Timeline.Should().HaveCount(2);
        created.Value.Timeline[0].ToStatus.Should().Be(BookingStatus.Initiated);
        created.Value.Timeline[1].ToStatus.Should().Be(BookingStatus.PaymentPending);
        created.Value.AddOns.Should().ContainSingle(a => a.Id == fixture.AddOn.Id);
        created.Value.Price.TotalPayable.Should().Be(800m);
        // Task 331's regression guard: the zero-payable fast path added there
        // must never claim a booking that someone still owes money on.
        created.Value.FinalPayable.Should().Be(800m);
        created.Value.Timeline.Should().NotContain(entry => entry.ToStatus == BookingStatus.Confirmed);

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(created.Value.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Items.Should().ContainSingle();
        reloaded.Items[0].AddOns.Should().ContainSingle(a => a.LineTotalSnapshot == 300m);
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Booking payment ... Provider availability
    /// gate": before this fix, CreateAsync had no idea whether any provider
    /// could actually serve this pincode/slot - it created the booking in
    /// PaymentPending regardless, and the customer only found out at the
    /// payment step (PaymentService.CreateOrderAsync's own gate), leaving an
    /// unpayable, uncancellable orphan behind. No provider is seeded here at
    /// all, so the real eligibility chain has nothing to find.
    /// </summary>
    [Fact]
    public async Task CreateAsync_fails_with_NoProviderAvailable_and_persists_no_booking_when_no_eligible_provider_exists()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using var createContext = _db.CreateContext();
        var created = await BuildServiceWithRealEligibility(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("Booking.NoProviderAvailable");

        using var readContext = _db.CreateContext();
        var (bookings, totalCount) = await new BookingRepository(readContext).ListByCustomerPagedAsync(
            fixture.Customer.Id, Enum.GetValues<BookingStatus>(), 1, 20);
        totalCount.Should().Be(0);
        bookings.Should().BeEmpty(
            "a request nobody can fulfil must never leave a persisted 'Awaiting Payment' row behind - the whole point of the gate");
    }

    /// <summary>
    /// The companion to the failure test above: the same real eligibility
    /// chain must not block a booking that genuinely has an eligible
    /// provider - the gate has to actually gate on eligibility, not just
    /// always refuse.
    /// </summary>
    [Fact]
    public async Task CreateAsync_succeeds_through_the_real_eligibility_gate_when_an_eligible_provider_exists()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            // Same coordinates as the address: a zero-length leg, so travel
            // feasibility never has grounds to refuse this provider (see
            // TravelFeasibilityFactory.Sandbox's doc comment).
            AddActiveEligibleProvider(context, fixture.Service.CategoryId, fixture.City.Id, fixture.Date.DayOfWeek, 12.9716m, 77.5946m);
            context.SaveChanges();
        }

        using var createContext = _db.CreateContext();
        var created = await BuildServiceWithRealEligibility(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        created.IsSuccess.Should().BeTrue();
        created.Value.Status.Should().Be(BookingStatus.PaymentPending);
    }

    /// <summary>
    /// Task 331: a booking whose payable has been driven to zero - here by
    /// wallet credit covering the whole price - is confirmed on creation
    /// instead of being parked in PaymentPending, which for it is a dead end
    /// (<see cref="PaymentTransaction"/> rejects a non-positive amount, so no
    /// gateway order exists to confirm it, and BookingExpirySweepJob would
    /// eventually expire a booking the customer has already paid for out of
    /// their wallet).
    /// </summary>
    [Fact]
    public async Task CreateAsync_confirms_a_wallet_fully_covered_booking_without_a_payment_step()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            await new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(fixture.Customer.Id, 900m, WalletSourceType.PromotionalCredit, null, "Promo");
        }

        using var createContext = _db.CreateContext();
        var created = await BuildService(createContext).CreateAsync(
            fixture.Customer.Id, RequestFor(fixture) with { ApplyWalletCredit = true });

        created.IsSuccess.Should().BeTrue();
        created.Value.FinalPayable.Should().Be(0m);
        created.Value.WalletCreditApplied.Should().Be(500m);
        created.Value.Status.Should().Be(BookingStatus.Confirmed);
        created.Value.StatusLabel.Should().Be("Confirmed");
        created.Value.Timeline.Should().HaveCount(2);
        created.Value.Timeline[0].ToStatus.Should().Be(BookingStatus.Initiated);
        created.Value.Timeline[1].ToStatus.Should().Be(BookingStatus.Confirmed);
        created.Value.Timeline[1].Reason.Should().Be(
            "Nothing payable on this booking - confirmed without a payment.",
            "the timeline is where a never-charged booking stays explainable later");

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(created.Value.Id);
        reloaded!.Status.Should().Be(BookingStatus.Confirmed);
        reloaded.StatusHistory.Should().NotContain(entry => entry.ToStatus == BookingStatus.PaymentPending);

        (await new PaymentTransactionRepository(readContext).GetByBookingIdAsync(created.Value.Id))
            .Should().BeNull("nothing was charged, so there is no gateway transaction to record");
        (await new WalletService(new WalletLedgerRepository(readContext), readContext).GetBalanceAsync(fixture.Customer.Id))
            .Value.Balance.Should().Be(400m, "the wallet ledger is what records how this booking was actually settled");
    }

    /// <summary>
    /// Task 331, the second zero-payable producer that is not AMC: a discount
    /// large enough to take the total to zero. Same confirmation path - the
    /// rule is "nothing is payable", not "this is an entitlement redemption".
    /// </summary>
    [Fact]
    public async Task CreateAsync_confirms_a_fully_discounted_booking_without_a_payment_step()
    {
        Fixture fixture;
        string couponCode = "FREE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            context.Add(new Coupon(
                Guid.NewGuid(), couponCode, "Fully discounted", CouponDiscountType.Percentage, 100m,
                maxDiscountAmount: null, minOrderAmount: 0m,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30),
                usageLimitTotal: null, usageLimitPerCustomer: null,
                applicableCategoryId: null, CouponCustomerSegment.All));
            context.SaveChanges();
        }

        using var createContext = _db.CreateContext();
        var created = await BuildService(createContext).CreateAsync(
            fixture.Customer.Id, RequestFor(fixture) with { CouponCode = couponCode });

        created.IsSuccess.Should().BeTrue();
        created.Value.FinalPayable.Should().Be(0m);
        created.Value.Status.Should().Be(BookingStatus.Confirmed);
        created.Value.Timeline.Should().NotContain(entry => entry.ToStatus == BookingStatus.PaymentPending);
    }

    [Fact]
    public async Task CreateAsync_with_a_selected_variant_and_a_grouped_addon_persists_their_snapshots()
    {
        Fixture fixture;
        ServiceVariant variant;
        ServiceAddOnGroup group;
        ServiceAddOn groupedAddOn;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            variant = new ServiceVariant(Guid.NewGuid(), fixture.Service.Id, "Premium Clean", 799m, 120);
            group = new ServiceAddOnGroup(Guid.NewGuid(), fixture.Service.Id, "Detergent", AddOnGroupSelectionType.Single);
            groupedAddOn = new ServiceAddOn(Guid.NewGuid(), fixture.Service.Id, "Eco Detergent", 60m);
            groupedAddOn.SetGroupId(group.Id);
            context.Add(variant);
            context.Add(group);
            context.Add(groupedAddOn);
            context.SaveChanges();
        }

        using var createContext = _db.CreateContext();
        var request = RequestFor(fixture, [new AddOnSelection(groupedAddOn.Id, 1)]) with { ServiceVariantId = variant.Id };
        var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, request);

        created.IsSuccess.Should().BeTrue();
        created.Value.Service.VariantId.Should().Be(variant.Id);
        created.Value.Price.BasePrice.Should().Be(799m);

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(created.Value.Id);
        var item = reloaded!.Items.Should().ContainSingle().Subject;
        item.ServiceVariantId.Should().Be(variant.Id);
        item.VariantNameSnapshot.Should().Be("Premium Clean");
        item.VariantDurationMinutesSnapshot.Should().Be(120);

        var addOnItem = item.AddOns.Should().ContainSingle().Subject;
        addOnItem.AddOnGroupId.Should().Be(group.Id);
        addOnItem.GroupNameSnapshot.Should().Be("Detergent");
    }

    [Fact]
    public async Task CreateAsync_for_a_grouped_service_persists_the_service_group_snapshot()
    {
        Fixture fixture;
        ServiceGroup group;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            group = new ServiceGroup(Guid.NewGuid(), fixture.Service.CategoryId, "Repair & gas refill");
            context.Add(group);
            context.SaveChanges();

            var serviceRepository = new ServiceRepository(context);
            var service = (await serviceRepository.GetByIdAsync(fixture.Service.Id))!;
            service.SetServiceGroupId(group.Id);
            await serviceRepository.UpdateAsync(service);
        }

        using var createContext = _db.CreateContext();
        var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        created.IsSuccess.Should().BeTrue();
        created.Value.Service.GroupId.Should().Be(group.Id);
        created.Value.Service.GroupName.Should().Be("Repair & gas refill");

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(created.Value.Id);
        var item = reloaded!.Items.Should().ContainSingle().Subject;
        item.ServiceGroupId.Should().Be(group.Id);
        item.ServiceGroupNameSnapshot.Should().Be("Repair & gas refill");
    }

    [Fact]
    public async Task CreateAsync_rejects_the_same_invalid_input_the_summary_would_reject()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using var context2 = _db.CreateContext();
        var request = RequestFor(fixture) with { SlotWindowId = Guid.NewGuid() };
        var result = await BuildService(context2).CreateAsync(fixture.Customer.Id, request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.SlotNotAvailable");
    }

    [Fact]
    public async Task GetDetailAsync_returns_not_found_for_another_customers_booking()
    {
        Fixture fixture;
        Guid bookingId;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetDetailAsync(Guid.NewGuid(), bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.NotFound");
    }

    /// <summary>
    /// Task 208 audit: Booking.Status stays "Assigned" through both the
    /// admin's offer and the provider's accept, so before this fix the
    /// customer's GetDetailAsync response had no way to distinguish the two -
    /// a provider accepting never touched Booking.Status/StatusHistory at all.
    /// ProviderAssignmentStatus (sourced from the live BookingProviderAssignment
    /// row, not the booking) is how the accept becomes visible immediately.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_reflects_a_providers_Accept_immediately()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        Guid bookingId;
        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        var beforeAssignment = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        beforeAssignment.Value.ProviderAssignmentStatus.Should().BeNull();

        Guid providerId;
        var adminUserId = Guid.NewGuid();
        using (var setupContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(setupContext).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await new BookingRepository(setupContext).UpdateAsync(booking);

            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
            provider.ChangeStatus(ProviderStatus.Active);
            setupContext.Add(provider);
            await setupContext.SaveChangesAsync();
            providerId = provider.Id;

            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(setupContext), new ProviderRepository(setupContext), new ServiceRepository(setupContext),
                new BookingProviderAssignmentRepository(setupContext), new ProviderScheduleConflictService(setupContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), setupContext);
            var assignResult = await assignmentService.AssignAsync(bookingId, adminUserId, new AssignProviderRequest(providerId, ResponseDeadline: null));
            assignResult.IsSuccess.Should().BeTrue();
        }

        var afterAssign = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        afterAssign.Value.Status.Should().Be(BookingStatus.Assigned);
        afterAssign.Value.ProviderAssignmentStatus.Should().Be(BookingProviderAssignmentStatus.Assigned);

        using (var acceptContext = _db.CreateContext())
        {
            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(acceptContext), new ProviderRepository(acceptContext), new ServiceRepository(acceptContext),
                new BookingProviderAssignmentRepository(acceptContext), new ProviderScheduleConflictService(acceptContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), acceptContext);
            var acceptResult = await assignmentService.AcceptAsync(bookingId, providerId);
            acceptResult.IsSuccess.Should().BeTrue();
        }

        var afterAccept = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        afterAccept.Value.Status.Should().Be(BookingStatus.Assigned, "Booking.Status has no separate accepted state by design (SRS 13.1)");
        afterAccept.Value.ProviderAssignmentStatus.Should().Be(BookingProviderAssignmentStatus.Accepted, "the accept must be visible to the customer the moment it happens");
    }

    /// <summary>Task 241: the core dedup case - a retried request carrying the same key must not create a second booking.</summary>
    [Fact]
    public async Task CreateAsync_with_a_repeated_idempotency_key_returns_the_same_booking_instead_of_a_duplicate()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        var request = RequestFor(fixture) with { IdempotencyKey = "checkout-attempt-1" };

        using var firstContext = _db.CreateContext();
        var first = await BuildService(firstContext).CreateAsync(fixture.Customer.Id, request);
        first.IsSuccess.Should().BeTrue();

        using var retryContext = _db.CreateContext();
        var retry = await BuildService(retryContext).CreateAsync(fixture.Customer.Id, request);

        retry.IsSuccess.Should().BeTrue();
        retry.Value.Id.Should().Be(first.Value.Id, "a replayed key must resolve to the booking already created, not a second one");

        using var readContext = _db.CreateContext();
        var all = await new BookingRepository(readContext).ListByCustomerAsync(fixture.Customer.Id, Enum.GetValues<BookingStatus>());
        all.Should().ContainSingle();
    }

    /// <summary>Task 241: a different key (a genuinely new attempt - e.g. the customer changed something and tried again) must not be treated as a duplicate.</summary>
    [Fact]
    public async Task CreateAsync_with_a_different_idempotency_key_creates_a_separate_booking()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildService(firstContext).CreateAsync(
                fixture.Customer.Id, RequestFor(fixture) with { IdempotencyKey = "checkout-attempt-1" });
            first.IsSuccess.Should().BeTrue();
        }

        using var secondContext = _db.CreateContext();
        var second = await BuildService(secondContext).CreateAsync(
            fixture.Customer.Id, RequestFor(fixture) with { IdempotencyKey = "checkout-attempt-2" });

        second.IsSuccess.Should().BeTrue();

        using var readContext = _db.CreateContext();
        var all = await new BookingRepository(readContext).ListByCustomerAsync(fixture.Customer.Id, Enum.GetValues<BookingStatus>());
        all.Should().HaveCount(2);
    }

    /// <summary>No key supplied at all (an older client, or a caller like RecurringBookingSchedulerService) gets no dedup protection - same behaviour as before task 241.</summary>
    [Fact]
    public async Task CreateAsync_with_no_idempotency_key_creates_a_new_booking_every_call()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildService(firstContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            first.IsSuccess.Should().BeTrue();
        }

        using var secondContext = _db.CreateContext();
        var second = await BuildService(secondContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));

        second.IsSuccess.Should().BeTrue();

        using var readContext = _db.CreateContext();
        var all = await new BookingRepository(readContext).ListByCustomerAsync(fixture.Customer.Id, Enum.GetValues<BookingStatus>());
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_filters_by_bucket()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        using (var createContext = _db.CreateContext())
        {
            await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext);

        var upcoming = await service.ListAsync(fixture.Customer.Id, BookingStatusBucket.Upcoming);
        upcoming.Value.Items.Should().ContainSingle();
        upcoming.Value.TotalCount.Should().Be(1);

        var completed = await service.ListAsync(fixture.Customer.Id, BookingStatusBucket.Completed);
        completed.Value.Items.Should().BeEmpty();
        completed.Value.TotalCount.Should().Be(0);

        var all = await service.ListAsync(fixture.Customer.Id, bucket: null);
        all.Value.Items.Should().ContainSingle();
    }

    /// <summary>Task 301-follow-up: a customer with more bookings than fit on one page gets a page, not the whole history at once - and TotalCount still reflects the full match count so the frontend knows there is a next page.</summary>
    [Fact]
    public async Task ListAsync_pages_results_newest_first()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        for (int i = 0; i < 3; i++)
        {
            using var createContext = _db.CreateContext();
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            created.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext);

        var firstPage = await service.ListAsync(fixture.Customer.Id, bucket: null, page: 1, pageSize: 2);
        firstPage.Value.Items.Should().HaveCount(2);
        firstPage.Value.TotalCount.Should().Be(3);

        var secondPage = await service.ListAsync(fixture.Customer.Id, bucket: null, page: 2, pageSize: 2);
        secondPage.Value.Items.Should().ContainSingle();
        secondPage.Value.TotalCount.Should().Be(3);

        firstPage.Value.Items.Select(i => i.Id).Should().NotIntersectWith(secondPage.Value.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Task 275's second deliverable: the booking detail names WHO is coming,
    /// not just that someone was assigned. This is the non-tracking path -
    /// the customer opening their booking before (or after) the live tracking
    /// window still needs the provider's identity.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_names_the_provider_once_one_is_assigned()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        Guid bookingId;
        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        var beforeAssignment = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);
        beforeAssignment.Value.Provider.Should().BeNull("nobody has been assigned yet, so there is nobody to name");

        using (var setupContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(setupContext).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await new BookingRepository(setupContext).UpdateAsync(booking);

            var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876500275");
            provider.ChangeStatus(ProviderStatus.Active);
            setupContext.Add(provider);
            await setupContext.SaveChangesAsync();

            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(setupContext), new ProviderRepository(setupContext), new ServiceRepository(setupContext),
                new BookingProviderAssignmentRepository(setupContext), new ProviderScheduleConflictService(setupContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), setupContext);
            var assignResult = await assignmentService.AssignAsync(bookingId, Guid.NewGuid(), new AssignProviderRequest(provider.Id, ResponseDeadline: null));
            assignResult.IsSuccess.Should().BeTrue();
        }

        var afterAssign = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);

        afterAssign.Value.Provider.Should().NotBeNull();
        afterAssign.Value.Provider!.DisplayName.Should().Be("Ravi's Repairs", "the trading name is what a customer recognises, not the legal name");
        afterAssign.Value.Provider.DisplayName.Should().NotBe("Ravi Kumar");

        // No column backs either yet - pinned so that whoever adds one has to
        // come back through this test rather than shipping a fabricated value.
        afterAssign.Value.Provider.PhotoUrl.Should().BeNull();
        afterAssign.Value.Provider.Rating.Should().BeNull();
    }

    /// <summary>
    /// The provider summary must not outlive the assignment. A booking whose
    /// provider was withdrawn is back to "nobody is coming", and a detail
    /// response still naming them would have the customer waiting for someone
    /// who is no longer on the job.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_drops_the_provider_summary_when_the_assignment_is_no_longer_live()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
        }

        Guid bookingId;
        using (var createContext = _db.CreateContext())
        {
            var created = await BuildService(createContext).CreateAsync(fixture.Customer.Id, RequestFor(fixture));
            bookingId = created.Value.Id;
        }

        Guid providerId;
        using (var setupContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(setupContext).GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await new BookingRepository(setupContext).UpdateAsync(booking);

            var provider = new Provider(Guid.NewGuid(), "Meena Rao", "Meena Home Services", ProviderType.Individual, "+919876500276");
            provider.ChangeStatus(ProviderStatus.Active);
            setupContext.Add(provider);
            await setupContext.SaveChangesAsync();
            providerId = provider.Id;

            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(setupContext), new ProviderRepository(setupContext), new ServiceRepository(setupContext),
                new BookingProviderAssignmentRepository(setupContext), new ProviderScheduleConflictService(setupContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), setupContext);
            await assignmentService.AssignAsync(bookingId, Guid.NewGuid(), new AssignProviderRequest(providerId, ResponseDeadline: null));
        }

        (await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId))
            .Value.Provider.Should().NotBeNull();

        using (var rejectContext = _db.CreateContext())
        {
            var assignmentService = new BookingProviderAssignmentService(
                new BookingRepository(rejectContext), new ProviderRepository(rejectContext), new ServiceRepository(rejectContext),
                new BookingProviderAssignmentRepository(rejectContext), new ProviderScheduleConflictService(rejectContext, TestServices.Occupancy()),
                Options.Create(new AutoAssignmentOptions()), rejectContext);
            var rejectResult = await assignmentService.RejectAsync(bookingId, new RejectAssignmentRequest("Unavailable"));
            rejectResult.IsSuccess.Should().BeTrue();
        }

        var afterReject = await BuildService(_db.CreateContext()).GetDetailAsync(fixture.Customer.Id, bookingId);

        afterReject.Value.ProviderAssignmentStatus.Should().BeNull();
        afterReject.Value.Provider.Should().BeNull("the summary is keyed off the LIVE assignment, not the booking's assignment history");
    }
}
