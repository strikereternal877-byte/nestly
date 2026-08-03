using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderIdentity;

/// <summary>
/// KYC document submission and status lookup (task 146c - the submission
/// side only; approval/rejection is task 150b's admin workflow, which calls
/// <c>ProviderKycDocument.Approve</c>/<c>Reject</c> directly rather than
/// through this interface).
/// </summary>
public interface IProviderKycService
{
    Task<Result<ProviderKycDocumentResponse>> SubmitDocumentAsync(SubmitProviderKycDocumentRequest request);

    Task<Result<ProviderKycStatusResponse>> GetStatusAsync(Guid providerId);
}
