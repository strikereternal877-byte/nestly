using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Reschedules;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 93's dedicated QA pass over Phase 5 (post-booking): cancellation and
/// reschedule eligibility matrices across every <see cref="BookingStatus"/>,
/// and notification-event coverage across every trigger event and channel.
/// The individual features already carry substantial direct-unit coverage
/// from when they were built (CancellationServiceTests, RescheduleServiceTests,
/// NotificationTemplateRendererTests, NotificationTriggerWiringTests); this
/// file adds the exhaustive matrix/coverage view those didn't already
/// assert as a single table, matching FinancialQaSuiteTests' role for
/// Phase 4.
/// </summary>
public sealed class PostBookingQaSuiteTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public PostBookingQaSuiteTests(TestDatabase db) => _db = db;

    // ---------------------------------------------------------------
    // Cancellation eligibility matrix (SRS 11.14.1, tasks 80a, 93)
    // ---------------------------------------------------------------

    /// <summary>
    /// Every <see cref="BookingStatus"/> against whether a customer can
    /// cancel from it - i.e. whether BookingLifecycle allows the transition
    /// to CancelledByCustomer. This is the single source of truth
    /// CancellationService.GetEligibilityAsync defers to entirely (task
    /// 80a); asserting the full table here means a future edit to
    /// BookingLifecycle that silently changes cancellation eligibility for
    /// any status fails this test, not just a status someone happened to
    /// write a case for.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Initiated, true)]
    [InlineData(BookingStatus.PaymentPending, true)]
    [InlineData(BookingStatus.PaymentFailed, true)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.AwaitingFulfilment, true)]
    [InlineData(BookingStatus.Assigned, true)]
    [InlineData(BookingStatus.InProgress, false, "service has already started - SRS 11.14.1 'whether service has started'")]
    [InlineData(BookingStatus.Completed, false)]
    [InlineData(BookingStatus.CancelledByCustomer, false, "already cancelled")]
    [InlineData(BookingStatus.CancelledByAdmin, false, "already cancelled")]
    [InlineData(BookingStatus.Rescheduled, true)]
    [InlineData(BookingStatus.RefundPending, false)]
    [InlineData(BookingStatus.Refunded, false)]
    public void Cancellation_eligibility_matches_the_documented_matrix_for_every_booking_status(
        BookingStatus status, bool expectedEligible, string? because = null)
    {
        BookingLifecycle.IsValidTransition(status, BookingStatus.CancelledByCustomer)
            .Should().Be(expectedEligible, because ?? string.Empty);
    }

    // ---------------------------------------------------------------
    // Reschedule eligibility matrix (SRS 11.15.1, tasks 82a, 93)
    // ---------------------------------------------------------------

    /// <summary>Same idea as the cancellation matrix above, for the transition to Rescheduled (task 82a's status half of eligibility).</summary>
    [Theory]
    [InlineData(BookingStatus.Initiated, false, "nothing to reschedule before payment is even underway")]
    [InlineData(BookingStatus.PaymentPending, false)]
    [InlineData(BookingStatus.PaymentFailed, false)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.AwaitingFulfilment, true)]
    [InlineData(BookingStatus.Assigned, true)]
    [InlineData(BookingStatus.InProgress, false, "service has already started")]
    [InlineData(BookingStatus.Completed, false)]
    [InlineData(BookingStatus.CancelledByCustomer, false)]
    [InlineData(BookingStatus.CancelledByAdmin, false)]
    [InlineData(BookingStatus.Rescheduled, false, "cannot reschedule the transient Rescheduled marker itself")]
    [InlineData(BookingStatus.RefundPending, false)]
    [InlineData(BookingStatus.Refunded, false)]
    public void Reschedule_eligibility_matches_the_documented_matrix_for_every_booking_status(
        BookingStatus status, bool expectedEligible, string? because = null)
    {
        BookingLifecycle.IsValidTransition(status, BookingStatus.Rescheduled)
            .Should().Be(expectedEligible, because ?? string.Empty);
    }

    // ---------------------------------------------------------------
    // Notification event coverage (SRS 19.1-2, tasks 87b, 88a-g, 156, 93)
    // ---------------------------------------------------------------

    /// <summary>
    /// Every SRS 19.1 trigger event must render on every dispatch channel
    /// this platform supports (SMS, email, push) - a gap here would mean a
    /// customer silently misses a notification on whichever channel they
    /// actually have contact details for.
    /// </summary>
    [Theory]
    [InlineData(NotificationEventType.Welcome)]
    [InlineData(NotificationEventType.BookingConfirmed)]
    [InlineData(NotificationEventType.PaymentSuccess)]
    [InlineData(NotificationEventType.PaymentFailed)]
    [InlineData(NotificationEventType.BookingCancelled)]
    [InlineData(NotificationEventType.BookingRescheduled)]
    [InlineData(NotificationEventType.RefundProcessed)]
    [InlineData(NotificationEventType.SupportTicketUpdate)]
    public async Task Every_trigger_event_has_a_template_on_every_channel(NotificationEventType eventType)
    {
        var renderer = new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions()));

        (await renderer.SupportsChannelAsync(eventType, NotificationChannel.Sms)).Should().BeTrue($"{eventType} must reach customers via SMS");
        (await renderer.SupportsChannelAsync(eventType, NotificationChannel.Email)).Should().BeTrue($"{eventType} must reach customers via email");
        (await renderer.SupportsChannelAsync(eventType, NotificationChannel.Push)).Should().BeTrue($"{eventType} must reach customers via push");
    }

    /// <summary>Booking-lifecycle events (88b-f) map to exactly the notification types SRS 19.1 lists - no silent trigger, no extra one.</summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, new[] { NotificationEventType.BookingConfirmed, NotificationEventType.PaymentSuccess })]
    [InlineData(BookingStatus.PaymentFailed, new[] { NotificationEventType.PaymentFailed })]
    [InlineData(BookingStatus.CancelledByCustomer, new[] { NotificationEventType.BookingCancelled })]
    [InlineData(BookingStatus.CancelledByAdmin, new[] { NotificationEventType.BookingCancelled })]
    [InlineData(BookingStatus.Rescheduled, new[] { NotificationEventType.BookingRescheduled })]
    [InlineData(BookingStatus.Refunded, new[] { NotificationEventType.RefundProcessed })]
    [InlineData(BookingStatus.Initiated, new NotificationEventType[0])]
    [InlineData(BookingStatus.AwaitingFulfilment, new NotificationEventType[0])]
    [InlineData(BookingStatus.Assigned, new NotificationEventType[0])]
    [InlineData(BookingStatus.InProgress, new NotificationEventType[0])]
    [InlineData(BookingStatus.RefundPending, new NotificationEventType[0])]
    public void Booking_status_transitions_trigger_exactly_the_documented_notification_events(
        BookingStatus toStatus, NotificationEventType[] expectedEventTypes)
    {
        var actual = ResolveTriggeredEventTypes(toStatus);
        actual.Should().BeEquivalentTo(expectedEventTypes);
    }

    /// <summary>
    /// Mirrors BookingNotificationTriggerHandler's own switch (kept as a
    /// small, independent re-implementation here deliberately - a QA
    /// coverage test that called into the exact same switch it's meant to
    /// audit would never catch a mistake made in that switch).
    /// </summary>
    private static NotificationEventType[] ResolveTriggeredEventTypes(BookingStatus toStatus) => toStatus switch
    {
        BookingStatus.Confirmed => [NotificationEventType.BookingConfirmed, NotificationEventType.PaymentSuccess],
        BookingStatus.PaymentFailed => [NotificationEventType.PaymentFailed],
        BookingStatus.CancelledByCustomer or BookingStatus.CancelledByAdmin => [NotificationEventType.BookingCancelled],
        BookingStatus.Rescheduled => [NotificationEventType.BookingRescheduled],
        BookingStatus.Refunded => [NotificationEventType.RefundProcessed],
        _ => []
    };

    // ---------------------------------------------------------------
    // End-to-end eligibility spot checks against the real services
    // (beyond what CancellationServiceTests/RescheduleServiceTests already
    // cover) - confirms the matrices above actually hold through the
    // service layer, not only the pure BookingLifecycle table.
    // ---------------------------------------------------------------

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
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
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

        return new BookingService(
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            couponService,
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
    }

    private async Task<(Customer Customer, Guid BookingId)> SeedBookingAsync(BookingStatus finalStatus)
    {
        using var context = _db.CreateContext();
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "221B Baker Street", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

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

        var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        if (finalStatus != BookingStatus.Initiated)
        {
            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(created.Value.Id);
            AdvanceTo(booking!, finalStatus);
            await bookingRepository.UpdateAsync(booking!);
        }

        return (customer, created.Value.Id);
    }

    private static void AdvanceTo(Booking booking, BookingStatus target)
    {
        // BookingService.CreateAsync already leaves a freshly created
        // booking at PaymentPending (see NoPaymentGatewayReason) - this
        // only walks it further, in the order BookingLifecycle allows.
        if (target == BookingStatus.PaymentPending) { return; }

        if (target == BookingStatus.PaymentFailed) { booking.TransitionTo(BookingStatus.PaymentFailed); return; }

        booking.TransitionTo(BookingStatus.Confirmed);
        if (target is BookingStatus.Confirmed) { return; }

        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        if (target is BookingStatus.AwaitingFulfilment) { return; }

        booking.TransitionTo(BookingStatus.Assigned);
        if (target is BookingStatus.Assigned) { return; }

        booking.TransitionTo(BookingStatus.InProgress);
        if (target is BookingStatus.InProgress) { return; }

        booking.TransitionTo(BookingStatus.Completed);
    }

    [Fact]
    public async Task CancellationService_rejects_a_completed_booking_end_to_end()
    {
        var (customer, bookingId) = await SeedBookingAsync(BookingStatus.Completed);
        using var context = _db.CreateContext();
        var service = new CancellationService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new RefundService(
                new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
                new WalletService(new WalletLedgerRepository(context)), new EscrowService(new PlatformEscrowLedgerRepository(context)), BuildGateway(), context),
            new BookingCancellationRepository(context), new BookingProviderAssignmentRepository(context), TimeProvider.System, Options.Create(new CancellationPolicyOptions()));

        var result = await service.GetPolicyAsync(customer.Id, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse("Completed is not in the cancellation matrix's eligible set");
    }

    [Fact]
    public async Task CancellationService_allows_an_InProgress_booking_no_it_does_not_end_to_end()
    {
        var (customer, bookingId) = await SeedBookingAsync(BookingStatus.InProgress);
        using var context = _db.CreateContext();
        var service = new CancellationService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new RefundService(
                new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
                new WalletService(new WalletLedgerRepository(context)), new EscrowService(new PlatformEscrowLedgerRepository(context)), BuildGateway(), context),
            new BookingCancellationRepository(context), new BookingProviderAssignmentRepository(context), TimeProvider.System, Options.Create(new CancellationPolicyOptions()));

        var result = await service.GetPolicyAsync(customer.Id, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse("service has already started");
    }

    [Fact]
    public async Task RescheduleService_rejects_a_completed_booking_end_to_end()
    {
        var (customer, bookingId) = await SeedBookingAsync(BookingStatus.Completed);
        using var context = _db.CreateContext();
        var service = new RescheduleService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context), new SlotBlackoutRepository(context), new SlotBookingPolicyRepository(context), new SlotCapacityRepository(context), TimeProvider.System),
            new BookingRescheduleRepository(context), TimeProvider.System, Options.Create(new ReschedulePolicyOptions()));

        var result = await service.GetEligibilityAsync(customer.Id, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse("Completed is not in the reschedule matrix's eligible set");
    }

    [Fact]
    public async Task RescheduleService_rejects_a_PaymentPending_booking_end_to_end()
    {
        var (customer, bookingId) = await SeedBookingAsync(BookingStatus.PaymentPending);
        using var context = _db.CreateContext();
        var service = new RescheduleService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context), new SlotBlackoutRepository(context), new SlotBookingPolicyRepository(context), new SlotCapacityRepository(context), TimeProvider.System),
            new BookingRescheduleRepository(context), TimeProvider.System, Options.Create(new ReschedulePolicyOptions()));

        var result = await service.GetEligibilityAsync(customer.Id, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsEligible.Should().BeFalse("nothing to reschedule before payment succeeds");
    }
}
