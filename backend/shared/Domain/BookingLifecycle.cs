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
        [BookingStatus.Initiated] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer],
        [BookingStatus.PaymentPending] = [BookingStatus.Confirmed, BookingStatus.PaymentFailed, BookingStatus.CancelledByCustomer],
        [BookingStatus.PaymentFailed] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.Confirmed] = [BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.AwaitingFulfilment] = [BookingStatus.Assigned, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        // AwaitingFulfilment added (task 159): when the assigned provider
        // rejects the job, IBookingProviderAssignmentService.RejectAsync moves
        // the booking back to AwaitingFulfilment so it re-enters the
        // assignable pool for manual admin reassignment (PROVIDER.md OPEN
        // DECISIONS #1 - no auto-match).
        [BookingStatus.Assigned] = [BookingStatus.InProgress, BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.InProgress] = [BookingStatus.Completed, BookingStatus.CancelledByAdmin],
        [BookingStatus.Completed] = [BookingStatus.RefundPending],
        [BookingStatus.CancelledByCustomer] = [BookingStatus.RefundPending, BookingStatus.Refunded],
        [BookingStatus.CancelledByAdmin] = [BookingStatus.RefundPending, BookingStatus.Refunded],
        [BookingStatus.Rescheduled] = [BookingStatus.AwaitingFulfilment, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
        [BookingStatus.RefundPending] = [BookingStatus.Refunded],
        [BookingStatus.Refunded] = [],
    };

    /// <summary>Whether moving from <paramref name="from"/> to <paramref name="to"/> is a legal transition.</summary>
    public static bool IsValidTransition(BookingStatus from, BookingStatus to) =>
        Transitions[from].Contains(to);

    /// <summary>No further transitions are possible from this status.</summary>
    public static bool IsTerminal(BookingStatus status) =>
        Transitions[status].Length == 0;
}
