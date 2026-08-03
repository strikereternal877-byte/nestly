using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

// ---- CRUD (task 150a) ----

/// <summary>Search/filter criteria for the admin provider list (mirrors <c>CustomerSearchFilter</c>).</summary>
public sealed record ProviderSearchFilter(
    string? Name,
    string? Phone,
    ProviderStatus? Status,
    ProviderOnboardingStatus? OnboardingStatus,
    int Page,
    int PageSize);

public sealed record ProviderSearchResult(IReadOnlyList<Provider> Rows, int TotalCount);

public sealed record ProviderSearchRequest(
    string? Name,
    string? Phone,
    ProviderStatus? Status,
    ProviderOnboardingStatus? OnboardingStatus,
    int Page = 1,
    int PageSize = 20);

public sealed record ProviderSummaryResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    string Phone,
    string? Email,
    ProviderStatus Status,
    ProviderOnboardingStatus OnboardingStatus,
    DateTime CreatedAt);

public sealed record ProviderSearchResponse(IReadOnlyList<ProviderSummaryResponse> Items, int TotalCount, int Page, int PageSize);

/// <summary>Admin creates a provider record directly (as opposed to the provider's own self-service registration, task 146a). ProviderType is always Individual - OPEN DECISIONS #2.</summary>
public sealed record CreateProviderRequest(string LegalName, string DisplayName, string Phone, string? Email);

public sealed record UpdateProviderRequest(string LegalName, string DisplayName, string? Email);

public sealed record SuspendProviderRequest(string Reason);

public sealed record ProviderKycDocumentResponse(
    Guid Id,
    ProviderKycDocumentType DocType,
    string? DocNumber,
    string FileRef,
    ProviderKycVerificationStatus VerificationStatus,
    Guid? VerifiedBy,
    DateTime? VerifiedAt,
    DateTime SubmittedAt);

public sealed record ProviderBackgroundCheckResponse(
    Guid Id,
    ProviderBackgroundCheckStatus Status,
    Guid CheckedBy,
    DateTime CheckedAt,
    string? Notes);

/// <summary>Full admin provider detail (task 150a/150b): profile plus KYC documents and background check history for the approval workflow.</summary>
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
    IReadOnlyList<ProviderKycDocumentResponse> KycDocuments,
    IReadOnlyList<ProviderBackgroundCheckResponse> BackgroundChecks);

// ---- KYC approval and activation (task 150b, 160) ----

public sealed record RejectProviderKycDocumentRequest(string Reason);

public sealed record RecordBackgroundCheckRequest(ProviderBackgroundCheckStatus Status, string? Notes);

// ---- Performance view (task 150c) ----

/// <summary>
/// A provider's job-fulfilment performance summary (PROVIDER.md API surface
/// "get provider performance metrics"). Built from <see cref="Booking"/>/<see cref="BookingProviderAssignment"/>
/// history rather than a new rollup table - <c>provider_rating_summary</c> is
/// out of this pass's scope (PROVIDER.md OPEN DECISIONS #4: rating does not
/// affect assignment, and no review-to-provider link exists yet).
/// </summary>
public sealed record ProviderPerformanceResponse(
    Guid ProviderId,
    int TotalAssignments,
    int AcceptedAssignments,
    int RejectedAssignments,
    int CompletedJobs,
    int InProgressJobs,
    decimal LifetimeEarnings);
