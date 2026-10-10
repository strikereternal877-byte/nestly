using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// A recurring plan's regular professional is spoken for at the plan's visit times - including days whose visit has
/// not been created or assigned yet, which is exactly when an unrelated order could otherwise be handed to them and the
/// plan's customer lose the person they were promised. These run the real service over a real database: what matters is
/// what the queries see (history, existing visits, the plan's own projection), not any one branch.
/// </summary>
public sealed class ProviderPlanReservationTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderPlanReservationTests(TestDatabase db) => _db = db;

    private static DateOnly Today => TestServices.Clock().Today;

    // ---- Seeding ---------------------------------------------------------------------------

    private Provider SeedProvider()
    {
        using var context = _db.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        context.Add(provider);
        context.SaveChanges();
        return provider;
    }

    private async Task<RecurringBookingPlan> AddPlanAsync(
        RecurringFixture fixture, DateOnly start,
        RecurringBookingRecurrenceFrequency frequency = RecurringBookingRecurrenceFrequency.Daily, DayOfWeek? dayOfWeek = null,
        SlotWindow? window = null, DateOnly? endDate = null)
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, (window ?? fixture.Morning).Id, 1, frequency, dayOfWeek, null, start, endDate, occurrenceCount: null);

        using var context = _db.CreateContext();
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        return plan;
    }

    private async Task UpdatePlanAsync(RecurringBookingPlan plan, Action<RecurringBookingPlan> change)
    {
        using var context = _db.CreateContext();
        var repository = new RecurringBookingPlanRepository(context);
        var loaded = (await repository.GetByIdAsync(plan.Id))!;
        change(loaded);
        await repository.UpdateAsync(loaded);
    }

    private Booking AddBooking(
        RecurringFixture fixture, DateOnly date, SlotWindow window, BookingStatus status,
        RecurringBookingPlan? plan = null, Provider? assignedTo = null) =>
        RecurringFixtures.AddBooking(_db, fixture, date, window, status, plan, assignedTo);

    /// <summary>
    /// An active plan whose regular professional is <paramref name="provider"/>: the plan has a visit in its history
    /// (yesterday, so it never touches a date under test) that the provider served.
    /// </summary>
    private async Task<RecurringBookingPlan> PlanServedByAsync(
        RecurringFixture fixture, Provider provider, DateOnly start,
        RecurringBookingRecurrenceFrequency frequency = RecurringBookingRecurrenceFrequency.Daily, DayOfWeek? dayOfWeek = null)
    {
        var plan = await AddPlanAsync(fixture, start, frequency, dayOfWeek);
        AddBooking(fixture, Today.AddDays(-1), fixture.Morning, BookingStatus.Confirmed, plan, provider);
        return plan;
    }

    private Task<bool> IsReservedAsync(Provider provider, Booking booking, int horizonDays = 30)
    {
        using var context = _db.CreateContext();
        var service = new ProviderPlanReservationService(
            context, TestServices.Clock(),
            Options.Create(new RecurringBookingOptions { ProviderReservationHorizonDays = horizonDays }),
            NullLogger<ProviderPlanReservationService>.Instance);
        return service.IsReservedByAnotherPlanAsync(provider.Id, booking.Id);
    }

    // ---- Dates the plan has not created a visit for yet ---------------------------------------

    [Fact]
    public async Task An_unrelated_order_cannot_take_the_regular_professional_on_a_date_the_plan_has_yet_to_create()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(2));

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeTrue("the plan will want them that morning, and its visit does not exist yet");
    }

    [Fact]
    public async Task A_different_time_of_day_is_not_reserved()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(2)); // a morning plan

        var afternoon = AddBooking(fixture, Today.AddDays(10), fixture.Afternoon, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, afternoon)).Should().BeFalse("9-13 and 14-18 do not overlap");
    }

    [Fact]
    public async Task Back_to_back_times_do_not_collide()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(2)); // 09:00-13:00

        // A window starting exactly when the plan's ends: [start, end) is half-open, like the double-booking guard.
        SlotWindow touching;
        using (var context = _db.CreateContext())
        {
            touching = new SlotWindow(Guid.NewGuid(), fixture.City.Id, "Right after", TimeSpan.FromHours(13), TimeSpan.FromHours(15));
            context.SlotWindows.Add(touching);
            context.SaveChanges();
        }

        var order = AddBooking(fixture, Today.AddDays(10), touching, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse();
    }

    [Fact]
    public async Task A_nine_to_eleven_plan_holds_its_professional_until_eleven_and_not_a_minute_longer()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();

        SlotWindow nineToEleven, tenToTwelve, elevenToOne, sevenToNine;
        using (var context = _db.CreateContext())
        {
            nineToEleven = new SlotWindow(Guid.NewGuid(), fixture.City.Id, "9-11", TimeSpan.FromHours(9), TimeSpan.FromHours(11));
            tenToTwelve = new SlotWindow(Guid.NewGuid(), fixture.City.Id, "10-12", TimeSpan.FromHours(10), TimeSpan.FromHours(12));
            elevenToOne = new SlotWindow(Guid.NewGuid(), fixture.City.Id, "11-13", TimeSpan.FromHours(11), TimeSpan.FromHours(13));
            sevenToNine = new SlotWindow(Guid.NewGuid(), fixture.City.Id, "7-9", TimeSpan.FromHours(7), TimeSpan.FromHours(9));
            context.SlotWindows.AddRange(nineToEleven, tenToTwelve, elevenToOne, sevenToNine);
            context.SaveChanges();
        }

        var plan = await AddPlanAsync(fixture, Today.AddDays(2), window: nineToEleven);
        AddBooking(fixture, Today.AddDays(-1), nineToEleven, BookingStatus.Confirmed, plan, provider);
        var date = Today.AddDays(10);

        var sameTime = AddBooking(fixture, date, nineToEleven, BookingStatus.Confirmed);
        var overlapping = AddBooking(fixture, date, tenToTwelve, BookingStatus.Confirmed);
        var fromEleven = AddBooking(fixture, date, elevenToOne, BookingStatus.Confirmed);
        var beforeNine = AddBooking(fixture, date, sevenToNine, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, sameTime)).Should().BeTrue("the same 9-11");
        (await IsReservedAsync(provider, overlapping)).Should().BeTrue("10-12 still overlaps the plan's last hour");
        (await IsReservedAsync(provider, fromEleven)).Should().BeFalse("the plan is over at 11:00, so an 11-13 order may have them");
        (await IsReservedAsync(provider, beforeNine)).Should().BeFalse("and so may a 7-9 one");
    }

    [Fact]
    public async Task A_date_before_the_plan_starts_is_not_reserved()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(8));

        var order = AddBooking(fixture, Today.AddDays(4), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_plans_regular_professional_is_reserved()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var original = SeedProvider();
        var current = SeedProvider();
        var stranger = SeedProvider();

        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        AddBooking(fixture, Today.AddDays(-3), fixture.Morning, BookingStatus.Confirmed, plan, original);
        AddBooking(fixture, Today.AddDays(-1), fixture.Morning, BookingStatus.Confirmed, plan, current); // newest assigned visit

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(current, order)).Should().BeTrue();
        (await IsReservedAsync(original, order)).Should().BeFalse("whoever served the newest visit is the regular professional, not whoever served the first");
        (await IsReservedAsync(stranger, order)).Should().BeFalse("they never served the plan");
    }

    [Fact]
    public async Task A_visit_the_customer_cancelled_does_not_make_someone_the_regular_professional()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        AddBooking(fixture, Today.AddDays(-1), fixture.Morning, BookingStatus.CancelledByCustomer, plan, provider);

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse("a cancelled visit's professional may never have set foot in the home");
    }

    [Fact]
    public async Task A_paused_plan_reserves_nothing_it_has_not_already_booked()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await PlanServedByAsync(fixture, provider, Today.AddDays(2));
        await UpdatePlanAsync(plan, p => p.Pause());

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse("a paused plan generates nothing, so it needs nobody");
    }

    [Fact]
    public async Task A_cancelled_plan_reserves_nothing()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await PlanServedByAsync(fixture, provider, Today.AddDays(2));
        await UpdatePlanAsync(plan, p => p.Cancel());

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse();
    }

    [Fact]
    public async Task A_weekly_plan_reserves_only_its_own_weekday()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var firstMonday = Today.AddDays(((int)DayOfWeek.Monday - (int)Today.DayOfWeek + 7) % 7 + 7);
        await PlanServedByAsync(fixture, provider, firstMonday, RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday);

        var monday = AddBooking(fixture, firstMonday.AddDays(7), fixture.Morning, BookingStatus.Confirmed);
        var tuesday = AddBooking(fixture, firstMonday.AddDays(8), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, monday)).Should().BeTrue();
        (await IsReservedAsync(provider, tuesday)).Should().BeFalse("the plan does not visit on Tuesdays");
    }

    [Fact]
    public async Task A_date_after_the_plans_end_date_is_not_reserved()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await AddPlanAsync(fixture, Today.AddDays(2), endDate: Today.AddDays(6));
        AddBooking(fixture, Today.AddDays(-1), fixture.Morning, BookingStatus.Confirmed, plan, provider);

        var within = AddBooking(fixture, Today.AddDays(5), fixture.Morning, BookingStatus.Confirmed);
        var after = AddBooking(fixture, Today.AddDays(7), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, within)).Should().BeTrue();
        (await IsReservedAsync(provider, after)).Should().BeFalse("the plan is over by then");
    }

    // ---- Horizon and switch -----------------------------------------------------------------

    [Fact]
    public async Task Dates_beyond_the_horizon_are_not_reserved_but_the_last_day_inside_it_is()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(2));

        var lastDay = AddBooking(fixture, Today.AddDays(30), fixture.Morning, BookingStatus.Confirmed);
        var beyond = AddBooking(fixture, Today.AddDays(31), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, lastDay, horizonDays: 30)).Should().BeTrue();
        (await IsReservedAsync(provider, beyond, horizonDays: 30)).Should().BeFalse("an open-ended plan must not lock a professional forever");
    }

    [Fact]
    public async Task A_horizon_of_zero_switches_the_rule_off()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        await PlanServedByAsync(fixture, provider, Today.AddDays(2));

        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order, horizonDays: 0)).Should().BeFalse();
    }

    // ---- Visits the plan already has --------------------------------------------------------

    [Fact]
    public async Task A_visit_already_booked_but_not_yet_assigned_still_reserves_the_regular_professional()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await PlanServedByAsync(fixture, provider, Today.AddDays(9)); // the cursor is past the date under test
        var date = Today.AddDays(3);
        AddBooking(fixture, date, fixture.Morning, BookingStatus.Confirmed, plan); // created, paid, no professional yet

        var order = AddBooking(fixture, date, fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeTrue("the double-booking guard cannot see a visit nobody is assigned to yet");
    }

    [Fact]
    public async Task A_visit_that_went_to_someone_else_frees_the_regular_professional_for_that_date()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var regular = SeedProvider();
        var substitute = SeedProvider();
        var plan = await PlanServedByAsync(fixture, regular, Today.AddDays(2));
        var date = Today.AddDays(3);
        AddBooking(fixture, date, fixture.Morning, BookingStatus.Confirmed, plan, substitute);

        var order = AddBooking(fixture, date, fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(regular, order)).Should().BeFalse("the plan's visit that day is already with the substitute");
    }

    [Fact]
    public async Task A_visit_that_expired_unpaid_does_not_reserve_the_date()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await PlanServedByAsync(fixture, provider, Today.AddDays(9));
        var date = Today.AddDays(3);
        AddBooking(fixture, date, fixture.Morning, BookingStatus.Expired, plan);

        var order = AddBooking(fixture, date, fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse("an expired visit never happens");
    }

    // ---- Whose bookings it applies to -------------------------------------------------------

    [Fact]
    public async Task A_plans_own_visits_are_never_blocked_by_the_plan()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var plan = await PlanServedByAsync(fixture, provider, Today.AddDays(2));

        var ownVisit = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed, plan);

        (await IsReservedAsync(provider, ownVisit)).Should().BeFalse("the reservation is for exactly this");
    }

    [Fact]
    public async Task Of_two_plans_that_share_a_professional_the_older_one_keeps_them()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var older = await PlanServedByAsync(fixture, provider, Today.AddDays(2));
        await Task.Delay(30); // the priority is the plans' creation order
        var younger = await PlanServedByAsync(fixture, provider, Today.AddDays(2));

        var olderVisit = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed, older);
        var youngerVisit = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed, younger);

        (await IsReservedAsync(provider, olderVisit)).Should().BeFalse("the younger plan yields to the older, never the reverse");
        (await IsReservedAsync(provider, youngerVisit)).Should().BeTrue("the older plan has them at that time");
    }

    [Fact]
    public async Task A_provider_who_never_served_a_plan_is_never_reserved()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var provider = SeedProvider();
        var order = AddBooking(fixture, Today.AddDays(10), fixture.Morning, BookingStatus.Confirmed);

        (await IsReservedAsync(provider, order)).Should().BeFalse();
    }

    // ---- The gate that uses it --------------------------------------------------------------

    private sealed class StubEligibility(bool answer) : IProviderAssignmentEligibilityService
    {
        public int Calls { get; private set; }

        public Task<bool> IsEligibleAsync(Guid providerId, Guid bookingId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class StubReservations(bool reserved) : IProviderPlanReservationService
    {
        public Task<bool> IsReservedByAnotherPlanAsync(Guid providerId, Guid bookingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(reserved);
    }

    [Fact]
    public async Task A_reserved_provider_is_not_eligible_and_the_inner_gate_is_never_consulted()
    {
        var inner = new StubEligibility(answer: true);
        var gate = new PlanReservationAwareEligibilityService(inner, new StubReservations(reserved: true));

        (await gate.IsEligibleAsync(Guid.NewGuid(), Guid.NewGuid())).Should().BeFalse();
        inner.Calls.Should().Be(0, "a reserved provider must not cost the inner gate's billed route lookup");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unreserved_provider_is_exactly_as_eligible_as_the_inner_gate_says(bool innerAnswer)
    {
        var inner = new StubEligibility(innerAnswer);
        var gate = new PlanReservationAwareEligibilityService(inner, new StubReservations(reserved: false));

        (await gate.IsEligibleAsync(Guid.NewGuid(), Guid.NewGuid())).Should().Be(innerAnswer);
        inner.Calls.Should().Be(1);
    }
}
