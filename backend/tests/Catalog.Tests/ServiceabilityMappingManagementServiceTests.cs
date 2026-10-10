using FluentAssertions;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

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
        // 6 hex chars (not 3) - this suite now has enough test methods
        // sharing one IClassFixture<TestDatabase> that a 3-char suffix
        // (4096 possibilities) collided across methods often enough to be
        // flaky; Pincode.Code allows up to 10 characters, so "560" + 6 stays
        // well within that.
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560" + Guid.NewGuid().ToString("N")[..6]);

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
            new PincodeRepository(context),
            TestServices.AuditLogWriter(context),
            TestServices.SystemSettings(context));

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

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Coverage
    /// gap map" - the third grid category: an active mapping with no active
    /// provider covering it at all must show up as "mapped but not
    /// fulfillable".
    /// </summary>
    [Fact]
    public async Task ListMappedPincodesWithoutProviderCoverageAsync_includes_a_mapping_with_no_covering_provider()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;

        var gaps = await service.ListMappedPincodesWithoutProviderCoverageAsync();

        gaps.Should().Contain(g => g.MappingId == mapping.Id && g.ServiceId == catalogService.Id && g.PincodeId == pincode.Id);
    }

    /// <summary>A mapping actively covered by a qualified provider is not a gap.</summary>
    [Fact]
    public async Task ListMappedPincodesWithoutProviderCoverageAsync_excludes_a_mapping_with_an_active_covering_provider()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;

        var gaps = await service.ListMappedPincodesWithoutProviderCoverageAsync();

        gaps.Should().NotContain(g => g.MappingId == mapping.Id);
    }

    /// <summary>A suspended (deactivated) mapping is not "mapped" at all, so it is excluded even with no coverage.</summary>
    [Fact]
    public async Task ListMappedPincodesWithoutProviderCoverageAsync_excludes_a_deactivated_mapping()
    {
        var (service, _, _, catalogService, pincode) = SeedAndCreateService();
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.DeactivateServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var gaps = await service.ListMappedPincodesWithoutProviderCoverageAsync();

        gaps.Should().NotContain(g => g.MappingId == mapping.Id);
    }

    /// <summary>A suspended provider or a deactivated skill mapping does not count as coverage, mirroring the coverage-gap query.</summary>
    [Fact]
    public async Task ListMappedPincodesWithoutProviderCoverageAsync_treats_an_inactive_provider_or_skill_as_no_coverage()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;

        var suspendedProvider = new Provider(Guid.NewGuid(), "Legal", "Suspended Provider", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        suspendedProvider.ChangeStatus(ProviderStatus.Active);
        suspendedProvider.ChangeStatus(ProviderStatus.Suspended);
        context.Providers.Add(suspendedProvider);
        context.ProviderSkillMappings.Add(new ProviderSkillMapping(Guid.NewGuid(), suspendedProvider.Id, category.Id));
        context.ProviderServiceAreas.Add(new ProviderServiceArea(Guid.NewGuid(), suspendedProvider.Id, city.Id, pincodeId: pincode.Id));
        context.SaveChanges();

        var gaps = await service.ListMappedPincodesWithoutProviderCoverageAsync();

        gaps.Should().Contain(g => g.MappingId == mapping.Id);
    }

    /// <summary>
    /// Backdates a mapping's pending-auto-disable timer past the grace
    /// period, directly in the database - the tests below have no injectable
    /// clock to fast-forward instead (this service uses <c>DateTime.UtcNow</c>
    /// directly, matching this codebase's convention for this kind of
    /// timestamp; see <c>AdminLoginService</c>/<c>AdminUserManagementService</c>
    /// for other examples of that same convention).
    /// </summary>
    private async Task BackdatePendingAutoDisableAsync(Guid mappingId)
    {
        var pastCutoff = DateTime.UtcNow.AddMinutes(-(ServiceabilityAutoManagementDefaults.AutoDisableGracePeriodMinutes + 1));
        using var backdateContext = _db.CreateContext();
        await backdateContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_pincode_mapping SET pending_auto_disable_since = {pastCutoff} WHERE id = {mappingId}");
    }

    /// <summary>
    /// A second <see cref="ServiceabilityMappingManagementService"/> over its
    /// own fresh <see cref="NestlyDbContext"/> - needed after
    /// <see cref="BackdatePendingAutoDisableAsync"/> because EF Core's
    /// identity map would otherwise keep serving the original service's own
    /// already-tracked (pre-backdate) copy of the mapping instead of the row
    /// as it now stands in the database.
    /// </summary>
    private ServiceabilityMappingManagementService CreateServiceOverFreshContext()
    {
        var context = _db.CreateContext();
        return new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context),
            new ServicePincodeMappingRepository(context),
            new CategoryRepository(context),
            new CityRepository(context),
            new ServiceRepository(context),
            new PincodeRepository(context),
            TestServices.AuditLogWriter(context),
            TestServices.SystemSettings(context));
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

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - follow-up
    /// review ("I think auto deactivating is required as well"). The reverse
    /// of auto-enable: a provider who was the ONLY active coverage for a
    /// (service, pincode) pair loses their skill, and the mapping - now
    /// unserved by anyone - is deactivated automatically.
    /// </summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_deactivates_a_mapping_that_loses_its_sole_coverage()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        // Snapshot BEFORE the coverage change, exactly as
        // ProviderProfileService.UpdateSkillsAsync does before its replace -
        // see ListMappedPairsCoveredByProviderAsync's doc comment for why.
        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);
        previouslyCovered.Should().Contain(p => p.ServiceId == catalogService.Id && p.PincodeId == pincode.Id);

        // Coverage lost: the provider's only skill row is deactivated.
        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        // First call only starts the grace-period timer - see
        // AutoDisableUnservedMappingsAsync_waits_for_the_grace_period_before_disabling
        // for that behaviour in isolation. This test is about the eventual
        // outcome, so it fast-forwards past the grace period.
        (await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered)).Should().Be(0);
        var mappingId = (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().Id;

        await BackdatePendingAutoDisableAsync(mappingId);
        var disabledCount = await CreateServiceOverFreshContext().AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered);

        disabledCount.Should().Be(1);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Single().IsActive.Should().BeFalse();
    }

    /// <summary>Another active provider still covering the pair means the mapping must stay active.</summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_leaves_the_mapping_active_when_another_provider_still_covers_it()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);

        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        var disabledCount = await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered);

        disabledCount.Should().Be(0);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Single().IsActive.Should().BeTrue();
    }

    /// <summary>
    /// A provider being suspended entirely (their skill/area rows untouched,
    /// only their status flips) is the no-explicit-snapshot call shape used
    /// by ProviderManagementService.SuspendAsync/DeleteAsync - it must
    /// compute the "before" picture itself from current coverage.
    /// </summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_deactivates_a_mapping_when_the_sole_covering_provider_is_suspended()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var suspended = context.Set<Provider>().Single(p => p.Id == provider.Id);
        suspended.ChangeStatus(ProviderStatus.Suspended);
        await context.SaveChangesAsync();

        (await service.AutoDisableUnservedMappingsAsync(provider.Id)).Should().Be(0, "the grace period has not elapsed yet");
        var mappingId = (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().Id;

        await BackdatePendingAutoDisableAsync(mappingId);
        var disabledCount = await CreateServiceOverFreshContext().AutoDisableUnservedMappingsAsync(provider.Id);

        disabledCount.Should().Be(1);
        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Single().IsActive.Should().BeFalse();
    }

    /// <summary>Idempotent: a second call after the mapping is already deactivated finds nothing left to disable, and does not error.</summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_is_idempotent_across_repeated_calls()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id)))
            .IsSuccess.Should().BeTrue();

        var suspended = context.Set<Provider>().Single(p => p.Id == provider.Id);
        suspended.ChangeStatus(ProviderStatus.Suspended);
        await context.SaveChangesAsync();

        (await service.AutoDisableUnservedMappingsAsync(provider.Id)).Should().Be(0, "the grace period has not elapsed yet");
        var mappingId = (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().Id;
        await BackdatePendingAutoDisableAsync(mappingId);

        var freshService = CreateServiceOverFreshContext();
        (await freshService.AutoDisableUnservedMappingsAsync(provider.Id)).Should().Be(1);
        (await freshService.AutoDisableUnservedMappingsAsync(provider.Id)).Should().Be(0);

        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Single().IsActive.Should().BeFalse();
    }

    /// <summary>A provider with no propped-up mappings (e.g. never had coverage) disables nothing.</summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_does_nothing_for_a_provider_with_no_mapped_coverage()
    {
        var (service, _, _, _, _) = SeedAndCreateService();

        var disabledCount = await service.AutoDisableUnservedMappingsAsync(Guid.NewGuid());

        disabledCount.Should().Be(0);
    }

    // ---- Auto-management safety net: audit trail, pin, flap-protection cooldown, auto-disable grace period, kill switch ----

    /// <summary>A real auto-enable writes a System-attributed audit entry naming the mapping and the triggering provider.</summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_writes_a_system_attributed_audit_entry_on_a_real_toggle()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);

        (await service.AutoEnableProviderCoverageAsync(provider.Id)).Should().Be(1);

        var mapping = (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single();

        using var auditContext = _db.CreateContext();
        var audit = auditContext.Set<AuditLog>().Single(a => a.EntityName == "ServicePincodeMapping" && a.EntityId == mapping.Id.ToString());
        audit.Action.Should().Be("AutoEnabled");
        audit.ActorType.Should().Be(AuditActorType.System);
        audit.ActorId.Should().BeNull();
        audit.NewValues.Should().Contain(provider.Id.ToString());
    }

    /// <summary>Pinning a mapping's active state means auto-enable never touches it, even when it is genuinely coverable.</summary>
    [Fact]
    public async Task AutoEnableProviderCoverageAsync_skips_a_pinned_mapping()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.DeactivateServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();
        (await service.PinServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var enabledCount = await service.AutoEnableProviderCoverageAsync(provider.Id);

        enabledCount.Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeFalse();
    }

    /// <summary>Pinning a mapping's active state means auto-disable never touches it, even when coverage is genuinely lost.</summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_skips_a_pinned_mapping()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        (await service.PinServicePincodeMappingAsync(mapping.Id)).IsSuccess.Should().BeTrue();

        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);
        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        var disabledCount = await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered);

        disabledCount.Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeTrue();

        using var readContext = _db.CreateContext();
        readContext.Set<ServicePincodeMapping>().Single(m => m.Id == mapping.Id).PendingAutoDisableSince.Should().BeNull();
        readContext.Set<AuditLog>().Where(a => a.EntityId == mapping.Id.ToString()).Should().BeEmpty();
    }

    /// <summary>
    /// Flap protection: an auto-enable followed almost immediately by a
    /// coverage loss, followed almost immediately by coverage returning -
    /// two rapid loss-then-gain cycles within the cooldown window - toggles
    /// the mapping exactly once (the original auto-enable), not twice.
    /// </summary>
    [Fact]
    public async Task Rapid_coverage_flapping_within_the_cooldown_window_toggles_the_mapping_only_once()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);

        // Cycle 0: coverage gained - the one real toggle this test expects.
        (await service.AutoEnableProviderCoverageAsync(provider.Id)).Should().Be(1);

        // Cycle 1: coverage lost, then gained again, both well within
        // ServiceabilityAutoManagementDefaults.AutoToggleCooldownMinutes of
        // the toggle above.
        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);
        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        (await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered)).Should().Be(0, "the cooldown must suppress the disable");

        skill.Activate();
        context.SaveChanges();

        (await service.AutoEnableProviderCoverageAsync(provider.Id)).Should().Be(0, "the mapping never actually left the active state");

        var mappings = await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id);
        mappings.Single().IsActive.Should().BeTrue();

        var mappingId = mappings.Single().Id;

        using var auditContext = _db.CreateContext();
        var auditActions = auditContext.Set<AuditLog>()
            .Where(a => a.EntityName == "ServicePincodeMapping" && a.EntityId == mappingId.ToString())
            .Select(a => a.Action)
            .ToList();
        auditActions.Should().ContainSingle().Which.Should().Be("AutoEnabled");
    }

    /// <summary>Auto-disable does not fire the instant coverage is lost - it waits for the grace period, then disables once it has actually elapsed.</summary>
    [Fact]
    public async Task AutoDisableUnservedMappingsAsync_waits_for_the_grace_period_before_disabling()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;

        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);
        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        // First observation of lost coverage: only starts the grace-period timer.
        (await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered)).Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeTrue();

        using (var checkContext = _db.CreateContext())
        {
            checkContext.Set<ServicePincodeMapping>().Single(m => m.Id == mapping.Id).PendingAutoDisableSince.Should().NotBeNull();
        }

        // A second call while still within the grace period changes nothing.
        (await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered)).Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeTrue();

        // Simulate the grace period having fully elapsed by backdating the
        // pending timer directly in the database - there is no injectable
        // clock on this service (it uses DateTime.UtcNow, matching this
        // codebase's convention for this kind of timestamp), so this is the
        // most direct way to exercise "after the grace period" without an
        // actual 30-minute wait. A fresh service/context is required to
        // observe it: EF Core's identity map would otherwise keep serving
        // `service`'s own already-tracked (recent) copy of this mapping
        // instead of the backdated row.
        await BackdatePendingAutoDisableAsync(mapping.Id);
        var disabledCount = await CreateServiceOverFreshContext().AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered);

        disabledCount.Should().Be(1);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeFalse();

        using var auditContext = _db.CreateContext();
        var audit = auditContext.Set<AuditLog>().Single(a =>
            a.EntityName == "ServicePincodeMapping" && a.EntityId == mapping.Id.ToString() && a.Action == "AutoDisabled");
        audit.ActorType.Should().Be(AuditActorType.System);
        audit.NewValues.Should().Contain(provider.Id.ToString());
    }

    /// <summary>The admin kill switch (FeatureFlagSettings.AutoManageServiceabilityEnabled = false) makes both methods no-op entirely, with no audit trail.</summary>
    [Fact]
    public async Task Both_auto_enable_and_auto_disable_are_no_ops_when_the_kill_switch_is_off()
    {
        var (service, category, city, catalogService, pincode) = SeedAndCreateService();
        var context = _db.CreateContext();
        SeedKillSwitchOff(context);

        var provider = SeedActiveProviderCoveringPincode(context, category.Id, city.Id, pincode.Id);
        (await service.AutoEnableProviderCoverageAsync(provider.Id)).Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Should().BeEmpty();

        var mapping = (await service.CreateServicePincodeMappingAsync(new ServicePincodeMappingCreateRequest(catalogService.Id, pincode.Id))).Value;
        var previouslyCovered = await service.ListMappedPairsCoveredByProviderAsync(provider.Id);
        var skill = context.Set<ProviderSkillMapping>().Single(s => s.ProviderId == provider.Id);
        skill.Deactivate();
        context.SaveChanges();

        (await service.AutoDisableUnservedMappingsAsync(provider.Id, previouslyCovered)).Should().Be(0);
        (await service.ListServicePincodeMappingsAsync(catalogService.Id, pincode.Id)).Single().IsActive.Should().BeTrue();

        using var readContext = _db.CreateContext();
        readContext.Set<ServicePincodeMapping>().Single(m => m.Id == mapping.Id).PendingAutoDisableSince.Should().BeNull();
        readContext.Set<AuditLog>().Where(a => a.EntityId == mapping.Id.ToString()).Should().BeEmpty();

        // Restore the default (no "features" row -> fails open as enabled)
        // so later tests in this class - which share one TestDatabase fixture
        // - are not left running with the kill switch permanently off.
        using var cleanupContext = _db.CreateContext();
        cleanupContext.Remove(cleanupContext.Set<SystemSetting>().Single(s => s.GroupKey == SystemSettingGroups.Feature));
        cleanupContext.SaveChanges();
    }

    /// <summary>Seeds the "features" settings group with the kill switch off - every other flag value is irrelevant to these tests.</summary>
    private static void SeedKillSwitchOff(NestlyDbContext context)
    {
        context.Add(new SystemSetting(
            Guid.NewGuid(),
            SystemSettingGroups.Feature,
            "{\"walletEnabled\":true,\"referralsEnabled\":true,\"amcSubscriptionsEnabled\":true,\"serviceRatingsEnabled\":true,\"bookingHelpLinkEnabled\":true,\"ratingsPageEnabled\":true,\"calendarViewEnabled\":true,\"earningsLedgerEnabled\":true,\"offersScreenEnabled\":true,\"autoManageServiceabilityEnabled\":false}"));
        context.SaveChanges();
    }
}
