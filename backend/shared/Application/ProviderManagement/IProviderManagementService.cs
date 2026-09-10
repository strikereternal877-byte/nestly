using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Admin provider directory management (PROVIDER.md API surface "Provider CRUD",
/// task 150a) plus the performance view (task 150c). Mirrors
/// <c>ICustomerManagementService</c>'s shape: one service for the related
/// read/write operations on a provider's own record, rather than one per
/// action. KYC approval and background-check/activation are a separate
/// service (<see cref="IProviderKycApprovalService"/>) - a distinct workflow
/// with its own approval gate, not simple field-level CRUD.
/// </summary>
public interface IProviderManagementService
{
    Task<Result<ProviderSearchResponse>> SearchAsync(ProviderSearchRequest request);

    Task<Result<ProviderDetailResponse>> GetDetailAsync(Guid providerId);

    /// <summary>Admin-created provider record. ProviderType is always Individual (OPEN DECISIONS #2).</summary>
    Task<Result<ProviderDetailResponse>> CreateAsync(CreateProviderRequest request);

    Task<Result<ProviderDetailResponse>> UpdateAsync(Guid providerId, UpdateProviderRequest request);

    /// <summary>Suspends a provider's account - blocks further assignment until reactivated.</summary>
    Task<Result<ProviderDetailResponse>> SuspendAsync(Guid providerId, SuspendProviderRequest request);

    /// <summary>Reactivates a previously suspended provider. Does not re-run the KYC/background-check activation gate - that only applies to the first activation (<see cref="IProviderKycApprovalService.ActivateAsync"/>).</summary>
    Task<Result<ProviderDetailResponse>> ReactivateAsync(Guid providerId);

    /// <summary>
    /// Support-initiated account deletion (right-to-erasure request handled
    /// on the provider's behalf). Terminal and irreversible - unlike
    /// Suspend/Reactivate there is no "undelete".
    /// </summary>
    Task<Result<ProviderDetailResponse>> DeleteAsync(Guid providerId);

    /// <summary>Job-fulfilment performance summary (task 150c).</summary>
    Task<Result<ProviderPerformanceResponse>> GetPerformanceAsync(Guid providerId);

    /// <summary>
    /// The provider-performance ranking list (docs/OPEN-FIXES-FEATURES.csv
    /// "Provider performance"): offers received, acceptance rate, average
    /// response time, completion rate and average rating, per provider, over
    /// a rolling window - the data admin/ops needs to judge or rank
    /// providers that the assignment picker's own pincode-match/jobs-today
    /// signals never provided.
    /// </summary>
    Task<Result<ProviderPerformanceListResponse>> ListPerformanceAsync(ProviderPerformanceListRequest request);

    /// <summary>Current dispatch capacity limits (task 245/308). Unlimited (both null) when no <c>ProviderCapacity</c> row exists yet.</summary>
    Task<Result<ProviderCapacityResponse>> GetCapacityAsync(Guid providerId);

    /// <summary>Sets a provider's dispatch capacity limits, hard-enforced by the automatic-assignment engine (task 245, 308).</summary>
    Task<Result<ProviderCapacityResponse>> SetCapacityAsync(Guid providerId, SetProviderCapacityRequest request);
}
