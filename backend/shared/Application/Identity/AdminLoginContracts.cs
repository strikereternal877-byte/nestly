namespace Nestly.Application.Identity;

/// <summary>Admin panel login request (SRS 12.1.1, task 95a).</summary>
public record AdminLoginRequest(string Email, string Password);

/// <summary>
/// Admin panel session: a short-lived signed access token plus a longer-lived,
/// single-use, rotate-on-refresh opaque refresh token (SRS 12.1.2), mirroring
/// the customer identity's <c>LoginResponse</c> shape. See
/// <see cref="AdminJwtOptions.RefreshTokenHours"/> for why the admin refresh
/// window is tuned much shorter than the customer's.
/// </summary>
public record AdminLoginResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken);
