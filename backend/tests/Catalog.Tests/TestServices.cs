using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Notifications;
using Nestly.Application.Wallet;
using Nestly.Application.Escrow;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Settings;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Shared construction of the collaborators that several services now take,
/// so a new cross-cutting dependency (a clock, an options binding) is added in
/// one place rather than in every <c>BuildService</c> helper in the suite.
/// </summary>
internal static class TestServices
{
    /// <summary>
    /// The business clock, pinned to UTC. Test fixtures build their slot dates
    /// from <see cref="DateTime.UtcNow"/>, so a UTC business timezone keeps
    /// "now" and "the slot" on the same clock and leaves each test measuring
    /// the rule it is actually about rather than a timezone offset. Production
    /// runs on the configured <see cref="BusinessTimeOptions.TimeZoneId"/>;
    /// the offset behaviour itself is covered by BusinessClockTests.
    /// </summary>
    public static IBusinessClock Clock(TimeProvider? timeProvider = null) =>
        new BusinessClock(
            timeProvider ?? TimeProvider.System,
            Options.Create(new BusinessTimeOptions { TimeZoneId = "UTC" }));

    /// <summary>
    /// The provider-queue early-release model's occupancy calculator (a
    /// verified-complete, non-duration-based job's actual finish time rather
    /// than its slot's own end), over <see cref="Clock"/> so it shares the
    /// same UTC-pinned "now" every other collaborator in this file does.
    /// </summary>
    public static IProviderJobOccupancyService Occupancy(TimeProvider? timeProvider = null) =>
        new ProviderJobOccupancyService(Clock(timeProvider));

    /// <summary>The provider-queue model's one-active-job gate, for suites that construct <see cref="ProviderJobService"/> directly.</summary>
    public static IProviderActiveJobLimitService ActiveJobLimit(NestlyDbContext context) =>
        new ProviderActiveJobLimitService(context);

    /// <summary>
    /// The provider-queue model's overrun-reassignment follow-up, for suites
    /// that construct <see cref="ProviderJobService"/> directly and are not
    /// themselves testing the overrun rule. Sandbox-backed travel feasibility
    /// (no network, no key) so it never fires for a suite whose bookings share
    /// one address.
    /// </summary>
    public static IOverrunReassignmentService OverrunReassignment(NestlyDbContext context) =>
        new OverrunReassignmentService(
            context,
            new BookingRepository(context),
            new BookingProviderAssignmentRepository(context),
            TravelFeasibilityFactory.Sandbox(context),
            NullLogger<OverrunReassignmentService>.Instance);

    /// <summary>Real <see cref="IProviderEarningLedgerService"/> over the test database, for suites (e.g. <c>RefundService</c>'s clawback) that need one only as a collaborator, not under test.</summary>
    public static IProviderEarningLedgerService ProviderEarningLedgerService(NestlyDbContext context) =>
        new ProviderEarningLedgerService(
            new ProviderRepository(context),
            new ProviderEarningLedgerRepository(context),
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new ProviderPayoutRepository(context));

    /// <summary>
    /// The full <see cref="RefundService"/> builder, extracted here once it
    /// grew a fifth and sixth collaborator (<see cref="ProviderEarningLedgerService"/>
    /// for the job-completion clawback) - every suite that only needs a
    /// working refund path, not the clawback itself under test, should call
    /// this rather than repeat the wiring.
    /// </summary>
    public static RefundService RefundService(NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            new ProviderEarningLedgerRepository(context),
            ProviderEarningLedgerService(context),
            gateway,
            context,
            NullLogger<RefundService>.Instance);

    /// <summary>
    /// The full <see cref="CancellationService"/> builder, extracted here
    /// once it grew enough collaborators (coupon release, subscription
    /// free-visit release, the cancellation-fee escrow release) that
    /// repeating the wiring per suite became the main source of test-file
    /// churn whenever a new one was added. Every suite that only needs a
    /// working cancellation path, not one of those releases itself under
    /// test, should call this rather than repeat the wiring.
    /// </summary>
    public static CancellationService CancellationService(
        NestlyDbContext context, IPaymentGateway gateway, TimeProvider timeProvider, CancellationPolicyOptions? policy = null) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            RefundService(context, gateway),
            new BookingCancellationRepository(context),
            new BookingProviderAssignmentRepository(context),
            SlotAvailability(context, timeProvider),
            new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
            new CustomerSubscriptionRepository(context),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            Clock(timeProvider),
            timeProvider,
            Policies(policy), ProviderNotificationPublisher(context), new BookingRescheduleRepository(context));

    /// <summary>
    /// The cancellation/reschedule policy as a fixed answer - the given options, or the defaults. What every suite that is
    /// not testing <see cref="BookingPolicyProvider"/> itself wants in place of the real, settings-backed provider.
    /// </summary>
    public static IBookingPolicyProvider Policies(CancellationPolicyOptions? cancellation = null, ReschedulePolicyOptions? reschedule = null) =>
        new FixedBookingPolicyProvider(cancellation ?? new CancellationPolicyOptions(), reschedule ?? new ReschedulePolicyOptions());

    /// <summary>
    /// Admin-saved platform rules as a fixed answer: only the groups passed are "saved", every other one is null - which is
    /// what an engine reads as "do what you always did".
    /// </summary>
    public static IPlatformRules Rules(
        BookingSettings? booking = null, SlotSettings? slot = null, TaxSettings? tax = null,
        WalletSettings? wallet = null, CouponSettings? coupon = null) =>
        new FixedPlatformRules(booking, slot, tax, wallet, coupon);

    private sealed class FixedPlatformRules(
        BookingSettings? booking, SlotSettings? slot, TaxSettings? tax, WalletSettings? wallet, CouponSettings? coupon) : IPlatformRules
    {
        public Task<BookingSettings?> GetBookingAsync(CancellationToken cancellationToken = default) => Task.FromResult(booking);

        public Task<SlotSettings?> GetSlotAsync(CancellationToken cancellationToken = default) => Task.FromResult(slot);

        public Task<TaxSettings?> GetTaxAsync(CancellationToken cancellationToken = default) => Task.FromResult(tax);

        public Task<WalletSettings?> GetWalletAsync(CancellationToken cancellationToken = default) => Task.FromResult(wallet);

        public Task<CouponSettings?> GetCouponAsync(CancellationToken cancellationToken = default) => Task.FromResult(coupon);
    }

    /// <summary>Real <see cref="IWalletService"/> over the test database.</summary>
    public static IWalletService Wallet(NestlyDbContext context) => new WalletService(new WalletLedgerRepository(context), context);

    /// <summary>Real <see cref="IEscrowService"/> over the test database.</summary>
    public static IEscrowService Escrow(NestlyDbContext context) => new EscrowService(new PlatformEscrowLedgerRepository(context));

    private sealed class FixedBookingPolicyProvider(CancellationPolicyOptions cancellation, ReschedulePolicyOptions reschedule) : IBookingPolicyProvider
    {
        public Task<CancellationSettings> GetCancellationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CancellationSettings(cancellation.FreeCancellationWindowHours, cancellation.LateCancellationFeePercentage, AllowAdminOverride: true));

        public Task<RescheduleSettings> GetRescheduleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RescheduleSettings(
                reschedule.MinHoursBeforeSlot, reschedule.MaxReschedulesPerBooking, reschedule.LateFeeThresholdHours, reschedule.LateRescheduleFeePercentage,
                reschedule.CollectLateFeeFromWallet));
    }

    public static SlotAvailabilityService SlotAvailability(NestlyDbContext context, TimeProvider? timeProvider = null) =>
        new(
            new ServiceabilityRepository(context),
            new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
            new SlotWindowRepository(context),
            new SlotBlackoutRepository(context),
            new SlotBookingPolicyRepository(context),
            new SlotCapacityRepository(context),
            Clock(timeProvider));

    public static IOptions<BookingOptions> BookingOptions(int? maxQuantityPerBooking = null) =>
        Options.Create(maxQuantityPerBooking is null
            ? new BookingOptions()
            : new BookingOptions { MaxQuantityPerBooking = maxQuantityPerBooking.Value });

    /// <summary>
    /// The <see cref="IOptionsMonitor{TOptions}"/> equivalent of
    /// <see cref="Options.Create{TOptions}"/> (task 276), which
    /// Microsoft.Extensions.Options does not ship. Only
    /// <c>BookingNotificationTriggerHandler</c> needs it today - it reads its
    /// mute switches through a monitor so an ops mute takes effect on config
    /// reload rather than at the next restart.
    /// </summary>
    public static IOptionsMonitor<T> Monitor<T>(T value) where T : class =>
        new StaticOptionsMonitor<T>(value);

    /// <summary>
    /// Attributed to <see cref="AuditContext.System"/> unconditionally -
    /// suites that construct <see cref="ServiceabilityMappingManagementService"/>
    /// (etc.) directly need a real <see cref="IAuditLogWriter"/> but are not
    /// themselves testing attribution, so there is nothing to stub per-suite
    /// the way <c>StubAuditContextProvider</c> is (that pattern still applies
    /// wherever a suite asserts on the ambient actor).
    /// </summary>
    public static IAuditLogWriter AuditLogWriter(NestlyDbContext context) =>
        new AuditLogWriter(context, SystemAuditContextProvider.Instance);

    /// <summary>Real <see cref="IProviderPlanReservationService"/> over the test database, for suites that build a service that consults it but are not testing it.</summary>
    public static IProviderPlanReservationService PlanReservations(NestlyDbContext context) =>
        new ProviderPlanReservationService(
            context, Clock(), Options.Create(new RecurringBookingOptions()),
            NullLogger<ProviderPlanReservationService>.Instance);

    /// <summary>Real <see cref="ISystemSettingsService"/> over the test database, for suites that need one only as a collaborator (not under test).</summary>
    public static ISystemSettingsService SystemSettings(NestlyDbContext context) =>
        new SystemSettingsService(new SystemSettingRepository(context), AuditLogWriter(context), SystemAuditContextProvider.Instance);

    /// <summary>
    /// Real <see cref="IProviderNotificationPublisher"/> over the test
    /// database with the sandbox push provider (no network, no key) - for
    /// suites that construct <see cref="BookingProviderAssignmentService"/>/
    /// <see cref="ProviderKycApprovalService"/>/<see cref="ProviderManagementService"/>/
    /// <see cref="ProviderPayoutService"/> directly and are not themselves
    /// testing the provider notification feed.
    /// </summary>
    public static IProviderNotificationPublisher ProviderNotificationPublisher(NestlyDbContext context) =>
        new ProviderNotificationPublisher(
            new ProviderNotificationRepository(context),
            new DeviceTokenRepository(context),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            NullLogger<Nestly.Infrastructure.Services.ProviderNotificationPublisher>.Instance);

    private sealed class SystemAuditContextProvider : IAuditContextProvider
    {
        public static readonly SystemAuditContextProvider Instance = new();
        public AuditContext GetCurrent() => AuditContext.System;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        /// <summary>Nothing ever changes, so the "changed" callback is never invoked - a no-op disposable is the honest registration.</summary>
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// The real task 294 coordinator over the test database. Real, not a stub:
    /// the claim it performs <i>is</i> the deduplication guarantee, so a test
    /// that swapped it for a pass-through would stop covering the one thing
    /// this collaborator exists for. With no intent rows present it fails open
    /// and dispatches, which is exactly how the handlers behaved before task
    /// 294 - that is what keeps the pre-existing trigger-wiring tests
    /// measuring what they always measured.
    /// </summary>
    public static NotificationIntentCoordinator IntentCoordinator(
        NestlyDbContext context,
        NotificationIntentOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            new NotificationIntentRepository(context),
            Monitor(options ?? new NotificationIntentOptions()),
            timeProvider ?? TimeProvider.System,
            NullLogger<NotificationIntentCoordinator>.Instance);

    /// <summary>
    /// Fulfilment notification switches, all enabled unless a test names the
    /// ones it wants muted (task 276).
    /// </summary>
    public static IOptionsMonitor<FulfilmentNotificationOptions> FulfilmentNotifications(
        bool providerAssigned = true,
        bool providerEnRoute = true,
        bool providerArrived = true,
        bool jobStarted = true,
        bool jobCompleted = true,
        bool providerChanged = true) =>
        Monitor(new FulfilmentNotificationOptions
        {
            ProviderAssignedEnabled = providerAssigned,
            ProviderEnRouteEnabled = providerEnRoute,
            ProviderArrivedEnabled = providerArrived,
            JobStartedEnabled = jobStarted,
            JobCompletedEnabled = jobCompleted,
            ProviderChangedEnabled = providerChanged
        });
}
