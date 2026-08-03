using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Abstractions.Observability;
using Nestly.Application.AdminUserManagement;
using Nestly.Application.Auditing;
using Nestly.Application.Chat;
using Nestly.Application.Identity;
using Nestly.Application.Profile;
using Nestly.Application.Bookings;
using Nestly.Application.BookingManagement;
using Nestly.Application.Cancellations;
using Nestly.Application.Catalog;
using Nestly.Application.Cms;
using Nestly.Application.Coupons;
using Nestly.Application.Dashboard;
using Nestly.Application.Customers;
using Nestly.Application.Escrow;
using Nestly.Application.Geography;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Notifications;
using Nestly.Application.ProviderAvailability;
using Nestly.Application.ProviderEarnings;
using Nestly.Application.ProviderIdentity;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Application.ProviderProfile;
using Nestly.Application.NestlyCoins;
using Nestly.Application.Referral;
using Nestly.Application.RecurringBookings;
using Nestly.Application.Refunds;
using Nestly.Application.Reports;
using Nestly.Application.Reschedules;
using Nestly.Application.Reviews;
using Nestly.Application.Settings;
using Nestly.Application.Subscriptions;
using Nestly.Application.Support;
using Nestly.Application.Wallet;
using Nestly.Application.Serviceability;
using Nestly.Application.Slots;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Authorization;
using Nestly.Infrastructure.BackgroundJobs;
using Nestly.Infrastructure.Caching;
using Nestly.Infrastructure.Observability;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Realtime;
using Nestly.Infrastructure.Services;
using OpenTelemetry.Metrics;

namespace Nestly.Infrastructure;

public static class DependencyInjection
{
    private const string DatabaseConnectionName = "Database";

    /// <summary>
    /// Registers infrastructure services: persistence, caching (T017),
    /// background jobs (T018), auditing (T020), health checks, and — as each
    /// capability lands — external providers.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<AccountOptions>()
            .Bind(configuration.GetSection(AccountOptions.SectionName));

        // No ValidateOnStart here (task 152 fix): AddInfrastructure is shared
        // by all three APIs, but only consumer-api's Program.cs calls
        // AddJwtAuthentication (which already throws eagerly on a missing
        // signing key) - admin-api and provider-api never resolve JwtOptions
        // at all, so validating it unconditionally at startup forced both to
        // carry a dummy customer-JWT secret they never use. Same reasoning
        // as AdminJwtOptions/ProviderJwtOptions below.
        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations();

        // No ValidateOnStart here (unlike JwtOptions): AddInfrastructure is
        // shared by both APIs, and consumer-api's configuration has no
        // reason to ever define an "AdminJwt" section. Validating eagerly
        // would fail consumer-api's own startup for a section it never
        // uses; binding lazily means the check only runs when admin-api
        // actually resolves AdminJwtOptions (i.e. when a token is issued).
        services
            .AddOptions<AdminJwtOptions>()
            .Bind(configuration.GetSection(AdminJwtOptions.SectionName))
            .ValidateDataAnnotations();

        services
            .AddOptions<AdminAccountOptions>()
            .Bind(configuration.GetSection(AdminAccountOptions.SectionName));

        // No ValidateOnStart here either, for the same reason as
        // AdminJwtOptions: only the future provider-api (task 149) configures
        // a "ProviderJwt" section - see ProviderJwtOptions' doc comment.
        services
            .AddOptions<ProviderJwtOptions>()
            .Bind(configuration.GetSection(ProviderJwtOptions.SectionName))
            .ValidateDataAnnotations();

        services
            .AddOptions<ProviderAccountOptions>()
            .Bind(configuration.GetSection(ProviderAccountOptions.SectionName));

        // No ValidateOnStart here either (task 152 fix): SandboxPaymentGateway
        // is a singleton constructed lazily by the DI container, so its
        // IOptions<SandboxGatewayOptions> is only ever resolved by an API
        // that actually injects IPaymentGateway/ISandboxPaymentSimulator
        // (consumer-api, admin-api) - provider-api never does, and shouldn't
        // need a placeholder webhook secret just to satisfy an eager check
        // for a gateway it never calls.
        services
            .AddOptions<SandboxGatewayOptions>()
            .Bind(configuration.GetSection(SandboxGatewayOptions.SectionName))
            .ValidateDataAnnotations();

        // Task 157: not a secret, so (unlike the options above) this is safe
        // to fall back to CommissionOptions' own defaults when a deployment
        // environment hasn't set the section at all - no ValidateOnStart.
        services
            .AddOptions<CommissionOptions>()
            .Bind(configuration.GetSection(CommissionOptions.SectionName))
            .ValidateDataAnnotations();

        // Task 137a-c (SRS 29.6, DEVOPS.md OBSERVABILITY): not a secret and
        // has safe production-sensible defaults, same reasoning as
        // CommissionOptions above - no ValidateOnStart.
        services
            .AddOptions<MetricsOptions>()
            .Bind(configuration.GetSection(MetricsOptions.SectionName))
            .ValidateDataAnnotations();

        // Task 162: not a secret, has a safe placeholder default, same
        // reasoning as CommissionOptions above - no ValidateOnStart.
        services
            .AddOptions<ReferralOptions>()
            .Bind(configuration.GetSection(ReferralOptions.SectionName))
            .ValidateDataAnnotations();

        // Task 178: not a secret, has safe production-sensible defaults,
        // same reasoning as CommissionOptions above - no ValidateOnStart.
        services
            .AddOptions<SubscriptionBillingOptions>()
            .Bind(configuration.GetSection(SubscriptionBillingOptions.SectionName))
            .ValidateDataAnnotations();

        // Task 185: not a secret, has a safe production-sensible default -
        // same reasoning as CommissionOptions/ReferralOptions above.
        services
            .AddOptions<RecurringBookingOptions>()
            .Bind(configuration.GetSection(RecurringBookingOptions.SectionName))
            .ValidateDataAnnotations();

        string connectionString = configuration.GetConnectionString(DatabaseConnectionName) ??
            throw new InvalidOperationException(
                $"Connection string '{DatabaseConnectionName}' is not configured.");

        services.AddSingleton<AuditableEntityInterceptor>();
        services.AddScoped<DomainEventDispatchInterceptor>();
        services.AddSingleton<NewOwnedChildEntityInterceptor>();

        services.AddDbContext<NestlyDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(
                    serviceProvider.GetRequiredService<AuditableEntityInterceptor>(),
                    serviceProvider.GetRequiredService<DomainEventDispatchInterceptor>(),
                    serviceProvider.GetRequiredService<NewOwnedChildEntityInterceptor>()));

        services
            .AddHealthChecks()
            .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

        // Task 137a-c (SRS 29.6, DEVOPS.md OBSERVABILITY): singleton so the
        // underlying Meter and rolling failure-rate windows accumulate across
        // the whole process lifetime, not per request/scope. The OpenTelemetry
        // SDK collects from NestlyMetricsService.MeterName and exposes it on a
        // self-hosted Prometheus scrape endpoint (see each API's Program.cs
        // MapPrometheusScrapingEndpoint call) - no OTel collector exists
        // anywhere in this repo yet, so a scrape endpoint is the smallest
        // infrastructure footprint that is still useful once DEVOPS.md's
        // "Monitoring/alerting stack" open decision is resolved.
        services.AddSingleton<IMetricsService, NestlyMetricsService>();
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(NestlyMetricsService.MeterName)
                .AddPrometheusExporter());

        services.AddCaching(configuration);
        services.AddBackgroundJobs(configuration, connectionString);

        // Task 190: real-time chat transport. One SignalR hub type
        // (ChatHub) mapped by both consumer-api and admin-api - see its doc
        // comment for why a shared Redis backplane, not two independent hub
        // instances, is what makes a message persisted by one API process
        // reach a live connection held by the other. Falls back to a
        // single-process hub (still fully correct for local dev/tests,
        // where only one API instance is ever running) when Redis is not
        // configured, same graceful-degradation shape as AddCaching above.
        var chatCacheOptions = new CacheOptions();
        configuration.GetSection(CacheOptions.SectionName).Bind(chatCacheOptions);
        var signalRBuilder = services.AddSignalR();
        if (chatCacheOptions.IsRedisConfigured)
        {
            signalRBuilder.AddStackExchangeRedis(chatCacheOptions.ConnectionString!, options =>
            {
                options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("nestly-chat");
            });
        }

        services.AddScoped<IChatThreadRepository, ChatThreadRepository>();
        services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IAdminChatService, AdminChatService>();
        services.AddSingleton<IChatPresenceTracker, ChatPresenceTracker>();

        // Application.DependencyInjection.AddApplication() only scans the
        // Application assembly for MediatR handlers, so this second
        // registration is what actually wires up CatalogCacheInvalidationHandler
        // (and any other Infrastructure-layer handler) - without it, domain
        // events would keep dispatching, but nothing in this assembly would
        // receive them.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        // Audit attribution reads the current request; without this accessor
        // every user action would be silently attributed to the system.
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditContextProvider, HttpAuditContextProvider>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();

        // Read side of the same audit trail (task 130) - filterable search
        // behind the admin audit-log-viewer screen.
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IServiceAddOnRepository, ServiceAddOnRepository>();
        services.AddScoped<IServiceFaqRepository, ServiceFaqRepository>();
        services.AddScoped<ISlotBlackoutRepository, SlotBlackoutRepository>();
        services.AddScoped<ISlotBookingPolicyRepository, SlotBookingPolicyRepository>();
        services.AddScoped<ISlotWindowRepository, SlotWindowRepository>();
        services.AddScoped<ISlotAvailabilityOverrideRepository, SlotAvailabilityOverrideRepository>();
        services.AddScoped<ISlotCapacityRepository, SlotCapacityRepository>();
        services.AddScoped<ISlotManagementService, SlotManagementService>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISlotAvailabilityService, SlotAvailabilityService>();
        services.AddScoped<IServiceCityPriceRepository, ServiceCityPriceRepository>();
        services.AddScoped<ICityPricingPolicyRepository, CityPricingPolicyRepository>();
        services.AddScoped<IPromotionalPriceRepository, PromotionalPriceRepository>();
        services.AddScoped<IPriceCalculationService, PriceCalculationService>();
        services.AddScoped<IPricingManagementService, PricingManagementService>();
        services.AddScoped<IServiceabilityRepository, ServiceabilityRepository>();
        services.AddScoped<IServiceabilityValidationService, ServiceabilityValidationService>();
        services.AddScoped<IGeographyRepository, GeographyRepository>();
        services.AddScoped<IGeographyQueryService, GeographyQueryService>();

        // Task 111: admin geography master CRUD + serviceability mapping
        // management (SRS 12.9). Separate repositories from the read-only
        // IGeographyRepository/IServiceabilityRepository above - those back
        // public lookups and validation, these back admin create/rename/
        // activate-deactivate.
        services.AddScoped<IStateRepository, StateRepository>();
        services.AddScoped<ICityRepository, CityRepository>();
        services.AddScoped<IZoneRepository, ZoneRepository>();
        services.AddScoped<ILocalityRepository, LocalityRepository>();
        services.AddScoped<IPincodeRepository, PincodeRepository>();
        services.AddScoped<IGeographyManagementService, GeographyManagementService>();
        services.AddScoped<ICategoryCityMappingRepository, CategoryCityMappingRepository>();
        services.AddScoped<IServicePincodeMappingRepository, ServicePincodeMappingRepository>();
        services.AddScoped<IServiceabilityMappingManagementService, ServiceabilityMappingManagementService>();
        services.AddScoped<ICategoryQueryService, CategoryQueryService>();
        services.AddScoped<IServiceQueryService, ServiceQueryService>();
        services.AddScoped<ICatalogSearchService, CatalogSearchService>();

        // Tasks 103a-108: admin catalog management (SRS 12.5-12.7) - category,
        // service/package and add-on CRUD. Separate from the read-only
        // I*QueryService above, which back public/consumer-facing catalog
        // browsing rather than admin create/edit/activate.
        services.AddScoped<ICategoryManagementService, CategoryManagementService>();
        services.AddScoped<IServiceMediaRepository, ServiceMediaRepository>();
        services.AddScoped<IServiceManagementService, ServiceManagementService>();
        services.AddScoped<IServiceAddOnManagementService, ServiceAddOnManagementService>();
        services.AddScoped<ICustomerAddressRepository, CustomerAddressRepository>();
        services.AddScoped<IBookingSummaryService, BookingSummaryService>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<ICustomerAuthIdentityRepository, CustomerAuthIdentityRepository>();
        services.AddScoped<ICustomerSessionRepository, CustomerSessionRepository>();
        services.AddScoped<ILoginAttemptRepository, LoginAttemptRepository>();
        services.AddScoped<IOTPService, OtpService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<ICustomerRegistrationService, CustomerRegistrationService>();
        services.AddScoped<ICustomerLoginService, CustomerLoginService>();
        services.AddScoped<ICustomerPasswordResetService, CustomerPasswordResetService>();
        services.AddScoped<ICustomerCommunicationPreferenceRepository, CustomerCommunicationPreferenceRepository>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();

        // Tasks 145a-146c: Provider module foundation (PROVIDER.md). Own
        // repositories/OTP/session/lockout tables throughout, kept
        // independent of the customer identity registrations above per
        // PROVIDER.md's SCOPE BOUNDARY - see ProviderOtp/ProviderLoginAttempt's
        // doc comments for why they are not shared with Customer's.
        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IProviderAuthIdentityRepository, ProviderAuthIdentityRepository>();
        services.AddScoped<IProviderSessionRepository, ProviderSessionRepository>();
        services.AddScoped<IProviderLoginAttemptRepository, ProviderLoginAttemptRepository>();
        services.AddScoped<IProviderKycDocumentRepository, ProviderKycDocumentRepository>();
        services.AddScoped<IProviderOtpService, ProviderOtpService>();
        services.AddScoped<IProviderTokenService, ProviderTokenService>();
        services.AddScoped<IProviderRegistrationService, ProviderRegistrationService>();
        services.AddScoped<IProviderLoginService, ProviderLoginService>();
        services.AddScoped<IProviderKycService, ProviderKycService>();

        // Task 149a: profile/service-area/skill management, reading and
        // writing the entities above through the provider-api's own
        // controllers. Task 149b: availability windows and blackout dates -
        // own repositories since neither is shared with any other module's
        // service.
        services.AddScoped<IProviderServiceAreaRepository, ProviderServiceAreaRepository>();
        services.AddScoped<IProviderSkillMappingRepository, ProviderSkillMappingRepository>();
        services.AddScoped<IProviderProfileService, ProviderProfileService>();
        services.AddScoped<IProviderAvailabilityWindowRepository, ProviderAvailabilityWindowRepository>();
        services.AddScoped<IProviderBlackoutDateRepository, ProviderBlackoutDateRepository>();
        services.AddScoped<IProviderAvailabilityService, ProviderAvailabilityService>();

        // Tasks 147, 148, 150a-c, 159, 160: admin-facing Provider management
        // (PROVIDER.md "Admin-Facing Additions") - assignment bridge, earning
        // ledger/payouts, CRUD, KYC approval and the background-check
        // activation gate, and the performance view. Kept apart from the
        // provider-identity registrations above, which are the provider's own
        // self-service auth/onboarding (tasks 145a-146c).
        services.AddScoped<IBookingProviderAssignmentRepository, BookingProviderAssignmentRepository>();
        services.AddScoped<IBookingProviderAssignmentService, BookingProviderAssignmentService>();
        // Task 195: completion verification (photo + checklist proof gating
        // the InProgress -> Completed transition, task 196) - registered
        // here rather than beside IBookingRepository above since every
        // caller today is provider/admin booking-management code, matching
        // where IBookingProviderAssignmentRepository lives.
        services.AddScoped<IBookingCompletionProofRepository, BookingCompletionProofRepository>();
        services.AddScoped<IProviderEarningLedgerRepository, ProviderEarningLedgerRepository>();
        services.AddScoped<IProviderEarningLedgerService, ProviderEarningLedgerService>();
        services.AddScoped<IProviderPayoutRepository, ProviderPayoutRepository>();
        services.AddScoped<IProviderPayoutService, ProviderPayoutService>();
        services.AddScoped<IProviderBackgroundCheckRepository, ProviderBackgroundCheckRepository>();
        services.AddScoped<IProviderManagementService, ProviderManagementService>();
        services.AddScoped<IProviderKycApprovalService, ProviderKycApprovalService>();

        // Tasks 149a/149c: provider-api's own self-service views over the
        // same Assignment Bridge/Financial Domain entities as the admin
        // registrations directly above - IProviderJobService additionally
        // owns the accept/reject IDOR checks and the start/complete booking
        // transitions; IProviderEarningsService is a read-only, ownership-
        // scoped facade over IProviderEarningLedgerService/IProviderPayoutService,
        // not a second copy of the ledger/payout logic.
        services.AddScoped<IProviderJobService, ProviderJobService>();
        services.AddScoped<IProviderEarningsService, ProviderEarningsService>();

        // Tasks 95a-95g: admin panel authentication. Separate registrations
        // from the customer identity services above - see AdminLoginService's
        // doc comment for why this is its own type rather than shared code.
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IAdminTokenService, AdminTokenService>();
        services.AddScoped<IAdminMfaChallengeProvider, NoOpAdminMfaChallengeProvider>();
        services.AddScoped<IAdminLoginService, AdminLoginService>();

        // Task 96c: role -> permission-code lookup at login time, embedded
        // into the JWT as claims (see AdminTokenService).
        services.AddScoped<IAdminRolePermissionQueryService, AdminRolePermissionQueryService>();

        // Task 96b/96d: evaluates one PermissionRequirement per admin
        // permission-code policy (registered in AddAdminJwtAuthentication
        // below) and audits denials/sensitive grants. Registered here rather
        // than there so it's discoverable alongside the rest of this file's
        // service registrations.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Tasks 97a-97d: admin user CRUD, role assignment, activate/
        // deactivate and admin-initiated password reset (SRS 12.2.1). Gated
        // behind "settings.read"/"settings.write" - the same policies
        // AdminAuthController.Unlock already uses for administering another
        // admin's account.
        services.AddScoped<IAdminRoleRepository, AdminRoleRepository>();
        services.AddScoped<IAdminUserManagementService, AdminUserManagementService>();

        // Tasks 131a-131h: admin-configurable settings/feature-flag store
        // (SRS 12.19). Gated behind "settings.read"/"settings.write" (already
        // generated by AdminPermissionCatalog for AdminModules.Settings).
        services.AddScoped<ISystemSettingRepository, SystemSettingRepository>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        // Tasks 128a-128d: standard reports (SRS 12.18.1) plus the
        // permission-gated async export queue (SRS 12.18.2). Gated behind
        // "reports.read"/"reports.write" (already generated by
        // AdminPermissionCatalog for AdminModules.Reports).
        services.AddScoped<IReportingQueryService, ReportingQueryService>();
        services.AddScoped<IExportJobRepository, ExportJobRepository>();
        services.AddScoped<IExportJobService, ExportJobService>();

        // Stateless - depends only on bound Options - so one shared instance
        // safely serves both interfaces (SandboxPaymentGateway implements
        // IPaymentGateway and the sandbox-only ISandboxPaymentSimulator).
        services.AddSingleton<SandboxPaymentGateway>();
        services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SandboxPaymentGateway>());
        services.AddSingleton<ISandboxPaymentSimulator>(sp => sp.GetRequiredService<SandboxPaymentGateway>());
        services.AddScoped<IPaymentTransactionRepository, PaymentTransactionRepository>();
        services.AddScoped<IPaymentWebhookService, PaymentWebhookService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<ICouponRepository, CouponRepository>();
        services.AddScoped<ICouponRedemptionRepository, CouponRedemptionRepository>();
        services.AddScoped<ICouponService, CouponService>();

        // Task 118: admin coupon management + redemption reporting (SRS
        // 12.12), gated behind "coupons.*" (task 96b) in AdminApi's
        // CouponsController. Distinct from the consumer-facing ICouponService
        // above - see CouponManagementService's doc comment.
        services.AddScoped<ICouponManagementService, CouponManagementService>();

        // Phase 10 subscription module (PRODUCT-ENHANCEMENTS.md #1, tasks
        // 177-183). ISubscriptionBenefitService feeds BookingSummaryService/
        // BookingService above, the same way ICouponService does.
        services.AddScoped<ISubscriptionPlanRepository, SubscriptionPlanRepository>();
        services.AddScoped<ICustomerSubscriptionRepository, CustomerSubscriptionRepository>();
        services.AddScoped<ISubscriptionPlanManagementService, SubscriptionPlanManagementService>();
        services.AddScoped<ICustomerSubscriptionService, CustomerSubscriptionService>();
        services.AddScoped<ISubscriptionBenefitService, SubscriptionBenefitService>();
        services.AddScoped<ISubscriptionBillingJob, SubscriptionBillingJob>();

        services.AddScoped<IWalletLedgerRepository, WalletLedgerRepository>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IWalletCreditExpirySweepJob, WalletCreditExpirySweepJob>();
        services.AddScoped<INestlyCoinsProgramConfigRepository, NestlyCoinsProgramConfigRepository>();
        services.AddScoped<INestlyCoinsService, NestlyCoinsService>();
        services.AddScoped<INestlyCoinsAdminService, NestlyCoinsAdminService>();
        services.AddScoped<INestlyCoinsCustomerService, NestlyCoinsCustomerService>();
        services.AddScoped<IReferralCodeService, ReferralCodeService>();
        services.AddScoped<IReferralRepository, ReferralRepository>();
        services.AddScoped<IReferralProgramConfigRepository, ReferralProgramConfigRepository>();
        services.AddScoped<IReferralRewardService, ReferralRewardService>();
        services.AddScoped<IReferralFraudReviewService, ReferralFraudReviewService>();
        services.AddScoped<IReferralMilestoneRepository, ReferralMilestoneRepository>();
        services.AddScoped<IReferralMilestoneAwardRepository, ReferralMilestoneAwardRepository>();
        services.AddScoped<IReferralProgramConfigAdminService, ReferralProgramConfigAdminService>();
        services.AddScoped<IReferralCustomerService, ReferralCustomerService>();
        services.AddScoped<IReferralAdminService, ReferralAdminService>();
        services.AddScoped<IRefundTransactionRepository, RefundTransactionRepository>();
        services.AddScoped<IRefundService, RefundService>();

        // Tasks 184-186: recurring booking plans. IRecurringBookingPlanService
        // depends on the existing IBookingSummaryService/IBookingService
        // (registered above) - the create/pause/cancel API and the scheduler
        // both call into the same booking orchestration rather than a
        // parallel one (PRODUCT-ENHANCEMENTS.md section 2).
        services.AddScoped<IRecurringBookingPlanRepository, RecurringBookingPlanRepository>();
        services.AddScoped<IRecurringBookingOccurrenceRepository, RecurringBookingOccurrenceRepository>();
        services.AddScoped<IRecurringBookingPlanService, RecurringBookingPlanService>();
        services.AddScoped<IRecurringBookingSchedulerService, RecurringBookingSchedulerService>();

        services
            .AddOptions<CancellationPolicyOptions>()
            .Bind(configuration.GetSection(CancellationPolicyOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddScoped<ICancellationRepository, BookingCancellationRepository>();
        services.AddScoped<ICancellationService, CancellationService>();

        services
            .AddOptions<ReschedulePolicyOptions>()
            .Bind(configuration.GetSection(ReschedulePolicyOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddScoped<IRescheduleRepository, BookingRescheduleRepository>();
        services.AddScoped<IRescheduleService, RescheduleService>();

        // Tasks 115a-117c: admin booking management (SRS 12.11, 12.13.2-3) -
        // composes IBookingRepository plus the Cancellation/Reschedule/Refund
        // services already registered above, so it needs no repositories of
        // its own beyond the read-side history repositories (Cancellation/
        // Reschedule/RefundTransaction) already registered above too.
        services.AddScoped<IBookingManagementService, BookingManagementService>();

        // Task 84a-d: support/experience schema repositories. The higher-
        // level services (review submission, ticket workflow, notification
        // dispatch) register themselves as each lands.
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
        services.AddScoped<INotificationEventRepository, NotificationEventRepository>();

        services
            .AddOptions<ReviewPolicyOptions>()
            .Bind(configuration.GetSection(ReviewPolicyOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddScoped<IReviewService, ReviewService>();

        // Task 122: admin review moderation (SRS 12.15) - search/hide/unhide/
        // flag/unflag/export over the same IReviewRepository above.
        services.AddScoped<IReviewModerationService, ReviewModerationService>();

        // Tasks 124a-125c: CMS - static pages, banners, and site-level FAQs
        // with draft/publish, scheduling, media, and placement (SRS 12.16;
        // 18). Gated behind "cms.read"/"cms.write" (already generated by
        // AdminPermissionCatalog for AdminModules.Cms).
        services.AddScoped<ICmsMediaRepository, CmsMediaRepository>();
        services.AddScoped<ICmsMediaService, CmsMediaService>();
        services.AddScoped<ICmsPageRepository, CmsPageRepository>();
        services.AddScoped<ICmsPageService, CmsPageService>();
        services.AddScoped<IBannerRepository, BannerRepository>();
        services.AddScoped<IBannerService, BannerService>();
        services.AddScoped<ICmsFaqRepository, CmsFaqRepository>();
        services.AddScoped<ICmsFaqService, CmsFaqService>();

        services.AddScoped<ISupportTicketService, SupportTicketService>();

        // Task 155: dispute mark/resolve workflow. Gated behind
        // "support.write" as of task 96b - see SupportTicketDisputesController's doc comment.
        services.AddScoped<IDisputeResolutionService, DisputeResolutionService>();

        // Tasks 120a-f: the general admin ticket workflow (search/detail,
        // assign/unassign, respond, escalate, resolve/close, link booking) -
        // gated behind "support.read"/"support.write" in SupportTicketsController.
        services.AddScoped<IAdminSupportTicketService, AdminSupportTicketService>();

        // Task 99: admin dashboard KPI widgets (SRS 12.3). Reads across the
        // Booking/Payment/Refund/Support aggregates directly - see
        // DashboardQueryService's doc comment for why this has no repository
        // of its own.
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();

        // Tasks 101a-101d: admin customer management (SRS 12.4). Composes the
        // Booking/Wallet/Coupon/SupportTicket repositories registered above
        // plus the new CustomerNote repository - gated behind "customers.*"
        // (task 96b) in AdminApi's CustomersController.
        services.AddScoped<ICustomerNoteRepository, CustomerNoteRepository>();
        services.AddScoped<ICustomerManagementService, CustomerManagementService>();

        // Task 87a-d: notification core. Tasks 126a-d (SRS 12.17) moved the
        // template set from a fixed built-in dictionary to an admin-managed,
        // DB-backed store - NotificationTemplateRenderer now depends on the
        // scoped NestlyDbContext (via INotificationTemplateRepository), so it
        // can no longer be a singleton; the active-template lookup itself is
        // still cached (IMemoryCache, a singleton) to keep the DB off the hot
        // dispatch path.
        services.AddMemoryCache();
        services.AddScoped<INotificationTemplateRepository, NotificationTemplateRepository>();
        services.AddScoped<INotificationTemplateRenderer, NotificationTemplateRenderer>();
        services.AddScoped<INotificationDispatchService, NotificationDispatchService>();

        // Task 126a-d: admin CRUD, preview and change audit over the template
        // store above (SRS 12.17).
        services.AddScoped<INotificationTemplateManagementService, NotificationTemplateManagementService>();

        // Task 156: push channel. Sandbox in every environment for now
        // (no real FCM/APNs credentials exist), same registration approach
        // as INotificationProvider above.
        services.AddScoped<IPushNotificationProvider, SandboxPushNotificationProvider>();
        services.AddScoped<IDeviceTokenRepository, DeviceTokenRepository>();
        services.AddScoped<IDeviceTokenService, DeviceTokenService>();

        // Task 157/158: commission calculation and the platform escrow
        // ledger. CommissionService is stateless (reads only bound Options),
        // same reasoning as SandboxPaymentGateway above.
        services.AddSingleton<ICommissionService, CommissionService>();
        services.AddScoped<IPlatformEscrowLedgerRepository, PlatformEscrowLedgerRepository>();
        services.AddScoped<IEscrowService, EscrowService>();

        // Sandbox in every environment for now (SRS 30.2): no real SMS/email
        // vendor is configured yet. Swap this registration, not the callers,
        // when a production provider lands.
        services.AddScoped<INotificationProvider, SandboxNotificationProvider>();

        return services;
    }

    /// <summary>
    /// JWT bearer authentication (SRS 11.2.2). Separate from
    /// <see cref="AddInfrastructure"/> because <c>AddAuthentication</c> sets
    /// the process-wide default scheme — each API's Program.cs calls this
    /// explicitly rather than getting it silently bundled in.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var signingKey = jwtSection[nameof(JwtOptions.SigningKey)] ??
            throw new InvalidOperationException($"Configuration section '{JwtOptions.SectionName}:{nameof(JwtOptions.SigningKey)}' is not configured.");
        var issuer = jwtSection[nameof(JwtOptions.Issuer)] ?? "Nestly";
        var audience = jwtSection[nameof(JwtOptions.Audience)] ?? "Nestly.Customers";

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Without this, the default inbound claim mapping silently
                // renames "sub" to ClaimTypes.NameIdentifier, so every
                // controller reading the customer id would have to know that
                // translation. Keep claim types exactly as TokenService issued
                // them (JwtRegisteredClaimNames.Sub, "mobile", ...jti).
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events = ChatHubJwtEvents.Create();
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// JWT bearer authentication for the admin panel (SRS 12.1, tasks
    /// 95a/95e). A distinct scheme name from the customer one so a single
    /// process could in principle host both without one scheme silently
    /// overriding the other; admin-api registers only this one.
    /// </summary>
    public const string AdminJwtBearerScheme = "AdminBearer";

    public static IServiceCollection AddAdminJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection(AdminJwtOptions.SectionName);
        var signingKey = jwtSection[nameof(AdminJwtOptions.SigningKey)] ??
            throw new InvalidOperationException($"Configuration section '{AdminJwtOptions.SectionName}:{nameof(AdminJwtOptions.SigningKey)}' is not configured.");
        var issuer = jwtSection[nameof(AdminJwtOptions.Issuer)] ?? "Nestly";
        var audience = jwtSection[nameof(AdminJwtOptions.Audience)] ?? "Nestly.AdminUsers";

        services
            .AddAuthentication(AdminJwtBearerScheme)
            .AddJwtBearer(AdminJwtBearerScheme, options =>
            {
                // Same reasoning as AddJwtAuthentication: keep claim types
                // exactly as AdminTokenService issued them rather than
                // letting the default inbound mapping rename "sub".
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events = ChatHubJwtEvents.Create();
            });

        // Task 96b: one authorization policy per permission code in the
        // matrix (AdminPermissionCatalog), so controllers can write
        // [Authorize(AuthenticationSchemes = AdminJwtBearerScheme, Policy = "catalog.write")]
        // rather than hand-rolling a role check. Every policy shares the
        // same PermissionAuthorizationHandler (registered in
        // AddInfrastructure) - only the requirement's permission code differs.
        services.AddAuthorization(options =>
        {
            foreach (string code in AdminPermissionCatalog.Permissions.Select(p => p.Code))
            {
                options.AddPolicy(code, policy => policy.Requirements.Add(new PermissionRequirement(code)));
            }
        });

        return services;
    }

    /// <summary>
    /// JWT bearer authentication for providers (task 146b, PROVIDER.md API
    /// surface). A distinct scheme name from the customer/admin ones, same
    /// reasoning as <see cref="AdminJwtBearerScheme"/>. Not called by either
    /// existing API's Program.cs - the future provider-api (task 149) will
    /// call this the same way admin-api calls <see cref="AddAdminJwtAuthentication"/>.
    /// </summary>
    public const string ProviderJwtBearerScheme = "ProviderBearer";

    public static IServiceCollection AddProviderJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection(ProviderJwtOptions.SectionName);
        var signingKey = jwtSection[nameof(ProviderJwtOptions.SigningKey)] ??
            throw new InvalidOperationException($"Configuration section '{ProviderJwtOptions.SectionName}:{nameof(ProviderJwtOptions.SigningKey)}' is not configured.");
        var issuer = jwtSection[nameof(ProviderJwtOptions.Issuer)] ?? "Nestly";
        var audience = jwtSection[nameof(ProviderJwtOptions.Audience)] ?? "Nestly.Providers";

        services
            .AddAuthentication(ProviderJwtBearerScheme)
            .AddJwtBearer(ProviderJwtBearerScheme, options =>
            {
                // Same reasoning as AddJwtAuthentication/AddAdminJwtAuthentication:
                // keep claim types exactly as ProviderTokenService issued them.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>CORS policy name every API's Program.cs passes to <c>UseCors</c>.</summary>
    public const string NestlyCorsPolicy = "NestlyCors";

    /// <summary>
    /// Registers a CORS policy from the "Cors:AllowedOrigins" configuration
    /// section (task 140a: the E2E suite surfaced that no API had a CORS
    /// policy at all, so every browser-originated request - real or test -
    /// failed the preflight check before ever reaching a controller).
    /// Credentials are not enabled: every API authenticates via a Bearer
    /// token in the Authorization header (see AddJwtAuthentication /
    /// AddAdminJwtAuthentication / AddProviderJwtAuthentication), never a
    /// cookie, so there is nothing that needs
    /// Access-Control-Allow-Credentials - keeping it off is the safer
    /// default per docs/CLAUDE.md SECURITY ("least privilege").
    /// </summary>
    public static IServiceCollection AddNestlyCors(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection(CorsOptions.SectionName)
            .Get<CorsOptions>()?.AllowedOrigins ?? [];

        services.AddCors(options =>
        {
            options.AddPolicy(NestlyCorsPolicy, policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
                }
                // No origins configured: the policy exists but permits
                // nothing, matching "external, environment specific"
                // configuration - a missing config value fails closed
                // (every browser request rejected) rather than open.
            });
        });

        return services;
    }
}
