namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "ProviderAccount" configuration section: the
/// login-lockout policy for providers, mirroring <see cref="AccountOptions"/>'s
/// lockout fields. No password-auth/email-uniqueness fields - PROVIDER.md's
/// API surface has no password login for providers, so those
/// <see cref="AccountOptions"/> fields have no provider equivalent (yet).
/// </summary>
public class ProviderAccountOptions
{
    public const string SectionName = "ProviderAccount";

    /// <summary>
    /// Consecutive failed login attempts against one mobile number within
    /// <see cref="LockoutWindowMinutes"/> before further attempts are
    /// refused (mirrors <see cref="AccountOptions.MaxFailedLoginAttempts"/>).
    /// </summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    public int LockoutWindowMinutes { get; set; } = 15;
}
