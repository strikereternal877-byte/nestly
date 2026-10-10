using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>Strongly typed binding of the "RecurringBookings" configuration section (task 185).</summary>
public class RecurringBookingOptions
{
    public const string SectionName = "RecurringBookings";

    /// <summary>
    /// How many days ahead of a scheduled occurrence the job attempts it.
    /// PRODUCT-ENHANCEMENTS.md section 2 requires "enough lead time to catch
    /// and surface a problem before the customer expects the visit" without
    /// naming a number - 3 days is chosen so a skip-and-notify reaches the
    /// customer with real time to react (rebook manually, or simply expect
    /// the plan to continue on its next date), while staying close enough to
    /// the visit that the address/catalog state the orchestration validates
    /// is still representative of what the customer will actually get.
    /// </summary>
    [Range(1, 30)]
    public int LeadTimeDays { get; set; } = 3;

    /// <summary>
    /// How long a recurring-generated occurrence may sit in
    /// <see cref="BookingStatus.PaymentPending"/> before
    /// <c>BookingExpirySweepJob</c> expires it - separate from, and much
    /// longer than, <see cref="BookingExpiryOptions.ExpiryMinutes"/>.
    ///
    /// <para>
    /// <see cref="BookingExpiryOptions.ExpiryMinutes"/> was sized (task 242)
    /// for a customer who is actively on a checkout screen mid-payment - "long
    /// enough to cover a slow gateway redirect... short enough that an
    /// abandoned seat is back in the pool the same booking session." A
    /// recurring occurrence is created unattended by
    /// <c>RecurringBookingSchedulerService</c>, <see cref="LeadTimeDays"/>
    /// ahead of the visit, with nobody watching a checkout screen at all -
    /// applying the 20-minute window to it meant almost every gateway-funded
    /// recurring occurrence expired before the customer ever saw a payment
    /// prompt, silently dropping a visit the plan had promised.
    /// </para>
    ///
    /// <para>
    /// 24 hours gives the customer a full day to see the "payment due"
    /// notification (<see cref="Nestly.Domain.NotificationEventType.RecurringBookingPaymentDue"/>)
    /// and act on it, while still leaving at least
    /// <c>LeadTimeDays - 1</c> day(s) of buffer before the actual visit for
    /// the slot to be released and, if needed, re-booked or escalated -
    /// mirroring why <see cref="LeadTimeDays"/> itself defaults to 3 rather
    /// than 1.
    /// </para>
    /// </summary>
    [Range(1, 240)]
    public int PaymentWindowHours { get; set; } = 24;

    /// <summary>
    /// How long <c>RecurringOccurrenceAutoChargeJob</c> waits after an
    /// auto-charge-enabled occurrence is created before making its first
    /// charge attempt. Not zero: the customer is sent
    /// <see cref="Nestly.Domain.NotificationEventType.RecurringAutoChargeScheduled"/>
    /// at creation time precisely so they have a real window to see it and
    /// react (turn auto-charge off, cancel the occurrence's plan) before any
    /// money moves - charging immediately would make that notice a receipt,
    /// not a heads-up.
    /// </summary>
    [Range(0, 72)]
    public int AutoChargeInitialDelayHours { get; set; } = 2;

    /// <summary>
    /// How many consecutive failed auto-charge attempts an occurrence
    /// tolerates before <c>RecurringOccurrenceAutoChargeJob</c> stops
    /// retrying and falls back to the manual
    /// <see cref="Nestly.Domain.NotificationEventType.RecurringBookingPaymentDue"/>
    /// notification for whatever remains of <see cref="PaymentWindowHours"/>.
    /// Smaller than <c>SubscriptionBillingOptions.RetryLimit</c> (3 over
    /// days) on purpose: a booking has a hard deadline the visit itself
    /// imposes, where a subscription renewal does not.
    /// </summary>
    [Range(1, 10)]
    public int AutoChargeRetryLimit { get; set; } = 3;

    /// <summary>How long after a failed auto-charge attempt before the next retry - hours, not <c>SubscriptionBillingOptions.RetryBackoffDays</c>' days, for the same reason as <see cref="AutoChargeRetryLimit"/>.</summary>
    [Range(1, 48)]
    public int AutoChargeRetryBackoffHours { get; set; } = 4;

    /// <summary>
    /// How many visits in a row may expire unpaid before a pay-as-you-go plan is paused. An unpaid visit
    /// never happens (no professional is assigned until it is paid, and it expires), so this is not about
    /// that visit - it stops a plan whose customer is not paying from creating a booking a day, holding a
    /// slot for the payment window each time and sending a reminder each time. One missed payment is more
    /// often an accident than a decision, hence a default of two.
    /// </summary>
    [Range(1, 10)]
    public int PauseAfterUnpaidVisits { get; set; } = 2;

    /// <summary>How many times one plan may be told "skip visits until a date". Capped so the feature cannot be chained into a permanent gap; an open-ended pause (with a manual resume) is not limited.</summary>
    [Range(0, 20)]
    public int MaxSkipRangesPerPlan { get; set; } = 2;

    /// <summary>The furthest ahead, in days from today, a "skip visits until" date may be.</summary>
    [Range(1, 365)]
    public int MaxSkipDays { get; set; } = 30;

    /// <summary>
    /// A plan that pays each visit from the customer's wallet warns them when the balance would no longer
    /// cover this many upcoming visits. Three matches <see cref="LeadTimeDays"/>: the visits already being
    /// created ahead of time.
    /// </summary>
    [Range(1, 30)]
    public int WalletLowBalanceVisits { get; set; } = 3;

    /// <summary>
    /// How many days ahead a recurring plan's regular professional is kept free for the plan's visits (see
    /// <c>IProviderPlanReservationService</c>): another order at an overlapping time on one of those dates is not
    /// given to them. Thirty matches how far ahead a customer can book at all, so nothing further out can need the
    /// reservation yet. 0 switches the rule off.
    /// </summary>
    [Range(0, 90)]
    public int ProviderReservationHorizonDays { get; set; } = 30;

    /// <summary>
    /// The most visits a customer can pay for in one prepaid checkout (the
    /// booking placed together with the plan plus its repeats). Every visit is
    /// a real booking that holds a slot and is created synchronously when the
    /// plan is created, so this bounds both the request's work and how much
    /// provider capacity one unpaid checkout can hold. Comfortably above the
    /// 30 a daily plan's first cycle needs.
    /// </summary>
    [Range(2, 120)]
    public int MaxPrepaidVisits { get; set; } = 60;

    /// <summary>
    /// How many days one prepaid cycle of an "until I cancel" plan covers -
    /// the customer pays this many days up front and is asked to renew before
    /// they run out. Bounded plans ignore it: their single cycle is however
    /// many visits they were bought with.
    /// </summary>
    [Range(7, 90)]
    public int PrepaidCycleDays { get; set; } = 30;

    /// <summary>
    /// How many days before an open-ended prepaid plan's paid cycle ends its
    /// next cycle is created and the customer is asked to pay for it. Long
    /// enough to act on, short enough that the next cycle's slots are not held
    /// for weeks (they are held from creation until paid or expired).
    /// </summary>
    [Range(1, 14)]
    public int PrepaidRenewalLeadDays { get; set; } = 5;
}
