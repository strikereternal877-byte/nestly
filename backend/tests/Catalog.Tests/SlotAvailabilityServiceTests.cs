using Nestly.Application.Settings;
using FluentAssertions;
using Nestly.Application.Serviceability;
using Nestly.Application.Slots;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 45a-d and 46's underlying logic: slot availability calculation.</summary>
public sealed class SlotAvailabilityServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    private SlotAvailabilityService BuildService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider? timeProvider = null, IPlatformRules? rules = null) => new(
        new ServiceabilityRepository(context),
        new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
        new SlotWindowRepository(context),
        new SlotBlackoutRepository(context),
        new SlotBookingPolicyRepository(context),
        new SlotCapacityRepository(context),
        TestServices.Clock(timeProvider),
        rules);

    public SlotAvailabilityServiceTests(TestDatabase db) => _db = db;

    private sealed record Fixture(State State, City City, Pincode Pincode, Locality Locality, Category Category, Service Service, SlotWindow? Window = null);

    private Fixture SeedGeographyAndService(Nestly.Infrastructure.Persistence.NestlyDbContext context, DayOfWeek windowDay, TimeSpan start, TimeSpan end, int? capacity = null)
    {
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, Guid.NewGuid().ToString("N")[..6]);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Test Locality");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 499m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Window", start, end);
        if (capacity is not null) window.SetCapacity(capacity);
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, windowDay);

        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SlotWindows.Add(window);
        context.SlotWindowRules.Add(rule);
        context.SaveChanges();

        return new Fixture(state, city, pincode, locality, category, service, window);
    }

    [Fact]
    public async Task Returns_the_window_on_a_matching_day_far_enough_in_the_future()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, futureDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsServiceable.Should().BeTrue();
        result.Value.Slots.Should().ContainSingle(s => s.SlotWindowId != Guid.Empty);
    }

    [Fact]
    public async Task Not_serviceable_when_service_has_no_mapping_to_the_pincode()
    {
        var state = new State(Guid.NewGuid(), "Maharashtra", "MH" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Pune");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, Guid.NewGuid().ToString("N")[..6]);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Unserved");
        var category = new Category(Guid.NewGuid(), "Painting", "painting-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Wall Paint", "wall-paint-" + Guid.NewGuid(), "desc", 999m);

        using (var context = _db.CreateContext())
        {
            context.States.Add(state);
            context.Cities.Add(city);
            context.Zones.Add(zone);
            context.Pincodes.Add(pincode);
            context.Localities.Add(locality);
            context.Add(category);
            context.Add(service);
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(service.Id, locality.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsServiceable.Should().BeFalse();
        result.Value.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task Window_starting_before_the_cutoff_threshold_today_is_excluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            // A window starting at midnight will always be in the past relative to "now" on the same day.
            fixture = SeedGeographyAndService(context, today.DayOfWeek, TimeSpan.Zero, TimeSpan.FromHours(1));
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, 60, 30));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task Date_beyond_max_advance_days_returns_no_slots_but_still_serviceable()
    {
        var farFuture = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, farFuture.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, 60, 7));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, farFuture);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsServiceable.Should().BeTrue();
        result.Value.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task Blacked_out_date_returns_no_slots()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            context.SlotBlackouts.Add(new SlotBlackout(Guid.NewGuid(), fixture.City.Id, futureDate, futureDate, SlotBlackoutType.Holiday, "Test Holiday"));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, futureDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task Slot_option_reports_the_windows_configured_capacity()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13), capacity: 5);
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, futureDate);

        result.Value.Slots.Single().MaxBookingsPerSlot.Should().Be(5);
    }

    /// <summary>
    /// A window at capacity must not be offered at all. It used to stay
    /// selectable through the picker and through revalidation, and only failed
    /// on the customer's final click.
    /// </summary>
    [Fact]
    public async Task A_window_at_capacity_is_not_offered_and_does_not_revalidate()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13), capacity: 1);
        }

        using (var reserveContext = _db.CreateContext())
        {
            var reserved = await BuildService(reserveContext).ReserveSlotAsync(fixture.Window!.Id, futureDate);
            reserved.IsSuccess.Should().BeTrue("the single seat should still be free");
        }

        using var readContext = _db.CreateContext();
        var availability = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, futureDate);

        availability.Value.Slots.Should().BeEmpty();
        availability.Value.Reason.Should().Be(SlotUnavailabilityReason.FullyBooked);

        var revalidation = await BuildService(readContext).RevalidateSlotAsync(fixture.Service.Id, fixture.Locality.Id, fixture.Window!.Id, futureDate);
        revalidation.Value.IsValid.Should().BeFalse();
        revalidation.Value.Reason.Should().Contain("fully booked");
    }

    /// <summary>
    /// The other half of the reservation: a released seat becomes bookable
    /// again. Without it a window silently fills up with cancelled bookings.
    /// </summary>
    [Fact]
    public async Task Releasing_a_seat_makes_a_full_window_bookable_again()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(6));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13), capacity: 1);
        }

        using (var reserveContext = _db.CreateContext())
        {
            await BuildService(reserveContext).ReserveSlotAsync(fixture.Window!.Id, futureDate);
        }

        using (var releaseContext = _db.CreateContext())
        {
            await BuildService(releaseContext).ReleaseSlotAsync(fixture.Window!.Id, futureDate);
        }

        using var readContext = _db.CreateContext();
        var availability = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, futureDate);

        availability.Value.Slots.Should().ContainSingle(s => s.SlotWindowId == fixture.Window!.Id);
    }

    /// <summary>
    /// A release with nothing reserved must not drive the counter negative -
    /// that would hand out seats the window does not have.
    /// </summary>
    [Fact]
    public async Task Releasing_more_than_was_reserved_never_oversells_the_window()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13), capacity: 1);
        }

        using (var releaseContext = _db.CreateContext())
        {
            await BuildService(releaseContext).ReleaseSlotAsync(fixture.Window!.Id, futureDate);
            await BuildService(releaseContext).ReleaseSlotAsync(fixture.Window!.Id, futureDate);
        }

        using (var reserveContext = _db.CreateContext())
        {
            var first = await BuildService(reserveContext).ReserveSlotAsync(fixture.Window!.Id, futureDate);
            var second = await BuildService(reserveContext).ReserveSlotAsync(fixture.Window!.Id, futureDate);

            first.IsSuccess.Should().BeTrue();
            second.IsFailure.Should().BeTrue("capacity is 1 - the stray releases must not have created extra seats");
        }
    }

    [Fact]
    public async Task Revalidate_succeeds_for_a_slot_that_is_still_available()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4));
        Fixture fixture;
        Guid windowId;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            windowId = context.SlotWindows.Single(w => w.CityId == fixture.City.Id).Id;
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).RevalidateSlotAsync(fixture.Service.Id, fixture.Locality.Id, windowId, futureDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsValid.Should().BeTrue();
        result.Value.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Revalidate_fails_once_the_date_becomes_blacked_out()
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(6));
        Fixture fixture;
        Guid windowId;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, futureDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            windowId = context.SlotWindows.Single(w => w.CityId == fixture.City.Id).Id;
        }

        using (var context = _db.CreateContext())
        {
            context.SlotBlackouts.Add(new SlotBlackout(Guid.NewGuid(), fixture.City.Id, futureDate, futureDate, SlotBlackoutType.Blackout, "Late admin block"));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).RevalidateSlotAsync(fixture.Service.Id, fixture.Locality.Id, windowId, futureDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsValid.Should().BeFalse();
        result.Value.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unknown_locality_returns_not_found()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).GetAvailableSlotsAsync(Guid.NewGuid(), Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Slots.LocalityNotFound");
    }

    /// <summary>Boundary check on SlotBlackout.CoversDate: the day right after a blackout range ends must not be blocked.</summary>
    [Fact]
    public async Task The_day_immediately_after_a_blackout_range_is_not_blocked()
    {
        var blackoutEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        var dayAfter = blackoutEnd.AddDays(1);
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, dayAfter.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            context.SlotBlackouts.Add(new SlotBlackout(
                Guid.NewGuid(), fixture.City.Id, blackoutEnd.AddDays(-1), blackoutEnd, SlotBlackoutType.Holiday, "Festival"));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, dayAfter);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slots.Should().ContainSingle();
    }

    /// <summary>Boundary check on the max-advance-days cutoff: exactly on the last bookable day, slots must still be returned.</summary>
    [Fact]
    public async Task Date_exactly_on_the_max_advance_days_boundary_still_returns_slots()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var boundaryDate = today.AddDays(7);
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, boundaryDate.DayOfWeek, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, 60, 7));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var result = await BuildService(readContext).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, boundaryDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsServiceable.Should().BeTrue();
        result.Value.Slots.Should().ContainSingle();
    }

    /// <summary>
    /// A slot valid when first offered can fail revalidation once the cutoff
    /// threshold passes before the customer confirms it - RevalidateSlotAsync
    /// must re-check against current time, not just against blackouts.
    /// </summary>
    [Fact]
    public async Task Revalidate_fails_once_the_cutoff_passes_between_offer_and_confirmation()
    {
        // A fixed midday anchor, not the real wall clock: keeps the window's
        // start time-of-day and the mutable clock in lockstep without any
        // risk of a midnight rollover flaking the test depending on when the
        // suite happens to run.
        var anchor = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(10);
        var today = DateOnly.FromDateTime(anchor.DateTime);
        var windowStart = anchor.TimeOfDay.Add(TimeSpan.FromMinutes(90));

        Fixture fixture;
        Guid windowId;
        using (var context = _db.CreateContext())
        {
            // Starts 90 minutes after the anchor: available with no policy yet,
            // but will fail a 60-minute cutoff once the clock advances 80 minutes.
            fixture = SeedGeographyAndService(context, today.DayOfWeek, windowStart, windowStart.Add(TimeSpan.FromHours(1)));
            windowId = context.SlotWindows.Single(w => w.CityId == fixture.City.Id).Id;
        }

        var clock = new MutableTimeProvider(anchor);

        using (var readContext = _db.CreateContext())
        {
            var initialOffer = await BuildService(readContext, clock).GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, today);
            initialOffer.Value.Slots.Should().ContainSingle(s => s.SlotWindowId == windowId);
        }

        using (var context = _db.CreateContext())
        {
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, cutoffMinutes: 60, maxAdvanceDays: 30));
            context.SaveChanges();
        }

        // The customer takes 80 minutes to confirm - past the window start minus the 60-minute cutoff.
        clock.Advance(TimeSpan.FromMinutes(80));

        using var revalidateContext = _db.CreateContext();
        var result = await BuildService(revalidateContext, clock).RevalidateSlotAsync(fixture.Service.Id, fixture.Locality.Id, windowId, today);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsValid.Should().BeFalse();
        result.Value.Reason.Should().NotBeNullOrEmpty();
    }

    /// <summary>Minimal mutable clock for cutoff/revalidation tests - TimeProvider has no built-in fake in the packages this project already references.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Platform rules (Settings -> Booking / Slot). A fixed clock keeps "now", "today" and the window start comparable.
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Tuesday8Am = new(2030, 3, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly TuesdayToday = new(2030, 3, 5);
    private static readonly DateOnly NextTuesday = new(2030, 3, 12);

    private static BookingSettings BookingRules(int minLeadHours = 0, int maxAdvanceDays = 365, bool allowSameDay = true) =>
        new(minLeadHours, maxAdvanceDays, null, allowSameDay);

    private static SlotSettings SlotRules(int sameDayCutoffHours = 0, int maxAdvanceDays = 365, bool allowOverbooking = false) =>
        new(60, sameDayCutoffHours, maxAdvanceDays, 1, allowOverbooking);

    [Fact]
    public async Task A_saved_booking_horizon_shortens_a_longer_city_window()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(12));
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, 0, 30));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext, new FakeTimeProvider(Tuesday8Am), TestServices.Rules(booking: BookingRules(maxAdvanceDays: 5)));

        var nextWeek = await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday);
        nextWeek.Value.Slots.Should().BeEmpty("seven days out is beyond the platform's five-day horizon, though the city allows thirty");
        nextWeek.Value.Reason.Should().Be(SlotUnavailabilityReason.DateOutOfBookableRange);

        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday)).Value.Slots.Should().ContainSingle();
    }

    [Fact]
    public async Task A_saved_platform_horizon_never_loosens_a_shorter_city_window()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(12));
            context.SlotBookingPolicies.Add(new SlotBookingPolicy(Guid.NewGuid(), fixture.City.Id, 0, 3));
            context.SaveChanges();
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext, new FakeTimeProvider(Tuesday8Am), TestServices.Rules(slot: SlotRules(maxAdvanceDays: 30)));

        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday)).Value.Slots
            .Should().BeEmpty("the city only books three days ahead, and a platform allowance of thirty does not widen that");
    }

    [Fact]
    public async Task A_saved_minimum_lead_time_hides_a_window_that_starts_too_soon()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            // Starts at 09:00; it is 08:00 now.
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        }

        using var readContext = _db.CreateContext();
        var clock = new FakeTimeProvider(Tuesday8Am);

        var twoHours = await BuildService(readContext, clock, TestServices.Rules(booking: BookingRules(minLeadHours: 2)))
            .GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday);
        twoHours.Value.Slots.Should().BeEmpty("the window starts in one hour and the platform wants two");
        twoHours.Value.Reason.Should().Be(SlotUnavailabilityReason.CutoffPassed);

        (await BuildService(readContext, clock, TestServices.Rules(booking: BookingRules(minLeadHours: 1)))
            .GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday)).Value.Slots.Should().ContainSingle();
    }

    [Fact]
    public async Task A_saved_same_day_cutoff_applies_to_today_and_not_to_later_days()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(12));
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext, new FakeTimeProvider(Tuesday8Am), TestServices.Rules(slot: SlotRules(sameDayCutoffHours: 3)));

        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday)).Value.Slots
            .Should().BeEmpty("today's 10:00 window is two hours away and same-day bookings need three");
        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday)).Value.Slots
            .Should().ContainSingle("a later day is not 'same day'");
    }

    [Fact]
    public async Task Switching_same_day_booking_off_makes_today_unbookable_but_not_tomorrow_onward()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(12));
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext, new FakeTimeProvider(Tuesday8Am), TestServices.Rules(booking: BookingRules(allowSameDay: false)));

        var today = await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday);
        today.Value.Slots.Should().BeEmpty();
        today.Value.Reason.Should().Be(SlotUnavailabilityReason.DateOutOfBookableRange);

        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday)).Value.Slots.Should().ContainSingle();
    }

    [Fact]
    public async Task Allowing_overbooking_keeps_a_full_window_on_offer_and_still_counts_the_seat()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(10), TimeSpan.FromHours(12), capacity: 1);
        }

        var clock = new FakeTimeProvider(Tuesday8Am);
        using (var context = _db.CreateContext())
        {
            (await BuildService(context, clock).ReserveSlotAsync(fixture.Window!.Id, NextTuesday)).IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var strict = BuildService(context, clock);
            (await strict.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday)).Value.Reason
                .Should().Be(SlotUnavailabilityReason.FullyBooked, "without overbooking the one seat is taken");
            (await strict.ReserveSlotAsync(fixture.Window!.Id, NextTuesday)).IsFailure.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var lenient = BuildService(context, clock, TestServices.Rules(slot: SlotRules(allowOverbooking: true)));
            (await lenient.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, NextTuesday)).Value.Slots
                .Should().ContainSingle("a full window stays selectable when overbooking is allowed");
            (await lenient.ReserveSlotAsync(fixture.Window!.Id, NextTuesday)).IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var counts = await new SlotCapacityRepository(readContext).GetBookedCountsAsync([fixture.Window!.Id], NextTuesday);
        counts[fixture.Window!.Id].Should().Be(2, "the overbooked seat is counted, so a release stays symmetric");
    }

    /// <summary>A clock that stands still at the instant it is given, so "now", "today" and a window's start stay comparable.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task With_no_saved_rules_the_slot_engine_behaves_as_it_always_did()
    {
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedGeographyAndService(context, DayOfWeek.Tuesday, TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        }

        using var readContext = _db.CreateContext();
        var service = BuildService(readContext, new FakeTimeProvider(Tuesday8Am), TestServices.Rules());

        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday)).Value.Slots
            .Should().ContainSingle("today at 09:00 is bookable at 08:00 when nothing is saved - no lead time, no horizon, same-day allowed");
        (await service.GetAvailableSlotsAsync(fixture.Service.Id, fixture.Locality.Id, TuesdayToday.AddDays(400))).Value.Reason
            .Should().NotBe(SlotUnavailabilityReason.DateOutOfBookableRange, "no horizon without a saved one or a city policy");
    }
}
