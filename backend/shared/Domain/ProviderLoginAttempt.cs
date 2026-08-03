using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// One provider login attempt, success or failure, keyed by the identifier
/// tried (mirrors <see cref="LoginAttempt"/> exactly). A separate table from
/// the customer <see cref="LoginAttempt"/> - not a shared one keyed by actor
/// type - for the same PROVIDER.md SCOPE BOUNDARY reason as
/// <see cref="ProviderOtp"/>: a mobile number that is registered as both a
/// customer and a provider must not have one role's failed attempts count
/// toward the other's lockout.
/// </summary>
public class ProviderLoginAttempt : Entity<Guid>
{
    public string Identifier { get; private set; } = string.Empty;
    public bool Succeeded { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    protected ProviderLoginAttempt() { }

    public ProviderLoginAttempt(Guid id, string identifier, bool succeeded, DateTime occurredAtUtc) : base(id)
    {
        Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        Succeeded = succeeded;
        OccurredAtUtc = occurredAtUtc;
    }
}
