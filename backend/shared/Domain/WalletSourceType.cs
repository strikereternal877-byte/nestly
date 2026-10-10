namespace Nestly.Domain;

/// <summary>
/// The kind of event that produced a <see cref="WalletLedgerEntry"/> (SRS
/// 14.5 - "every credit/debit must reference source event"). Paired with
/// <see cref="WalletLedgerEntry.SourceReferenceId"/> for the concrete row.
/// </summary>
public enum WalletSourceType
{
    /// <summary>Credited from a <see cref="RefundTransaction"/> settled to wallet.</summary>
    Refund,

    /// <summary>Promotional/goodwill credit with no source aggregate (SourceReferenceId is null).</summary>
    PromotionalCredit,

    /// <summary>Manual adjustment (support/admin correction), no source aggregate.</summary>
    ManualAdjustment,

    /// <summary>Credited from a qualifying <see cref="Referral"/>'s reward disbursement (REFERRAL.md, task 165). SourceReferenceId is the Referral id.</summary>
    ReferralReward,

    /// <summary>Credited from a referrer crossing a <see cref="ReferralMilestone"/> threshold (task 174). SourceReferenceId is the ReferralMilestone id.</summary>
    ReferralMilestoneBonus,

    /// <summary>Debited by the expiry sweep (task 175) for the unconsumed portion of an expiring wallet credit. SourceReferenceId is the expiring WalletLedgerEntry's id.</summary>
    ReferralCreditExpiry,

    /// <summary>Credited from a qualifying order's Nestly Coins reward (docs/NESTLY-COINS.md, task 201). SourceReferenceId is the completed Booking's id.</summary>
    NestlyCoinsReward,

    /// <summary>Debited to reverse a Nestly Coins reward when its crediting booking is cancelled/refunded within the program's ClawbackWindowDays (docs/NESTLY-COINS.md FRAUD/ABUSE PREVENTION, task 201) - distinct from the credit's own NestlyCoinsReward tag, mirroring ReferralCreditExpiry's separation from ReferralReward. SourceReferenceId is the same Booking's id.</summary>
    NestlyCoinsClawback,

    /// <summary>Debited when a customer applies wallet balance at checkout (SRS 11.7.2, task 310). SourceReferenceId is the Booking's id.</summary>
    BookingWalletCredit,

    /// <summary>
    /// Credited back when a booking that consumed wallet balance is refunded
    /// (task 310) - the wallet-side counterpart of the reversal RefundService
    /// already performs for the escrow hold. SourceReferenceId is the
    /// wallet-funded <see cref="RefundTransaction"/> that reversed it (task
    /// 356; it was the Booking's id while the reversal was an untracked side
    /// effect of fully refunding the payment, which could not say WHICH of a
    /// booking's refunds handed the balance back).
    /// </summary>
    BookingWalletCreditReversal,

    /// <summary>
    /// Credited because the customer added their own money through the payment gateway (<see cref="WalletTopUp"/>).
    /// SourceReferenceId is the WalletTopUp's id. Unlike the promotional and referral credits this is the
    /// customer's own cash: it never expires.
    /// </summary>
    TopUp,

    /// <summary>
    /// Debited as the late-reschedule fee when a customer moves a booking inside the late-reschedule window
    /// (<see cref="BookingReschedule.FeeCollectedAmount"/>). SourceReferenceId is the <see cref="BookingReschedule"/>'s id.
    /// </summary>
    RescheduleFee,

    /// <summary>
    /// Credited back when a late-reschedule fee was taken but the reschedule then failed to be saved, so the customer is
    /// not charged for a move that never happened. SourceReferenceId is the id the fee debit carried.
    /// </summary>
    RescheduleFeeReversal
}
