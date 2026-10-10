using System.Text.Json;
using FluentAssertions;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;

namespace Nestly.Catalog.Tests;

/// <summary>
/// What a customer is sent after they change their own recurring plan, and what the plan card is given to show. The
/// wording is covered by <see cref="RecurringPlanChangeMessagesTests"/>; these are about the behaviour around it - that
/// every action confirms itself with real facts, that a failed action says nothing, and that a notification which cannot
/// be sent never breaks the action it confirms.
/// </summary>
public sealed class RecurringPlanConfirmationsTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringPlanConfirmationsTests(TestDatabase db) => _db = db;

    private static DateOnly Today => TestServices.Clock().Today;

    // ---- Seeding and reading ----------------------------------------------------------------

    private async Task<RecurringBookingPlan> AddPlanAsync(RecurringFixture fixture, DateOnly start, bool prepaid = false)
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, fixture.Morning.Id, 1, RecurringBookingRecurrenceFrequency.Daily, null, null, start,
            endDate: null, occurrenceCount: null,
            prepaidUpfront: prepaid, prepaidLeadBookingId: prepaid ? Guid.NewGuid() : null);

        using var context = _db.CreateContext();
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        return plan;
    }

    private async Task<IReadOnlyList<NotificationEvent>> ChangeNotificationsAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await new NotificationEventRepository(context).ListByCustomerAsync(customerId))
            .Where(n => n.EventType == NotificationEventType.RecurringPlanChanged)
            .ToList();
    }

    /// <summary>One variable the notification was rendered with (the payload is the serialised variables).</summary>
    private static string Variable(NotificationEvent notification, string name) =>
        JsonDocument.Parse(notification.PayloadJson!).RootElement.GetProperty(name).GetString()!;

    private async Task<BuildingBlocks.Results.Result<RecurringBookingPlanResponse>> RunAsync(
        Func<Nestly.Application.RecurringBookings.IRecurringBookingPlanService, Task<BuildingBlocks.Results.Result<RecurringBookingPlanResponse>>> action,
        IRecurringPlanNotifier? notifier = null)
    {
        using var context = _db.CreateContext();
        return await action(RecurringTestWiring.PlanService(context, new RecurringBookingOptions(), notifier));
    }

    // ---- Confirmations ----------------------------------------------------------------------

    [Fact]
    public async Task Pausing_confirms_it_and_says_how_many_booked_visits_are_still_going_ahead()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(5));
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(3), fixture.Morning, BookingStatus.Confirmed, plan);

        var result = await RunAsync(s => s.PauseAsync(fixture.Customer.Id, plan.Id));

        result.IsSuccess.Should().BeTrue();
        var sent = await ChangeNotificationsAsync(fixture.Customer.Id);
        sent.Should().NotBeEmpty("the customer is told what they just did");
        sent.Should().OnlyContain(n => n.TemplateKey != "no_template", "the confirmation's templates exist - a missing seed would send nothing");
        Variable(sent[0], "ActionTitle").Should().Be("Plan paused");
        Variable(sent[0], "Summary").Should().Contain("2 visits already booked").And.Contain("still charged");
    }

    [Fact]
    public async Task Resuming_confirms_it_with_the_next_visit()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(5));
        (await RunAsync(s => s.PauseAsync(fixture.Customer.Id, plan.Id))).IsSuccess.Should().BeTrue();

        var result = await RunAsync(s => s.ResumeAsync(fixture.Customer.Id, plan.Id));

        result.IsSuccess.Should().BeTrue();
        var resumed = (await ChangeNotificationsAsync(fixture.Customer.Id)).Where(n => Variable(n, "ActionTitle") == "Plan resumed").ToList();
        resumed.Should().NotBeEmpty();
        Variable(resumed[0], "Summary").Should().Contain("Next visit").And.Contain("Morning 09:00-13:00");
    }

    [Fact]
    public async Task Skipping_that_cancels_a_booked_visit_confirms_it_with_what_was_cancelled()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(1));
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Morning, BookingStatus.PaymentPending, plan);

        var result = await RunAsync(s => s.SkipVisitsAsync(
            fixture.Customer.Id, plan.Id, new SkipVisitsRequest(Today.AddDays(9), CancelBookedVisits: true)));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        var skipped = (await ChangeNotificationsAsync(fixture.Customer.Id)).Where(n => Variable(n, "ActionTitle") == "Visits skipped").ToList();
        skipped.Should().NotBeEmpty();
        Variable(skipped[0], "Summary").Should().Contain("No visits before").And.Contain("1 visit already booked before then was cancelled");
    }

    [Fact]
    public async Task Changing_the_time_confirms_the_new_window_and_that_booked_visits_keep_the_old_one()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(1), fixture.Morning, BookingStatus.Confirmed, plan);

        var result = await RunAsync(s => s.ChangeSlotAsync(fixture.Customer.Id, plan.Id, new ChangePlanSlotRequest(fixture.Afternoon.Id)));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        var changed = (await ChangeNotificationsAsync(fixture.Customer.Id)).Where(n => Variable(n, "ActionTitle") == "Visit time changed").ToList();
        changed.Should().NotBeEmpty();
        Variable(changed[0], "Summary").Should().Contain("Afternoon 14:00-18:00").And.Contain("1 visit already booked keeps the old time");
    }

    [Fact]
    public async Task Cancelling_a_plan_confirms_it()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));

        var result = await RunAsync(s => s.CancelAsync(fixture.Customer.Id, plan.Id));

        result.IsSuccess.Should().BeTrue();
        var cancelled = (await ChangeNotificationsAsync(fixture.Customer.Id)).Where(n => Variable(n, "ActionTitle") == "Plan cancelled").ToList();
        cancelled.Should().NotBeEmpty();
        Variable(cancelled[0], "Summary").Should().Contain("No further visits");
    }

    [Fact]
    public async Task An_action_that_fails_sends_no_confirmation()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        (await RunAsync(s => s.PauseAsync(fixture.Customer.Id, plan.Id))).IsSuccess.Should().BeTrue();
        int before = (await ChangeNotificationsAsync(fixture.Customer.Id)).Count;

        var again = await RunAsync(s => s.PauseAsync(fixture.Customer.Id, plan.Id)); // already paused

        again.IsFailure.Should().BeTrue();
        (await ChangeNotificationsAsync(fixture.Customer.Id)).Count.Should().Be(before, "nothing happened, so there is nothing to confirm");
    }

    private sealed class ThrowingNotifier : IRecurringPlanNotifier
    {
        public Task NotifyChangedAsync(RecurringBookingPlan plan, RecurringPlanChange change, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMS provider is down.");
    }

    [Fact]
    public async Task A_confirmation_that_cannot_be_sent_never_fails_or_undoes_the_action()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));

        var result = await RunAsync(s => s.PauseAsync(fixture.Customer.Id, plan.Id), new ThrowingNotifier());

        result.IsSuccess.Should().BeTrue("the customer's action happened; only telling them about it failed");
        result.Value.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        using var context = _db.CreateContext();
        (await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id))!.Status.Should().Be(RecurringBookingPlanStatus.Paused);
    }

    // ---- The plan card ------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_list_carries_what_the_card_shows_the_time_the_price_the_booked_visits_and_a_visit_awaiting_payment()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(6));
        var booked = RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Morning, BookingStatus.Confirmed, plan);
        var awaiting = RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(3), fixture.Morning, BookingStatus.PaymentPending, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(4), fixture.Morning, BookingStatus.Expired, plan);

        BuildingBlocks.Results.Result<IReadOnlyList<RecurringBookingPlanResponse>> list;
        using (var context = _db.CreateContext())
        {
            list = await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).ListAsync(fixture.Customer.Id);
        }

        list.IsSuccess.Should().BeTrue();
        var card = list.Value.Single();
        card.SlotWindowName.Should().Be("Morning");
        card.SlotStartTime.Should().Be(TimeSpan.FromHours(9));
        card.SlotEndTime.Should().Be(TimeSpan.FromHours(13));
        card.UpcomingBookedVisitDates.Should().Equal(Today.AddDays(2)).And.NotContain(Today.AddDays(3), "a visit awaiting payment is not yet booked");
        card.VisitAwaitingPayment.Should().NotBeNull();
        card.VisitAwaitingPayment!.BookingId.Should().Be(awaiting.Id);
        card.VisitAwaitingPayment.SlotDate.Should().Be(Today.AddDays(3));
        card.VisitAwaitingPayment.AmountDue.Should().Be(620m);
        card.VisitAmount.Should().Be(620m, "taken from the newest real visit; the expired one does not count");
        card.CancellationFreeWindowHours.Should().Be(new CancellationPolicyOptions().FreeCancellationWindowHours);
        card.LateCancellationFeePercentage.Should().Be(new CancellationPolicyOptions().LateCancellationFeePercentage);
        booked.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_plan_with_no_visits_yet_has_a_window_but_no_price_and_nothing_awaiting_payment()
    {
        var fixture = RecurringFixtures.Seed(_db);
        await AddPlanAsync(fixture, Today.AddDays(2));

        BuildingBlocks.Results.Result<IReadOnlyList<RecurringBookingPlanResponse>> list;
        using (var context = _db.CreateContext())
        {
            list = await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).ListAsync(fixture.Customer.Id);
        }

        var card = list.Value.Single();
        card.SlotWindowName.Should().Be("Morning");
        card.VisitAmount.Should().BeNull();
        card.VisitAwaitingPayment.Should().BeNull();
        card.UpcomingBookedVisitDates.Should().BeEmpty();
    }
}
