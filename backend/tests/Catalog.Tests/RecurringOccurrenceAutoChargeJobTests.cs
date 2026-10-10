using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Recurring-booking payment-timing fix: covers
/// <see cref="RecurringOccurrenceAutoChargeJob"/> - a recurring occurrence
/// whose plan opted in to auto-charge gets charged off-session through the
/// same sandbox gateway seam <c>SubscriptionBillingJob</c> already uses, a
/// declined attempt is retried with backoff, and once the retry budget is
/// exhausted the customer falls back to the manual "payment due" notification
/// rather than being silently left with an ever-expiring booking.
/// </summary>
public sealed class RecurringOccurrenceAutoChargeJobTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RecurringOccurrenceAutoChargeJobTests(TestDatabase db) => _db = db;

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixedNow;
        public FakeTimeProvider(DateTime now) => _fixedNow = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _fixedNow;
    }

    private sealed record Fixture(Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window);

    private static Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
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

    /// <summary>An Active provider matching the fixture's category/city/hours, so PaymentService.CreateOrderAsync's eligible-provider gate lets the charge attempt through.</summary>
    private static void AddEligibleProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture fixture, DayOfWeek dayOfWeek)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Cleaning", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        provider.UpdateLocation(12.9716m, 77.5946m);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, fixture.Service.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, fixture.City.Id));
        context.Add(new ProviderAvailabilityWindow(Guid.NewGuid(), provider.Id, dayOfWeek, TimeSpan.FromHours(8), TimeSpan.FromHours(18)));
        context.SaveChanges();
    }

    private static async Task<Guid> SeedAutoChargePlanAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture fixture, bool openEnded = false)
    {
        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), fixture.Customer.Id, fixture.Service.Id, fixture.City.Id, fixture.Locality.Id,
            fixture.Address.Id, fixture.Window.Id, quantity: 1, RecurringBookingRecurrenceFrequency.Weekly,
            DayOfWeek.Monday, recurrenceDayOfMonth: null,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)), endDate: null, occurrenceCount: openEnded ? null : 52,
            addOns: null, applyWalletCredit: false, autoChargeEnabled: true);
        await new RecurringBookingPlanRepository(context).AddAsync(plan);
        return plan.Id;
    }

    /// <summary>Creates a PaymentPending occurrence booking linked to the plan, with <paramref name="totalPayable"/> controlling SandboxPaymentGateway's deterministic outcome (a paisa value of 13 always declines).</summary>
    private static async Task<Booking> SeedOccurrenceAsync(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, Fixture fixture, Guid planId, decimal totalPayable, DateTime createdAtUtc)
    {
        var address = new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210");
        var slot = new SlotSnapshot(fixture.Window.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, totalPayable);
        var booking = new Booking(
            Guid.NewGuid(), fixture.Customer.Id, new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile),
            fixture.Address.Id, address, slot, price, recurringBookingPlanId: planId);
        booking.AddItem(Guid.NewGuid(), fixture.Service.Id, fixture.Service.Name, fixture.Service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);

        await new BookingRepository(context).AddAsync(booking);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE booking SET created_at_utc = {createdAtUtc} WHERE id = {booking.Id}");

        return booking;
    }

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static RecurringOccurrenceAutoChargeJob BuildJob(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, SandboxPaymentGateway gateway, RecurringBookingOptions options, TimeProvider timeProvider)
    {
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var simulator = (ISandboxPaymentSimulator)gateway;
        var webhookService = new PaymentWebhookService(paymentRepository,
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            bookingRepository,
            new ServiceRepository(context),
            gateway,
            new CommissionService(Options.Create(new CommissionOptions())),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context,
            new NoOpMetricsService(),
            NullLogger<PaymentWebhookService>.Instance);
        var paymentService = new PaymentService(paymentRepository,
            bookingRepository,
            gateway,
            simulator,
            webhookService,
            BuildEligibleProviderSearchService(context),
            new PaymentGroupRepository(context),
            new RecurringBookingPlanRepository(context),
            new RecurringBookingOccurrenceRepository(context),
            null!);

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

        return new RecurringOccurrenceAutoChargeJob(
            bookingRepository,
            new RecurringBookingPlanRepository(context),
            paymentService,
            new CustomerRepository(context),
            new ServiceRepository(context),
            new SlotWindowRepository(context),
            new DeviceTokenRepository(context),
            notificationDispatchService,
            Options.Create(options),
            timeProvider,
            NullLogger<RecurringOccurrenceAutoChargeJob>.Instance);
    }

    private static EligibleProviderSearchService BuildEligibleProviderSearchService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(
            new ProviderMatchingService(
                new BookingRepository(context),
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions())),
            BuildEligibilityService(context));

    private static ProviderAssignmentEligibilityService BuildEligibilityService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        TravelFeasibilityFactory.Sandbox(context),
        context);

    /// <summary>Hand-rolled fake matching this test project's no-mocking-library convention.</summary>
    private sealed class NoOpNotificationProvider : INotificationProvider
    {
        public Task<BuildingBlocks.Results.Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());

        public Task<BuildingBlocks.Results.Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());
    }

    [Fact]
    public async Task ProcessDueAttemptsAsync_confirms_the_booking_once_the_initial_delay_has_passed()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            // .00 paisa - SandboxPaymentGateway.DetermineOutcome succeeds.
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now.AddHours(-3));
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var gateway = BuildGateway();
            // 3 hours after creation, past the 2-hour initial delay.
            var job = BuildJob(context, gateway, options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.Confirmed, "the sandbox gateway deterministically succeeds for an amount not ending in .13");
            reloaded.AutoChargeAttemptCount.Should().Be(1);

            var transaction = await new PaymentTransactionRepository(context).GetByBookingIdAsync(occurrence.Id);
            transaction.Should().NotBeNull();
            transaction!.Status.Should().Be(PaymentTransactionStatus.Success);
        }
    }

    [Fact]
    public async Task ProcessDueAttemptsAsync_leaves_a_freshly_created_occurrence_untouched_before_the_initial_delay()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now);
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var gateway = BuildGateway();
            var job = BuildJob(context, gateway, options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentPending, "created moments ago - the initial delay has not elapsed yet");
            reloaded.AutoChargeAttemptCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task ProcessDueAttemptsAsync_falls_back_to_the_manual_payment_due_notification_once_retries_are_exhausted()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            // .13 paisa - SandboxPaymentGateway.DetermineOutcome deterministically declines every attempt.
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.13m, createdAtUtc: now.AddHours(-3));
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 2 };

        // First attempt: due (3h > 2h initial delay), declines.
        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentFailed, "a declined gateway attempt moves the booking to PaymentFailed, not back to PaymentPending - PaymentService.CreateOrderAsync retries from there itself");
            reloaded.AutoChargeAttemptCount.Should().Be(1);
        }

        // Second attempt, once the retry backoff has elapsed: retry limit (2) reached -> exhausted, falls back.
        // First attempt's LastAutoChargeAttemptAtUtc was stamped at `now`; the 4-hour backoff clears at now+4h.
        // ListRecurringPaymentPendingAsync must also pick up PaymentFailed, or this second attempt would never be found.
        var afterBackoff = now.AddHours(6);
        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(afterBackoff));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentFailed, "still unpaid - BookingExpirySweepJob's longer recurring window is what eventually reaps this, not this job");
            reloaded.AutoChargeAttemptCount.Should().Be(2);

            var notifications = await new NotificationEventRepository(context).ListByCustomerAsync(fixture.Customer.Id);
            notifications.Should().Contain(n => n.EventType == NotificationEventType.RecurringBookingPaymentDue);
        }

        // Third tick: exhausted, so no further attempt and no repeat notification.
        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(afterBackoff.AddDays(1)));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.AutoChargeAttemptCount.Should().Be(2, "the retry budget is exhausted - no further attempts");
        }
    }

    [Theory]
    [InlineData(true, RecurringBookingPlanStatus.Paused)]
    [InlineData(false, RecurringBookingPlanStatus.Active)]
    public async Task ProcessDueAttemptsAsync_pauses_only_an_open_ended_plan_once_retries_are_exhausted(bool openEnded, RecurringBookingPlanStatus expectedStatus)
    {
        Fixture fixture;
        Guid planId;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture, openEnded);
            // .13 paisa - SandboxPaymentGateway.DetermineOutcome deterministically declines every attempt.
            await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.13m, createdAtUtc: now.AddHours(-3));
        }

        // Retry limit 1: the very first declined attempt exhausts the budget.
        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 1 };
        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var plan = await new RecurringBookingPlanRepository(context).GetByIdAsync(planId);
            plan!.Status.Should().Be(expectedStatus);
        }
    }

    [Fact]
    public async Task ForceAttemptAsync_charges_a_freshly_created_occurrence_before_its_initial_delay_has_passed()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            // Created moments ago - ProcessDueAttemptsAsync would leave this untouched (see the sibling test above).
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now);
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            var result = await job.ForceAttemptAsync(occurrence.Id);
            result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "an admin-forced attempt must bypass the initial-delay gate");
            result.Value.Should().Be(AutoChargeAttemptOutcome.Succeeded);
        }

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(occurrence.Id);
        reloaded!.Status.Should().Be(BookingStatus.Confirmed);
        reloaded.AutoChargeAttemptCount.Should().Be(1, "a forced attempt is still a real attempt for the retry-limit calculation");
    }

    [Fact]
    public async Task ForceAttemptAsync_bypasses_a_plan_with_auto_charge_turned_off_and_a_booking_with_retries_cancelled()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now.AddHours(-3));

            var planRepository = new RecurringBookingPlanRepository(context);
            var plan = await planRepository.GetByIdAsync(planId);
            plan!.SetAutoCharge(false);
            await planRepository.UpdateAsync(plan);

            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(occurrence.Id);
            booking!.CancelAutoChargeRetries();
            await bookingRepository.UpdateAsync(booking);
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            var result = await job.ForceAttemptAsync(occurrence.Id);
            result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "an explicit admin force overrides both the plan toggle and a prior cancellation");
            result.Value.Should().Be(AutoChargeAttemptOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task ForceAttemptAsync_refuses_a_booking_that_is_not_a_recurring_occurrence()
    {
        Fixture fixture;
        using var context = _db.CreateContext();
        fixture = Seed(context);
        var address = new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210");
        var slot = new SlotSnapshot(fixture.Window.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659.00m);
        var oneOff = new Booking(Guid.NewGuid(), fixture.Customer.Id, new CustomerSnapshot(fixture.Customer.Name, fixture.Customer.Mobile), fixture.Address.Id, address, slot, price);
        oneOff.AddItem(Guid.NewGuid(), fixture.Service.Id, fixture.Service.Name, fixture.Service.Slug, 500m, 1);
        oneOff.TransitionTo(BookingStatus.PaymentPending);
        await new BookingRepository(context).AddAsync(oneOff);

        var job = BuildJob(context, BuildGateway(), new RecurringBookingOptions(), TimeProvider.System);
        var result = await job.ForceAttemptAsync(oneOff.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringAutoCharge.NotARecurringOccurrence");
    }

    [Fact]
    public async Task ForceAttemptAsync_refuses_a_booking_that_is_already_Confirmed()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: DateTime.UtcNow);

            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(occurrence.Id);
            booking!.TransitionTo(BookingStatus.Confirmed);
            await bookingRepository.UpdateAsync(booking);
        }

        using var readContext = _db.CreateContext();
        var job = BuildJob(readContext, BuildGateway(), new RecurringBookingOptions(), TimeProvider.System);
        var result = await job.ForceAttemptAsync(occurrence.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RecurringAutoCharge.NothingToCharge");
    }

    [Fact]
    public void NextAttemptDueAtUtc_is_null_once_cancelled_by_admin_or_the_retry_limit_is_reached()
    {
        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 2 };
        var neverAttempted = new Booking(
            Guid.NewGuid(), Guid.NewGuid(), new CustomerSnapshot("Priya Nair", "9876543210"), null,
            new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659.00m),
            recurringBookingPlanId: Guid.NewGuid());

        RecurringOccurrenceAutoChargeJob.NextAttemptDueAtUtc(neverAttempted, options).Should().NotBeNull("a fresh occurrence still has attempts available");

        neverAttempted.CancelAutoChargeRetries();
        RecurringOccurrenceAutoChargeJob.NextAttemptDueAtUtc(neverAttempted, options).Should().BeNull("cancelled - there is no next attempt to wait for");

        var atLimit = new Booking(
            Guid.NewGuid(), Guid.NewGuid(), new CustomerSnapshot("Priya Nair", "9876543210"), null,
            new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659.00m),
            recurringBookingPlanId: Guid.NewGuid());
        atLimit.RecordAutoChargeAttempt(DateTime.UtcNow);
        atLimit.RecordAutoChargeAttempt(DateTime.UtcNow);
        RecurringOccurrenceAutoChargeJob.NextAttemptDueAtUtc(atLimit, options).Should().BeNull("AttemptCount (2) already reached RetryLimit (2)");
    }

    [Fact]
    public async Task ProcessDueAttemptsAsync_skips_a_booking_whose_plan_has_since_turned_auto_charge_off()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now.AddHours(-3));
        }

        using (var context = _db.CreateContext())
        {
            var planRepository = new RecurringBookingPlanRepository(context);
            var plan = await planRepository.GetByIdAsync(planId);
            plan!.SetAutoCharge(false);
            await planRepository.UpdateAsync(plan);
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(occurrence.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentPending);
            reloaded.AutoChargeAttemptCount.Should().Be(0, "the customer turned auto-charge off after this occurrence was created - the job must not charge them anyway");
        }
    }

    [Fact]
    public async Task ProcessDueAttemptsAsync_skips_a_booking_an_admin_already_cancelled_retries_on()
    {
        Fixture fixture;
        Guid planId;
        Booking occurrence;
        var now = DateTime.UtcNow;

        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            AddEligibleProvider(context, fixture, now.AddDays(3).DayOfWeek);
            planId = await SeedAutoChargePlanAsync(context, fixture);
            occurrence = await SeedOccurrenceAsync(context, fixture, planId, totalPayable: 659.00m, createdAtUtc: now.AddHours(-3));

            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(occurrence.Id);
            booking!.CancelAutoChargeRetries();
            await bookingRepository.UpdateAsync(booking);
        }

        var options = new RecurringBookingOptions { AutoChargeInitialDelayHours = 2, AutoChargeRetryBackoffHours = 4, AutoChargeRetryLimit = 3 };

        using (var context = _db.CreateContext())
        {
            var job = BuildJob(context, BuildGateway(), options, new FakeTimeProvider(now));
            await job.ProcessDueAttemptsAsync();
        }

        using var readContext = _db.CreateContext();
        var reloaded = await new BookingRepository(readContext).GetByIdAsync(occurrence.Id);
        reloaded!.Status.Should().Be(BookingStatus.PaymentPending);
        reloaded.AutoChargeAttemptCount.Should().Be(0, "an admin cancelled retries on this booking - the automatic sweep must never attempt it");
    }
}
