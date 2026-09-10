using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

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
        BuildLedgerService(context),
        BuildPayoutService(context));

    private static ProviderEarningLedgerService BuildLedgerService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderEarningLedgerRepository(context),
        new BookingRepository(context),
        new PaymentTransactionRepository(context),
        new ProviderPayoutRepository(context));

    private static ProviderPayoutService BuildPayoutService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderPayoutRepository(context),
        new ProviderEarningLedgerRepository(context),
        new AuditLogWriter(context, new StubAuditContextProvider()));

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private async Task CreditAsync(NestlyDbContext context, Guid providerId, decimal amount)
    {
        var ledgerService = BuildLedgerService(context);
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
        var payoutService = BuildPayoutService(context);
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
        var payoutService = BuildPayoutService(context);
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
        var payoutService = BuildPayoutService(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await payoutService.CreateBatchAsync(_providerId, new CreateProviderPayoutRequest(today.AddDays(-7), today));
        created.IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPayoutDetailAsync(_providerId, created.Value.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAmount.Should().Be(1000m);
    }

    /// <summary>
    /// Task 251/252: neither payout list endpoint validated its query string -
    /// admin PayoutsController.Search and provider EarningsController.ListPayouts
    /// both hand raw page/pageSize to this service. An unbounded pageSize let a
    /// single request materialize the whole payout table, and a page below 1
    /// reached PostgreSQL as a negative OFFSET (a hard error there, though
    /// in-memory SQLite tolerates it - hence asserting on the echoed values).
    /// </summary>
    [Theory]
    [InlineData(101)]
    [InlineData(10_000)]
    [InlineData(int.MaxValue)]
    public async Task ListPayoutsAsync_caps_an_oversized_page_size(int requestedPageSize)
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).ListPayoutsAsync(_providerId, status: null, page: 1, pageSize: requestedPageSize);

        result.IsSuccess.Should().BeTrue();
        result.Value.PageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ListPayoutsAsync_normalizes_a_non_positive_page_to_the_first_page(int requestedPage)
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).ListPayoutsAsync(_providerId, status: null, page: requestedPage, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListPayoutsAsync_substitutes_the_default_for_a_non_positive_page_size()
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).ListPayoutsAsync(_providerId, status: null, page: 1, pageSize: 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task ListPayoutsAsync_survives_a_page_number_that_used_to_overflow_the_offset()
    {
        await using var context = _database.CreateContext();
        await CreditAsync(context, _providerId, 1000m);
        var payoutService = BuildPayoutService(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await payoutService.CreateBatchAsync(_providerId, new CreateProviderPayoutRequest(today.AddDays(-7), today))).IsSuccess.Should().BeTrue();

        // (page - 1) * pageSize wrapped negative here before task 261.
        var result = await CreateService(context).ListPayoutsAsync(_providerId, status: null, page: 2_000_000_000, pageSize: 100);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty("a page that far past the end is empty, not an error");
        result.Value.TotalCount.Should().Be(1);
    }

    /// <summary>
    /// Seeds a completed, commission-recorded booking (mirrors
    /// <c>ProviderJobServiceTests.SeedPaidCommissionAsync</c>'s payment-side
    /// setup) plus the <see cref="ProviderEarningSourceType.JobCompletion"/>
    /// ledger credit <see cref="EscrowReleaseOnCompletionHandler"/> records
    /// for it, so <see cref="ProviderEarningLedgerService.GetJobEarningsAsync"/>
    /// has something real to read. Returns the booking id and the net amount
    /// credited (gross - commission), the same figure the handler passes on.
    /// </summary>
    private async Task<(Guid BookingId, decimal NetAmount)> SeedCompletedJobEarningAsync(
        NestlyDbContext context, Guid providerId, DateOnly slotDate, decimal grossAmount = 1000m, decimal commissionAmount = 150m)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), slotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(grossAmount, 1, grossAmount, 0m, 0m, grossAmount, 0m, 0m, 0m, grossAmount));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", grossAmount, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);

        var transaction = new PaymentTransaction(Guid.NewGuid(), booking.Id, customer.Id, grossAmount, "INR", Guid.NewGuid().ToString());
        var attempt = transaction.StartAttempt(Guid.NewGuid(), "order_" + Guid.NewGuid());
        transaction.MarkAttemptSucceeded(attempt.Id, "pay_" + Guid.NewGuid());
        transaction.RecordCommission(15m, commissionAmount);

        context.AddRange(customer, booking, transaction);
        await context.SaveChangesAsync();

        decimal netAmount = grossAmount - commissionAmount;
        var ledgerService = BuildLedgerService(context);
        var credit = await ledgerService.RecordAdjustmentAsync(
            providerId,
            new RecordProviderEarningAdjustmentRequest(
                ProviderEarningEntryType.Credit, netAmount, ProviderEarningSourceType.JobCompletion, booking.Id, $"Job completed - booking {booking.Id}."));
        credit.IsSuccess.Should().BeTrue();

        return (booking.Id, netAmount);
    }

    [Fact]
    public async Task GetJobEarningsAsync_returns_the_gross_commission_net_breakdown()
    {
        await using var context = _database.CreateContext();
        var (bookingId, netAmount) = await SeedCompletedJobEarningAsync(context, _providerId, DateOnly.FromDateTime(DateTime.UtcNow), grossAmount: 1000m, commissionAmount: 150m);

        var result = await CreateService(context).GetJobEarningsAsync(_providerId, fromDate: null, toDate: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        var row = result.Value.Items[0];
        row.BookingId.Should().Be(bookingId);
        row.ServiceName.Should().Be("Deep Cleaning");
        row.CommissionAmount.Should().Be(150m);
        row.NetAmountToProvider.Should().Be(netAmount);
        row.GrossAmount.Should().Be(1000m);
        row.PayoutStatus.Should().Be(ProviderJobPayoutStatus.AwaitingBatch, "no payout batch has been run yet for this job's period");
    }

    [Fact]
    public async Task GetJobEarningsAsync_scopes_results_to_the_callers_own_jobs()
    {
        await using var context = _database.CreateContext();
        await SeedCompletedJobEarningAsync(context, _otherProviderId, DateOnly.FromDateTime(DateTime.UtcNow));

        var result = await CreateService(context).GetJobEarningsAsync(_providerId, fromDate: null, toDate: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetJobEarningsAsync_mirrors_the_covering_payout_batchs_status()
    {
        await using var context = _database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedCompletedJobEarningAsync(context, _providerId, today);

        var payoutService = BuildPayoutService(context);
        var created = await payoutService.CreateBatchAsync(_providerId, new CreateProviderPayoutRequest(today.AddDays(-7), today));
        created.IsSuccess.Should().BeTrue();
        var marked = await payoutService.UpdateStatusAsync(created.Value.Id, new UpdateProviderPayoutStatusRequest(ProviderPayoutStatus.Processing, null, null));
        marked.IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetJobEarningsAsync(_providerId, fromDate: null, toDate: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].PayoutStatus.Should().Be(ProviderJobPayoutStatus.Processing);
    }

    [Fact]
    public async Task GetJobEarningsAsync_excludes_jobs_outside_the_requested_date_range()
    {
        await using var context = _database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedCompletedJobEarningAsync(context, _providerId, today.AddDays(-60), grossAmount: 500m, commissionAmount: 75m);
        await SeedCompletedJobEarningAsync(context, _providerId, today, grossAmount: 1000m, commissionAmount: 150m);

        var result = await CreateService(context).GetJobEarningsAsync(_providerId, fromDate: today.AddDays(-1), toDate: today, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].GrossAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task GetJobEarningsAsync_summarizes_the_full_filtered_period_not_just_the_page()
    {
        await using var context = _database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedCompletedJobEarningAsync(context, _providerId, today, grossAmount: 1000m, commissionAmount: 150m);
        await SeedCompletedJobEarningAsync(context, _providerId, today, grossAmount: 500m, commissionAmount: 75m);

        var result = await CreateService(context).GetJobEarningsAsync(_providerId, fromDate: null, toDate: null, page: 1, pageSize: 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle("the page size limits the returned page to one row");
        result.Value.TotalCount.Should().Be(2);
        result.Value.JobCount.Should().Be(2);
        result.Value.TotalNetAmount.Should().Be(850m + 425m, "the summary covers the whole filtered period, not just the returned page");
    }

    public void Dispose() => _database.Dispose();
}
