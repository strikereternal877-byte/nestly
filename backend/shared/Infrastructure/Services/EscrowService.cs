using Nestly.Application.Escrow;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Platform escrow hold/release bookkeeping (task 158). A single
/// platform-owned running balance, mirroring <see cref="WalletService"/>'s
/// "no separate mutable balance column" design - every entry both records
/// what happened and snapshots the resulting balance.
/// </summary>
public class EscrowService : IEscrowService
{
    private readonly IPlatformEscrowLedgerRepository _repository;

    public EscrowService(IPlatformEscrowLedgerRepository repository)
    {
        _repository = repository;
    }

    public async Task HoldAsync(Guid bookingId, Guid paymentTransactionId, decimal amount)
    {
        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        var entry = new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Hold, amount, currentBalance + amount,
            EscrowSourceType.PaymentConfirmed, paymentTransactionId,
            "Payment confirmed - funds held in platform escrow pending fulfilment.");

        await _repository.AddAsync(entry);
    }

    public async Task HoldWalletCreditAsync(Guid bookingId, decimal amount)
    {
        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        var entry = new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Hold, amount, currentBalance + amount,
            EscrowSourceType.WalletCreditConfirmed, sourceReferenceId: null,
            "Booking confirmed - wallet-funded share held in platform escrow pending fulfilment.");

        await _repository.AddAsync(entry);
    }

    public async Task<EscrowReleaseResult?> ReleaseToProviderAsync(Guid bookingId, Guid? paymentTransactionId, Guid? providerId, decimal commissionAmount)
    {
        decimal held = await GetHeldBalanceAsync(bookingId);
        if (held <= 0)
        {
            // Already released (to a provider or via refund), or never held.
            return null;
        }

        if (commissionAmount < 0 || commissionAmount > held)
        {
            throw new ArgumentOutOfRangeException(nameof(commissionAmount), "Commission amount must be between 0 and the booking's held escrow balance.");
        }

        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        var entry = new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Release, held, currentBalance - held,
            EscrowSourceType.BookingCompleted, paymentTransactionId,
            "Booking completed - escrow released to provider net of commission.",
            providerId, commissionAmount);

        // The held<=0 check above and this insert are not atomic - two
        // concurrent completions of the same booking can both read "held"
        // before either commits. TryAddCompletionReleaseAsync's filtered
        // unique index is the real, database-level guard: whichever of the
        // two loses the race gets false here and reports the same "already
        // released" outcome the held<=0 check above reports for the more
        // common sequential case.
        bool inserted = await _repository.TryAddCompletionReleaseAsync(entry);
        if (!inserted)
        {
            return null;
        }

        return new EscrowReleaseResult(held, commissionAmount, held - commissionAmount);
    }

    public async Task ReleaseForRefundAsync(Guid bookingId, Guid refundTransactionId, decimal refundAmount)
    {
        decimal held = await GetHeldBalanceAsync(bookingId);
        if (held <= 0)
        {
            // Nothing left in escrow for this booking - it was already
            // released to its provider before this refund was issued.
            return;
        }

        decimal releaseAmount = Math.Min(held, refundAmount);
        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        var entry = new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Release, releaseAmount, currentBalance - releaseAmount,
            EscrowSourceType.RefundIssued, refundTransactionId,
            "Refund issued - escrow released back out (not paid to a provider).");

        await _repository.AddAsync(entry);
    }

    public async Task ReleaseRetainedFeeAsync(Guid bookingId, Guid cancellationId, decimal feeAmount)
    {
        decimal held = await GetHeldBalanceAsync(bookingId);
        if (held <= 0)
        {
            return;
        }

        decimal releaseAmount = Math.Min(held, feeAmount);
        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        var entry = new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Release, releaseAmount, currentBalance - releaseAmount,
            EscrowSourceType.CancellationFeeRetained, cancellationId,
            "Late-cancellation fee retained as platform revenue.");

        await _repository.AddAsync(entry);
    }

    public async Task RecordRescheduleFeeAsync(Guid bookingId, Guid rescheduleId, decimal feeAmount)
    {
        if (feeAmount <= 0)
        {
            return;
        }

        decimal currentBalance = (await _repository.GetLatestAsync())?.BalanceAfter ?? 0m;
        await _repository.AddAsync(new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Hold, feeAmount, currentBalance + feeAmount,
            EscrowSourceType.RescheduleFeeCollected, rescheduleId,
            "Late-reschedule fee taken from the customer's wallet."));
        await _repository.AddAsync(new PlatformEscrowLedger(
            Guid.NewGuid(), bookingId, EscrowEntryType.Release, feeAmount, currentBalance,
            EscrowSourceType.RescheduleFeeCollected, rescheduleId,
            "Late-reschedule fee recognised as platform revenue."));
    }

    public async Task<decimal> GetHeldBalanceAsync(Guid bookingId)
    {
        var entries = await _repository.ListByBookingAsync(bookingId);
        decimal held = 0m;
        foreach (var entry in entries)
        {
            held += entry.EntryType == EscrowEntryType.Hold ? entry.Amount : -entry.Amount;
        }

        return held;
    }
}
