using FluentAssertions;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;

namespace Nestly.Catalog.Tests;

/// <summary>
/// What a customer is told after changing their own recurring plan. Each of these pins a sentence the customer would
/// otherwise have to guess at - that pausing and changing the time are free, that visits already booked keep going (and
/// are charged) unless cancelled, and what a cancellation actually cost and refunded - so a rewording that drops one of
/// them fails here instead of in front of a customer.
/// </summary>
public sealed class RecurringPlanChangeMessagesTests
{
    private static readonly DateOnly Next = new(2026, 10, 11); // a Sunday

    private static RecurringBookingPlan Plan(bool prepaid = false, bool wallet = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, null, Next, null, null,
            applyWalletCredit: wallet && !prepaid, prepaidUpfront: prepaid, prepaidLeadBookingId: prepaid ? Guid.NewGuid() : null);

    private static RecurringPlanChangeMessage Build(RecurringBookingPlan plan, RecurringPlanChange change, string window = "Morning 09:00-13:00") =>
        RecurringPlanChangeMessages.Build(plan, window, change);

    // ---- Paused ------------------------------------------------------------------------------

    [Fact]
    public void Pausing_says_that_visits_already_booked_still_happen_and_are_still_charged()
    {
        var message = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.Paused, BookedVisitsStillAhead: 2, NextBookedVisitDate: new DateOnly(2026, 10, 7)));

        message.Title.Should().Be("Plan paused");
        message.Summary.Should().Contain("2 visits").And.Contain("Wed, 7 Oct 2026").And.Contain("still go ahead and are still charged unless you cancel them").And.Contain("Pausing itself is free");
        message.Details.Should().Contain("My bookings").And.Contain("cancellation policy").And.Contain("Resume any time");
    }

    [Fact]
    public void Pausing_with_nothing_booked_does_not_talk_about_booked_visits()
    {
        var message = Build(Plan(), new RecurringPlanChange(RecurringPlanChangeKind.Paused));

        message.Summary.Should().Contain("No new visits").And.Contain("free").And.NotContain("already booked");
        message.Details.Should().NotContain("My bookings");
    }

    [Fact]
    public void One_booked_visit_is_worded_in_the_singular()
    {
        var message = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.Paused, BookedVisitsStillAhead: 1, NextBookedVisitDate: Next));

        message.Summary.Should().Contain("1 visit already booked").And.NotContain("1 visits");
        message.Summary.Should().Contain("still goes ahead and is still charged unless you cancel it")
            .And.NotContain("go ahead").And.NotContain("cancel them");
    }

    // ---- Resumed -----------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, "Each visit is paid as it is booked.")]
    [InlineData(false, true, "Each visit is paid from your wallet as it is booked.")]
    [InlineData(true, false, "Your visits are paid for in advance.")]
    public void Resuming_gives_the_next_visit_its_time_and_how_it_will_be_paid(bool prepaid, bool wallet, string payment)
    {
        var message = Build(Plan(prepaid, wallet), new RecurringPlanChange(RecurringPlanChangeKind.Resumed));

        message.Title.Should().Be("Plan resumed");
        message.Summary.Should().Contain("Next visit Sun, 11 Oct 2026, Morning 09:00-13:00").And.Contain(payment);
    }

    [Fact]
    public void Resuming_a_pay_as_you_go_plan_warns_that_an_unpaid_visit_is_released_but_a_prepaid_one_does_not()
    {
        var payAsYouGo = Build(Plan(), new RecurringPlanChange(RecurringPlanChangeKind.Resumed));
        var prepaid = Build(Plan(prepaid: true), new RecurringPlanChange(RecurringPlanChangeKind.Resumed));

        payAsYouGo.Details.Should().Contain("isn't paid in time is released");
        prepaid.Details.Should().NotContain("released");
    }

    // ---- Skipped -----------------------------------------------------------------------------

    [Fact]
    public void Skipping_states_the_dates_and_that_skipping_is_free()
    {
        var message = Build(Plan(), new RecurringPlanChange(RecurringPlanChangeKind.VisitsSkipped, ResumeOn: Next));

        message.Title.Should().Be("Visits skipped");
        message.Summary.Should().Contain("No visits before Sun, 11 Oct 2026").And.Contain("Your next visit is Sun, 11 Oct 2026");
        message.Details.Should().Contain("Skipping is free");
    }

    [Fact]
    public void Skipping_that_cancelled_paid_visits_reports_the_fees_kept_and_the_refund()
    {
        var message = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.VisitsSkipped, ResumeOn: Next, VisitsCancelled: 2, CancellationFees: 124m, Refunded: 496m));

        message.Summary.Should().Contain("2 visits already booked before then were cancelled")
            .And.Contain("₹124.00 was kept as cancellation fees").And.Contain("₹496.00 is being refunded");
    }

    [Fact]
    public void Cancelling_unpaid_visits_while_skipping_does_not_mention_a_refund()
    {
        var message = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.VisitsSkipped, ResumeOn: Next, VisitsCancelled: 1));

        message.Summary.Should().Contain("1 visit already booked before then was cancelled").And.Contain("no fee was charged")
            .And.NotContain("refund");
    }

    [Fact]
    public void Skipping_while_leaving_booked_visits_says_they_still_go_ahead()
    {
        var message = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.VisitsSkipped, ResumeOn: Next, BookedVisitsStillAhead: 2));

        message.Summary.Should().Contain("2 visits already booked before then still go ahead").And.Contain("charged unless you cancel them");
        message.Details.Should().Contain("cancellation policy");
    }

    // ---- Time changed ------------------------------------------------------------------------

    [Fact]
    public void Changing_the_time_names_the_new_window_says_it_is_free_and_that_booked_visits_keep_the_old_time()
    {
        var message = Build(
            Plan(),
            new RecurringPlanChange(RecurringPlanChangeKind.TimeChanged, BookedVisitsStillAhead: 3),
            window: "Evening 17:00-21:00");

        message.Title.Should().Be("Visit time changed");
        message.Summary.Should().Contain("From Sun, 11 Oct 2026 your visits are at Evening 17:00-21:00")
            .And.Contain("3 visits already booked keep the old time").And.Contain("Changing the time is free");
        message.Details.Should().Contain("reschedule it from My bookings").And.Contain("reschedule policy");
    }

    // ---- Cancelled ---------------------------------------------------------------------------

    [Fact]
    public void Cancelling_a_pay_as_you_go_plan_says_what_happens_to_visits_already_booked()
    {
        var withVisits = Build(Plan(), new RecurringPlanChange(
            RecurringPlanChangeKind.Cancelled, BookedVisitsStillAhead: 2, NextBookedVisitDate: new DateOnly(2026, 10, 7)));
        var without = Build(Plan(), new RecurringPlanChange(RecurringPlanChangeKind.Cancelled));

        withVisits.Summary.Should().Contain("No further visits").And.Contain("2 visits already booked (next: Wed, 7 Oct 2026) still go ahead")
            .And.Contain("charged unless you cancel them");
        without.Summary.Should().Contain("There are no visits already booked.");
    }

    [Fact]
    public void Cancelling_a_prepaid_plan_reports_how_many_visits_were_cancelled_the_fees_and_the_refund_destination()
    {
        var message = Build(Plan(prepaid: true), new RecurringPlanChange(
            RecurringPlanChangeKind.Cancelled, VisitsCancelled: 3, CancellationFees: 100m, Refunded: 1763m));

        message.Summary.Should().Contain("3 visits that had not happened were cancelled")
            .And.Contain("₹100.00 was kept as cancellation fees").And.Contain("₹1763.00 is being refunded to your original payment method");
        message.Details.Should().Contain("booking you placed together with the plan");
    }

    [Fact]
    public void Cancelling_a_prepaid_plan_with_nothing_left_to_cancel_says_so()
    {
        Build(Plan(prepaid: true), new RecurringPlanChange(RecurringPlanChangeKind.Cancelled))
            .Summary.Should().Contain("no remaining visits");
    }

    // ---- Shape -------------------------------------------------------------------------------

    [Fact]
    public void A_typical_summary_is_short_enough_for_a_text_message()
    {
        var change = new RecurringPlanChange(RecurringPlanChangeKind.Paused, BookedVisitsStillAhead: 2, NextBookedVisitDate: Next);

        // SMS wraps the summary with the action title, the service name and a signature; keep the summary itself modest.
        Build(Plan(), change).Summary.Length.Should().BeLessThan(260);
    }

    [Fact]
    public void Every_kind_of_change_has_a_title_a_summary_and_details()
    {
        foreach (var kind in Enum.GetValues<RecurringPlanChangeKind>())
        {
            var message = Build(Plan(), new RecurringPlanChange(kind, ResumeOn: Next));

            message.Title.Should().NotBeNullOrWhiteSpace(kind.ToString());
            message.Summary.Should().NotBeNullOrWhiteSpace(kind.ToString());
            message.Details.Should().NotBeNullOrWhiteSpace(kind.ToString());
        }
    }
}
