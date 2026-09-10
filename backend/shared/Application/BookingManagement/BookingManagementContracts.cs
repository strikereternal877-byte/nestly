using Nestly.Domain;

namespace Nestly.Application.BookingManagement;

// ---- List (SRS 12.11.1, task 115a) ----

/// <summary>Admin booking-list request. Mirrors <see cref="Bookings.BookingSearchFilter"/> - see its doc comment for the two SRS filters deliberately omitted.</summary>
public sealed record AdminBookingSearchRequest(
    Guid? BookingId,
    string? CustomerName,
    string? CustomerMobile,
    BookingStatus? Status,
    string? City,
    DateOnly? SlotDateFrom,
    DateOnly? SlotDateTo,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    Guid? ServiceId,
    Guid? CategoryId,
    string? CouponCode,
    int Page = 1,
    int PageSize = 20,
    // Short human-facing code ("GLX-260825-K7F3M") or any substring of one -
    // see Booking.BookingReference's doc comment. Separate from BookingId
    // (exact GUID match) rather than replacing it: existing API callers that
    // already search by GUID keep working unchanged.
    string? Reference = null);

/// <summary>One row of the admin booking list - a lighter shape than the detail (SRS 12.11.1).</summary>
public sealed record AdminBookingListItemResponse(
    Guid Id,
    string CustomerName,
    string CustomerMobile,
    string ServiceName,
    string City,
    DateOnly SlotDate,
    BookingStatus Status,
    string StatusLabel,
    decimal TotalPayable,
    string? CouponCode,
    DateTime CreatedAtUtc,
    // Short human-facing code ("GLX-260825-K7F3M") - see Booking.BookingReference's
    // doc comment. Appended last: this is a positional record.
    string Reference);

public sealed record AdminBookingSearchResponse(IReadOnlyList<AdminBookingListItemResponse> Items, int TotalCount, int Page, int PageSize);

// ---- Detail (SRS 12.11.2, tasks 115b-115c) ----

public sealed record AdminBookingCustomerSnapshot(Guid CustomerId, string Name, string Mobile);

public sealed record AdminBookingAddressSnapshot(
    string Label, string Line1, string? Line2, string? Landmark, string Pincode, string City, string State,
    string ContactName, string ContactMobile);

public sealed record AdminBookingSlotSnapshot(Guid SlotWindowId, DateOnly Date, string WindowName, TimeSpan StartTime, TimeSpan EndTime);

public sealed record AdminBookingAddOnResponse(Guid Id, Guid ServiceAddOnId, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record AdminBookingItemResponse(
    Guid Id, Guid ServiceId, string Name, decimal UnitPrice, int Quantity, decimal LineTotal, IReadOnlyList<AdminBookingAddOnResponse> AddOns);

public sealed record AdminBookingPriceResponse(
    decimal BasePrice, int Quantity, decimal BaseTotal, decimal AddOnTotal, decimal VisitCharge, decimal Subtotal,
    decimal TaxPercentage, decimal TaxAmount, decimal PlatformFee, decimal TotalPayable,
    string? CouponCode, decimal? CouponDiscountAmount, decimal FinalPayable);

/// <summary>One entry in a booking's status timeline (SRS 12.11.2/12.11.3, task 115c), mirroring <see cref="Nestly.Domain.BookingStatusHistory"/>.</summary>
public sealed record AdminBookingStatusTimelineEntry(BookingStatus? FromStatus, BookingStatus ToStatus, string ToStatusLabel, string? Reason, DateTime ChangedAtUtc);

/// <summary>Payment summary for the booking's detail view (SRS 12.13.1).</summary>
public sealed record AdminBookingPaymentSummary(
    Guid Id, PaymentTransactionStatus Status, decimal Amount, string Currency, string? GatewayPaymentRef, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record AdminBookingCancellationResponse(
    Guid Id, CancellationActor Actor, string Reason, bool WithinFreeCancellationWindow,
    decimal CancellationFeeAmount, decimal RefundAmount, RefundMethod? RefundMethod, Guid? RefundTransactionId,
    string? InternalNotes, DateTime CreatedAtUtc);

public sealed record AdminBookingRescheduleResponse(
    Guid Id, RescheduleActor Actor, string? Reason,
    DateOnly FromSlotDate, TimeSpan FromSlotStartTime, DateOnly ToSlotDate, TimeSpan ToSlotStartTime,
    bool IsLate, decimal FeeAmount, DateTime CreatedAtUtc);

/// <summary>
/// One refund settlement on the booking. <paramref name="FundingSource"/>
/// (task 356) is what <paramref name="Method"/> cannot say on its own: a
/// Wallet method is either the customer's own checkout balance coming back or
/// a gateway payment handed back as credit, and reconciliation needs to tell
/// those two apart.
/// </summary>
public sealed record AdminBookingRefundResponse(
    Guid Id, RefundFundingSource FundingSource, RefundType Type, RefundMethod Method, decimal Amount, RefundStatus Status,
    string? GatewayRefundRef, string Reason, DateTime CreatedAtUtc, DateTime? ProcessedAtUtc);

/// <summary>
/// Full admin booking detail (SRS 12.11.2). Deliberately does not include
/// linked support tickets, free-form internal notes, or an embedded audit
/// summary - those SRS 12.11.2 bullets need domain concepts (a
/// booking-scoped note entity; a ticket-to-booking join, which belongs to the
/// separate Support vertical) this task's scope (115a-117c) does not cover;
/// the existing audit-log-viewer (task 130, SRS 21, <c>AuditLogController</c>)
/// remains the source for a booking's audit trail in the meantime, filtered
/// by EntityName="Booking".
/// </summary>
public sealed record AdminBookingDetailResponse(
    Guid Id,
    AdminBookingCustomerSnapshot Customer,
    AdminBookingAddressSnapshot Address,
    AdminBookingSlotSnapshot Slot,
    IReadOnlyList<AdminBookingItemResponse> Items,
    AdminBookingPriceResponse Price,
    BookingStatus Status,
    string StatusLabel,
    IReadOnlyList<AdminBookingStatusTimelineEntry> Timeline,
    AdminBookingPaymentSummary? Payment,
    AdminBookingCancellationResponse? Cancellation,
    IReadOnlyList<AdminBookingRescheduleResponse> Reschedules,
    IReadOnlyList<AdminBookingRefundResponse> Refunds,
    DateTime CreatedAtUtc,
    // Short human-facing code ("GLX-260825-K7F3M") - see Booking.BookingReference's
    // doc comment. Appended last: this is a positional record.
    string Reference);

// ---- Actions (SRS 12.11.3, tasks 115d, 117a-c) ----

/// <summary>
/// General admin-driven status transition (task 115d). Restricted to
/// operational statuses - <see cref="BookingStatus.CancelledByCustomer"/>,
/// <see cref="BookingStatus.CancelledByAdmin"/>, <see cref="BookingStatus.Rescheduled"/>,
/// <see cref="BookingStatus.RefundPending"/> and <see cref="BookingStatus.Refunded"/>
/// are rejected here (see <c>BookingManagementService.DisallowedGenericTransitionTargets</c>)
/// - those five must go through the dedicated cancel/reschedule/refund
/// actions below, which also raise the correct history rows and refunds;
/// letting this generic endpoint flip straight to them would silently skip
/// that bookkeeping while still passing <see cref="BookingLifecycle"/>'s own
/// transition check.
/// </summary>
public sealed record AdminBookingStatusUpdateRequest(BookingStatus NewStatus, string? Reason);

public sealed record AdminCancelBookingRequest(string Reason, string? InternalNotes);

public sealed record AdminRescheduleBookingRequest(Guid LocalityId, Guid SlotWindowId, DateOnly SlotDate, string? Reason);

/// <summary>
/// Admin refund request (SRS 12.11.3, 12.13.2-3, task 117c).
/// <paramref name="Amount"/> is required (and must be positive) for a
/// partial refund, and ignored for a full one - <see cref="IRefundService.InitiateFullRefundAsync"/>
/// always refunds whatever remains on the payment. Full and partial refunds
/// are both gated behind "bookings.write" - see <c>BookingsController</c>'s
/// doc comment for why this does not split into two permission tiers.
/// </summary>
public sealed record AdminRefundRequest(bool IsFullRefund, decimal? Amount, string Reason, RefundMethod Method);

/// <summary>
/// Admin manual/offline payment request (row 25, docs/OPEN-FIXES-FEATURES.csv) -
/// the only money-in action alongside the gateway/sandbox flow. Gated behind
/// "bookings.write", the same tier <see cref="AdminRefundRequest"/> uses, for
/// the same reason (see <c>BookingsController</c>'s doc comment).
/// </summary>
public sealed record AdminManualPaymentRequest(ManualPaymentMethod Method, string Reference);

// ---- Unassigned & at-risk queue (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
// Proposed new page, Unassigned and at-risk queue"): paid bookings that are
// in a status <c>BookingProviderAssignmentService.IsAssignableStatus</c>
// would accept an admin assignment for, but with no live provider on them
// yet - see <see cref="Bookings.IBookingRepository.ListUnassignedAtRiskAsync"/>. ----

/// <summary>Paging only - no filters, this is a small, fixed operational queue rather than a general search.</summary>
public sealed record AdminUnassignedAtRiskBookingRequest(int Page = 1, int PageSize = 20);

/// <summary>
/// One queue row. <paramref name="SlotDate"/>/<paramref name="SlotStartTime"/>
/// are returned raw rather than a precomputed "time until slot" - admin-web
/// renders and keeps that countdown fresh on its own (the response may sit in
/// the browser for a while on a page an ops user leaves open), the same
/// split the booking-tracking endpoints already use for ETAs.
/// </summary>
public sealed record AdminUnassignedAtRiskBookingResponse(
    Guid Id,
    string Reference,
    string CustomerName,
    string ServiceName,
    DateOnly SlotDate,
    TimeSpan SlotStartTime,
    string City,
    string Pincode,
    BookingStatus Status,
    string StatusLabel,
    DateTime CreatedAtUtc);

public sealed record AdminUnassignedAtRiskBookingSearchResponse(IReadOnlyList<AdminUnassignedAtRiskBookingResponse> Items, int TotalCount, int Page, int PageSize);

// ---- Fulfilment control room (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
// Proposed new page, Fulfilment control room"): a single day's operationally
// live bookings, flat - admin-web buckets these into status columns
// (Unassigned/Assigned/En route/In progress/Completed) and layers its own
// "overdue/at-risk" read on top from Status/AssignedProviderId/SlotStartTime,
// the same client-side urgency computation the unassigned-at-risk queue page
// already does, rather than this response baking in a server-computed "now"
// that would go stale the moment the admin's tab sits open. ----

/// <summary><paramref name="Date"/> defaults to the caller's local "today" when omitted - see <see cref="Bookings.IBookingRepository.ListForFulfilmentBoardAsync"/> for which statuses this returns.</summary>
public sealed record AdminFulfilmentBoardRequest(DateOnly? Date);

/// <summary>
/// One board card. <paramref name="SlotDate"/>/<paramref name="SlotStartTime"/>
/// are raw, like <see cref="AdminUnassignedAtRiskBookingResponse"/>'s -
/// admin-web derives and refreshes any "in 2h"/"overdue" label itself rather
/// than trusting a value computed once at request time.
/// </summary>
public sealed record AdminFulfilmentBoardBookingResponse(
    Guid Id,
    string Reference,
    string CustomerName,
    string ServiceName,
    DateOnly SlotDate,
    TimeSpan SlotStartTime,
    string City,
    string Pincode,
    BookingStatus Status,
    string StatusLabel,
    Guid? AssignedProviderId,
    string? AssignedProviderName,
    DateTime CreatedAtUtc);

public sealed record AdminFulfilmentBoardResponse(DateOnly Date, IReadOnlyList<AdminFulfilmentBoardBookingResponse> Items);
