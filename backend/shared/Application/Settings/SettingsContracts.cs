namespace Nestly.Application.Settings;

/// <summary>
/// Booking rules settings group (SRS 12.19 "Booking rules", task 131a).
/// Governs how far ahead and how close to a slot a booking may be created -
/// distinct from <see cref="SlotSettings"/>, which governs how slots
/// themselves are generated. Once an admin has saved this group the slot engine applies the lead time, the booking horizon
/// and same-day on/off, and booking creation applies the active-bookings cap (see <see cref="IPlatformRules"/>).
/// </summary>
/// <param name="MinLeadTimeHours">A booking must start at least this many hours from now.</param>
/// <param name="MaxAdvanceBookingDays">A booking cannot be made more than this many days ahead.</param>
/// <param name="MaxActiveBookingsPerCustomer">Cap on a customer's simultaneous non-terminal bookings; null = unlimited.</param>
/// <param name="AllowSameDayBooking">Whether a booking may be created for the current calendar day at all.</param>
public sealed record BookingSettings(
    int MinLeadTimeHours,
    int MaxAdvanceBookingDays,
    int? MaxActiveBookingsPerCustomer,
    bool AllowSameDayBooking);

/// <summary>
/// Slot engine settings group (SRS 12.19 "Slot rules", SRS 15.2, task 131b). Once saved, the slot engine applies the same-day
/// cutoff, the booking horizon and overbooking; the default duration and capacity are recorded but not applied, because
/// slots are created one at a time with their own times and capacity.
/// </summary>
/// <param name="DefaultSlotDurationMinutes">Length of a generated slot window.</param>
/// <param name="SameDayCutoffHours">Same-day slots starting within this many hours are no longer offered (SRS 15.2 "same-day cutoff rules").</param>
/// <param name="MaxAdvanceBookingDays">How many days ahead slots are generated/offered (SRS 15.2 "advance booking days must be configurable").</param>
/// <param name="DefaultSlotCapacity">Default concurrent-booking capacity for a newly generated slot.</param>
/// <param name="AllowOverbooking">Whether a slot may accept bookings beyond its configured capacity.</param>
public sealed record SlotSettings(
    int DefaultSlotDurationMinutes,
    int SameDayCutoffHours,
    int MaxAdvanceBookingDays,
    int DefaultSlotCapacity,
    bool AllowOverbooking);

/// <summary>
/// Cancellation policy settings group (SRS 12.19 "Cancellation rules", SRS
/// 11.14.1, task 131c). Field shape mirrors the
/// <c>CancellationPolicyOptions</c> configuration binding. Once an admin has saved this group it is what
/// cancellations enforce (<see cref="IBookingPolicyProvider"/>); until then the configuration binding is.
/// </summary>
/// <param name="FreeCancellationWindowHours">Cancelling at least this many hours before the slot owes no fee.</param>
/// <param name="LateCancellationFeePercentage">Percentage of the payable amount retained when cancelling inside the free window.</param>
/// <param name="AllowAdminOverride">Whether an admin may waive the late-cancellation fee on a case-by-case basis.</param>
public sealed record CancellationSettings(
    decimal FreeCancellationWindowHours,
    decimal LateCancellationFeePercentage,
    bool AllowAdminOverride);

/// <summary>
/// Reschedule policy settings group (SRS 12.19 "Reschedule rules", SRS
/// 11.15.1, task 131d). Field shape mirrors the
/// <c>ReschedulePolicyOptions</c> configuration binding. Once an admin has saved this group it is what
/// reschedules enforce (<see cref="IBookingPolicyProvider"/>); until then the configuration binding is.
/// </summary>
/// <param name="MinHoursBeforeSlot">Rescheduling with less than this many hours to the current slot is blocked entirely.</param>
/// <param name="MaxReschedulesPerBooking">How many times a single booking may be rescheduled.</param>
/// <param name="LateFeeThresholdHours">Rescheduling with less than this many hours to go (but above <see cref="MinHoursBeforeSlot"/>) incurs a fee.</param>
/// <param name="LateRescheduleFeePercentage">Percentage of the booking's payable amount that is the late-reschedule fee.</param>
/// <param name="CollectLateFeeFromWallet">
/// Whether that fee is actually taken from the customer's wallet (a late reschedule is then refused when the wallet cannot cover it)
/// or only recorded on the booking. Off unless an admin turns it on; a value saved before this existed reads as off.
/// </param>
public sealed record RescheduleSettings(
    decimal MinHoursBeforeSlot,
    int MaxReschedulesPerBooking,
    decimal LateFeeThresholdHours,
    decimal LateRescheduleFeePercentage,
    bool CollectLateFeeFromWallet = false);

/// <summary>
/// Tax settings group (SRS 12.19 "Tax settings", task 131e). Once saved, <c>DefaultTaxPercentage</c> is charged in a city that
/// has no pricing policy of its own; <c>TaxInclusivePricing</c> and <c>TaxRegistrationNumber</c> are recorded but not applied
/// (docs/GST.md - the tax posture needs sign-off before inclusive pricing can change how every total is derived, and no tax
/// invoice exists to carry a registration number).
/// </summary>
/// <param name="DefaultTaxPercentage">Default GST/tax rate applied to a booking (0-100), used where no city-specific override (<c>CityPricingPolicy</c>) exists.</param>
/// <param name="TaxRegistrationNumber">Platform's tax/GST registration number, shown on customer invoices; null if not yet configured.</param>
/// <param name="TaxInclusivePricing">Whether displayed service prices already include tax.</param>
public sealed record TaxSettings(
    decimal DefaultTaxPercentage,
    string? TaxRegistrationNumber,
    bool TaxInclusivePricing);

/// <summary>
/// Wallet settings group (SRS 12.19 "Wallet settings", SRS 14.5, task 131f). Add-money and the balance cap are read by the
/// top-up service; once saved, <c>MaxWalletUsagePercentagePerBooking</c> caps the wallet's share at checkout.
/// <c>WalletCreditExpiryDays</c> is recorded but not applied - every credit type is the customer's own cash, has its own
/// programme expiry, or is documented as never expiring.
/// </summary>
/// <param name="MaxWalletBalance">Upper bound a customer's wallet balance may reach.</param>
/// <param name="MaxWalletUsagePercentagePerBooking">Cap on how much of a single booking's payable amount may be covered from wallet balance (0-100).</param>
/// <param name="WalletCreditExpiryDays">Days after which a wallet credit expires; null = credits never expire.</param>
/// <param name="AllowWalletTopUp">Whether customers may add funds to their wallet directly (as opposed to only receiving refund/cashback credits).</param>
public sealed record WalletSettings(
    decimal MaxWalletBalance,
    decimal MaxWalletUsagePercentagePerBooking,
    int? WalletCreditExpiryDays,
    bool AllowWalletTopUp);

/// <summary>
/// Coupon settings group (SRS 12.19 "Coupon settings", SRS 14.2, task 131g).
/// Platform-wide guardrails that apply across every <c>Coupon</c>, distinct
/// from a single coupon's own fields (code, discount value, validity window)
/// on the <c>Coupon</c> aggregate itself. Once saved: the switch refuses every code, the per-customer cap limits different
/// coupons held across live bookings, and the maximum percentage guards coupon creation. Stacking is recorded but not
/// applied - a booking takes one coupon.
/// </summary>
/// <param name="MaxDiscountPercentagePerCoupon">Upper bound any individual coupon's percentage discount may be configured to (0-100).</param>
/// <param name="MaxActiveCouponsPerCustomer">Cap on how many distinct coupons a customer may redeem while active; null = unlimited.</param>
/// <param name="AllowCouponStacking">Whether more than one coupon may be applied to the same booking.</param>
/// <param name="CouponsEnabled">Platform-wide feature flag - when false, coupon redemption is disabled everywhere regardless of individual coupon state (SRS 12.19 "Feature flags").</param>
public sealed record CouponSettings(
    decimal MaxDiscountPercentagePerCoupon,
    int? MaxActiveCouponsPerCustomer,
    bool AllowCouponStacking,
    bool CouponsEnabled);

/// <summary>
/// Customer- and provider-facing feature flags (SRS 12.19 "Feature flags").
/// Every flag here gates a genuinely optional, already-wired UI surface that
/// can disappear without breaking the booking/payment/fulfilment lifecycle -
/// core flow steps (booking creation, payment, provider job accept/status
/// advance, availability, profile) are deliberately never represented here.
/// Coupons already has its own platform-wide flag
/// (<see cref="CouponSettings.CouponsEnabled"/>) predating this group, so it
/// is not duplicated here - the public feature-flags endpoints project that
/// existing flag alongside these instead.
/// </summary>
/// <param name="WalletEnabled">Customer-web: the Wallet nav entry, account-menu link and bottom-tab entry (SRS 11.17).</param>
/// <param name="ReferralsEnabled">Customer-web: the Refer &amp; Earn nav entry and page (SRS 14.3).</param>
/// <param name="AmcSubscriptionsEnabled">Customer-web: the AMC Plans nav entry and promo card (SRS 14.6-ish AMC module).</param>
/// <param name="ServiceRatingsEnabled">Customer-web: the rating/review-count trust badge on the service detail page (<c>ServiceRatingBadge</c>).</param>
/// <param name="BookingHelpLinkEnabled">Customer-web: the "Need help? Contact support" link on the booking summary/payment pages (<c>BookingHelpLink</c>).</param>
/// <param name="RatingsPageEnabled">Provider-web: the Ratings &amp; feedback promo card on Profile and the <c>/ratings</c> page.</param>
/// <param name="CalendarViewEnabled">Provider-web: the "View week calendar" entry points on Jobs/Availability and the <c>/calendar</c> page.</param>
/// <param name="EarningsLedgerEnabled">Provider-web: only the ledger section of the Earnings page (<c>LedgerSection</c>) - the rest of Earnings (summary, per-job earnings, payouts) is unaffected.</param>
/// <param name="OffersScreenEnabled">Provider-web: the dedicated <c>/offers</c> screen - offer accept/decline stays reachable from <c>/today</c> and <c>/jobs</c>, which are never gated.</param>
/// <param name="AutoManageServiceabilityEnabled">
/// Internal/admin-only operational kill switch (docs/OPEN-FIXES-FEATURES.csv
/// "Service to pincode mapping" follow-up) - not customer- or provider-
/// facing, so deliberately absent from <see cref="CustomerFeatureFlagsResponse"/>/
/// <see cref="ProviderFeatureFlagsResponse"/> and their public
/// <c>GET /api/v1/feature-flags</c> endpoints. When false,
/// <c>IServiceabilityMappingManagementService.AutoEnableProviderCoverageAsync</c>
/// and <c>AutoDisableUnservedMappingsAsync</c> both no-op entirely, checked at
/// the start of each call before any work. Defaults true.
/// </param>
public sealed record FeatureFlagSettings(
    bool WalletEnabled,
    bool ReferralsEnabled,
    bool AmcSubscriptionsEnabled,
    bool ServiceRatingsEnabled,
    bool BookingHelpLinkEnabled,
    bool RatingsPageEnabled,
    bool CalendarViewEnabled,
    bool EarningsLedgerEnabled,
    bool OffersScreenEnabled,
    bool AutoManageServiceabilityEnabled);

/// <summary>
/// The customer-facing subset of <see cref="FeatureFlagSettings"/>, plus the
/// pre-existing coupons flag, for the public unauthenticated
/// <c>GET /api/v1/feature-flags</c> on consumer-api. Deliberately narrower
/// than the admin shape - an anonymous endpoint must never leak provider-side
/// flags or the full settings record.
/// </summary>
public sealed record CustomerFeatureFlagsResponse(
    bool WalletEnabled,
    bool CouponsEnabled,
    bool ReferralsEnabled,
    bool AmcSubscriptionsEnabled,
    bool ServiceRatingsEnabled,
    bool BookingHelpLinkEnabled);

/// <summary>
/// The provider-facing subset of <see cref="FeatureFlagSettings"/>, for the
/// public unauthenticated <c>GET /api/v1/feature-flags</c> on provider-api.
/// </summary>
public sealed record ProviderFeatureFlagsResponse(
    bool RatingsPageEnabled,
    bool CalendarViewEnabled,
    bool EarningsLedgerEnabled,
    bool OffersScreenEnabled);

/// <summary>Every settings group at once, for the admin Settings landing page (task 131h).</summary>
public sealed record AllSystemSettingsResponse(
    BookingSettings Booking,
    SlotSettings Slot,
    CancellationSettings Cancellation,
    RescheduleSettings Reschedule,
    TaxSettings Tax,
    WalletSettings Wallet,
    CouponSettings Coupon,
    FeatureFlagSettings Feature);
