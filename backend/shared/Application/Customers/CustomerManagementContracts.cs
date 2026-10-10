using Nestly.Application.CustomerRatings;
using Nestly.Domain;

namespace Nestly.Application.Customers;

/// <summary>
/// Search/filter criteria for the admin customer list (SRS 12.4.1). All
/// filters are optional and combine with AND; string filters are
/// case-insensitive substring matches.
/// </summary>
public sealed record CustomerSearchFilter(
    string? Name,
    string? Mobile,
    string? Email,
    string? City,
    CustomerStatus? Status,
    DateTime? RegisteredFromUtc,
    DateTime? RegisteredToUtc,
    int? MinBookingCount,
    int? MaxBookingCount,
    int Page,
    int PageSize);

/// <summary>One customer row from a search, with its denormalized booking count (SRS 12.4.1 "Booking count" filter/column).</summary>
public sealed record CustomerSearchRow(Customer Customer, int BookingCount);

/// <summary>A page of <see cref="CustomerSearchRow"/> plus the total match count, for pagination.</summary>
public sealed record CustomerSearchResult(IReadOnlyList<CustomerSearchRow> Rows, int TotalCount);

/// <summary>Query-string shape of a customer search request (task 101a).</summary>
public sealed record CustomerSearchRequest(
    string? Name,
    string? Mobile,
    string? Email,
    string? City,
    CustomerStatus? Status,
    DateTime? RegisteredFromUtc,
    DateTime? RegisteredToUtc,
    int? MinBookingCount,
    int? MaxBookingCount,
    int Page = 1,
    int PageSize = 20);

public sealed record CustomerSummaryResponse(
    Guid Id,
    string Name,
    string Mobile,
    string? Email,
    string? City,
    CustomerStatus Status,
    DateTime CreatedAtUtc,
    int BookingCount);

public sealed record CustomerSearchResponse(
    IReadOnlyList<CustomerSummaryResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record CustomerAddressResponse(
    Guid Id,
    string Label,
    string Line1,
    string? Line2,
    string? Landmark,
    string Pincode,
    string City,
    string State,
    bool IsDefault);

/// <summary>One booking row in the 360 view's booking history (SRS 12.4.2).</summary>
public sealed record CustomerBookingResponse(
    Guid Id,
    BookingStatus Status,
    DateOnly SlotDate,
    decimal TotalPayableSnapshot,
    DateTime CreatedAtUtc);

/// <summary>One wallet ledger row in the 360 view (SRS 12.4.2 "Wallet/refund history").</summary>
public sealed record CustomerWalletEntryResponse(
    Guid Id,
    WalletEntryType EntryType,
    decimal Amount,
    decimal BalanceAfter,
    WalletSourceType SourceType,
    string Description,
    DateTime CreatedAtUtc);

/// <summary>One redeemed coupon in the 360 view (SRS 12.4.2 "Coupons used").</summary>
public sealed record CustomerCouponUsageResponse(
    Guid CouponId,
    string CouponCode,
    Guid BookingId,
    decimal DiscountAmount,
    DateTime RedeemedAtUtc);

/// <summary>One support ticket in the 360 view (SRS 12.4.2 "Support tickets").</summary>
public sealed record CustomerSupportTicketResponse(
    Guid Id,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    string Subject,
    SupportTicketStatus Status,
    DateTime CreatedAtUtc);

public sealed record CustomerNoteResponse(
    Guid Id,
    Guid AuthorAdminUserId,
    string Note,
    DateTime CreatedAtUtc);

/// <summary>The full customer 360 view (SRS 12.4.2, task 101b): profile, addresses, bookings, wallet, coupons, tickets and notes in one payload.</summary>
public sealed record CustomerDetailResponse(
    Guid Id,
    string Name,
    string Mobile,
    string? Email,
    DateTime? DateOfBirth,
    string? City,
    string? State,
    string? Pincode,
    string? Country,
    CustomerStatus Status,
    DateTime CreatedAtUtc,
    IReadOnlyList<CustomerAddressResponse> Addresses,
    IReadOnlyList<CustomerBookingResponse> Bookings,
    decimal WalletBalance,
    IReadOnlyList<CustomerWalletEntryResponse> WalletEntries,
    IReadOnlyList<CustomerCouponUsageResponse> Coupons,
    IReadOnlyList<CustomerSupportTicketResponse> SupportTickets,
    IReadOnlyList<CustomerNoteResponse> Notes,
    CustomerProviderRatingsResponse ProviderRatings);

/// <summary>
/// Bidirectional reviews: the ratings providers have left about this
/// customer, admin-only per product decision (never shown to the customer).
/// <see cref="AverageRating"/>/<see cref="RatingCount"/> are null/0 when the
/// customer has no ratings yet, same "no rating" vs "rated 0" distinction as
/// <c>ProviderRatingSummary</c> on the provider side.
/// </summary>
public sealed record CustomerProviderRatingsResponse(
    double? AverageRating,
    int RatingCount,
    IReadOnlyList<CustomerRatingRow> Recent);

/// <summary>Block a customer's account (SRS 12.4.3, task 101c). A reason is required for the audit trail.</summary>
public sealed record BlockCustomerRequest(string Reason);

/// <summary>
/// Admin-initiated manual wallet adjustment (Provider Management UX pass'
/// customer-side counterpart to <c>RecordProviderEarningAdjustmentRequest</c>)
/// - a goodwill credit or a correction/penalty debit, for when the wallet's
/// normal sources (refund, coupon, referral, Nestly Coins) don't apply.
/// Always recorded as <see cref="WalletSourceType.ManualAdjustment"/> with no
/// source aggregate, via <see cref="Nestly.Application.Wallet.IWalletService"/>
/// so the same balance/FIFO-consumption guarantees apply as every other
/// wallet write. A reason is required for the audit trail, same as
/// <see cref="BlockCustomerRequest"/>.
/// </summary>
public sealed record AdjustCustomerWalletRequest(WalletEntryType Direction, decimal Amount, string Reason);

/// <summary>Add an internal note to a customer's record (SRS 12.4.3, task 101d).</summary>
public sealed record AddCustomerNoteRequest(string Note);

/// <summary>
/// Query-string shape of the Customer Analytics dashboard request (Admin Web
/// new page, customer counterpart to the Provider Onboarding Overview/
/// Performance dashboards). <see cref="TrendDays"/> is a caller-tunable
/// rolling window (mirrors <c>ProviderPerformanceListRequest.PeriodDays</c>)
/// that sizes both the registration-trend series and the "new in this
/// window" KPI tile - there is no separate calendar-month concept on
/// <see cref="Customer"/>.
/// </summary>
public sealed record CustomerAnalyticsRequest(int TrendDays = 30);

/// <summary>One day of the registration-trend series - always present for every day in the window, zero-filled where nothing registered.</summary>
public sealed record CustomerRegistrationTrendPoint(DateOnly Date, int Count);

/// <summary>One row of the "top cities by customer count" breakdown table. Customers with a blank/null <see cref="Customer.City"/> are excluded rather than bucketed as "Unknown" - see <see cref="CustomerAnalyticsResponse"/>'s doc comment.</summary>
public sealed record CustomerCityBreakdown(string City, int Count);

/// <summary>
/// Repository-level result behind <see cref="CustomerAnalyticsResponse"/> -
/// the same aggregates, before the request's echoed
/// <see cref="CustomerAnalyticsRequest.TrendDays"/> is attached and
/// <see cref="CustomerAnalyticsResponse.CustomersWithZeroBookings"/> is
/// derived.
/// </summary>
public sealed record CustomerAnalyticsCounts(
    int TotalCustomers,
    int ActiveCount,
    int BlockedCount,
    int UnverifiedCount,
    int SoftDeletedCount,
    int NewToday,
    int NewLast7Days,
    int NewInTrendWindow,
    int CustomersWithBookings,
    IReadOnlyList<CustomerRegistrationTrendPoint> RegistrationTrend,
    IReadOnlyList<CustomerCityBreakdown> TopCities);

/// <summary>
/// Customer Analytics dashboard (Admin Web new page): KPI counts plus a
/// registration-trend graph, computed from real <see cref="Customer"/> and
/// <see cref="Nestly.Domain.Booking"/> data only - no invented metrics.
/// Deliberately excludes any KYC/verification concept (customers, unlike
/// providers, have no onboarding funnel - just <see cref="CustomerStatus"/>)
/// and any wallet/referral rollup (out of scope for this dashboard; see the
/// Customer 360 view for those per-customer).
/// </summary>
/// <param name="TrendDays">Echoes the request's rolling-window size.</param>
/// <param name="TotalCustomers">Every customer record, any status.</param>
/// <param name="ActiveCount">Current <see cref="CustomerStatus.Active"/> count.</param>
/// <param name="BlockedCount">Current <see cref="CustomerStatus.Blocked"/> count.</param>
/// <param name="UnverifiedCount">Current <see cref="CustomerStatus.Unverified"/> count.</param>
/// <param name="SoftDeletedCount">Current <see cref="CustomerStatus.SoftDeleted"/> count.</param>
/// <param name="NewToday">Registered on today's UTC calendar date.</param>
/// <param name="NewLast7Days">Registered in the trailing 7 days (today inclusive).</param>
/// <param name="NewInTrendWindow">Registered in the trailing <see cref="TrendDays"/> days (today inclusive) - the same window <see cref="RegistrationTrend"/> covers.</param>
/// <param name="CustomersWithBookings">Customers with at least one booking, of any status (SRS 12.4.1's own booking-count filter) - the activation half of the acquisition-vs-activation funnel.</param>
/// <param name="CustomersWithZeroBookings">Derived as <see cref="TotalCustomers"/> minus <see cref="CustomersWithBookings"/> - registered but never booked.</param>
/// <param name="RegistrationTrend">Daily registration counts for the trailing <see cref="TrendDays"/> days, oldest first, zero-filled.</param>
/// <param name="TopCities">Top 10 cities by customer count (customers with a blank/null city excluded - see <see cref="CustomerCityBreakdown"/>).</param>
public sealed record CustomerAnalyticsResponse(
    int TrendDays,
    int TotalCustomers,
    int ActiveCount,
    int BlockedCount,
    int UnverifiedCount,
    int SoftDeletedCount,
    int NewToday,
    int NewLast7Days,
    int NewInTrendWindow,
    int CustomersWithBookings,
    int CustomersWithZeroBookings,
    IReadOnlyList<CustomerRegistrationTrendPoint> RegistrationTrend,
    IReadOnlyList<CustomerCityBreakdown> TopCities);
