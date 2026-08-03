namespace Nestly.Application.ProviderIdentity;

public record RequestProviderLoginOtpRequest(string Mobile);

public record LoginProviderWithOtpRequest(string Mobile, string OtpCode);

public record RefreshProviderTokenRequest(string RefreshToken);

public record LogoutProviderRequest(string RefreshToken);

public record ProviderLoginResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken);
