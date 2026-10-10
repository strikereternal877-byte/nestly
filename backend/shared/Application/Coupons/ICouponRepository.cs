using Nestly.Domain;

namespace Nestly.Application.Coupons;

public interface ICouponRepository
{
    Task<Coupon?> GetByIdAsync(Guid id);

    /// <summary>Codes are normalized to uppercase at construction (see <see cref="Coupon"/>) - lookups must match that normalization.</summary>
    Task<Coupon?> GetByCodeAsync(string code);

    /// <summary>Codes for a set of coupon ids in one round trip (task 258) - the admin customer-detail screen renders a code per redemption row. Ids with no matching coupon are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Atomically increments the coupon's redemption counter, but only if
    /// both its overall usage cap and <paramref name="customerId"/>'s
    /// per-customer cap are still unmet (either cap unlimited = null) - a
    /// single conditional UPDATE, not a read-then-write, so neither cap
    /// (task 72c, NESTLY-009) can be exceeded by concurrent bookings racing
    /// for the last redemption, whether they're racing for the same
    /// customer's single-use allowance or the campaign-wide total. Returns
    /// false (no state change) if either cap had already been reached.
    /// </summary>
    Task<bool> TryReserveRedemptionAsync(Guid couponId, Guid customerId);

    /// <summary>
    /// Atomically releases one reservation <see cref="TryReserveRedemptionAsync"/>
    /// previously granted - the booking it was reserved for never actually
    /// completed (expired unpaid, or was cancelled before payment ever
    /// settled), so the usage it would have consumed never really happened.
    /// Symmetric with the reservation and floor-guarded at zero so this can
    /// never run either counter negative, mirroring the exact compensation
    /// <see cref="TryReserveRedemptionAsync"/> itself already performs when
    /// the global cap loses the race after the per-customer claim succeeded.
    /// </summary>
    Task ReleaseRedemptionAsync(Guid couponId, Guid customerId);

    // ---- Admin management (SRS 12.12.1, task 118) ----

    Task AddAsync(Coupon coupon);

    Task UpdateAsync(Coupon coupon);

    /// <summary>Whether a coupon already exists with this code (normalized the same way as <see cref="GetByCodeAsync"/>) - used to reject duplicate codes on create.</summary>
    Task<bool> CodeExistsAsync(string code);

    /// <summary>Filtered, paginated admin coupon list (SRS 12.12.1's "manage coupons" screen).</summary>
    Task<CouponSearchResult> SearchAsync(CouponSearchFilter filter);
}
