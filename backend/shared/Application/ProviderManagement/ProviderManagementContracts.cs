using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

// ---- CRUD (task 150a) ----

/// <summary>
/// Search/filter criteria for the admin provider list (mirrors
/// <c>CustomerSearchFilter</c>). <see cref="CityId"/> matches a provider with
/// an active <see cref="ProviderServiceArea"/> covering that city (task 371)
/// - ops finding "which providers serve city X" without opening every
/// provider's detail page. <see cref="CreatedFromUtc"/>/<see cref="CreatedToUtc"/>
/// (Provider Onboarding Overview dashboard) bound <see cref="Provider.CreatedAt"/>
/// the same inclusive way <c>BookingSearchFilter.CreatedFromUtc/CreatedToUtc</c>
/// already does - this is what lets a click on one of that dashboard's tiles
/// land here filtered to exactly the cohort ("registered on this day") it
/// summarized.
/// </summary>
public sealed record ProviderSearchFilter(
    string? Name,
    string? Phone,
    ProviderStatus? Status,
    ProviderOnboardingStatus? OnboardingStatus,
    Guid? CityId,
    int Page,
    int PageSize,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null);

public sealed record ProviderSearchResult(IReadOnlyList<Provider> Rows, int TotalCount);

public sealed record ProviderSearchRequest(
    string? Name,
    string? Phone,
    ProviderStatus? Status,
    ProviderOnboardingStatus? OnboardingStatus,
    Guid? CityId = null,
    int Page = 1,
    int PageSize = 20,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null);

/// <summary><see cref="ServiceCities"/> (task 371) names the cities this provider has an active <see cref="ProviderServiceArea"/> for, alphabetically - empty if the provider has not configured any service areas yet.</summary>
public sealed record ProviderSummaryResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    string Phone,
    string? Email,
    ProviderStatus Status,
    ProviderOnboardingStatus OnboardingStatus,
    DateTime CreatedAt,
    IReadOnlyList<string> ServiceCities);

public sealed record ProviderSearchResponse(IReadOnlyList<ProviderSummaryResponse> Items, int TotalCount, int Page, int PageSize);

/// <summary>Admin creates a provider record directly (as opposed to the provider's own self-service registration, task 146a). ProviderType is always Individual - OPEN DECISIONS #2.</summary>
public sealed record CreateProviderRequest(string LegalName, string DisplayName, string Phone, string? Email);

/// <param name="Latitude">
/// Task 243: both-or-neither with <paramref name="Longitude"/>, feeding the
/// automatic-assignment engine's distance ranking (task 244). Full-overwrite
/// semantics, same as every other field on this request (this is a PUT-style
/// update, not a patch) - submitting both as null clears a previously set
/// location.
/// </param>
public sealed record UpdateProviderRequest(string LegalName, string DisplayName, string? Email, decimal? Latitude = null, decimal? Longitude = null);

public sealed record SuspendProviderRequest(string Reason);

/// <summary>Admin-supplied reason for a right-to-erasure deletion, recorded to <see cref="Domain.ProviderStatusHistory"/> - mirrors <see cref="SuspendProviderRequest"/>.</summary>
public sealed record DeleteProviderRequest(string Reason);

public sealed record ProviderKycDocumentResponse(
    Guid Id,
    ProviderKycDocumentType DocType,
    string? DocNumber,
    string FileRef,
    ProviderKycVerificationStatus VerificationStatus,
    Guid? VerifiedBy,
    DateTime? VerifiedAt,
    DateTime SubmittedAt,
    string? RejectionReason);

/// <summary>
/// One row of the admin KYC verification queue (Provider Management UX
/// pass): a pending document plus the provider identity an admin needs to
/// tell one queue row from another, since <see cref="ProviderKycDocumentResponse"/>
/// alone (as returned nested under a single provider's own detail page) has
/// no name/phone to render across a cross-provider list.
/// </summary>
public sealed record ProviderKycDocumentQueueItemResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderDisplayName,
    ProviderKycDocumentType DocType,
    string? DocNumber,
    string FileRef,
    DateTime SubmittedAt);

/// <summary>One entry of a provider's append-only status change log (mirrors <c>BookingStatusHistoryEntryResponse</c>).</summary>
public sealed record ProviderStatusHistoryEntryResponse(
    Guid Id,
    ProviderStatus? FromStatus,
    ProviderStatus ToStatus,
    string? Reason,
    DateTime ChangedAtUtc);

public sealed record ProviderBackgroundCheckResponse(
    Guid Id,
    ProviderBackgroundCheckStatus Status,
    Guid CheckedBy,
    DateTime CheckedAt,
    string? Notes);

/// <summary>
/// Full admin provider detail (task 150a/150b): profile plus KYC documents
/// and background check history for the approval workflow, plus the profile
/// photo and its moderation state (task 293).
/// </summary>
/// <param name="Photo">
/// Appended last on purpose: this is a positional record, and inserting a
/// parameter mid-list would silently re-bind every argument after it at the
/// one call site (<c>ProviderDetailMapper</c>) that builds it.
/// </param>
/// <param name="BankAccount">
/// Structured bank account details for payouts (OPEN DECISIONS #3), appended
/// last for the same reason as <paramref name="Photo"/> above - null when the
/// provider has not submitted any yet.
/// </param>
public sealed record ProviderDetailResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    ProviderType ProviderType,
    string Phone,
    string? Email,
    ProviderStatus Status,
    ProviderOnboardingStatus OnboardingStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    decimal? Latitude,
    decimal? Longitude,
    IReadOnlyList<ProviderKycDocumentResponse> KycDocuments,
    IReadOnlyList<ProviderBackgroundCheckResponse> BackgroundChecks,
    ProviderPhotoResponse Photo,
    IReadOnlyList<ProviderStatusHistoryEntryResponse> StatusHistory,
    ProviderBankAccountResponse? BankAccount);

// ---- Photo moderation (task 293) ----

/// <summary>
/// One provider's profile photo and where it stands with moderation (task
/// 293) - the row the admin queue renders and the shape every verdict
/// returns.
/// </summary>
/// <param name="PhotoUrl">Deliberately the raw stored reference, not <see cref="Provider.PublicPhotoUrl"/>: a moderator has to see the photo precisely because it has NOT been approved.</param>
public sealed record ProviderPhotoResponse(
    Guid ProviderId,
    string DisplayName,
    string? PhotoUrl,
    ProviderPhotoModerationStatus? ModerationStatus,
    Guid? ModeratedByAdminUserId,
    DateTime? ModeratedAtUtc,
    string? ModerationNote);

/// <summary>A rejection must say why - the note is shown back to the provider so a rejected photo is actionable rather than a silent dead end (mirrors <see cref="RejectProviderKycDocumentRequest"/>).</summary>
public sealed record RejectProviderPhotoRequest(string Reason);

// ---- KYC approval and activation (task 150b, 160) ----

public sealed record RejectProviderKycDocumentRequest(string Reason);

public sealed record RecordBackgroundCheckRequest(ProviderBackgroundCheckStatus Status, string? Notes);

// ---- Capacity limits (task 245 built enforcement, task 308 adds the write path) ----

/// <summary>
/// A provider's dispatch capacity limits. Null on either field means
/// unlimited (mirrors <see cref="ProviderCapacity"/>'s own null-is-unlimited
/// convention) - returned even when no <see cref="ProviderCapacity"/> row
/// exists yet, so the admin screen always has something to render.
/// </summary>
public sealed record ProviderCapacityResponse(Guid ProviderId, int? MaxJobsPerDay, int? MaxJobsPerSlot);

/// <summary>Full-overwrite set of a provider's capacity limits (PUT-style, same convention as <see cref="UpdateProviderRequest"/>). Null clears a limit back to unlimited.</summary>
public sealed record SetProviderCapacityRequest(int? MaxJobsPerDay, int? MaxJobsPerSlot);

// ---- Performance view (task 150c) ----

/// <summary>
/// A provider's job-fulfilment performance summary (PROVIDER.md API surface
/// "get provider performance metrics"). Built from <see cref="Booking"/>/<see cref="BookingProviderAssignment"/>
/// history rather than a new rollup table. All-time (unlike the ranking list
/// in <see cref="ProviderPerformanceListResponse"/>, which defaults to a
/// rolling window) - matches <see cref="InProgressJobs"/>/<see cref="LifetimeEarnings"/>,
/// which were already all-time before this record grew the rate/rating
/// fields below.
/// </summary>
/// <param name="AcceptanceRatePercent">
/// <c>AcceptedAssignments / TotalAssignments * 100</c>, rounded to one
/// decimal - null when <see cref="TotalAssignments"/> is zero (no offers to
/// rate, not a 0% rate).
/// </param>
/// <param name="AverageResponseTimeMinutes">
/// Mean minutes between <c>BookingProviderAssignment.AssignedAt</c> and
/// <c>RespondedAt</c>, over every assignment the provider actually answered
/// (Accepted/Rejected/Completed - Completed's <c>RespondedAt</c> is its
/// original Accept, per <see cref="Domain.BookingProviderAssignment.Accept"/>).
/// Null when the provider has never responded to an offer. Computable only
/// because both timestamps are tracked on the assignment row - see
/// PROVIDER.md OPEN-FIXES-FEATURES.csv "Provider performance" row for why
/// this metric would otherwise have to be skipped.
/// </param>
/// <param name="CompletionRatePercent">
/// <c>CompletedJobs / AcceptedAssignments * 100</c>, rounded to one decimal
/// - null when the provider has never accepted an offer. Deliberately
/// against accepted work, not total offers: a provider who never gets
/// offered a job they'd finish should not be penalised the same as one who
/// accepts and then does not deliver.
/// </param>
/// <param name="AverageRating">
/// From <see cref="Review.ProviderId"/> (task 293) via
/// <see cref="Reviews.IReviewRepository.GetProviderRatingAsync"/> - visible
/// reviews only, all-time, rounded to one decimal. Null when the provider
/// has no visible provider-scoped review yet, which is not the same as a
/// rating of zero.
/// </param>
/// <param name="RatingCount">
/// Appended last, matching this positional record's own append-only rule -
/// how many reviews <see cref="AverageRating"/> is averaged over, so the UI
/// can show "4.6 (12)" rather than a bare, potentially thin, average.
/// </param>
public sealed record ProviderPerformanceResponse(
    Guid ProviderId,
    int TotalAssignments,
    int AcceptedAssignments,
    int RejectedAssignments,
    int CompletedJobs,
    int InProgressJobs,
    decimal LifetimeEarnings,
    double? AcceptanceRatePercent,
    double? AverageResponseTimeMinutes,
    double? CompletionRatePercent,
    double? AverageRating,
    int RatingCount);

// ---- Performance ranking list (docs/OPEN-FIXES-FEATURES.csv "Provider performance") ----

/// <summary>
/// One row of the provider-performance ranking list - the per-provider
/// numbers admin/ops actually need to judge or rank providers, and to weigh
/// alongside the assignment picker's existing pincode-match/jobs-today
/// signals (<see cref="EligibleProviderResponse"/>).
///
/// <see cref="Cancellations"/> is deliberately NOT a field here. The CSV row
/// asks for "cancellations", but <see cref="Domain.BookingProviderAssignmentStatus"/>
/// has no state for "provider accepted, then backed out" - only
/// <see cref="Domain.BookingProviderAssignmentStatus.Rejected"/> (declined
/// before ever accepting, already counted in <see cref="AcceptanceRatePercent"/>)
/// and <see cref="Domain.BookingProviderAssignmentStatus.Withdrawn"/>
/// (the booking itself was cancelled out from under a live assignment - not
/// the provider's doing). Reporting either of those as "cancellations" would
/// misattribute a customer/admin cancellation to the provider, or silently
/// double-count a decline this same row already reports. Rather than
/// fabricate a number from data that does not exist, this is omitted; a real
/// "provider cancelled after accepting" metric needs its own tracked event.
/// </summary>
/// <param name="OffersReceived">Every <see cref="Domain.BookingProviderAssignment"/> row created for this provider within the requested window, regardless of outcome.</param>
/// <param name="AcceptedOffers">Offers the provider accepted (Accepted or since-Completed) within the window.</param>
/// <param name="AcceptanceRatePercent">Rounded to one decimal; null when <see cref="OffersReceived"/> is zero.</param>
/// <param name="AverageResponseTimeMinutes">Mean minutes AssignedAt→RespondedAt over answered offers in the window; null when none were answered.</param>
/// <param name="CompletedJobs">Offers completed (verified) within the window.</param>
/// <param name="CompletionRatePercent"><see cref="CompletedJobs"/> / <see cref="AcceptedOffers"/>, rounded to one decimal; null when nothing was accepted.</param>
/// <param name="AverageRating">All-time (not window-scoped - a rating reflects the person, not this period), from <see cref="Review.ProviderId"/>; null with no visible review yet.</param>
/// <param name="RatingCount">How many visible reviews <see cref="AverageRating"/> is averaged over.</param>
public sealed record ProviderPerformanceSummaryResponse(
    Guid ProviderId,
    string DisplayName,
    ProviderStatus Status,
    int OffersReceived,
    int AcceptedOffers,
    double? AcceptanceRatePercent,
    double? AverageResponseTimeMinutes,
    int CompletedJobs,
    double? CompletionRatePercent,
    double? AverageRating,
    int RatingCount);

/// <summary>Sortable columns for <see cref="ProviderPerformanceListRequest"/> - mirrors the admin-web table's own sortable columns.</summary>
public enum ProviderPerformanceSortField
{
    DisplayName,
    OffersReceived,
    AcceptanceRate,
    AverageResponseTime,
    CompletionRate,
    AverageRating
}

/// <param name="PeriodDays">Rolling window in days over which offers/rates/response-time/completions are computed - defaults to 30 (task's "some period... pick one, make it filterable if cheap"). Does not affect <see cref="ProviderPerformanceSummaryResponse.AverageRating"/>, which is always all-time.</param>
public sealed record ProviderPerformanceListRequest(
    int Page = 1,
    int PageSize = 20,
    int PeriodDays = 30,
    ProviderPerformanceSortField SortBy = ProviderPerformanceSortField.OffersReceived,
    bool SortDescending = true);

public sealed record ProviderPerformanceListResponse(
    IReadOnlyList<ProviderPerformanceSummaryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int PeriodDays);

// ---- Provider Onboarding Overview dashboard ----

/// <summary>
/// Request for the Provider Onboarding Overview dashboard's funnel counts
/// (Admin Web new page). <see cref="Date"/> is an "as of" cutoff, not a
/// single day's cohort filter - mirrors <c>AdminFulfilmentBoardRequest</c>'s
/// own single-optional-date shape, same "defaults to today when absent"
/// convention.
/// </summary>
public sealed record AdminProviderOnboardingOverviewRequest(DateOnly? Date);

/// <summary>
/// A cumulative funnel: of every provider ever registered on or before
/// <see cref="Date"/>, how many are now at each stage
/// (docs/OPEN-FIXES-FEATURES.csv "Provider Onboarding Overview": counting
/// only <see cref="Date"/>'s own registrations made the tiles look far
/// emptier than the real, ongoing funnel). The six counts are NOT mutually
/// exclusive partitions - they read two independent dimensions of the same
/// <see cref="Provider"/> row (<see cref="ProviderOnboardingStatus"/> and
/// <see cref="ProviderStatus"/>, per that type's own doc comment) at once,
/// so a provider already <see cref="ProviderOnboardingStatus.Completed"/>
/// and <see cref="ProviderStatus.Active"/> counts toward both
/// <see cref="LiveCount"/> and <see cref="ActiveCount"/>, and every provider
/// in every other bucket also counts toward <see cref="TotalOnboardingCount"/>.
/// </summary>
/// <param name="TotalOnboardingCount">Every provider created on or before <see cref="Date"/>, regardless of current stage.</param>
/// <param name="DocumentVerificationCount">Currently at <see cref="ProviderOnboardingStatus.KycSubmitted"/> - KYC documents uploaded, awaiting an admin verdict.</param>
/// <param name="VerifiedCount">Currently at <see cref="ProviderOnboardingStatus.KycVerified"/> - at least one KYC document approved.</param>
/// <param name="PendingCount">Currently at <see cref="ProviderStatus.PendingVerification"/> - not yet activated.</param>
/// <param name="LiveCount">Currently at <see cref="ProviderOnboardingStatus.Completed"/> - the one-time onboarding flow is done.</param>
/// <param name="ActiveCount">Currently at <see cref="ProviderStatus.Active"/> - live and assignable.</param>
public sealed record AdminProviderOnboardingOverviewResponse(
    DateOnly Date,
    int TotalOnboardingCount,
    int DocumentVerificationCount,
    int VerifiedCount,
    int PendingCount,
    int LiveCount,
    int ActiveCount);

/// <summary>Repository-level result behind <see cref="AdminProviderOnboardingOverviewResponse"/> - the same six counts, before the request's echoed <see cref="AdminProviderOnboardingOverviewRequest.Date"/> is attached.</summary>
public sealed record ProviderOnboardingOverviewCounts(
    int TotalOnboardingCount,
    int DocumentVerificationCount,
    int VerifiedCount,
    int PendingCount,
    int LiveCount,
    int ActiveCount);

// ---- Bank account (structured payout details, OPEN DECISIONS #3) ----

/// <summary>
/// A provider submits/edits their own bank account details - the caller's
/// provider id is taken from the JWT (see <c>ProfileController.CurrentProviderId</c>),
/// never accepted here, so this is safe to bind straight from a request body
/// with no risk of a spoofed <see cref="ProviderId"/> (SRS 28.3 IDOR).
/// </summary>
public sealed record SubmitProviderBankAccountRequest(Guid ProviderId, string AccountHolderName, string AccountNumber, string IfscCode, string BankName);

/// <summary>Full bank account detail - the shape returned to the owning provider, and to an admin reviewing/approving one (full account number, unlike <see cref="ProviderBankAccountQueueItemResponse"/>'s masked queue row).</summary>
public sealed record ProviderBankAccountResponse(
    Guid Id,
    Guid ProviderId,
    string AccountHolderName,
    string AccountNumber,
    string IfscCode,
    string BankName,
    ProviderBankAccountVerificationStatus VerificationStatus,
    Guid? VerifiedBy,
    DateTime? VerifiedAt,
    string? RejectionReason,
    DateTime UpdatedAt);

/// <summary>A rejection must say why - shown back to the provider so it is actionable (mirrors <see cref="RejectProviderKycDocumentRequest"/>).</summary>
public sealed record RejectProviderBankAccountRequest(string Reason);

/// <summary>
/// One row of the admin bank-account verification queue - deliberately masks
/// <see cref="AccountNumber"/> to its last 4 digits (e.g. "•••• 1234") so the
/// cross-provider pending-review LIST does not dump every full account number
/// onto one screen; the single-record detail/approve view
/// (<see cref="ProviderBankAccountResponse"/>) still carries the full number,
/// matching how a real back office works.
/// </summary>
public sealed record ProviderBankAccountQueueItemResponse(
    Guid Id,
    Guid ProviderId,
    string ProviderDisplayName,
    string AccountHolderName,
    string MaskedAccountNumber,
    string IfscCode,
    string BankName,
    DateTime UpdatedAt);

/// <summary>
/// A provider's current bank account, as surfaced on the payout screen so an
/// admin processing a real bank transfer can see the account holder/number/
/// IFSC/bank and its verification status without navigating away (product
/// decision - see <c>ProviderPayoutResponse.BankAccount</c>). Full account
/// number, unlike the queue's masked row: an admin about to process a payout
/// needs the real number.
/// </summary>
public sealed record ProviderPayoutBankAccountSummaryResponse(
    string AccountHolderName,
    string AccountNumber,
    string IfscCode,
    string BankName,
    ProviderBankAccountVerificationStatus VerificationStatus);
