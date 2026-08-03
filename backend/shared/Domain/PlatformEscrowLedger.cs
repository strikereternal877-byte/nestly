using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// One append-only entry in the platform's escrow ledger (task 158): a
/// single platform-owned holding account, not one per customer or booking -
/// mirrors <see cref="WalletLedgerEntry"/>'s "no separate mutable balance
/// column" design. The running balance is derived by reading the latest
/// entry's <see cref="BalanceAfter"/>; a booking's own currently-held amount
/// is derived by summing its entries (see <c>IPlatformEscrowLedgerRepository.ListByBookingAsync</c>).
/// Entries are never updated or deleted after creation.
/// </summary>
public class PlatformEscrowLedger : Entity<Guid>
{
    public Guid BookingId { get; private set; }

    public EscrowEntryType EntryType { get; private set; }

    /// <summary>Always positive; direction comes from <see cref="EntryType"/>.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Running platform-wide escrow balance immediately after this entry - an audit snapshot, not re-derived on read.</summary>
    public decimal BalanceAfter { get; private set; }

    public EscrowSourceType SourceType { get; private set; }

    /// <summary>The id of the source aggregate (a PaymentTransaction or RefundTransaction) that produced this entry.</summary>
    public Guid? SourceReferenceId { get; private set; }

    /// <summary>
    /// Release-target placeholder (task 158): there is no Provider identity
    /// in the domain yet (deferred to Phase 8/Provider), so a Release entry
    /// for BookingCompleted just records who it would be paid out to once
    /// that concept exists. Null on Hold entries, and on a RefundIssued
    /// Release (nothing was paid to a provider).
    /// </summary>
    public Guid? ProviderId { get; private set; }

    /// <summary>
    /// The commission withheld from a BookingCompleted Release (task 157's
    /// calculation, already recorded on the booking's PaymentTransaction at
    /// confirmation time and reused here for consistency). Null on Hold
    /// entries and on a RefundIssued Release.
    /// </summary>
    public decimal? CommissionAmount { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    protected PlatformEscrowLedger() { }

    public PlatformEscrowLedger(
        Guid id,
        Guid bookingId,
        EscrowEntryType entryType,
        decimal amount,
        decimal balanceAfter,
        EscrowSourceType sourceType,
        Guid? sourceReferenceId,
        string description,
        Guid? providerId = null,
        decimal? commissionAmount = null)
        : base(id)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Escrow entry amount must be positive.");
        }

        if (balanceAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(balanceAfter), "Escrow balance cannot go negative.");
        }

        BookingId = bookingId;
        EntryType = entryType;
        Amount = amount;
        BalanceAfter = balanceAfter;
        SourceType = sourceType;
        SourceReferenceId = sourceReferenceId;
        ProviderId = providerId;
        CommissionAmount = commissionAmount;
        Description = description ?? throw new ArgumentException("Description is required.", nameof(description));
        CreatedAtUtc = DateTime.UtcNow;
    }
}
