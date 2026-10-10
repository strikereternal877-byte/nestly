using MediatR;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.Payments;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// The wallet-funded counterpart to <see cref="PaymentWebhookService"/>'s own
/// commission-recording and escrow hold (task 157-158) - fires the moment a
/// booking reaches <see cref="BookingStatus.Confirmed"/>, exactly like
/// <see cref="EscrowReleaseOnCompletionHandler"/> fires on Completed, so it
/// covers both ways a booking reaches Confirmed uniformly: a real gateway
/// payment (<see cref="PaymentWebhookService.ApplySuccessfulPaymentAsync"/>)
/// and a booking with nothing left to pay after wallet credit covered the
/// remainder (<c>BookingService.CreateAsync</c>'s zero-payable branch,
/// task 331), which never goes through that method at all.
///
/// Without this, only the gateway-collected share of a booking's price ever
/// entered escrow or counted toward commission - the wallet-funded share
/// had no <see cref="PaymentTransaction"/> of its own to record either on,
/// so a provider who completed a partly-or-fully wallet-funded job was paid
/// only for whatever slice the customer's card covered, or nothing at all
/// if the wallet covered everything. See
/// <see cref="Booking.WalletCreditCommissionAmountSnapshot"/>'s doc comment.
/// </summary>
public sealed class WalletCreditEscrowHoldOnConfirmationHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly ICommissionService _commissionService;
    private readonly IEscrowService _escrowService;

    public WalletCreditEscrowHoldOnConfirmationHandler(
        IBookingRepository bookingRepository,
        IServiceRepository serviceRepository,
        ICommissionService commissionService,
        IEscrowService escrowService)
    {
        _bookingRepository = bookingRepository;
        _serviceRepository = serviceRepository;
        _commissionService = commissionService;
        _escrowService = escrowService;
    }

    public async Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        if (domainEvent.ToStatus != BookingStatus.Confirmed)
        {
            return;
        }

        var booking = await _bookingRepository.GetByIdAsync(domainEvent.BookingId);
        if (booking?.WalletCreditAppliedSnapshot is not { } walletAmount || walletAmount <= 0)
        {
            // No wallet credit on this booking - gateway-only, or an AMC/
            // 100%-off booking with genuinely nothing collected by any
            // means. Nothing for this handler to do either way.
            return;
        }

        Guid? categoryId = null;
        var firstItem = booking.Items.FirstOrDefault();
        if (firstItem is not null)
        {
            var service = await _serviceRepository.GetByIdAsync(firstItem.ServiceId);
            categoryId = service?.CategoryId;
        }

        var commission = _commissionService.Calculate(walletAmount, categoryId);
        booking.RecordWalletCreditCommission(commission.CommissionAmount);
        await _bookingRepository.UpdateAsync(booking);

        await _escrowService.HoldWalletCreditAsync(booking.Id, walletAmount);
    }
}
