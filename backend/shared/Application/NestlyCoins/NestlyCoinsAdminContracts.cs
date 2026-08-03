using Nestly.Domain.NestlyCoins;

namespace Nestly.Application.NestlyCoins;

public sealed record NestlyCoinsProgramConfigResponse(
    Guid Id,
    NestlyCoinsAudience Audience,
    decimal EarnRatePer100,
    decimal MinimumOrderAmount,
    bool RequireReorder,
    decimal? MaxCoinsPerMonth,
    int ExpiryDays,
    int ClawbackWindowDays,
    bool IsActive,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByAdminUserId);

/// <summary>
/// Creates the audience's config row if it doesn't exist yet, or updates it
/// if it does - the only way an audience's program is ever activated (tasks
/// 200/201 treat "no row for this audience" as "coins disabled for this
/// side", never inventing default values).
/// </summary>
public sealed record NestlyCoinsProgramConfigUpsertRequest(
    decimal EarnRatePer100,
    decimal MinimumOrderAmount,
    bool RequireReorder,
    decimal? MaxCoinsPerMonth,
    int ExpiryDays,
    int ClawbackWindowDays,
    bool IsActive);

/// <summary>
/// Coins issued vs. clawed back for one audience over a date range (docs/
/// NESTLY-COINS.md API SURFACE "coins issued vs. redeemed, program cost over
/// a date range", mirrors Referral's funnel/cost report). Scoped to what's
/// honestly computable from the existing ledgers: "issued" and "clawed back"
/// are both a real ledger SourceType sum; a true "redeemed" (spent) figure
/// would need per-entry consumption tracking the provider earning ledger
/// doesn't have (see NestlyCoinsService's doc comments), so this reports the
/// net cost instead of a redemption rate.
/// </summary>
public sealed record NestlyCoinsReportResponse(
    NestlyCoinsAudience Audience,
    DateTime FromUtc,
    DateTime ToUtc,
    decimal TotalIssued,
    decimal TotalClawedBack,
    decimal NetOutstanding);
