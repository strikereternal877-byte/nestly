using FluentAssertions;
using Nestly.Domain;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Pure domain-logic coverage for <see cref="RecurringBookingPlan"/> (tasks
/// 184-186): occurrence-date generation from a recurrence rule, the
/// skipped-occurrence-doesn't-count-against-budget open decision, and
/// pause/resume/cancel transitions. No database needed - the aggregate holds
/// no infrastructure dependencies.
/// </summary>
public sealed class RecurringBookingPlanTests
{
    private static RecurringBookingPlan WeeklyPlan(DateOnly startDate, DayOfWeek dayOfWeek, int? occurrenceCount = 4) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Weekly, dayOfWeek, recurrenceDayOfMonth: null,
            startDate, endDate: null, occurrenceCount);

    private static RecurringBookingPlan BiweeklyPlan(DateOnly startDate, DayOfWeek dayOfWeek, int? occurrenceCount = 4) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Biweekly, dayOfWeek, recurrenceDayOfMonth: null,
            startDate, endDate: null, occurrenceCount);

    private static RecurringBookingPlan MonthlyPlan(DateOnly startDate, int dayOfMonth, int? occurrenceCount = 6, DateOnly? endDate = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Monthly, recurrenceDayOfWeek: null, dayOfMonth,
            startDate, endDate, occurrenceCount);

    /// <summary>A plan bounded only by an end date - <c>occurrenceCount</c> null, so end-date termination is the only thing that can complete it.</summary>
    private static RecurringBookingPlan WeeklyPlanEndingOn(DateOnly startDate, DayOfWeek dayOfWeek, DateOnly endDate) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Weekly, dayOfWeek, recurrenceDayOfMonth: null,
            startDate, endDate, occurrenceCount: null);

    [Fact]
    public void Ctor_weekly_plan_rolls_forward_to_the_next_matching_day_of_week()
    {
        // 2026-08-01 is a Saturday; asking for Tuesday should land on 2026-08-04.
        var start = new DateOnly(2026, 8, 1);
        var plan = WeeklyPlan(start, DayOfWeek.Tuesday);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void Ctor_weekly_plan_keeps_start_date_when_it_already_matches_the_day_of_week()
    {
        var start = new DateOnly(2026, 8, 1); // Saturday
        var plan = WeeklyPlan(start, DayOfWeek.Saturday);

        plan.NextOccurrenceDate.Should().Be(start);
    }

    [Fact]
    public void Ctor_monthly_plan_clamps_day_of_month_to_the_actual_month_length()
    {
        // Requesting day 31 in a 30-day-or-fewer month must clamp, not throw or roll into the next month.
        var start = new DateOnly(2026, 2, 1);
        var plan = MonthlyPlan(start, dayOfMonth: 31);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void RecordOccurrenceBooked_advances_the_next_date_and_increments_completed_count()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 4);
        var due = plan.NextOccurrenceDate;

        plan.RecordOccurrenceBooked(due);

        plan.CompletedOccurrenceCount.Should().Be(1);
        plan.NextOccurrenceDate.Should().Be(due.AddDays(7));
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void RecordOccurrenceSkipped_advances_the_next_date_but_does_not_count_against_the_occurrence_budget()
    {
        // OPEN DECISION (RecurringBookingPlan doc comment): a supply-side
        // miss must not cost the customer one of their paid-for occurrences.
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 4);
        var due = plan.NextOccurrenceDate;

        plan.RecordOccurrenceSkipped(due);

        plan.CompletedOccurrenceCount.Should().Be(0);
        plan.NextOccurrenceDate.Should().Be(due.AddDays(7));
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void Plan_completes_once_the_occurrence_budget_is_exhausted()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 2);

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void ReleaseOccurrence_decrements_the_completed_count_and_reopens_a_plan_the_budget_had_completed()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 2);
        var firstDue = plan.NextOccurrenceDate;
        plan.RecordOccurrenceBooked(firstDue);
        var secondDue = plan.NextOccurrenceDate;
        plan.RecordOccurrenceBooked(secondDue);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed, "both promised occurrences were booked");

        // The first occurrence's booking is later cancelled before the visit.
        plan.ReleaseOccurrence();

        plan.CompletedOccurrenceCount.Should().Be(1, "one of the two booked occurrences never actually happened");
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active, "the budget is no longer exhausted, so the plan owes one more visit");
        plan.NextOccurrenceDate.Should().Be(secondDue.AddDays(7), "the make-up occurrence continues the existing cadence rather than picking a new date");
    }

    [Fact]
    public void ReleaseOccurrence_never_goes_negative()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 4);

        plan.ReleaseOccurrence();

        plan.CompletedOccurrenceCount.Should().Be(0);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void ReleaseOccurrence_does_not_reopen_a_plan_that_completed_by_reaching_its_end_date()
    {
        // Bounded by EndDate only (occurrenceCount null) - see WeeklyPlanEndingOn's doc comment.
        var start = new DateOnly(2026, 8, 4); // Tuesday
        var plan = WeeklyPlanEndingOn(start, DayOfWeek.Tuesday, endDate: start);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed, "the very next date after the only occurrence is already past EndDate");

        plan.ReleaseOccurrence();

        plan.CompletedOccurrenceCount.Should().Be(0);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed, "EndDate is a hard calendar boundary, not a budget - releasing an occurrence cannot move it");
    }

    [Fact]
    public void ReleaseOccurrence_never_reopens_a_cancelled_plan()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 2);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Cancel();

        plan.ReleaseOccurrence();

        plan.CompletedOccurrenceCount.Should().Be(0);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Cancelled, "Cancel is a deliberate one-way door - a booking-level event must never reverse it");
    }

    [Fact]
    public void SetOccurrenceBounds_lowers_the_occurrence_count_while_leaving_the_plan_active()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 3);

        plan.OccurrenceCount.Should().Be(3);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active, "1 of the new 3 is booked - two more are still owed");
    }

    [Fact]
    public void SetOccurrenceBounds_completes_the_plan_immediately_when_the_new_count_is_already_met()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 2);

        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed, "both of the new 2 occurrences are already booked - nothing left to schedule");
    }

    [Fact]
    public void SetOccurrenceBounds_throws_when_reducing_below_what_is_already_booked()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        var act = () => plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
        plan.OccurrenceCount.Should().Be(10, "the rejected edit must not partially apply");
    }

    [Fact]
    public void SetOccurrenceBounds_with_neither_bound_makes_the_plan_open_ended_and_keeps_it_active()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);

        plan.SetOccurrenceBounds(endDate: null, occurrenceCount: null);

        plan.IsOpenEnded.Should().BeTrue();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void SetOccurrenceBounds_completes_the_plan_when_the_new_end_date_is_already_past_the_next_occurrence()
    {
        // 2026-08-01 is a Saturday, so NextOccurrenceDate rolls forward to
        // 2026-08-04 (Tuesday) - an end date between the two (08-03) is
        // still on/after StartDate but already before what's next due.
        var plan = WeeklyPlan(new DateOnly(2026, 8, 1), DayOfWeek.Tuesday, occurrenceCount: 10);

        plan.SetOccurrenceBounds(endDate: new DateOnly(2026, 8, 3), occurrenceCount: null);

        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void SetOccurrenceBounds_is_callable_while_paused()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);
        plan.Pause();

        plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 5);

        plan.OccurrenceCount.Should().Be(5);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Paused, "editing bounds is not itself a resume");
    }

    [Fact]
    public void SetOccurrenceBounds_throws_once_the_plan_is_terminal()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 10);
        plan.Cancel();

        var act = () => plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 5);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RecordOccurrence_throws_when_the_date_does_not_match_the_plans_next_due_date()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);

        var act = () => plan.RecordOccurrenceBooked(plan.NextOccurrenceDate.AddDays(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Pause_then_Resume_preserves_the_next_occurrence_date()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        var dueBeforePause = plan.NextOccurrenceDate;

        plan.Pause();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Paused);

        plan.Resume();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
        plan.NextOccurrenceDate.Should().Be(dueBeforePause);
    }

    [Fact]
    public void Pause_throws_when_the_plan_is_not_active()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.Cancel();

        var act = () => plan.Pause();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_is_terminal_and_cannot_be_resumed()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.Cancel();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
        var act = () => plan.Resume();
        act.Should().Throw<InvalidOperationException>();

        var cancelAgain = () => plan.Cancel();
        cancelAgain.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetAutoCharge_toggles_the_flag_regardless_of_pause_state()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.AutoChargeEnabled.Should().BeFalse("consent is opt-in, never on by default");

        plan.SetAutoCharge(true);
        plan.AutoChargeEnabled.Should().BeTrue();

        plan.Pause();
        plan.SetAutoCharge(false);
        plan.AutoChargeEnabled.Should().BeFalse("a standing preference, not a scheduling state - callable while paused");
    }

    [Fact]
    public void SetAutoCharge_throws_once_the_plan_is_terminal()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.Cancel();

        var act = () => plan.SetAutoCharge(true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Ctor_without_end_date_or_occurrence_count_creates_an_open_ended_plan()
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Weekly, DayOfWeek.Monday, recurrenceDayOfMonth: null,
            startDate: new DateOnly(2026, 8, 1), endDate: null, occurrenceCount: null);

        plan.IsOpenEnded.Should().BeTrue();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    // ---- Task 296: frequency date arithmetic ----------------------------

    [Fact]
    public void Ctor_biweekly_plan_rolls_forward_to_the_next_matching_day_of_week()
    {
        // 2026-08-01 is a Saturday; asking for Tuesday should land on 2026-08-04.
        var plan = BiweeklyPlan(new DateOnly(2026, 8, 1), DayOfWeek.Tuesday);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void Biweekly_advances_fourteen_days_not_seven()
    {
        // The distinguishing property of Biweekly. A weekly-vs-biweekly mixup
        // is invisible to the constructor (both land on the same first date)
        // and only shows up on the advance.
        var plan = BiweeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        var first = plan.NextOccurrenceDate;

        plan.RecordOccurrenceBooked(first);

        plan.NextOccurrenceDate.Should().Be(first.AddDays(14));
        plan.NextOccurrenceDate.DayOfWeek.Should().Be(DayOfWeek.Tuesday);
    }

    [Fact]
    public void Weekly_advances_seven_days_and_stays_on_the_same_day_of_week()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 5);
        var first = plan.NextOccurrenceDate;

        plan.RecordOccurrenceBooked(first);

        plan.NextOccurrenceDate.Should().Be(first.AddDays(7));
        plan.NextOccurrenceDate.DayOfWeek.Should().Be(DayOfWeek.Tuesday);
    }

    [Fact]
    public void Monthly_day_31_clamps_in_a_short_month_but_does_not_ratchet_down_afterwards()
    {
        // The classic month-end bug: clamping 31 -> 28 for February and then
        // treating 28 as the plan's day forever, so a customer who asked for
        // "the 31st" silently becomes "the 28th" from March onwards. The
        // requested day of month is the source of truth every month; the
        // clamp applies per month, never to the stored rule.
        var plan = MonthlyPlan(new DateOnly(2026, 1, 31), dayOfMonth: 31, occurrenceCount: 4);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 1, 31));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 2, 28));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 3, 31));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 4, 30));
    }

    [Fact]
    public void Monthly_day_31_clamps_to_29_in_a_leap_February()
    {
        var plan = MonthlyPlan(new DateOnly(2028, 1, 31), dayOfMonth: 31, occurrenceCount: 3);

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public void Monthly_plan_starting_after_its_day_of_month_lands_in_the_next_month()
    {
        var plan = MonthlyPlan(new DateOnly(2026, 8, 20), dayOfMonth: 5);

        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 9, 5));
    }

    // ---- Task 296: end-date vs occurrence-count termination -------------

    [Fact]
    public void Plan_completes_once_the_next_date_would_fall_past_the_end_date()
    {
        // Bounded by an end date ONLY (no occurrence count), so nothing but
        // the end-date check can terminate this plan.
        var plan = WeeklyPlanEndingOn(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, endDate: new DateOnly(2026, 8, 15));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate); // 08-04, next 08-11 is still within the end date
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
        plan.NextOccurrenceDate.Should().Be(new DateOnly(2026, 8, 11));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate); // 08-11, next 08-18 is past 08-15
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void End_date_termination_also_applies_to_a_skipped_occurrence()
    {
        // A skip does not consume the occurrence budget, but it does advance
        // the schedule - so an end-date-bounded plan must still be able to
        // run out of calendar while never delivering anything.
        var plan = WeeklyPlanEndingOn(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, endDate: new DateOnly(2026, 8, 6));

        plan.RecordOccurrenceSkipped(plan.NextOccurrenceDate);

        plan.CompletedOccurrenceCount.Should().Be(0);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void Occurrence_count_terminates_a_plan_before_its_end_date_when_both_are_set()
    {
        // Both bounds set: whichever is reached first wins. Here the budget
        // (2) runs out long before the end date (a year out).
        var plan = MonthlyPlan(new DateOnly(2026, 8, 5), dayOfMonth: 5, occurrenceCount: 2, endDate: new DateOnly(2027, 8, 5));

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void PreviewUpcomingOccurrenceDates_stops_at_the_end_date()
    {
        var plan = WeeklyPlanEndingOn(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, endDate: new DateOnly(2026, 8, 25));

        var preview = plan.PreviewUpcomingOccurrenceDates(10);

        // 08-04, 08-11, 08-18, 08-25 - and nothing past the end date.
        preview.Should().HaveCount(4);
        preview[^1].Should().Be(new DateOnly(2026, 8, 25));
    }

    // ---- Task 296: Active/Paused/Cancelled transitions -------------------

    [Fact]
    public void A_paused_plan_records_no_occurrences_until_it_is_resumed()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.Pause();

        var booked = () => plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        booked.Should().Throw<InvalidOperationException>();

        var skipped = () => plan.RecordOccurrenceSkipped(plan.NextOccurrenceDate);
        skipped.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_cancelled_plan_records_no_occurrences()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);
        plan.Cancel();

        var act = () => plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_completed_plan_cannot_be_cancelled_or_paused()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 1);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);

        var cancel = () => plan.Cancel();
        cancel.Should().Throw<InvalidOperationException>();

        var pause = () => plan.Pause();
        pause.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resume_throws_on_an_active_plan_so_resume_is_never_a_no_op_success()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday);

        var act = () => plan.Resume();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PreviewUpcomingOccurrenceDates_returns_the_requested_count_capped_by_the_remaining_budget()
    {
        var plan = WeeklyPlan(new DateOnly(2026, 8, 4), DayOfWeek.Tuesday, occurrenceCount: 3);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate); // 1 of 3 used up

        var preview = plan.PreviewUpcomingOccurrenceDates(10);

        preview.Should().HaveCount(2); // only 2 occurrences left in the budget
        preview.Should().BeInAscendingOrder();
        preview[0].Should().Be(plan.NextOccurrenceDate);
    }

    private static RecurringBookingPlan DailyPlan(DateOnly startDate, int? occurrenceCount = null, DateOnly? endDate = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Daily, recurrenceDayOfWeek: null, recurrenceDayOfMonth: null,
            startDate, endDate, occurrenceCount);

    [Fact]
    public void Ctor_daily_plan_starts_on_the_start_date_and_advances_one_day_at_a_time()
    {
        var start = new DateOnly(2026, 8, 1);
        var plan = DailyPlan(start, occurrenceCount: 3);

        plan.NextOccurrenceDate.Should().Be(start);

        plan.RecordOccurrenceBooked(start);
        plan.NextOccurrenceDate.Should().Be(start.AddDays(1));
    }

    [Fact]
    public void Ctor_daily_plan_rejects_a_day_of_week_or_day_of_month()
    {
        var withDayOfWeek = () => new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, DayOfWeek.Monday, null, new DateOnly(2026, 8, 1), null, 5);
        var withDayOfMonth = () => new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, 15, new DateOnly(2026, 8, 1), null, 5);

        withDayOfWeek.Should().Throw<ArgumentException>();
        withDayOfMonth.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Daily_plan_completes_after_its_occurrence_count()
    {
        var plan = DailyPlan(new DateOnly(2026, 8, 1), occurrenceCount: 2);

        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);

        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);
    }

    [Fact]
    public void Open_ended_plan_never_completes_on_its_own()
    {
        var plan = DailyPlan(new DateOnly(2026, 8, 1));

        for (var i = 0; i < 400; i++)
        {
            plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        }

        plan.IsOpenEnded.Should().BeTrue();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
        plan.PreviewUpcomingOccurrenceDates(5).Should().HaveCount(5);
    }

    [Fact]
    public void PauseForPaymentFailure_pauses_an_open_ended_plan()
    {
        var plan = DailyPlan(new DateOnly(2026, 8, 1));

        plan.PauseForPaymentFailure().Should().BeTrue();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        plan.Resume();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void PauseForPaymentFailure_leaves_a_bounded_plan_alone()
    {
        var plan = DailyPlan(new DateOnly(2026, 8, 1), occurrenceCount: 10);

        plan.PauseForPaymentFailure().Should().BeFalse();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    [Fact]
    public void PauseForPaymentFailure_is_a_no_op_on_a_cancelled_open_ended_plan()
    {
        var plan = DailyPlan(new DateOnly(2026, 8, 1));
        plan.Cancel();

        plan.PauseForPaymentFailure().Should().BeFalse();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
    }

    // ---- Prepaid plans ------------------------------------------------------------------------

    private static RecurringBookingPlan PrepaidDailyPlan(DateOnly startDate, int? occurrenceCount, Guid? leadBookingId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            quantity: 1, RecurringBookingRecurrenceFrequency.Daily, recurrenceDayOfWeek: null, recurrenceDayOfMonth: null,
            startDate, endDate: null, occurrenceCount,
            prepaidUpfront: true, prepaidLeadBookingId: leadBookingId ?? Guid.NewGuid());

    [Fact]
    public void Ctor_prepaid_plan_needs_the_booking_its_payment_page_is_keyed_by()
    {
        var act = () => new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, null, new DateOnly(2026, 8, 1), null, 3,
            prepaidUpfront: true, prepaidLeadBookingId: null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_prepaid_plan_rejects_auto_charge_and_per_visit_wallet_credit()
    {
        var lead = Guid.NewGuid();
        var withAutoCharge = () => new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, null, new DateOnly(2026, 8, 1), null, 3,
            autoChargeEnabled: true, prepaidUpfront: true, prepaidLeadBookingId: lead);
        var withWallet = () => new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, null, new DateOnly(2026, 8, 1), null, 3,
            applyWalletCredit: true, prepaidUpfront: true, prepaidLeadBookingId: lead);

        withAutoCharge.Should().Throw<ArgumentException>();
        withWallet.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_prepaid_plan_waits_on_its_lead_booking_until_the_cycle_is_paid()
    {
        var lead = Guid.NewGuid();
        var plan = PrepaidDailyPlan(new DateOnly(2026, 8, 2), occurrenceCount: null, lead);
        plan.BeginPrepaymentCycle(lead, new DateOnly(2026, 8, 30));

        plan.IsAwaitingPrepayment.Should().BeTrue();
        plan.PrepaidThroughDate.Should().BeNull("an unpaid cycle is not coverage");

        plan.ConfirmPrepayment();

        plan.IsAwaitingPrepayment.Should().BeFalse();
        plan.PrepaidCyclesPaid.Should().Be(1);
        plan.PrepaidThroughDate.Should().Be(new DateOnly(2026, 8, 30));
        plan.PendingPrepaymentThroughDate.Should().BeNull();
    }

    [Fact]
    public void An_unpaid_first_cycle_ends_the_plan()
    {
        var plan = PrepaidDailyPlan(new DateOnly(2026, 8, 2), occurrenceCount: 3);

        plan.AbandonUnpaidFirstCycle().Should().BeTrue();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
        plan.IsAwaitingPrepayment.Should().BeFalse();
    }

    [Fact]
    public void An_unpaid_renewal_pauses_a_plan_that_has_already_delivered_paid_visits_and_first_cycle_abandonment_leaves_it_alone()
    {
        var plan = PrepaidDailyPlan(new DateOnly(2026, 8, 2), occurrenceCount: null);
        plan.BeginPrepaymentCycle(Guid.NewGuid(), new DateOnly(2026, 8, 30));
        plan.ConfirmPrepayment();
        plan.BeginPrepaymentCycle(Guid.NewGuid(), new DateOnly(2026, 9, 29));

        plan.AbandonUnpaidFirstCycle().Should().BeFalse("the first cycle was paid; only the renewal is unpaid");
        plan.AbandonUnpaidRenewal().Should().BeTrue();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        plan.PrepaidThroughDate.Should().Be(new DateOnly(2026, 8, 30), "the unpaid renewal never counted as coverage");
    }

    [Fact]
    public void A_prepaid_plan_that_has_generated_all_its_visits_can_still_be_cancelled()
    {
        var plan = PrepaidDailyPlan(new DateOnly(2026, 8, 2), occurrenceCount: 1);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed);

        plan.Cancel();

        plan.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
    }

    [Fact]
    public void A_prepaid_plans_visits_cannot_be_edited_once_paid_and_it_never_reopens_to_make_up_a_missed_visit()
    {
        var plan = PrepaidDailyPlan(new DateOnly(2026, 8, 2), occurrenceCount: 1);
        plan.RecordOccurrenceBooked(plan.NextOccurrenceDate);
        plan.ConfirmPrepayment();

        var edit = () => plan.SetOccurrenceBounds(endDate: null, occurrenceCount: 5);
        edit.Should().Throw<InvalidOperationException>();

        plan.ReleaseOccurrence();
        plan.Status.Should().Be(RecurringBookingPlanStatus.Completed, "a prepaid plan sold exactly the visits it created");
    }
}
