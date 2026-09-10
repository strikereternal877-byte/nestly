using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Catalog;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page,
/// Catalog health": <see cref="ServiceManagementService.ListHealthIssuesAsync"/>
/// flags an active service missing a city price, a cover image, an active
/// serviceability mapping, or that has never been booked - and only returns
/// services with at least one failing check.
/// </summary>
public sealed class CatalogHealthTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public CatalogHealthTests(TestDatabase db) => _db = db;

    private static ServiceManagementService BuildService(NestlyDbContext context) =>
        new(
            new ServiceRepository(context),
            new CategoryRepository(context),
            new ServiceGroupRepository(context),
            new ServiceMediaRepository(context),
            new AuditLogWriter(context, new StubAuditContextProvider()),
            new InMemoryCacheService(),
            new ServiceCityPriceRepository(context),
            new BookingRepository(context),
            new ServiceabilityMappingManagementService(
                new CategoryCityMappingRepository(context),
                new ServicePincodeMappingRepository(context),
                new CategoryRepository(context),
                new CityRepository(context),
                new ServiceRepository(context),
                new PincodeRepository(context)));

    private static (Category Category, Service Service, City City, Pincode Pincode) SeedActiveService(NestlyDbContext context, string? coverImageUrl = null)
    {
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Cleaning", "deep-cleaning-" + Guid.NewGuid(), "desc", 999m);
        if (coverImageUrl is not null)
        {
            service.SetCoverImageUrl(coverImageUrl);
        }

        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560" + Guid.NewGuid().ToString("N")[..3]);

        context.Add(category);
        context.Add(service);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Pincodes.Add(pincode);
        context.SaveChanges();

        return (category, service, city, pincode);
    }

    private static void SeedBookingFor(NestlyDbContext context, Service service)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Test Customer", CustomerStatus.Active);
        context.Add(customer);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot(customer.Name, customer.Mobile),
            null,
            new AddressSnapshot("Home", "123 St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(service.Price, 1, service.Price, 0m, 0m, service.Price, 0m, 0m, 0m, service.Price));
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, service.Price, 1);

        context.Add(booking);
        context.SaveChanges();
    }

    /// <summary>A freshly created active service with no price row, no image, no mapping and no booking fails all four checks at once.</summary>
    [Fact]
    public async Task A_brand_new_service_is_flagged_for_every_check()
    {
        using var context = _db.CreateContext();
        var (category, service, _, _) = SeedActiveService(context);
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        var issue = issues.Should().ContainSingle(i => i.ServiceId == service.Id).Subject;
        issue.ServiceName.Should().Be("Deep Cleaning");
        issue.CategoryId.Should().Be(category.Id);
        issue.Reasons.Should().BeEquivalentTo(
            CatalogHealthReason.NoPrice, CatalogHealthReason.NoImage, CatalogHealthReason.NoMapping, CatalogHealthReason.NeverBooked);
    }

    [Fact]
    public async Task A_service_with_an_active_city_price_row_is_not_flagged_for_missing_price()
    {
        using var context = _db.CreateContext();
        var (_, service, city, _) = SeedActiveService(context);
        context.Add(new ServiceCityPrice(Guid.NewGuid(), service.Id, city.Id, 899m));
        context.SaveChanges();
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Single(i => i.ServiceId == service.Id).Reasons.Should().NotContain(CatalogHealthReason.NoPrice);
    }

    /// <summary>An expired city price row (EffectiveEndDate in the past) does not count as currently active.</summary>
    [Fact]
    public async Task A_service_with_only_an_expired_city_price_row_is_still_flagged_for_missing_price()
    {
        using var context = _db.CreateContext();
        var (_, service, city, _) = SeedActiveService(context);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        context.Add(new ServiceCityPrice(Guid.NewGuid(), service.Id, city.Id, 899m, yesterday.AddDays(-30), yesterday));
        context.SaveChanges();
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Single(i => i.ServiceId == service.Id).Reasons.Should().Contain(CatalogHealthReason.NoPrice);
    }

    [Fact]
    public async Task A_service_with_a_cover_image_url_is_not_flagged_for_missing_image()
    {
        using var context = _db.CreateContext();
        var (_, service, _, _) = SeedActiveService(context, coverImageUrl: "https://cdn.example.com/deep-cleaning.jpg");
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Single(i => i.ServiceId == service.Id).Reasons.Should().NotContain(CatalogHealthReason.NoImage);
    }

    [Fact]
    public async Task A_service_with_an_active_serviceability_mapping_is_not_flagged_for_missing_mapping()
    {
        using var context = _db.CreateContext();
        var (_, service, _, pincode) = SeedActiveService(context);
        context.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SaveChanges();
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Single(i => i.ServiceId == service.Id).Reasons.Should().NotContain(CatalogHealthReason.NoMapping);
    }

    [Fact]
    public async Task A_service_that_has_been_booked_is_not_flagged_as_never_booked()
    {
        using var context = _db.CreateContext();
        var (_, service, _, _) = SeedActiveService(context);
        SeedBookingFor(context, service);
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Single(i => i.ServiceId == service.Id).Reasons.Should().NotContain(CatalogHealthReason.NeverBooked);
    }

    /// <summary>A service passing every check does not appear in the result at all - only failing services are returned.</summary>
    [Fact]
    public async Task A_service_passing_every_check_is_absent_from_the_result()
    {
        using var context = _db.CreateContext();
        var (_, service, city, pincode) = SeedActiveService(context, coverImageUrl: "https://cdn.example.com/deep-cleaning.jpg");
        context.Add(new ServiceCityPrice(Guid.NewGuid(), service.Id, city.Id, 899m));
        context.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SaveChanges();
        SeedBookingFor(context, service);
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Should().NotContain(i => i.ServiceId == service.Id);
    }

    /// <summary>An inactive service is never surfaced, even if it fails every check - only active services are pre-publish candidates.</summary>
    [Fact]
    public async Task An_inactive_service_is_never_surfaced_regardless_of_its_completeness()
    {
        using var context = _db.CreateContext();
        var (_, service, _, _) = SeedActiveService(context);
        var tracked = context.Set<Service>().Single(s => s.Id == service.Id);
        tracked.Deactivate();
        context.SaveChanges();
        var health = BuildService(context);

        var issues = await health.ListHealthIssuesAsync();

        issues.Should().NotContain(i => i.ServiceId == service.Id);
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }
}
