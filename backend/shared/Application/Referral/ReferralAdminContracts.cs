using Nestly.Domain;

namespace Nestly.Application.Referral;

/// <summary>Admin list/search filters (task 170, REFERRAL.md "Referral list/detail: filter by status, search by customer").</summary>
public record ReferralAdminSearchRequest(
    ReferralStatus? Status,
    bool? IsFraudFlagged,
    string? CustomerSearch,
    int Page = 1,
    int PageSize = 20);

public record ReferralAdminListItemResponse(
    Guid Id,
    Guid ReferrerCustomerId,
    string ReferrerName,
    Guid RefereeCustomerId,
    string RefereeName,
    ReferralStatus Status,
    bool IsFraudFlagged,
    DateTime RegisteredAtUtc,
    DateTime? RewardedAtUtc);

public record ReferralAdminSearchResponse(IReadOnlyList<ReferralAdminListItemResponse> Items, int TotalCount, int Page, int PageSize);

/// <summary>Admin detail view (task 170).</summary>
public record ReferralAdminDetailResponse(
    Guid Id,
    Guid ReferrerCustomerId,
    string ReferrerName,
    string ReferrerMobile,
    Guid RefereeCustomerId,
    string RefereeName,
    string RefereeMobile,
    string ReferralCodeUsed,
    ReferralStatus Status,
    Guid? QualifyingBookingId,
    ReferralRewardType ReferrerRewardType,
    decimal ReferrerRewardValue,
    ReferralRewardType RefereeRewardType,
    decimal RefereeRewardValue,
    decimal MinQualifyingOrderAmount,
    Guid? ReferrerWalletEntryId,
    Guid? ReferrerCouponId,
    Guid? RefereeWalletEntryId,
    Guid? RefereeCouponId,
    DateTime RegisteredAtUtc,
    DateTime? QualifiedAtUtc,
    DateTime? RewardedAtUtc,
    DateTime ExpiresAtUtc,
    bool IsFraudFlagged,
    string? FraudReviewNote,
    Guid? FraudReviewedByAdminUserId,
    DateTime? FraudReviewedAtUtc);

/// <summary>
/// Task 171's funnel report. <see cref="InvitedCount"/> equals
/// <see cref="RegisteredCount"/> deliberately - this codebase has no
/// invite-link-click tracking anywhere (a referral row is only ever created
/// at registration, task 163), so "invited but never registered" isn't data
/// that exists to report; reporting a fabricated distinct number would be
/// worse than reporting the honest one. Both stages are cohort-based: of the
/// referrals that registered within [FromUtc, ToUtc], how many have since
/// progressed further (as of now, not "within the range").
/// </summary>
public record ReferralFunnelReportResponse(
    int InvitedCount,
    int RegisteredCount,
    int QualifiedCount,
    int RewardedCount,
    DateTime? FromUtc,
    DateTime? ToUtc);

/// <summary>Task 171's total program cost report - every reward (per-referral and milestone bonus) actually disbursed within the range, split wallet-credit vs coupon.</summary>
public record ReferralCostReportResponse(
    decimal TotalWalletCreditCost,
    decimal TotalCouponCost,
    decimal TotalCost,
    int RewardedReferralCount,
    int MilestoneBonusCount,
    DateTime? FromUtc,
    DateTime? ToUtc);

/// <summary>Body for the fraud approve/reject actions (task 170, task 166's <see cref="IReferralFraudReviewService"/>). Note is optional context recorded on the audit trail.</summary>
public record FraudReviewActionRequest(string? Note);
