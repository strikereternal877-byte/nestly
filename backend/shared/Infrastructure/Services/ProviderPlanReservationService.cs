using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IProviderPlanReservationService"/>.</summary>
public class ProviderPlanReservationService : IProviderPlanReservationService
{
    /// <summary>
    /// Visit statuses that mean "booked, and nobody is on it yet" - the visit exists but has no assignment row, so
    /// the double-booking guard cannot see it. (Once assigned a visit is <c>Assigned</c> and is the guard's concern.)
    /// A visit still awaiting payment is included: an unpaid daily visit expires within a day, but a paid-for one
    /// that is about to land must not lose its professional in the meantime.
    /// </summary>
    private static readonly BookingStatus[] StatusesAwaitingAProvider =
    [
        BookingStatus.PaymentPending,
        BookingStatus.Confirmed,
        BookingStatus.AwaitingFulfilment
    ];

    // The question spans bookings, plans and slot windows and no single repository owns that join - read directly
    // off the shared context, like ProviderScheduleConflictService does for its own cross-aggregate question.
    private readonly NestlyDbContext _context;
    private readonly IBusinessClock _clock;
    private readonly RecurringBookingOptions _options;
    private readonly ILogger<ProviderPlanReservationService> _logger;

    public ProviderPlanReservationService(
        NestlyDbContext context,
        IBusinessClock clock,
        IOptions<RecurringBookingOptions> options,
        ILogger<ProviderPlanReservationService> logger)
    {
        _context = context;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsReservedByAnotherPlanAsync(Guid providerId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        int horizonDays = _options.ProviderReservationHorizonDays;
        if (horizonDays <= 0)
        {
            return false;
        }

        var booking = await _context.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new
            {
                b.SlotDate,
                Start = b.SlotStartTimeSnapshot,
                End = b.SlotEndTimeSnapshot,
                b.RecurringBookingPlanId
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return false;
        }

        var today = _clock.Today;
        if (booking.SlotDate < today || booking.SlotDate > today.AddDays(horizonDays))
        {
            return false;
        }

        // Narrowed in SQL first, because this runs once per candidate provider for every booking: the plans this
        // provider has ever served, which for almost every provider is none and ends the question here.
        var nonPrecedent = RecurringPlanProviderContinuityService.NonPrecedentStatuses.ToArray();
        var servedPlanIds = await _context.Bookings.AsNoTracking()
            .Where(b => b.AssignedProviderId == providerId
                && b.RecurringBookingPlanId != null
                && !nonPrecedent.Contains(b.Status))
            .Select(b => b.RecurringBookingPlanId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (booking.RecurringBookingPlanId is { } ownPlanId)
        {
            servedPlanIds.Remove(ownPlanId);
        }

        if (servedPlanIds.Count == 0)
        {
            return false;
        }

        // Having served a plan once does not make someone its regular professional - whoever served its newest
        // assigned visit is (the same rule RecurringPlanProviderContinuityService applies).
        var assignedVisits = await _context.Bookings.AsNoTracking()
            .Where(b => b.RecurringBookingPlanId != null
                && servedPlanIds.Contains(b.RecurringBookingPlanId.Value)
                && b.AssignedProviderId != null
                && !nonPrecedent.Contains(b.Status))
            .Select(b => new { PlanId = b.RecurringBookingPlanId!.Value, ProviderId = b.AssignedProviderId!.Value, b.SlotDate })
            .ToListAsync(cancellationToken);

        var standingPlanIds = assignedVisits
            .GroupBy(v => v.PlanId)
            .Where(g => g.OrderByDescending(v => v.SlotDate).First().ProviderId == providerId)
            .Select(g => g.Key)
            .ToList();
        if (standingPlanIds.Count == 0)
        {
            return false;
        }

        var plans = await _context.RecurringBookingPlans.AsNoTracking()
            .Where(p => standingPlanIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        // Priority: a booking that is itself a plan's visit yields only to plans created before its own plan. Without
        // this two plans that happen to share a professional would each reserve them against the other and both would
        // lose them; with it the older plan keeps them.
        if (booking.RecurringBookingPlanId is { } bookingPlanId)
        {
            var own = await _context.RecurringBookingPlans.AsNoTracking()
                .Where(p => p.Id == bookingPlanId)
                .Select(p => new { p.Id, p.CreatedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (own is not null)
            {
                plans = plans
                    .Where(p => p.CreatedAtUtc < own.CreatedAtUtc
                        || (p.CreatedAtUtc == own.CreatedAtUtc && p.Id.CompareTo(own.Id) < 0))
                    .ToList();
            }
        }

        if (plans.Count == 0)
        {
            return false;
        }

        // (a) Visits those plans already have on this date. One still waiting for a professional (and not already
        // someone else's) is the provider's, whether or not an assignment row exists yet.
        var planIds = plans.Select(p => p.Id).ToList();
        var visitsOnDate = await _context.Bookings.AsNoTracking()
            .Where(b => b.RecurringBookingPlanId != null
                && planIds.Contains(b.RecurringBookingPlanId.Value)
                && b.SlotDate == booking.SlotDate
                && b.Id != bookingId)
            .Select(b => new
            {
                PlanId = b.RecurringBookingPlanId!.Value,
                b.Status,
                b.AssignedProviderId,
                Start = b.SlotStartTimeSnapshot,
                End = b.SlotEndTimeSnapshot
            })
            .ToListAsync(cancellationToken);

        foreach (var visit in visitsOnDate)
        {
            if (StatusesAwaitingAProvider.Contains(visit.Status)
                && (visit.AssignedProviderId is null || visit.AssignedProviderId == providerId)
                && Overlaps(booking.Start, booking.End, visit.Start, visit.End))
            {
                LogReserved(providerId, bookingId, visit.PlanId, booking.SlotDate, "an existing visit");
                return true;
            }
        }

        // (b) Dates the plan has not generated a visit for yet. Only an active plan will; and one that already has a
        // booking on the date (cancelled, reassigned elsewhere...) has had that date decided, so it is left to (a).
        var plansWithAVisitOnDate = visitsOnDate.Select(v => v.PlanId).ToHashSet();
        var forecastPlans = plans
            .Where(p => p.Status == RecurringBookingPlanStatus.Active && !plansWithAVisitOnDate.Contains(p.Id))
            .ToList();
        if (forecastPlans.Count == 0)
        {
            return false;
        }

        var windowIds = forecastPlans.Select(p => p.SlotWindowId).Distinct().ToList();
        var windows = await _context.SlotWindows.AsNoTracking()
            .Where(w => windowIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        foreach (var plan in forecastPlans)
        {
            int daysFromCursor = booking.SlotDate.DayNumber - plan.NextOccurrenceDate.DayNumber;
            if (daysFromCursor < 0)
            {
                continue; // the plan's cursor is already past this date
            }

            // The plan's own projection honours its cadence, end date and remaining visit count. At most one date per
            // day, so daysFromCursor + 1 dates always reach the booking's date if the plan has one there.
            if (!plan.PreviewUpcomingOccurrenceDates(daysFromCursor + 1).Contains(booking.SlotDate))
            {
                continue;
            }

            if (windows.TryGetValue(plan.SlotWindowId, out var window)
                && Overlaps(booking.Start, booking.End, window.StartTime, window.EndTime))
            {
                LogReserved(providerId, bookingId, plan.Id, booking.SlotDate, "a visit the plan has yet to create");
                return true;
            }
        }

        return false;
    }

    /// <summary>Half-open [start, end) overlap - the same boundary the double-booking guard uses, so back-to-back slots do not collide.</summary>
    private static bool Overlaps(TimeSpan startA, TimeSpan endA, TimeSpan startB, TimeSpan endB) =>
        startA < endB && startB < endA;

    private void LogReserved(Guid providerId, Guid bookingId, Guid planId, DateOnly date, string basis) =>
        _logger.LogDebug(
            "Provider {ProviderId} is reserved for recurring plan {PlanId} on {Date} ({Basis}); not eligible for booking {BookingId}.",
            providerId, planId, date, basis, bookingId);
}
