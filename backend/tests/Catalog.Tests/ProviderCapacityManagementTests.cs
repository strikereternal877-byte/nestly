using FluentAssertions;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 308: the write path for <see cref="ProviderCapacity"/>. Before this,
/// nothing in the codebase could create a row (see
/// <c>ProviderDoubleBookingTests</c>'s "every provider is unlimited today"
/// baseline) even though <c>ProviderAssignmentEligibilityServiceTests</c>
/// already covered enforcement once a row existed.
/// </summary>
public sealed class ProviderCapacityManagementTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderCapacityManagementTests(TestDatabase db) => _db = db;

    private static ProviderManagementService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderKycDocumentRepository(context),
        new ProviderBackgroundCheckRepository(context),
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderEarningLedgerRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderServiceAreaRepository(context),
        new Nestly.Infrastructure.Persistence.Repositories.ProviderSessionRepository(context),
        new Nestly.Infrastructure.Services.ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context)));

    private async Task<Guid> SeedProviderAsync()
    {
        using var context = _db.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        await context.SaveChangesAsync();
        return provider.Id;
    }

    [Fact]
    public async Task GetCapacityAsync_returns_unlimited_when_no_row_exists_yet()
    {
        var providerId = await SeedProviderAsync();

        using var context = _db.CreateContext();
        var result = await BuildService(context).GetCapacityAsync(providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.MaxJobsPerDay.Should().BeNull();
        result.Value.MaxJobsPerSlot.Should().BeNull();
    }

    [Fact]
    public async Task GetCapacityAsync_returns_not_found_for_an_unknown_provider()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).GetCapacityAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Provider.NotFound");
    }

    [Fact]
    public async Task SetCapacityAsync_creates_a_row_and_the_limits_round_trip_through_GetCapacityAsync()
    {
        var providerId = await SeedProviderAsync();

        using (var context = _db.CreateContext())
        {
            var setResult = await BuildService(context).SetCapacityAsync(providerId, new SetProviderCapacityRequest(5, 2));
            setResult.IsSuccess.Should().BeTrue();
            setResult.Value.MaxJobsPerDay.Should().Be(5);
            setResult.Value.MaxJobsPerSlot.Should().Be(2);
        }

        using (var readContext = _db.CreateContext())
        {
            var getResult = await BuildService(readContext).GetCapacityAsync(providerId);
            getResult.Value.MaxJobsPerDay.Should().Be(5);
            getResult.Value.MaxJobsPerSlot.Should().Be(2);
        }
    }

    [Fact]
    public async Task SetCapacityAsync_a_second_time_replaces_the_existing_row_rather_than_conflicting_on_the_unique_index()
    {
        var providerId = await SeedProviderAsync();

        using (var context = _db.CreateContext())
        {
            await BuildService(context).SetCapacityAsync(providerId, new SetProviderCapacityRequest(5, 2));
        }

        using (var context = _db.CreateContext())
        {
            var second = await BuildService(context).SetCapacityAsync(providerId, new SetProviderCapacityRequest(null, 3));
            second.IsSuccess.Should().BeTrue();
            second.Value.MaxJobsPerDay.Should().BeNull();
            second.Value.MaxJobsPerSlot.Should().Be(3);
        }
    }

    [Fact]
    public async Task SetCapacityAsync_rejects_a_zero_or_negative_limit()
    {
        var providerId = await SeedProviderAsync();

        using var context = _db.CreateContext();
        var result = await BuildService(context).SetCapacityAsync(providerId, new SetProviderCapacityRequest(0, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCapacity.InvalidLimits");
    }

    [Fact]
    public async Task SetCapacityAsync_returns_not_found_for_an_unknown_provider()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).SetCapacityAsync(Guid.NewGuid(), new SetProviderCapacityRequest(5, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Provider.NotFound");
    }
}
