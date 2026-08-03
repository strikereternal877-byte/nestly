using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// Whether a provider is an individual technician or a company with
/// sub-technicians (PROVIDER.md DATA MODEL). PROVIDER.md's OPEN DECISIONS #2
/// resolved that v1 supports individuals only — <see cref="Company"/> exists
/// in the enum so the column/shape does not need to change when that is
/// extended later, but <see cref="Provider"/>'s constructor currently rejects
/// it.
/// </summary>
public enum ProviderType
{
    Individual,
    Company
}

/// <summary>
/// Lifecycle status of a provider account (PROVIDER.md DATA MODEL). A provider
/// starts <see cref="PendingVerification"/> (mobile ownership proven by OTP
/// at registration, but KYC not yet reviewed) and only an admin moves it to
/// <see cref="Active"/> once KYC is approved (task 150b, not built here).
/// </summary>
public enum ProviderStatus
{
    PendingVerification,
    Active,
    Suspended,
    Deactivated
}

/// <summary>
/// Where a provider is in the onboarding funnel (PROVIDER.md DATA MODEL
/// "onboarding_status"), independent of <see cref="ProviderStatus"/>: status
/// is the account's operational state, onboarding_status is progress through
/// the one-time setup flow that gates it.
/// </summary>
public enum ProviderOnboardingStatus
{
    Registered,
    ProfileCompleted,
    KycSubmitted,
    KycVerified,
    Completed
}

/// <summary>
/// A service provider who fulfils bookings (PROVIDER.md "Provider" module).
/// Deliberately independent of <see cref="Customer"/> — same shape
/// (identity + status) for a different actor, not a specialization of it,
/// matching this module's SCOPE BOUNDARY of staying extractable on its own.
/// </summary>
public class Provider : Entity<Guid>
{
    public string LegalName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public ProviderType ProviderType { get; private set; }
    public string Phone { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public ProviderStatus Status { get; private set; }
    public ProviderOnboardingStatus OnboardingStatus { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    protected Provider() { }

    public Provider(
        Guid id,
        string legalName,
        string displayName,
        ProviderType providerType,
        string phone,
        string? email = null)
        : base(id)
    {
        if (providerType != ProviderType.Individual)
        {
            // OPEN DECISIONS #2: company providers with sub-technicians are
            // not supported in v1 - see this type's doc comment.
            throw new ArgumentException(
                "Only individual providers are supported in this release.", nameof(providerType));
        }

        LegalName = string.IsNullOrWhiteSpace(legalName)
            ? throw new ArgumentException("Legal name is required.", nameof(legalName))
            : legalName;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("Display name is required.", nameof(displayName))
            : displayName;
        ProviderType = providerType;
        Phone = string.IsNullOrWhiteSpace(phone)
            ? throw new ArgumentException("Phone is required.", nameof(phone))
            : phone;
        Email = email;

        // OTP at registration only proves mobile ownership, not KYC - the
        // account cannot become Active until an admin approves KYC
        // (PROVIDER.md API surface "KYC approval", task 150b).
        Status = ProviderStatus.PendingVerification;
        OnboardingStatus = ProviderOnboardingStatus.Registered;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateProfile(string legalName, string displayName, string? email)
    {
        LegalName = string.IsNullOrWhiteSpace(legalName)
            ? throw new ArgumentException("Legal name is required.", nameof(legalName))
            : legalName;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("Display name is required.", nameof(displayName))
            : displayName;
        Email = email;
        UpdatedAt = DateTime.UtcNow;

        if (OnboardingStatus == ProviderOnboardingStatus.Registered)
        {
            OnboardingStatus = ProviderOnboardingStatus.ProfileCompleted;
        }
    }

    /// <summary>Applied only after an OTP proved control of the new number (mirrors <c>Customer.ChangeMobile</c>).</summary>
    public void ChangePhone(string newPhone)
    {
        Phone = string.IsNullOrWhiteSpace(newPhone)
            ? throw new ArgumentException("Phone is required.", nameof(newPhone))
            : newPhone;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances onboarding once a KYC document has been submitted (task
    /// 146c). Idempotent - resubmitting further documents does not move the
    /// funnel backwards.
    /// </summary>
    public void MarkKycSubmitted()
    {
        if (OnboardingStatus is ProviderOnboardingStatus.Registered or ProviderOnboardingStatus.ProfileCompleted)
        {
            OnboardingStatus = ProviderOnboardingStatus.KycSubmitted;
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Admin-driven transitions (KYC approval task 150b, suspend/reactivate).
    /// Kept generic here rather than one method per transition since this
    /// entity does not yet encode which transitions are legal from which
    /// state - that workflow belongs to the admin service that will call it.
    /// In particular, the "KYC approved AND background check passed" gate
    /// (task 160) before a transition to <see cref="ProviderStatus.Active"/>
    /// is enforced by <c>IProviderKycApprovalService.ActivateAsync</c>, not
    /// here - consistent with this method staying transition-agnostic.
    /// </summary>
    public void ChangeStatus(ProviderStatus status)
    {
        Status = status;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances onboarding once an admin has approved at least one submitted
    /// KYC document (task 150b - the admin-side counterpart to
    /// <see cref="MarkKycSubmitted"/>). Idempotent, mirroring that method.
    /// </summary>
    public void MarkKycVerified()
    {
        if (OnboardingStatus == ProviderOnboardingStatus.KycSubmitted)
        {
            OnboardingStatus = ProviderOnboardingStatus.KycVerified;
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Onboarding is fully done once the provider has been activated (KYC verified and background check passed - task 160).</summary>
    public void MarkOnboardingCompleted()
    {
        OnboardingStatus = ProviderOnboardingStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }
}
