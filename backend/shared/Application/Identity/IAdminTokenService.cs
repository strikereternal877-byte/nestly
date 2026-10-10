namespace Nestly.Application.Identity;

public record AdminAccessToken(string Value, DateTime ExpiresAtUtc);

public interface IAdminTokenService
{
    /// <param name="roleName">Null when the account has no role assigned yet (see <see cref="AdminRolePermissions"/>).</param>
    /// <param name="permissionCodes">Sourced from the account's role -&gt; permission mappings (task 96c); embedded as one <see cref="AdminClaimTypes.Permission"/> claim per code.</param>
    AdminAccessToken GenerateAccessToken(Guid adminUserId, string email, string? roleName, IReadOnlyList<string> permissionCodes);

    /// <summary>A random opaque token; the caller is responsible for hashing it before storage.</summary>
    string GenerateRefreshToken();

    TimeSpan RefreshTokenLifetime { get; }
}
