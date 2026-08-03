using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A single OTP challenge for a provider (PROVIDER.md "provider_otp ... mirrors
/// the customer auth tables"). Reuses <see cref="OtpPurpose"/> - the set of
/// purposes is generic, not customer-specific - and is otherwise an exact
/// structural mirror of <see cref="CustomerOtp"/>, kept as its own table
/// (rather than a shared one keyed by an actor-type discriminator) so this
/// module stays independent of Customer per PROVIDER.md's SCOPE BOUNDARY: a
/// person who is both a customer and a provider with the same mobile number
/// must not have one role's OTP/lockout state affect the other's.
/// </summary>
public class ProviderOtp : Entity<Guid>
{
    public Guid? ProviderId { get; private set; }
    public string Target { get; private set; } = string.Empty;
    public OtpPurpose Purpose { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime CreatedAt { get; private set; }

    protected ProviderOtp() { }

    public ProviderOtp(Guid id, Guid? providerId, string target, OtpPurpose purpose, string codeHash, DateTime expiresAt)
        : base(id)
    {
        ProviderId = providerId;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Purpose = purpose;
        CodeHash = codeHash ?? throw new ArgumentNullException(nameof(codeHash));
        ExpiresAt = expiresAt;
        AttemptCount = 0;
        CreatedAt = DateTime.UtcNow;
    }

    public bool IsExpired(DateTime asOfUtc) => asOfUtc >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;

    public void RecordAttempt() => AttemptCount++;

    public void MarkConsumed() => ConsumedAt = DateTime.UtcNow;
}
