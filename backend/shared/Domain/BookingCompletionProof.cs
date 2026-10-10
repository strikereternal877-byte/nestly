using System.Text.Json;
using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>One checklist item's answer captured at job completion (tasks 195, 197).</summary>
public sealed record CompletionChecklistAnswer(string Item, bool Completed, string? Notes);

/// <summary>
/// Admin review outcome of a submitted completion proof. Mirrors
/// <see cref="ProviderKycVerificationStatus"/> deliberately - both are "an
/// admin approves or rejects provider-submitted evidence before something
/// consequential happens" gates, and match on shape rather than fields
/// (no <c>Superseded</c> here: a resubmission overwrites the one row per
/// booking, see <see cref="BookingCompletionProof.Update"/>, rather than
/// leaving an old row to supersede).
/// </summary>
public enum CompletionProofReviewStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// Evidence a provider submits that a job was actually completed
/// (PRODUCT-ENHANCEMENTS.md "Service Completion Verification", tasks
/// 195-198): one or more photo references plus a checklist.
/// <see cref="BookingLifecycle"/>'s InProgress -&gt; Completed transition
/// requires a row like this to exist for the booking (task 196) AND that
/// row's <see cref="ReviewStatus"/> to be <see cref="CompletionProofReviewStatus.Approved"/> -
/// enforced in the application layer
/// (<c>ProviderJobService.CompleteAsync</c> stops at submission, leaving the
/// booking InProgress; <c>BookingManagementService.ApproveCompletionProofAsync</c>
/// performs the actual transition once an admin signs off), not here or on
/// <see cref="Booking"/> itself, since checking another aggregate's
/// existence/status is not this entity's (or Booking's) own invariant to
/// know about - it needs a repository, which a domain entity does not have.
/// <para>
/// <see cref="PhotoRefs"/> are storage keys/URLs to already-uploaded files
/// (matching <see cref="ProviderKycDocument.FileRef"/> and
/// <see cref="BookingProviderAssignment.CompletionProofRef"/> - never binary
/// content). Both collections are persisted as JSON strings (matching how
/// <see cref="AuditLog.OldValues"/>/<see cref="AuditLog.NewValues"/> already
/// store structured data this codebase has no dedicated child table for) -
/// see <see cref="PhotoRefsJson"/>/<see cref="ChecklistAnswersJson"/>.
/// </para>
/// <para>
/// One row per booking: a resubmission (<see cref="Update"/>) replaces the
/// previous evidence rather than appending a new row, since only the latest
/// submission is meaningful evidence for the task 196 guard and for
/// dispute review (task 198).
/// </para>
/// </summary>
public class BookingCompletionProof : Entity<Guid>
{
    public Guid BookingId { get; private set; }
    public Guid SubmittedByProviderId { get; private set; }
    public DateTime SubmittedAtUtc { get; private set; }

    /// <summary>JSON-serialized <c>List&lt;string&gt;</c> - the persisted form of <see cref="PhotoRefs"/>.</summary>
    public string PhotoRefsJson { get; private set; } = "[]";

    /// <summary>JSON-serialized <c>List&lt;CompletionChecklistAnswer&gt;</c> - the persisted form of <see cref="ChecklistAnswers"/>.</summary>
    public string ChecklistAnswersJson { get; private set; } = "[]";

    public CompletionProofReviewStatus ReviewStatus { get; private set; } = CompletionProofReviewStatus.Pending;
    public Guid? ReviewedBy { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }

    /// <summary>Required on <see cref="Reject"/>, same as <c>ProviderKycDocument</c>'s admin rejection - the provider needs to know what to fix before resubmitting.</summary>
    public string? RejectionReason { get; private set; }

    protected BookingCompletionProof() { }

    public BookingCompletionProof(
        Guid id,
        Guid bookingId,
        Guid submittedByProviderId,
        IReadOnlyList<string> photoRefs,
        IReadOnlyList<CompletionChecklistAnswer> checklistAnswers)
        : base(id)
    {
        BookingId = bookingId;
        SubmittedByProviderId = submittedByProviderId;
        Apply(photoRefs, checklistAnswers);
    }

    public IReadOnlyList<string> PhotoRefs =>
        JsonSerializer.Deserialize<List<string>>(PhotoRefsJson) ?? [];

    public IReadOnlyList<CompletionChecklistAnswer> ChecklistAnswers =>
        JsonSerializer.Deserialize<List<CompletionChecklistAnswer>>(ChecklistAnswersJson) ?? [];

    /// <summary>Replaces this proof's evidence with a resubmission (e.g. the provider adds a missed photo before the job is actually marked Completed, or resubmits after <see cref="Reject"/>). Always returns to <see cref="CompletionProofReviewStatus.Pending"/> - an admin's prior verdict was against the old evidence, not this one.</summary>
    public void Update(IReadOnlyList<string> photoRefs, IReadOnlyList<CompletionChecklistAnswer> checklistAnswers) =>
        Apply(photoRefs, checklistAnswers);

    /// <summary>Admin approves the submitted evidence - the caller (BookingManagementService) is then clear to transition the booking to Completed.</summary>
    public void Approve(Guid adminUserId)
    {
        ReviewStatus = CompletionProofReviewStatus.Approved;
        ReviewedBy = adminUserId;
        ReviewedAtUtc = DateTime.UtcNow;
        RejectionReason = null;
    }

    /// <summary>Admin rejects the submitted evidence - the caller reopens the booking to InProgress so the provider can finish the job and resubmit.</summary>
    public void Reject(Guid adminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required when rejecting a completion proof.", nameof(reason));
        }

        ReviewStatus = CompletionProofReviewStatus.Rejected;
        ReviewedBy = adminUserId;
        ReviewedAtUtc = DateTime.UtcNow;
        RejectionReason = reason;
    }

    private void Apply(IReadOnlyList<string> photoRefs, IReadOnlyList<CompletionChecklistAnswer> checklistAnswers)
    {
        if (photoRefs is null || photoRefs.Count == 0 || photoRefs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty photo reference is required.", nameof(photoRefs));
        }

        PhotoRefsJson = JsonSerializer.Serialize(photoRefs);
        ChecklistAnswersJson = JsonSerializer.Serialize(checklistAnswers ?? []);
        SubmittedAtUtc = DateTime.UtcNow;
        ReviewStatus = CompletionProofReviewStatus.Pending;
        ReviewedBy = null;
        ReviewedAtUtc = null;
        RejectionReason = null;
    }
}
