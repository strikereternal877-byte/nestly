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

    /// <summary>Job-fulfilment performance summary (task 150c).</summary>
    Task<Result<ProviderPerformanceResponse>> GetPerformanceAsync(Guid providerId);
}
