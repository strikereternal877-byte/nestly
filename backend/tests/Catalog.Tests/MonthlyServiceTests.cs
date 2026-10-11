using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.MonthlyService;
using Nestly.Domain;
using Nestly.Domain.Meals;
using Nestly.Domain.MonthlyService;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;
using Nestly.Infrastructure.Services.MonthlyService;

namespace Nestly.Catalog.Tests;

/// <summary>
/// docs/MONTHLY-SERVICE.md: the attendance rules and invoice arithmetic at
/// the domain level, then the whole engagement lifecycle against a real
/// (SQLite) database - request, assignment, day-code check-in, skip, leave,
/// dispute holding the month's invoice, month-end billing, payment crediting
/// the professional's ledger, and the overdue pause/resume loop.
/// </summary>
public sealed class MonthlyServiceTests : IClassFixture<TestDatabase>
{
    private static readonly MonthlyServiceAttendancePolicy Policy = new MonthlyServiceOptions().ToPolicy();
    private readonly TestDatabase _db;

    public MonthlyServiceTests(TestDatabase db) => _db = db;

    // ---- Domain ----

    [Fact]
    public void Plan_requires_hours_for_hourly_and_tasks_for_task_based()
    {
        var hourlyWithoutHours = () => NewPlan(MonthlyServicePlanBasis.Hourly, hours: null, tasks: null);
        hourlyWithoutHours.Should().Throw<ArgumentOutOfRangeException>();

        var taskBasedWithoutTasks = () => NewPlan(MonthlyServicePlanBasis.TaskBased, hours: null, tasks: []);
        taskBasedWithoutTasks.Should().Throw<ArgumentException>();

        var taskBased = NewPlan(MonthlyServicePlanBasis.TaskBased, hours: 3m, tasks: ["Sweeping", " sweeping ", "Dishes"]);
        taskBased.HoursPerVisit.Should().BeNull("hours only apply to hourly plans");
        taskBased.IncludedTasks.Should().Equal("Sweeping", "Dishes");
    }

    [Fact]
    public void Check_in_needs_the_days_code_inside_the_window()
    {
        var row = NewRow(new DateOnly(2026, 10, 5), new TimeOnly(8, 0));
        var inWindow = new DateTime(2026, 10, 5, 8, 5, 0);

        var wrongCode = () => row.CheckIn(row.DayCode == "0000" ? "1111" : "0000", inWindow, DateTime.UtcNow, Policy, null, null);
        wrongCode.Should().Throw<ArgumentException>();

        var tooEarly = () => row.CheckIn(row.DayCode, new DateTime(2026, 10, 5, 6, 30, 0), DateTime.UtcNow, Policy, null, null);
        tooEarly.Should().Throw<InvalidOperationException>();

        row.CheckIn(row.DayCode, inWindow, DateTime.UtcNow, Policy, 26.9m, 75.8m);
        row.Status.Should().Be(MonthlyServiceAttendanceStatus.Present);
        row.MarkedBy.Should().Be(MonthlyServiceAttendanceActor.Provider);
        row.IsBillable(Policy.BillCustomerUnavailable).Should().BeTrue();
    }

    [Fact]
    public void Customer_can_skip_only_before_the_cutoff_and_it_is_not_charged()
    {
        var row = NewRow(new DateOnly(2026, 10, 6), new TimeOnly(8, 0));

        var late = () => row.Skip(new DateTime(2026, 10, 6, 7, 0, 0), DateTime.UtcNow, Policy);
        late.Should().Throw<InvalidOperationException>("the default cutoff is 2 hours before the visit");

        row.Skip(new DateTime(2026, 10, 5, 21, 0, 0), DateTime.UtcNow, Policy);
        row.Status.Should().Be(MonthlyServiceAttendanceStatus.CustomerSkipped);
        row.IsBillable(Policy.BillCustomerUnavailable).Should().BeFalse();
    }

    [Fact]
    public void Leave_must_be_marked_before_the_visit_and_is_not_charged()
    {
        var row = NewRow(new DateOnly(2026, 10, 7), new TimeOnly(8, 0));

        var afterStart = () => row.MarkLeave(null, new DateTime(2026, 10, 7, 8, 1, 0), DateTime.UtcNow);
        afterStart.Should().Throw<InvalidOperationException>();

        row.MarkLeave("Family function", new DateTime(2026, 10, 6, 19, 0, 0), DateTime.UtcNow);
        row.Status.Should().Be(MonthlyServiceAttendanceStatus.ProviderLeave);
        row.IsBillable(Policy.BillCustomerUnavailable).Should().BeFalse();
    }

    [Fact]
    public void Upheld_dispute_turns_a_charged_day_into_an_uncharged_one_and_invoiced_days_are_locked()
    {
        var row = NewRow(new DateOnly(2026, 10, 5), new TimeOnly(8, 0));
        row.ConfirmByCustomer(new DateTime(2026, 10, 5, 9, 0, 0), DateTime.UtcNow, Policy);
        row.RaiseDispute("She did not come", DateTime.UtcNow, Policy);

        var invoiceWhileOpen = () => row.AttachToInvoice(Guid.NewGuid());
        invoiceWhileOpen.Should().Throw<InvalidOperationException>();

        var upheldAsBillable = () => row.ResolveDispute(true, MonthlyServiceAttendanceStatus.Present, null, DateTime.UtcNow, Policy);
        upheldAsBillable.Should().Throw<ArgumentException>();

        row.ResolveDispute(true, MonthlyServiceAttendanceStatus.Absent, "Confirmed with the customer", DateTime.UtcNow, Policy);
        row.Status.Should().Be(MonthlyServiceAttendanceStatus.Absent);
        row.DisputeStatus.Should().Be(MonthlyServiceDisputeStatus.Upheld);

        row.AttachToInvoice(Guid.NewGuid());
        var editAfterInvoice = () => row.CorrectByAdmin(MonthlyServiceAttendanceStatus.Present, null, DateTime.UtcNow);
        editAfterInvoice.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Invoice_bills_charged_visits_at_the_snapshotted_rate_minus_commission()
    {
        var contract = NewContract(NewPlan(MonthlyServicePlanBasis.Hourly, 2m, null), MonthlyServiceWeekdays.All, new TimeOnly(8, 0));
        var counts = new MonthlyServiceVisitCounts(Present: 20, CustomerUnavailable: 2, CustomerSkipped: 3, ProviderLeave: 1, Absent: 1);

        var invoice = new MonthlyServiceInvoice(Guid.NewGuid(), contract, Guid.NewGuid(),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), counts, billCustomerUnavailable: true,
            new DateOnly(2026, 11, 2), dueDays: 7, DateTime.UtcNow);

        invoice.BillableVisits.Should().Be(22);
        invoice.Amount.Should().Be(3300m);
        invoice.CommissionAmount.Should().Be(330m);
        invoice.ProviderNetAmount.Should().Be(2970m);
        invoice.DueDate.Should().Be(new DateOnly(2026, 11, 9));

        var nothingCharged = () => new MonthlyServiceInvoice(Guid.NewGuid(), contract, Guid.NewGuid(),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), new MonthlyServiceVisitCounts(0, 2, 0, 0, 0), billCustomerUnavailable: false,
            new DateOnly(2026, 11, 2), 7, DateTime.UtcNow);
        nothingCharged.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Conflict_is_found_only_on_shared_days_with_overlapping_times()
    {
        var plan = NewPlan(MonthlyServicePlanBasis.Hourly, 2m, null);
        var morningWeekdays = NewContract(plan, MonthlyServiceWeekdays.Monday | MonthlyServiceWeekdays.Wednesday, new TimeOnly(8, 0));
        var overlapping = NewContract(plan, MonthlyServiceWeekdays.Wednesday, new TimeOnly(9, 0));
        var sameTimeOtherDay = NewContract(plan, MonthlyServiceWeekdays.Tuesday, new TimeOnly(8, 0));
        var afterwards = NewContract(plan, MonthlyServiceWeekdays.Monday, new TimeOnly(10, 0));

        MonthlyServiceEngine.FindConflict(overlapping, [morningWeekdays]).Should().Be(morningWeekdays);
        MonthlyServiceEngine.FindConflict(sameTimeOtherDay, [morningWeekdays]).Should().BeNull();
        MonthlyServiceEngine.FindConflict(afterwards, [morningWeekdays]).Should().BeNull("a 2-hour 8am visit ends at 10am");
    }

    [Fact]
    public void Times_per_week_plan_needs_exactly_that_many_weekdays()
    {
        var carWash = NewPlan(MonthlyServicePlanBasis.TaskBased, null, ["Exterior wash"], MonthlyServiceFrequency.TimesPerWeek, 3);

        var tooFew = () => NewContract(carWash, MonthlyServiceWeekdays.Monday | MonthlyServiceWeekdays.Friday, new TimeOnly(7, 0));
        tooFew.Should().Throw<ArgumentException>();

        var contract = NewContract(carWash, MonthlyServiceWeekdays.Monday | MonthlyServiceWeekdays.Wednesday | MonthlyServiceWeekdays.Friday, new TimeOnly(7, 0));
        contract.IsScheduledOn(new DateOnly(2026, 10, 5)).Should().BeTrue("5 Oct 2026 is a Monday");
        contract.IsScheduledOn(new DateOnly(2026, 10, 6)).Should().BeFalse();
    }

    [Fact]
    public void Times_per_month_plan_visits_on_the_chosen_dates_only()
    {
        var carWash = NewPlan(MonthlyServicePlanBasis.TaskBased, null, ["Exterior wash"], MonthlyServiceFrequency.TimesPerMonth, 4);
        var invalidPlan = () => NewPlan(MonthlyServicePlanBasis.TaskBased, null, ["Wash"], MonthlyServiceFrequency.TimesPerMonth, 29);
        invalidPlan.Should().Throw<ArgumentOutOfRangeException>();

        var wrongCount = () => NewContract(carWash, MonthlyServiceWeekdays.None, new TimeOnly(7, 0), MonthDays.ToMask([1, 15]));
        wrongCount.Should().Throw<ArgumentException>();
        var badDate = () => MonthDays.ToMask([30]);
        badDate.Should().Throw<ArgumentOutOfRangeException>();

        var contract = NewContract(carWash, MonthlyServiceWeekdays.All, new TimeOnly(7, 0), MonthDays.ToMask([1, 8, 15, 22]));
        contract.Weekdays.Should().Be(MonthlyServiceWeekdays.None, "a per-month schedule ignores weekdays");
        MonthDays.FromMask(contract.MonthDaysMask).Should().Equal(1, 8, 15, 22);
        contract.IsScheduledOn(new DateOnly(2026, 10, 8)).Should().BeTrue();
        contract.IsScheduledOn(new DateOnly(2026, 10, 9)).Should().BeFalse();
        contract.IsScheduledOn(new DateOnly(2026, 11, 22)).Should().BeTrue();
    }

    [Fact]
    public void Conflict_is_found_between_a_weekday_and_a_monthly_schedule_on_a_shared_date()
    {
        var maid = NewContract(NewPlan(MonthlyServicePlanBasis.Hourly, 2m, null), MonthlyServiceWeekdays.Monday, new TimeOnly(8, 0));
        var washPlan = NewPlan(MonthlyServicePlanBasis.Hourly, 1m, null, MonthlyServiceFrequency.TimesPerMonth, 1);
        // 5 Oct 2026 is a Monday, so the 5th clashes with the Monday maid visit at 8-10am.
        var clashing = NewContract(washPlan, MonthlyServiceWeekdays.None, new TimeOnly(9, 0), MonthDays.ToMask([5]));
        var otherTime = NewContract(washPlan, MonthlyServiceWeekdays.None, new TimeOnly(11, 0), MonthDays.ToMask([5]));

        MonthlyServiceEngine.FindConflict(clashing, [maid]).Should().Be(maid);
        MonthlyServiceEngine.FindConflict(otherTime, [maid]).Should().BeNull();
    }

    [Fact]
    public async Task Car_wash_four_times_a_month_is_scheduled_on_its_dates()
    {
        // 07:30 IST on Thu 1 Oct 2026.
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);

        var plan = await app.Admin.CreatePlanAsync(
            new MonthlyServicePlanUpsertRequest(seed.ServiceId, seed.CityId, "Car wash 4x " + Guid.NewGuid().ToString("N")[..6], null,
                MonthlyServicePlanBasis.TaskBased, null, ["Exterior wash", "Interior vacuum"], 200m, 15m,
                MonthlyServiceFrequency.TimesPerMonth, 4),
            Guid.NewGuid());
        plan.IsSuccess.Should().BeTrue(plan.IsFailure ? plan.Error.Message : null);
        plan.Value.TimesPerPeriod.Should().Be(4);

        var wrong = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Value.Id, seed.AddressId, [], "09:00", new DateOnly(2026, 10, 1), null, null, [1, 15]));
        wrong.IsFailure.Should().BeTrue("four dates are required");

        var requested = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Value.Id, seed.AddressId, [], "09:00", new DateOnly(2026, 10, 1), null, null, [1, 8, 15, 22]));
        requested.IsSuccess.Should().BeTrue(requested.IsFailure ? requested.Error.Message : null);
        requested.Value.MonthDates.Should().Equal(1, 8, 15, 22);

        (await app.Admin.AssignProviderAsync(requested.Value.Id, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        // 14-day horizon from 1 Oct covers the 1st and the 8th only.
        var october = (await app.Customer.GetAttendanceAsync(seed.CustomerId, requested.Value.Id, 2026, 10)).Value;
        october.Items.Select(i => i.Date).Should().Equal(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8));
    }

    [Fact]
    public async Task Replacement_hands_over_skips_and_cancel_clears_every_upcoming_day()
    {
        // 07:30 IST on Mon 5 Oct 2026.
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);

        var plan = (await app.Admin.CreatePlanAsync(PlanRequest(seed), Guid.NewGuid())).Value;
        var contractId = (await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday], "09:00", new DateOnly(2026, 10, 5), null, null))).Value.Id;
        (await app.Admin.AssignProviderAsync(contractId, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        var rows = (await app.Customer.GetAttendanceAsync(seed.CustomerId, contractId, 2026, 10)).Value.Items;
        var tuesday = rows.Single(i => i.Date == new DateOnly(2026, 10, 6));
        var wednesday = rows.Single(i => i.Date == new DateOnly(2026, 10, 7));
        (await app.Customer.SkipAsync(seed.CustomerId, tuesday.Id)).IsSuccess.Should().BeTrue();
        (await app.Provider.MarkLeaveAsync(seed.ProviderId, wednesday.Id, null)).IsSuccess.Should().BeTrue();

        var wrongCode = await app.Provider.CheckInAsync(seed.ProviderId, rows.Single(i => i.Date == new DateOnly(2026, 10, 5)).Id,
            new MonthlyServiceCheckInRequest("abcd", null, null));
        wrongCode.Error.Message.Should().NotContain("Parameter", "users never see .NET argument names");

        var replacement = new Provider(Guid.NewGuid(), "Meena Kumari", "Meena", ProviderType.Individual, "+9197" + Guid.NewGuid().ToString("N")[..8]);
        replacement.ChangeStatus(ProviderStatus.Active);
        var categoryId = await context.Set<Service>().Where(x => x.Id == seed.ServiceId).Select(x => x.CategoryId).SingleAsync();
        context.Add(replacement);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), replacement.Id, categoryId, seed.ServiceId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), replacement.Id, seed.CityId, null, null));
        await context.SaveChangesAsync();
        var replaced = await app.Admin.AssignProviderAsync(contractId, replacement.Id, Guid.NewGuid());
        replaced.IsSuccess.Should().BeTrue(replaced.IsFailure ? replaced.Error.Message : null);

        var afterReplace = (await app.Customer.GetAttendanceAsync(seed.CustomerId, contractId, 2026, 10)).Value.Items;
        afterReplace.Single(i => i.Date == new DateOnly(2026, 10, 6)).Status
            .Should().Be(MonthlyServiceAttendanceStatus.CustomerSkipped, "the customer's skip still stands");
        afterReplace.Single(i => i.Date == new DateOnly(2026, 10, 7)).Status
            .Should().Be(MonthlyServiceAttendanceStatus.Scheduled, "the outgoing professional's leave does not bind the new one");
        (await app.Provider.ListVisitsAsync(replacement.Id, new DateOnly(2026, 10, 7))).Should().ContainSingle();

        (await app.Customer.CancelContractAsync(seed.CustomerId, contractId, "Moving")).IsSuccess.Should().BeTrue();
        (await app.Customer.GetAttendanceAsync(seed.CustomerId, contractId, 2026, 10)).Value.Items
            .Should().NotContain(i => i.Date > new DateOnly(2026, 10, 5), "a stopped service shows nothing ahead");
    }

    [Fact]
    public void Customer_and_professional_are_told_about_the_moments_that_matter()
    {
        var contract = NewContract(NewPlan(MonthlyServicePlanBasis.Hourly, 2m, null), MonthlyServiceWeekdays.All, new TimeOnly(8, 0));
        var providerId = Guid.NewGuid();
        contract.AssignProvider(providerId, DateTime.UtcNow);
        var assigned = contract.DomainEvents.OfType<Nestly.Domain.Events.MonthlyServiceProviderAssignedEvent>().Single();
        assigned.IsReplacement.Should().BeFalse();
        Nestly.Application.Notifications.NotificationIntentPlanner.Plan(assigned)
            .Should().BeEquivalentTo([NotificationEventType.MonthlyProviderAssigned, NotificationEventType.MonthlyNewClient]);

        contract.AssignProvider(Guid.NewGuid(), DateTime.UtcNow);
        var replaced = contract.DomainEvents.OfType<Nestly.Domain.Events.MonthlyServiceProviderAssignedEvent>().Last();
        replaced.IsReplacement.Should().BeTrue();
        replaced.PreviousProviderId.Should().Be(providerId);
        Nestly.Application.Notifications.NotificationIntentPlanner.Plan(replaced)
            .Should().Contain(NotificationEventType.MonthlyClientCancelled, "the outgoing professional must stop going");

        var row = NewRow(new DateOnly(2026, 10, 7), new TimeOnly(8, 0));
        row.MarkLeave(null, new DateTime(2026, 10, 6, 20, 0, 0), DateTime.UtcNow);
        Nestly.Application.Notifications.NotificationIntentPlanner.Plan(row.DomainEvents.Single())
            .Should().Equal(NotificationEventType.MonthlyProviderLeave);

        var unassigned = NewContract(NewPlan(MonthlyServicePlanBasis.Hourly, 2m, null), MonthlyServiceWeekdays.All, new TimeOnly(8, 0));
        unassigned.Cancel(null, DateTime.UtcNow);
        Nestly.Application.Notifications.NotificationIntentPlanner.Plan(unassigned.DomainEvents.Single())
            .Should().BeEmpty("nobody was going, so no professional needs telling");
    }

    [Fact]
    public void Every_monthly_service_notification_has_sms_email_and_push_templates()
    {
        var types = new[]
        {
            NotificationEventType.MonthlyProviderAssigned, NotificationEventType.MonthlyNewClient,
            NotificationEventType.MonthlyProviderLeave, NotificationEventType.MonthlyVisitSkipped,
            NotificationEventType.MonthlyInvoiceIssued, NotificationEventType.MonthlyServicePaused,
            NotificationEventType.MonthlyClientCancelled
        };
        var seeded = Nestly.Infrastructure.Persistence.Seed.NotificationTemplateSeedData.BuildDefaults();
        foreach (var type in types)
        {
            type.ToString().Length.Should().BeLessThanOrEqualTo(30, "event_type columns are 30 characters");
            seeded.Where(r => r.EventType == type).Select(r => r.Channel)
                .Should().BeEquivalentTo([NotificationChannel.Sms, NotificationChannel.Email, NotificationChannel.Push]);
        }
    }

    // ---- Full lifecycle ----

    [Fact]
    public async Task Engagement_runs_from_request_to_paid_invoice()
    {
        // 07:30 IST on Monday 5 Oct 2026.
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);

        var plan = (await app.Admin.CreatePlanAsync(PlanRequest(seed), Guid.NewGuid())).Value;
        var requested = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday],
            "08:00", new DateOnly(2026, 10, 5), null, "Please ring twice"));
        requested.IsSuccess.Should().BeTrue(requested.IsFailure ? requested.Error.Message : null);
        requested.Value.Status.Should().Be(MonthlyServiceContractStatus.PendingAssignment);

        var duplicate = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId, [DayOfWeek.Sunday], "18:00", new DateOnly(2026, 10, 5), null, null));
        duplicate.IsFailure.Should().BeTrue("the same service is already requested at this address");

        var contractId = requested.Value.Id;
        var eligible = (await app.Admin.ListEligibleProvidersAsync(contractId)).Value;
        eligible.Should().Contain(p => p.Id == seed.ProviderId && p.HasSkill && p.ServesCity);

        var assigned = await app.Admin.AssignProviderAsync(contractId, seed.ProviderId, Guid.NewGuid());
        assigned.IsSuccess.Should().BeTrue(assigned.IsFailure ? assigned.Error.Message : null);
        assigned.Value.Contract.Status.Should().Be(MonthlyServiceContractStatus.Active);

        // 14-day horizon from Mon 5 Oct, Mon-Sat: 12 visits.
        var october = (await app.Customer.GetAttendanceAsync(seed.CustomerId, contractId, 2026, 10)).Value;
        october.Items.Should().HaveCount(12);

        var today = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        today.DayCode.Should().MatchRegex("^[0-9]{4}$", "the customer sees today's code");

        clock.Set(new DateTime(2026, 10, 5, 2, 35, 0, DateTimeKind.Utc)); // 08:05 IST
        var visits = await app.Provider.ListVisitsAsync(seed.ProviderId, null);
        visits.Single().Attendance.DayCode.Should().BeNull("the professional never sees the code");
        visits.Single().Attendance.AllowedActions.Should().Contain(MonthlyServiceEngine.Actions.CheckIn);

        var checkIn = await app.Provider.CheckInAsync(seed.ProviderId, today.Id, new MonthlyServiceCheckInRequest(today.DayCode!, null, null));
        checkIn.IsSuccess.Should().BeTrue(checkIn.IsFailure ? checkIn.Error.Message : null);
        checkIn.Value.Status.Should().Be(MonthlyServiceAttendanceStatus.Present);

        var tuesday = october.Items.Single(i => i.Date == new DateOnly(2026, 10, 6));
        (await app.Customer.SkipAsync(seed.CustomerId, tuesday.Id)).Value.Status.Should().Be(MonthlyServiceAttendanceStatus.CustomerSkipped);

        var wednesday = october.Items.Single(i => i.Date == new DateOnly(2026, 10, 7));
        (await app.Provider.MarkLeaveAsync(seed.ProviderId, wednesday.Id, "Festival")).Value.Status.Should().Be(MonthlyServiceAttendanceStatus.ProviderLeave);

        (await app.Customer.DisputeAsync(seed.CustomerId, today.Id, "Left after 30 minutes")).IsSuccess.Should().BeTrue();

        // 00:30 IST on 3 Nov: October is closed and due for billing, but the open dispute holds it.
        clock.Set(new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc));
        var firstRun = await app.Job.RunAsync(CancellationToken.None);
        // The fixture's database is shared across this class's tests, so the
        // job's global count can include other tests' rows - assert on this
        // contract's own register instead.
        firstRun.DaysClosedAsAbsent.Should().BeGreaterThanOrEqualTo(9);
        (await app.Customer.GetAttendanceAsync(seed.CustomerId, contractId, 2026, 10)).Value.Summary.Absent
            .Should().Be(9, "the 9 remaining scheduled days had nothing recorded");
        firstRun.InvoicesIssued.Should().Be(0);
        firstRun.InvoicesHeldForDisputes.Should().Be(1);

        var disputes = await app.Admin.ListOpenDisputesAsync();
        disputes.Should().ContainSingle(d => d.Attendance.Id == today.Id);
        (await app.Admin.ResolveDisputeAsync(today.Id, new MonthlyServiceResolveDisputeRequest(false, null, "Checked in on time"), Guid.NewGuid()))
            .IsSuccess.Should().BeTrue();

        var secondRun = await app.Job.RunAsync(CancellationToken.None);
        secondRun.InvoicesIssued.Should().Be(1);

        var invoice = (await app.Customer.ListInvoicesAsync(seed.CustomerId)).Single();
        invoice.PresentCount.Should().Be(1);
        invoice.CustomerSkippedCount.Should().Be(1);
        invoice.ProviderLeaveCount.Should().Be(1);
        invoice.AbsentCount.Should().Be(9);
        invoice.Amount.Should().Be(150m);
        invoice.CommissionAmount.Should().Be(0m, "the customer view does not expose commission");

        var againRun = await app.Job.RunAsync(CancellationToken.None);
        againRun.InvoicesIssued.Should().Be(0, "re-running the job never double-bills");

        var paid = await app.Customer.PayInvoiceAsync(seed.CustomerId, invoice.Id);
        paid.IsSuccess.Should().BeTrue(paid.IsFailure ? paid.Error.Message : null);
        paid.Value.Status.Should().Be(MonthlyServiceInvoiceStatus.Paid);

        var credit = await context.Set<ProviderEarningLedgerEntry>()
            .SingleAsync(e => e.SourceType == ProviderEarningSourceType.MonthlyServiceInvoice && e.SourceReferenceId == invoice.Id);
        credit.Amount.Should().Be(135m, "Rs 150 minus 10% commission");
    }

    [Fact]
    public async Task Unpaid_invoice_pauses_the_contract_and_paying_it_resumes()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);

        var plan = (await app.Admin.CreatePlanAsync(PlanRequest(seed), Guid.NewGuid())).Value;
        var contractId = (await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId, [DayOfWeek.Monday], "08:00", new DateOnly(2026, 10, 5), null, null))).Value.Id;
        (await app.Admin.AssignProviderAsync(contractId, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        var monday = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        clock.Set(new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc));
        (await app.Customer.ConfirmVisitAsync(seed.CustomerId, monday.Id)).IsSuccess.Should().BeTrue();

        clock.Set(new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc));
        (await app.Job.RunAsync(CancellationToken.None)).InvoicesIssued.Should().Be(1);
        var invoice = (await app.Admin.SearchInvoicesAsync(null, 1, 20)).Items.Single(i => i.ContractId == contractId);
        invoice.DueDate.Should().Be(new DateOnly(2026, 11, 10));

        // Due 10 Nov + 7 days grace: on 18 Nov it is overdue past grace.
        clock.Set(new DateTime(2026, 11, 17, 19, 0, 0, DateTimeKind.Utc));
        var run = await app.Job.RunAsync(CancellationToken.None);
        run.InvoicesMarkedOverdue.Should().BeGreaterThanOrEqualTo(1);
        run.ContractsPausedForNonPayment.Should().BeGreaterThanOrEqualTo(1);
        var paused = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value;
        paused.Status.Should().Be(MonthlyServiceContractStatus.Paused);
        paused.PauseReason.Should().Be(MonthlyServicePauseReason.OverdueInvoice);

        var recorded = await app.Admin.RecordPaymentAsync(invoice.Id,
            new MonthlyServiceRecordPaymentRequest(MonthlyServicePaymentMethod.Cash, "Collected by field team"), Guid.NewGuid());
        recorded.IsSuccess.Should().BeTrue(recorded.IsFailure ? recorded.Error.Message : null);
        recorded.Value.PaymentMethod.Should().Be(MonthlyServicePaymentMethod.Cash);

        var resumed = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value;
        resumed.Status.Should().Be(MonthlyServiceContractStatus.Active);
    }

    [Fact]
    public async Task Cook_for_breakfast_and_dinner_is_a_morning_and_an_evening_contract_one_professional_can_take()
    {
        // 07:30 IST on Thu 1 Oct 2026.
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context, cook: true);
        var app = Build(context, clock);

        var plan = await app.Admin.CreatePlanAsync(
            new MonthlyServicePlanUpsertRequest(seed.ServiceId, seed.CityId, "Home cook " + Guid.NewGuid().ToString("N")[..6], null,
                MonthlyServicePlanBasis.MealBased, null, null, 0m, 10m),
            Guid.NewGuid());
        plan.IsSuccess.Should().BeTrue(plan.IsFailure ? plan.Error.Message : null);
        plan.Value.RatePerVisit.Should().Be(0m, "a meal plan is priced per contract");

        var withoutMeals = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Value.Id, seed.AddressId, [DayOfWeek.Monday], "08:00", new DateOnly(2026, 10, 1), null, null));
        withoutMeals.IsFailure.Should().BeTrue("a cook needs meals and persons");

        var dinnerInTheMorning = await app.Customer.RequestMealContractsAsync(seed.CustomerId, new MonthlyServiceMealContractRequest(
            plan.Value.Id, seed.AddressId, [DayOfWeek.Monday], new DateOnly(2026, 10, 1), null, null, [MealType.Dinner], 2, EveningVisitTime: "09:00"));
        dinnerInTheMorning.IsFailure.Should().BeTrue("an evening visit cannot start at 9 am");

        var requested = await app.Customer.RequestMealContractsAsync(seed.CustomerId, new MonthlyServiceMealContractRequest(
            plan.Value.Id, seed.AddressId,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday],
            new DateOnly(2026, 10, 1), null, "Less oil please", [MealType.Dinner, MealType.Breakfast], 4, EveningVisitTime: "19:30"));
        requested.IsSuccess.Should().BeTrue(requested.IsFailure ? requested.Error.Message : null);
        requested.Value.Should().HaveCount(2);

        var morning = requested.Value[0];
        morning.IncludedTasks.Should().Equal("Breakfast");
        morning.Persons.Should().Be(4);
        morning.VisitStartTime.Should().Be("07:30", "no time given, so the meal's own default");
        morning.RatePerVisit.Should().Be(100m + 2 * 30m);
        morning.HoursPerVisit.Should().Be(1.08m, "45 minutes + 10 for each of 2 extra people");

        var evening = requested.Value[1];
        evening.IncludedTasks.Should().Equal("Dinner");
        evening.VisitStartTime.Should().Be("19:30");
        evening.RatePerVisit.Should().Be(150m + 2 * 40m);

        var secondMorningCook = await app.Customer.RequestMealContractsAsync(seed.CustomerId, new MonthlyServiceMealContractRequest(
            plan.Value.Id, seed.AddressId, [DayOfWeek.Monday], new DateOnly(2026, 10, 1), null, null, [MealType.Lunch], 2));
        secondMorningCook.Error.Code.Should().Be("MonthlyService.DuplicateContract");

        (await app.Admin.AssignProviderAsync(morning.Id, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();
        var sameCookInTheEvening = await app.Admin.AssignProviderAsync(evening.Id, seed.ProviderId, Guid.NewGuid());
        sameCookInTheEvening.IsSuccess.Should().BeTrue("the morning and evening visits never overlap");
    }

    [Fact]
    public async Task Wallet_autopay_never_part_pays_and_settles_the_bill_once_the_balance_covers_it()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);
        var wallet = new WalletService(new WalletLedgerRepository(context), context);

        var contractId = await ContractWithOneVisitAsync(app, seed, clock, payFromWallet: true);
        (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.PayFromWallet.Should().BeTrue();
        await wallet.CreditAsync(seed.CustomerId, 100m, WalletSourceType.ManualAdjustment, null, "test credit");

        clock.Set(new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc));
        var issuedRun = await app.Job.RunAsync(CancellationToken.None);
        issuedRun.InvoicesIssued.Should().Be(1);
        issuedRun.InvoicesPaidFromWallet.Should().Be(0, "Rs 100 does not cover the Rs 150 bill, and a bill is never part-paid");
        (await app.Customer.ListInvoicesAsync(seed.CustomerId)).Single(i => i.ContractId == contractId)
            .Status.Should().Be(MonthlyServiceInvoiceStatus.Issued);
        (await wallet.GetBalanceAsync(seed.CustomerId)).Value.Balance.Should().Be(100m, "nothing was taken");

        await wallet.CreditAsync(seed.CustomerId, 100m, WalletSourceType.ManualAdjustment, null, "top-up");
        clock.Set(new DateTime(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc));
        (await app.Job.RunAsync(CancellationToken.None)).InvoicesPaidFromWallet.Should().Be(1, "the next run retries once the balance covers it");

        var paid = (await app.Customer.ListInvoicesAsync(seed.CustomerId)).Single(i => i.ContractId == contractId);
        paid.Status.Should().Be(MonthlyServiceInvoiceStatus.Paid);
        paid.PaymentMethod.Should().Be(MonthlyServicePaymentMethod.Wallet);
        (await wallet.GetBalanceAsync(seed.CustomerId)).Value.Balance.Should().Be(50m);
        (await new ProviderEarningLedgerRepository(context).FindBySourceAsync(ProviderEarningSourceType.MonthlyServiceInvoice, paid.Id))
            .Should().NotBeNull("the professional is credited exactly as for any other payment");
    }

    [Fact]
    public async Task Customer_pays_a_bill_from_wallet_only_when_the_balance_covers_it()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);
        var wallet = new WalletService(new WalletLedgerRepository(context), context);

        var contractId = await ContractWithOneVisitAsync(app, seed, clock, payFromWallet: false);
        clock.Set(new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc));
        var run = await app.Job.RunAsync(CancellationToken.None);
        run.InvoicesPaidFromWallet.Should().Be(0, "this contract never opted in");
        var invoice = (await app.Customer.ListInvoicesAsync(seed.CustomerId)).Single(i => i.ContractId == contractId);

        var empty = await app.Customer.PayInvoiceFromWalletAsync(seed.CustomerId, invoice.Id);
        empty.Error.Code.Should().Be("MonthlyService.WalletBalanceLow");

        await wallet.CreditAsync(seed.CustomerId, 500m, WalletSourceType.ManualAdjustment, null, "test credit");
        var paid = await app.Customer.PayInvoiceFromWalletAsync(seed.CustomerId, invoice.Id);
        paid.IsSuccess.Should().BeTrue(paid.IsFailure ? paid.Error.Message : null);
        paid.Value.PaymentMethod.Should().Be(MonthlyServicePaymentMethod.Wallet);
        (await wallet.GetBalanceAsync(seed.CustomerId)).Value.Balance.Should().Be(350m);

        (await app.Customer.PayInvoiceFromWalletAsync(seed.CustomerId, invoice.Id)).Error.Code.Should().Be("MonthlyService.AlreadyPaid");
        (await wallet.GetBalanceAsync(seed.CustomerId)).Value.Balance.Should().Be(350m, "a second tap never charges twice");
    }

    [Fact]
    public async Task Daily_wallet_pays_each_day_ahead_turns_off_unpaid_days_and_refunds_days_that_did_not_happen()
    {
        // Mon 5 Oct 2026, 07:30 IST. Mon/Tue/Wed at 08:00, Rs 150 a visit, Rs 200 in the wallet.
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context);
        var app = Build(context, clock);
        var wallet = new WalletService(new WalletLedgerRepository(context), context);
        async Task<decimal> Balance() => (await wallet.GetBalanceAsync(seed.CustomerId)).Value.Balance;
        await wallet.CreditAsync(seed.CustomerId, 200m, WalletSourceType.ManualAdjustment, null, "test credit");

        var plan = (await app.Admin.CreatePlanAsync(PlanRequest(seed), Guid.NewGuid())).Value;
        var request = await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday], "08:00", new DateOnly(2026, 10, 5), null, null,
            BillingMode: MonthlyServiceBillingMode.DailyWallet));
        request.IsSuccess.Should().BeTrue(request.IsFailure ? request.Error.Message : null);
        request.Value.BillingMode.Should().Be(MonthlyServiceBillingMode.DailyWallet);
        var contractId = request.Value.Id;

        (await app.Admin.AssignProviderAsync(contractId, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();
        var monday = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        monday.PrepaidAmount.Should().Be(150m, "assignment pays for the visit due today straight away");
        (await Balance()).Should().Be(50m);
        (await app.Customer.ConfirmVisitAsync(seed.CustomerId, monday.Id)).IsSuccess.Should().BeTrue();

        // Tue 00:30 IST: Rs 50 can't pay Tuesday - the day is off, and Wednesday is already at risk.
        clock.Set(new DateTime(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc));
        var tuesdayRun = await app.Job.RunAsync(CancellationToken.None);
        tuesdayRun.VisitsNotBookedLowBalance.Should().Be(1);
        tuesdayRun.LowBalanceWarnings.Should().Be(1, "Rs 50 won't cover Wednesday either");
        var tuesday = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        tuesday.Status.Should().Be(MonthlyServiceAttendanceStatus.WalletShortfall);
        tuesday.IsBillable.Should().BeFalse();
        tuesday.AllowedActions.Should().Contain(MonthlyServiceEngine.Actions.Rebook);

        // 05:00 IST: booking it again needs the money first.
        clock.Set(new DateTime(2026, 10, 5, 23, 30, 0, DateTimeKind.Utc));
        (await app.Customer.RebookAsync(seed.CustomerId, tuesday.Id)).Error.Code.Should().Be(MonthlyServiceEngine.WalletBalanceLowCode);
        await wallet.CreditAsync(seed.CustomerId, 200m, WalletSourceType.ManualAdjustment, null, "top-up");
        var rebooked = await app.Customer.RebookAsync(seed.CustomerId, tuesday.Id);
        rebooked.IsSuccess.Should().BeTrue(rebooked.IsFailure ? rebooked.Error.Message : null);
        rebooked.Value.Status.Should().Be(MonthlyServiceAttendanceStatus.Scheduled);
        rebooked.Value.PrepaidAmount.Should().Be(150m);
        (await Balance()).Should().Be(100m);

        // Wed 00:30 IST: nobody recorded Tuesday - it closes as missed and its Rs 150 comes back; Wednesday is paid.
        clock.Set(new DateTime(2026, 10, 6, 19, 0, 0, DateTimeKind.Utc));
        var wednesdayRun = await app.Job.RunAsync(CancellationToken.None);
        wednesdayRun.VisitsPaidFromWallet.Should().Be(1);
        (await Balance()).Should().Be(100m, "Rs 100 + Rs 150 refunded for Tuesday - Rs 150 for Wednesday");

        // 05:00 IST: skipping a paid day refunds it.
        clock.Set(new DateTime(2026, 10, 6, 23, 30, 0, DateTimeKind.Utc));
        var wednesday = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        (await app.Customer.SkipAsync(seed.CustomerId, wednesday.Id)).IsSuccess.Should().BeTrue();
        (await Balance()).Should().Be(250m);

        // Month end: October is a paid statement for the one visit that happened, and the professional is credited.
        clock.Set(new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc));
        await app.Job.RunAsync(CancellationToken.None);
        var october = (await app.Customer.ListInvoicesAsync(seed.CustomerId)).Single(i => i.ContractId == contractId);
        october.Amount.Should().Be(150m);
        october.Status.Should().Be(MonthlyServiceInvoiceStatus.Paid);
        october.PaymentMethod.Should().Be(MonthlyServicePaymentMethod.Wallet);
        (await Balance()).Should().Be(100m,
            "the statement takes nothing more - only Tue 3 Nov, a visit day, is paid ahead as usual (Rs 250 - Rs 150)");
        (await new ProviderEarningLedgerRepository(context).FindBySourceAsync(ProviderEarningSourceType.MonthlyServiceInvoice, october.Id))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Monthly_meal_plan_discount_applies_to_each_visit()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));
        await using var context = _db.CreateContext();
        var seed = await SeedAsync(context, cook: true);
        var app = Build(context, clock);

        var plan = await app.Admin.CreatePlanAsync(
            new MonthlyServicePlanUpsertRequest(seed.ServiceId, seed.CityId, "Home cook " + Guid.NewGuid().ToString("N")[..6], null,
                MonthlyServicePlanBasis.MealBased, null, null, 0m, 10m, MonthlyDiscountPercent: 10m),
            Guid.NewGuid());
        plan.Value.MonthlyDiscountPercent.Should().Be(10m);

        var requested = await app.Customer.RequestMealContractsAsync(seed.CustomerId, new MonthlyServiceMealContractRequest(
            plan.Value.Id, seed.AddressId, [DayOfWeek.Monday], new DateOnly(2026, 10, 1), null, null, [MealType.Lunch], 2));
        requested.Value.Single().RatePerVisit.Should().Be(135m, "Rs 150 one-time, 10% off monthly");
    }

    // ---- Helpers ----

    /// <summary>A Rs 150/visit Monday contract, assigned, with Monday 5 Oct 2026 confirmed - one billable visit in October.</summary>
    private static async Task<Guid> ContractWithOneVisitAsync(App app, Seed seed, MutableTimeProvider clock, bool payFromWallet)
    {
        var plan = (await app.Admin.CreatePlanAsync(PlanRequest(seed), Guid.NewGuid())).Value;
        var contractId = (await app.Customer.RequestContractAsync(seed.CustomerId, new MonthlyServiceContractRequest(
            plan.Id, seed.AddressId, [DayOfWeek.Monday], "08:00", new DateOnly(2026, 10, 5), null, null,
            PayFromWallet: payFromWallet))).Value.Id;
        (await app.Admin.AssignProviderAsync(contractId, seed.ProviderId, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        var monday = (await app.Customer.GetContractAsync(seed.CustomerId, contractId)).Value.Today!;
        clock.Set(new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc));
        (await app.Customer.ConfirmVisitAsync(seed.CustomerId, monday.Id)).IsSuccess.Should().BeTrue();
        return contractId;
    }

    private static MonthlyServicePlan NewPlan(
        MonthlyServicePlanBasis basis, decimal? hours, IEnumerable<string>? tasks,
        MonthlyServiceFrequency frequency = MonthlyServiceFrequency.Weekdays, int? timesPerPeriod = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Plan " + Guid.NewGuid().ToString("N")[..6], null, basis, hours, tasks, 150m, 10m, frequency, timesPerPeriod);

    private static MonthlyServiceContract NewContract(MonthlyServicePlan plan, MonthlyServiceWeekdays days, TimeOnly start, int monthDaysMask = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), plan, Guid.NewGuid(), days, start, new DateOnly(2026, 10, 1), null, null, DateTime.UtcNow, monthDaysMask);

    private static MonthlyServiceAttendance NewRow(DateOnly date, TimeOnly start) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), date, start, DateTime.UtcNow);

    private static MonthlyServicePlanUpsertRequest PlanRequest(Seed seed) =>
        new(seed.ServiceId, seed.CityId, "House help 2h " + Guid.NewGuid().ToString("N")[..6], "Daily house help",
            MonthlyServicePlanBasis.Hourly, 2m, ["Sweeping", "Mopping"], 150m, 10m);

    private sealed record Seed(Guid CustomerId, Guid AddressId, Guid ProviderId, Guid ServiceId, Guid CityId);

    private static async Task<Seed> SeedAsync(NestlyDbContext context, bool cook = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var state = new State(Guid.NewGuid(), "Rajasthan", "RJ" + suffix[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Jaipur " + suffix);
        var category = new Category(Guid.NewGuid(), "House Help", "house-help-" + suffix, "desc");
        var service = new Service(Guid.NewGuid(), category.Id, cook ? "Home Cook" : "Maid", (cook ? "cook-" : "maid-") + suffix, "desc", 150m);
        if (cook)
        {
            service.SetPricingType(ServicePricingType.MealBased);
            service.SetMealOptions(MealPricingTests.CookMeals);
        }
        var customer = new Customer(Guid.NewGuid(), "9" + suffix + "1", "Asha Rao", CustomerStatus.Active);
        var address = new CustomerAddress(Guid.NewGuid(), customer.Id, "Home", "12 MI Road", null, null,
            "302001", city.Name, "Rajasthan", 26.9m, 75.8m, "Asha Rao", "9876543210", true);
        var provider = new Provider(Guid.NewGuid(), "Sunita Devi", "Sunita", ProviderType.Individual, "+9198" + suffix);
        provider.ChangeStatus(ProviderStatus.Active);

        context.AddRange(state, city, category, service, customer, address, provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, category.Id, service.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, city.Id, null, null));
        await context.SaveChangesAsync();
        return new Seed(customer.Id, address.Id, provider.Id, service.Id, city.Id);
    }

    private sealed record App(
        MonthlyServiceAdminService Admin,
        MonthlyServiceCustomerService Customer,
        MonthlyServiceProviderService Provider,
        MonthlyServiceDailyJob Job);

    private static App Build(NestlyDbContext context, TimeProvider clock)
    {
        var options = Options.Create(new MonthlyServiceOptions());
        var planRepo = new MonthlyServicePlanRepository(context);
        var contractRepo = new MonthlyServiceContractRepository(context);
        var attendanceRepo = new MonthlyServiceAttendanceRepository(context);
        var invoiceRepo = new MonthlyServiceInvoiceRepository(context);
        var ledgerRepo = new ProviderEarningLedgerRepository(context);
        var ledgerService = new ProviderEarningLedgerService(
            new ProviderRepository(context), ledgerRepo, new BookingRepository(context),
            new PaymentTransactionRepository(context), new ProviderPayoutRepository(context));
        var businessClock = new BusinessClock(clock, Options.Create(new BusinessTimeOptions()));
        var engine = new MonthlyServiceEngine(contractRepo, attendanceRepo, invoiceRepo, ledgerService, ledgerRepo,
            new WalletService(new WalletLedgerRepository(context), context), context, businessClock, clock, options,
            NullLogger<MonthlyServiceEngine>.Instance);
        var job = new MonthlyServiceDailyJob(contractRepo, attendanceRepo, invoiceRepo, engine, NullLogger<MonthlyServiceDailyJob>.Instance);
        var gateway = new SandboxPaymentGateway(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

        return new App(
            new MonthlyServiceAdminService(planRepo, contractRepo, attendanceRepo, invoiceRepo, job,
                new AuditLogWriter(context, new StubAuditContextProvider()), engine, context, NullLogger<MonthlyServiceAdminService>.Instance),
            new MonthlyServiceCustomerService(planRepo, contractRepo, attendanceRepo, invoiceRepo,
                new CustomerAddressRepository(context), new ServiceRepository(context), gateway, gateway, engine, NullLogger<MonthlyServiceCustomerService>.Instance),
            new MonthlyServiceProviderService(contractRepo, attendanceRepo, invoiceRepo, engine, NullLogger<MonthlyServiceProviderService>.Instance),
            job);
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTime utcNow) => Set(utcNow);

        public void Set(DateTime utcNow) => _now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
