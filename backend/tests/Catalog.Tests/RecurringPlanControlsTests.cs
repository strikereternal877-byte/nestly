using FluentAssertions;
using Nestly.Application;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;
using Fixture = Nestly.Catalog.Tests.RecurringFixture;

namespace Nestly.Catalog.Tests;

/// <summary>
/// What a customer can do to a pay-as-you-go recurring plan once it is running: skip visits while they are away,
/// change the plan's time, resume after a long pause - and what the system does when visits keep going unpaid or the
/// wallet that pays them runs low. The domain rules are exercised directly; the rest go through the real services
/// against a real database, because the point of each is what ends up persisted and what is left alone.
/// </summary>
public sealed class RecurringPlanControlsTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringPlanControlsTests(TestDatabase db) => _db = db;

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- Domain ----------------------------------------------------------------------------

    private static RecurringBookingPlan DomainPlan(
        DateOnly start, RecurringBookingRecurrenceFrequency frequency = RecurringBookingRecurrenceFrequency.Daily,
        DayOfWeek? dayOfWeek = null, DateOnly? endDate = null, int? occurrenceCount = null, bool prepaid = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, frequency, dayOfWeek, null, start, endDate, occurrenceCount,
            prepaidUpfront: prepaid, prepaidLeadBookingId: prepaid ? Guid.NewGuid() : null);

    [Fact]
    public void A_customers_pause_records_why_and_resume_clears_it()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 1));

        plan.Pause();
        plan.PauseReason.Should().Be(RecurringBookingPauseReason.Customer);

        plan.Resume();
        plan.PauseReason.Should().BeNull();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Visits_that_keep_expiring_unpaid_pause_a_pay_as_you_go_plan_but_never_a_prepaid_one()
    {
        var payAsYouGo = DomainPlan(new DateOnly(2026, 10, 1));
        payAsYouGo.PauseForUnpaidVisits().Should().BeTrue();
        payAsYouGo.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        payAsYouGo.PauseReason.Should().Be(RecurringBookingPauseReason.UnpaidVisits);
        payAsYouGo.PauseForUnpaidVisits().Should().BeFalse("already paused - no second notification");

        var prepaid = DomainPlan(new DateOnly(2026, 10, 1), prepaid: true);
        prepaid.PauseForUnpaidVisits().Should().BeFalse("its visits are paid for up front");
        prepaid.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Resuming_after_the_plans_dates_have_gone_by_moves_to_the_next_real_date_instead_of_replaying_the_past()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 1));
        plan.Pause();

        plan.Resume(today: new DateOnly(2026, 10, 20));

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 10, 20));
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Resuming_keeps_a_date_that_is_still_ahead_so_the_customer_loses_nothing_they_were_owed()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 20));
        plan.Pause();

        plan.Resume(today: new DateOnly(2026, 10, 10));

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 10, 20));
    }

    [Fact]
    public void Resuming_a_weekly_plan_lands_on_its_own_weekday()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 5), RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday);
        plan.Pause();

        plan.Resume(today: new DateOnly(2026, 10, 14)); // a Wednesday

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 10, 19));
        plan.NextOccurrenceDate.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void Resuming_past_a_plans_end_date_completes_it()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 1), endDate: new DateOnly(2026, 10, 10));
        plan.Pause();

        plan.Resume(today: new DateOnly(2026, 10, 20));

        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void Skipping_until_a_date_moves_the_next_visit_there_and_leaves_the_plan_active()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 5));

        int moved = plan.SkipVisitsUntil(new DateOnly(2026, 10, 12), today: new DateOnly(2026, 10, 3));

        moved.Should().Be(7);
        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 10, 12));
        plan.SkipUntilDate.Should().Be(new DateOnly(2026, 10, 12));
        plan.SkipRangesUsed.Should().Be(1);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Skipping_does_not_use_up_a_visit_count_and_pushes_an_end_date_out_by_the_days_skipped()
    {
        var counted = DomainPlan(new DateOnly(2026, 10, 5), occurrenceCount: 4);
        counted.SkipVisitsUntil(new DateOnly(2026, 10, 12), today: new DateOnly(2026, 10, 3));
        counted.OccurrenceCount.Should().Be(4);
        counted.CompletedOccurrenceCount.Should().Be(0, "skipped dates are not charged against what the customer bought");

        var dated = DomainPlan(new DateOnly(2026, 10, 5), endDate: new DateOnly(2026, 10, 15));
        dated.SkipVisitsUntil(new DateOnly(2026, 10, 12), today: new DateOnly(2026, 10, 3));
        dated.EndDate.Should().Be(new DateOnly(2026, 10, 22), "the 7 days skipped are added back, so the customer still gets what they were promised");
    }

    [Fact]
    public void Skipping_a_weekly_plan_lands_on_its_own_weekday_after_the_chosen_date()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 5), RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday);

        plan.SkipVisitsUntil(new DateOnly(2026, 10, 14), today: new DateOnly(2026, 10, 3)); // a Wednesday

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 10, 19));
    }

    [Fact]
    public void Skipping_is_refused_when_it_has_no_effect_or_does_not_apply()
    {
        var today = new DateOnly(2026, 10, 3);

        var notAfterToday = () => DomainPlan(new DateOnly(2026, 10, 5)).SkipVisitsUntil(today, today);
        notAfterToday.Should().Throw<ArgumentOutOfRangeException>();

        var nothingToSkip = () => DomainPlan(new DateOnly(2026, 10, 20)).SkipVisitsUntil(new DateOnly(2026, 10, 10), today);
        nothingToSkip.Should().Throw<InvalidOperationException>("no visit falls before that date");

        var paused = DomainPlan(new DateOnly(2026, 10, 5));
        paused.Pause();
        var onPaused = () => paused.SkipVisitsUntil(new DateOnly(2026, 10, 12), today);
        onPaused.Should().Throw<InvalidOperationException>();

        var onPrepaid = () => DomainPlan(new DateOnly(2026, 10, 5), prepaid: true).SkipVisitsUntil(new DateOnly(2026, 10, 12), today);
        onPrepaid.Should().Throw<InvalidOperationException>("a prepaid plan's visits already exist");
    }

    [Fact]
    public void Changing_a_plans_time_sets_the_new_window_and_nothing_else()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 5));
        var next = plan.NextOccurrenceDate;
        var newWindow = Guid.NewGuid();

        plan.ChangeSlotWindow(newWindow);

        plan.SlotWindowId.Should().Be(newWindow);
        plan.NextOccurrenceDate.Should().Be(next);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Changing_a_plans_time_is_refused_for_the_same_window_an_empty_id_a_prepaid_plan_or_an_ended_plan()
    {
        var plan = DomainPlan(new DateOnly(2026, 10, 5));
        var sameWindow = () => plan.ChangeSlotWindow(plan.SlotWindowId);
        sameWindow.Should().Throw<InvalidOperationException>();

        var empty = () => plan.ChangeSlotWindow(Guid.Empty);
        empty.Should().Throw<ArgumentException>();

        var prepaid = () => DomainPlan(new DateOnly(2026, 10, 5), prepaid: true).ChangeSlotWindow(Guid.NewGuid());
        prepaid.Should().Throw<InvalidOperationException>();

        var cancelled = DomainPlan(new DateOnly(2026, 10, 5));
        cancelled.Cancel();
        var onCancelled = () => cancelled.ChangeSlotWindow(Guid.NewGuid());
        onCancelled.Should().Throw<InvalidOperationException>();
    }

    // ---- Database fixture -------------------------------------------------------------------

    private Fixture Seed() => RecurringFixtures.Seed(_db);

    private async Task<RecurringBookingPlan> AddPlanAsync(
        Fixture fixture, DateOnly start, bool applyWalletCredit = false,
        RecurringBookingRecurrenceFrequency frequency = RecurringBookingRecurrenceFrequency.Daily, DayOfWeek? dayOfWeek = null,
        bool prepaid = false)
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, fixture.Morning.Id, 1, frequency, dayOfWeek, null, start, endDate: null, occurrenceCount: null,
            applyWalletCredit: applyWalletCredit && !prepaid, prepaidUpfront: prepaid, prepaidLeadBookingId: prepaid ? Guid.NewGuid() : null);

        using var context = _db.CreateContext();
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        return plan;
    }

    private async Task<RecurringBookingPlan> ReloadAsync(Guid planId)
    {
        using var context = _db.CreateContext();
        return (await new RecurringBookingPlanRepository(context).GetByIdAsync(planId))!;
    }

    private async Task<IReadOnlyList<Booking>> BookingsAsync(Guid planId)
    {
        using var context = _db.CreateContext();
        return await new BookingRepository(context).ListByRecurringPlanAsync(planId);
    }

    private async Task RunSchedulerAsync(int leadTimeDays)
    {
        using var context = _db.CreateContext();
        await RecurringTestWiring.Scheduler(context, new RecurringBookingOptions { LeadTimeDays = leadTimeDays })
            .ProcessDueOccurrencesAsync(CancellationToken.None);
    }

    private async Task<BuildingBlocks.Results.Result<RecurringBookingPlanResponse>> SkipAsync(
        Guid customerId, Guid planId, DateOnly resumeOn, bool cancelBooked = false, RecurringBookingOptions? options = null)
    {
        using var context = _db.CreateContext();
        return await RecurringTestWiring.PlanService(context, options ?? new RecurringBookingOptions())
            .SkipVisitsAsync(customerId, planId, new SkipVisitsRequest(resumeOn, cancelBooked));
    }

    private async Task<BuildingBlocks.Results.Result<RecurringBookingPlanResponse>> ChangeSlotAsync(Guid customerId, Guid planId, Guid windowId)
    {
        using var context = _db.CreateContext();
        return await RecurringTestWiring.PlanService(context, new RecurringBookingOptions())
            .ChangeSlotAsync(customerId, planId, new ChangePlanSlotRequest(windowId));
    }

    // ---- Skip visits (service) ---------------------------------------------------------------

    [Fact]
    public async Task Skipping_visits_keeps_the_plan_active_and_the_scheduler_creates_nothing_until_the_date_is_near()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        var resumeOn = TestServices.Clock().Today.AddDays(20);

        var result = await SkipAsync(fixture.Customer.Id, plan.Id, resumeOn);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        result.Value.SkipUntilDate.Should().Be(resumeOn);
        result.Value.SkipRangesUsed.Should().Be(1);
        result.Value.Status.Should().Be(RecurringBookingPlanStatus.Active);

        await RunSchedulerAsync(leadTimeDays: 3);

        (await BookingsAsync(plan.Id)).Should().BeEmpty("nothing is due until the plan's next date comes within the lead time");
        (await ReloadAsync(plan.Id)).NextOccurrenceDate.Should().Be(resumeOn);
    }

    [Fact]
    public async Task Skipping_visits_is_limited_in_how_far_ahead_how_often_and_to_a_future_date()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        var today = TestServices.Clock().Today;
        var options = new RecurringBookingOptions { MaxSkipDays = 30, MaxSkipRangesPerPlan = 2 };

        (await SkipAsync(fixture.Customer.Id, plan.Id, today, options: options)).Error.Code
            .Should().Be("RecurringBookingPlan.InvalidSkipDate");
        (await SkipAsync(fixture.Customer.Id, plan.Id, today.AddDays(31), options: options)).Error.Code
            .Should().Be("RecurringBookingPlan.SkipTooFar");

        (await SkipAsync(fixture.Customer.Id, plan.Id, today.AddDays(5), options: options)).IsSuccess.Should().BeTrue();
        (await SkipAsync(fixture.Customer.Id, plan.Id, today.AddDays(10), options: options)).IsSuccess.Should().BeTrue();

        var third = await SkipAsync(fixture.Customer.Id, plan.Id, today.AddDays(15), options: options);
        third.IsFailure.Should().BeTrue();
        third.Error.Code.Should().Be("RecurringBookingPlan.SkipLimitReached");
    }

    [Fact]
    public async Task Skipping_visits_is_refused_for_a_paused_plan_a_prepaid_plan_and_somebody_elses_plan()
    {
        var fixture = Seed();
        var other = Seed();
        var resumeOn = TestServices.Clock().Today.AddDays(10);

        var paused = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        using (var context = _db.CreateContext())
        {
            await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).PauseAsync(fixture.Customer.Id, paused.Id);
        }

        (await SkipAsync(fixture.Customer.Id, paused.Id, resumeOn)).Error.Code.Should().Be("RecurringBookingPlan.InvalidSkip");

        var prepaid = await AddPlanAsync(fixture, UtcToday.AddDays(2), prepaid: true);
        (await SkipAsync(fixture.Customer.Id, prepaid.Id, resumeOn)).Error.Code.Should().Be("RecurringBookingPlan.InvalidSkip");

        var active = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        (await SkipAsync(other.Customer.Id, active.Id, resumeOn)).Error.Code.Should().Be("RecurringBookingPlan.NotFound");
    }

    [Fact]
    public async Task Skipping_can_also_cancel_the_visits_already_booked_before_the_chosen_date()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        await RunSchedulerAsync(leadTimeDays: 4); // books ahead; the plan's next date is then beyond the horizon
        var booked = (await BookingsAsync(plan.Id)).OrderBy(b => b.SlotDate).ToList();
        booked.Should().HaveCountGreaterThan(2);

        var resumeOn = booked[^1].SlotDate; // everything before the last booked day is skipped
        var result = await SkipAsync(fixture.Customer.Id, plan.Id, resumeOn, cancelBooked: true);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        var after = (await BookingsAsync(plan.Id)).OrderBy(b => b.SlotDate).ToList();
        after.Where(b => b.SlotDate < resumeOn).Should().OnlyContain(b => b.Status == BookingStatus.CancelledByCustomer);
        after.Where(b => b.SlotDate >= resumeOn).Should().OnlyContain(b => b.Status == BookingStatus.PaymentPending, "visits on or after the chosen date are left alone");
        (await ReloadAsync(plan.Id)).SkipRangesUsed.Should().Be(0, "nothing was moved, so no skip period was used");
    }

    [Fact]
    public async Task Skipping_without_cancelling_leaves_booked_visits_alone_and_says_so_when_there_is_nothing_to_move()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        await RunSchedulerAsync(leadTimeDays: 4);
        var booked = (await BookingsAsync(plan.Id)).OrderBy(b => b.SlotDate).ToList();

        var result = await SkipAsync(fixture.Customer.Id, plan.Id, booked[^1].SlotDate, cancelBooked: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringBookingPlan.InvalidSkip");
        (await BookingsAsync(plan.Id)).Should().OnlyContain(b => b.Status == BookingStatus.PaymentPending);
    }

    // ---- Resume (service) --------------------------------------------------------------------

    [Fact]
    public async Task Resuming_a_plan_that_sat_paused_past_some_of_its_dates_does_not_replay_them()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(-10));

        using (var context = _db.CreateContext())
        {
            var service = RecurringTestWiring.PlanService(context, new RecurringBookingOptions());
            var paused = await service.PauseAsync(fixture.Customer.Id, plan.Id);
            paused.Value.PauseReason.Should().Be(RecurringBookingPauseReason.Customer);

            var resumed = await service.ResumeAsync(fixture.Customer.Id, plan.Id);
            resumed.IsSuccess.Should().BeTrue();
            resumed.Value.PauseReason.Should().BeNull();
            resumed.Value.NextOccurrenceDate.Should().BeOnOrAfter(TestServices.Clock().Today);
        }

        // The scheduler then books real dates and does not walk through the ten that went by.
        await RunSchedulerAsync(leadTimeDays: 3);
        using var assertContext = _db.CreateContext();
        var history = await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id);
        var earliestAllowed = TestServices.Clock().Today.AddDays(-1);
        history.Should().OnlyContain(h => h.ScheduledDate >= earliestAllowed);
    }

    // ---- Change time (service) ---------------------------------------------------------------

    [Fact]
    public async Task Changing_a_plans_time_moves_future_visits_and_leaves_booked_ones_where_they_are()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        await RunSchedulerAsync(leadTimeDays: 3);
        var alreadyBooked = await BookingsAsync(plan.Id);
        alreadyBooked.Should().NotBeEmpty();

        var result = await ChangeSlotAsync(fixture.Customer.Id, plan.Id, fixture.Afternoon.Id);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        result.Value.SlotWindowId.Should().Be(fixture.Afternoon.Id);
        (await BookingsAsync(plan.Id)).Should().OnlyContain(b => b.SlotWindowId == fixture.Morning.Id, "visits already booked keep their slot");

        // The next visits the scheduler creates use the new time.
        await RunSchedulerAsync(leadTimeDays: 8);
        var all = await BookingsAsync(plan.Id);
        all.Should().HaveCountGreaterThan(alreadyBooked.Count);
        all.Where(b => !alreadyBooked.Any(a => a.Id == b.Id)).Should().OnlyContain(b => b.SlotWindowId == fixture.Afternoon.Id);
    }

    [Fact]
    public async Task Changing_a_plans_time_to_a_window_that_cannot_serve_the_next_visit_is_refused_and_changes_nothing()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(2));

        var result = await ChangeSlotAsync(fixture.Customer.Id, plan.Id, fixture.Closed.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringBookingPlan.SlotNotAvailable");
        (await ReloadAsync(plan.Id)).SlotWindowId.Should().Be(fixture.Morning.Id);
    }

    [Fact]
    public async Task Changing_a_plans_time_is_refused_for_the_same_time_a_paused_plan_a_prepaid_plan_and_somebody_elses_plan()
    {
        var fixture = Seed();
        var other = Seed();

        var active = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        (await ChangeSlotAsync(fixture.Customer.Id, active.Id, fixture.Morning.Id)).Error.Code.Should().Be("RecurringBookingPlan.SlotChangeNotAllowed");
        (await ChangeSlotAsync(other.Customer.Id, active.Id, fixture.Afternoon.Id)).Error.Code.Should().Be("RecurringBookingPlan.NotFound");

        var paused = await AddPlanAsync(fixture, UtcToday.AddDays(2));
        using (var context = _db.CreateContext())
        {
            await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).PauseAsync(fixture.Customer.Id, paused.Id);
        }

        var whilePaused = await ChangeSlotAsync(fixture.Customer.Id, paused.Id, fixture.Afternoon.Id);
        whilePaused.Error.Code.Should().Be("RecurringBookingPlan.SlotChangeNotAllowed");
        whilePaused.Error.Message.Should().Contain("Resume");

        var prepaid = await AddPlanAsync(fixture, UtcToday.AddDays(2), prepaid: true);
        (await ChangeSlotAsync(fixture.Customer.Id, prepaid.Id, fixture.Afternoon.Id)).Error.Code.Should().Be("RecurringBookingPlan.SlotChangeNotAllowed");
    }

    // ---- Visits that keep going unpaid ---------------------------------------------------------

    private Task<Booking> AddVisitAsync(Fixture fixture, RecurringBookingPlan plan, DateOnly date, BookingStatus finalStatus)
    {
        using var context = _db.CreateContext();
        var booking = new Booking(
            Guid.NewGuid(), fixture.Customer.Id,
            new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
            fixture.Address.Id,
            new AddressSnapshot("Home", "12 MG Road", null, null, fixture.Address.Pincode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(fixture.Morning.Id, date, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 620m),
            recurringBookingPlanId: plan.Id);
        booking.AddItem(Guid.NewGuid(), fixture.Service.Id, fixture.Service.Name, fixture.Service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        switch (finalStatus)
        {
            case BookingStatus.Expired:
                booking.TransitionTo(BookingStatus.Expired);
                break;
            case BookingStatus.Confirmed:
                booking.TransitionTo(BookingStatus.Confirmed);
                break;
            case BookingStatus.PaymentPending:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(finalStatus));
        }

        context.Bookings.Add(booking);
        context.SaveChanges();
        return Task.FromResult(booking);
    }

    private async Task NotifyExpiredAsync(Booking booking)
    {
        using var context = _db.CreateContext();
        await RecurringTestWiring.ReleaseHandler(context).Handle(
            new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(booking.Id, BookingStatus.PaymentPending, BookingStatus.Expired)),
            CancellationToken.None);
    }

    private async Task<int> NotificationsAsync(Guid customerId, NotificationEventType type)
    {
        using var context = _db.CreateContext();
        return (await new NotificationEventRepository(context).ListByCustomerAsync(customerId)).Count(n => n.EventType == type);
    }

    [Fact]
    public async Task Two_visits_in_a_row_expiring_unpaid_pause_the_plan_and_tell_the_customer()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1));
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(5), BookingStatus.Expired);
        var latest = await AddVisitAsync(fixture, plan, UtcToday.AddDays(6), BookingStatus.Expired);

        await NotifyExpiredAsync(latest);

        var reloaded = await ReloadAsync(plan.Id);
        reloaded.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        reloaded.PauseReason.Should().Be(RecurringBookingPauseReason.UnpaidVisits);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringPlanPaused)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task One_unpaid_visit_does_not_pause_the_plan()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1));
        var only = await AddVisitAsync(fixture, plan, UtcToday.AddDays(5), BookingStatus.Expired);

        await NotifyExpiredAsync(only);

        (await ReloadAsync(plan.Id)).Status.Should().Be(RecurringBookingPlanStatus.Active);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringPlanPaused)).Should().Be(0);
    }

    [Fact]
    public async Task A_paid_visit_between_two_unpaid_ones_breaks_the_streak()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1));
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(5), BookingStatus.Expired);
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(6), BookingStatus.Confirmed);
        var latest = await AddVisitAsync(fixture, plan, UtcToday.AddDays(7), BookingStatus.Expired);

        await NotifyExpiredAsync(latest);

        (await ReloadAsync(plan.Id)).Status.Should().Be(RecurringBookingPlanStatus.Active, "they paid in between, so they are still engaged");
    }

    [Fact]
    public async Task A_visit_still_awaiting_payment_is_not_counted_either_way()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1));
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(5), BookingStatus.Expired);
        var second = await AddVisitAsync(fixture, plan, UtcToday.AddDays(6), BookingStatus.Expired);
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(7), BookingStatus.PaymentPending); // the next one, not yet decided

        await NotifyExpiredAsync(second);

        (await ReloadAsync(plan.Id)).Status.Should().Be(RecurringBookingPlanStatus.Paused);
    }

    [Fact]
    public async Task A_prepaid_plan_is_never_paused_for_unpaid_visits_and_a_second_expiry_does_not_notify_twice()
    {
        var fixture = Seed();

        var prepaid = await AddPlanAsync(fixture, UtcToday.AddDays(1), prepaid: true);
        await AddVisitAsync(fixture, prepaid, UtcToday.AddDays(5), BookingStatus.Expired);
        var prepaidLatest = await AddVisitAsync(fixture, prepaid, UtcToday.AddDays(6), BookingStatus.Expired);
        await NotifyExpiredAsync(prepaidLatest);
        (await ReloadAsync(prepaid.Id)).Status.Should().Be(RecurringBookingPlanStatus.Active);

        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1));
        await AddVisitAsync(fixture, plan, UtcToday.AddDays(5), BookingStatus.Expired);
        var second = await AddVisitAsync(fixture, plan, UtcToday.AddDays(6), BookingStatus.Expired);
        await NotifyExpiredAsync(second);
        var third = await AddVisitAsync(fixture, plan, UtcToday.AddDays(7), BookingStatus.Expired);
        await NotifyExpiredAsync(third); // already paused: nothing more to do, nothing more to say

        var sent = await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringPlanPaused);
        sent.Should().BeGreaterThan(0);
        sent.Should().BeLessThanOrEqualTo(3, "one notification (at most one row per channel), not one per expiry");
    }

    // ---- Wallet running low --------------------------------------------------------------------

    private async Task CreditWalletAsync(Guid customerId, decimal amount)
    {
        using var context = _db.CreateContext();
        await new WalletService(new WalletLedgerRepository(context), context)
            .CreditAsync(customerId, amount, WalletSourceType.PromotionalCredit, null, "Test credit");
    }

    [Fact]
    public async Task A_wallet_paid_plan_warns_once_a_day_when_the_balance_would_no_longer_cover_the_next_visits()
    {
        var fixture = Seed();
        await CreditWalletAsync(fixture.Customer.Id, 700m); // one visit (620) fully, then 80 left
        var first = await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);

        await RunSchedulerAsync(leadTimeDays: 1);

        (await BookingsAsync(first.Id)).Should().ContainSingle(b => b.Status == BookingStatus.Confirmed, "the wallet paid the visit in full");
        var warnings = await NotificationsAsync(fixture.Customer.Id, NotificationEventType.WalletLowBalance);
        warnings.Should().BeGreaterThan(0);

        // A second plan the same day, same shortfall: not warned again.
        await CreditWalletAsync(fixture.Customer.Id, 620m);
        var second = await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);
        await RunSchedulerAsync(leadTimeDays: 1);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.WalletLowBalance)).Should().Be(warnings);
        (await BookingsAsync(second.Id)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_wallet_that_comfortably_covers_the_next_visits_triggers_no_warning()
    {
        var fixture = Seed();
        await CreditWalletAsync(fixture.Customer.Id, 5_000m);
        await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);

        await RunSchedulerAsync(leadTimeDays: 1);

        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.WalletLowBalance)).Should().Be(0);
    }

    [Fact]
    public async Task A_visit_the_wallet_could_not_cover_says_so_and_is_neither_a_generic_payment_reminder_nor_a_low_balance_warning()
    {
        var fixture = Seed();
        var plan = await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);

        await RunSchedulerAsync(leadTimeDays: 1);

        (await BookingsAsync(plan.Id)).Should().ContainSingle(b => b.Status == BookingStatus.PaymentPending);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringWalletShortfall)).Should().BeGreaterThan(0);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringBookingPaymentDue)).Should().Be(0, "the wallet-specific message replaces the generic reminder");
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.WalletLowBalance)).Should().Be(0, "the shortfall message already says it");
    }

    private async Task<IReadOnlyList<NotificationEvent>> ShortfallNotificationsAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await new NotificationEventRepository(context).ListByCustomerAsync(customerId))
            .Where(n => n.EventType == NotificationEventType.RecurringWalletShortfall)
            .ToList();
    }

    private static string PayloadValue(NotificationEvent notification, string name) =>
        System.Text.Json.JsonDocument.Parse(notification.PayloadJson!).RootElement.GetProperty(name).GetString()!;

    [Fact]
    public async Task The_shortfall_message_says_the_wallet_was_empty_and_how_much_is_still_to_pay()
    {
        var fixture = Seed();
        await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);

        await RunSchedulerAsync(leadTimeDays: 1);

        var sent = await ShortfallNotificationsAsync(fixture.Customer.Id);
        sent.Should().NotBeEmpty();
        sent.Should().OnlyContain(n => n.TemplateKey != "no_template", "the template exists - a missing seed would send nothing");
        PayloadValue(sent[0], "WalletNote").Should().Contain("wallet was empty");
        PayloadValue(sent[0], "Amount").Should().Contain("500.00", "the whole visit is still to pay");
    }

    [Fact]
    public async Task The_shortfall_message_says_how_much_the_wallet_did_pay_when_it_covered_part()
    {
        var fixture = Seed();
        await CreditWalletAsync(fixture.Customer.Id, 200m); // of a 500 visit
        await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: true);

        await RunSchedulerAsync(leadTimeDays: 1);

        var sent = await ShortfallNotificationsAsync(fixture.Customer.Id);
        sent.Should().NotBeEmpty();
        PayloadValue(sent[0], "WalletNote").Should().Contain("200.00").And.Contain("paid from your wallet");
        PayloadValue(sent[0], "Amount").Should().Contain("300.00", "500 less the 200 the wallet paid");
    }

    [Fact]
    public async Task A_plan_that_does_not_use_the_wallet_still_gets_the_ordinary_payment_reminder()
    {
        var fixture = Seed();
        await AddPlanAsync(fixture, UtcToday.AddDays(1), applyWalletCredit: false);

        await RunSchedulerAsync(leadTimeDays: 1);

        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringBookingPaymentDue)).Should().BeGreaterThan(0);
        (await NotificationsAsync(fixture.Customer.Id, NotificationEventType.RecurringWalletShortfall)).Should().Be(0);
    }
}
