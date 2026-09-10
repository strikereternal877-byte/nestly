using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.BookingManagement;

/// <summary>
/// Admin booking management (SRS 12.11, 12.13.2-3; tasks 115a-117c): the
/// filterable list, the full detail/timeline view, general operational
/// status updates, and the cancel/reschedule/refund actions. Every
/// cancellation/reschedule/refund is delegated to the existing domain
/// services (<c>ICancellationService</c>, <c>IRescheduleService</c>,
/// <c>IRefundService</c> - tasks 80c, 82d, 75d) rather than reimplementing
/// their policy math; this service composes those plus <c>IBookingRepository</c>
/// and writes one audit entry per action via <c>IAuditLogWriter</c>.
/// </summary>
public interface IBookingManagementService
{
    /// <summary>Filterable, paginated booking search (SRS 12.11.1, task 115a).</summary>
    Task<Result<AdminBookingSearchResponse>> SearchAsync(AdminBookingSearchRequest request);

    /// <summary>Full detail: snapshots, status timeline, payment, cancellation/reschedule/refund history (SRS 12.11.2, tasks 115b-115c).</summary>
    Task<Result<AdminBookingDetailResponse>> GetDetailAsync(Guid bookingId);

    /// <summary>General operational status transition (SRS 12.11.3, task 115d) - see <see cref="AdminBookingStatusUpdateRequest"/> for the restricted target-status set.</summary>
    Task<Result<AdminBookingDetailResponse>> UpdateStatusAsync(Guid bookingId, Guid adminUserId, AdminBookingStatusUpdateRequest request);

    /// <summary>Admin-initiated cancellation (SRS 12.11.3, task 117a) via <c>ICancellationService.AdminCancelAsync</c>.</summary>
    Task<Result<AdminBookingDetailResponse>> CancelAsync(Guid bookingId, Guid adminUserId, AdminCancelBookingRequest request);

    /// <summary>Admin-initiated reschedule (SRS 12.11.3, task 117b) via <c>IRescheduleService.AdminRescheduleAsync</c>.</summary>
    Task<Result<AdminBookingDetailResponse>> RescheduleAsync(Guid bookingId, Guid adminUserId, AdminRescheduleBookingRequest request);

    /// <summary>Full or partial refund (SRS 12.11.3, 12.13.2-3, task 117c) via <c>IRefundService</c>.</summary>
    Task<Result<AdminBookingDetailResponse>> RefundAsync(Guid bookingId, Guid adminUserId, AdminRefundRequest request);

    /// <summary>
    /// Records a manual/offline payment (row 25, docs/OPEN-FIXES-FEATURES.csv)
    /// via <c>IPaymentWebhookService.RecordManualPaymentAsync</c>, which
    /// applies the same success transition a gateway payment does.
    /// </summary>
    Task<Result<AdminBookingDetailResponse>> RecordManualPaymentAsync(Guid bookingId, Guid adminUserId, AdminManualPaymentRequest request);

    /// <summary>
    /// Row "Unassigned and at-risk queue", docs/OPEN-FIXES-FEATURES.csv: paid,
    /// assignable bookings with no live provider, soonest slot first. See
    /// <see cref="Bookings.IBookingRepository.ListUnassignedAtRiskAsync"/>.
    /// </summary>
    Task<Result<AdminUnassignedAtRiskBookingSearchResponse>> ListUnassignedAtRiskAsync(AdminUnassignedAtRiskBookingRequest request);

    /// <summary>
    /// Row "Fulfilment control room", docs/OPEN-FIXES-FEATURES.csv: every
    /// operationally live booking for one day, flat, for admin-web's kanban
    /// board. See <see cref="Bookings.IBookingRepository.ListForFulfilmentBoardAsync"/>.
    /// </summary>
    Task<Result<AdminFulfilmentBoardResponse>> GetFulfilmentBoardAsync(AdminFulfilmentBoardRequest request);
}
