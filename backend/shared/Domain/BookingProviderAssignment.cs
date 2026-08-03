using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// Who initiated a <see cref="BookingProviderAssignment"/> (PROVIDER.md
/// "Assignment Bridge": assigned_by admin/system). OPEN DECISIONS #1 (manual
/// admin-driven assignment in v1) means every row created today uses
/// <see cref="Admin"/> - <see cref="System"/> exists so a future automatic
/// dispatch engine can write to this same table without a schema change.
/// </summary>
public enum BookingAssignedByType
{
    Admin,
    System
}

/// <summary>
/// Lifecycle of one provider's offer to fulfil a booking (PROVIDER.md
/// "Assignment Bridge"). <see cref="Assigned"/> is the initial, outstanding
/// state; <see cref="Accepted"/>/<see cref="Rejected"/> are the provider's
/// response; <see cref="Reassigned"/> marks a row that was superseded by a
/// newer assignment before the provider responded (task 147/159) - per
/// PROVIDER.md OPEN DECISIONS #5, only one row per booking is ever "live"
/// (Assigned or Accepted) at a time. <see cref="Withdrawn"/> marks a
/// still-live (Assigned or Accepted) row whose booking was cancelled out
/// from under it (task 208) - distinct from <see cref="Rejected"/> (the
/// provider's own choice) and <see cref="Reassigned"/> (superseded by another
/// assignment), since here nobody responded, the booking itself just stopped
/// needing fulfilment.
/// </summary>
public enum BookingProviderAssignmentStatus
{
    Assigned,
    Accepted,
    Rejected,
    Reassigned,
    Withdrawn
}

/// <summary>
/// The one bridge entity <see cref="Booking"/> is allowed to reference into
/// the Provider module (PROVIDER.md SCOPE BOUNDARY, task 147): which provider
/// was offered a booking, by whom, and how they responded. History is kept -
/// a rejected or superseded row is never deleted or overwritten - so the
/// admin/provider UI can show a full assignment trail per booking (task 159:
/// "surfaces the booking as needs reassignment").
/// </summary>
public class BookingProviderAssignment : Entity<Guid>
{
    public Guid BookingId { get; private set; }
    public Guid ProviderId { get; private set; }
    public BookingAssignedByType AssignedByType { get; private set; }

    /// <summary>The acting admin user's id when <see cref="AssignedByType"/> is <see cref="BookingAssignedByType.Admin"/>; null for a future system-driven assignment.</summary>
    public Guid? AssignedByUserId { get; private set; }

    public DateTime AssignedAt { get; private set; }
    public BookingProviderAssignmentStatus Status { get; private set; }

    /// <summary>How long the provider has to respond before the assignment is considered stale (advisory - no background job enforces this in v1).</summary>
    public DateTime? ResponseDeadline { get; private set; }

    public DateTime? RespondedAt { get; private set; }

    /// <summary>Rejection reason or admin notes, set when the provider responds or the assignment is superseded.</summary>
    public string? Notes { get; private set; }

    /// <summary>
    /// Reference (storage key/URL) to completion evidence the provider
    /// uploaded (task 149a, PROVIDER.md API surface "upload completion
    /// proof") - a reference only, mirroring how <see cref="ProviderKycDocument.FileRef"/>
    /// stores an already-uploaded file's reference rather than binary
    /// content. Lives here rather than on <see cref="Booking"/> because it is
    /// specific to this assignment's execution, not booking-wide state - the
    /// SCOPE BOUNDARY only allows one denormalized field on Booking, already
    /// spent on <see cref="Booking.AssignedProviderId"/>.
    /// </summary>
    public string? CompletionProofRef { get; private set; }

    protected BookingProviderAssignment() { }

    public BookingProviderAssignment(
        Guid id,
        Guid bookingId,
        Guid providerId,
        BookingAssignedByType assignedByType,
        Guid? assignedByUserId,
        DateTime? responseDeadline)
        : base(id)
    {
        if (assignedByType == BookingAssignedByType.Admin && assignedByUserId is null)
        {
            throw new ArgumentException("An acting admin user id is required for an admin-driven assignment.", nameof(assignedByUserId));
        }

        BookingId = bookingId;
        ProviderId = providerId;
        AssignedByType = assignedByType;
        AssignedByUserId = assignedByUserId;
        AssignedAt = DateTime.UtcNow;
        Status = BookingProviderAssignmentStatus.Assigned;
        ResponseDeadline = responseDeadline;
    }

    /// <summary>The provider accepts the job (task 151, provider-api - exposed here so that future controller can call it through the same shared service).</summary>
    public void Accept()
    {
        EnsureOutstanding();
        Status = BookingProviderAssignmentStatus.Accepted;
        RespondedAt = DateTime.UtcNow;
    }

    /// <summary>The provider rejects the job (task 159) - the booking's own re-assignment handling lives in <c>IBookingProviderAssignmentService.RejectAsync</c>, not here.</summary>
    public void Reject(string? reason)
    {
        EnsureOutstanding();
        Status = BookingProviderAssignmentStatus.Rejected;
        RespondedAt = DateTime.UtcNow;
        Notes = reason;
    }

    /// <summary>Marks this row superseded because a newer assignment was created for the same booking before this one was responded to (task 147/159).</summary>
    public void MarkReassigned()
    {
        if (Status is not (BookingProviderAssignmentStatus.Assigned or BookingProviderAssignmentStatus.Accepted))
        {
            throw new InvalidOperationException($"Cannot mark a {Status} assignment as reassigned.");
        }

        Status = BookingProviderAssignmentStatus.Reassigned;
        RespondedAt = DateTime.UtcNow;
    }

    /// <summary>Marks this still-live assignment withdrawn because its booking was cancelled (task 208, <c>CancellationService</c>) - so a provider's job list stops showing a cancelled booking as an active job.</summary>
    public void Withdraw()
    {
        if (Status is not (BookingProviderAssignmentStatus.Assigned or BookingProviderAssignmentStatus.Accepted))
        {
            throw new InvalidOperationException($"Cannot withdraw a {Status} assignment.");
        }

        Status = BookingProviderAssignmentStatus.Withdrawn;
        RespondedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Attaches/replaces the completion evidence reference (task 149a). Only
    /// legal once the provider has actually accepted the job - an assignment
    /// still awaiting a response, or one that was rejected/superseded, has no
    /// job execution to attach evidence to.
    /// </summary>
    public void SetCompletionProof(string proofRef)
    {
        if (string.IsNullOrWhiteSpace(proofRef))
        {
            throw new ArgumentException("A completion proof reference is required.", nameof(proofRef));
        }

        if (Status != BookingProviderAssignmentStatus.Accepted)
        {
            throw new InvalidOperationException($"Cannot attach completion proof to an assignment in status {Status}.");
        }

        CompletionProofRef = proofRef;
    }

    private void EnsureOutstanding()
    {
        if (Status != BookingProviderAssignmentStatus.Assigned)
        {
            throw new InvalidOperationException($"Cannot respond to an assignment in status {Status}.");
        }
    }
}
