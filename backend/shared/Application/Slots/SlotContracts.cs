namespace Nestly.Application.Slots;

/// <summary>A bookable window on the requested date (task 46, SRS 24.4).</summary>
public record SlotOptionResponse(
    Guid SlotWindowId,
    string Name,
    TimeSpan StartTime,
    TimeSpan EndTime,
    int? MaxBookingsPerSlot);

/// <summary>
/// Why a date came back with nothing bookable.
///
/// Without this the client cannot tell a genuinely full day apart from one
/// whose windows simply closed for the evening, and every empty list has to
/// be rendered with the same worst-case wording ("Fully booked") - which
/// reads as scarcity to a customer whose only real problem is that they
/// opened the app after today's cutoff.
/// </summary>
public enum SlotUnavailabilityReason
{
    /// <summary>Slots were returned - nothing to explain.</summary>
    None = 0,

    /// <summary>The service isn't offered at this address at all (SRS 12.9.2).</summary>
    NotServiceable,

    /// <summary>The date is in the past, or beyond the city's bookable advance window.</summary>
    DateOutOfBookableRange,

    /// <summary>The whole date is blacked out for this city (holiday, capacity freeze).</summary>
    Blackout,

    /// <summary>No slot windows are configured for this city on this weekday.</summary>
    NoWindowsConfigured,

    /// <summary>Windows exist on this date but all of them are already past their booking cutoff.</summary>
    CutoffPassed,

    /// <summary>Every window still open on this date has reached its per-day booking capacity.</summary>
    FullyBooked,
}

/// <summary>
/// Slot availability for a service+address+date. IsServiceable is false only
/// when the service isn't offered at this address at all (SRS 12.9.2); an
/// empty Slots list with IsServiceable true carries its cause in
/// <see cref="Reason"/> so the client can say something true about it.
/// </summary>
public record SlotAvailabilityResponse(
    bool IsServiceable,
    IReadOnlyList<SlotOptionResponse> Slots,
    SlotUnavailabilityReason Reason = SlotUnavailabilityReason.None);

public record SlotRevalidationResponse(bool IsValid, string? Reason);

/// <summary>One day's availability inside a <see cref="SlotRangeResponse"/> - the per-date payload plus the date it belongs to.</summary>
public record SlotDayAvailabilityResponse(
    DateOnly Date,
    bool IsServiceable,
    IReadOnlyList<SlotOptionResponse> Slots,
    SlotUnavailabilityReason Reason = SlotUnavailabilityReason.None);

/// <summary>
/// Availability for a contiguous run of dates, in date order.
///
/// Exists because the date strip needs every visible day at once to know
/// which chips to disable, and was fetching them one request per day - seven
/// serial round-trips against the availability API for a single page.
/// </summary>
public record SlotRangeResponse(IReadOnlyList<SlotDayAvailabilityResponse> Days);
