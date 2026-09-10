namespace Nestly.Application.ProviderProfile;

/// <summary>
/// Provider's own profile view (PROVIDER.md API surface "get/update profile").
/// </summary>
/// <param name="PhotoUrl">
/// Task 293. This is the provider's OWN view, so it shows the photo they
/// actually uploaded whatever its moderation state - unlike the
/// customer-facing summaries, which read
/// <see cref="Nestly.Domain.Provider.PublicPhotoUrl"/> and therefore see
/// nothing until it is approved. Showing a provider a blank where their pending photo is
/// would read as "the upload failed" and produce a second upload.
/// </param>
/// <param name="PhotoModerationStatus">
/// Null exactly when <paramref name="PhotoUrl"/> is. This is what lets the
/// portal tell a provider their photo is still under review, or was rejected
/// and why (<paramref name="PhotoModerationNote"/>), instead of leaving them
/// to guess why customers cannot see it.
///
/// A <c>string</c>, not the enum, matching <paramref name="Status"/> and
/// <paramref name="OnboardingStatus"/> on this same response: no API in this
/// solution registers a JsonStringEnumConverter, so an enum here would go
/// over the wire as an ordinal and couple provider-web to the C# declaration
/// order. This response is the one place that has always avoided that.
/// </param>
public record ProviderProfileResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    string Phone,
    string? Email,
    string Status,
    string OnboardingStatus,
    string? PhotoUrl,
    string? PhotoModerationStatus,
    string? PhotoModerationNote,
    // Task 309: the provider's own rating (task 293's provider-scoped
    // aggregate, IReviewRepository.GetProviderRatingAsync - computed from
    // visible reviews, not a rollup table, per PROVIDER.md OPEN DECISIONS
    // #4). Both null together when the provider has no visible reviews yet -
    // that is a distinct state from a rating of zero, so the portal must
    // render "not yet rated," not "0.0 stars."
    double? AverageRating,
    int? ReviewCount);

public record UpdateProviderProfileRequest(string LegalName, string DisplayName, string? Email);

/// <summary>
/// Sets or clears the provider's profile photo (task 293). A separate request
/// from <see cref="UpdateProviderProfileRequest"/> on purpose: a photo change
/// re-enters moderation and a name change does not, so folding it into the
/// general profile PUT would send every provider's photo back to Pending
/// every time they corrected a typo in their email.
/// </summary>
/// <param name="PhotoUrl">
/// A reference (storage key/URL) to an already-hosted image - the same
/// reference-only convention <c>SubmitProviderKycDocumentRequest.FileRef</c>
/// uses, since this solution still has no blob-storage abstraction. Null or
/// empty clears the photo.
/// </param>
public record UpdateProviderPhotoRequest(string? PhotoUrl);

/// <summary>
/// A file just saved to <c>IFileStorageService</c> and resolved to an
/// absolute URL (task 349) - the response for
/// <c>ProfileController.UploadPhoto</c>/<c>UploadKycDocument</c>. The client
/// feeds <see cref="Url"/> straight into <see cref="UpdateProviderPhotoRequest.PhotoUrl"/>
/// or <see cref="SubmitProviderKycDocumentRequest.FileRef"/>: uploading and
/// submitting stay two calls, not one, so the existing validation/moderation
/// rules on those two endpoints need no duplicate.
/// </summary>
public record ProviderFileUploadResponse(string Url);

/// <summary>One geography a provider covers (PROVIDER.md "provider_service_area").</summary>
public record ProviderServiceAreaResponse(
    Guid Id,
    Guid ProviderId,
    Guid CityId,
    Guid? ZoneId,
    Guid? PincodeId,
    bool IsActive);

public record ProviderServiceAreaInput(Guid CityId, Guid? ZoneId, Guid? PincodeId);

/// <summary>Full replacement of a provider's coverage set (PROVIDER.md API surface "update service areas").</summary>
public record UpdateProviderServiceAreasRequest(IReadOnlyList<ProviderServiceAreaInput> Areas);

/// <summary>A category/service a provider is qualified for (PROVIDER.md "provider_skill_mapping").</summary>
public record ProviderSkillResponse(
    Guid Id,
    Guid ProviderId,
    Guid CategoryId,
    Guid? ServiceId,
    bool IsActive);

public record ProviderSkillInput(Guid CategoryId, Guid? ServiceId);

/// <summary>Full replacement of a provider's declared skills (PROVIDER.md API surface "update skills").</summary>
public record UpdateProviderSkillsRequest(IReadOnlyList<ProviderSkillInput> Skills);

/// <summary>
/// One prerequisite on the go-live checklist
/// (docs/OPEN-FIXES-FEATURES.csv "Onboarding checklist and go-live status").
/// </summary>
/// <param name="Key">
/// Stable machine-readable identifier (e.g. <c>"kycApproved"</c>) - what
/// provider-web switches on to pick the right fix-it link, independent of
/// <paramref name="Label"/>'s wording.
/// </param>
/// <param name="Label">Human-readable description of the prerequisite, ready to show as-is.</param>
/// <param name="IsComplete">Whether this specific prerequisite is currently satisfied.</param>
public record ProviderGoLiveCheckResponse(string Key, string Label, bool IsComplete);

/// <summary>
/// The provider's go-live checklist as a whole
/// (docs/OPEN-FIXES-FEATURES.csv "Onboarding checklist and go-live status").
/// </summary>
/// <param name="IsGoLiveReady">True only once every check in <see cref="Checks"/> is complete.</param>
public record ProviderGoLiveStatusResponse(bool IsGoLiveReady, IReadOnlyList<ProviderGoLiveCheckResponse> Checks);
