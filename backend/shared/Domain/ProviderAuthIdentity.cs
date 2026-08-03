using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A single credential a provider can authenticate with (PROVIDER.md
/// "provider_auth_identity ... mirrors the customer auth tables"). Reuses
/// <see cref="AuthProviderType"/> - it is a generic provider-kind enum, not
/// customer-specific - so a future email+password mode for providers needs no
/// new type. Only <see cref="AuthProviderType.MobileOtp"/> is issued by the
/// registration flow today (PROVIDER.md API surface lists no password login
/// for providers), mirroring <see cref="CustomerAuthIdentity"/>'s shape
/// exactly so it can be extended the same way if that changes.
/// </summary>
public class ProviderAuthIdentity : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public AuthProviderType Provider { get; private set; }
    public string Identifier { get; private set; } = string.Empty;
    public string? PasswordHash { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTime CreatedAt { get; private set; }

    protected ProviderAuthIdentity() { }

    public ProviderAuthIdentity(Guid id, Guid providerId, AuthProviderType provider, string identifier, bool isPrimary)
        : base(id)
    {
        ProviderId = providerId;
        Provider = provider;
        Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        IsPrimary = isPrimary;
        CreatedAt = DateTime.UtcNow;
    }

    public void SetPasswordHash(string passwordHash) =>
        PasswordHash = passwordHash ?? throw new ArgumentNullException(nameof(passwordHash));

    public void MakePrimary() => IsPrimary = true;

    /// <summary>Repoints this credential after the provider re-verified a new mobile/email (mirrors <c>CustomerAuthIdentity.ChangeIdentifier</c>).</summary>
    public void ChangeIdentifier(string identifier) =>
        Identifier = string.IsNullOrWhiteSpace(identifier)
            ? throw new ArgumentException("Identifier is required.", nameof(identifier))
            : identifier;
}
