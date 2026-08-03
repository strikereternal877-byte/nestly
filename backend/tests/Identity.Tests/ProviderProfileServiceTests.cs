using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
        new(new ProviderRepository(context), new ProviderServiceAreaRepository(context), new ProviderSkillMappingRepository(context));

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

    [Fact]
    public async Task GetServiceAreasAsync_returns_an_empty_list_when_none_are_set()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetServiceAreasAsync(_providerId);

        result.Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();
}
