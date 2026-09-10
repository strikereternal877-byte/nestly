using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Events;

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
    Withdrawn,

    /// <summary>
    /// The provider finished the job and it was verified (completion proof /
    /// OTP - see <c>ProviderJobService.CompleteAsync</c>). Terminal, and the
    /// point at which the provider stops counting as committed to this job for
    /// scheduling: unlike <see cref="Accepted"/>, a Completed assignment no
    /// longer occupies the provider for the remainder of its slot window, which
    /// is what lets a provider who finishes early become eligible for the next
    /// order (subject to travel/buffer/duration - see the eligibility service).
    /// Appended last so the persisted string values and any ordinal mirrors of
    /// the earlier members are unchanged.
    /// </summary>
    Completed,

    /// <summary>
    /// The provider never responded (neither <see cref="Accept"/> nor
    /// <see cref="Reject"/>) before <see cref="ResponseDeadline"/> passed -
    /// detected by the recurring assignment-response-expiry sweep, not the
    /// provider's own action. Deliberately distinct from <see cref="Rejected"/>:
    /// silence and an explicit decline are different signals worth telling
    /// apart in the assignment history and in any future provider-reliability
    /// scoring, even though both return the booking to the assignable pool
    /// identically today.
    /// </summary>
    Expired
}

/// <summary>
/// The one bridge entity <see cref="Booking"/> is allowed to reference into
/// the Provider module (PROVIDER.md SCOPE BOUNDARY, task 147): which provider
/// was offered a booking, by whom, and how they responded. History is kept -
/// a rejected or superseded row is never deleted or overwritten - so the
/// admin/provider UI can show a full assignment trail per booking (task 159:
/// "surfaces the booking as needs reassignment").
/// </summary>
/// <remarks>
/// An <see cref="AggregateRoot{TId}"/> rather than a plain
/// <see cref="Entity{TId}"/> since task 272. It was already a root in every
/// way that matters - its own table, its own repository, never loaded as a
/// navigation off <see cref="Booking"/> - and the base type is what makes its
/// events reachable: <c>DomainEventDispatchInterceptor</c> only scans
/// <c>ChangeTracker.Entries&lt;AggregateRoot&lt;Guid&gt;&gt;()</c>, so an
/// event raised on a plain entity is collected, saved and silently never
/// dispatched.
/// </remarks>
public class BookingProviderAssignment : AggregateRoot<Guid>
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

    /// <summary>When the job was verified-complete (see <see cref="Complete"/>) - null until then. The provider's actual finish time, which the scheduler uses as the "free from" instant for a non-duration-based service.</summary>
    public DateTime? CompletedAt { get; private set; }

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

    /// <summary>
    /// The provider accepts the job (task 151, provider-api - exposed here so
    /// that future controller can call it through the same shared service).
    /// Raises <see cref="ProviderAssignmentAcceptedEvent"/> (task 272): this
    /// is the moment a job becomes trackable, and until now it produced no
    /// signal at all, so nothing - not the customer's tracking screen, not the
    /// "a professional accepted your booking" notification (task 276) - could
    /// react to it.
    /// </summary>
    public void Accept()
    {
        EnsureOutstanding();
        Status = BookingProviderAssignmentStatus.Accepted;
        RespondedAt = DateTime.UtcNow;
        RaiseDomainEvent(new ProviderAssignmentAcceptedEvent(Id, BookingId, ProviderId, RespondedAt.Value));
    }

    /// <summary>The provider rejects the job (task 159) - the booking's own re-assignment handling lives in <c>IBookingProviderAssignmentService.RejectAsync</c>, not here.</summary>
    public void Reject(string? reason)
    {
        EnsureOutstanding();
        Status = BookingProviderAssignmentStatus.Rejected;
        RespondedAt = DateTime.UtcNow;
        Notes = reason;
    }

    /// <summary>
    /// Row 38, docs/OPEN-FIXES-FEATURES.csv: pushes <see cref="ResponseDeadline"/>
    /// out by <paramref name="extension"/> rather than letting the
    /// assignment-response-expiry sweep treat a genuine delivery failure -
    /// not the provider's own inaction - as a silent decline. Only ever
    /// moves the deadline later (never shortens it, and is a no-op with no
    /// deadline to extend) and only while the assignment is still
    /// <see cref="BookingProviderAssignmentStatus.Assigned"/> - once the
    /// provider has actually responded (or the assignment moved on) there is
    /// nothing left to extend.
    /// </summary>
    public void ExtendResponseDeadline(TimeSpan extension)
    {
        EnsureOutstanding();
        if (ResponseDeadline is null)
        {
            return;
        }

        var extended = DateTime.UtcNow.Add(extension);
        if (extended > ResponseDeadline)
        {
            ResponseDeadline = extended;
        }
    }

    /// <summary>
    /// The response window passed with no accept/reject from the provider -
    /// the assignment-response-expiry sweep's action, not the provider's own.
    /// <see cref="RespondedAt"/> is deliberately left null: it means exactly
    /// what it says, the provider never responded, which is what lets the
    /// sweep's own query (and anyone reading history afterwards) tell an
    /// expiry apart from a reject without inspecting <see cref="Status"/>
    /// alone. The booking's own reassignment handling lives in
    /// <c>IBookingProviderAssignmentService.ExpireAsync</c>, mirroring how
    /// <see cref="Reject"/> leaves that half to <c>RejectAsync</c>.
    /// </summary>
    public void Expire()
    {
        EnsureOutstanding();
        Status = BookingProviderAssignmentStatus.Expired;
        Notes = "Provider did not respond within the response window.";
    }

    /// <summary>
    /// Marks this row superseded because a newer assignment was created for the
    /// same booking before this one was responded to (task 147/159).
    ///
    /// <para>
    /// Raises <see cref="BookingProviderChangedEvent"/> (task 295) whenever the
    /// replacement is a *different* provider. This is the only place in the
    /// system where one live assignment is replaced by another, and - unlike
    /// every other fulfilment-half change - it need not move the booking's
    /// status at all: replacing the provider on an already-Assigned booking
    /// leaves it Assigned, so <c>BookingStatusChangedEvent</c> is silent and
    /// nothing downstream could see the swap. The event carries whether this
    /// row had been <see cref="BookingProviderAssignmentStatus.Accepted"/>,
    /// since <see cref="Status"/> is overwritten on the next line and the
    /// notification path's rule depends on it.
    /// </para>
    /// </summary>
    /// <param name="supersededByProviderId">
    /// The provider taking this assignment's place. Re-offering the same
    /// booking to the same provider raises nothing - the customer's answer to
    /// "who is coming?" has not changed.
    /// </param>
    public void MarkReassigned(Guid supersededByProviderId)
    {
        if (Status is not (BookingProviderAssignmentStatus.Assigned or BookingProviderAssignmentStatus.Accepted))
        {
            throw new InvalidOperationException($"Cannot mark a {Status} assignment as reassigned.");
        }

        bool wasAccepted = Status == BookingProviderAssignmentStatus.Accepted;

        Status = BookingProviderAssignmentStatus.Reassigned;
        RespondedAt = DateTime.UtcNow;

        if (supersededByProviderId != ProviderId)
        {
            RaiseDomainEvent(new BookingProviderChangedEvent(BookingId, Id, ProviderId, supersededByProviderId, wasAccepted));
        }
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
    /// Attaches/replaces the completion evidence reference (task 149a). Legal
    /// while the provider is actively on the job (<see cref="BookingProviderAssignmentStatus.Accepted"/>)
    /// or just after (<see cref="BookingProviderAssignmentStatus.Completed"/>) -
    /// supplementary evidence uploaded moments after tapping "complete" is a
    /// real flow this must not block. An assignment still awaiting a response,
    /// or one that was rejected/superseded, has no job execution to attach
    /// evidence to.
    /// </summary>
    public void SetCompletionProof(string proofRef)
    {
        if (string.IsNullOrWhiteSpace(proofRef))
        {
            throw new ArgumentException("A completion proof reference is required.", nameof(proofRef));
        }

        if (Status is not (BookingProviderAssignmentStatus.Accepted or BookingProviderAssignmentStatus.Completed))
        {
            throw new InvalidOperationException($"Cannot attach completion proof to an assignment in status {Status}.");
        }

        CompletionProofRef = proofRef;
    }

    /// <summary>
    /// Marks the job verified-complete at <paramref name="completedAtUtc"/>
    /// (the provider's actual finish time). Legal only from
    /// <see cref="BookingProviderAssignmentStatus.Accepted"/> - the provider
    /// must have taken and been working the job. The caller is responsible for
    /// the verification itself (completion proof / OTP); this records the
    /// terminal state and the finish time the scheduler reads to release the
    /// provider early (subject to travel/buffer/duration).
    /// </summary>
    public void Complete(DateTime completedAtUtc)
    {
        if (Status != BookingProviderAssignmentStatus.Accepted)
        {
            throw new InvalidOperationException($"Cannot complete an assignment in status {Status}.");
        }

        Status = BookingProviderAssignmentStatus.Completed;
        CompletedAt = completedAtUtc;
    }

    private void EnsureOutstanding()
    {
        if (Status != BookingProviderAssignmentStatus.Assigned)
        {
            throw new InvalidOperationException($"Cannot respond to an assignment in status {Status}.");
        }
    }
}
