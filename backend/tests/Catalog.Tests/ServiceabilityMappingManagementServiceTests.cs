using FluentAssertions;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 111: admin category/city and service/pincode serviceability mapping (SRS 12.9.2).</summary>
public sealed class ServiceabilityMappingManagementServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ServiceabilityMappingManagementServiceTests(TestDatabase db) => _db = db;

    private (ServiceabilityMappingManagementService MappingService, Category Category, City City, Service Service, Pincode Pincode)
        SeedAndCreateService()
    {
        var context = _db.CreateContext();

        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Cleaning", "deep-cleaning-" + Guid.NewGuid(), "desc", 999m);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560" + Guid.NewGuid().ToString("N")[..3]);

        context.Add(category);
        context.Add(service);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Pincodes.Add(pincode);
        context.SaveChanges();

        var managementService = new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context),
            new ServicePincodeMappingRepository(context),
            new CategoryRepository(context),
            new CityRepository(context),
            new ServiceRepository(context),
            new PincodeRepository(context));

        return (managementService, category, city, service, pincode);
    }

    [Fact]
    public async Task Listing_categories_and_services_returns_the_seeded_active_entries()
    {
        var (service, category, _, catalogService, _) = SeedAndCreateService();

        var categories = await service.ListCategoriesAsync();
        var services = await service.ListServicesAsync();

        categories.Should().Contain(c => c.Id == category.Id && c.Name == "Cleaning");
        services.Should().Contain(s => s.Id == catalogService.Id && s.Name == "Deep Cleaning");
    }

    [Fact]
    public async Task Creating_a_category_city_mapping_then_listing_returns_it_with_names()
    {
        var (service, category, city, _, _) = SeedAndCreateService();

        var created = await service.CreateCategoryCityMappingAsync(new CategoryCityMappingCreateRequest(category.Id, city.Id));

        created.IsSuccess.Should().BeTrue();
        created.Value.CategoryName.Should().Be("Cleaning");
        created.Value.CityName.Should().Be("Bengaluru");
        created.Value.IsActive.Should().BeTrue();

        var list = await service.ListCategoryCityMappingsAsync(category.Id, city.Id);
        list.Should().ContainSingle(m => m.Id == created.Value.Id);
    }

    [Fact]
    public async Task Creating_a_mapping_for_an_unknown_category_returns_not_found()
    {
        var (service, _, city, _, _) = SeedAndCreateService();

        var result = await service.CreateCategoryCityMappingAsync(new CategoryCityMappingCreateRequest(Guid.NewGuid(), city.Id));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Serviceability.CategoryNotFound");
    }

    [Fact]
    public async Task Deactivating_then_recreating_a_mapping_reactivates_it_instead_of_duplicating()
    {
        var (service, category, city, _, _) = SeedAndCreateService();
        var created = (await service.CreateCategoryCityMappingAsync(new CategoryCityMappingCreateRequest(category.Id, city.Id))).Value;

        (await service.DeactivateCategoryCityMappingAsync(created.Id)).IsSuccess.Should().BeTrue();
        var afterDeactivate = await service.ListCategoryCityMappingsAsync(category.Id, city.Id);
        afterDeactivate.Single().IsActive.Should().BeFalse();

        var recreated = await service.CreateCategoryCityMappingAsync(new CategoryCityMappingCreateRequest(category.Id, city.Id));

        recreated.IsSuccess.Should().BeTrue();
        recreated.Value.Id.Should().Be(created.Id);
        recreated.Value.IsActive.Should().BeTrue();

        var afterRecreate = await service.ListCategoryCityMappingsAsync(category.Id, city.Id);
        afterRecreate.Should().ContainSingle();
    }

    [Fact]
    public async Task Deactivating_an_unknown_mapping_returns_not_found()
    {
        var (service, _, _, _, _) = SeedAndCreateService();

        var result = await service.DeactivateCategoryCityMappingAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Serviceability.MappingNotFound");
    }

    [Fact]
    public async Task Creating_a_service_pincode_mapping_then_listing_returns_it_with_names()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();

        var created = await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id));

        created.IsSuccess.Should().BeTrue();
        created.Value.ServiceName.Should().Be("Deep Cleaning");
        created.Value.PincodeCode.Should().Be(pincode.Code);

        var list = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        list.Should().ContainSingle(m => m.Id == created.Value.Id && m.IsActive);
    }

    [Fact]
    public async Task Activating_a_service_pincode_mapping_after_deactivation_restores_serviceability()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();
        var created = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;

        (await service.DeactivateServicePincodeMappingAsync(created.Id)).IsSuccess.Should().BeTrue();
        (await service.ActivateServicePincodeMappingAsync(created.Id)).IsSuccess.Should().BeTrue();

        var list = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        list.Single().IsActive.Should().BeTrue();
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service pincode mapping coverage": the
    /// exact shape the CSV row describes - an active, launched service with
    /// zero pincode mappings - must show up in the warning list so an admin
    /// can catch it before a customer finds "not serviceable" everywhere.
    /// </summary>
    [Fact]
    public async Task ListUnmappedActiveServicesAsync_includes_an_active_service_with_no_pincode_mapping_at_all()
    {
        var (service, category, _, catalogService, _) = SeedAndCreateService();

        var unmapped = await service.ListUnmappedActiveServicesAsync();

        unmapped.Should().Contain(u =>
            u.ServiceId == catalogService.Id && u.ServiceName == "Deep Cleaning" && u.CategoryId == category.Id);
    }

    [Fact]
    public async Task ListUnmappedActiveServicesAsync_excludes_a_service_once_it_has_an_active_pincode_mapping()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var unmapped = await service.ListUnmappedActiveServicesAsync();

        unmapped.Should().NotContain(u => u.ServiceId == catalogService.Id);
    }

    /// <summary>
    /// A service whose only mapping has been suspended (deactivated) is not
    /// currently serviceable anywhere - IsServiceServiceableByPincodeAsync
    /// requires an *active* mapping - so it must reappear in the warning list
    /// exactly as if it had never been mapped.
    /// </summary>
    [Fact]
    public async Task ListUnmappedActiveServicesAsync_includes_a_service_whose_only_mapping_was_deactivated()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.DeactivateServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var unmapped = await service.ListUnmappedActiveServicesAsync();

        unmapped.Should().Contain(u => u.ServiceId == catalogService.Id);
    }
}
