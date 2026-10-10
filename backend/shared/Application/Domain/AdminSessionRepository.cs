using Nestly.Domain;

namespace Nestly.Application;

/// <summary>Mirrors <see cref="ICustomerSessionRepository"/> for the admin identity.</summary>
public interface IAdminSessionRepository
{
    Task AddAsync(AdminSession entity);
    Task UpdateAsync(AdminSession entity);
    Task<AdminSession?> GetByRefreshTokenHashAsync(string refreshTokenHash);
}
