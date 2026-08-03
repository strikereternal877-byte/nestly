using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>Direction of a <see cref="ProviderEarningLedgerEntry"/>, mirroring <see cref="WalletEntryType"/>.</summary>
public enum ProviderEarningEntryType
{
    Credit,
    Debit
}

/// <summary>
/// The kind of event that produced a <see cref="ProviderEarningLedgerEntry"/>
/// (PROVIDER.md Financial Domain: "credit per completed job ... debit for
/// penalties"). Paired with <see cref="ProviderEarningLedgerEntry.SourceReferenceId"/>
/// for the concrete row, mirroring <see cref="WalletSourceType"/>.
/// </summary>
public enum ProviderEarningSourceType
{
    /// <summary>Credited when a booking assigned to this provider is completed. SourceReferenceId is the booking id.</summary>
    JobCompletion,

    /// <summary>Debited as a penalty (e.g. a late rejection or a quality issue). SourceReferenceId is the booking id when one applies.</summary>
    Penalty,

    /// <summary>Manual admin correction, no source aggregate.</summary>
    ManualAdjustment,

    /// <summary>Credited from a qualifying job's Nestly Coins reward (docs/NESTLY-COINS.md, task 201). SourceReferenceId is the completed Booking's id.</summary>
    NestlyCoinsReward,

    /// <summary>Debited to reverse a Nestly Coins reward when its crediting booking is cancelled/refunded within the program's ClawbackWindowDays (docs/NESTLY-COINS.md FRAUD/ABUSE PREVENTION, task 201) - distinct from the credit's own NestlyCoinsReward tag, mirroring WalletSourceType's equivalent separation. SourceReferenceId is the same Booking's id.</summary>
    NestlyCoinsClawback
}

/// <summary>
/// One append-only entry in a provider's earning ledger (PROVIDER.md Financial
/// Domain "provider_earning_ledger", task 148). Mirrors <see cref="WalletLedgerEntry"/>
/// exactly: the running balance is derived by reading the latest entry's
/// <see cref="BalanceAfter"/>, never re-derived on read, and entries are
/// never updated or deleted after creation - the repository intentionally
/// exposes no Update/Delete method.
/// </summary>
public class ProviderEarningLedgerEntry : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public ProviderEarningEntryType EntryType { get; private set; }

    /// <summary>Always positive; direction comes from <see cref="EntryType"/>.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Running earnings balance immediately after this entry - an audit snapshot, not re-derived on read.</summary>
    public decimal BalanceAfter { get; private set; }

    public ProviderEarningSourceType SourceType { get; private set; }

    /// <summary>The id of the source aggregate (typically a Booking) that produced this entry, when one exists.</summary>
    public Guid? SourceReferenceId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    protected ProviderEarningLedgerEntry() { }

    public ProviderEarningLedgerEntry(
        Guid id, Guid providerId, ProviderEarningEntryType entryType, decimal amount, decimal balanceAfter,
        ProviderEarningSourceType sourceType, Guid? sourceReferenceId, string description)
        : base(id)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Earning ledger entry amount must be positive.");
        }

        if (balanceAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(balanceAfter), "Earning balance cannot go negative.");
        }

        ProviderId = providerId;
        EntryType = entryType;
        Amount = amount;
        BalanceAfter = balanceAfter;
        SourceType = sourceType;
        SourceReferenceId = sourceReferenceId;
        Description = description ?? throw new ArgumentException("Description is required.", nameof(description));
        CreatedAtUtc = DateTime.UtcNow;
    }
}
