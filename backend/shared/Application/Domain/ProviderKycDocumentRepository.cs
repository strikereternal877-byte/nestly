using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderKycDocumentRepository
{
    Task AddAsync(ProviderKycDocument entity);
    Task UpdateAsync(ProviderKycDocument entity);
    Task<ProviderKycDocument?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<ProviderKycDocument>> GetByProviderAsync(Guid providerId);
}
