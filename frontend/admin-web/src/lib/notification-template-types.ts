/**
 * Response/request shapes for the Admin API's notification template surface
 * (SRS 12.17, tasks 126a-d): `NotificationTemplatesController`. Mirrors the
 * backend contracts in `Application/Notifications/NotificationTemplateContracts.cs`
 * field for field.
 *
 * AdminApi has no JsonStringEnumConverter registered (see lib/coupon-types.ts's
 * doc comment), so every enum below serialises over the wire as its ordinal
 * and must stay in declaration-order sync with its C# source.
 */

/**
 * Mirrors Nestly.Domain.NotificationEventType's declaration order exactly.
 *
 * Task 276 completed this mirror. It had stopped at SupportTicketUpdate = 7
 * while the C# enum grew to 16 through the referral, recurring-booking, chat,
 * subscription and booking-expiry work, so nine event types' templates were
 * unreachable from the admin screens - the list filter could not name them and
 * the create form could not offer them. The ordinals below are not a choice:
 * every value's number is fixed by its position in the C# enum, which is why
 * that enum is only ever appended to.
 *
 * This mirror had drifted again: it stopped at ProviderChanged = 22 while the
 * C# enum grew through the Phase 20 AMC module (23-26) and the recurring-
 * booking payment-timing fix (27), leaving five more event types' templates
 * unreachable from the admin screens. Fixed alongside adding
 * RecurringBookingPaymentDue so the two never fell out of sync while being
 * edited in the same change.
 */
export enum NotificationEventType {
  Welcome = 0,
  BookingConfirmed = 1,
  PaymentSuccess = 2,
  PaymentFailed = 3,
  BookingCancelled = 4,
  BookingRescheduled = 5,
  RefundProcessed = 6,
  SupportTicketUpdate = 7,
  ReferralRegistered = 8,
  ReferralRewardCredited = 9,
  RecurringBookingUpcoming = 10,
  RecurringBookingSkipped = 11,
  NewChatMessage = 12,
  SubscriptionRenewed = 13,
  SubscriptionExpiringSoon = 14,
  SubscriptionPaymentFailed = 15,
  BookingExpired = 16,
  ProviderAssigned = 17,
  ProviderEnRoute = 18,
  ProviderArrived = 19,
  JobStarted = 20,
  JobCompleted = 21,
  ProviderChanged = 22,
  AmcContractPurchased = 23,
  AmcVisitRedeemed = 24,
  AmcContractExpiringSoon = 25,
  AmcContractExhausted = 26,
  RecurringBookingPaymentDue = 27,
  RecurringAutoChargeScheduled = 28,
  RecurringPlanPaused = 29,
  WalletLowBalance = 30,
  RecurringPlanChanged = 31,
  RecurringWalletShortfall = 32,
}

/** Mirrors Nestly.Domain.NotificationChannel's declaration order exactly. */
export enum NotificationChannel {
  Sms = 0,
  Email = 1,
  Push = 2,
}

export const NOTIFICATION_EVENT_TYPE_LABELS: Record<NotificationEventType, string> = {
  [NotificationEventType.Welcome]: "Welcome",
  [NotificationEventType.BookingConfirmed]: "Booking confirmed",
  [NotificationEventType.PaymentSuccess]: "Payment success",
  [NotificationEventType.PaymentFailed]: "Payment failed",
  [NotificationEventType.BookingCancelled]: "Booking cancelled",
  [NotificationEventType.BookingRescheduled]: "Booking rescheduled",
  [NotificationEventType.RefundProcessed]: "Refund processed",
  [NotificationEventType.SupportTicketUpdate]: "Support ticket update",
  [NotificationEventType.ReferralRegistered]: "Referral registered",
  [NotificationEventType.ReferralRewardCredited]: "Referral reward credited",
  [NotificationEventType.RecurringBookingUpcoming]: "Recurring booking upcoming",
  [NotificationEventType.RecurringBookingSkipped]: "Recurring booking skipped",
  [NotificationEventType.NewChatMessage]: "New chat message",
  [NotificationEventType.SubscriptionRenewed]: "Subscription renewed",
  [NotificationEventType.SubscriptionExpiringSoon]: "Subscription expiring soon",
  [NotificationEventType.SubscriptionPaymentFailed]: "Subscription payment failed",
  [NotificationEventType.BookingExpired]: "Booking expired",
  [NotificationEventType.ProviderAssigned]: "Professional assigned",
  [NotificationEventType.ProviderEnRoute]: "Professional on the way",
  [NotificationEventType.ProviderArrived]: "Professional arrived",
  [NotificationEventType.JobStarted]: "Job started",
  [NotificationEventType.JobCompleted]: "Job completed",
  [NotificationEventType.ProviderChanged]: "Professional changed",
  [NotificationEventType.AmcContractPurchased]: "AMC contract purchased",
  [NotificationEventType.AmcVisitRedeemed]: "AMC visit redeemed",
  [NotificationEventType.AmcContractExpiringSoon]: "AMC contract expiring soon",
  [NotificationEventType.AmcContractExhausted]: "AMC contract exhausted",
  [NotificationEventType.RecurringBookingPaymentDue]: "Recurring booking payment due",
  [NotificationEventType.RecurringAutoChargeScheduled]: "Recurring booking auto-charge scheduled",
  [NotificationEventType.RecurringPlanPaused]: "Recurring plan paused (unpaid visits)",
  [NotificationEventType.WalletLowBalance]: "Wallet balance low for recurring plan",
  [NotificationEventType.RecurringPlanChanged]: "Recurring plan changed by the customer",
  [NotificationEventType.RecurringWalletShortfall]: "Wallet couldn't cover a recurring visit",
};

export const NOTIFICATION_CHANNEL_LABELS: Record<NotificationChannel, string> = {
  [NotificationChannel.Sms]: "SMS",
  [NotificationChannel.Email]: "Email",
  [NotificationChannel.Push]: "Push",
};

/** Full template detail for the admin list/edit screens (SRS 12.17.2's field set). */
export interface NotificationTemplateResponse {
  id: string;
  eventType: NotificationEventType;
  channel: NotificationChannel;
  templateKey: string;
  subject: string | null;
  body: string;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
  updatedByAdminUserId: string | null;
}

/** Query parameters for the template list endpoint. All optional. */
export interface NotificationTemplateListParams {
  channel?: NotificationChannel;
  eventType?: NotificationEventType;
  isActive?: boolean;
}

/** Create request for a not-yet-covered (EventType, Channel) combination - rejected with a conflict if one already exists. */
export interface NotificationTemplateCreateRequest {
  eventType: NotificationEventType;
  channel: NotificationChannel;
  templateKey: string;
  subject: string | null;
  body: string;
}

/** Edit request for an existing template's content - eventType/channel/templateKey are immutable once created. */
export interface NotificationTemplateUpdateRequest {
  subject: string | null;
  body: string;
}

/** Renders a saved template's subject/body against sample values (SRS 12.17.2 "Preview/test capability") - a pure render, nothing is sent or persisted. */
export interface NotificationTemplatePreviewRequest {
  sampleVariables: Record<string, string>;
}

/** Ad-hoc preview of draft (not-yet-saved) subject/body text, for the editor's live preview. */
export interface NotificationTemplateAdHocPreviewRequest {
  channel: NotificationChannel;
  subject: string | null;
  body: string;
  sampleVariables: Record<string, string>;
}

export interface NotificationTemplatePreviewResponse {
  subject: string | null;
  body: string;
}
