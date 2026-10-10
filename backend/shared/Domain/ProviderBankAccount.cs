using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// Review outcome of a provider's submitted bank account details. Named and
/// shaped after <see cref="ProviderKycVerificationStatus"/> (Pending/Approve/
/// Reject lifecycle), but with no "Superseded" state: unlike
/// <see cref="ProviderKycDocument"/>, which keeps a growing, append-only list
/// of submissions per (provider, doc type), a <see cref="ProviderBankAccount"/>
/// is a single upsert-in-place row per provider - a resubmission calls
/// <see cref="ProviderBankAccount.UpdateDetails"/> on the SAME row rather than
/// creating a new one, so there is never a second, superseded row to mark.
/// </summary>
/// <remarks>
/// Stored via <c>HasConversion&lt;string&gt;()</c> (see
/// <c>ProviderBankAccountConfiguration</c>), so - like
/// <see cref="ProviderKycVerificationStatus"/> - this is safe to extend
/// regardless of position; it is still appended-to as a matter of house style,
/// not correctness.
/// </remarks>
public enum ProviderBankAccountVerificationStatus
{
    Pending,
    Verified,
    Rejected
}

/// <summary>
/// A provider's structured bank account details for payouts (docs/PROVIDER.md
/// OPEN DECISIONS #3: "v1 payouts are manual bank transfer"). Before this,
/// the only bank-related record was <see cref="ProviderKycDocument"/> with
/// <see cref="ProviderKycDocumentType.BankAccountProof"/> - an uploaded
/// photo/PDF of a cheque or passbook, plus a generic, unvalidated
/// <c>DocNumber</c> free-text field - so an admin processing a payout had to
/// open that image and manually transcribe an account number/IFSC, with real
/// risk of a transcription error sending money to the wrong account. This
/// entity is the structured, admin-verifiable record actually used
/// operationally; the KYC document upload remains alongside it as supporting
/// evidence (it is not removed or deprecated).
///
/// One row per provider (see <see cref="ProviderBankAccountConfiguration"/>'s
/// unique index on <c>ProviderId</c>) with upsert semantics - a provider
/// editing their bank details always calls <see cref="UpdateDetails"/> on
/// their existing row and always resets it to <see cref="ProviderBankAccountVerificationStatus.Pending"/>,
/// since a changed account number/IFSC must always be re-verified before an
/// admin can trust it again.
///
/// Deliberately NOT a payout gate for the manual flow (product decision,
/// task brief scope #3): <c>ProviderPayoutService.CreateBatchAsync</c>/
/// <c>UpdateStatusAsync</c> do not check this entity's <see cref="VerificationStatus"/>
/// and never refuse to proceed based on it - store-and-display only,
/// surfaced to the admin processing a manual payout so they can visually
/// double check it, not an enforced precondition. The real PayU Payouts
/// integration is the one exception: <c>ProviderPayoutService.PayViaPayUAsync</c>
/// DOES require <see cref="ProviderBankAccountVerificationStatus.Verified"/>
/// before it will call out to PayU with these details - a real, unattended
/// bank transfer via someone else's API is a materially different risk than
/// an admin manually typing a reference after eyeballing the same numbers.
/// </summary>
public class ProviderBankAccount : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public string AccountHolderName { get; private set; } = string.Empty;
    public string AccountNumber { get; private set; } = string.Empty;
    public string IfscCode { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public ProviderBankAccountVerificationStatus VerificationStatus { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public DateTime? VerifiedAt { get; private set; }

    /// <summary>Why an admin rejected these details - null except after <see cref="Reject"/>. Shown back to the provider so a rejection is actionable, mirrors <see cref="ProviderKycDocument.RejectionReason"/>.</summary>
    public string? RejectionReason { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    protected ProviderBankAccount() { }

    public ProviderBankAccount(Guid id, Guid providerId, string accountHolderName, string accountNumber, string ifscCode, string bankName)
        : base(id)
    {
        ProviderId = providerId;
        AccountHolderName = RequireNonEmpty(accountHolderName, nameof(accountHolderName));
        AccountNumber = RequireNonEmpty(accountNumber, nameof(accountNumber));
        IfscCode = RequireNonEmpty(ifscCode, nameof(ifscCode));
        BankName = RequireNonEmpty(bankName, nameof(bankName));
        VerificationStatus = ProviderBankAccountVerificationStatus.Pending;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A resubmission over the SAME row (upsert, not append - see this type's
    /// own doc comment). Always resets verification back to
    /// <see cref="ProviderBankAccountVerificationStatus.Pending"/> and clears
    /// any prior <see cref="VerifiedBy"/>/<see cref="VerifiedAt"/>/<see cref="RejectionReason"/>:
    /// a provider editing already-approved details must not leave the old
    /// approval standing over new, unreviewed numbers.
    /// </summary>
    public void UpdateDetails(string accountHolderName, string accountNumber, string ifscCode, string bankName)
    {
        AccountHolderName = RequireNonEmpty(accountHolderName, nameof(accountHolderName));
        AccountNumber = RequireNonEmpty(accountNumber, nameof(accountNumber));
        IfscCode = RequireNonEmpty(ifscCode, nameof(ifscCode));
        BankName = RequireNonEmpty(bankName, nameof(bankName));
        VerificationStatus = ProviderBankAccountVerificationStatus.Pending;
        VerifiedBy = null;
        VerifiedAt = null;
        RejectionReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Admin approval.</summary>
    public void Approve(Guid verifiedByAdminUserId)
    {
        VerificationStatus = ProviderBankAccountVerificationStatus.Verified;
        VerifiedBy = verifiedByAdminUserId;
        VerifiedAt = DateTime.UtcNow;
        RejectionReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Admin rejection. <paramref name="reason"/> is required - a rejected provider must be told why, not left to guess (mirrors <see cref="ProviderKycDocument.Reject"/>).</summary>
    public void Reject(Guid verifiedByAdminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        VerificationStatus = ProviderBankAccountVerificationStatus.Rejected;
        VerifiedBy = verifiedByAdminUserId;
        VerifiedAt = DateTime.UtcNow;
        RejectionReason = reason.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Called by the provider's own right-to-erasure deletion
    /// (<c>ProviderManagementService.DeleteAsync</c>), mirroring
    /// <see cref="ProviderKycDocument.PurgeFile"/>'s reasoning: the row itself
    /// is kept (its verification history remains part of the provider's
    /// financial/onboarding record, same "financial/job history is retained"
    /// rule as <c>Provider.SoftDelete</c>), but the actual account details are
    /// overwritten - unlike a KYC document's file reference, these are live
    /// bank credentials with no ongoing operational need once the account is
    /// gone, not just a pointer to something already purged elsewhere.
    /// </summary>
    public void Erase()
    {
        AccountHolderName = "[erased]";
        AccountNumber = "[erased]";
        IfscCode = "[erased]";
        BankName = "[erased]";
        UpdatedAt = DateTime.UtcNow;
    }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", paramName)
            : value.Trim();
}
