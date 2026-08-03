using Nestly.Application.ProviderManagement;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Builds a <see cref="ProviderDetailResponse"/> from a <see cref="Provider"/>
/// plus its KYC documents and background checks. Shared by
/// <see cref="ProviderManagementService"/> and <see cref="ProviderKycApprovalService"/>
/// so both services return the identical detail shape without duplicating
/// the mapping.
/// </summary>
internal static class ProviderDetailMapper
{
    public static ProviderDetailResponse ToDetailResponse(
        Provider provider,
        IReadOnlyList<ProviderKycDocument> documents,
        IReadOnlyList<ProviderBackgroundCheck> backgroundChecks) => new(
        provider.Id,
        provider.LegalName,
        provider.DisplayName,
        provider.ProviderType,
        provider.Phone,
        provider.Email,
        provider.Status,
        provider.OnboardingStatus,
        provider.CreatedAt,
        provider.UpdatedAt,
        documents.Select(d => new ProviderKycDocumentResponse(
            d.Id, d.DocType, d.DocNumber, d.FileRef, d.VerificationStatus, d.VerifiedBy, d.VerifiedAt, d.SubmittedAt)).ToList(),
        backgroundChecks.Select(c => new ProviderBackgroundCheckResponse(c.Id, c.Status, c.CheckedBy, c.CheckedAt, c.Notes)).ToList());
}
