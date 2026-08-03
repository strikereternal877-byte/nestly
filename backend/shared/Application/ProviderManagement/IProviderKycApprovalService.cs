using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Admin-side KYC approval (task 150b - the counterpart to task 146c's
/// submission-only <c>IProviderKycService</c>) and the distinct post-KYC
/// background/reference check step (task 160). A provider cannot become fully
/// <see cref="Nestly.Domain.ProviderStatus.Active"/> until both KYC is
/// approved and the background check passes - <see cref="ActivateAsync"/> is
/// where that gate is enforced, per <c>Provider.ChangeStatus</c>'s own doc
/// comment that transition legality belongs to the calling service, not the
/// entity.
/// </summary>
public interface IProviderKycApprovalService
{
    Task<Result<ProviderKycDocumentResponse>> ApproveDocumentAsync(Guid documentId, Guid adminUserId);

    Task<Result<ProviderKycDocumentResponse>> RejectDocumentAsync(Guid documentId, Guid adminUserId, RejectProviderKycDocumentRequest request);

    /// <summary>Records a background/reference check outcome (task 160). Append-only - a re-check adds a new row rather than overwriting the previous one.</summary>
    Task<Result<ProviderBackgroundCheckResponse>> RecordBackgroundCheckAsync(Guid providerId, Guid adminUserId, RecordBackgroundCheckRequest request);

    /// <summary>
    /// Activates a provider (moves <see cref="Nestly.Domain.ProviderStatus"/>
    /// to Active) once its KYC onboarding status is KycVerified and its most
    /// recent background check passed (task 160's gate). Fails with a
    /// business error otherwise - both conditions are checked explicitly
    /// rather than left to the caller to sequence correctly.
    /// </summary>
    Task<Result<ProviderDetailResponse>> ActivateAsync(Guid providerId);
}
