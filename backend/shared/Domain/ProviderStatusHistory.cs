using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// An append-only record of a single provider status change (Provider
/// Management UX pass: previously a suspend reason was captured by the
/// admin UI, validated, and claimed to be "recorded to the audit trail" -
/// but there was no audit trail for it to go to). Mirrors
/// <see cref="BookingStatusHistory"/> exactly: never updated or deleted once
/// written, and <see cref="Provider"/> is the only thing that creates rows
/// here, through <see cref="Provider.ChangeStatus"/>/<see cref="Provider.SoftDelete"/>.
/// </summary>
public class ProviderStatusHistory : Entity<Guid>
{
    public Guid ProviderId { get; private set; }

    /// <summary>Null only for the first row, recording the provider's initial status at registration.</summary>
    public ProviderStatus? FromStatus { get; private set; }

    public ProviderStatus ToStatus { get; private set; }
    public string? Reason { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }

    protected ProviderStatusHistory() { }

    public ProviderStatusHistory(Guid id, Guid providerId, ProviderStatus? fromStatus, ProviderStatus toStatus, string? reason, DateTime changedAtUtc)
        : base(id)
    {
        ProviderId = providerId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        ChangedAtUtc = changedAtUtc;
    }
}
