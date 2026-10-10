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
    SubscriptionPaymentFailed,

    /// <summary>A PaymentPending booking was auto-expired by BookingExpirySweepJob without ever being paid for (task 240).</summary>
    BookingExpired,

    // Task 276: the fulfilment half of the booking lifecycle, which was
    // entirely silent - BookingNotificationTriggerHandler mapped
    // Assigned/InProgress/Completed to nothing, so a customer was never told
    // a professional had been assigned, was on the way, had arrived, had
    // started or had finished.
    //
    // APPENDED, NEVER INSERTED. The column stores the *name*
    // (NotificationEventConfiguration/NotificationTemplateConfiguration both
    // apply HasConversion<string>(), max length 30 - every name below fits),
    // so persistence would tolerate insertion. The wire does not: AdminApi
    // registers no JsonStringEnumConverter, so this enum serialises to the
    // admin template screens as its ordinal and
    // frontend/admin-web/src/lib/notification-template-types.ts mirrors those
    // ordinals by hand. Inserting mid-enum would silently remap every
    // already-deployed admin client's template rows. Same reasoning, and the
    // same hazard, as BookingStatus and ProviderJobStatus.

    /// <summary>A provider accepted the job and is confirmed to the customer. Fires on <c>ProviderAssignmentAcceptedEvent</c>, not on the AwaitingFulfilment -> Assigned transition (task 295): that transition happens when the *offer* is made, and a provider who then rejects would have had their name announced for a job they never took. See <c>BookingNotificationTriggerHandler</c>'s doc comment for the full rule.</summary>
    ProviderAssigned,

    /// <summary>The assigned provider set off for the address (-> <see cref="BookingStatus.ProviderEnRoute"/>). A mute candidate: chatty by nature, and the live tracking screen already says the same thing.</summary>
    ProviderEnRoute,

    /// <summary>The assigned provider reached the address but has not begun work (-> <see cref="BookingStatus.ProviderArrived"/>). The other mute candidate.</summary>
    ProviderArrived,

    /// <summary>The provider started the job (-> <see cref="BookingStatus.InProgress"/>).</summary>
    JobStarted,

    /// <summary>The provider marked the job finished (-> <see cref="BookingStatus.Completed"/>).</summary>
    JobCompleted,

    /// <summary>
    /// The professional the customer was told about has been taken off the job
    /// and someone else is being lined up (task 295) - fires on
    /// <c>BookingProviderChangedEvent</c>. Deliberately its own event type
    /// rather than a second <see cref="ProviderAssigned"/>: that template reads
    /// as a first assignment, and a customer who already has a name in mind
    /// needs to be told it is no longer valid, not handed a second one with no
    /// explanation. Only ever sent when the outgoing provider had accepted, so
    /// it never refers to a name the customer never heard.
    /// </summary>
    ProviderChanged,

    // Phase 20 AMC module (docs/AMC.md, tasks 323-330). APPENDED, NEVER
    // INSERTED - see the comment above ProviderAssigned on why this enum's
    // wire format requires strict append-only growth.

    /// <summary>A customer purchased a new AMC contract.</summary>
    AmcContractPurchased,

    /// <summary>A visit was redeemed against an AMC contract's entitlement, on booking completion.</summary>
    AmcVisitRedeemed,

    /// <summary>An AMC contract's term end date is within the reminder window.</summary>
    AmcContractExpiringSoon,

    /// <summary>Every entitled visit on an AMC contract has been redeemed while the term still has time left.</summary>
    AmcContractExhausted,

    // Recurring-booking payment-timing fix. APPENDED, NEVER INSERTED - see the
    // comment above ProviderAssigned.

    /// <summary>
    /// A recurring plan's occurrence was created but still needs payment
    /// (<see cref="BookingStatus.PaymentPending"/>) - fires instead of
    /// <see cref="RecurringBookingUpcoming"/> when the occurrence has
    /// something payable, so the customer is told the truth: the visit is
    /// booked, not confirmed, and needs action before it is auto-expired.
    /// Unlike a one-off checkout's silent PaymentPending (nobody needs telling
    /// what they are already looking at), a recurring occurrence is created
    /// unattended, days ahead of the visit, so this is the only signal the
    /// customer gets that payment is due at all.
    /// </summary>
    RecurringBookingPaymentDue,

    /// <summary>
    /// A recurring plan's occurrence was created with
    /// <see cref="Nestly.Application.RecurringBookings.CreateRecurringBookingPlanRequest.AutoChargeEnabled"/>
    /// on - fires instead of <see cref="RecurringBookingPaymentDue"/>, at the
    /// same "just created" moment, so the customer is told their card will be
    /// charged automatically (with the amount and roughly when) before
    /// <c>RecurringOccurrenceAutoChargeJob</c> makes its first attempt -
    /// never a silent auto-deduction. A successful charge is announced by the
    /// booking's own Confirmed transition (<see cref="PaymentSuccess"/>/
    /// <see cref="BookingConfirmed"/>) exactly like any other payment; this
    /// event only ever covers the advance notice.
    ///
    /// Named "Recurring...", not "RecurringBooking...", to stay inside the
    /// 30-character column cap this enum's own header comment documents -
    /// the more obvious name overran it by 5 characters and only surfaced
    /// when the seed migration actually ran against Postgres.
    /// </summary>
    RecurringAutoChargeScheduled,

    /// <summary>
    /// A pay-as-you-go recurring plan was paused by the system because its last
    /// <c>RecurringBookingOptions.PauseAfterUnpaidVisits</c> visits expired unpaid (see
    /// <see cref="Nestly.Domain.RecurringBookingPlan.PauseForUnpaidVisits"/>). Tells the customer the plan has
    /// stopped creating visits and how to start it again; without it a paused plan would look like a plan that
    /// silently stopped working. Variables: ServiceName, UnpaidVisits.
    /// </summary>
    RecurringPlanPaused,

    /// <summary>
    /// A plan that pays each visit from the customer's wallet created a visit and the remaining balance no
    /// longer covers the next few - an early heads-up so the customer can add money before a visit is left
    /// waiting on payment. At most one a day per customer. Variables: ServiceName, WalletBalance, Visits.
    /// </summary>
    WalletLowBalance,

    /// <summary>
    /// To the customer: they changed their own recurring plan (paused, resumed, skipped visits, changed its time or
    /// cancelled it). The transactional confirmation of an action the customer took - what happened, from when,
    /// what it did to visits already booked, and any fee or refund - so they have a record and nothing is left to
    /// be inferred from a screen that has since closed. One event for every kind of change: the wording is built
    /// per change (<c>RecurringPlanChangeMessages</c>) and arrives in the variables. Variables: CustomerName,
    /// ServiceName, ActionTitle, Summary (short - SMS and push), Details (full - e-mail).
    /// </summary>
    RecurringPlanChanged,

    /// <summary>
    /// To the customer: a plan that pays each visit from the wallet created a visit the wallet could not fully
    /// cover. Says how much the wallet paid, how much is still to pay and by when, so "the wallet ran out" is stated
    /// rather than left to be worked out from a generic payment reminder. Variables: CustomerName, ServiceName,
    /// SlotDate, SlotWindow, WalletNote, Amount (still to pay), PaymentWindowHours.
    /// </summary>
    RecurringWalletShortfall
}
