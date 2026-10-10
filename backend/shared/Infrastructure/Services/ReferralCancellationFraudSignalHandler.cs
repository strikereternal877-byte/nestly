using MediatR;
using Microsoft.Extensions.Logging;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Persistence.Interceptors;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Task 166's one structurally-detectable fraud signal (REFERRAL.md "FRAUD
/// / ABUSE PREVENTION" - "a qualifying booking cancelled right after
/// reward"). The other listed signal ("same device/payment method as the
/// referrer") has no data source anywhere in this codebase today - no
/// device fingerprinting or payment-method-on-customer field exists, so it
/// is honestly not implemented rather than faked. A fourth independent
/// handler on BookingStatusChangedEvent, same shape as
/// EscrowReleaseOnCompletionHandler/BookingNotificationTriggerHandler/
/// ReferralQualifyingBookingHandler.
///
/// <see cref="BookingStatus.Refunded"/> is included alongside the two
/// Cancelled statuses, mirroring <c>NestlyCoinsClawbackHandler</c>'s
/// identical trigger set - a qualifying booking only ever reaches
/// ReferralQualifyingBookingHandler.DisburseAsync once it is already
/// Completed, and BookingLifecycle allows a Completed booking only one way
/// out: Completed -> RefundPending -> Refunded (never back to either
/// Cancelled status - see BookingLifecycle's own transition table). Without
/// Refunded here, the one signal this handler exists to raise could
/// structurally never fire for the exact case REFERRAL.md wrote it for: a
/// dispute or admin refund reversing an already-rewarded referral's
/// qualifying order.
/// </summary>
public sealed class ReferralCancellationFraudSignalHandler : INotificationHandler<DomainEventNotification<BookingStatusChangedEvent>>
{
    // A cancellation this soon after the reward landed is what REFERRAL.md
    // calls "right after reward" - long enough to allow a legitimate
    // same-day cancellation for an unrelated reason, short enough that a
    // cancellation months later (unrelated to the referral) doesn't get
    // flagged on a stale signal.
    private static readonly TimeSpan SuspiciousWindow = TimeSpan.FromDays(2);

    private readonly IReferralRepository _referralRepository;
    private readonly IReferralFraudReviewService _fraudReviewService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReferralCancellationFraudSignalHandler> _logger;

    public ReferralCancellationFraudSignalHandler(
        IReferralRepository referralRepository,
        IReferralFraudReviewService fraudReviewService,
        TimeProvider timeProvider,
        ILogger<ReferralCancellationFraudSignalHandler> logger)
    {
        _referralRepository = referralRepository;
        _fraudReviewService = fraudReviewService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<BookingStatusChangedEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        if (domainEvent.ToStatus is not (BookingStatus.CancelledByCustomer or BookingStatus.CancelledByAdmin or BookingStatus.Refunded))
        {
            return;
        }

        Domain.Referral? referral = await _referralRepository.GetByQualifyingBookingIdAsync(domainEvent.BookingId);
        if (referral is null || referral.Status != ReferralStatus.Rewarded || referral.RewardedAtUtc is null)
        {
            return;
        }

        var sinceReward = _timeProvider.GetUtcNow().UtcDateTime - referral.RewardedAtUtc.Value;
        if (sinceReward > SuspiciousWindow)
        {
            return;
        }

        var result = await _fraudReviewService.FlagAsync(
            referral.Id,
            adminUserId: null,
            note: $"System: qualifying booking reached {domainEvent.ToStatus} {sinceReward.TotalHours:0.0}h after reward.");

        if (result.IsFailure)
        {
            _logger.LogWarning("Could not fraud-flag referral {ReferralId}: {Error}", referral.Id, result.Error.Message);
        }
    }
}
