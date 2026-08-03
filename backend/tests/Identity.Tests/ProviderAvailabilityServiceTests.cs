using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application.ProviderAvailability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Provider recurring availability windows and blackout dates (task 149b,
/// PROVIDER.md API surface "Availability").
/// </summary>
public class ProviderAvailabilityServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private Guid _providerId;

    public ProviderAvailabilityServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    private ProviderAvailabilityService CreateService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context));

    [Fact]
    public async Task GetAsync_returns_empty_collections_when_nothing_is_set()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetAsync(_providerId);

        result.Windows.Should().BeEmpty();
        result.BlackoutDates.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateWindowsAsync_replaces_the_whole_weekly_schedule()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);

        await service.UpdateWindowsAsync(_providerId, new UpdateProviderAvailabilityWindowsRequest(
            [new ProviderAvailabilityWindowInput(DayOfWeek.Monday, TimeSpan.FromHours(9), TimeSpan.FromHours(17))]));

        var result = await service.UpdateWindowsAsync(_providerId, new UpdateProviderAvailabilityWindowsRequest(
            [
                new ProviderAvailabilityWindowInput(DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(18)),
                new ProviderAvailabilityWindowInput(DayOfWeek.Wednesday, TimeSpan.FromHours(10), TimeSpan.FromHours(18))
            ]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);

        var stored = await context.Set<ProviderAvailabilityWindow>().Where(w => w.ProviderId == _providerId).ToListAsync();
        stored.Should().HaveCount(2);
        stored.Should().NotContain(w => w.DayOfWeek == DayOfWeek.Monday);
    }

    [Fact]
    public async Task UpdateWindowsAsync_rejects_an_unknown_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).UpdateWindowsAsync(Guid.NewGuid(), new UpdateProviderAvailabilityWindowsRequest(
            [new ProviderAvailabilityWindowInput(DayOfWeek.Monday, TimeSpan.FromHours(9), TimeSpan.FromHours(17))]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderAvailability.NotFound");
    }

    [Fact]
    public async Task AddBlackoutDateAsync_stores_the_blackout_date()
    {
        await using var context = _database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await CreateService(context).AddBlackoutDateAsync(
            _providerId, new AddProviderBlackoutDateRequest(today, today.AddDays(3), "Personal leave"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Reason.Should().Be("Personal leave");

        var stored = await context.Set<ProviderBlackoutDate>().SingleAsync();
        stored.ProviderId.Should().Be(_providerId);
    }

    [Fact]
    public async Task DeleteBlackoutDateAsync_removes_the_blackout_date()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var added = await service.AddBlackoutDateAsync(_providerId, new AddProviderBlackoutDateRequest(today, today, null));

        var result = await service.DeleteBlackoutDateAsync(_providerId, added.Value.Id);

        result.IsSuccess.Should().BeTrue();
        (await context.Set<ProviderBlackoutDate>().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteBlackoutDateAsync_rejects_a_blackout_date_belonging_to_another_provider()
    {
        await using var context = _database.CreateContext();
        var otherProvider = new Provider(Guid.NewGuid(), "Other Provider", "Other's Services", ProviderType.Individual, "+919876500000");
        context.Add(otherProvider);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var added = await service.AddBlackoutDateAsync(otherProvider.Id, new AddProviderBlackoutDateRequest(today, today, null));

        var result = await service.DeleteBlackoutDateAsync(_providerId, added.Value.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderAvailability.BlackoutDateNotFound");
    }

    public void Dispose() => _database.Dispose();
}
