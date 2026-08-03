namespace Nestly.Domain;

/// <summary>Notification trigger events (SRS 19.1, tasks 87a, 88a-g). OTP is deliberately absent - it already sends through <see cref="INotificationProvider"/> directly via <c>OtpService</c> and predates this event-log framework.</summary>
public enum NotificationEventType
{
    Welcome,
    BookingConfirmed,
    PaymentSuccess,
    PaymentFailed,
    BookingCancelled,
    BookingRescheduled,
    RefundProcessed,
    SupportTicketUpdate,

    /// <summary>A referrer's shared code/link was used at registration (REFERRAL.md, task 172). Sent to the referrer only.</summary>
    ReferralRegistered,

    /// <summary>A referral reward (wallet credit or coupon) was disbursed (REFERRAL.md, task 172). Sent to both referrer and referee - dispatched once per recipient, same event type.</summary>
    ReferralRewardCredited,

    /// <summary>Sent ahead of a recurring plan's next occurrence, at the scheduler's lead time (PRODUCT-ENHANCEMENTS.md section 2, task 188) - either confirming the upcoming visit after a successful booking, or as a heads-up before the attempt. See <see cref="RecurringBookingSkipped"/> for the failure case.</summary>
    RecurringBookingUpcoming,

    /// <summary>A recurring plan's occurrence was skipped because the slot/address was no longer available, or the booking orchestration otherwise rejected the attempt (task 185, task 188). This is the "does not silently fail" notification PRODUCT-ENHANCEMENTS.md section 2 requires.</summary>
    RecurringBookingSkipped,

    /// <summary>
    /// A chat message arrived while the recipient had no live SignalR
    /// connection (PRODUCT-ENHANCEMENTS.md IN-APP CHAT, task 194). Only ever
    /// dispatched for the customer side of a thread today - see
    /// <c>ChatNotificationTriggerHandler</c>'s doc comment for the documented
    /// scope gap on the admin/provider side.
    /// </summary>
    NewChatMessage,

    /// <summary>A subscription's recurring charge succeeded and it rolled to its next billing period (PRODUCT-ENHANCEMENTS.md #1, task 183).</summary>
    SubscriptionRenewed,

    /// <summary>A subscription's next billing attempt is within the reminder window (PRODUCT-ENHANCEMENTS.md #1, task 183).</summary>
    SubscriptionExpiringSoon,

    /// <summary>A subscription's recurring charge failed - either a recoverable suspension still retrying, or the terminal expiry once retries are exhausted (PRODUCT-ENHANCEMENTS.md #1, task 183).</summary>
    SubscriptionPaymentFailed
}
