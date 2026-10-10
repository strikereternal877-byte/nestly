using MediatR;
using Microsoft.Extensions.Logging;
using Nestly.Application.Bookings;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Task 164 (REFERRAL.md): when a booking reaches Completed, checks for a
/// pending referral keyed by the customer (the referee) and, if the
/// booking's amount meets the configured minimum, marks it Qualified and
/// immediately disburses the reward - REFERRAL.md's DECISIONS section is
/// explicit both sides are rewarded on the same event as qualification, not
/// a separately scheduled step. A third independent handler on
/// BookingStatusChangedEvent, same shape as EscrowReleaseOnCompletionHandler
/// and BookingNotificationTriggerHandler - each concern reacts to the event
/// on its own rather than any of them knowing about the others.
/// </summary>
public sealed class ReferralQualifyingBookingHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IReferralRepository _referralRepository;
    private readonly IReferralRewardService _rewardService;
    private readonly ILogger<ReferralQualifyingBookingHandler> _logger;

    public ReferralQualifyingBookingHandler(
        IBookingRepository bookingRepository,
        IReferralRepository referralRepository,
        IReferralRewardService rewardService,
        ILogger<ReferralQualifyingBookingHandler> logger)
    {
        _bookingRepository = bookingRepository;
        _referralRepository = referralRepository;
        _rewardService = rewardService;
        _logger = logger;
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
            _logger.LogWarning("Referral qualification check found no booking for id {BookingId}.", domainEvent.BookingId);
            return;
        }

        Referral? referral = await _referralRepository.GetByRefereeCustomerIdAsync(booking.CustomerId);
        if (referral is null || referral.Status != ReferralStatus.Registered)
        {
            // No referral, or already qualified/rewarded/expired/flagged -
            // REFERRAL.md's loop only ever fires once per referee, on their
            // FIRST qualifying booking (see task 164's own description);
            // a second completed booking from the same referee is a no-op.
            return;
        }

        if (booking.TotalPayableSnapshot < referral.MinQualifyingOrderAmount)
        {
            // Not an error - the referral simply stays Registered and can
            // still qualify from a later booking, until task 175's expiry
            // sweep closes it out.
            return;
        }

        // Two of this referee's bookings completing near-simultaneously can
        // both reach this point with their own independently-loaded
        // Registered referral - TryMarkQualifiedAsync's atomic conditional
        // UPDATE, not the read above, is what actually decides which one (if
        // either) gets to disburse. The loser must stop here: DisburseAsync
        // issues a real wallet credit/coupon, so calling it twice for one
        // referral is a real duplicate payout, not a harmless retry.
        bool wonRace = await _referralRepository.TryMarkQualifiedAsync(referral.Id, booking.Id);
        if (!wonRace)
        {
            return;
        }

        referral.MarkQualified(booking.Id);
        await _rewardService.DisburseAsync(referral);
    }
}
