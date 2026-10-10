namespace Nestly.Domain;

/// <summary>
/// The kind of event that produced a <see cref="PlatformEscrowLedger"/> entry
/// (task 158) - mirrors <see cref="WalletSourceType"/>'s traceability role.
/// Paired with <see cref="PlatformEscrowLedger.SourceReferenceId"/> for the
/// concrete row.
/// </summary>
public enum EscrowSourceType
{
    /// <summary>Hold: a customer's payment succeeded (SourceReferenceId is the PaymentTransactionId).</summary>
    PaymentConfirmed,

    /// <summary>Release: the booking reached BookingStatus.Completed and its hold was paid out to the provider net of commission (SourceReferenceId is the PaymentTransactionId).</summary>
    BookingCompleted,

    /// <summary>Release: a refund was issued against the booking's payment, so its (remaining) hold is released back out instead of to a provider (SourceReferenceId is the RefundTransactionId).</summary>
    RefundIssued,

    /// <summary>
    /// Hold: the wallet-funded share of a booking's price, moved into escrow
    /// once the booking reaches Confirmed - the same moment <see cref="PaymentConfirmed"/>
    /// holds the gateway-funded share, but keyed by the booking itself since
    /// a wallet debit has no PaymentTransaction of its own (SourceReferenceId
    /// is null). Released the same way as any other hold - to the provider on
    /// completion, or back out on refund; <c>IPlatformEscrowLedgerRepository.ListByBookingAsync</c>
    /// sums every entry for a booking regardless of source type.
    /// </summary>
    WalletCreditConfirmed,

    /// <summary>
    /// Release: a late-cancellation fee (<see cref="CancellationFeeCalculator"/>)
    /// is kept as platform revenue rather than refunded - <see cref="RefundIssued"/>
    /// only ever releases the refunded amount, never the fee withheld from
    /// it, and a cancelled booking never reaches Completed to let
    /// <see cref="BookingCompleted"/> claim the remainder either. Without a
    /// release for it, every cancellation fee ever charged stayed "held" in
    /// escrow forever - not lost (the customer was never refunded that
    /// share), but permanently misrepresenting the platform's outstanding
    /// escrow liability and never formally recognized as revenue.
    /// SourceReferenceId is the BookingCancellation's id.
    /// </summary>
    CancellationFeeRetained,

    /// <summary>
    /// A late-reschedule fee taken from the customer's wallet and recognised as platform revenue in the same breath: a
    /// hold for the amount and a release for the same amount, both carrying this source, so the booking's own held
    /// balance - what completion pays the provider out of - is left exactly as it was.
    /// SourceReferenceId is the BookingReschedule's id.
    /// </summary>
    RescheduleFeeCollected
}
