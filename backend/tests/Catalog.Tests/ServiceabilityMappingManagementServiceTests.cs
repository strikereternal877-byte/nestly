using FluentAssertions;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
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

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Serviceability and provider skills ...
    /// Service to pincode mapping": an active provider with a matching skill
    /// and area covering the pincode, but no serviceability mapping at all,
    /// must show up as a coverage gap.
    /// </summary>
    [Fact]
    public async Task ListPincodesWithProviderCoverageButNoServiceMappingAsync_includes_a_pincode_with_provider_coverage_and_no_mapping()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);

        var gaps = await service.ListPincodesWithProviderCoverageButNoServiceMappingAsync();

        gaps.Should().Contain(g => g.ServiceId == catalogService.Id && g.PincodeId == pincode.Id);
    }

    /// <summary>A pincode that already has an active serviceability mapping is not a gap, even with provider coverage.</summary>
    [Fact]
    public async Task ListPincodesWithProviderCoverageButNoServiceMappingAsync_excludes_a_pincode_with_an_active_mapping()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var gaps = await service.ListPincodesWithProviderCoverageButNoServiceMappingAsync();

        gaps.Should().NotContain(g => g.ServiceId == catalogService.Id && g.PincodeId == pincode.Id);
    }

    /// <summary>An inactive (suspended) provider or a deactivated skill mapping does not count as coverage.</summary>
    [Fact]
    public async Task ListPincodesWithProviderCoverageButNoServiceMappingAsync_excludes_an_inactive_provider_or_skill()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();

        var suspendedProvider = new Provider(Guid.NewGuid(), "Legal", "Suspended Provider", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        suspendedProvider.ChangeStatus(ProviderStatus.Active);
        suspendedProvider.ChangeStatus(ProviderStatus.Suspended);
        context.Providers.Add(suspendedProvider);
        context.ProviderSkillMappings.Add(new ProviderSkillMapping(Guid.NewGuid(), suspendedProvider.Id, category.Id));
        context.ProviderServiceAreas.Add(new ProviderServiceArea(Guid.NewGuid(), suspendedProvider.Id, city.Id, pincodeId: pincode.Id));

        var inactiveSkillProvider = new Provider(Guid.NewGuid(), "Legal", "Inactive Skill Provider", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        inactiveSkillProvider.ChangeStatus(ProviderStatus.Active);
        context.Providers.Add(inactiveSkillProvider);
        var deactivatedSkill = new ProviderSkillMapping(Guid.NewGuid(), inactiveSkillProvider.Id, category.Id);
        deactivatedSkill.Deactivate();
        context.ProviderSkillMappings.Add(deactivatedSkill);
        context.ProviderServiceAreas.Add(new ProviderServiceArea(Guid.NewGuid(), inactiveSkillProvider.Id, city.Id, pincodeId: pincode.Id));
        context.SaveChanges();

        var gaps = await service.ListPincodesWithProviderCoverageButNoServiceMappingAsync();

        gaps.Should().NotContain(g => g.ServiceId == catalogService.Id && g.PincodeId == pincode.Id);
    }

    /// <summary>A mapping whose only row was deactivated still counts as a gap, mirroring <see cref="ListUnmappedActiveServicesAsync_includes_a_service_whose_only_mapping_was_deactivated"/>.</summary>
    [Fact]
    public async Task ListPincodesWithProviderCoverageButNoServiceMappingAsync_includes_a_pincode_whose_only_mapping_was_deactivated()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.DeactivateServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var gaps = await service.ListPincodesWithProviderCoverageButNoServiceMappingAsync();

        gaps.Should().Contain(g => g.ServiceId == catalogService.Id && g.PincodeId == pincode.Id);
    }

    private static Provider SeedActiveProviderCoveringPincode(NestlyDbContext context, Guid categoryId, Guid cityId, Guid pincodeId)
    {
        var provider = new Provider(Guid.NewGuid(), "Legal", "Covering Provider", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Providers.Add(provider);
        context.ProviderSkillMappings.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, categoryId));
        context.ProviderServiceAreas.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, cityId, pincodeId: pincodeId));
        context.SaveChanges();
        return provider;
    }

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - upgraded
    /// from warning-only (7f8ec29) to auto-enable: a provider newly gaining
    /// skill + area coverage for a (service, pincode) pair must get the
    /// ServicePincodeMapping created automatically, not just flagged.
    /// </summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_creates_the_mapping_for_newly_covered_service_and_pincode()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);

        var enabledCount = await service.AutoEnableProviderCoverageAsync(provider.Id);

        enabledCount.Should().Be(1);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Should().ContainSingle(m => m.IsActive);
    }

    /// <summary>Idempotent: coverage that is already actively mapped must not be duplicated.</summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_is_a_no_op_when_the_mapping_already_exists()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var enabledCount = await service.AutoEnableProviderCoverageAsync(provider.Id);

        enabledCount.Should().Be(0);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Should().ContainSingle();

        // Running it again for the same provider (e.g. a second skill/area
        // save that adds nothing new) must stay a no-op too.
        (await service.AutoEnableProviderCoverageAsync(provider.Id)).Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Should().ContainSingle();
    }

    /// <summary>
    /// A mapping an admin had suspended reactivates once coverage returns -
    /// same reactivate-if-suspended path <see cref="CreateServicePincodeMappingAsync"/>
    /// already uses, reused here rather than hand-rolled.
    /// </summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_reactivates_a_previously_deactivated_mapping()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.DeactivateServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var enabledCount = await service.AutoEnableProviderCoverageAsync(provider.Id);

        enabledCount.Should().Be(1);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Should().ContainSingle(m => m.Id == mapping.Id && m.IsActive);
    }

    /// <summary>A provider with no coverage yet (e.g. inactive, or no skill/area rows) enables nothing.</summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_does_nothing_for_a_provider_with_no_coverage()
    {
        var (service, _, _, _, _) = SeedAndCreateService();

        var enabledCount = await service.AutoEnableProviderCoverageAsync(Guid.NewGuid());

        enabledCount.Should().Be(0);
    }
}
