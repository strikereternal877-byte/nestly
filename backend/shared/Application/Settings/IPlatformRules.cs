namespace Nestly.Application.Settings;

/// <summary>
/// The platform-wide rules an admin has saved in Settings for the groups that have no configuration fallback of their own
/// (booking, slot, tax, wallet, coupon). Every engine that applies one of them asks this, so a saved value takes effect for
/// the next request with no restart or deploy.
///
/// <para>
/// <b>A result of <c>null</c> means "no admin has saved this group", and the engine then does exactly what it did before
/// Settings could drive it.</b> The seeded row says nothing about what the platform is meant to enforce - several of its
/// seeded values (a 30-day booking horizon, a 2-hour lead time) are stricter than anything enforced today - so a seed is
/// never treated as a rule; only a group an admin has saved is. Saving a group saves every field in it, so an admin who
/// saves the Booking group is choosing all of its values, and the Settings page says so.
/// </para>
///
/// <para>
/// Where a more specific setting already exists (a city's slot booking policy, a city's pricing policy), the rules here are
/// the platform default or floor underneath it: limits only ever tighten, they never loosen what a city already sets.
/// Within one request the answer is read once (the provider is scoped), so a date range of 31 days costs one lookup, not 31.
/// </para>
/// </summary>
public interface IPlatformRules
{
    Task<BookingSettings?> GetBookingAsync(CancellationToken cancellationToken = default);

    Task<SlotSettings?> GetSlotAsync(CancellationToken cancellationToken = default);

    Task<TaxSettings?> GetTaxAsync(CancellationToken cancellationToken = default);

    Task<WalletSettings?> GetWalletAsync(CancellationToken cancellationToken = default);

    Task<CouponSettings?> GetCouponAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// No admin-saved rules for any group - what an engine uses when it has not been given a real <see cref="IPlatformRules"/>
/// (a unit test that is not about Settings), and so the behaviour every engine had before Settings could drive it.
/// </summary>
public sealed class NoPlatformRules : IPlatformRules
{
    public static readonly NoPlatformRules Instance = new();

    private NoPlatformRules() { }

    public Task<BookingSettings?> GetBookingAsync(CancellationToken cancellationToken = default) => Task.FromResult<BookingSettings?>(null);

    public Task<SlotSettings?> GetSlotAsync(CancellationToken cancellationToken = default) => Task.FromResult<SlotSettings?>(null);

    public Task<TaxSettings?> GetTaxAsync(CancellationToken cancellationToken = default) => Task.FromResult<TaxSettings?>(null);

    public Task<WalletSettings?> GetWalletAsync(CancellationToken cancellationToken = default) => Task.FromResult<WalletSettings?>(null);

    public Task<CouponSettings?> GetCouponAsync(CancellationToken cancellationToken = default) => Task.FromResult<CouponSettings?>(null);
}
