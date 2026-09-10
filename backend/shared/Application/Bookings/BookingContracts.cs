using Nestly.Application.Catalog;
using Nestly.Application.Coupons;
using Nestly.Application.Pricing;
using Nestly.Application.Reviews;
using Nestly.Application.Subscriptions;
using Nestly.Application.Wallet;
using Nestly.Domain;

namespace Nestly.Application.Bookings;

/// <summary>
/// A preview of what booking would produce (SRS 11.7, task 57) - the cart
/// model is single-service for now (SRS 11.7.1), so this always describes
/// exactly one service. LocalityId is separate from AddressId rather than
/// derived from it: CustomerAddress.LocalityId (task 154) resolves to null
/// until the address form gains a locality field, so callers must still
/// supply the locality they picked for serviceability/slot checks, the same
/// way the existing frontend location picker already works.
///
/// <paramref name="CouponCode"/> is optional and doubles as the coupon
/// apply/remove mechanism (task 73): supplying a code (re-)applies and
/// recomputes the discount server-side; omitting it (or calling again with
/// null) recomputes with no discount - "remove" is simply "preview again
/// without a code," not a separate stateful operation, since nothing about
/// checkout is server-side session state until the booking itself is created.
/// </summary>
/// <param name="IdempotencyKey">
/// Task 241: only meaningful on POST /bookings (ignored by the POST
/// /bookings/summary preview, which never persists anything) - minted once
/// per checkout attempt on the summary screen and replayed unchanged on any
/// client-side retry of that same attempt (timeout, double-tap, a second open
/// tab). <see cref="IBookingService.CreateAsync"/> returns the
/// already-created booking instead of a new one when it recognises the key,
/// same pattern <see cref="Nestly.Application.Payments.CreatePaymentOrderRequest"/>
/// already established for POST /payments/orders. Optional - a caller that
/// omits it (e.g. RecurringBookingSchedulerService) simply gets no dedup
/// protection, same as an ordinary create today.
/// </param>
/// <param name="ApplyWalletCredit">
/// Task 310 (SRS 11.7.2). A boolean toggle rather than a customer-specified
/// amount, matching the "applied automatically" promise the /wallet page
/// already makes - opting in applies as much of the available balance as the
/// booking can absorb (see <see cref="WalletCreditSummaryResponse.AppliedAmount"/>),
/// never a partial amount the customer has to type in. Applied last, against
/// whatever remains payable after any coupon/subscription discount.
/// </param>
/// <param name="ServiceVariantId">
/// Phase 3 catalog redesign: null for a service with no variants - the
/// service's flat price applies exactly as before this field existed. When
/// set, must belong to <paramref name="ServiceId"/> and be active (validated
/// by <see cref="Pricing.IPriceCalculationService"/>).
/// </param>
public record BookingSummaryRequest(
    Guid ServiceId,
    Guid CityId,
    Guid AddressId,
    Guid LocalityId,
    Guid SlotWindowId,
    DateOnly SlotDate,
    int Quantity,
    IReadOnlyList<AddOnSelection> AddOns,
    string? CouponCode = null,
    string? IdempotencyKey = null,
    bool ApplyWalletCredit = false,
    Guid? ServiceVariantId = null);

/// <summary>
/// The <c>Variant*</c> fields are null when no variant was selected (Phase 3
/// catalog redesign) - the service's flat price/duration applied instead.
/// <c>Group*</c> is null when the service isn't assigned to a
/// <see cref="Catalog.ServiceGroupSummaryResponse"/> section (Appliance/
/// Service Group catalog redesign) - inherited from the service, never a
/// customer selection.
/// </summary>
public record BookingServiceSummary(
    Guid Id, string Name, string Slug,
    Guid? VariantId = null, string? VariantName = null, int? VariantDurationMinutes = null,
    Guid? GroupId = null, string? GroupName = null,
    // Carried so the booking can snapshot the provider-commitment at creation
    // (duration + whether the service is time-based) - see Booking's
    // ServiceDurationMinutesSnapshot / IsDurationBasedSnapshot.
    int DurationMinutes = 0, bool IsDurationBased = false);

public record BookingAddressSummary(
    Guid Id,
    string Label,
    string Line1,
    string? Line2,
    string? Landmark,
    string Pincode,
    string City,
    string State,
    decimal Latitude,
    decimal Longitude,
    string ContactName,
    string ContactMobile);

public record BookingSlotSummary(Guid SlotWindowId, string Name, DateOnly Date, TimeSpan StartTime, TimeSpan EndTime);

/// <summary>
/// Booking summary data (SRS 11.7.2). <paramref name="Coupon"/>
/// is null when no coupon was applied (or removed); when present,
/// <paramref name="FinalPayable"/> is <c>Price.TotalPayable - Coupon.DiscountAmount</c>
/// (SRS 14.1's formula subtracts the discount after tax/fees, not before) -
/// <see cref="PriceBreakdownResponse"/> itself is always the pre-discount
/// breakdown, so a caller can still show "was / now" pricing.
///
/// <paramref name="SubscriptionBenefit"/> (task 179) is populated
/// automatically from the customer's active subscription, if any - unlike
/// <paramref name="Coupon"/> it needs no code from the caller. A coupon
/// always takes precedence: <paramref name="SubscriptionBenefit"/> is only
/// ever populated when <paramref name="Coupon"/> is null, so the two never
/// stack on the same booking.
///
/// <paramref name="Wallet"/> (task 310) is always populated - its Balance is
/// surfaced whether or not the customer opted in, and its AppliedAmount folds
/// into <paramref name="FinalPayable"/> last, after both the coupon/subscription
/// discount above: unlike those two, wallet credit stacks with either.
/// </summary>
public record BookingSummaryResponse(
    BookingServiceSummary Service,
    IReadOnlyList<ServiceAddOnSummaryResponse> AddOns,
    BookingAddressSummary Address,
    BookingSlotSummary Slot,
    PriceBreakdownResponse Price,
    string? CancellationPolicy,
    string? ReschedulePolicy,
    CouponSummaryResponse? Coupon,
    decimal FinalPayable,
    SubscriptionBenefitSummary? SubscriptionBenefit,
    WalletCreditSummaryResponse Wallet);

/// <summary>
/// One entry in a booking's status timeline (SRS 11.13, task 60c), mirroring
/// <see cref="Nestly.Domain.BookingStatusHistory"/> with the customer-visible
/// label attached so the frontend never has to know the mapping itself.
/// </summary>
public record BookingStatusTimelineEntry(BookingStatus? FromStatus, BookingStatus ToStatus, string ToStatusLabel, string? Reason, DateTime ChangedAtUtc);

/// <summary>
/// Who is coming (task 275). Identity only - the three things a customer
/// needs to recognise the person at the door - and deliberately not the
/// provider's id, phone, email or status: this rides on a general booking
/// read, and a general read is the wrong place to widen PII exposure.
///
/// <paramref name="PhotoUrl"/> and <paramref name="Rating"/> are both real
/// values since task 293, and both stay nullable because both are genuinely
/// absent for ordinary reasons (see <see cref="From"/>). The shape is
/// unchanged from the one task 275 fixed, so nothing moved under the
/// frontends: they already render a null photo as the placeholder avatar and
/// a null rating as no stars.
/// </summary>
public record BookingProviderSummary(string DisplayName, string? PhotoUrl, double? Rating)
{
    /// <summary>
    /// The single mapping from the aggregate, shared by the booking detail
    /// and the live tracking response so the two can never disagree about
    /// who the provider is.
    ///
    /// Task 293 closed the two gaps that used to force both optional fields
    /// to a hardcoded null here:
    ///
    /// <list type="bullet">
    /// <item><see cref="Domain.Provider"/> now carries a photo, and this
    /// reads <see cref="Domain.Provider.PublicPhotoUrl"/> rather than the raw
    /// column - a photo awaiting moderation or already rejected must not
    /// reach a customer's screen, and that gate lives on the entity so no
    /// mapper can forget it.</item>
    /// <item><see cref="Domain.Review"/> is now provider-scoped, so the
    /// rating passed in is computed over reviews written about THIS person
    /// (<c>IReviewRepository.GetProviderRatingAsync</c>) rather than inferred
    /// from the services they happened to work on. The caller supplies it
    /// instead of this mapper fetching it, because a mapper that queries is a
    /// mapper that queries once per row.</item>
    /// </list>
    ///
    /// Still null in the honest cases, and callers must keep rendering them:
    /// a provider who has not set a photo, one whose photo has not been
    /// approved yet, and one with no visible reviews yet - which is every
    /// brand-new professional, and must read as "no rating" rather than a bad
    /// one.
    /// </summary>
    public static BookingProviderSummary From(Provider provider, ProviderRatingSummary? rating) =>
        new(provider.DisplayName, provider.PublicPhotoUrl, rating?.AverageRating);
}

/// <summary>
/// Full booking detail (SRS 11.13, task 60c). <paramref name="CouponCode"/>/
/// <paramref name="CouponDiscountAmount"/> mirror the booking's own
/// snapshot (immutable once created, unlike the live re-validated preview
/// on <see cref="BookingSummaryResponse"/>). <paramref name="FinalPayable"/>
/// and <c>Price.TotalPayable</c> are equal here and both already
/// coupon-adjusted - unlike the pre-booking preview, a persisted booking has
/// only one "amount actually charged" (<see cref="Domain.Booking.TotalPayableSnapshot"/>),
/// not a separate pre-/post-discount pair. <paramref name="ProviderAssignmentStatus"/>
/// is null until a provider is assigned, then tracks the live
/// <see cref="Domain.BookingProviderAssignment"/> row (task 208) - <see cref="Domain.BookingStatus"/>
/// itself stays "Assigned" through both the offer and the provider's accept,
/// so this is how the customer sees an accept the moment it happens rather
/// than only once the provider starts the job. <paramref name="Provider"/>
/// (task 275) answers the question the status alone cannot - WHO is coming -
/// and is populated off the same live assignment, so it appears and
/// disappears with <paramref name="ProviderAssignmentStatus"/>. Appended
/// last: this is a positional record, and inserting a parameter mid-list
/// would silently re-bind every argument after it at the one call site that
/// builds it.
/// </summary>
public record BookingDetailResponse(
    Guid Id,
    BookingServiceSummary Service,
    IReadOnlyList<ServiceAddOnSummaryResponse> AddOns,
    BookingAddressSummary Address,
    BookingSlotSummary Slot,
    PriceBreakdownResponse Price,
    BookingStatus Status,
    string StatusLabel,
    IReadOnlyList<BookingStatusTimelineEntry> Timeline,
    DateTime CreatedAtUtc,
    string? CouponCode,
    decimal? CouponDiscountAmount,
    decimal FinalPayable,
    BookingProviderAssignmentStatus? ProviderAssignmentStatus,
    BookingProviderSummary? Provider,
    // Wallet balance applied at checkout (task 310), mirroring
    // Domain.Booking.WalletCreditAppliedSnapshot. Null when none was applied.
    decimal? WalletCreditApplied = null,
    // Short human-facing code ("GLX-260825-K7F3M") - see Booking.BookingReference's
    // doc comment. What the customer reads/quotes, never the GUID in Id.
    string Reference = "");

/// <summary>A row in the booking list (SRS 11.13, task 60b) - a lighter shape than the detail, for a list screen.</summary>
public record BookingListItemResponse(
    Guid Id,
    string ServiceName,
    DateOnly SlotDate,
    decimal TotalPayable,
    BookingStatus Status,
    string StatusLabel,
    DateTime CreatedAtUtc,
    // Short human-facing code ("GLX-260825-K7F3M") - see Booking.BookingReference's
    // doc comment. Appended last: this is a positional record.
    string Reference);

/// <summary>A page of the customer's own booking list, newest first, plus the total match count for that bucket - the same Items/TotalCount/Page/PageSize shape the admin booking search already uses.</summary>
public record BookingListResponse(IReadOnlyList<BookingListItemResponse> Items, int TotalCount, int Page, int PageSize);
