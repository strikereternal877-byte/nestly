using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 299: the admin-side view of every recurring plan, and the
/// status/cadence/upcoming-volume report behind it.
///
/// Two things are under test here and only one of them is about values:
///
/// 1. The numbers are right - counts per lifecycle status (including the
///    statuses with no rows), the active-plan cadence mix, plans the scheduler
///    has not reached yet, and the volume of already-generated recurring work
///    inside the horizon.
/// 2. The database computes them. A report that streams every plan row into
///    the API process and counts them in C# returns exactly the same numbers,
///    so no assertion on the values can tell the two apart -
///    <see cref="GetReportAsync_aggregates_in_the_database"/> asserts on the
///    emitted SQL instead, which is the only place the difference is visible.
/// </summary>
public sealed class RecurringBookingPlanAdminServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringBookingPlanAdminServiceTests(TestDatabase db) => _db = db;

    private static RecurringBookingPlanAdminService CreateService(NestlyDbContext context) => new(
        context,
        new RecurringBookingPlanRepository(context),
        TestServices.AuditLogWriter(context),
        RecurringTestWiring.PlanNotifier(context),
        TestServices.Clock(),
        NullLogger<RecurringBookingPlanAdminService>.Instance);

    private static readonly DateOnly Today = new(2026, 8, 10);

    private sealed record Fixture(Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window);

    /// <summary>
    /// Every test in this class aggregates over the whole table, so a row left
    /// behind by the previous test would be counted by the next one. The
    /// fixture's database is per-class, so clearing here is enough.
    /// </summary>
    private static void Reset(NestlyDbContext context)
    {
        context.Bookings.RemoveRange(context.Bookings);
        context.SaveChanges();
        context.RecurringBookingOccurrences.RemoveRange(context.RecurringBookingOccurrences);
        context.RecurringBookingPlans.RemoveRange(context.RecurringBookingPlans);
        context.SaveChanges();
    }

    private static Fixture Seed(NestlyDbContext context, string customerName = "Priya Nair", string serviceName = "Deep Clean")
    {
        string pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], customerName, CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, customerName, "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, serviceName, "svc-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

        context.Add(customer);
        context.Add(address);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.SlotWindows.Add(window);
        context.SaveChanges();

        return new Fixture(customer, address, city, locality, service, window);
    }

    private static RecurringBookingPlan NewPlan(
        Fixture fixture,
        RecurringBookingRecurrenceFrequency frequency = RecurringBookingRecurrenceFrequency.Weekly,
        DateOnly? startDate = null,
        int occurrenceCount = 4) =>
        new(Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, fixture.Window.Id, quantity: 1,
            frequency,
            frequency == RecurringBookingRecurrenceFrequency.Monthly ? null : DayOfWeek.Tuesday,
            frequency == RecurringBookingRecurrenceFrequency.Monthly ? 11 : null,
            startDate ?? Today, endDate: null, occurrenceCount: occurrenceCount);

    private static Booking NewBooking(Fixture fixture, Guid? planId, DateOnly slotDate) =>
        new(Guid.NewGuid(),
            fixture.Customer.Id,
            new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
            fixture.Address.Id,
            new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(fixture.Window.Id, slotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m),
            recurringBookingPlanId: planId);

    private static AdminRecurringPlanReportRequest Horizon(int days = 28) =>
        new(Today, Today.AddDays(days));

    [Fact]
    public async Task GetReportAsync_counts_plans_by_lifecycle_status()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        var active = NewPlan(fixture);

        var paused = NewPlan(fixture);
        paused.Pause();

        var cancelled = NewPlan(fixture);
        cancelled.Cancel();

        // A one-occurrence plan completes the moment that occurrence is booked.
        var completed = NewPlan(fixture, occurrenceCount: 1);
        completed.RecordOccurrenceBooked(completed.NextOccurrenceDate);
        completed.Status.Should().Be(RecurringBookingPlanStatus.Completed);

        var secondActive = NewPlan(fixture);

        context.RecurringBookingPlans.AddRange(active, paused, cancelled, completed, secondActive);
        await context.SaveChangesAsync();

        var report = (await CreateService(context).GetReportAsync(Horizon())).Value;

        report.TotalPlans.Should().Be(5);
        report.ByStatus.Should().BeEquivalentTo(new[]
        {
            new RecurringPlanStatusCountRow(RecurringBookingPlanStatus.Active, 2),
            new RecurringPlanStatusCountRow(RecurringBookingPlanStatus.Paused, 1),
            new RecurringPlanStatusCountRow(RecurringBookingPlanStatus.Cancelled, 1),
            new RecurringPlanStatusCountRow(RecurringBookingPlanStatus.Completed, 1),
        });
    }

    [Fact]
    public async Task GetReportAsync_reports_a_zero_for_a_status_with_no_plans()
    {
        // "Cancelled: 0" and a missing Cancelled row read very differently to
        // an admin - the second one looks like cancellations were not measured.
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        context.RecurringBookingPlans.Add(NewPlan(fixture));
        await context.SaveChangesAsync();

        var report = (await CreateService(context).GetReportAsync(Horizon())).Value;

        report.ByStatus.Should().HaveCount(Enum.GetValues<RecurringBookingPlanStatus>().Length);
        report.ByStatus.Single(r => r.Status == RecurringBookingPlanStatus.Cancelled).PlanCount.Should().Be(0);
        report.ActiveByFrequency.Should().HaveCount(Enum.GetValues<RecurringBookingRecurrenceFrequency>().Length);
        report.ActiveByFrequency.Single(r => r.Frequency == RecurringBookingRecurrenceFrequency.Monthly).PlanCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReportAsync_breaks_active_plans_down_by_cadence_and_ignores_the_rest()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        var weekly = NewPlan(fixture, RecurringBookingRecurrenceFrequency.Weekly);
        var biweekly = NewPlan(fixture, RecurringBookingRecurrenceFrequency.Biweekly);
        var monthly = NewPlan(fixture, RecurringBookingRecurrenceFrequency.Monthly);

        // A cancelled weekly plan places no standing load on the platform, so
        // it must not inflate the cadence mix even though it is still a row.
        var cancelledWeekly = NewPlan(fixture, RecurringBookingRecurrenceFrequency.Weekly);
        cancelledWeekly.Cancel();

        context.RecurringBookingPlans.AddRange(weekly, biweekly, monthly, cancelledWeekly);
        await context.SaveChangesAsync();

        var report = (await CreateService(context).GetReportAsync(Horizon())).Value;

        report.ActiveByFrequency.Should().BeEquivalentTo(new[]
        {
            new RecurringPlanFrequencyCountRow(RecurringBookingRecurrenceFrequency.Weekly, 1),
            new RecurringPlanFrequencyCountRow(RecurringBookingRecurrenceFrequency.Biweekly, 1),
            new RecurringPlanFrequencyCountRow(RecurringBookingRecurrenceFrequency.Monthly, 1),
            new RecurringPlanFrequencyCountRow(RecurringBookingRecurrenceFrequency.Daily, 0),
        });
    }

    [Fact]
    public async Task GetReportAsync_counts_only_active_plans_the_scheduler_has_yet_to_reach_inside_the_horizon()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        // Next occurrence lands on 2026-08-11 (the first Tuesday from Today).
        var dueInside = NewPlan(fixture);
        dueInside.NextOccurrenceDate.Should().Be(new DateOnly(2026, 8, 11));

        // Starts well past the horizon.
        var dueOutside = NewPlan(fixture, startDate: Today.AddDays(90));

        // Due inside the window, but paused - the scheduler skips it entirely.
        var pausedInside = NewPlan(fixture);
        pausedInside.Pause();

        context.RecurringBookingPlans.AddRange(dueInside, dueOutside, pausedInside);
        await context.SaveChangesAsync();

        var report = (await CreateService(context).GetReportAsync(Horizon(days: 14))).Value;

        report.PlansDueInHorizon.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_reports_upcoming_recurring_work_per_day_and_excludes_one_off_and_called_off_bookings()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        var plan = NewPlan(fixture);
        context.RecurringBookingPlans.Add(plan);
        await context.SaveChangesAsync();

        var firstOfTheDay = NewBooking(fixture, plan.Id, Today.AddDays(1));
        var secondOfTheDay = NewBooking(fixture, plan.Id, Today.AddDays(1));
        var nextWeek = NewBooking(fixture, plan.Id, Today.AddDays(8));

        // A one-off booking is not recurring work, however close it sits.
        var oneOff = NewBooking(fixture, planId: null, Today.AddDays(1));

        // Past the horizon.
        var beyondHorizon = NewBooking(fixture, plan.Id, Today.AddDays(40));

        // Generated by the plan, then cancelled - nobody is going to do it, so
        // staffing against it would be staffing against nothing.
        var cancelled = NewBooking(fixture, plan.Id, Today.AddDays(2));
        cancelled.TransitionTo(BookingStatus.CancelledByCustomer, "Customer cancelled.");

        context.Bookings.AddRange(firstOfTheDay, secondOfTheDay, nextWeek, oneOff, beyondHorizon, cancelled);
        await context.SaveChangesAsync();

        var report = (await CreateService(context).GetReportAsync(Horizon())).Value;

        report.UpcomingOccurrenceVolume.Should().Be(3);
        report.UpcomingVolumeByDate.Should().BeEquivalentTo(new[]
        {
            new RecurringPlanDailyVolumeRow(Today.AddDays(1), 2),
            new RecurringPlanDailyVolumeRow(Today.AddDays(8), 1),
        }, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// The falsifiable half of this suite. Every value assertion above passes
    /// just as happily against an implementation that calls
    /// <c>ToListAsync()</c> and groups in memory - which is precisely the
    /// implementation that falls over on a table with 50,000 standing plans.
    /// The emitted SQL is where the two differ, so that is what is asserted.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_aggregates_in_the_database()
    {
        var capture = new SqlCapturingInterceptor();
        using var context = _db.CreateContext(capture);
        Reset(context);
        var fixture = Seed(context);

        var plan = NewPlan(fixture);
        context.RecurringBookingPlans.Add(plan);
        await context.SaveChangesAsync();
        context.Bookings.Add(NewBooking(fixture, plan.Id, Today.AddDays(1)));
        await context.SaveChangesAsync();

        capture.Reset();
        await CreateService(context).GetReportAsync(Horizon());

        capture.Commands.Should().NotBeEmpty();

        // Grouping happens in SQL, not in C#.
        capture.Commands.Count(sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            .Should().Be(3, "the two plan breakdowns and the per-day booking volume are each one grouped query");

        // ...and the plan/booking rows themselves are never selected. A count
        // that first fetched the rows would have to name their columns.
        capture.Commands.Should().NotContain(
            sql => sql.Contains("\"next_occurrence_date\"", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase),
            "no query may materialize plan rows to count them");
    }

    [Fact]
    public async Task GetReportAsync_rejects_a_horizon_that_ends_before_it_starts()
    {
        using var context = _db.CreateContext();
        Reset(context);

        var result = await CreateService(context)
            .GetReportAsync(new AdminRecurringPlanReportRequest(Today, Today.AddDays(-1)));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Reports.InvalidDateRange");
    }

    [Fact]
    public async Task GetReportAsync_defaults_to_a_four_week_horizon_from_today()
    {
        using var context = _db.CreateContext();
        Reset(context);

        var report = (await CreateService(context)
            .GetReportAsync(new AdminRecurringPlanReportRequest(null, null))).Value;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        report.HorizonFromDate.Should().Be(today);
        report.HorizonToDate.Should().Be(today.AddDays(IRecurringBookingPlanAdminService.DefaultHorizonDays));
    }

    [Fact]
    public async Task SearchAsync_lists_every_customers_plan_with_the_current_customer_and_service_names()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var first = Seed(context, "Priya Nair", "Deep Clean");
        var second = Seed(context, "Arjun Rao", "Sofa Shampoo");

        context.RecurringBookingPlans.AddRange(NewPlan(first), NewPlan(second));
        await context.SaveChangesAsync();

        var response = (await CreateService(context)
            .SearchAsync(new AdminRecurringPlanSearchRequest(null, null, null, null))).Value;

        response.TotalCount.Should().Be(2);
        response.Items.Select(i => i.CustomerName).Should().BeEquivalentTo("Priya Nair", "Arjun Rao");
        response.Items.Select(i => i.ServiceName).Should().BeEquivalentTo("Deep Clean", "Sofa Shampoo");
    }

    [Fact]
    public async Task SearchAsync_filters_by_status_frequency_customer_and_service()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var mine = Seed(context, "Priya Nair");
        var theirs = Seed(context, "Arjun Rao");

        var wanted = NewPlan(mine, RecurringBookingRecurrenceFrequency.Biweekly);
        var wrongFrequency = NewPlan(mine, RecurringBookingRecurrenceFrequency.Weekly);
        var wrongCustomer = NewPlan(theirs, RecurringBookingRecurrenceFrequency.Biweekly);
        var wrongStatus = NewPlan(mine, RecurringBookingRecurrenceFrequency.Biweekly);
        wrongStatus.Cancel();

        context.RecurringBookingPlans.AddRange(wanted, wrongFrequency, wrongCustomer, wrongStatus);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        (await service.SearchAsync(new AdminRecurringPlanSearchRequest(
            RecurringBookingPlanStatus.Active, RecurringBookingRecurrenceFrequency.Biweekly, mine.Customer.Id, mine.Service.Id)))
            .Value.Items.Select(i => i.Id).Should().Equal(wanted.Id);

        (await service.SearchAsync(new AdminRecurringPlanSearchRequest(
            RecurringBookingPlanStatus.Cancelled, null, null, null)))
            .Value.Items.Select(i => i.Id).Should().Equal(wrongStatus.Id);
    }

    [Fact]
    public async Task SearchAsync_pages_in_the_database_newest_first()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);

        var plans = Enumerable.Range(0, 5).Select(_ => NewPlan(fixture)).ToList();
        context.RecurringBookingPlans.AddRange(plans);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var firstPage = (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, null, null, Page: 1, PageSize: 2))).Value;
        firstPage.TotalCount.Should().Be(5, "the total counts every match, not just this page");
        firstPage.Items.Should().HaveCount(2);

        var secondPage = (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, null, null, Page: 2, PageSize: 2))).Value;
        secondPage.Items.Should().HaveCount(2);

        // Stable boundaries: no plan may appear on both pages.
        firstPage.Items.Select(i => i.Id).Should().NotIntersectWith(secondPage.Items.Select(i => i.Id));

        var lastPage = (await service.SearchAsync(new AdminRecurringPlanSearchRequest(null, null, null, null, Page: 3, PageSize: 2))).Value;
        lastPage.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchAsync_clamps_an_absurd_page_size_rather_than_honouring_it()
    {
        using var context = _db.CreateContext();
        Reset(context);

        var response = (await CreateService(context)
            .SearchAsync(new AdminRecurringPlanSearchRequest(null, null, null, null, Page: 1, PageSize: 100_000))).Value;

        response.PageSize.Should().Be(PagedQueryExtensions.MaxPageSize);
    }

    [Fact]
    public async Task CancelAsync_cancels_an_active_plan_and_does_not_touch_its_bookings()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);
        var plan = NewPlan(fixture);
        context.RecurringBookingPlans.Add(plan);
        var booking = NewBooking(fixture, plan.Id, plan.NextOccurrenceDate);
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        var result = await CreateService(context).CancelAsync(plan.Id, Guid.NewGuid(), new AdminCancelRecurringPlanRequest("Customer moved out of the service area."));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "an active plan should cancel");
        result.Value.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
        result.Value.CustomerName.Should().Be(fixture.Customer.Name);
        result.Value.ServiceName.Should().Be(fixture.Service.Name);

        await using var verifyContext = _db.CreateContext();
        var persistedPlan = await verifyContext.RecurringBookingPlans.FindAsync(plan.Id);
        persistedPlan!.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);

        // Cancelling the plan is only "no more future occurrences" - it must
        // never reach into bookings the plan already produced.
        var persistedBooking = await verifyContext.Bookings.FindAsync(booking.Id);
        persistedBooking!.Status.Should().Be(BookingStatus.Initiated);
    }

    [Fact]
    public async Task CancelAsync_an_already_cancelled_plan_returns_a_business_error()
    {
        using var context = _db.CreateContext();
        Reset(context);
        var fixture = Seed(context);
        var plan = NewPlan(fixture);
        plan.Cancel();
        context.RecurringBookingPlans.Add(plan);
        await context.SaveChangesAsync();

        var result = await CreateService(context).CancelAsync(plan.Id, Guid.NewGuid(), new AdminCancelRecurringPlanRequest("Any reason"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringBookingPlan.InvalidCancel");
    }

    [Fact]
    public async Task CancelAsync_unknown_plan_returns_not_found()
    {
        using var context = _db.CreateContext();
        Reset(context);

        var result = await CreateService(context).CancelAsync(Guid.NewGuid(), Guid.NewGuid(), new AdminCancelRecurringPlanRequest("Any reason"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringBookingPlan.NotFound");
    }

    /// <summary>
    /// Records the SQL text of every command a unit of work issues. Distinct
    /// from <see cref="CountingCommandInterceptor"/>, which records only how
    /// many: an aggregate pushed down to the database and one computed in
    /// memory can both be a single command, so the count cannot tell them
    /// apart - only the text can.
    /// </summary>
    private sealed class SqlCapturingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_commands)
                {
                    return _commands.ToList();
                }
            }
        }

        public void Reset()
        {
            lock (_commands)
            {
                _commands.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(string sql)
        {
            lock (_commands)
            {
                _commands.Add(sql);
            }
        }
    }
}
