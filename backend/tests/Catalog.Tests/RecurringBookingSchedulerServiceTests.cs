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
        address.LinkToGeography(pincode.Id, locality.Id);
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
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context), new InMemoryCacheService()),
            new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)),
            new WalletService(new WalletLedgerRepository(context), context),
        new ServiceabilityRepository(context),
        TestServices.BookingOptions());

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
                TestServices.Clock()),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);

        var notificationDispatchService = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
            new NoOpNotificationProvider(),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            new NotificationEventRepository(context),
            new DeviceTokenRepository(context),
            new CustomerRepository(context),
            new ProviderRepository(context),
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
            new RecurringPlanProviderContinuityService(new BookingRepository(context)),
            BuildEligibilityService(context),
            new EligibleProviderSearchService(
                new ProviderMatchingService(
                    new BookingRepository(context),
                    context,
                    new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                    Options.Create(new AutoAssignmentOptions())),
                BuildEligibilityService(context)),
            Options.Create(new RecurringBookingOptions { LeadTimeDays = leadTimeDays }),
            NullLogger<RecurringBookingSchedulerService>.Instance);
    }

    /// <summary>The real task 245 gate, sandbox-routed - task 297's placement decision is only meaningful against the same eligibility rules the auto-assignment engine applies.</summary>
    private static ProviderAssignmentEligibilityService BuildEligibilityService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        TravelFeasibilityFactory.Sandbox(context),
        context);

    /// <summary>
    /// Task 297: the auto-assignment engine, built over the same context, so
    /// the continuity preference can be exercised against a booking this
    /// suite's own scheduler actually generated rather than a hand-built one.
    /// </summary>
    private static ProviderAutoAssignmentHandler BuildAutoAssignmentHandler(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new EligibleProviderSearchService(BuildMatchingService(context), BuildEligibilityService(context)),
        BuildEligibilityService(context),
        new BookingProviderAssignmentService(
            new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
            new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
            Options.Create(new AutoAssignmentOptions()), context),
        new BookingProviderAssignmentRepository(context),
        new BookingRepository(context),
        new RecurringPlanProviderContinuityService(new BookingRepository(context)),
        Options.Create(new AutoAssignmentOptions { RetryAttempts = 3, Enabled = true }),
        NullLogger<ProviderAutoAssignmentHandler>.Instance);

    private static ProviderMatchingService BuildMatchingService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingRepository(context),
        context,
        new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
        Options.Create(new AutoAssignmentOptions()));

    /// <summary>
    /// An Active provider who matches this fixture's category and city and
    /// whose weekly hours cover the occurrence slot - i.e. one the task 244
    /// matcher finds and the task 245 gate lets through, unless a blackout
    /// below takes them out.
    /// </summary>
    private static Provider AddEligibleProvider(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture fixture, DateOnly occurrenceDate, decimal lat, decimal lng)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Cleaning", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        provider.UpdateLocation(lat, lng);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, fixture.Service.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, fixture.City.Id));
        context.Add(new ProviderAvailabilityWindow(
            Guid.NewGuid(), provider.Id, occurrenceDate.DayOfWeek, TimeSpan.FromHours(8), TimeSpan.FromHours(18)));
        return provider;
    }

    /// <summary>
    /// The plan's history: a completed visit from a previous occurrence,
    /// served by <paramref name="providerId"/>. This is what makes them the
    /// plan's standing provider - nothing is stored on the plan itself
    /// (see <see cref="Nestly.Application.RecurringBookings.IRecurringPlanProviderContinuityService"/>).
    /// </summary>
    private static void AddPriorOccurrenceServedBy(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture fixture, RecurringBookingPlan plan, Guid providerId, DateOnly slotDate)
    {
        var booking = new Booking(
            Guid.NewGuid(), fixture.Customer.Id,
            new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
            fixture.Address.Id,
            new AddressSnapshot(
                "Home", "12 MG Road", null, null, fixture.Address.Pincode, "Bengaluru", "Karnataka",
                12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(fixture.Window.Id, slotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m),
            recurringBookingPlanId: plan.Id);
        booking.AddItem(Guid.NewGuid(), fixture.Service.Id, fixture.Service.Name, fixture.Service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.AssignProvider(providerId);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);
        context.Bookings.Add(booking);
        context.SaveChanges();
    }

    private static RecurringBookingPlan NewWeeklyPlan(Fixture fixture, DateOnly startDate) => new(
        Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
        fixture.Address.Id, fixture.Window.Id, quantity: 1, RecurringBookingRecurrenceFrequency.Weekly,
        startDate.DayOfWeek, recurrenceDayOfMonth: null, startDate: startDate, endDate: null, occurrenceCount: 4);

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

        // Task 297: a genuinely unplaceable date is still the one case that
        // remains a skip - and it is a recorded, notified skip with no
        // half-created booking left behind, not a silent drop.
        var bookings = await new BookingRepository(assertContext).ListByRecurringPlanAsync(plan.Id);
        bookings.Should().BeEmpty("the orchestration refused, so no booking exists for this date at all");
    }

    /// <summary>
    /// Task 297. Task 296 added <c>Booking.RecurringBookingPlanId</c> and the
    /// row promises "each occurrence is an ordinary Booking row with a FK back
    /// to the plan" - but nothing set it, so every generated booking looked
    /// like a one-off to tasks 299/300. It is passed through
    /// <c>IBookingService.CreateAsync</c>, so it is part of the booking's own
    /// insert rather than a follow-up write.
    /// </summary>
    [Fact]
    public async Task ProcessDueOccurrencesAsync_stamps_the_generated_booking_with_its_recurring_plan_id()
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
            plan = NewWeeklyPlan(fixture, occurrenceDate);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);
        }

        using (var runContext = _db.CreateContext())
        {
            await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var history = await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id);
        var generated = await new BookingRepository(assertContext).GetByIdAsync(history.Single().BookingId!.Value);

        generated!.RecurringBookingPlanId.Should().Be(
            plan.Id, "a generated occurrence must be traceable to the plan that produced it without joining through the occurrence log");

        var byPlan = await new BookingRepository(assertContext).ListByRecurringPlanAsync(plan.Id);
        byPlan.Should().ContainSingle(b => b.Id == generated.Id);
    }

    /// <summary>
    /// Task 297's headline requirement: "handles provider-unavailable-on-date
    /// by falling back to the existing provider reassignment flow instead of
    /// silently skipping". The plan's standing professional is blacked out on
    /// the date; a substitute exists; the occurrence must be BOOKED and marked
    /// for reassignment, and the customer's occurrence budget must be consumed
    /// (it is a delivered visit, not a supply-side miss).
    /// </summary>
    [Fact]
    public async Task ProcessDueOccurrencesAsync_reassigns_rather_than_skips_when_the_standing_provider_is_unavailable()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
        }

        var occurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        RecurringBookingPlan plan;
        Provider standing, substitute;
        using (var planContext = _db.CreateContext())
        {
            standing = AddEligibleProvider(planContext, fixture, occurrenceDate, 12.9716m, 77.5946m);
            substitute = AddEligibleProvider(planContext, fixture, occurrenceDate, 12.9800m, 77.6000m);
            // The regular professional is on leave for exactly this date.
            planContext.Add(new ProviderBlackoutDate(Guid.NewGuid(), standing.Id, occurrenceDate, occurrenceDate, "Annual leave"));
            planContext.SaveChanges();

            plan = NewWeeklyPlan(fixture, occurrenceDate);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);
            AddPriorOccurrenceServedBy(planContext, fixture, plan, standing.Id, occurrenceDate.AddDays(-7));
        }

        using (var runContext = _db.CreateContext())
        {
            await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var occurrence = (await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id))
            .Single(o => o.ScheduledDate == occurrenceDate);

        occurrence.Outcome.Should().Be(
            RecurringBookingOccurrenceOutcome.BookedProviderReassigned,
            "an unavailable regular is a reassignment, never a reason to drop the customer's visit");
        occurrence.BookingId.Should().NotBeNull();
        occurrence.SkipReason.Should().NotBeNullOrWhiteSpace("the reassignment must leave a recorded reason, not be silent");

        var reloadedPlan = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloadedPlan!.CompletedOccurrenceCount.Should().Be(1, "the visit was delivered, so it counts against the plan's budget");

        // And the reassignment is real, not just a label: the auto-assignment
        // engine puts the substitute - not the blacked-out regular - on the job.
        using (var assignContext = _db.CreateContext())
        {
            var repository = new BookingRepository(assignContext);
            var booking = await repository.GetByIdAsync(occurrence.BookingId!.Value);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await repository.UpdateAsync(booking);

            await BuildAutoAssignmentHandler(assignContext).TryAssignAsync(booking.Id, null, CancellationToken.None);
        }

        using var finalContext = _db.CreateContext();
        var assigned = await new BookingRepository(finalContext).GetByIdAsync(occurrence.BookingId!.Value);
        assigned!.AssignedProviderId.Should().Be(substitute.Id);
        assigned.AssignedProviderId.Should().NotBe(standing.Id);
    }

    /// <summary>
    /// Task 297: "only genuinely-unplaceable occurrences should remain a skip,
    /// and that skip must be visible". Nobody at all can serve the date - the
    /// standing professional is on leave and there is no one else - so the
    /// occurrence is recorded with its reason rather than vanishing, and the
    /// booking is still created for the manual admin queue rather than being
    /// thrown away over a forecast made days ahead of the visit.
    /// </summary>
    [Fact]
    public async Task ProcessDueOccurrencesAsync_records_a_reason_when_no_provider_at_all_is_eligible()
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
            var standing = AddEligibleProvider(planContext, fixture, occurrenceDate, 12.9716m, 77.5946m);
            planContext.Add(new ProviderBlackoutDate(Guid.NewGuid(), standing.Id, occurrenceDate, occurrenceDate, "Annual leave"));
            planContext.SaveChanges();

            plan = NewWeeklyPlan(fixture, occurrenceDate);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);
            AddPriorOccurrenceServedBy(planContext, fixture, plan, standing.Id, occurrenceDate.AddDays(-7));
        }

        using (var runContext = _db.CreateContext())
        {
            await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var occurrence = (await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id))
            .Single(o => o.ScheduledDate == occurrenceDate);

        occurrence.Outcome.Should().Be(RecurringBookingOccurrenceOutcome.BookedProviderUnavailable);
        occurrence.SkipReason.Should().NotBeNullOrWhiteSpace("an unstaffable date must leave evidence behind");
        occurrence.BookingId.Should().NotBeNull("the customer's slot is held; only the staffing is unresolved");

        var booking = await new BookingRepository(assertContext).GetByIdAsync(occurrence.BookingId!.Value);
        booking!.AssignedProviderId.Should().BeNull();
        booking.RecurringBookingPlanId.Should().Be(plan.Id);
    }

    /// <summary>
    /// The other side of the same rule: when the plan's standing professional
    /// IS free, nothing is flagged and nothing is reassigned - the occurrence
    /// is an ordinary <see cref="RecurringBookingOccurrenceOutcome.Booked"/>,
    /// and the auto-assignment engine gives the job back to them even though a
    /// nearer candidate exists. Continuity is the point of a standing plan.
    /// </summary>
    [Fact]
    public async Task ProcessDueOccurrencesAsync_keeps_the_standing_provider_when_they_are_available()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = Seed(seedContext);
        }

        var occurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        RecurringBookingPlan plan;
        Provider standing, nearer;
        using (var planContext = _db.CreateContext())
        {
            // Deliberately the FURTHER of the two, so a pass that ignored
            // continuity would rank the other one first and fail below.
            standing = AddEligibleProvider(planContext, fixture, occurrenceDate, 13.0827m, 80.2707m);
            nearer = AddEligibleProvider(planContext, fixture, occurrenceDate, 12.9717m, 77.5947m);
            planContext.SaveChanges();

            plan = NewWeeklyPlan(fixture, occurrenceDate);
            await new RecurringBookingPlanRepository(planContext).AddAsync(plan);
            AddPriorOccurrenceServedBy(planContext, fixture, plan, standing.Id, occurrenceDate.AddDays(-7));
        }

        using (var runContext = _db.CreateContext())
        {
            await BuildScheduler(runContext).ProcessDueOccurrencesAsync(CancellationToken.None);
        }

        Guid bookingId;
        using (var assertContext = _db.CreateContext())
        {
            var occurrence = (await new RecurringBookingOccurrenceRepository(assertContext).ListByPlanAsync(plan.Id))
                .Single(o => o.ScheduledDate == occurrenceDate);
            occurrence.Outcome.Should().Be(RecurringBookingOccurrenceOutcome.Booked);
            occurrence.SkipReason.Should().BeNull();
            bookingId = occurrence.BookingId!.Value;
        }

        using (var assignContext = _db.CreateContext())
        {
            var repository = new BookingRepository(assignContext);
            var booking = await repository.GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.AwaitingFulfilment);
            await repository.UpdateAsync(booking);

            await BuildAutoAssignmentHandler(assignContext).TryAssignAsync(bookingId, null, CancellationToken.None);
        }

        using var finalContext = _db.CreateContext();
        var assigned = await new BookingRepository(finalContext).GetByIdAsync(bookingId);
        assigned!.AssignedProviderId.Should().Be(
            standing.Id, "a recurring customer gets the professional they already know back, even when someone else is nearer");
        assigned.AssignedProviderId.Should().NotBe(nearer.Id);
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
            // The booking has to be a REAL row since task 296 made
            // recurring_booking_occurrence.booking_id a foreign key; an
            // invented Guid no longer stands in for one.
            var priorBooking = new Booking(
                Guid.NewGuid(), fixture.Customer.Id,
                new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
                fixture.Address.Id,
                new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
                new SlotSnapshot(fixture.Window.Id, occurrenceDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
                new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m),
                recurringBookingPlanId: plan.Id);
            planContext.Bookings.Add(priorBooking);
            await planContext.SaveChangesAsync();

            await new RecurringBookingOccurrenceRepository(planContext).AddAsync(
                new RecurringBookingOccurrence(Guid.NewGuid(), plan.Id, occurrenceDate, RecurringBookingOccurrenceOutcome.Booked, priorBooking.Id, null));
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
