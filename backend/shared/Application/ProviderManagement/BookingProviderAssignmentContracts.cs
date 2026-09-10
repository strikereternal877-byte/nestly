using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>Admin assigns a provider to a booking (task 147, PROVIDER.md OPEN DECISIONS #1 - manual, admin-driven).</summary>
public sealed record AssignProviderRequest(Guid ProviderId, DateTime? ResponseDeadline);

/// <summary>Rejects the booking's current outstanding assignment (task 159). An admin may record this on the provider's behalf (e.g. a phone-call decline); the same service method is what a future provider-facing reject endpoint (task 151) would call.</summary>
public sealed record RejectAssignmentRequest(string? Reason);

public sealed record BookingProviderAssignmentResponse(
    Guid Id,
    Guid BookingId,
    Guid ProviderId,
    string ProviderDisplayName,
    BookingAssignedByType AssignedByType,
    Guid? AssignedByUserId,
    DateTime AssignedAt,
    BookingProviderAssignmentStatus Status,
    DateTime? ResponseDeadline,
    DateTime? RespondedAt,
    string? Notes,
    string? CompletionProofRef,
    // Appended last, matching this record's own append-only convention (see
    // ProviderPhotoResponse). Lets the admin booking screen tell a provider
    // whose KYC review is still outstanding apart from one already verified,
    // without a second call - see IProviderRepository.GetOnboardingStatusesByIdsAsync.
    ProviderOnboardingStatus ProviderOnboardingStatus);

/// <summary>
/// A candidate for manual assignment to a booking (PROVIDER.md OPEN DECISIONS
/// #1 - assists the admin's decision, does not assign anything itself: no
/// auto-dispatch/matching engine, exactly as that decision requires).
/// Matched on <see cref="Nestly.Domain.ProviderServiceArea"/> (the booking's
/// pincode/city) and <see cref="Nestly.Domain.ProviderSkillMapping"/> (the
/// booking's service/category), ranked by specificity - not by
/// <c>provider_rating_summary</c>, which OPEN DECISIONS #4 explicitly keeps
/// out of the manual assignment flow. <see cref="MaxJobsPerDay"/>/
/// <see cref="AssignedJobsToday"/> surface <see cref="Nestly.Domain.ProviderCapacity"/>,
/// which that entity's own doc comment already describes as "advisory only
/// in v1... an admin can consult them when hand-assigning a booking" - this
/// is that consultation, still nothing enforced automatically.
/// </summary>
/// <param name="AcceptanceRatePercent">
/// Docs/OPEN-FIXES-FEATURES.csv "Provider performance": "expose the key
/// metrics inline in the assignment picker" - all-time, rounded to one
/// decimal, null with no offers yet. Display only, exactly like
/// <see cref="AverageRating"/> below - it does not change
/// <see cref="ProviderId"/>'s position in the already-ranked list this
/// record is one row of (still specificity-then-load, never rating/rate;
/// OPEN DECISIONS #4/#3).
/// </param>
/// <param name="AverageRating">
/// Appended last, matching this record's own append-only convention. From
/// <see cref="Review.ProviderId"/> (task 293) - display only, per OPEN
/// DECISIONS #4: "`provider_rating_summary` exists for display (provider
/// performance views, admin provider detail)... does not read it to rank or
/// restrict candidates." Null with no visible review yet.
/// </param>
public sealed record EligibleProviderResponse(
    Guid ProviderId,
    string DisplayName,
    string Phone,
    bool PincodeMatch,
    bool ServiceMatch,
    int? MaxJobsPerDay,
    int AssignedJobsToday,
    double? AcceptanceRatePercent,
    double? AverageRating);
