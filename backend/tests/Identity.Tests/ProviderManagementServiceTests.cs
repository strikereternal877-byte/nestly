using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - follow-up
/// review ("I think auto deactivating is required as well"): admin
/// suspend/delete on <see cref="ProviderManagementService"/> is one of the
/// three trigger points that must call
/// <c>IServiceabilityMappingManagementService.AutoDisableUnservedMappingsAsync</c>
/// (mirroring <c>ReactivateAsync</c>'s existing auto-enable call, task
/// a6866b4). No test file previously exercised
/// SuspendAsync/ReactivateAsync/DeleteAsync at all.
/// </summary>
public sealed class ProviderManagementServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private static ProviderManagementService CreateService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderKycDocumentRepository(context),
        new ProviderBackgroundCheckRepository(context),
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderEarningLedgerRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderServiceAreaRepository(context),
        new ProviderSessionRepository(context),
        new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context)),
        new ProviderAvailabilityWindowRepository(context),
        new ReviewRepository(context));

    /// <summary>Seeds an Active provider with skill + area coverage for one (service, pincode) pair, and the resulting active mapping.</summary>
    private async Task<(Guid ProviderId, Guid ServiceId, Guid PincodeId)> SeedSoleCoverageAsync(NestlyDbContext context)
    {
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560" + Guid.NewGuid().ToString("N")[..3]);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var catalogService = new Service(Guid.NewGuid(), category.Id, "Deep Cleaning", "deep-cleaning-" + Guid.NewGuid(), "desc", 999m);
        var mapping = new ServicePincodeMapping(Guid.NewGuid(), catalogService.Id, pincode.Id);

        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        provider.ChangeStatus(ProviderStatus.Active);

        context.AddRange(state, city, pincode, category, catalogService, mapping, provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, category.Id, catalogService.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, city.Id, pincodeId: pincode.Id));
        await context.SaveChangesAsync();

        return (provider.Id, catalogService.Id, pincode.Id);
    }

    /// <summary>
    /// Row 31, docs/OPEN-FIXES-FEATURES.csv: an admin-created provider must
    /// be immediately matchable too, not just a self-registered one.
    /// </summary>
    [Fact]
    public async Task CreateAsync_seeds_a_non_empty_default_weekly_availability()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).CreateAsync(
            new CreateProviderRequest("Ravi Kumar", "Ravi's Repairs", "+9198" + Guid.NewGuid().ToString("N")[..8], "ravi@example.com"));

        result.IsSuccess.Should().BeTrue();
        var windows = await new ProviderAvailabilityWindowRepository(context).GetByProviderAsync(result.Value.Id);
        windows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SuspendAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);

        var result = await CreateService(context).SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"));

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);

        var result = await CreateService(context).DeleteAsync(providerId);

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task SuspendAsync_leaves_the_mapping_active_when_another_provider_still_covers_it()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);
        var service = await context.Set<Service>().SingleAsync(s => s.Id == serviceId);
        var pincode = await context.Set<Pincode>().SingleAsync(p => p.Id == pincodeId);

        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        otherProvider.ChangeStatus(ProviderStatus.Active);
        context.Add(otherProvider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), otherProvider.Id, service.CategoryId, service.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), otherProvider.Id, pincode.CityId, pincodeId: pincode.Id));
        await context.SaveChangesAsync();

        var result = await CreateService(context).SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"));

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeTrue("the other active provider's coverage still makes the pair bookable");
    }

    /// <summary>Idempotent: suspending an already-suspended trigger path a second time (e.g. via ReactivateAsync then SuspendAsync again) must not error or double-toggle.</summary>
    [Fact]
    public async Task SuspendAsync_then_reactivating_and_suspending_again_stays_idempotent()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);
        var management = CreateService(context);

        (await management.SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"))).IsSuccess.Should().BeTrue();
        (await management.ReactivateAsync(providerId)).IsSuccess.Should().BeTrue();
        var afterReactivate = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        afterReactivate.IsActive.Should().BeTrue("ReactivateAsync's auto-enable restores the mapping once the provider's coverage counts again");

        (await management.SuspendAsync(providerId, new SuspendProviderRequest("Policy violation again"))).IsSuccess.Should().BeTrue();

        var final = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        final.IsActive.Should().BeFalse();
    }

    public void Dispose() => _database.Dispose();
}
