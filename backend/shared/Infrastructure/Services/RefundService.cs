using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Refunds;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Refund initiation and lifecycle (SRS 11.17.2, 14.4, tasks 75a-d). Like
/// <see cref="PaymentWebhookService"/>, this depends on <see cref="NestlyDbContext"/>
/// directly to commit the Booking transition and the RefundTransaction
/// atomically - a refund that "succeeded" but left the booking in the wrong
/// state (or vice versa) is exactly the kind of inconsistency SRS 29.3
/// ("critical workflows should support reconciliation") exists to prevent.
///
/// Also keeps the platform escrow ledger honest (task 158): a refunded
/// booking's hold - if any is still held, i.e. it wasn't already released to
/// a provider on completion - is released back out in the same transaction,
/// without changing how the refund itself is actually paid out (still
/// <see cref="IWalletService"/> or the gateway, exactly as before).
///
/// Task 356: a booking is refundable up to what it was actually funded with,
/// which is its gateway payment PLUS whatever wallet balance it consumed at
/// checkout - not the payment alone. Either half can be zero: a booking whose
/// wallet balance covered the whole price is confirmed with no
/// PaymentTransaction at all (task 331), and until this change had no refund
/// path whatsoever. Each half settles as its own <see cref="RefundTransaction"/>
/// (see <see cref="RefundFundingSource"/>), so both show up in the customer's
/// refund history rather than the wallet half moving invisibly.
/// </summary>
public class RefundService : IRefundService
{
    private static readonly BookingStatus[] EligibleBookingStatuses =
    [
        BookingStatus.Completed, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin, BookingStatus.RefundPending
    ];

    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IRefundTransactionRepository _refundRepository;
    private readonly IWalletService _walletService;
    private readonly IEscrowService _escrowService;
    private readonly IProviderEarningLedgerRepository _providerEarningLedgerRepository;
    private readonly IProviderEarningLedgerService _providerEarningLedgerService;
    private readonly IPaymentGateway _gateway;
    private readonly NestlyDbContext _context;
    private readonly ILogger<RefundService> _logger;

    public RefundService(
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentRepository,
        IRefundTransactionRepository refundRepository,
        IWalletService walletService,
        IEscrowService escrowService,
        IProviderEarningLedgerRepository providerEarningLedgerRepository,
        IProviderEarningLedgerService providerEarningLedgerService,
        IPaymentGateway gateway,
        NestlyDbContext context,
        ILogger<RefundService> logger)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _refundRepository = refundRepository;
        _walletService = walletService;
        _escrowService = escrowService;
        _providerEarningLedgerRepository = providerEarningLedgerRepository;
        _providerEarningLedgerService = providerEarningLedgerService;
        _gateway = gateway;
        _context = context;
        _logger = logger;
    }

    public Task<Result<RefundOutcomeResponse>> InitiateFullRefundAsync(Guid bookingId, string reason, RefundMethod method = RefundMethod.Gateway) =>
        InitiateAsync(bookingId, RefundType.Full, requestedAmount: null, reason, method);

    public Task<Result<RefundOutcomeResponse>> InitiatePartialRefundAsync(Guid bookingId, decimal amount, string reason, RefundMethod method = RefundMethod.Gateway)
    {
        if (amount <= 0)
        {
            return Task.FromResult(Result.Failure<RefundOutcomeResponse>(Error.Validation("Refund.InvalidAmount", "Refund amount must be positive.")));
        }

        return InitiateAsync(bookingId, RefundType.Partial, amount, reason, method);
    }

    public async Task<Result<IReadOnlyList<RefundTransactionResponse>>> ListByBookingAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Refund.BookingNotFound", "The specified booking does not exist.");
        }

        var refunds = await _refundRepository.ListByBookingAsync(bookingId);
        IReadOnlyList<RefundTransactionResponse> response = refunds.Select(ToResponse).ToList();
        return Result.Success(response);
    }

    private async Task<Result<RefundOutcomeResponse>> InitiateAsync(
        Guid bookingId, RefundType type, decimal? requestedAmount, string reason, RefundMethod method)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Refund.BookingNotFound", "The specified booking does not exist.");
        }

        if (!EligibleBookingStatuses.Contains(booking.Status))
        {
            return Error.Business(
                "Refund.BookingNotEligible",
                $"A booking in status '{booking.Status}' is not eligible for a refund.");
        }

        var payment = await _paymentRepository.GetByBookingIdAsync(bookingId);
        decimal paymentSettledAmount = payment is { Status: PaymentTransactionStatus.Success } ? payment.Amount : 0m;
        decimal walletCreditApplied = booking.WalletCreditAppliedSnapshot ?? 0m;

        if (paymentSettledAmount <= 0 && walletCreditApplied <= 0)
        {
            // Nothing was ever collected for this booking - no successful
            // payment and no wallet balance spent on it. A 100%-off coupon,
            // a subscription free visit and an AMC entitlement redemption all
            // land here (task 331 confirms them without charging anything):
            // there is genuinely nothing to hand back, which is a clean
            // business refusal rather than the missing capability this same
            // error used to report for a wallet-covered booking.
            return Error.Business("Refund.NoSuccessfulPayment", "This booking has no payment or wallet credit to refund.");
        }

        var priorRefunds = await _refundRepository.ListByBookingAsync(bookingId);
        var remaining = RefundAllocationCalculator.ComputeRemaining(paymentSettledAmount, walletCreditApplied, priorRefunds);

        decimal amount;
        if (type == RefundType.Full)
        {
            if (remaining.Total <= 0)
            {
                return Error.Business("Refund.NothingToRefund", "This booking has already been fully refunded.");
            }

            amount = remaining.Total;
        }
        else
        {
            amount = requestedAmount!.Value;
            if (amount > remaining.Total)
            {
                return Error.Validation("Refund.ExceedsRemainingBalance", $"Only {remaining.Total} remains refundable on this booking.");
            }
        }

        var allocation = RefundAllocationCalculator.Allocate(amount, remaining);
        var settlements = new List<RefundTransaction>(capacity: 2);
        if (allocation.FromPayment > 0)
        {
            settlements.Add(RefundTransaction.ForPayment(Guid.NewGuid(), bookingId, payment!.Id, type, method, allocation.FromPayment, reason));
        }

        if (allocation.FromWallet > 0)
        {
            settlements.Add(RefundTransaction.ForWalletCredit(Guid.NewGuid(), bookingId, type, allocation.FromWallet, reason));
        }

        foreach (var settlement in settlements)
        {
            settlement.MarkProcessing();
        }

        await using var dbTransaction = await _context.Database.BeginTransactionAsync();
        try
        {
            if (booking.Status != BookingStatus.RefundPending)
            {
                booking.TransitionTo(BookingStatus.RefundPending, reason);
            }

            foreach (var settlement in settlements)
            {
                await SettleAsync(settlement, booking, payment, reason);
            }

            // A real gateway's refund can fail (the sandbox's never does -
            // see SettleAsync's own comment): only treat this refund as
            // complete, and only release escrow/claw back the provider's
            // earning, for a settlement that actually reached Refunded. A
            // failed settlement is still persisted below - RefundTransactionLifecycle
            // allows Failed -> Processing, so it stays visible and retryable
            // rather than silently disappearing - but leaves the booking at
            // RefundPending rather than falsely advancing it to Refunded.
            bool anyFailed = settlements.Any(s => s.Status == RefundStatus.Failed);

            if (!anyFailed && amount >= remaining.Total)
            {
                booking.TransitionTo(BookingStatus.Refunded, "Refund completed.");
            }

            foreach (var settlement in settlements)
            {
                await _refundRepository.AddAsync(settlement);
            }

            await _bookingRepository.UpdateAsync(booking);

            // Task 158: release this refund's amount back out of escrow -
            // a no-op if the booking's hold was already released to its
            // provider on completion before this refund was issued. Only the
            // payment-funded settlement releases anything: escrow only ever
            // held the gateway payment (see EscrowService.HoldAsync), so
            // counting the wallet-funded half here would release a hold that
            // still belongs to money nobody has refunded yet.
            var paymentSettlement = settlements.SingleOrDefault(
                r => r.FundingSource == RefundFundingSource.Payment && r.Status == RefundStatus.Refunded);
            if (paymentSettlement is not null)
            {
                await _escrowService.ReleaseForRefundAsync(bookingId, paymentSettlement.Id, paymentSettlement.Amount);
                await ClawBackProviderEarningAsync(bookingId, payment, paymentSettlement.Amount);
            }

            await dbTransaction.CommitAsync();

            if (anyFailed)
            {
                var failedSettlement = settlements.First(s => s.Status == RefundStatus.Failed);
                return Error.Business(
                    "Refund.GatewayDeclined",
                    $"The payment gateway declined this refund: {failedSettlement.Reason}. It has been recorded and can be retried.");
            }
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }

        return Result.Success(new RefundOutcomeResponse(bookingId, amount, settlements.Select(ToResponse).ToList()));
    }

    /// <summary>
    /// Claws back part or all of a provider's job-completion earning when a
    /// booking is refunded after escrow already paid it out (task 158's
    /// counterpart to <see cref="EscrowReleaseOnCompletionHandler"/>) - a
    /// no-op if the booking never reached Completed with a provider credited
    /// in the first place, per <see cref="ProviderEarningSourceType.JobCompletion"/>'s
    /// own scope. Without this, <see cref="IEscrowService.ReleaseForRefundAsync"/>
    /// correctly finds nothing left held (it was already released to the
    /// provider) and silently no-ops, so the customer would be refunded
    /// while the provider keeps the full payout for a job the platform is
    /// now treating as (partly) unpaid for.
    ///
    /// <paramref name="paymentFundedRefundAmount"/> is this refund's own
    /// gateway-funded settlement, not the full requested amount - the same
    /// scoping <see cref="IEscrowService.ReleaseForRefundAsync"/> uses,
    /// since the provider was never paid anything against the wallet-funded
    /// half to begin with. A booking refunded across several instalments
    /// therefore claws back its matching proportional slice each time,
    /// never more than once against the same money.
    ///
    /// Mirrors <c>NestlyCoinsService.ClawbackProviderCreditAsync</c>'s
    /// lookup-then-debit shape; unlike that one, this runs synchronously
    /// inside the refund's own transaction (like the escrow release right
    /// above it) rather than as a fire-and-forget event handler, since a
    /// partial refund that never drains the booking to fully Refunded would
    /// otherwise never fire the clawback at all.
    /// </summary>
    private async Task ClawBackProviderEarningAsync(Guid bookingId, PaymentTransaction? payment, decimal paymentFundedRefundAmount)
    {
        var credit = await _providerEarningLedgerRepository.FindBySourceAsync(ProviderEarningSourceType.JobCompletion, bookingId);
        if (credit is null || payment is null)
        {
            return;
        }

        decimal clawback = ProviderEarningClawbackCalculator.Compute(payment.Amount, credit.Amount, paymentFundedRefundAmount);
        if (clawback <= 0)
        {
            return;
        }

        var debitResult = await _providerEarningLedgerService.RecordAdjustmentAsync(
            credit.ProviderId,
            new RecordProviderEarningAdjustmentRequest(
                ProviderEarningEntryType.Debit,
                clawback,
                ProviderEarningSourceType.JobCompletionClawback,
                bookingId,
                $"Job-completion earning clawed back - booking {bookingId} refunded {paymentFundedRefundAmount:F2} after payout."));

        if (debitResult.IsFailure)
        {
            // Same reasoning as EscrowReleaseOnCompletionHandler's own
            // failed-credit branch: the refund itself is already legitimate
            // and must not be blocked by a provider-ledger bookkeeping
            // problem (most likely the provider's balance was already too
            // low, e.g. from an earlier penalty) - logged for an admin to
            // reconcile manually, not thrown.
            _logger.LogWarning(
                "Booking {BookingId} refunded {RefundAmount} but clawing back {ClawbackAmount} from provider {ProviderId}'s earning ledger failed: {ErrorCode} {ErrorMessage}.",
                bookingId, paymentFundedRefundAmount, clawback, credit.ProviderId, debitResult.Error.Code, debitResult.Error.Message);
        }
    }

    /// <summary>
    /// Actually moves one settlement's money and marks it Refunded. A
    /// wallet-FUNDED settlement is the reversal of what the customer spent at
    /// checkout, tagged <see cref="WalletSourceType.BookingWalletCreditReversal"/>
    /// so their ledger still tells them apart from a gateway payment that was
    /// refunded into the wallet as goodwill (<see cref="WalletSourceType.Refund"/>) -
    /// the same distinction <see cref="WalletSourceType.ReferralCreditExpiry"/>
    /// keeps from <see cref="WalletSourceType.ReferralReward"/>. Both reference
    /// their own refund row as the source event (SRS 14.5), which is what keeps
    /// the append-only ledger traceable when a booking is refunded in more
    /// than one instalment.
    /// </summary>
    private async Task SettleAsync(RefundTransaction settlement, Booking booking, PaymentTransaction? payment, string reason)
    {
        if (settlement.FundingSource == RefundFundingSource.Wallet)
        {
            await _walletService.CreditAsync(
                booking.CustomerId, settlement.Amount, WalletSourceType.BookingWalletCreditReversal, settlement.Id,
                "Wallet credit reversed - booking refunded");
            settlement.MarkRefunded(gatewayRefundRef: null);
            return;
        }

        if (settlement.Method == RefundMethod.Wallet)
        {
            await _walletService.CreditAsync(booking.CustomerId, settlement.Amount, WalletSourceType.Refund, settlement.Id, reason);
            settlement.MarkRefunded(gatewayRefundRef: null);
            return;
        }

        var successfulAttempt = payment!.Attempts.First(a => a.Status == PaymentAttemptStatus.Success);
        var gatewayResult = await _gateway.RefundAsync(
            new GatewayRefundRequest(successfulAttempt.GatewayPaymentRef!, settlement.Amount, payment.Currency, booking.Id.ToString("N")));

        // The sandbox never fails a refund (see SandboxPaymentGateway's own
        // doc comment), so this branch was unreachable until a real gateway
        // existed - a real one genuinely can reject a refund (insufficient
        // balance, already refunded, bank-side rejection), and blindly
        // calling MarkRefunded regardless would record money as returned to
        // the customer when it was not.
        if (string.Equals(gatewayResult.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            settlement.MarkFailed(gatewayResult.FailureReason ?? "The payment gateway declined the refund.");
        }
        else
        {
            settlement.MarkRefunded(gatewayResult.GatewayRefundId);
        }
    }

    private static RefundTransactionResponse ToResponse(RefundTransaction refund) => new(
        refund.Id, refund.BookingId, refund.PaymentTransactionId, refund.FundingSource, refund.Type, refund.Method,
        refund.Amount, refund.Status, refund.GatewayRefundRef, refund.Reason, refund.CreatedAtUtc, refund.ProcessedAtUtc);
}
