using Microsoft.EntityFrameworkCore;
using Nestly.Domain;
using Nestly.Domain.NestlyCoins;

namespace Nestly.Infrastructure.Persistence;

public sealed class NestlyDbContext : DbContext
{
    public DbSet<State> States { get; set; }
    public DbSet<City> Cities { get; set; }
    public DbSet<Zone> Zones { get; set; }
    public DbSet<Pincode> Pincodes { get; set; }
    public DbSet<Locality> Localities { get; set; }
    public DbSet<CategoryCityMapping> CategoryCityMappings { get; set; }
    public DbSet<ServicePincodeMapping> ServicePincodeMappings { get; set; }
    public DbSet<SlotWindow> SlotWindows { get; set; }
    public DbSet<SlotWindowRule> SlotWindowRules { get; set; }
    public DbSet<SlotBlackout> SlotBlackouts { get; set; }
    public DbSet<SlotBookingPolicy> SlotBookingPolicies { get; set; }
    public DbSet<SlotAvailabilityOverride> SlotAvailabilityOverrides { get; set; }
    public DbSet<SlotBookingCounter> SlotBookingCounters { get; set; }
    public DbSet<ServiceCityPrice> ServiceCityPrices { get; set; }
    public DbSet<CityPricingPolicy> CityPricingPolicies { get; set; }
    public DbSet<PromotionalPrice> PromotionalPrices { get; set; }
    public DbSet<Booking> Bookings { get; set; }
    public DbSet<BookingItem> BookingItems { get; set; }
    public DbSet<BookingAddOnItem> BookingAddOnItems { get; set; }
    public DbSet<BookingStatusHistory> BookingStatusHistories { get; set; }
    public DbSet<BookingCompletionProof> BookingCompletionProofs { get; set; }
    public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
    public DbSet<PaymentAttempt> PaymentAttempts { get; set; }
    public DbSet<RefundTransaction> RefundTransactions { get; set; }
    public DbSet<WalletLedgerEntry> WalletLedgerEntries { get; set; }
    public DbSet<PlatformEscrowLedger> PlatformEscrowLedgers { get; set; }
    public DbSet<Coupon> Coupons { get; set; }
    public DbSet<CouponRedemption> CouponRedemptions { get; set; }
    public DbSet<BookingCancellation> BookingCancellations { get; set; }
    public DbSet<BookingReschedule> BookingReschedules { get; set; }
    public DbSet<Review> Reviews { get; set; }
    public DbSet<SupportTicket> SupportTickets { get; set; }
    public DbSet<SupportTicketComment> SupportTicketComments { get; set; }
    public DbSet<NotificationEvent> NotificationEvents { get; set; }
    public DbSet<DeviceToken> DeviceTokens { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<CmsPage> CmsPages { get; set; }
    public DbSet<Banner> Banners { get; set; }
    public DbSet<CmsFaq> CmsFaqs { get; set; }
    public DbSet<CmsMedia> CmsMediaAssets { get; set; }
    public DbSet<NotificationTemplate> NotificationTemplates { get; set; }
    public DbSet<ExportJob> ExportJobs { get; set; }
    public DbSet<Provider> Providers { get; set; }
    public DbSet<ProviderAuthIdentity> ProviderAuthIdentities { get; set; }
    public DbSet<ProviderOtp> ProviderOtps { get; set; }
    public DbSet<ProviderSession> ProviderSessions { get; set; }
    public DbSet<ProviderLoginAttempt> ProviderLoginAttempts { get; set; }
    public DbSet<ProviderKycDocument> ProviderKycDocuments { get; set; }
    public DbSet<ProviderSkillMapping> ProviderSkillMappings { get; set; }
    public DbSet<ProviderServiceArea> ProviderServiceAreas { get; set; }
    public DbSet<ProviderAvailabilityWindow> ProviderAvailabilityWindows { get; set; }
    public DbSet<ProviderBlackoutDate> ProviderBlackoutDates { get; set; }
    public DbSet<ProviderCapacity> ProviderCapacities { get; set; }
    public DbSet<BookingProviderAssignment> BookingProviderAssignments { get; set; }
    public DbSet<ProviderEarningLedgerEntry> ProviderEarningLedgerEntries { get; set; }
    public DbSet<ProviderPayout> ProviderPayouts { get; set; }
    public DbSet<ProviderBackgroundCheck> ProviderBackgroundChecks { get; set; }
    public DbSet<Referral> Referrals { get; set; }
    public DbSet<ReferralProgramConfig> ReferralProgramConfigs { get; set; }
    public DbSet<RecurringBookingPlan> RecurringBookingPlans { get; set; }
    public DbSet<RecurringBookingPlanAddOn> RecurringBookingPlanAddOns { get; set; }
    public DbSet<RecurringBookingOccurrence> RecurringBookingOccurrences { get; set; }
    public DbSet<ReferralMilestone> ReferralMilestones { get; set; }
    public DbSet<ReferralMilestoneAward> ReferralMilestoneAwards { get; set; }
    public DbSet<ChatThread> ChatThreads { get; set; }
    public DbSet<ChatMessage> ChatMessages { get; set; }
    public DbSet<NestlyCoinsProgramConfig> NestlyCoinsProgramConfigs { get; set; }
    public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }
    public DbSet<CustomerSubscription> CustomerSubscriptions { get; set; }

    public NestlyDbContext(DbContextOptions<NestlyDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NestlyDbContext).Assembly);
    }
}
