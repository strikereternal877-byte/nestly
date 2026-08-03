using Nestly.Domain;

namespace Nestly.Application;

public interface IProviderLoginAttemptRepository
{
    Task AddAsync(ProviderLoginAttempt entity);
    Task<int> CountFailuresSinceAsync(string identifier, DateTime sinceUtc);
}
