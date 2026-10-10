using Nestly.Application.ProviderManagement;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Builds a <see cref="ProviderDetailResponse"/> from a <see cref="Provider"/>
/// plus its KYC documents and background checks. Also the single place a
/// <see cref="ProviderPhotoResponse"/> is projected from the aggregate (task
/// 293), so the provider detail and the moderation queue cannot describe the
/// same photo differently. Shared by
/// <see cref="ProviderManagementService"/> and <see cref="ProviderKycApprovalService"/>
/// so both services return the identical detail shape without duplicating
/// the mapping.
/// </summary>
internal static class ProviderDetailMapper
{
    public static ProviderDetailResponse ToDetailResponse(
        Provider provider,
        IReadOnlyList<ProviderKycDocument> documents,
        IReadOnlyList<ProviderBackgroundCheck> backgroundChecks,
        IReadOnlyList<ProviderStatusHistory> statusHistory,
        ProviderBankAccount? bankAccount = null) => new(
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
        provider.Latitude,
        provider.Longitude,
        documents.Select(d => new ProviderKycDocumentResponse(
            d.Id, d.DocType, d.DocNumber, d.FileRef, d.VerificationStatus, d.VerifiedBy, d.VerifiedAt, d.SubmittedAt, d.RejectionReason)).ToList(),
        backgroundChecks.Select(c => new ProviderBackgroundCheckResponse(c.Id, c.Status, c.CheckedBy, c.CheckedAt, c.Notes)).ToList(),
        ToPhotoResponse(provider),
        statusHistory
            .Select(h => new ProviderStatusHistoryEntryResponse(h.Id, h.FromStatus, h.ToStatus, h.Reason, h.ChangedAtUtc))
            .ToList(),
        ToBankAccountResponse(bankAccount));

    public static ProviderBankAccountResponse? ToBankAccountResponse(ProviderBankAccount? bankAccount) =>
        bankAccount is null
            ? null
            : new ProviderBankAccountResponse(
                bankAccount.Id, bankAccount.ProviderId, bankAccount.AccountHolderName, bankAccount.AccountNumber,
                bankAccount.IfscCode, bankAccount.BankName, bankAccount.VerificationStatus, bankAccount.VerifiedBy,
                bankAccount.VerifiedAt, bankAccount.RejectionReason, bankAccount.UpdatedAt);

    public static ProviderPhotoResponse ToPhotoResponse(Provider provider) => new(
        provider.Id,
        provider.DisplayName,
        // Raw, not PublicPhotoUrl - a moderator reviews the photo precisely
        // because it is not yet approved. See ProviderPhotoResponse.
        provider.PhotoUrl,
        provider.PhotoModerationStatus,
        provider.PhotoModeratedByAdminUserId,
        provider.PhotoModeratedAtUtc,
        provider.PhotoModerationNote);
}
