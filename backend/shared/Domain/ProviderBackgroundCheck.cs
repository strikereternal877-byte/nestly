using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>Outcome of a background/reference check (task 160).</summary>
public enum ProviderBackgroundCheckStatus
{
    Pending,
    Passed,
    Failed
}

/// <summary>
/// A distinct post-KYC background/reference check step (task 160,
/// PROVIDER.md) - police verification, prior work history, etc. Kept as its
/// own small entity rather than a field on <see cref="Provider"/> because it
/// is a policy-configurable, admin-recorded event with its own
/// who/when/why (<see cref="CheckedBy"/>/<see cref="CheckedAt"/>/<see cref="Notes"/>),
/// separate from KYC document validation (task 150b). Append-only, like
/// <see cref="ProviderKycDocument"/> - a re-check creates a new row rather
/// than overwriting the previous outcome, so <see cref="Provider"/>'s
/// activation gate always reads the most recent one
/// (<c>IProviderBackgroundCheckRepository.GetLatestByProviderAsync</c>).
/// </summary>
public class ProviderBackgroundCheck : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public ProviderBackgroundCheckStatus Status { get; private set; }
    public Guid CheckedBy { get; private set; }
    public DateTime CheckedAt { get; private set; }
    public string? Notes { get; private set; }

    protected ProviderBackgroundCheck() { }

    public ProviderBackgroundCheck(Guid id, Guid providerId, ProviderBackgroundCheckStatus status, Guid checkedBy, string? notes)
        : base(id)
    {
        if (status == ProviderBackgroundCheckStatus.Pending)
        {
            throw new ArgumentException("A background check record must be created with a final Passed/Failed outcome.", nameof(status));
        }

        ProviderId = providerId;
        Status = status;
        CheckedBy = checkedBy;
        CheckedAt = DateTime.UtcNow;
        Notes = notes;
    }
}
