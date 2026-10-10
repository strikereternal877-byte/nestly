using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>Admin-recorded manual adjustment to a provider's earning ledger (task 148) - a credit (e.g. a correction) or a debit (a penalty).</summary>
public sealed record RecordProviderEarningAdjustmentRequest(
    ProviderEarningEntryType EntryType,
    decimal Amount,
    ProviderEarningSourceType SourceType,
    Guid? SourceReferenceId,
    string Description);

public sealed record ProviderEarningLedgerEntryResponse(
    Guid Id,
    Guid ProviderId,
    ProviderEarningEntryType EntryType,
    decimal Amount,
    decimal BalanceAfter,
    ProviderEarningSourceType SourceType,
    Guid? SourceReferenceId,
    string Description,
    DateTime CreatedAtUtc);

public sealed record ProviderEarningsSummaryResponse(
    Guid ProviderId,
    decimal CurrentBalance,
    IReadOnlyList<ProviderEarningLedgerEntryResponse> Entries);

/// <summary>Admin runs a payout batch for a provider over a period (PROVIDER.md API surface "run payout batch", task 148). Sums the earning ledger for that period - no gateway call, OPEN DECISIONS #3.</summary>
public sealed record CreateProviderPayoutRequest(DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record UpdateProviderPayoutStatusRequest(ProviderPayoutStatus Status, string? PayoutReference, string? Notes);

/// <param name="BankAccount">
/// The provider's current bank account (product decision: visible "on the
/// payout screen" so an admin processing a real transfer does not have to
/// navigate away to see it) - appended last, matching this positional
/// record's own convention elsewhere in this module. Null when the provider
/// has not submitted bank account details yet.
/// </param>
/// <param name="ProcessedVia">
/// Which of the two always-available paths moved this payout into
/// Processing - see <see cref="ProviderPayoutChannel"/>'s own doc comment.
/// Stays <see cref="ProviderPayoutChannel.Manual"/> for a payout that never
/// left Pending, same as the domain property's own default.
/// </param>
/// <param name="IsGatewayConfigured">
/// Whether real PayU Payouts is configured server-side right now (PayU
/// Payouts task brief: the "Pay via PayU" button is "only shown/enabled in
/// admin-web when PayU Payouts is configured server-side"). Appended last,
/// like <paramref name="BankAccount"/> before it - admin-web reads this,
/// not <paramref name="ProcessedVia"/>, to decide whether to render that
/// button, since a payout already settled manually must still report the
/// gateway's current configuration state honestly.
/// </param>
public sealed record ProviderPayoutResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderDisplayName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalAmount,
    ProviderPayoutStatus Status,
    string? PayoutReference,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    ProviderPayoutBankAccountSummaryResponse? BankAccount = null,
    ProviderPayoutChannel ProcessedVia = ProviderPayoutChannel.Manual,
    bool IsGatewayConfigured = false);

public sealed record ProviderPayoutSearchResponse(IReadOnlyList<ProviderPayoutResponse> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// The provider-visible payout status of one completed job's earning
/// (docs/OPEN-FIXES-FEATURES.csv "Earnings detail and payouts"). Not a
/// persisted enum - derived at read time the same way <c>ProviderJobStatus</c>
/// is, by checking whether the job's credit date falls inside one of the
/// provider's <see cref="ProviderPayout"/> batches and, if so, mirroring that
/// batch's own <see cref="ProviderPayoutStatus"/>. v1 payouts are manual
/// batches an admin runs over a period (OPEN DECISIONS #3) - a completed job
/// is credited to the ledger immediately (<see cref="EscrowReleaseOnCompletionHandler"/>-equivalent
/// wording), but stays un-batched until an admin actually runs a payout
/// covering its date, so <see cref="AwaitingBatch"/> is a real, common state
/// here rather than a placeholder.
/// </summary>
public enum ProviderJobPayoutStatus
{
    /// <summary>Credited to the ledger, but no payout batch has been run yet for a period covering this job's completion date.</summary>
    AwaitingBatch,

    /// <summary>Included in a payout batch that has been created but not yet marked Processing/Paid.</summary>
    PendingSettlement,

    /// <summary>Included in a payout batch whose bank transfer is underway.</summary>
    Processing,

    /// <summary>Included in a payout batch whose bank transfer has been completed.</summary>
    Paid,

    /// <summary>Included in a payout batch whose bank transfer failed - awaiting a retry batch.</summary>
    Failed
}

/// <summary>
/// One completed job's earning breakdown (docs/OPEN-FIXES-FEATURES.csv
/// "Earnings detail and payouts") - the gross-to-net figures
/// <c>ProviderJobDetailResponse</c> already shows on the job screen
/// (<see cref="GrossAmount"/>/<see cref="CommissionAmount"/>/<see cref="NetAmountToProvider"/>,
/// same <c>ProviderJobService.ResolvePayoutAsync</c> source of truth), plus
/// this job's place in the payout timeline - so a provider can tell not just
/// what a job paid but when they will actually receive it.
/// </summary>
public sealed record ProviderEarningJobResponse(
    Guid BookingId,
    string BookingReference,
    string ServiceName,
    DateOnly CompletionDate,
    decimal GrossAmount,
    decimal CommissionAmount,
    decimal NetAmountToProvider,
    ProviderJobPayoutStatus PayoutStatus,
    DateTime CreditedAtUtc);

/// <summary>
/// Paginated response for the job-level earnings ledger, plus a summary over
/// the *entire filtered period* (not just the returned page) - cheap to
/// compute alongside the list since it is the same filtered query, summed.
/// </summary>
public sealed record ProviderEarningJobSearchResponse(
    IReadOnlyList<ProviderEarningJobResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    decimal TotalNetAmount,
    int JobCount);

// ---- Real PayU Payouts integration ----

/// <summary>
/// PayU Payouts' transfer webhook, exactly as PayU posts it - JSON, unlike
/// Hosted Checkout's form-encoded callback (<see cref="Payments.PayUWebhookFormPayload"/>),
/// so this only needs property names, not a form-field binder. Property
/// names mirror PayU's own field names (verified against docs.payu.in this
/// session): <see cref="Event"/> is one of many documented event types (only
/// <c>TRANSFER_SUCCESS</c>/<c>TRANSFER_FAILED</c> are acted on -
/// <see cref="IProviderPayoutService.HandlePayUTransferWebhookAsync"/>
/// acknowledges every other one - deposit_success, low_balance_alert,
/// downtime_notification, etc. - without applying it). ASP.NET's default
/// System.Text.Json model binder matches these case-insensitively against
/// PayU's camelCase wire names, so no <c>JsonPropertyName</c> attributes are
/// needed, unlike PayU Hosted Checkout's PascalCase-vs-lowercase form field
/// mismatch.
/// </summary>
/// <remarks>
/// PayU does not document an HMAC/signature scheme for this webhook the way
/// Hosted Checkout's callback has (<see cref="Payments.IPaymentGateway.VerifyWebhookSignature"/>) -
/// this is a real, acknowledged gap, not an oversight papered over: the only
/// lightweight authenticity check available is confirming
/// <see cref="PayoutMerchantId"/> echoes this deployment's own configured
/// merchant id (<see cref="IProviderPayoutService.HandlePayUTransferWebhookAsync"/>'s
/// doc comment), which rules out a stray/misdirected call but not a forged
/// one from a party who already knows that id.
/// </remarks>
public sealed class PayUPayoutWebhookPayload
{
    public string? Event { get; set; }
    public string? Msg { get; set; }
    public string? PayuRefId { get; set; }
    public string? MerchantReferenceId { get; set; }
    public string? BankReferenceId { get; set; }
    public string? PayoutMerchantId { get; set; }
}

/// <summary>
/// The normalized shape <see cref="IProviderPayoutService.HandlePayUTransferWebhookAsync"/>
/// takes, built from <see cref="PayUPayoutWebhookPayload"/> by the admin-api
/// controller action - mirrors the Hosted Checkout webhook's own raw-payload
/// (<see cref="Payments.PayUWebhookFormPayload"/>) vs. normalized-request
/// (<see cref="Payments.PaymentWebhookRequest"/>) split. A plain record
/// rather than the same class reused: the service layer should depend on its
/// own contract, not on the literal shape PayU happens to POST.
/// </summary>
public sealed record PayUPayoutWebhookRequest(
    string Event,
    string? Msg,
    string? PayuRefId,
    string MerchantReferenceId,
    string? BankReferenceId,
    string? PayoutMerchantId);
