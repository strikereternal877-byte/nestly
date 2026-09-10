using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.ProviderProfile;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Provider profile editing plus service-area/skill replace-all management
/// (task 149a, PROVIDER.md API surface "Profile/Onboarding").
/// </summary>
public class ProviderProfileServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private Guid _providerId;
    private Guid _cityId;
    private Guid _secondCityId;
    private Guid _categoryId;

    public ProviderProfileServiceTests()
    {
        using var context = _database.CreateContext();

        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        _providerId = provider.Id;
        context.Add(provider);

        var state = new State(Guid.NewGuid(), "Karnataka", "KA");
        context.Add(state);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        _cityId = city.Id;
        context.Add(city);
        var secondCity = new City(Guid.NewGuid(), state.Id, "Mysuru");
        _secondCityId = secondCity.Id;
        context.Add(secondCity);

        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning", "Home cleaning services");
        _categoryId = category.Id;
        context.Add(category);

        context.SaveChanges();
    }

    private ProviderProfileService CreateService(NestlyDbContext context) =>
        new(new ProviderRepository(context), new ProviderServiceAreaRepository(context), new ProviderSkillMappingRepository(context),
            new ReviewRepository(context), new ProviderSessionRepository(context), CreateServiceabilityMappingManagementService(context));

    private static ServiceabilityMappingManagementService CreateServiceabilityMappingManagementService(NestlyDbContext context) =>
        new(new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context));

    [Fact]
    public async Task GetAsync_returns_the_provider_profile()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.DisplayName.Should().Be("Ravi's Repairs");
        result.Value.Status.Should().Be(nameof(ProviderStatus.PendingVerification));
    }

    [Fact]
    public async Task GetAsync_has_no_rating_when_the_provider_has_no_visible_reviews_yet()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().BeNull("a provider with zero visible reviews has no rating - distinct from a rating of zero");
        result.Value.ReviewCount.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_returns_the_average_and_count_of_only_visible_provider_scoped_reviews()
    {
        await using var context = _database.CreateContext();

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var service = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(customer);
        context.Add(service);

        // Review.BookingId is uniquely indexed (one primary review per
        // booking), so each review needs its own booking - mirrors
        // ProviderPhotoAndRatingTests' fixture shape exactly.
        Booking NewBooking() => new(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot(customer.Name, customer.Mobile),
            null,
            new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.97m, 77.59m, customer.Name, "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m));

        var bookingA = NewBooking();
        var bookingB = NewBooking();
        var bookingC = NewBooking();
        context.Add(bookingA);
        context.Add(bookingB);
        context.Add(bookingC);

        context.Add(new Review(Guid.NewGuid(), bookingA.Id, customer.Id, service.Id, _providerId, 5, null));
        context.Add(new Review(Guid.NewGuid(), bookingB.Id, customer.Id, service.Id, _providerId, 3, null));
        // Not provider-scoped (task 293's backfill population) - must not count toward this provider's rating.
        context.Add(new Review(Guid.NewGuid(), bookingC.Id, customer.Id, service.Id, null, 1, null));
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().Be(4.0);
        result.Value.ReviewCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_rejects_an_unknown_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderProfile.NotFound");
    }

    [Fact]
    public async Task UpdateAsync_persists_the_new_values_and_advances_onboarding()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).UpdateAsync(
            _providerId, new UpdateProviderProfileRequest("Ravi Kumar S", "Ravi's Home Repairs", "ravi@example.com"));

        result.IsSuccess.Should().BeTrue();
        result.Value.DisplayName.Should().Be("Ravi's Home Repairs");

        var stored = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        stored.Email.Should().Be("ravi@example.com");
        stored.OnboardingStatus.Should().Be(ProviderOnboardingStatus.ProfileCompleted);
    }

    [Fact]
    public async Task UpdateServiceAreasAsync_replaces_the_whole_coverage_set()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);

        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, null)]));

        var result = await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, null), new ProviderServiceAreaInput(_secondCityId, null, null)]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);

        var stored = await context.Set<ProviderServiceArea>().Where(a => a.ProviderId == _providerId).ToListAsync();
        stored.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateServiceAreasAsync_rejects_an_unknown_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).UpdateServiceAreasAsync(
            Guid.NewGuid(), new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, null)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderProfile.NotFound");
    }

    [Fact]
    public async Task UpdateSkillsAsync_replaces_the_whole_skill_set()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);

        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, null)]));
        var result = await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();

        var stored = await context.Set<ProviderSkillMapping>().Where(s => s.ProviderId == _providerId).ToListAsync();
        stored.Should().BeEmpty();
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - auto-enable
    /// upgrade from the warning-only fix (7f8ec29): once an Active provider
    /// has both a matching skill and an area covering a pincode, the
    /// ServicePincodeMapping for that service/pincode must be created
    /// automatically rather than left for an admin to notice and map by hand.
    /// Order doesn't matter for coverage, but the trigger only fires on the
    /// second call here (skill alone isn't fulfillable without an area too) -
    /// exercising that the area-save path is the one that completes coverage.
    /// </summary>
    [Fact]
    public async Task UpdateServiceAreasAsync_auto_enables_the_service_pincode_mapping_once_skill_and_area_coverage_meet()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        var result = await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>()
            .SingleOrDefaultAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mapping.Should().NotBeNull();
        mapping!.IsActive.Should().BeTrue();
    }

    /// <summary>Re-saving the same skills/areas a second time must not create a duplicate mapping.</summary>
    [Fact]
    public async Task UpdateSkillsAsync_auto_enable_is_idempotent_across_repeated_saves()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        // Saving the identical skill set again should not create a second mapping row.
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        var mappings = await context.Set<ServicePincodeMapping>()
            .Where(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id).ToListAsync();
        mappings.Should().ContainSingle();
        mappings.Single().IsActive.Should().BeTrue();
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - follow-up
    /// review ("I think auto deactivating is required as well"). This
    /// provider is the sole coverage for the pincode; dropping their skill
    /// (via a skills replace-all that no longer includes it) must
    /// auto-deactivate the mapping it was propping up.
    /// </summary>
    [Fact]
    public async Task UpdateSkillsAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        var mappingBeforeDrop = await context.Set<ServicePincodeMapping>()
            .SingleAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mappingBeforeDrop.IsActive.Should().BeTrue();

        // Skills replace-all with an empty set - the provider no longer has
        // any skill covering this service.
        var result = await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([]));

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>()
            .SingleAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mapping.IsActive.Should().BeFalse();
    }

    /// <summary>Same auto-disable, triggered from the area side instead of the skill side.</summary>
    [Fact]
    public async Task UpdateServiceAreasAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));
        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));

        // Areas replace-all that drops the pincode entirely.
        await service.UpdateServiceAreasAsync(_providerId, new UpdateProviderServiceAreasRequest([]));

        var mapping = await context.Set<ServicePincodeMapping>()
            .SingleAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mapping.IsActive.Should().BeFalse();
    }

    /// <summary>A second active provider still covering the pincode means the mapping must stay bookable.</summary>
    [Fact]
    public async Task UpdateSkillsAsync_does_not_auto_disable_a_mapping_another_provider_still_covers()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);

        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        otherProvider.ChangeStatus(ProviderStatus.Active);
        context.Add(otherProvider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), otherProvider.Id, _categoryId, catalogService.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), otherProvider.Id, _cityId, pincodeId: pincode.Id));
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([]));

        var mapping = await context.Set<ServicePincodeMapping>()
            .SingleAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mapping.IsActive.Should().BeTrue("the other provider's own skill + area still cover this service/pincode");
    }

    /// <summary>Idempotent: re-saving the already-empty skill set must not error or re-toggle anything.</summary>
    [Fact]
    public async Task UpdateSkillsAsync_auto_disable_is_idempotent_across_repeated_saves()
    {
        await using var context = _database.CreateContext();
        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.ChangeStatus(ProviderStatus.Active);
        var pincode = new Pincode(Guid.NewGuid(), _cityId, "560" + Guid.NewGuid().ToString("N")[..3]);
        var catalogService = new Service(Guid.NewGuid(), _categoryId, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        context.Add(pincode);
        context.Add(catalogService);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateServiceAreasAsync(_providerId,
            new UpdateProviderServiceAreasRequest([new ProviderServiceAreaInput(_cityId, null, pincode.Id)]));
        await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([new ProviderSkillInput(_categoryId, catalogService.Id)]));

        var first = await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([]));
        var second = await service.UpdateSkillsAsync(_providerId, new UpdateProviderSkillsRequest([]));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>()
            .SingleAsync(m => m.ServiceId == catalogService.Id && m.PincodeId == pincode.Id);
        mapping.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetServiceAreasAsync_returns_an_empty_list_when_none_are_set()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetServiceAreasAsync(_providerId);

        result.Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();
}
