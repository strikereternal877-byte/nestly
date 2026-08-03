using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain.NestlyCoins;

/// <summary>
/// Admin-editable Nestly Coins program settings (docs/NESTLY-COINS.md "DATA
/// MODEL", task 200), one mutable row per <see cref="NestlyCoinsAudience"/>
/// (GUIDELINES #5 - customer and provider are independently configured, never
/// shared). Same single-mutable-row shape as <see cref="ReferralProgramConfig"/>,
/// just keyed by audience instead of being a true singleton.
///
/// Deliberately has no <c>effective_from</c>/<c>effective_to</c> versioning
/// despite docs/NESTLY-COINS.md's data model section naming those columns -
/// same reasoning <see cref="ReferralProgramConfig"/>'s doc comment already
/// established for this exact situation: the amount actually credited to a
/// <c>WalletLedgerEntry</c>/<c>ProviderEarningLedgerEntry</c> is computed once
/// at credit time and stored permanently on that entry, so a later admin
/// change to this config can never retroactively alter a credit already
/// issued. A versioned config would achieve the same non-retroactivity with
/// strictly more moving parts for no behavioural gain.
/// </summary>
public class NestlyCoinsProgramConfig : Entity<Guid>
{
    public NestlyCoinsAudience Audience { get; private set; }

    /// <summary>Wallet/earning-ledger credit issued per ₹100 of qualifying order value (GUIDELINES #1). Coins ARE currency - this is a cashback-style rate, not a separate points-to-rupee exchange rate.</summary>
    public decimal EarnRatePer100 { get; private set; }

    /// <summary>Orders below this amount accrue no coins at all (GUIDELINES #1, prevents gaming via many tiny orders).</summary>
    public decimal MinimumOrderAmount { get; private set; }

    /// <summary>True (the shipped default, task 199) = only a 2nd+ completed order qualifies. False = every order qualifies, including the first (GUIDELINES #2).</summary>
    public bool RequireReorder { get; private set; }

    /// <summary>Fraud cap: max coins one customer/provider can earn per calendar month. Null = unlimited (FRAUD/ABUSE PREVENTION).</summary>
    public decimal? MaxCoinsPerMonth { get; private set; }

    /// <summary>Days after crediting before an unspent coin credit expires (GUIDELINES #3 - enforced via the existing wallet FIFO consumption/expiry model, see task 199's resolution).</summary>
    public int ExpiryDays { get; private set; }

    /// <summary>If the crediting order is cancelled/refunded within this many days of completion, the credit is reversed via an explicit debit (FRAUD/ABUSE PREVENTION) - zero means only a same-day cancellation reverses it.</summary>
    public int ClawbackWindowDays { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByAdminUserId { get; private set; }

    protected NestlyCoinsProgramConfig() { }

    public NestlyCoinsProgramConfig(
        Guid id,
        NestlyCoinsAudience audience,
        decimal earnRatePer100,
        decimal minimumOrderAmount,
        bool requireReorder,
        decimal? maxCoinsPerMonth,
        int expiryDays,
        int clawbackWindowDays,
        bool isActive)
        : base(id)
    {
        Validate(earnRatePer100, minimumOrderAmount, maxCoinsPerMonth, expiryDays, clawbackWindowDays);

        Audience = audience;
        EarnRatePer100 = earnRatePer100;
        MinimumOrderAmount = minimumOrderAmount;
        RequireReorder = requireReorder;
        MaxCoinsPerMonth = maxCoinsPerMonth;
        ExpiryDays = expiryDays;
        ClawbackWindowDays = clawbackWindowDays;
        IsActive = isActive;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Audience is deliberately not updatable - it identifies which row this is, not a setting on it (same reasoning as a table's key never being part of its own update).</summary>
    public void Update(
        decimal earnRatePer100,
        decimal minimumOrderAmount,
        bool requireReorder,
        decimal? maxCoinsPerMonth,
        int expiryDays,
        int clawbackWindowDays,
        bool isActive,
        Guid updatedByAdminUserId)
    {
        Validate(earnRatePer100, minimumOrderAmount, maxCoinsPerMonth, expiryDays, clawbackWindowDays);

        EarnRatePer100 = earnRatePer100;
        MinimumOrderAmount = minimumOrderAmount;
        RequireReorder = requireReorder;
        MaxCoinsPerMonth = maxCoinsPerMonth;
        ExpiryDays = expiryDays;
        ClawbackWindowDays = clawbackWindowDays;
        IsActive = isActive;
        UpdatedAtUtc = DateTime.UtcNow;
        UpdatedByAdminUserId = updatedByAdminUserId;
    }

    private static void Validate(
        decimal earnRatePer100, decimal minimumOrderAmount, decimal? maxCoinsPerMonth,
        int expiryDays, int clawbackWindowDays)
    {
        if (earnRatePer100 <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(earnRatePer100), "Earn rate must be positive.");
        }

        if (minimumOrderAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumOrderAmount), "Minimum order amount cannot be negative.");
        }

        if (maxCoinsPerMonth is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCoinsPerMonth), "Max coins per month must be positive when set.");
        }

        if (expiryDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expiryDays), "Expiry days must be positive.");
        }

        if (clawbackWindowDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clawbackWindowDays), "Clawback window days cannot be negative.");
        }
    }
}
