using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.RecurringBookings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// The service graph the recurring-plan tests need - the booking orchestration, the scheduler, the plan service and
/// the handler that pauses a plan - built over one real database context, the way the application wires it. Kept in
/// one place because rebuilding it per test class is the main source of churn whenever a constructor grows.
/// </summary>
internal static class RecurringTestWiring
{
    public sealed class NoOpNotificationProvider : INotificationProvider
    {
        public Task<BuildingBlocks.Results.Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());

        public Task<BuildingBlocks.Results.Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());
    }

    public static SandboxPaymentGateway Gateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    public static BookingSummaryService SummaryService(NestlyDbContext context) => new(
        new ServiceRepository(context),
        new ServiceAddOnRepository(context),
        new ServiceGroupRepository(context),
        new CustomerAddressRepository(context),
        TestServices.SlotAvailability(context),
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

    public static BookingService BookingService(NestlyDbContext context) => new(
        SummaryService(context),
        new BookingRepository(context),
        new CustomerRepository(context),
        new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
        TestServices.SlotAvailability(context),
        new NoOpMetricsService(),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ReviewRepository(context),
        new CustomerSubscriptionRepository(context),
        new WalletService(new WalletLedgerRepository(context), context),
        new AlwaysEligibleProviderSearchStub(),
        context);

    public static ProviderAssignmentEligibilityService EligibilityService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        TravelFeasibilityFactory.Sandbox(context),
        context);

    public static NotificationDispatchService DispatchService(NestlyDbContext context) => new(
        new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
        new NoOpNotificationProvider(),
        new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
        new NotificationEventRepository(context),
        new DeviceTokenRepository(context),
        new CustomerRepository(context),
        new ProviderRepository(context),
        new NoOpMetricsService(),
        NullLogger<NotificationDispatchService>.Instance);

    public static RecurringBookingSchedulerService Scheduler(NestlyDbContext context, RecurringBookingOptions options) => new(
        new RecurringBookingPlanRepository(context),
        new RecurringBookingOccurrenceRepository(context),
        BookingService(context),
        new CustomerRepository(context),
        new ServiceRepository(context),
        new SlotWindowRepository(context),
        new DeviceTokenRepository(context),
        DispatchService(context),
        new RecurringPlanProviderContinuityService(new BookingRepository(context)),
        EligibilityService(context),
        new EligibleProviderSearchService(
            new ProviderMatchingService(
                new BookingRepository(context),
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions())),
            EligibilityService(context)),
        new WalletService(new WalletLedgerRepository(context), context),
        new NotificationEventRepository(context),
        Options.Create(options),
        NullLogger<RecurringBookingSchedulerService>.Instance);

    public static RecurringBookingPlanService PlanService(
        NestlyDbContext context, RecurringBookingOptions options, IRecurringPlanNotifier? notifier = null) => new(
        new RecurringBookingPlanRepository(context),
        new RecurringBookingOccurrenceRepository(context),
        SummaryService(context),
        new ServiceRepository(context),
        new BookingRepository(context),
        Scheduler(context, options),
        TestServices.CancellationService(context, Gateway(), TimeProvider.System),
        Options.Create(options),
        TestServices.Clock(),
        TestServices.Policies(),
        new SlotWindowRepository(context),
        notifier ?? PlanNotifier(context),
        NullLogger<RecurringBookingPlanService>.Instance);

    /// <summary>The confirmation sender the plan service uses, over the same no-op providers as the dispatch service above.</summary>
    public static RecurringPlanNotifier PlanNotifier(NestlyDbContext context) => new(
        new CustomerRepository(context),
        new ServiceRepository(context),
        new SlotWindowRepository(context),
        new DeviceTokenRepository(context),
        DispatchService(context),
        NullLogger<RecurringPlanNotifier>.Instance);

    public static UnpaidBookingReleaseService ReleaseService(NestlyDbContext context) => new(
        new BookingRepository(context),
        TestServices.SlotAvailability(context),
        new WalletService(new WalletLedgerRepository(context), context),
        new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System));

    public static RecurringPlanOccurrenceReleaseHandler ReleaseHandler(NestlyDbContext context, RecurringBookingOptions? options = null) => new(
        new BookingRepository(context),
        new RecurringBookingPlanRepository(context),
        ReleaseService(context),
        new CustomerRepository(context),
        new ServiceRepository(context),
        new DeviceTokenRepository(context),
        DispatchService(context),
        Options.Create(options ?? new RecurringBookingOptions()),
        NullLogger<RecurringPlanOccurrenceReleaseHandler>.Instance);
}
