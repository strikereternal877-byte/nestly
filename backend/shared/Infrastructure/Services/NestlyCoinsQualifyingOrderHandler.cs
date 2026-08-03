using MediatR;
using Nestly.Application.Bookings;
using Nestly.Application.NestlyCoins;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Task 201 (docs/NESTLY-COINS.md "HOW IT WORKS"): when a booking reaches
/// Completed, credits Nestly Coins to the customer and (if one was
/// assigned) the provider, same timing as a completed job's earning credit.
/// Independent handler on BookingStatusChangedEvent, same shape as
/// EscrowReleaseOnCompletionHandler/ReferralQualifyingBookingHandler - each
/// concern reacts to the event on its own rather than any of them knowing
/// about the others.
/// </summary>
public sealed class NestlyCoinsQualifyingOrderHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly INestlyCoinsService _coinsService;

    public NestlyCoinsQualifyingOrderHandler(IBookingRepository bookingRepository, INestlyCoinsService coinsService)
    {
        _bookingRepository = bookingRepository;
        _coinsService = coinsService;
    }

    public async Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        if (domainEvent.ToStatus != BookingStatus.Completed)
        {
            return;
        }

        Booking? booking = await _bookingRepository.GetByIdAsync(domainEvent.BookingId);
        if (booking is null)
        {
            return;
        }

        await _coinsService.CreditCustomerCoinsAsync(booking);
        await _coinsService.CreditProviderCoinsAsync(booking);
    }
}
