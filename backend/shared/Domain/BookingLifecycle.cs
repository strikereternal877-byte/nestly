namespace Nestly.Domain;

/// <summary>
/// The booking state transition matrix (SRS 13.1-13.2, task 56b). Kept
/// separate from <see cref="Booking"/> itself so the full set of legal
/// transitions can be read (and unit-tested) as one table rather than
/// scattered across guard clauses on individual methods.
/// </summary>
public static class BookingLifecycle
{
    private static readonly Dictionary<BookingStatus, BookingStatus[]> Transitions = new()
    {
        // Confirmed added (task 331): a booking with nothing payable - an AMC
        // entitlement redemption (docs/AMC.md), a fully wallet-covered
        // checkout, a discount that takes the total to zero - is confirmed
        // straight from Initiated by BookingService.CreateAsync. It never
        // awaits a payment that will never be asked for, and PaymentPending
        // was a dead end for it: PaymentTransaction rejects a non-positive
        // amount, so nothing could ever move it on to Confirmed.
        [BookingStatus.Initiated] = [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.CancelledByCustomer],
        // CancelledByAdmin added: PaymentPending was the only cancellable
        // state that admin could not act on - even PaymentFailed below allows
        // it - so a booking stuck awaiting a payment that will never arrive
        // could only be waited out. BookingExpirySweepJob does expire it after
        // BookingExpiryOptions.ExpiryMinutes, but that is a 20-minute wait an
        // ops user handling a customer on the phone should not have to sit
        // through, and it does nothing when the background job server is off.
        [BookingStatus.PaymentPending] = [BookingStatus.Confirmed, BookingStatus.PaymentFailed, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin, BookingStatus.Expired],
        [BookingStatus.PaymentFailed] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.Confirmed] = [BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.AwaitingFulfilment] = [BookingStatus.Assigned, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        // AwaitingFulfilment added (task 159): when the assigned provider
        // rejects the job, IBookingProviderAssignmentService.RejectAsync moves
        // the booking back to AwaitingFulfilment so it re-enters the
        // assignable pool for manual admin reassignment (PROVIDER.md OPEN
        // DECISIONS #1 - no auto-match).
        // ProviderEnRoute added (task 264). Assigned -> InProgress is
        // deliberately kept alongside it: tapping en-route is optional, and a
        // provider who goes straight from accepting the job to starting work
        // must not be blocked by a tracking state they never entered.
        [BookingStatus.Assigned] = [BookingStatus.ProviderEnRoute, BookingStatus.InProgress, BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        // Task 264: the two tracking states carry the same cancel/reschedule
        // edges Assigned has - a customer whose provider is en route or at the
        // door has not lost the right to cancel, and admin can still step in.
        // They do NOT carry Assigned's AwaitingFulfilment edge: that one exists
        // for a provider *rejecting* an offer, and a provider who has already
        // set off has accepted the job - returning it to the assignable pool
        // from here is a withdrawal/reassignment, not a rejection.
        [BookingStatus.ProviderEnRoute] = [BookingStatus.ProviderArrived, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.ProviderArrived] = [BookingStatus.InProgress, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.InProgress] = [BookingStatus.Completed, BookingStatus.CancelledByAdmin],
        [BookingStatus.Completed] = [BookingStatus.RefundPending],
        [BookingStatus.CancelledByCustomer] = [BookingStatus.RefundPending, BookingStatus.Refunded],
        [BookingStatus.CancelledByAdmin] = [BookingStatus.RefundPending, BookingStatus.Refunded],
        [BookingStatus.Rescheduled] = [BookingStatus.AwaitingFulfilment, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.RefundPending] = [BookingStatus.Refunded],
        [BookingStatus.Refunded] = [],
        // Task 240: no payment was ever captured on a PaymentPending booking,
        // so unlike CancelledByCustomer/CancelledByAdmin there is nothing to
        // refund and no cancellation-fee policy to apply - terminal on its own.
        [BookingStatus.Expired] = [],
    };

    /// <summary>Whether moving from <paramref name="from"/> to <paramref name="to"/> is a legal transition.</summary>
    public static bool IsValidTransition(BookingStatus from, BookingStatus to) =>
        Transitions[from].Contains(to);

    /// <summary>No further transitions are possible from this status.</summary>
    public static bool IsTerminal(BookingStatus status) =>
        Transitions[status].Length == 0;

    /// <summary>
    /// The fulfilment window during which live tracking is meaningful (task
    /// 273): a provider is committed to the job and has not finished it. Lives
    /// here, next to the transition table, because it is a statement about the
    /// lifecycle rather than about any one feature - the tracking hub, the
    /// location-ingest endpoint and the tracking read model all have to agree
    /// on it, and three copies of this set would be three chances to disagree.
    ///
    /// Everything before <see cref="BookingStatus.Assigned"/> has no provider
    /// to track, and everything after <see cref="BookingStatus.InProgress"/>
    /// (completed, cancelled, refunded) has nothing left to watch - so a
    /// customer who could track a booking a minute ago is denied the moment it
    /// leaves this set.
    /// </summary>
    private static readonly HashSet<BookingStatus> TrackableStatuses =
    [
        BookingStatus.Assigned,
        BookingStatus.ProviderEnRoute,
        BookingStatus.ProviderArrived,
        BookingStatus.InProgress
    ];

    /// <summary>Whether live tracking is available for a booking in this status - see <see cref="TrackableStatuses"/>.</summary>
    public static bool IsTrackable(BookingStatus status) =>
        TrackableStatuses.Contains(status);
}
