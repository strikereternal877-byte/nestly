using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderPayoutService"/>
/// <remarks>
/// Writes an audit entry for batch creation and every status change (task
/// 132c gap fix, NESTLY-007): a payout batch and its Processing/Paid/Failed
/// transitions are directly financial (bank reference, amount owed), the
/// same "every write is audited" reasoning <c>CouponManagementService</c>'s
/// doc comment gives for discount changes applies here. Staged before the
/// repository call so the repository's own <c>SaveChangesAsync</c> commits
/// both in one transaction.
/// </remarks>
public class ProviderPayoutService : IProviderPayoutService
{
    /// <summary>
    /// Task 251: page-size bounds for <see cref="SearchAsync"/>. Clamped here
    /// rather than in each caller because both payout list endpoints - admin
    /// PayoutsController.Search and provider EarningsController.ListPayouts -
    /// funnel through this one method, and neither validates its query
    /// string. Unbounded, a single request materializes the whole table;
    /// a page below 1 reaches the repository as a negative OFFSET, which
    /// PostgreSQL rejects outright ("OFFSET must not be negative") for a 500.
    /// Same limits as AuditLogQueryService and the admin *Validators.cs.
    /// </summary>
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IProviderRepository _providerRepository;
    private readonly IProviderPayoutRepository _payoutRepository;
    private readonly IProviderEarningLedgerRepository _ledgerRepository;
    private readonly IProviderBankAccountRepository _bankAccountRepository;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IProviderNotificationPublisher _notificationPublisher;
    private readonly IProviderPayoutGateway _payoutGateway;

    public ProviderPayoutService(
        IProviderRepository providerRepository,
        IProviderPayoutRepository payoutRepository,
        IProviderEarningLedgerRepository ledgerRepository,
        IProviderBankAccountRepository bankAccountRepository,
        IAuditLogWriter auditLogWriter,
        IProviderNotificationPublisher notificationPublisher,
        IProviderPayoutGateway payoutGateway)
    {
        _providerRepository = providerRepository;
        _payoutRepository = payoutRepository;
        _ledgerRepository = ledgerRepository;
        _bankAccountRepository = bankAccountRepository;
        _auditLogWriter = auditLogWriter;
        _notificationPublisher = notificationPublisher;
        _payoutGateway = payoutGateway;
    }

    public async Task<Result<ProviderPayoutResponse>> CreateBatchAsync(Guid providerId, CreateProviderPayoutRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("ProviderPayout.ProviderNotFound", "Provider was not found.");
        }

        var entries = await _ledgerRepository.ListByProviderAndPeriodAsync(providerId, request.PeriodStart, request.PeriodEnd);
        decimal total = entries.Sum(e => e.EntryType == ProviderEarningEntryType.Credit ? e.Amount : -e.Amount);

        if (total <= 0)
        {
            return Error.Business("ProviderPayout.NothingToPay", "There is no positive earning balance for this provider in the given period.");
        }

        var payout = new ProviderPayout(Guid.NewGuid(), providerId, request.PeriodStart, request.PeriodEnd, total);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderPayout",
            payout.Id.ToString(),
            "Created",
            NewValues: $"ProviderId={providerId}; Status=(none)->{payout.Status}; TotalAmount={payout.TotalAmount}"));

        await _payoutRepository.AddAsync(payout);

        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(providerId);
        return ToResponse(payout, provider.DisplayName, bankAccount);
    }

    public async Task<Result<ProviderPayoutResponse>> GetByIdAsync(Guid payoutId)
    {
        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(payout.ProviderId);
        return ToResponse(payout, provider?.DisplayName ?? "(unknown provider)", bankAccount);
    }

    public async Task<Result<ProviderPayoutSearchResponse>> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize)
    {
        // Clamp before the query, and echo the clamped values back in the
        // response so a caller that asked for page 0 / pageSize 10000 can see
        // what it actually got rather than silently mis-paging.
        page = page < 1 ? 1 : page;
        pageSize = pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

        var (rows, totalCount) = await _payoutRepository.SearchAsync(providerId, status, page, pageSize);

        // Task 254: the local dictionary only avoided re-querying a provider
        // already seen on this page - the first row for each distinct provider
        // still cost its own round trip, so an admin page spanning 100
        // providers issued 100 of them. One batched lookup instead.
        var providerIds = rows.Select(p => p.ProviderId).Distinct().ToList();
        var displayNames = await _providerRepository.GetDisplayNamesByIdsAsync(providerIds);

        // Same batched-lookup reasoning as displayNames just above (task
        // 254) - a direct repository call, not a second N+1.
        var bankAccounts = await _bankAccountRepository.GetByProviderIdsAsync(providerIds);

        var items = rows
            .Select(payout => ToResponse(
                payout,
                displayNames.GetValueOrDefault(payout.ProviderId, "(unknown provider)"),
                bankAccounts.GetValueOrDefault(payout.ProviderId)))
            .ToList();

        return new ProviderPayoutSearchResponse(items, totalCount, page, pageSize);
    }

    public async Task<Result<ProviderPayoutResponse>> UpdateStatusAsync(Guid payoutId, UpdateProviderPayoutStatusRequest request)
    {
        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        var previousStatus = payout.Status;

        try
        {
            switch (request.Status)
            {
                case ProviderPayoutStatus.Processing:
                    payout.MarkProcessing();
                    break;
                case ProviderPayoutStatus.Paid:
                    if (string.IsNullOrWhiteSpace(request.PayoutReference))
                    {
                        return Error.Validation("ProviderPayout.ReferenceRequired", "A payout reference is required to mark a payout paid.");
                    }

                    payout.MarkPaid(request.PayoutReference);
                    break;
                case ProviderPayoutStatus.Failed:
                    payout.MarkFailed(request.Notes);
                    break;
                default:
                    return Error.Validation("ProviderPayout.InvalidTargetStatus", "A payout can only be moved to Processing, Paid or Failed.");
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderPayout.InvalidTransition", ex.Message);
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderPayout",
            payout.Id.ToString(),
            "StatusChanged",
            NewValues: $"ProviderId={payout.ProviderId}; Status={previousStatus}->{payout.Status}; PayoutReference={payout.PayoutReference ?? "null"}"));

        await _payoutRepository.UpdateAsync(payout);

        if (payout.Status == ProviderPayoutStatus.Paid)
        {
            await _notificationPublisher.NotifyAsync(
                payout.ProviderId,
                ProviderNotificationType.PayoutProcessed,
                "Payout processed",
                $"Your payout of ₹{payout.TotalAmount:N2} for {payout.PeriodStart:d MMM} - {payout.PeriodEnd:d MMM} has been paid.",
                deepLinkPath: $"/earnings/payouts/{payout.Id}");
        }

        var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(payout.ProviderId);
        return ToResponse(payout, provider?.DisplayName ?? "(unknown provider)", bankAccount);
    }

    /// <inheritdoc cref="IProviderPayoutService.PayViaPayUAsync"/>
    public async Task<Result<ProviderPayoutResponse>> PayViaPayUAsync(Guid payoutId)
    {
        if (!_payoutGateway.IsConfigured)
        {
            return Error.Business("ProviderPayout.PayUNotConfigured", "PayU Payouts is not configured.");
        }

        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        if (payout.Status != ProviderPayoutStatus.Pending)
        {
            return Error.Business("ProviderPayout.InvalidTransition", $"Cannot pay via PayU a payout in {payout.Status} status - only a Pending payout can be.");
        }

        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(payout.ProviderId);
        if (bankAccount is null || bankAccount.VerificationStatus != ProviderBankAccountVerificationStatus.Verified)
        {
            return Error.Business("ProviderPayout.BankAccountNotVerified", "The provider has no admin-verified bank account on file.");
        }

        var transferResult = await _payoutGateway.InitiateTransferAsync(new PayoutTransferRequest(
            MerchantReferenceId: payout.Id.ToString(),
            Amount: payout.TotalAmount,
            BeneficiaryAccountNumber: bankAccount.AccountNumber,
            BeneficiaryIfscCode: bankAccount.IfscCode,
            BeneficiaryName: bankAccount.AccountHolderName));

        if (!transferResult.Accepted)
        {
            return Error.Business("ProviderPayout.PayUTransferDeclined", transferResult.FailureReason ?? "PayU declined the transfer request.");
        }

        var previousStatus = payout.Status;
        payout.MarkProcessingViaPayU();

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderPayout",
            payout.Id.ToString(),
            "PayUTransferInitiated",
            NewValues: $"ProviderId={payout.ProviderId}; Status={previousStatus}->{payout.Status}; ProcessedVia={payout.ProcessedVia}"));

        await _payoutRepository.UpdateAsync(payout);

        var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
        return ToResponse(payout, provider?.DisplayName ?? "(unknown provider)", bankAccount);
    }

    /// <inheritdoc cref="IProviderPayoutService.HandlePayUTransferWebhookAsync"/>
    public async Task<Result> HandlePayUTransferWebhookAsync(PayUPayoutWebhookRequest request)
    {
        // Every documented PayU Payouts event other than the two transfer
        // outcomes (deposit_success, low_balance_alert, downtime_notification,
        // etc.) is acknowledged without being applied - see this method's own
        // interface doc comment.
        bool isSuccess = string.Equals(request.Event, "TRANSFER_SUCCESS", StringComparison.OrdinalIgnoreCase);
        bool isFailure = string.Equals(request.Event, "TRANSFER_FAILED", StringComparison.OrdinalIgnoreCase);
        if (!isSuccess && !isFailure)
        {
            return Result.Success();
        }

        if (!Guid.TryParse(request.MerchantReferenceId, out var payoutId))
        {
            return Result.Failure(Error.NotFound("ProviderPayout.NotFound", "No payout matches this webhook's merchant reference."));
        }

        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Result.Failure(Error.NotFound("ProviderPayout.NotFound", "No payout matches this webhook's merchant reference."));
        }

        // PayU does not document an HMAC/signature scheme for this webhook -
        // see PayUPayoutWebhookPayload's own doc comment for that gap. This
        // merchant-id echo is the only lightweight authenticity check
        // available: it rules out a stray/misdirected call, though not a
        // forged one from a party who already knows the configured id.
        if (!_payoutGateway.MatchesConfiguredMerchantId(request.PayoutMerchantId))
        {
            return Result.Failure(Error.Unauthorized("ProviderPayout.PayUMerchantMismatch", "This webhook's payoutMerchantId does not match the configured PayU Payouts merchant."));
        }

        // Idempotent against redelivery by construction: only a payout
        // currently Processing is resolved - a redelivered webhook for an
        // already-Paid/Failed payout, or one that was never moved to
        // Processing via PayU at all (still Pending, or a manual payout
        // some prior process failed), is a safe no-op success.
        if (payout.Status != ProviderPayoutStatus.Processing)
        {
            return Result.Success();
        }

        var previousStatus = payout.Status;

        if (isSuccess)
        {
            payout.MarkPaid(request.PayuRefId ?? request.MerchantReferenceId);
        }
        else
        {
            payout.MarkFailed(request.Msg ?? "PayU reported the transfer as failed.");
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderPayout",
            payout.Id.ToString(),
            "PayUTransferWebhookApplied",
            NewValues: $"ProviderId={payout.ProviderId}; Status={previousStatus}->{payout.Status}; PayoutReference={payout.PayoutReference ?? "null"}"));

        await _payoutRepository.UpdateAsync(payout);

        if (payout.Status == ProviderPayoutStatus.Paid)
        {
            await _notificationPublisher.NotifyAsync(
                payout.ProviderId,
                ProviderNotificationType.PayoutProcessed,
                "Payout processed",
                $"Your payout of ₹{payout.TotalAmount:N2} for {payout.PeriodStart:d MMM} - {payout.PeriodEnd:d MMM} has been paid.",
                deepLinkPath: $"/earnings/payouts/{payout.Id}");
        }

        return Result.Success();
    }

    /// <summary>
    /// <paramref name="bankAccount"/> is the provider's CURRENT bank account
    /// (product decision - visible "on the payout screen" so an admin does
    /// not have to navigate away), not a snapshot of what it was when this
    /// payout was created - store-and-display only, matching
    /// <see cref="ProviderBankAccount"/>'s own "no enforcement" scope for the
    /// manual flow (the real PayU flow above DOES enforce <see cref="ProviderBankAccountVerificationStatus.Verified"/>
    /// via <see cref="PayViaPayUAsync"/> - see that method and <see cref="ProviderBankAccount"/>'s
    /// own doc comment for the distinction).
    /// </summary>
    private ProviderPayoutResponse ToResponse(ProviderPayout payout, string providerDisplayName, ProviderBankAccount? bankAccount) => new(
        payout.Id, payout.ProviderId, providerDisplayName, payout.PeriodStart, payout.PeriodEnd,
        payout.TotalAmount, payout.Status, payout.PayoutReference, payout.Notes, payout.CreatedAt, payout.UpdatedAt,
        bankAccount is null
            ? null
            : new ProviderPayoutBankAccountSummaryResponse(
                bankAccount.AccountHolderName, bankAccount.AccountNumber, bankAccount.IfscCode, bankAccount.BankName, bankAccount.VerificationStatus),
        payout.ProcessedVia,
        // Read live off the gateway rather than cached at construction - a
        // payout fetched right after an admin enables/disables PayU Payouts
        // configuration should reflect that immediately, not a stale value
        // captured elsewhere in the request pipeline.
        _payoutGateway.IsConfigured);
}
