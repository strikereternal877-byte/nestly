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
/// Review outcome of a provider-supplied profile photo (task 293). Mirrors
/// <see cref="ProviderKycVerificationStatus"/> deliberately: a photo is
/// user-supplied content that customers see, so it goes through the same
/// pending/approved/rejected gate this codebase already applies to every
/// other file a provider hands us.
/// </summary>
public enum ProviderPhotoModerationStatus
{
    Pending,
    Approved,
    Rejected
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

    /// <summary>
    /// Task 243: where this provider actually is, for the automatic-assignment
    /// engine's distance ranking (PROVIDER.md OPEN DECISIONS - AUTOMATIC
    /// ASSIGNMENT #1, task 244). Null until set via <see cref="UpdateLocation"/>
    /// - a provider with no coordinates simply can't be auto-matched (falls
    /// through to the existing manual admin queue), not a hard onboarding
    /// blocker. Same decimal(9,6) shape as CustomerAddress.Latitude/Longitude,
    /// not a PostGIS geography column - no such package exists anywhere in
    /// this solution (see the OPEN DECISIONS entry).
    /// </summary>
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }

    /// <summary>
    /// Task 268: when the coordinates above were observed, null whenever they
    /// are. Without it the pair is a last-known location with no age, so a fix
    /// from three seconds ago and one from three days ago are
    /// indistinguishable - and only one of them should be routed to a customer
    /// as "your technician is here". Not <c>UpdatedAt</c>: that moves on every
    /// profile edit, which says nothing about the location.
    /// </summary>
    public DateTime? LocationUpdatedAtUtc { get; private set; }

    /// <summary>
    /// Task 293: the provider's profile photo, as a reference (storage
    /// key/URL) to an already-hosted image - the same reference-only
    /// convention <see cref="ProviderKycDocument.FileRef"/>, <c>CmsMedia.Url</c>
    /// and <c>BookingCompletionProof</c>'s photo refs already use. This
    /// solution has no blob-storage abstraction and this column does not
    /// introduce one.
    ///
    /// Null until the provider sets one. Never read directly by a
    /// customer-facing mapper - see <see cref="PublicPhotoUrl"/>.
    /// </summary>
    public string? PhotoUrl { get; private set; }

    /// <summary>
    /// Null exactly when <see cref="PhotoUrl"/> is (same both-or-neither
    /// discipline as the location pair above): a moderation verdict for a
    /// photo that does not exist is worse than no verdict. Otherwise it is
    /// the current review state of the photo actually stored.
    /// </summary>
    public ProviderPhotoModerationStatus? PhotoModerationStatus { get; private set; }

    /// <summary>Admin who last approved or rejected the photo - traceability only, not a foreign key (same rationale as <see cref="Review.ModeratedByAdminUserId"/>).</summary>
    public Guid? PhotoModeratedByAdminUserId { get; private set; }

    public DateTime? PhotoModeratedAtUtc { get; private set; }

    /// <summary>The moderator's reason for the most recent verdict, shown back to the provider so a rejection is actionable rather than silent.</summary>
    public string? PhotoModerationNote { get; private set; }

    /// <summary>This provider's own shareable referral code (PROVIDER-REFERRAL.md), mirroring <see cref="Customer.ReferralCode"/>. Null until first requested - generated lazily, not at signup, since most providers never share it.</summary>
    public string? ReferralCode { get; private set; }

    /// <summary>
    /// The moderation gate, expressed once. Every customer-facing surface
    /// (<c>BookingProviderSummary</c>, <c>TrackedProviderSummary</c>) reads
    /// this and never <see cref="PhotoUrl"/>, so a photo that is still
    /// Pending or was Rejected cannot reach a customer's screen through some
    /// mapper that forgot to check the status. Deliberately a property on the
    /// entity rather than a filter each caller applies: a gate re-implemented
    /// per call site is a gate that will eventually be missed.
    /// </summary>
    public string? PublicPhotoUrl =>
        PhotoModerationStatus == ProviderPhotoModerationStatus.Approved ? PhotoUrl : null;

    private readonly List<ProviderStatusHistory> _statusHistory = [];

    /// <summary>Append-only status change log (Provider Management UX pass), mirrors <see cref="Booking.StatusHistory"/>.</summary>
    public IReadOnlyList<ProviderStatusHistory> StatusHistory => _statusHistory;

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
        RecordStatusHistory(null, Status, reason: null);
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

    /// <summary>
    /// Task 243. Both-or-neither: a lone latitude or longitude is not a
    /// usable coordinate, so this rejects the half-set case rather than
    /// silently persisting an unusable point. Pass both null to clear a
    /// previously set location (e.g. the provider relocated and hasn't
    /// re-shared a new one yet).
    /// </summary>
    /// <param name="observedAtUtc">
    /// Task 268: when the fix was actually taken, for callers that know -
    /// notably location-ping ingest, where a queued upload can deliver a fix
    /// minutes after the device took it and stamping "now" would make a stale
    /// position look fresh. Omitted by callers who are supplying coordinates
    /// rather than observing them (an admin editing the profile), who get the
    /// current time. Cleared along with the coordinates: a timestamp for a
    /// location that no longer exists is worse than none.
    /// </param>
    public void UpdateLocation(decimal? latitude, decimal? longitude, DateTime? observedAtUtc = null)
    {
        if (latitude.HasValue != longitude.HasValue)
        {
            throw new ArgumentException("Latitude and longitude must be set together.");
        }

        Latitude = latitude;
        Longitude = longitude;
        LocationUpdatedAtUtc = latitude.HasValue ? observedAtUtc ?? DateTime.UtcNow : null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Task 293: the provider sets or replaces their own profile photo.
    /// Always lands in <see cref="ProviderPhotoModerationStatus.Pending"/> -
    /// including a replacement of an already-approved photo, because
    /// otherwise swapping the image after approval would be a way to publish
    /// an unreviewed one under an old verdict. The previous verdict and note
    /// are cleared for the same reason: they described a different image.
    /// </summary>
    /// <param name="photoUrl">
    /// A reference to an already-hosted image. Rejected unless it is an
    /// absolute http/https URL: this value is rendered straight into an
    /// <c>img src</c> on a customer's screen, and a <c>javascript:</c> or
    /// <c>data:</c> reference there is script execution, not a picture. The
    /// check is here rather than only in the request validator so no future
    /// caller (an admin tool, a seed, a background import) can bypass it.
    /// </param>
    public void SubmitPhoto(string photoUrl)
    {
        if (!IsSafeImageReference(photoUrl))
        {
            throw new ArgumentException("A photo must be an absolute http or https URL.", nameof(photoUrl));
        }

        PhotoUrl = photoUrl.Trim();
        PhotoModerationStatus = ProviderPhotoModerationStatus.Pending;
        PhotoModeratedByAdminUserId = null;
        PhotoModeratedAtUtc = null;
        PhotoModerationNote = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Clears the photo and its whole moderation record together, keeping the null-exactly-when invariant on <see cref="PhotoModerationStatus"/>.</summary>
    public void RemovePhoto()
    {
        PhotoUrl = null;
        PhotoModerationStatus = null;
        PhotoModeratedByAdminUserId = null;
        PhotoModeratedAtUtc = null;
        PhotoModerationNote = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Admin approves the current photo (task 293) - the only transition that makes it visible to customers, via <see cref="PublicPhotoUrl"/>.</summary>
    public void ApprovePhoto(Guid moderatorAdminUserId, string? note = null) =>
        ApplyPhotoVerdict(ProviderPhotoModerationStatus.Approved, moderatorAdminUserId, note);

    /// <summary>Admin rejects the current photo. The image itself is kept, not wiped - the provider needs to see what was rejected, and the audit trail needs it to still exist.</summary>
    public void RejectPhoto(Guid moderatorAdminUserId, string? note = null) =>
        ApplyPhotoVerdict(ProviderPhotoModerationStatus.Rejected, moderatorAdminUserId, note);

    private void ApplyPhotoVerdict(ProviderPhotoModerationStatus status, Guid moderatorAdminUserId, string? note)
    {
        if (PhotoUrl is null)
        {
            throw new InvalidOperationException("This provider has no photo to moderate.");
        }

        PhotoModerationStatus = status;
        PhotoModeratedByAdminUserId = moderatorAdminUserId;
        PhotoModeratedAtUtc = DateTime.UtcNow;
        PhotoModerationNote = note;
        UpdatedAt = DateTime.UtcNow;
    }

    private static bool IsSafeImageReference(string photoUrl) =>
        !string.IsNullOrWhiteSpace(photoUrl)
        && Uri.TryCreate(photoUrl.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
    public void ChangeStatus(ProviderStatus status, string? reason = null)
    {
        var previousStatus = Status;
        Status = status;
        UpdatedAt = DateTime.UtcNow;
        RecordStatusHistory(previousStatus, status, reason);
    }

    /// <summary>
    /// Right-to-erasure account deletion (mirrors <c>Customer.SoftDelete</c>):
    /// terminal and irreversible, unlike <see cref="ChangeStatus"/>'s other
    /// transitions - there is no "undelete". Personally-identifying fields
    /// are overwritten with anonymized placeholders (derived from this
    /// provider's own id, so they satisfy unique-phone/unique-email
    /// constraints without colliding across deletions) rather than left in
    /// place, since financial/job history is retained (this row and its id
    /// stay). Login is blocked the moment <see cref="Status"/> leaves
    /// <see cref="ProviderStatus.Active"/> - the login service already gates
    /// on Suspended/Deactivated, so no separate "kill switch" is needed here.
    /// <paramref name="reason"/> lands in <see cref="StatusHistory"/> - unlike
    /// <c>Customer.SoftDelete</c>, which has no status-history table of its
    /// own and instead writes a <c>CustomerNote</c>, this project's audit
    /// trail for a provider's status changes.
    /// </summary>
    public void SoftDelete(string reason)
    {
        if (Status == ProviderStatus.Deactivated)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A deletion reason is required.", nameof(reason));
        }

        var previousStatus = Status;
        LegalName = "Deleted Provider";
        DisplayName = "Deleted Provider";
        Email = $"deleted+{Id:N}@deleted.glavyx.invalid";
        // Phone is varchar(20) (ProviderConfiguration) - the full "deleted-{32 hex chars}"
        // form is 40 characters and was silently never enforced by the test
        // database, but a real Postgres column rejects it outright (22001:
        // value too long), so every call to this method has been failing in
        // any environment with the real schema. Truncated to exactly 20:
        // "deleted-" (8) plus 12 hex characters retains far more entropy than
        // this app's realistic provider count could ever collide on, and a
        // collision would only ever be caught (safely) by the phone column's
        // own unique index, never silently overwrite another row.
        Phone = $"deleted-{Id:N}"[..20];
        Latitude = null;
        Longitude = null;
        LocationUpdatedAtUtc = null;
        RemovePhoto();
        Status = ProviderStatus.Deactivated;
        UpdatedAt = DateTime.UtcNow;
        RecordStatusHistory(previousStatus, Status, reason);
    }

    private void RecordStatusHistory(ProviderStatus? from, ProviderStatus to, string? reason) =>
        _statusHistory.Add(new ProviderStatusHistory(Guid.NewGuid(), Id, from, to, reason, DateTime.UtcNow));

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

    /// <summary>
    /// Assigns this provider's referral code (PROVIDER-REFERRAL.md, mirrors
    /// <see cref="Customer.SetReferralCode"/>). Set-once: a provider's code is
    /// stable for life once generated, so links already shared never break.
    /// Uniqueness is the caller's responsibility (the generating service
    /// checks against <c>IProviderRepository</c> before calling this) - the
    /// entity only enforces its own invariant.
    /// </summary>
    public void SetReferralCode(string code)
    {
        if (ReferralCode is not null)
        {
            throw new InvalidOperationException("Referral code is already assigned and cannot be changed.");
        }

        ReferralCode = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Referral code is required.", nameof(code))
            : code;
        UpdatedAt = DateTime.UtcNow;
    }
}
