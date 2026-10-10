using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Serviceability;
using Nestly.Application.Settings;
using Nestly.Application.Slots;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Slot availability calculation (tasks 45a-d, SRS 12.10). Single-timezone
/// assumption throughout - no per-city timezone field exists anywhere in the
/// geography schema; add one, and resolve <see cref="IBusinessClock"/> per
/// city, if the platform ever spans multiple timezones.
///
/// Every "is this still bookable" comparison goes through
/// <see cref="IBusinessClock"/> rather than <see cref="TimeProvider"/>
/// directly: stored slot times are business wall-clock values, and comparing
/// them against a UTC instant made the configured cutoff lenient by the
/// business timezone's offset.
///
/// <para>
/// <b>Platform rules.</b> Once an admin has saved the Booking and/or Slot groups in Settings (<see cref="IPlatformRules"/>),
/// they sit underneath a city's own <see cref="SlotBookingPolicy"/> as a floor: the booking window is the <i>shortest</i>
/// of the city's and the platform's, and the lead time the <i>longest</i>. Limits only tighten here, never loosen what a city
/// sets. Booking <c>MinLeadTimeHours</c> and <c>MaxAdvanceBookingDays</c>, Slot <c>SameDayCutoffHours</c> (same-day slots
/// only) and <c>MaxAdvanceBookingDays</c>, Booking <c>AllowSameDayBooking</c> and Slot <c>AllowOverbooking</c> are applied;
/// Slot <c>DefaultSlotDurationMinutes</c> / <c>DefaultSlotCapacity</c> are defaults for creating windows, which this engine
/// never does, so they are not read here. A group nobody has saved changes nothing.
/// </para>
/// </summary>
public class SlotAvailabilityService : ISlotAvailabilityService
{
    private readonly IServiceabilityRepository _serviceabilityRepository;
    private readonly IServiceabilityValidationService _serviceabilityValidationService;
    private readonly ISlotWindowRepository _slotWindowRepository;
    private readonly ISlotBlackoutRepository _blackoutRepository;
    private readonly ISlotBookingPolicyRepository _policyRepository;
    private readonly ISlotCapacityRepository _slotCapacityRepository;
    private readonly IBusinessClock _businessClock;
    private readonly IPlatformRules _platformRules;

    public SlotAvailabilityService(
        IServiceabilityRepository serviceabilityRepository,
        IServiceabilityValidationService serviceabilityValidationService,
        ISlotWindowRepository slotWindowRepository,
        ISlotBlackoutRepository blackoutRepository,
        ISlotBookingPolicyRepository policyRepository,
        ISlotCapacityRepository slotCapacityRepository,
        IBusinessClock businessClock,
        IPlatformRules? platformRules = null)
    {
        _serviceabilityRepository = serviceabilityRepository;
        _serviceabilityValidationService = serviceabilityValidationService;
        _slotWindowRepository = slotWindowRepository;
        _blackoutRepository = blackoutRepository;
        _policyRepository = policyRepository;
        _slotCapacityRepository = slotCapacityRepository;
        _businessClock = businessClock;
        _platformRules = platformRules ?? NoPlatformRules.Instance;
    }

    public async Task<Result<SlotAvailabilityResponse>> GetAvailableSlotsAsync(Guid serviceId, Guid localityId, DateOnly date)
    {
        var geo = await _serviceabilityRepository.GetCityAndPincodeForLocalityAsync(localityId);
        if (geo is null)
        {
            return Error.NotFound("Slots.LocalityNotFound", "The specified locality does not exist.");
        }

        var (cityId, pincodeId) = geo.Value;

        var serviceableResult = await _serviceabilityValidationService.IsServiceServiceableByPincodeAsync(serviceId, pincodeId);
        if (serviceableResult.IsFailure)
        {
            return serviceableResult.Error;
        }

        if (!serviceableResult.Value)
        {
            return new SlotAvailabilityResponse(IsServiceable: false, Slots: [], SlotUnavailabilityReason.NotServiceable);
        }

        // Business wall-clock on both sides of every comparison below: slot
        // dates and window start times are stored as local business time with
        // no offset, so measuring them against a UTC instant skews every
        // cutoff by the business timezone's offset (see IBusinessClock).
        DateTime now = _businessClock.Now;
        DateOnly today = _businessClock.Today;

        var policy = await _policyRepository.GetByCityAsync(cityId);
        var bookingRules = await _platformRules.GetBookingAsync();
        var slotRules = await _platformRules.GetSlotAsync();

        // The city's own policy first, then the platform's rules as a floor underneath it: the window is the shortest of
        // them and the lead time the longest, so nothing here can loosen what a city already sets.
        int maxAdvanceDays = policy?.MaxAdvanceDays ?? int.MaxValue;
        int cutoffMinutes = policy?.CutoffMinutes ?? 0;
        if (bookingRules is not null)
        {
            maxAdvanceDays = Math.Min(maxAdvanceDays, bookingRules.MaxAdvanceBookingDays);
            cutoffMinutes = Math.Max(cutoffMinutes, bookingRules.MinLeadTimeHours * 60);
        }

        if (slotRules is not null)
        {
            maxAdvanceDays = Math.Min(maxAdvanceDays, slotRules.MaxAdvanceBookingDays);
            if (date == today)
            {
                cutoffMinutes = Math.Max(cutoffMinutes, slotRules.SameDayCutoffHours * 60);
            }
        }

        // Same-day booking switched off: today is simply outside what can be booked.
        bool sameDayNotAllowed = bookingRules is { AllowSameDayBooking: false } && date == today;

        if (date < today || sameDayNotAllowed || (maxAdvanceDays != int.MaxValue && date > today.AddDays(maxAdvanceDays)))
        {
            return new SlotAvailabilityResponse(IsServiceable: true, Slots: [], SlotUnavailabilityReason.DateOutOfBookableRange);
        }

        var blackouts = await _blackoutRepository.ListInRangeAsync(cityId, date, date);
        if (blackouts.Any(b => b.CoversDate(date)))
        {
            return new SlotAvailabilityResponse(IsServiceable: true, Slots: [], SlotUnavailabilityReason.Blackout);
        }

        var windows = await _slotWindowRepository.ListActiveForCityAndDayAsync(cityId, date.DayOfWeek);
        if (windows.Count == 0)
        {
            return new SlotAvailabilityResponse(IsServiceable: true, Slots: [], SlotUnavailabilityReason.NoWindowsConfigured);
        }

        DateTime cutoffThreshold = now.AddMinutes(cutoffMinutes);

        var openWindows = windows
            .Where(w => date.ToDateTime(TimeOnly.MinValue).Add(w.StartTime) >= cutoffThreshold)
            .ToList();

        if (openWindows.Count == 0)
        {
            return new SlotAvailabilityResponse(IsServiceable: true, Slots: [], SlotUnavailabilityReason.CutoffPassed);
        }

        // Capacity is part of "is this bookable", not a surprise sprung at
        // creation time. Without this filter a window at MaxBookingsPerSlot
        // stayed selectable through the picker and through RevalidateSlotAsync
        // below, and only failed on the customer's final click, when
        // ReserveSlotAsync returned Booking.SlotCapacityReached.
        // Overbooking allowed: a window's seat limit is a target, not a wall, so a full window stays on offer.
        var slots = slotRules is { AllowOverbooking: true }
            ? openWindows.Select(ToOption).ToList()
            : await FilterOutFullWindowsAsync(openWindows, date);

        return slots.Count == 0
            ? new SlotAvailabilityResponse(IsServiceable: true, Slots: [], SlotUnavailabilityReason.FullyBooked)
            : new SlotAvailabilityResponse(IsServiceable: true, Slots: slots);
    }

    /// <summary>The widest range one call may ask for - comfortably past any city's MaxAdvanceDays, and a bound on what a single request can cost.</summary>
    private const int MaxRangeDays = 31;

    public async Task<Result<SlotRangeResponse>> GetAvailableSlotsRangeAsync(
        Guid serviceId,
        Guid localityId,
        DateOnly from,
        DateOnly to)
    {
        if (to < from)
        {
            return Error.Validation("Slots.InvalidRange", "The end date must not be before the start date.");
        }

        int dayCount = to.DayNumber - from.DayNumber + 1;
        if (dayCount > MaxRangeDays)
        {
            return Error.Validation("Slots.RangeTooWide", $"A slot range may cover at most {MaxRangeDays} days.");
        }

        var days = new List<SlotDayAvailabilityResponse>(dayCount);

        // ponytail: loops the existing per-date calculation rather than
        // rewriting it as one set-based query. The win being claimed here is
        // the HTTP round-trips - the date strip went from seven serial
        // requests to one, which is what a customer on a slow connection
        // actually feels - and the per-date logic stays the single source of
        // truth for what "bookable" means. If the database work itself becomes
        // the bottleneck, batch the blackout/window/capacity lookups across
        // the whole range instead of per day.
        for (int offset = 0; offset < dayCount; offset++)
        {
            DateOnly date = from.AddDays(offset);
            var result = await GetAvailableSlotsAsync(serviceId, localityId, date);

            // A failure here is date-independent (an unknown locality, an
            // unknown service), so it is the answer for the whole range.
            if (result.IsFailure)
            {
                return result.Error;
            }

            days.Add(new SlotDayAvailabilityResponse(date, result.Value.IsServiceable, result.Value.Slots, result.Value.Reason));
        }

        return new SlotRangeResponse(days);
    }

    /// <summary>
    /// Drops the windows that have already taken every seat they are allowed
    /// for <paramref name="date"/>. Windows with no configured capacity are
    /// unlimited and always survive; the counter lookup is one query for the
    /// whole set, and is skipped entirely when nothing is capped.
    /// </summary>
    private async Task<List<SlotOptionResponse>> FilterOutFullWindowsAsync(IReadOnlyList<SlotWindow> openWindows, DateOnly date)
    {
        var cappedWindowIds = openWindows
            .Where(w => w.MaxBookingsPerSlot is not null)
            .Select(w => w.Id)
            .ToList();

        if (cappedWindowIds.Count == 0)
        {
            return openWindows.Select(ToOption).ToList();
        }

        var bookedCounts = await _slotCapacityRepository.GetBookedCountsAsync(cappedWindowIds, date);

        return openWindows
            .Where(w => w.MaxBookingsPerSlot is null
                || !bookedCounts.TryGetValue(w.Id, out int booked)
                || booked < w.MaxBookingsPerSlot.Value)
            .Select(ToOption)
            .ToList();
    }

    public async Task<Result<SlotRevalidationResponse>> RevalidateSlotAsync(Guid serviceId, Guid localityId, Guid slotWindowId, DateOnly date)
    {
        var availability = await GetAvailableSlotsAsync(serviceId, localityId, date);
        if (availability.IsFailure)
        {
            return availability.Error;
        }

        if (!availability.Value.IsServiceable)
        {
            return new SlotRevalidationResponse(false, "This address is no longer serviceable for this service.");
        }

        bool stillAvailable = availability.Value.Slots.Any(s => s.SlotWindowId == slotWindowId);
        return new SlotRevalidationResponse(
            stillAvailable,
            stillAvailable ? null : DescribeUnavailability(availability.Value.Reason));
    }

    /// <summary>
    /// Customer-facing wording for a slot that did not survive revalidation.
    /// Specific per cause: "no longer available" covers every case and
    /// explains none of them, which leaves the customer re-picking the same
    /// slot to see whether it takes this time.
    /// </summary>
    private static string DescribeUnavailability(SlotUnavailabilityReason reason) => reason switch
    {
        SlotUnavailabilityReason.FullyBooked => "This slot has just been fully booked. Please choose another.",
        SlotUnavailabilityReason.CutoffPassed => "Bookings for this slot have closed. Please choose a later slot.",
        SlotUnavailabilityReason.Blackout => "We aren't taking bookings on this date. Please choose another day.",
        SlotUnavailabilityReason.DateOutOfBookableRange => "This date is outside the window we can book. Please choose another day.",
        _ => "This slot is no longer available. Please choose another.",
    };

    public async Task<Result> ReserveSlotAsync(Guid slotWindowId, DateOnly date)
    {
        var window = await _slotWindowRepository.GetByIdAsync(slotWindowId);
        if (window is null)
        {
            return Result.Failure(Error.NotFound("Slots.WindowNotFound", "The specified slot window does not exist."));
        }

        if (window.MaxBookingsPerSlot is null)
        {
            return Result.Success();
        }

        // Overbooking allowed: the seat is still counted - so a cancellation's release stays symmetric and the counter stays
        // true if overbooking is switched off again - it just is never refused for being the seat past the limit.
        var slotRules = await _platformRules.GetSlotAsync();
        int limit = slotRules is { AllowOverbooking: true } ? int.MaxValue : window.MaxBookingsPerSlot.Value;
        bool reserved = await _slotCapacityRepository.TryReserveAsync(slotWindowId, date, limit);
        return reserved
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "Booking.SlotCapacityReached",
                "This slot has reached its maximum bookings for the day. Please choose a different slot."));
    }

    public async Task ReleaseSlotAsync(Guid slotWindowId, DateOnly date)
    {
        var window = await _slotWindowRepository.GetByIdAsync(slotWindowId);

        // Nothing was reserved for an uncapped window, so there is nothing to
        // hand back. Guarded on the window rather than blindly decrementing so
        // a window that had its cap removed after the booking was made cannot
        // push a stale counter below the seats still legitimately held.
        if (window?.MaxBookingsPerSlot is null)
        {
            return;
        }

        await _slotCapacityRepository.ReleaseAsync(slotWindowId, date);
    }

    private static SlotOptionResponse ToOption(SlotWindow window) => new(
        window.Id,
        window.Name,
        window.StartTime,
        window.EndTime,
        window.MaxBookingsPerSlot);
}
