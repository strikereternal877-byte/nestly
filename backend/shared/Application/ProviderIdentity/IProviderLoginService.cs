using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderIdentity;

/// <summary>
/// Login, session issuance, refresh, and logout for providers. OTP-only -
/// unlike <c>ICustomerLoginService</c> there is no password login, matching
/// PROVIDER.md's API surface.
/// </summary>
public interface IProviderLoginService
{
    Task<Result> RequestOtpAsync(RequestProviderLoginOtpRequest request);

    Task<Result<ProviderLoginResponse>> LoginWithOtpAsync(LoginProviderWithOtpRequest request);

    Task<Result<ProviderLoginResponse>> RefreshAsync(RefreshProviderTokenRequest request);

    Task<Result> LogoutAsync(LogoutProviderRequest request);
}
