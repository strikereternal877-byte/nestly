using Nestly.Domain;

namespace Nestly.Application.ProviderIdentity;

/// <summary>
/// KYC document submission (task 146c, PROVIDER.md API surface "upload KYC
/// documents"). <see cref="FileRef"/> is a reference to an already-uploaded
/// file (storage key/URL) - this workflow does not itself handle the binary
/// upload/storage concern, matching how <c>ProviderKycDocument.FileRef</c> is
/// modeled.
/// </summary>
public record SubmitProviderKycDocumentRequest(
    Guid ProviderId,
    ProviderKycDocumentType DocType,
    string FileRef,
    string? DocNumber);

public record ProviderKycDocumentResponse(
    Guid Id,
    Guid ProviderId,
    string DocType,
    string? DocNumber,
    string FileRef,
    string VerificationStatus,
    DateTime SubmittedAt,
    DateTime? VerifiedAt);

/// <summary>Overall KYC picture for a provider (PROVIDER.md API surface "get KYC status").</summary>
public record ProviderKycStatusResponse(
    Guid ProviderId,
    string OnboardingStatus,
    IReadOnlyList<ProviderKycDocumentResponse> Documents);
