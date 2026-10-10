using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Covers the real PayU Payouts additions to <see cref="ProviderPayoutService"/>
/// (PayU Payouts task brief) - <see cref="ProviderPayoutService.PayViaPayUAsync"/>'s
/// guards and <see cref="ProviderPayoutService.HandlePayUTransferWebhookAsync"/>'s
/// idempotency/authenticity checks. Uses a small hand-written
/// <see cref="StubProviderPayoutGateway"/> rather than any HTTP stubbing -
/// <see cref="Catalog.Tests.PayUProviderPayoutGatewayTests"/> (a different
/// test project) already covers the real HTTP-calling gateway in isolation,
/// so this class only needs to control what that gateway seam returns.
/// </summary>
public class ProviderPayoutServicePayUTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;

    public ProviderPayoutServicePayUTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    private static ProviderPayoutService BuildService(NestlyDbContext context, IProviderPayoutGateway gateway) => new(
        new ProviderRepository(context),
        new ProviderPayoutRepository(context),
        new ProviderEarningLedgerRepository(context),
        new ProviderBankAccountRepository(context),
        new AuditLogWriter(context, new StubAuditContextProvider()),
        TestServices.ProviderNotificationPublisher(context),
        gateway);

    private async Task<ProviderPayout> CreatePendingPayoutAsync(NestlyDbContext context, decimal amount = 1000m)
    {
        var ledgerService = new ProviderEarningLedgerService(
            new ProviderRepository(context),
            new ProviderEarningLedgerRepository(context),
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new ProviderPayoutRepository(context));
        var credited = await ledgerService.RecordAdjustmentAsync(
            _providerId, new RecordProviderEarningAdjustmentRequest(ProviderEarningEntryType.Credit, amount, ProviderEarningSourceType.JobCompletion, Guid.NewGuid(), "Job completed."));
        credited.IsSuccess.Should().BeTrue();

        var payoutRepository = new ProviderPayoutRepository(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var payout = new ProviderPayout(Guid.NewGuid(), _providerId, today.AddDays(-7), today, amount);
        await payoutRepository.AddAsync(payout);
        return payout;
    }

    private async Task AddBankAccountAsync(NestlyDbContext context, ProviderBankAccountVerificationStatus status)
    {
        var repository = new ProviderBankAccountRepository(context);
        var account = new ProviderBankAccount(Guid.NewGuid(), _providerId, "Ravi Kumar", "000111222333", "HDFC0000123", "HDFC Bank");
        if (status == ProviderBankAccountVerificationStatus.Verified)
        {
            account.Approve(Guid.NewGuid());
        }
        else if (status == ProviderBankAccountVerificationStatus.Rejected)
        {
            account.Reject(Guid.NewGuid(), "Mismatched account holder name.");
        }

        await repository.AddAsync(account);
    }

    // ---- PayViaPayUAsync guards ----

    [Fact]
    public async Task PayViaPayUAsync_returns_business_error_when_gateway_is_not_configured()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        var service = BuildService(context, new NoOpProviderPayoutGateway());

        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.PayUNotConfigured");
    }

    [Fact]
    public async Task PayViaPayUAsync_returns_not_found_for_an_unknown_payout()
    {
        await using var context = _database.CreateContext();
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.PayViaPayUAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.NotFound");
    }

    [Fact]
    public async Task PayViaPayUAsync_rejects_a_payout_that_is_not_pending()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway();
        var service = BuildService(context, gateway);
        (await service.PayViaPayUAsync(payout.Id)).IsSuccess.Should().BeTrue();

        // Already Processing now - a second call must be rejected, not
        // double-initiate a transfer.
        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.InvalidTransition");
        gateway.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PayViaPayUAsync_rejects_when_the_provider_has_no_bank_account_on_file()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.BankAccountNotVerified");
    }

    [Theory]
    [InlineData(ProviderBankAccountVerificationStatus.Pending)]
    [InlineData(ProviderBankAccountVerificationStatus.Rejected)]
    public async Task PayViaPayUAsync_rejects_when_the_bank_account_is_not_verified(ProviderBankAccountVerificationStatus status)
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, status);
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.BankAccountNotVerified");
    }

    [Fact]
    public async Task PayViaPayUAsync_accepts_and_moves_the_payout_to_processing_via_payu()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context, 750m);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway();
        var service = BuildService(context, gateway);

        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProviderPayoutStatus.Processing);
        result.Value.ProcessedVia.Should().Be(ProviderPayoutChannel.PayUAutomated);
        result.Value.IsGatewayConfigured.Should().BeTrue();

        gateway.Requests.Should().ContainSingle();
        gateway.Requests[0].MerchantReferenceId.Should().Be(payout.Id.ToString());
        gateway.Requests[0].Amount.Should().Be(750m);
        gateway.Requests[0].BeneficiaryAccountNumber.Should().Be("000111222333");

        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Processing);
        reloaded.ProcessedVia.Should().Be(ProviderPayoutChannel.PayUAutomated);
    }

    [Fact]
    public async Task PayViaPayUAsync_leaves_the_payout_pending_when_PayU_declines_the_transfer()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway(transferResult: new PayoutTransferResult(false, "Invalid beneficiary details"));
        var service = BuildService(context, gateway);

        var result = await service.PayViaPayUAsync(payout.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.PayUTransferDeclined");

        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Pending);
    }

    // ---- HandlePayUTransferWebhookAsync ----

    [Fact]
    public async Task Webhook_rejects_a_mismatched_payoutMerchantId()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway(configuredMerchantId: "REAL_MERCHANT");
        var service = BuildService(context, gateway);
        (await service.PayViaPayUAsync(payout.Id)).IsSuccess.Should().BeTrue();

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: null, PayuRefId: "UTR123", MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: null, PayoutMerchantId: "SOMEONE_ELSE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.PayUMerchantMismatch");
    }

    [Fact]
    public async Task Webhook_returns_not_found_for_an_unresolvable_merchant_reference()
    {
        await using var context = _database.CreateContext();
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: null, PayuRefId: "UTR123", MerchantReferenceId: Guid.NewGuid().ToString(),
            BankReferenceId: null, PayoutMerchantId: "MERCHANT123"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.NotFound");
    }

    [Fact]
    public async Task Webhook_returns_not_found_for_a_malformed_merchant_reference()
    {
        await using var context = _database.CreateContext();
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: null, PayuRefId: "UTR123", MerchantReferenceId: "not-a-guid",
            BankReferenceId: null, PayoutMerchantId: "MERCHANT123"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderPayout.NotFound");
    }

    [Fact]
    public async Task Webhook_ignores_every_non_transfer_event_as_a_safe_no_op()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "deposit_success", Msg: null, PayuRefId: null, MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: null, PayoutMerchantId: "MERCHANT123"));

        result.IsSuccess.Should().BeTrue();
        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Pending);
    }

    [Fact]
    public async Task Webhook_is_a_safe_no_op_for_a_payout_never_moved_to_processing_via_payu()
    {
        // Still Pending (never had PayViaPayUAsync called) - a stray/replayed
        // webhook here must not force a state transition it never earned.
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        var service = BuildService(context, new StubProviderPayoutGateway());

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: null, PayuRefId: "UTR123", MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: null, PayoutMerchantId: "MERCHANT123"));

        result.IsSuccess.Should().BeTrue();
        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Pending);
    }

    [Fact]
    public async Task Webhook_marks_the_payout_paid_with_PayUs_own_reference_on_transfer_success()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway();
        var service = BuildService(context, gateway);
        (await service.PayViaPayUAsync(payout.Id)).IsSuccess.Should().BeTrue();

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: "Success", PayuRefId: "UTR999888777", MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: "BANKREF1", PayoutMerchantId: "MERCHANT123"));

        result.IsSuccess.Should().BeTrue();
        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Paid);
        reloaded.PayoutReference.Should().Be("UTR999888777");
    }

    [Fact]
    public async Task Webhook_marks_the_payout_failed_on_transfer_failed()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway();
        var service = BuildService(context, gateway);
        (await service.PayViaPayUAsync(payout.Id)).IsSuccess.Should().BeTrue();

        var result = await service.HandlePayUTransferWebhookAsync(new PayUPayoutWebhookRequest(
            Event: "TRANSFER_FAILED", Msg: "Beneficiary account frozen", PayuRefId: null, MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: null, PayoutMerchantId: "MERCHANT123"));

        result.IsSuccess.Should().BeTrue();
        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Failed);
        reloaded.Notes.Should().Be("Beneficiary account frozen");
    }

    [Fact]
    public async Task Webhook_redelivery_after_resolution_is_a_safe_no_op_not_a_re_apply()
    {
        await using var context = _database.CreateContext();
        var payout = await CreatePendingPayoutAsync(context);
        await AddBankAccountAsync(context, ProviderBankAccountVerificationStatus.Verified);
        var gateway = new StubProviderPayoutGateway();
        var service = BuildService(context, gateway);
        (await service.PayViaPayUAsync(payout.Id)).IsSuccess.Should().BeTrue();
        var webhookRequest = new PayUPayoutWebhookRequest(
            Event: "TRANSFER_SUCCESS", Msg: "Success", PayuRefId: "UTR999888777", MerchantReferenceId: payout.Id.ToString(),
            BankReferenceId: "BANKREF1", PayoutMerchantId: "MERCHANT123");
        (await service.HandlePayUTransferWebhookAsync(webhookRequest)).IsSuccess.Should().BeTrue();

        // Redelivered - must not throw (MarkPaid on an already-Paid payout
        // would throw InvalidOperationException) and must not touch the
        // reference again.
        var result = await service.HandlePayUTransferWebhookAsync(webhookRequest);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await new ProviderPayoutRepository(context).GetByIdAsync(payout.Id);
        reloaded!.Status.Should().Be(ProviderPayoutStatus.Paid);
        reloaded.PayoutReference.Should().Be("UTR999888777");
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    /// <summary>
    /// A configurable <see cref="IProviderPayoutGateway"/> test double -
    /// deliberately NOT an HTTP stub (see this file's own doc comment): these
    /// tests exercise <see cref="ProviderPayoutService"/>'s own guards and
    /// idempotency logic, not the gateway's wire format.
    /// </summary>
    private sealed class StubProviderPayoutGateway : IProviderPayoutGateway
    {
        private readonly bool _isConfigured;
        private readonly PayoutTransferResult _transferResult;
        private readonly string? _configuredMerchantId;

        public StubProviderPayoutGateway(bool isConfigured = true, PayoutTransferResult? transferResult = null, string? configuredMerchantId = "MERCHANT123")
        {
            _isConfigured = isConfigured;
            _transferResult = transferResult ?? new PayoutTransferResult(true);
            _configuredMerchantId = configuredMerchantId;
        }

        public bool IsConfigured => _isConfigured;

        public List<PayoutTransferRequest> Requests { get; } = [];

        public bool MatchesConfiguredMerchantId(string? payoutMerchantId) =>
            _isConfigured && !string.IsNullOrWhiteSpace(payoutMerchantId) && payoutMerchantId == _configuredMerchantId;

        public Task<PayoutTransferResult> InitiateTransferAsync(PayoutTransferRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_transferResult);
        }
    }

    public void Dispose() => _database.Dispose();
}
