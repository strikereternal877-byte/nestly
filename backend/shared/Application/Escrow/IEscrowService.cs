namespace Nestly.Application.Escrow;

/// <summary>The outcome of releasing a booking's held escrow to its provider.</summary>
public record EscrowReleaseResult(decimal GrossAmount, decimal CommissionAmount, decimal NetAmountToProvider);

/// <summary>
/// The platform-owned escrow holding account (task 158): a customer's
/// payment is held here from confirmation until the booking is either
/// completed (released to the provider net of commission) or refunded
/// (released back out, un-paid). Mirrors the append-only bookkeeping
/// <see cref="Nestly.Application.Wallet.IWalletService"/> already uses for
/// the customer wallet - this is not a payout system, just the hold/release
/// ledger entries.
/// </summary>
public interface IEscrowService
{
    /// <summary>
    /// Moves a just-confirmed payment's amount into escrow (task 158).
    /// Called once per booking, right after its payment succeeds - reaching
    /// this twice for the same payment is already prevented upstream by the
    /// webhook handler's own attempt-status idempotency guard.
    /// </summary>
    Task HoldAsync(Guid bookingId, Guid paymentTransactionId, decimal amount);

    /// <summary>
    /// Moves a booking's wallet-funded share into escrow, the moment the
    /// booking reaches Confirmed - the wallet-side counterpart to
    /// <see cref="HoldAsync"/>, since a wallet debit has no PaymentTransaction
    /// of its own to key a hold on. See <see cref="EscrowSourceType.WalletCreditConfirmed"/>.
    /// </summary>
    Task HoldWalletCreditAsync(Guid bookingId, decimal amount);

    /// <summary>
    /// Releases a booking's currently-held escrow to its provider, net of
    /// <paramref name="commissionAmount"/> (task 157's already-recorded
    /// figure, reused here rather than recomputed, so the settlement and the
    /// escrow ledger never disagree). <paramref name="providerId"/> is a
    /// placeholder (task 158) - there is no Provider identity in the domain
    /// yet, so it may be null. <paramref name="paymentTransactionId"/> is
    /// null for a booking held entirely through <see cref="HoldWalletCreditAsync"/>
    /// (no PaymentTransaction exists to reference). Returns null (a no-op) if
    /// nothing is currently held for the booking - already released, or
    /// never held.
    /// </summary>
    Task<EscrowReleaseResult?> ReleaseToProviderAsync(Guid bookingId, Guid? paymentTransactionId, Guid? providerId, decimal commissionAmount);

    /// <summary>
    /// Releases up to <paramref name="refundAmount"/> of a booking's
    /// currently-held escrow back out, because a refund was issued for it
    /// (task 158's refund-path handling) - the refund's actual money
    /// movement is unchanged (still <see cref="Nestly.Application.Wallet.IWalletService"/>
    /// or the gateway); this only keeps the escrow bookkeeping honest. A
    /// no-op if nothing remains held (e.g. the booking was already released
    /// to its provider on completion before this refund was issued).
    /// </summary>
    Task ReleaseForRefundAsync(Guid bookingId, Guid refundTransactionId, decimal refundAmount);

    /// <summary>
    /// Releases up to <paramref name="feeAmount"/> of a booking's currently-held
    /// escrow, kept as platform revenue rather than refunded - the
    /// counterpart to <see cref="ReleaseForRefundAsync"/> for whatever a
    /// late-cancellation fee withholds from the refund it issues alongside.
    /// See <see cref="EscrowSourceType.CancellationFeeRetained"/>. A no-op
    /// if nothing remains held.
    /// </summary>
    Task ReleaseRetainedFeeAsync(Guid bookingId, Guid cancellationId, decimal feeAmount);

    /// <summary>
    /// Books a late-reschedule fee that was just taken from the customer's wallet as platform revenue: a hold and a
    /// release of the same amount (<see cref="EscrowSourceType.RescheduleFeeCollected"/>), leaving the booking's held
    /// balance - and so what completion pays the provider - untouched. No-op for a zero fee.
    /// </summary>
    Task RecordRescheduleFeeAsync(Guid bookingId, Guid rescheduleId, decimal feeAmount);

    /// <summary>The sum of a booking's Hold entries minus its Release entries - what remains held right now.</summary>
    Task<decimal> GetHeldBalanceAsync(Guid bookingId);
}
