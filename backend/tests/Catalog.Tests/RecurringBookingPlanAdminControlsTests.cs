using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// What an admin can see and do with a recurring plan: the list says how each plan is paid for and why a paused one
/// is paused, the detail adds the customer's wallet and the visits the plan has produced, and support can pause,
/// resume and cancel a plan on the customer's behalf - with the customer told, the reason audited, and a plan support
/// paused staying paused until support resumes it.
/// </summary>
public sealed class RecurringBookingPlanAdminControlsTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringBookingPlanAdminControlsTests(TestDatabase db) => _db = db;

    private static DateOnly Today => TestServices.Clock().Today;

    private static readonly Guid AdminId = Guid.NewGuid();

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static RecurringBookingPlanAdminService AdminService(NestlyDbContext context, IRecurringPlanNotifier? notifier = null) => new(
        context,
        new RecurringBookingPlanRepository(context),
        TestServices.AuditLogWriter(context),
        notifier ?? RecurringTestWiring.PlanNotifier(context),
        TestServices.Clock(),
        NullLogger<RecurringBookingPlanAdminService>.Instance);

    private async Task<RecurringBookingPlan> AddPlanAsync(
        RecurringFixture fixture, DateOnly start, bool prepaid = false, bool applyWalletCredit = false, bool autoCharge = false)
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, fixture.Morning.Id, 1, RecurringBookingRecurrenceFrequency.Daily, null, null, start,
            endDate: null, occurrenceCount: null,
            applyWalletCredit: applyWalletCredit, autoChargeEnabled: autoCharge,
            prepaidUpfront: prepaid, prepaidLeadBookingId: prepaid ? Guid.NewGuid() : null);

        using var context = _db.CreateContext();
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        return plan;
    }

    private async Task<RecurringBookingPlan> ReloadAsync(Guid planId)
    {
        using var context = _db.CreateContext();
        return (await new RecurringBookingPlanRepository(context).GetByIdAsync(planId))!;
    }

    private async Task<IReadOnlyList<NotificationEvent>> ChangeNotificationsAsync(Guid customerId)
    {
        using var context = _db.CreateContext();
        return (await new NotificationEventRepository(context).ListByCustomerAsync(customerId))
            .Where(n => n.EventType == NotificationEventType.RecurringPlanChanged)
            .ToList();
    }

    private static string Variable(NotificationEvent notification, string name) =>
        JsonDocument.Parse(notification.PayloadJson!).RootElement.GetProperty(name).GetString()!;

    private async Task<AuditLog> AuditEntryAsync(Guid planId, string action)
    {
        using var context = _db.CreateContext();
        return await context.Set<AuditLog>().SingleAsync(a => a.EntityName == "RecurringBookingPlan" && a.EntityId == planId.ToString() && a.Action == action);
    }

    private sealed class ThrowingNotifier : IRecurringPlanNotifier
    {
        public Task NotifyChangedAsync(RecurringBookingPlan plan, RecurringPlanChange change, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMS provider is down.");
    }

    // ---- the list ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_carries_how_each_plan_is_paid_for_and_why_a_paused_one_is_paused()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var prepaid = await AddPlanAsync(fixture, Today.AddDays(3), prepaid: true);
        var perVisit = await AddPlanAsync(fixture, Today.AddDays(3), applyWalletCredit: true, autoCharge: true);
        var unpaid = await AddPlanAsync(fixture, Today.AddDays(3));
        using (var context = _db.CreateContext())
        {
            var tracked = await new RecurringBookingPlanRepository(context).GetByIdAsync(unpaid.Id);
            tracked!.PauseForUnpaidVisits().Should().BeTrue();
            await new RecurringBookingPlanRepository(context).UpdateAsync(tracked);
        }

        using var readContext = _db.CreateContext();
        var result = await AdminService(readContext).SearchAsync(
            new AdminRecurringPlanSearchRequest(null, null, fixture.Customer.Id, null));

        var byId = result.Value.Items.ToDictionary(i => i.Id);
        byId[prepaid.Id].PrepaidUpfront.Should().BeTrue();
        byId[prepaid.Id].IsAwaitingPrepayment.Should().BeTrue("the first prepaid cycle has been started and not yet paid");
        byId[perVisit.Id].PrepaidUpfront.Should().BeFalse();
        byId[perVisit.Id].ApplyWalletCredit.Should().BeTrue();
        byId[perVisit.Id].AutoChargeEnabled.Should().BeTrue();
        byId[unpaid.Id].Status.Should().Be(RecurringBookingPlanStatus.Paused);
        byId[unpaid.Id].PauseReason.Should().Be(RecurringBookingPauseReason.UnpaidVisits);
        byId[perVisit.Id].PauseReason.Should().BeNull();
    }

    [Fact]
    public async Task The_list_can_be_filtered_by_pause_reason_and_by_whether_it_is_prepaid()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var prepaid = await AddPlanAsync(fixture, Today.AddDays(3), prepaid: true);
        var perVisit = await AddPlanAsync(fixture, Today.AddDays(3));
        var adminPaused = await AddPlanAsync(fixture, Today.AddDays(3));
        using (var context = _db.CreateContext())
        {
            var tracked = await new RecurringBookingPlanRepository(context).GetByIdAsync(adminPaused.Id);
            tracked!.PauseByAdmin();
            await new RecurringBookingPlanRepository(context).UpdateAsync(tracked);
        }

        using var readContext = _db.CreateContext();
        var service = AdminService(readContext);

        (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, fixture.Customer.Id, null, PauseReason: RecurringBookingPauseReason.Admin)))
            .Value.Items.Should().ContainSingle().Which.Id.Should().Be(adminPaused.Id);

        (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, fixture.Customer.Id, null, PrepaidUpfront: true)))
            .Value.Items.Should().ContainSingle().Which.Id.Should().Be(prepaid.Id);

        (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, fixture.Customer.Id, null, PrepaidUpfront: false)))
            .Value.Items.Select(i => i.Id).Should().BeEquivalentTo([perVisit.Id, adminPaused.Id]);
    }

    // ---- the detail -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_detail_shows_the_customers_wallet_and_the_visits_upcoming_first_then_the_latest_past_ones()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(1), applyWalletCredit: true);

        using (var context = _db.CreateContext())
        {
            await new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(fixture.Customer.Id, 750m, WalletSourceType.TopUp, Guid.NewGuid(), "Wallet top-up");
        }

        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(5), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(-3), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(-1), fixture.Morning, BookingStatus.Expired, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Afternoon, BookingStatus.Confirmed); // not this plan's

        using var readContext = _db.CreateContext();
        var result = await AdminService(readContext).GetAsync(plan.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerMobile.Should().Be(fixture.Customer.Mobile);
        result.Value.WalletBalance.Should().Be(750m);
        result.Value.Plan.ApplyWalletCredit.Should().BeTrue();
        result.Value.Visits.Select(v => v.SlotDate).Should().Equal(Today.AddDays(2), Today.AddDays(5), Today.AddDays(-1), Today.AddDays(-3));
        result.Value.Visits.Select(v => v.Status).Should().Equal(
            BookingStatus.Confirmed, BookingStatus.Confirmed, BookingStatus.Expired, BookingStatus.Confirmed);
        result.Value.Visits.Should().OnlyContain(v => v.BookingReference != string.Empty && v.TotalPayable > 0 && v.StatusLabel != string.Empty);
    }

    [Fact]
    public async Task The_detail_lists_at_most_ten_upcoming_and_ten_past_visits()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(1));
        for (int i = 1; i <= 12; i++)
        {
            RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(i), fixture.Morning, BookingStatus.Confirmed, plan);
            RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(-i), fixture.Morning, BookingStatus.Confirmed, plan);
        }

        using var readContext = _db.CreateContext();
        var visits = (await AdminService(readContext).GetAsync(plan.Id)).Value.Visits;

        visits.Should().HaveCount(20);
        visits.First().SlotDate.Should().Be(Today.AddDays(1));
        visits.Last().SlotDate.Should().Be(Today.AddDays(-10));
    }

    [Fact]
    public async Task A_plan_that_does_not_exist_is_not_found_for_detail_pause_resume_and_cancel()
    {
        using var context = _db.CreateContext();
        var service = AdminService(context);
        var id = Guid.NewGuid();

        (await service.GetAsync(id)).Error.Code.Should().Be("RecurringBookingPlan.NotFound");
        (await service.PauseAsync(id, AdminId, new AdminPauseRecurringPlanRequest("x"))).Error.Code.Should().Be("RecurringBookingPlan.NotFound");
        (await service.ResumeAsync(id, AdminId, new AdminResumeRecurringPlanRequest("x"))).Error.Code.Should().Be("RecurringBookingPlan.NotFound");
        (await service.CancelAsync(id, AdminId, new AdminCancelRecurringPlanRequest("x"))).Error.Code.Should().Be("RecurringBookingPlan.NotFound");
    }

    // ---- pause ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pausing_stops_the_plan_records_who_paused_it_and_audits_the_reason()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context).PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("Customer asked by phone"));

            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(RecurringBookingPlanStatus.Paused);
            result.Value.PauseReason.Should().Be(RecurringBookingPauseReason.Admin);
        }

        var stored = await ReloadAsync(plan.Id);
        stored.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        stored.PauseReason.Should().Be(RecurringBookingPauseReason.Admin);

        var audit = await AuditEntryAsync(plan.Id, "AdminPause");
        audit.NewValues.Should().Contain("Customer asked by phone").And.Contain(AdminId.ToString());
    }

    [Fact]
    public async Task Pausing_tells_the_customer_that_support_did_it_and_how_many_booked_visits_still_go_ahead()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(5));
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(2), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(3), fixture.Morning, BookingStatus.Confirmed, plan);
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(4), fixture.Morning, BookingStatus.Expired, plan); // already off

        using (var context = _db.CreateContext())
        {
            (await AdminService(context).PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("Billing dispute"))).IsSuccess.Should().BeTrue();
        }

        var sent = await ChangeNotificationsAsync(fixture.Customer.Id);
        sent.Should().ContainSingle();
        sent[0].TemplateKey.Should().NotBe("no_template");
        Variable(sent[0], "ActionTitle").Should().Be("Plan paused by support");
        Variable(sent[0], "Summary").Should().Contain("2 visits already booked").And.Contain("Contact support to resume");
        Variable(sent[0], "Summary").Should().NotContain("Billing dispute", "the internal reason is not sent to the customer");
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Pausing_a_plan_that_is_not_active_is_refused_and_tells_nobody(string state)
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        using (var context = _db.CreateContext())
        {
            var tracked = await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id);
            if (state == "paused")
            {
                tracked!.Pause();
            }
            else
            {
                tracked!.Cancel();
            }

            await new RecurringBookingPlanRepository(context).UpdateAsync(tracked);
        }

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context).PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("x"));
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("RecurringBookingPlan.InvalidPause");
        }

        (await ChangeNotificationsAsync(fixture.Customer.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_notification_that_cannot_be_sent_never_fails_or_undoes_the_pause()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context, new ThrowingNotifier())
                .PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("x"));
            result.IsSuccess.Should().BeTrue("the pause happened; only telling the customer failed");
        }

        (await ReloadAsync(plan.Id)).Status.Should().Be(RecurringBookingPlanStatus.Paused);
    }

    // ---- a plan support paused is not the customer's to resume --------------------------------------------------

    [Fact]
    public async Task The_customer_cannot_resume_a_plan_support_paused_but_can_resume_one_they_paused_themselves()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var supportPaused = await AddPlanAsync(fixture, Today.AddDays(2));
        var selfPaused = await AddPlanAsync(fixture, Today.AddDays(2));

        using (var context = _db.CreateContext())
        {
            (await AdminService(context).PauseAsync(supportPaused.Id, AdminId, new AdminPauseRecurringPlanRequest("x"))).IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            (await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).PauseAsync(fixture.Customer.Id, selfPaused.Id))
                .IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var refused = await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).ResumeAsync(fixture.Customer.Id, supportPaused.Id);
            refused.IsFailure.Should().BeTrue();
            refused.Error.Code.Should().Be("RecurringBookingPlan.PausedBySupport");
        }

        using (var context = _db.CreateContext())
        {
            (await RecurringTestWiring.PlanService(context, new RecurringBookingOptions()).ResumeAsync(fixture.Customer.Id, selfPaused.Id))
                .IsSuccess.Should().BeTrue();
        }

        (await ReloadAsync(supportPaused.Id)).Status.Should().Be(RecurringBookingPlanStatus.Paused);
        (await ReloadAsync(selfPaused.Id)).Status.Should().Be(RecurringBookingPlanStatus.Active);
    }

    // ---- resume -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resuming_a_support_paused_plan_runs_it_again_tells_the_customer_and_audits_the_reason()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(4));
        using (var context = _db.CreateContext())
        {
            await AdminService(context).PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("Hold"));
        }

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context).ResumeAsync(plan.Id, AdminId, new AdminResumeRecurringPlanRequest("Sorted with the customer"));

            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(RecurringBookingPlanStatus.Active);
            result.Value.PauseReason.Should().BeNull();
        }

        var resumed = (await ChangeNotificationsAsync(fixture.Customer.Id)).Where(n => Variable(n, "ActionTitle") == "Plan resumed by support").ToList();
        resumed.Should().ContainSingle();
        Variable(resumed[0], "Summary").Should().Contain("Next visit").And.Contain("Morning 09:00-13:00");

        (await AuditEntryAsync(plan.Id, "AdminResume")).NewValues.Should().Contain("Sorted with the customer");
    }

    [Fact]
    public async Task Support_can_resume_a_plan_the_system_paused_for_unpaid_visits()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(4));
        using (var context = _db.CreateContext())
        {
            var tracked = await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id);
            tracked!.PauseForUnpaidVisits().Should().BeTrue();
            await new RecurringBookingPlanRepository(context).UpdateAsync(tracked);
        }

        using (var context = _db.CreateContext())
        {
            (await AdminService(context).ResumeAsync(plan.Id, AdminId, new AdminResumeRecurringPlanRequest("Customer paid by UPI"))).IsSuccess
                .Should().BeTrue();
        }

        var stored = await ReloadAsync(plan.Id);
        stored.Status.Should().Be(RecurringBookingPlanStatus.Active);
        stored.PauseReason.Should().BeNull();
    }

    [Fact]
    public async Task Resuming_moves_a_cursor_that_fell_behind_to_the_next_real_date()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(1));
        using (var context = _db.CreateContext())
        {
            await AdminService(context).PauseAsync(plan.Id, AdminId, new AdminPauseRecurringPlanRequest("Hold"));
            var tracked = await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id);
            context.Entry(tracked!).Property(p => p.NextOccurrenceDate).CurrentValue = Today.AddDays(-20);
            await context.SaveChangesAsync();
        }

        using (var context = _db.CreateContext())
        {
            (await AdminService(context).ResumeAsync(plan.Id, AdminId, new AdminResumeRecurringPlanRequest("Back"))).IsSuccess.Should().BeTrue();
        }

        (await ReloadAsync(plan.Id)).NextOccurrenceDate.Should().BeOnOrAfter(Today,
            "dates that have already gone by cannot be booked, and walking through them would notify the customer for each");
    }

    [Fact]
    public async Task Resuming_a_plan_that_is_not_paused_is_refused_and_tells_nobody()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context).ResumeAsync(plan.Id, AdminId, new AdminResumeRecurringPlanRequest("x"));
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("RecurringBookingPlan.InvalidResume");
        }

        (await ChangeNotificationsAsync(fixture.Customer.Id)).Should().BeEmpty();
    }

    // ---- cancel -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_plan_now_tells_the_customer_that_support_cancelled_it()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        RecurringFixtures.AddBooking(_db, fixture, Today.AddDays(1), fixture.Morning, BookingStatus.Confirmed, plan);

        using (var context = _db.CreateContext())
        {
            var result = await AdminService(context).CancelAsync(plan.Id, AdminId, new AdminCancelRecurringPlanRequest("Duplicate plan"));
            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
        }

        var sent = await ChangeNotificationsAsync(fixture.Customer.Id);
        sent.Should().ContainSingle();
        Variable(sent[0], "ActionTitle").Should().Be("Plan cancelled by support");
        Variable(sent[0], "Summary").Should().Contain("1 visit already booked");
    }

    // ---- validation and domain --------------------------------------------------------------------------------

    [Fact]
    public void A_reason_is_required_for_pause_and_resume()
    {
        new AdminPauseRecurringPlanRequestValidator().Validate(new AdminPauseRecurringPlanRequest("")).IsValid.Should().BeFalse();
        new AdminPauseRecurringPlanRequestValidator().Validate(new AdminPauseRecurringPlanRequest(new string('x', 1001))).IsValid.Should().BeFalse();
        new AdminPauseRecurringPlanRequestValidator().Validate(new AdminPauseRecurringPlanRequest("Customer asked")).IsValid.Should().BeTrue();

        new AdminResumeRecurringPlanRequestValidator().Validate(new AdminResumeRecurringPlanRequest(" ")).IsValid.Should().BeFalse();
        new AdminResumeRecurringPlanRequestValidator().Validate(new AdminResumeRecurringPlanRequest("Sorted")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Only_an_active_plan_can_be_paused_by_an_admin()
    {
        var fixture = RecurringFixtures.Seed(_db);
        var plan = await AddPlanAsync(fixture, Today.AddDays(2));
        var loaded = await ReloadAsync(plan.Id);

        loaded.PauseByAdmin();
        loaded.PauseReason.Should().Be(RecurringBookingPauseReason.Admin);

        var again = () => loaded.PauseByAdmin();
        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_wording_for_support_actions_never_says_you_did_it()
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, RecurringBookingRecurrenceFrequency.Daily, null, null, Today.AddDays(1), endDate: null, occurrenceCount: null);

        foreach (var kind in new[]
        {
            RecurringPlanChangeKind.PausedBySupport, RecurringPlanChangeKind.ResumedBySupport, RecurringPlanChangeKind.CancelledBySupport
        })
        {
            var message = RecurringPlanChangeMessages.Build(plan, "Morning 09:00-13:00", new RecurringPlanChange(kind, 1, Today.AddDays(2)));

            message.Title.Should().EndWith("by support");
            (message.Summary + message.Details).Should().Contain("support team").And.NotContain("You paused").And.NotContain("You cancelled");
        }
    }
}
