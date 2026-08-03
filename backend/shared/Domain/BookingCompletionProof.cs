using System.Text.Json;
using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>One checklist item's answer captured at job completion (tasks 195, 197).</summary>
public sealed record CompletionChecklistAnswer(string Item, bool Completed, string? Notes);

/// <summary>
/// Evidence a provider submits that a job was actually completed
/// (PRODUCT-ENHANCEMENTS.md "Service Completion Verification", tasks
/// 195-198): one or more photo references plus a checklist.
/// <see cref="BookingLifecycle"/>'s InProgress -&gt; Completed transition is
/// conditional on a row like this existing for the booking (task 196) -
/// enforced in the application layer
/// (<c>ProviderJobService.CompleteAsync</c>,
/// <c>BookingManagementService.UpdateStatusAsync</c>), not here or on
/// <see cref="Booking"/> itself, since checking another aggregate's
/// existence is not this entity's (or Booking's) own invariant to know
/// about - it needs a repository, which a domain entity does not have.
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

    /// <summary>Replaces this proof's evidence with a resubmission (e.g. the provider adds a missed photo before the job is actually marked Completed).</summary>
    public void Update(IReadOnlyList<string> photoRefs, IReadOnlyList<CompletionChecklistAnswer> checklistAnswers) =>
        Apply(photoRefs, checklistAnswers);

    private void Apply(IReadOnlyList<string> photoRefs, IReadOnlyList<CompletionChecklistAnswer> checklistAnswers)
    {
        if (photoRefs is null || photoRefs.Count == 0 || photoRefs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty photo reference is required.", nameof(photoRefs));
        }

        PhotoRefsJson = JsonSerializer.Serialize(photoRefs);
        ChecklistAnswersJson = JsonSerializer.Serialize(checklistAnswers ?? []);
        SubmittedAtUtc = DateTime.UtcNow;
    }
}
