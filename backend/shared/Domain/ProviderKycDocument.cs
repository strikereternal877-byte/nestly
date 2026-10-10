using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>Category of KYC document a provider can submit (PROVIDER.md "provider_kyc_document").</summary>
public enum ProviderKycDocumentType
{
    IdentityProof,
    AddressProof,
    BankAccountProof,
    ProfessionalCertificate,
    Other
}

/// <summary>
/// Review outcome of one submitted document. Approval/rejection is task
/// 150b (admin-side, not built here) - this module only builds the
/// submission side (task 146c) and exposes the transition methods for that
/// future admin workflow to call.
/// </summary>
/// <remarks>
/// Stored via <c>HasConversion&lt;string&gt;()</c> (see
/// <c>ProviderKycDocumentConfiguration</c>), so unlike the strictly
/// append-only, ordinal-sensitive enums elsewhere in this codebase
/// (<c>NotificationEventType</c>, <c>BookingStatus</c>), inserting
/// <see cref="Superseded"/> here is safe regardless of position.
/// </remarks>
public enum ProviderKycVerificationStatus
{
    Pending,
    Approved,
    Rejected,

    /// <summary>
    /// Task 349: replaced by a newer document of the same
    /// <see cref="ProviderKycDocument.DocType"/> - set on the OLD row the
    /// moment a provider submits a new one, so at most one
    /// Pending-or-Approved document ever exists per (provider, doc type).
    /// Without this, a provider resubmitting an already-approved doc type
    /// (an expired ID, a changed address) left two live rows - the approved
    /// original and a new Pending one - with nothing distinguishing which is
    /// current, and no way for admins to tell a genuine re-review request
    /// from queue noise. Terminal, like <see cref="Rejected"/>: never
    /// admin-approved/rejected once superseded (see
    /// <c>ProviderKycApprovalService</c>'s <c>AlreadyReviewed</c> guard).
    /// </summary>
    Superseded
}

/// <summary>
/// One KYC document submitted by a provider (PROVIDER.md "provider_kyc_document:
/// doc_type, doc_number, file_ref, verification_status, verified_by,
/// verified_at"). <see cref="FileRef"/> is a reference (storage key/URL) to
/// the uploaded file, not the file content itself - matching how the rest of
/// this codebase handles media (see <c>CmsMedia</c>).
/// </summary>
public class ProviderKycDocument : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public ProviderKycDocumentType DocType { get; private set; }
    public string? DocNumber { get; private set; }
    public string FileRef { get; private set; } = string.Empty;
    public ProviderKycVerificationStatus VerificationStatus { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public DateTime? VerifiedAt { get; private set; }
    public DateTime SubmittedAt { get; private set; }

    /// <summary>Why an admin rejected this document - null except after <see cref="Reject"/>. Shown back to the provider so a rejection is actionable, not a silent dead end (mirrors <c>Provider.PhotoModerationNote</c>).</summary>
    public string? RejectionReason { get; private set; }

    protected ProviderKycDocument() { }

    public ProviderKycDocument(Guid id, Guid providerId, ProviderKycDocumentType docType, string fileRef, string? docNumber = null)
        : base(id)
    {
        ProviderId = providerId;
        DocType = docType;
        FileRef = string.IsNullOrWhiteSpace(fileRef)
            ? throw new ArgumentException("A file reference is required.", nameof(fileRef))
            : fileRef;
        DocNumber = docNumber;
        VerificationStatus = ProviderKycVerificationStatus.Pending;
        SubmittedAt = DateTime.UtcNow;
    }

    /// <summary>Admin approval (task 150b). Not called by anything in this pass - built for that future workflow.</summary>
    public void Approve(Guid verifiedByAdminUserId)
    {
        VerificationStatus = ProviderKycVerificationStatus.Approved;
        VerifiedBy = verifiedByAdminUserId;
        VerifiedAt = DateTime.UtcNow;
    }

    /// <summary>Admin rejection (task 150b). <paramref name="reason"/> is required - a rejected provider must be told why, not left to guess.</summary>
    public void Reject(Guid verifiedByAdminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        VerificationStatus = ProviderKycVerificationStatus.Rejected;
        VerifiedBy = verifiedByAdminUserId;
        VerifiedAt = DateTime.UtcNow;
        RejectionReason = reason.Trim();
    }

    /// <summary>
    /// Retired by a newer submission of the same <see cref="DocType"/> (task
    /// 349), called on the OLD document by <c>ProviderKycService.SubmitDocumentAsync</c>.
    /// A no-op past <see cref="ProviderKycVerificationStatus.Rejected"/>: a
    /// rejected document is already inactive history, not a second "current"
    /// row competing with the new submission, so leaving it Rejected keeps
    /// its reason intact instead of relabelling it as merely superseded.
    /// </summary>
    public void Supersede()
    {
        if (VerificationStatus == ProviderKycVerificationStatus.Rejected)
        {
            return;
        }

        VerificationStatus = ProviderKycVerificationStatus.Superseded;
    }

    /// <summary>
    /// Called by the provider's own right-to-erasure deletion
    /// (<c>ProviderManagementService.DeleteAsync</c>) once the underlying
    /// file has been removed from storage. The row itself is kept - same
    /// "financial/job history is retained" reasoning as <c>Provider.SoftDelete</c>,
    /// since this document's review outcome and timestamps remain part of
    /// that provider's onboarding history - but <see cref="FileRef"/> is
    /// non-nullable and constructor-validated as non-empty, so it cannot
    /// simply be cleared; it is overwritten with a placeholder instead so it
    /// stops pointing at content that no longer exists.
    /// </summary>
    public void PurgeFile()
    {
        FileRef = "[erased]";
    }
}
