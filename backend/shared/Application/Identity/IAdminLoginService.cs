using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Identity;

public interface IAdminLoginService
{
    Task<Result<AdminLoginResponse>> LoginAsync(AdminLoginRequest request);

    /// <summary>Administrative override that clears an account lockout immediately (task 95d).</summary>
    Task<Result> UnlockAsync(Guid adminUserId);

    /// <summary>Exchanges a still-valid refresh token for a new access+refresh pair (rotation, SRS 28.3), mirroring <c>ICustomerLoginService.RefreshAsync</c>.</summary>
    Task<Result<AdminLoginResponse>> RefreshAsync(RefreshTokenRequest request);

    /// <summary>Invalidates a session's refresh token, mirroring <c>ICustomerLoginService.LogoutAsync</c>.</summary>
    Task<Result> LogoutAsync(LogoutRequest request);
}
