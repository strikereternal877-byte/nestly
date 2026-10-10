namespace Nestly.Application.ProviderManagement;

/// <summary>
/// A recurring plan's regular professional is spoken for on the plan's visit dates, even on days whose visit has not
/// been created or assigned yet.
///
/// <para>
/// Why this exists: the double-booking guard (<see cref="IProviderScheduleConflictService"/>) only sees jobs that are
/// already <i>assigned</i>. A plan's visit is created a few days ahead and assigned only inside the fulfilment
/// window (a day before the slot), so for any date further out its professional looks free, and an unrelated order
/// at the same time could be given to them - after which the plan's visit loses the professional the customer was
/// promised. This service closes that gap by treating the plan's regular professional as booked for the plan's
/// visits at that time of day.
/// </para>
/// </summary>
public interface IProviderPlanReservationService
{
    /// <summary>
    /// True when <paramref name="providerId"/> is the regular professional of another recurring plan that has (or is
    /// due to have) a visit on the booking's date at an overlapping time, so the booking must not be given to them.
    ///
    /// <para>
    /// Only a plan that has priority counts: a one-off booking yields to every such plan, while a booking that is
    /// itself a plan's visit yields only to plans created before its own - two plans that share a professional can
    /// therefore never exclude each other, the older one simply keeps them. A booking's own plan never reserves
    /// against it. Dates further out than <c>RecurringBookings:ProviderReservationHorizonDays</c> are not reserved
    /// (0 switches the whole rule off).
    /// </para>
    /// </summary>
    Task<bool> IsReservedByAnotherPlanAsync(Guid providerId, Guid bookingId, CancellationToken cancellationToken = default);
}
