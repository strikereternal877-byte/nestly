using MediatR;
using Microsoft.Extensions.Logging;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Payments;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Releases a booking's held escrow to its provider as soon as it reaches
/// <see cref="BookingStatus.Completed"/> (task 158) - the counterpart to
/// <see cref="PaymentWebhookService"/> moving the payment into escrow at
/// confirmation time. Wired the same way <c>CatalogCacheInvalidationHandler</c>
/// reacts to catalog events (task 49): whatever code eventually drives a
/// booking to Completed (the provider-facing "complete job" action, task
/// 149a) gets this settlement for free, without that flow needing to know
/// escrow/earnings bookkeeping exists.
///
/// A Provider identity now exists (task 147's <see cref="Booking.AssignedProviderId"/>),
/// so <see cref="IEscrowService.ReleaseToProviderAsync"/>'s ProviderId
/// placeholder is filled in here, and the same release also credits that
/// provider's earning ledger (task 148, "credit per completed job") with the
/// net amount released - the automatic-crediting hook
/// <c>IProviderEarningLedgerService</c>'s doc comment anticipated once a
/// provider-facing complete-job flow existed.
/// </summary>
public sealed class EscrowReleaseOnCompletionHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IEscrowService _escrowService;
    private readonly IProviderEarningLedgerService _earningLedgerService;
    private readonly ILogger<EscrowReleaseOnCompletionHandler> _logger;

    public EscrowReleaseOnCompletionHandler(
        IPaymentTransactionRepository paymentRepository,
        IBookingRepository bookingRepository,
        IEscrowService escrowService,
        IProviderEarningLedgerService earningLedgerService,
        ILogger<EscrowReleaseOnCompletionHandler> logger)
    {
        _paymentRepository = paymentRepository;
        _bookingRepository = bookingRepository;
        _escrowService = escrowService;
        _earningLedgerService = earningLedgerService;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        if (domainEvent.ToStatus != BookingStatus.Completed)
        {
            return;
        }

        var transaction = await _paymentRepository.GetByBookingIdAsync(domainEvent.BookingId);
        if (transaction is null || transaction.Status != PaymentTransactionStatus.Success || transaction.CommissionAmount is null)
        {
            // Data-integrity gap, not a business outcome - a booking cannot
            // legally reach Completed without having gone through Confirmed
            // first, which is exactly where the transaction/commission are
            // recorded. Logged rather than thrown: this runs as a fire-and-
            // forget domain event handler, not inside the caller's own unit
            // of work.
            _logger.LogWarning(
                "Booking {BookingId} reached Completed with no successful, commission-recorded payment transaction - skipping escrow release.",
                domainEvent.BookingId);
            return;
        }

        var booking = await _bookingRepository.GetByIdAsync(domainEvent.BookingId);
        var providerId = booking?.AssignedProviderId;

        var release = await _escrowService.ReleaseToProviderAsync(
            domainEvent.BookingId, transaction.Id, providerId, transaction.CommissionAmount.Value);

        if (release is null || providerId is null)
        {
            // Nothing was actually released (already released/refunded), or
            // there is no provider on record for this booking (e.g. a
            // pre-Provider-module booking, or an admin force-completed it
            // without ever assigning one) - no earning to credit either way.
            return;
        }

        var creditResult = await _earningLedgerService.RecordAdjustmentAsync(
            providerId.Value,
            new RecordProviderEarningAdjustmentRequest(
                ProviderEarningEntryType.Credit,
                release.NetAmountToProvider,
                ProviderEarningSourceType.JobCompletion,
                domainEvent.BookingId,
                $"Job completed - booking {domainEvent.BookingId}."));

        if (creditResult.IsFailure)
        {
            // Escrow has already been released at this point - logged, not
            // thrown, for the same fire-and-forget reason as the warning
            // above; a failed credit here (e.g. the provider record was
            // deleted between assignment and completion) is a data-integrity
            // gap for an admin to reconcile manually, not a reason to fail
            // the booking-completion request itself.
            _logger.LogWarning(
                "Booking {BookingId} completed and escrow released, but crediting provider {ProviderId}'s earning ledger failed: {ErrorCode} {ErrorMessage}.",
                domainEvent.BookingId, providerId, creditResult.Error.Code, creditResult.Error.Message);
        }
    }
}
