using Nestly.Domain;

namespace Nestly.Application.Escrow;

public interface IPlatformEscrowLedgerRepository
{
    /// <summary>Entries are append-only - there is deliberately no Update/Delete method (task 158, mirrors SRS 14.5's wallet rule).</summary>
    Task AddAsync(PlatformEscrowLedger entry);

    /// <summary>
    /// Adds a BookingCompleted Release entry, but treats losing the race
    /// against a concurrent completion of the same booking as a clean
    /// no-op (returns false) rather than letting the unique constraint
    /// violation (<c>PlatformEscrowLedgerConfiguration</c>'s filtered index)
    /// escape as an unhandled exception - the same
    /// "structurally impossible, so a race is a no-op, not an error" shape
    /// <c>ICouponRepository.TryReserveRedemptionAsync</c> and friends use for
    /// their own atomic conditional UPDATEs. This is the append-only-ledger
    /// equivalent: there is no row to conditionally UPDATE, so the database
    /// constraint itself is the atomic guard, and this method is what turns
    /// its rejection into the same "false = lost the race" contract.
    /// </summary>
    Task<bool> TryAddCompletionReleaseAsync(PlatformEscrowLedger entry);

    /// <summary>The most recent entry across the whole platform, whose BalanceAfter is the platform's current escrow balance (null when there has never been any escrow activity).</summary>
    Task<PlatformEscrowLedger?> GetLatestAsync();

    /// <summary>Every entry recorded against one booking, oldest first - used to derive that booking's currently-held (un-released) balance.</summary>
    Task<IReadOnlyList<PlatformEscrowLedger>> ListByBookingAsync(Guid bookingId);
}
