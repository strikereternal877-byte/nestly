using FluentAssertions;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// The provider's own self-service earnings/payouts view (task 149c,
/// PROVIDER.md API surface "Earnings"), wired to the real
/// <c>ProviderEarningLedgerEntry</c>/<c>ProviderPayout</c> entities (task 148)
/// rather than the earlier 501-stub EarningsController.
/// </summary>
public class ProviderEarningsServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _otherProviderId;

    public ProviderEarningsServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        _providerId = provider.Id;
        _otherProviderId = otherProvider.Id;
        context.AddRange(provider, otherProvider);
        context.SaveChanges();
    }

    private ProviderEarningsService CreateService(NestlyDbContext context) => new(
        new ProviderEarningLedgerService(new ProviderRepository(context), new ProviderEarningLedgerRepository(context)),
        new ProviderPayoutService(new ProviderRepository(context), new ProviderPayoutRepository(context), new ProviderEarningLedgerRepository(context)));

    private async Task CreditAsync(NestlyDbContext context, Guid providerId, decimal amount)
    {
        var ledgerService = new ProviderEarningLedgerService(new ProviderRepository(context), new ProviderEarningLedgerRepository(context));
        var result = await ledgerService.RecordAdjustmentAsync(
            providerId, new RecordProviderEarningAdjustmentRequest(ProviderEarningEntryType.Credit, amount, ProviderEarningSourceType.JobCompletion, Guid.NewGuid(), "Job completed."));
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetSummaryAsync_reflects_the_providers_current_balance()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _providerId, 500m);

        var result = await CreateService(context).GetSummaryAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentBalance.Should().Be(500m);
    }

    [Fact]
    public async Task GetLedgerAsync_returns_the_providers_own_entries_only()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _providerId, 300m);
        await CreditAsync(context, _otherProviderId, 900m);

        var result = await CreateService(context).GetLedgerAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].Amount.Should().Be(300m);
    }

    [Fact]
    public async Task ListPayoutsAsync_scopes_the_search_to_the_caller()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _providerId, 1000m);
        var payoutRepository = new ProviderPayoutRepository(context);
        var payoutService = new ProviderPayoutService(new ProviderRepository(context), payoutRepository, new ProviderEarningLedgerRepository(context));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await payoutService.CreateBatchAsync(_providerId, new CreateProviderPayoutRequest(today.AddDays(-7), today))).IsSuccess.Should().BeTrue();

        var result = await CreateService(context).ListPayoutsAsync(_providerId, status: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(p => p.ProviderId == _providerId);
    }

    [Fact]
    public async Task GetPayoutDetailAsync_hides_a_payout_belonging_to_another_provider()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _otherProviderId, 1000m);
        var payoutService = new ProviderPayoutService(new ProviderRepository(context), new ProviderPayoutRepository(context), new ProviderEarningLedgerRepository(context));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await payoutService.CreateBatchAsync(_otherProviderId, new CreateProviderPayoutRequest(today.AddDays(-7), today));
        created.IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPayoutDetailAsync(_providerId, created.Value.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.NotFound");
    }

    [Fact]
    public async Task GetPayoutDetailAsync_returns_the_callers_own_payout()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _providerId, 1000m);
        var payoutService = new ProviderPayoutService(new ProviderRepository(context), new ProviderPayoutRepository(context), new ProviderEarningLedgerRepository(context));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await payoutService.CreateBatchAsync(_providerId, new CreateProviderPayoutRequest(today.AddDays(-7), today));
        created.IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPayoutDetailAsync(_providerId, created.Value.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAmount.Should().Be(1000m);
    }

    public void Dispose() => _database.Dispose();
}
