using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Structured, admin-verifiable provider bank account details for payouts
/// (docs/PROVIDER.md OPEN DECISIONS #3). One record per provider with upsert
/// semantics - <see cref="SubmitAsync"/> creates the row the first time and
/// updates it (resetting verification to Pending) on every resubmission - so,
/// unlike KYC documents (submission and admin approval split across
/// <c>IProviderKycService</c> and <c>IProviderKycApprovalService</c>), both
/// sides of this lifecycle (provider submit, admin approve/reject/queue) live
/// on this one interface. Store-and-display only for the manual payout flow
/// (<see cref="IProviderPayoutService.UpdateStatusAsync"/> never checks it) -
/// but the real PayU Payouts integration's <see cref="IProviderPayoutService.PayViaPayUAsync"/>
/// DOES require <see cref="ProviderBankAccountVerificationStatus.Verified"/>
/// before it will hand these details to PayU (see <c>ProviderBankAccount</c>'s
/// own doc comment for why that one path is different).
/// </summary>
public interface IProviderBankAccountService
{
    /// <summary>Creates the provider's bank account row the first time, or updates the existing one (upsert) and resets it to Pending on every resubmission.</summary>
    Task<Result<ProviderBankAccountResponse>> SubmitAsync(SubmitProviderBankAccountRequest request);

    /// <summary>The caller's own bank account details - NotFound when nothing has been submitted yet.</summary>
    Task<Result<ProviderBankAccountResponse>> GetAsync(Guid providerId);

    /// <summary>The admin verification queue: every row still Pending, oldest submission first.</summary>
    Task<IReadOnlyList<ProviderBankAccountQueueItemResponse>> ListPendingAsync(CancellationToken cancellationToken = default);

    Task<Result<ProviderBankAccountResponse>> ApproveAsync(Guid bankAccountId, Guid adminUserId);

    Task<Result<ProviderBankAccountResponse>> RejectAsync(Guid bankAccountId, Guid adminUserId, RejectProviderBankAccountRequest request);
}
