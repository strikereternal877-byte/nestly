using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Performance.Tests;

/// <summary>
/// Shared by <see cref="ConcurrentSlotBookingPerformanceTests"/> (SQLite,
/// correctness) and <see cref="ConcurrentSlotBookingLoadTests"/> (real
/// Postgres, correctness + load) so the two suites prove the identical
/// invariant - <c>SlotWindow.MaxBookingsPerSlot</c> is never exceeded under
/// concurrent booking attempts - against two different database engines
/// without the ~14-dependency <see cref="BookingService"/> construction and
/// the domain-entity seeding drifting apart between two hand-maintained
/// copies.
/// </summary>
internal static class SlotBookingScenario
{
    public static BookingService BuildBookingService(NestlyDbContext context)
    {
        var couponService = new CouponService(
            new CouponRepository(context),
            new CouponRedemptionRepository(context),
            new BookingRepository(context),
            TimeProvider.System);

        var slotAvailabilityService = new SlotAvailabilityService(
            new ServiceabilityRepository(context),
            new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
            new SlotWindowRepository(context),
            new SlotBlackoutRepository(context),
            new SlotBookingPolicyRepository(context),
            new SlotCapacityRepository(context),
            TestServices.Clock());

        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            slotAvailabilityService,
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
            slotAvailabilityService,
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);
    }

    public sealed record Fixture(
        IReadOnlyList<(Customer Customer, CustomerAddress Address)> Customers,
        Guid ServiceId, Guid CityId, Guid LocalityId, Guid SlotWindowId, DateOnly Date);

    /// <summary>Seeds one city/service/slot window plus <paramref name="customerCount"/> independent customers, each with their own address, using a fresh context from <paramref name="createContext"/>.</summary>
    public static Fixture SeedSlotWithCapacity(Func<NestlyDbContext> createContext, int? capacity, int customerCount)
    {
        using var context = createContext();

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];

        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Promo Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        if (capacity is not null)
        {
            window.SetCapacity(capacity.Value);
        }

        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

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

        var customers = new List<(Customer, CustomerAddress)>(customerCount);
        for (int i = 0; i < customerCount; i++)
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], $"Customer {i}", CustomerStatus.Active);
            var address = new CustomerAddress(
                Guid.NewGuid(), customer.Id, "Home", $"{i} Residency Road", null, null,
                pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, $"Customer {i}", "9876500000", true);
            // As CustomerAddressService does on save - a booking is only
            // accepted for an address that resolves to the service area it is
            // being booked in.
            address.LinkToGeography(pincode.Id, locality.Id);
            context.Add(customer);
            context.Add(address);
            customers.Add((customer, address));
        }

        context.SaveChanges();

        return new Fixture(customers, service.Id, city.Id, locality.Id, window.Id, futureDate);
    }

    public static BookingSummaryRequest RequestFor(Fixture f, Guid addressId) =>
        new(f.ServiceId, f.CityId, addressId, f.LocalityId, f.SlotWindowId, f.Date, Quantity: 1, []);

    /// <summary>
    /// Always reports exactly one eligible provider - keeps these suites
    /// measuring slot-capacity contention, not provider-matching query cost,
    /// which is unrelated. See Catalog.Tests' identical stub for the full
    /// rationale; duplicated here rather than referenced because
    /// Performance.Tests does not reference that project.
    /// </summary>
    private sealed class AlwaysEligibleProviderSearchStub : IEligibleProviderSearchService
    {
        public async IAsyncEnumerable<ProviderMatchCandidate> FindEligibleAsync(
            Guid bookingId,
            IReadOnlyCollection<Guid>? excludeProviderIds = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ProviderMatchCandidate(Guid.NewGuid(), null);
        }
    }
}
