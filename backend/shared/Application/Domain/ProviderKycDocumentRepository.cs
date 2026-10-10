using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderKycDocumentRepository
{
    Task AddAsync(ProviderKycDocument entity);
    Task UpdateAsync(ProviderKycDocument entity);
    Task<ProviderKycDocument?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<ProviderKycDocument>> GetByProviderAsync(Guid providerId);

    /// <summary>
    /// The admin KYC verification queue: every document still
    /// <see cref="ProviderKycVerificationStatus.Pending"/>, across every
    /// provider, oldest submission first so nothing starves at the back of
    /// it - same shape and same "unpaginated work queue, not a directory"
    /// reasoning as <c>IProviderRepository.ListPendingPhotoModerationAsync</c>.
    /// </summary>
    Task<IReadOnlyList<ProviderKycDocument>> ListPendingAsync(CancellationToken cancellationToken = default);
}
