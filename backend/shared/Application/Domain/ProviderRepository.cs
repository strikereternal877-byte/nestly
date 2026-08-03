using Nestly.Application.ProviderManagement;
using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderRepository : IRepository<Provider>
{
    Task<bool> ExistsByPhoneAsync(string phone);
    Task<Provider?> GetByPhoneAsync(string phone);

    /// <summary>Search/filter with pagination for the admin provider list (task 150a) - mirrors <c>ICustomerRepository.SearchAsync</c>.</summary>
    Task<ProviderSearchResult> SearchAsync(ProviderSearchFilter filter);
}
