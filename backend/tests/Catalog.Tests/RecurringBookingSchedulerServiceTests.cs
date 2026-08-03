using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 185 (the Hangfire occurrence scheduler) end to end against a
/// real database: it must create a real booking through
/// <see cref="BookingService.CreateAsync"/> - never a second, parallel
/// booking-creation path - and it must handle a slot-conflicting occurrence
/// by recording it as skipped and notifying, without crashing the sweep or
/// charging the customer's occurrence budget for a supply-side miss (task
/// 188's "slot-conflict/failure alert" exists precisely for this case).
/// </summary>
public sealed class RecurringBookingSchedulerServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringBookingSchedulerServiceTests(TestDatabase db) => _db = db;

    private sealed record Fixture(
        Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window);

    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context, int? maxBookingsPerSlot = null)
    {
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        if (maxBookingsPerSlot is { } capacity)
        {
            window.SetCapacity(capacity);
        }

        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, dueDate.DayOfWeek);

        context.Add(customer);
        context.Add(address);
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

        return new Fixture(customer, address, city, locality, service, window);
    }

    private static RecurringBookingSchedulerService BuildScheduler(Nestly.Infrastructure.Persistence.NestlyDbContext context, int leadTimeDays = 5)
    {
        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context)),
            new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

        var bookingService = new BookingService(
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));

        var notificationDispatchService = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
            new NoOpNotificationProvider(),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            new NotificationEventRepository(context),
            new NoOpMetricsService(),
            NullLogger<NotificationDispatchService>.Instance);

        return new RecurringBookingSchedulerService(
            new RecurringBookingPlanRepository(context),
            new RecurringBookingOccurrenceRepository(context),
            bookingService,
            new CustomerRepository(context),
            new ServiceRepository(context),
            new SlotWindowRepository(context),
            new DeviceTokenRepository(context),
            notificationDispatchService,
            Options.Create(new RecurringBookingOptions { LeadTimeDays = leadTimeDays }),
            NullLogger<RecurringBookingSchedulerService>.Instance);
    }

    /// <summary>Hand-rolled fake matching this test project's no-mocking-library convention - see FakeNotificationTemplateRepository's doc comment.</summary>
    private sealed class NoOpNotificationProvider : INotificationProvider
    {
        public Task<BuildingBlocks.Results.Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());

        public Task<BuildingBlocks.Results.Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());
    }

    [Fact]
    public async Task ProcessDueOccurrencesAsync_books_a_real_booking_through_BookingService_and_advances_the_plan()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
        }

        RecurringBookingPlan plan;
        using (var planContext = _db.CreateContext())
        {
            plan = new RecurringBookingPlan(
                Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
                fixture.Address.Id, fixture.Window.Id, quantity: 1, RecurringBookingRecurrenceFrequency.Weekly,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).DayOfWeek, recurrenceDayOfMonth: null,
                startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), endDate: null, occurrenceCount: 4);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);
        }

        using var runContext = _db.CreateContext();
        await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);

        using var assertContext = _db.CreateContext();
        var reloaded = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloaded!.CompletedOccurrenceCount.Should().Be(1);
        reloaded.NextOccurrenceDate.Should().Be(plan.NextOccurrenceDate.AddDays(7));

        var history = await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id);
        history.Should().ContainSingle();
        history[0].Outcome.Should().Be(RecurringBookingOccurrenceOutcome.Booked);
        history[0].BookingId.Should().NotBeNull();

        var bookings = await new BookingRepository(assertContext).ListByCustomerAsync(fixture.Customer.Id, Enum.GetValues<BookingStatus>());
        bookings.Should().ContainSingle(b => b.Id == history[0].BookingId);
    }

    [Fact]
    public async Task ProcessDueOccurrencesAsync_records_a_skip_and_does_not_charge_the_occurrence_budget_when_the_slot_is_full()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            // Capacity of 1, pre-reserved below, so the scheduler's own
            // attempt hits Booking.SlotCapacityReached exactly like two
            // customers racing for the same last seat would.
            fixture = Seed(seedContext, maxBookingsPerSlot: 1);
        }

        var occurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        RecurringBookingPlan plan;
        using (var planContext = _db.CreateContext())
        {
            plan = new RecurringBookingPlan(
                Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
                fixture.Address.Id, fixture.Window.Id, quantity: 1, RecurringBookingRecurrenceFrequency.Weekly,
                occurrenceDate.DayOfWeek, recurrenceDayOfMonth: null,
                startDate: occurrenceDate, endDate: null, occurrenceCount: 4);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);

            // Exhaust the slot's only seat for that date before the sweep runs.
            var reserved = await new SlotCapacityRepository(planContext).TryReserveAsync(fixture.Window.Id, occurrenceDate, maxCapacity: 1);
            reserved.Should().BeTrue();
        }

        using var runContext = _db.CreateContext();
        // Must not throw - a slot conflict is a handled, notified outcome, not an unhandled exception.
        await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);

        using var assertContext = _db.CreateContext();
        var reloaded = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloaded!.CompletedOccurrenceCount.Should().Be(0, "a supply-side miss must not cost the customer one of their paid-for occurrences");
        reloaded.NextOccurrenceDate.Should().Be(occurrenceDate.AddDays(7), "the schedule still advances even though this occurrence was skipped");
        reloaded.Status.Should().Be(RecurringBookingPlanStatus.Active);

        var history = await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id);
        history.Should().ContainSingle();
        history[0].Outcome.Should().Be(RecurringBookingOccurrenceOutcome.SkippedSlotUnavailable);
        history[0].BookingId.Should().BeNull();
        history[0].SkipReason.Should().NotBeNullOrWhiteSpace();

        var notifications = await new NotificationEventRepository(assertContext).ListByCustomerAsync(fixture.Customer.Id);
        notifications.Should().Contain(n => n.EventType == NotificationEventType.RecurringBookingSkipped);
    }

    [Fact]
    public async Task ProcessDueOccurrencesAsync_is_idempotent_and_does_not_reprocess_an_already_recorded_date()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
        }

        var occurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        RecurringBookingPlan plan;
        using (var planContext = _db.CreateContext())
        {
            plan = new RecurringBookingPlan(
                Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
                fixture.Address.Id, fixture.Window.Id, quantity: 1, RecurringBookingRecurrenceFrequency.Weekly,
                occurrenceDate.DayOfWeek, recurrenceDayOfMonth: null,
                startDate: occurrenceDate, endDate: null, occurrenceCount: 4);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);

            // Simulate a prior run that already recorded this date (e.g. a
            // Hangfire retry after a crash post-save) - the second sweep
            // below must not create a second occurrence or booking for it.
            await new RecurringBookingOccurrenceRepository(planContext).AddAsync(
                new RecurringBookingOccurrence(Guid.NewGuid(), plan.Id, occurrenceDate, RecurringBookingOccurrenceOutcome.Booked, Guid.NewGuid(), null));
        }

        using var runContext = _db.CreateContext();
        await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);

        using var assertContext = _db.CreateContext();
        var history = await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id);
        history.Should().ContainSingle("the scheduler must skip re-processing a date that already has a recorded occurrence");

        var reloaded = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloaded!.NextOccurrenceDate.Should().Be(occurrenceDate, "the plan's pointer is only advanced by RecordOccurrenceBooked/Skipped, which the idempotency guard prevented from running again");
    }
}
