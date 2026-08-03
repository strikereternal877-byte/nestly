/**
 * Response shapes returned by the Consumer API.
 *
 * These mirror the C# records in Nestly.Application (Identity/*, Profile/*,
 * Addresses/*) — ASP.NET serialises records as camelCase JSON by default, so
 * property names here are the camelCase form of the C# ones.
 */

export interface LoginResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
}

export interface CustomerSummary {
  id: string;
  mobile: string;
  email: string | null;
  name: string;
  status: string;
}

export interface CustomerProfile {
  id: string;
  mobile: string;
  email: string | null;
  name: string;
  dateOfBirth: string | null;
  city: string | null;
  state: string | null;
  pincode: string | null;
  country: string | null;
  status: string;
  createdAt: string;
  updatedAt: string;
}

export interface CommunicationPreferences {
  transactionalSms: boolean;
  transactionalEmail: boolean;
  transactionalWhatsApp: boolean;
  promotionalSms: boolean;
  promotionalEmail: boolean;
  promotionalWhatsApp: boolean;
  push: boolean;
  updatedAt: string;
}

export interface CustomerAddress {
  id: string;
  label: string;
  line1: string;
  line2: string | null;
  landmark: string | null;
  pincode: string;
  city: string;
  state: string;
  latitude: number;
  longitude: number;
  contactName: string;
  contactMobile: string;
  isDefault: boolean;
}

/**
 * Catalog/geography/serviceability/slot/pricing shapes mirror the C# records
 * in Nestly.Application (Catalog/*, Geography/*, Serviceability/*, Slots/*,
 * Pricing/*) - see CategoriesController, ServicesController, CatalogSearchController,
 * GeographyController, ServiceabilityController, SlotsController, PricingController.
 */

export interface CategorySummary {
  id: string;
  name: string;
  slug: string;
  iconUrl: string | null;
  bannerUrl: string | null;
  isFeatured: boolean;
}

export interface ServiceAddOnSummary {
  id: string;
  name: string;
  description: string | null;
  price: number;
}

export interface ServiceSummary {
  id: string;
  name: string;
  slug: string;
  description: string;
  price: number;
  addOns: ServiceAddOnSummary[];
}

export interface CategoryDetail {
  id: string;
  name: string;
  slug: string;
  description: string;
  iconUrl: string | null;
  bannerUrl: string | null;
  services: ServiceSummary[];
}

export interface ServiceListItem {
  id: string;
  name: string;
  slug: string;
  description: string;
  price: number;
}

export interface ServiceFaq {
  id: string;
  question: string;
  answer: string;
}

export interface ServiceDetail {
  id: string;
  name: string;
  slug: string;
  description: string;
  price: number;
  inclusions: string;
  exclusions: string;
  cancellationPolicy: string | null;
  reschedulePolicy: string | null;
  categoryId: string;
  categoryName: string;
  categorySlug: string;
  addOns: ServiceAddOnSummary[];
  faqs: ServiceFaq[];
}

/** One recent review in a service's rating summary (SRS 11.6.1). */
export interface ServiceReviewItem {
  id: string;
  rating: number;
  reviewText: string | null;
  createdAtUtc: string;
}

/** Rating summary + recent reviews for a service detail page (SRS 11.6.1 "Reviews and rating summary"). */
export interface ServiceReviewSummary {
  averageRating: number;
  totalCount: number;
  ratingBreakdown: Record<number, number>;
  recentReviews: ServiceReviewItem[];
}

export interface CatalogSearchResult {
  categories: CategorySummary[];
  services: ServiceListItem[];
}

export interface City {
  id: string;
  name: string;
  stateName: string;
}

export interface LocalitySearchResult {
  id: string;
  name: string;
  zoneName: string;
  pincodeCode: string;
  pincodeId: string;
}

export interface ServiceabilityResult {
  isServiceable: boolean;
}

export interface SlotOption {
  slotWindowId: string;
  name: string;
  /** .NET TimeSpan serialises as "hh:mm:ss". */
  startTime: string;
  endTime: string;
  maxBookingsPerSlot: number | null;
}

export interface SlotAvailability {
  isServiceable: boolean;
  slots: SlotOption[];
}

export interface SlotRevalidation {
  isValid: boolean;
  reason: string | null;
}

export interface AddOnSelection {
  addOnId: string;
  quantity: number;
}

export interface PriceCalculationRequest {
  serviceId: string;
  cityId: string;
  quantity: number;
  addOns: AddOnSelection[];
}

export interface AddOnLineItem {
  addOnId: string;
  name: string;
  unitPrice: number;
  quantity: number;
  lineTotal: number;
}

export interface PriceBreakdown {
  basePrice: number;
  quantity: number;
  baseTotal: number;
  addOnLineItems: AddOnLineItem[];
  addOnTotal: number;
  visitCharge: number;
  subtotal: number;
  taxPercentage: number;
  taxAmount: number;
  platformFee: number;
  totalPayable: number;
}

/**
 * Booking shapes mirror the C# records in Nestly.Application.Bookings
 * (BookingContracts.cs) - see BookingsController.
 */

export interface BookingSummaryRequestBody {
  serviceId: string;
  cityId: string;
  addressId: string;
  localityId: string;
  slotWindowId: string;
  /** .NET DateOnly serialises as "yyyy-MM-dd". */
  slotDate: string;
  quantity: number;
  addOns: AddOnSelection[];
  couponCode?: string | null;
}

export interface BookingServiceSummary {
  id: string;
  name: string;
  slug: string;
}

export interface BookingAddressSummary {
  id: string;
  label: string;
  line1: string;
  line2: string | null;
  landmark: string | null;
  pincode: string;
  city: string;
  state: string;
  latitude: number;
  longitude: number;
  contactName: string;
  contactMobile: string;
}

export interface BookingSlotSummary {
  slotWindowId: string;
  name: string;
  date: string;
  startTime: string;
  endTime: string;
}

/** Mirrors the C# CouponSummary record returned by CouponsController. */
export interface CouponSummary {
  couponId: string;
  code: string;
  description: string | null;
  discountAmount: number;
}

/** Booking summary/preview (SRS 11.7), with the coupon module wired in (task 77). */
export interface BookingSummary {
  service: BookingServiceSummary;
  addOns: ServiceAddOnSummary[];
  address: BookingAddressSummary;
  slot: BookingSlotSummary;
  price: PriceBreakdown;
  cancellationPolicy: string | null;
  reschedulePolicy: string | null;
  coupon: CouponSummary | null;
  /** price.totalPayable - coupon.discountAmount when a coupon is applied, else === price.totalPayable. */
  finalPayable: number;
}

/**
 * Mirrors Nestly.Domain.BookingStatus's declaration order exactly. The
 * ConsumerApi has no JsonStringEnumConverter registered, so this enum
 * serialises over the wire as its ordinal (a plain number), not its name -
 * this mapping must stay in sync with BookingStatus.cs if that enum's order
 * ever changes.
 */
export enum BookingStatus {
  Initiated = 0,
  PaymentPending = 1,
  PaymentFailed = 2,
  Confirmed = 3,
  AwaitingFulfilment = 4,
  Assigned = 5,
  InProgress = 6,
  Completed = 7,
  CancelledByCustomer = 8,
  CancelledByAdmin = 9,
  Rescheduled = 10,
  RefundPending = 11,
  Refunded = 12,
}

/** Matches Nestly.Domain.BookingStatusBucket's member names - passed as the `bucket` query string value, which ASP.NET binds by name. */
export type BookingStatusBucket = "Upcoming" | "Completed" | "Cancelled";

export interface BookingStatusTimelineEntry {
  fromStatus: BookingStatus | null;
  toStatus: BookingStatus;
  toStatusLabel: string;
  reason: string | null;
  changedAtUtc: string;
}

export interface BookingDetail {
  id: string;
  service: BookingServiceSummary;
  addOns: ServiceAddOnSummary[];
  address: BookingAddressSummary;
  slot: BookingSlotSummary;
  price: PriceBreakdown;
  status: BookingStatus;
  statusLabel: string;
  timeline: BookingStatusTimelineEntry[];
  createdAtUtc: string;
  couponCode: string | null;
  couponDiscountAmount: number | null;
  /** Equals price.totalPayable on a persisted booking - both already reflect the discounted amount actually charged. */
  finalPayable: number;
}

export interface BookingListItem {
  id: string;
  serviceName: string;
  slotDate: string;
  totalPayable: number;
  status: BookingStatus;
  statusLabel: string;
  createdAtUtc: string;
}

/**
 * Recurring booking plan shapes mirror the C# records in
 * Nestly.Application.RecurringBookings (RecurringBookingPlanContracts.cs) -
 * see RecurringBookingPlansController.
 */

/** Mirrors Nestly.Domain.RecurringBookingRecurrenceFrequency's declaration order exactly (no JsonStringEnumConverter registered - see BookingStatus's doc comment). */
export enum RecurringBookingRecurrenceFrequency {
  Weekly = 0,
  Biweekly = 1,
  Monthly = 2,
}

/** Mirrors Nestly.Domain.RecurringBookingPlanStatus's declaration order exactly. */
export enum RecurringBookingPlanStatus {
  Active = 0,
  Paused = 1,
  Cancelled = 2,
  Completed = 3,
}

/** Mirrors Nestly.Domain.RecurringBookingOccurrenceOutcome's declaration order exactly. */
export enum RecurringBookingOccurrenceOutcome {
  Booked = 0,
  SkippedSlotUnavailable = 1,
  SkippedOrchestrationRejected = 2,
}

/**
 * .NET's System.DayOfWeek serialises as its ordinal, and its ordinal order
 * (Sunday=0 ... Saturday=6) already matches JS Date#getDay() - no remapping
 * needed, unlike BookingStatus/PaymentTransactionStatus above.
 */
export const DAY_OF_WEEK_LABELS = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
] as const;

export interface CreateRecurringBookingPlanRequestBody {
  serviceId: string;
  cityId: string;
  addressId: string;
  localityId: string;
  slotWindowId: string;
  quantity: number;
  frequency: RecurringBookingRecurrenceFrequency;
  recurrenceDayOfWeek: number | null;
  recurrenceDayOfMonth: number | null;
  /** .NET DateOnly serialises as "yyyy-MM-dd". */
  startDate: string;
  endDate: string | null;
  occurrenceCount: number | null;
  addOns: AddOnSelection[];
}

export interface RecurringBookingPlanResponse {
  id: string;
  serviceId: string;
  serviceName: string;
  addressId: string;
  slotWindowId: string;
  quantity: number;
  frequency: RecurringBookingRecurrenceFrequency;
  recurrenceDayOfWeek: number | null;
  recurrenceDayOfMonth: number | null;
  startDate: string;
  endDate: string | null;
  occurrenceCount: number | null;
  completedOccurrenceCount: number;
  nextOccurrenceDate: string;
  status: RecurringBookingPlanStatus;
  createdAtUtc: string;
}

export interface UpcomingOccurrenceResponse {
  scheduledDate: string;
  isProjected: boolean;
}

export interface OccurrenceHistoryResponse {
  scheduledDate: string;
  outcome: RecurringBookingOccurrenceOutcome;
  bookingId: string | null;
  skipReason: string | null;
  processedAtUtc: string;
}

/**
 * Payment shapes mirror the C# records in Nestly.Application.Payments
 * (PaymentContracts.cs) - see PaymentsController.
 */

/**
 * Mirrors Nestly.Domain.PaymentTransactionStatus's declaration order exactly
 * (no JsonStringEnumConverter is registered - see BookingStatus's doc comment
 * above for the same pattern).
 */
export enum PaymentTransactionStatus {
  Pending = 0,
  Success = 1,
  Failed = 2,
  Cancelled = 3,
}

/** Mirrors Nestly.Domain.PaymentAttemptStatus's declaration order exactly. */
export enum PaymentAttemptStatus {
  Created = 0,
  Success = 1,
  Failed = 2,
}

export interface PaymentOrderResponse {
  paymentTransactionId: string;
  attemptId: string;
  gatewayOrderId: string;
  amount: number;
  currency: string;
  attemptNumber: number;
  createdAtUtc: string;
}

export interface PaymentAttemptResponse {
  id: string;
  attemptNumber: number;
  gatewayOrderId: string;
  gatewayPaymentRef: string | null;
  status: PaymentAttemptStatus;
  failureReason: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
}

export interface PaymentTransactionResponse {
  id: string;
  bookingId: string;
  customerId: string;
  amount: number;
  currency: string;
  status: PaymentTransactionStatus;
  attempts: PaymentAttemptResponse[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

/**
 * Refund shapes mirror the C# records in Nestly.Application.Refunds
 * (RefundContracts.cs) - see RefundsController.
 */

/** Mirrors Nestly.Domain.RefundType's declaration order exactly. */
export enum RefundType {
  Full = 0,
  Partial = 1,
}

/** Mirrors Nestly.Domain.RefundMethod's declaration order exactly. */
export enum RefundMethod {
  Gateway = 0,
  Wallet = 1,
}

/** Mirrors Nestly.Domain.RefundStatus's declaration order exactly. */
export enum RefundStatus {
  Initiated = 0,
  Processing = 1,
  Refunded = 2,
  Failed = 3,
}

export interface RefundTransactionResponse {
  id: string;
  bookingId: string;
  paymentTransactionId: string;
  type: RefundType;
  method: RefundMethod;
  amount: number;
  status: RefundStatus;
  gatewayRefundRef: string | null;
  reason: string;
  createdAtUtc: string;
  processedAtUtc: string | null;
}

/**
 * Wallet shapes mirror the C# records in Nestly.Application.Wallet
 * (WalletContracts.cs) - see WalletController.
 */

/** Mirrors Nestly.Domain.WalletEntryType's declaration order exactly. */
export enum WalletEntryType {
  Credit = 0,
  Debit = 1,
}

/** Mirrors Nestly.Domain.WalletSourceType's declaration order exactly. */
export enum WalletSourceType {
  Refund = 0,
  PromotionalCredit = 1,
  ManualAdjustment = 2,
  ReferralReward = 3,
  ReferralMilestoneBonus = 4,
  ReferralCreditExpiry = 5,
  NestlyCoinsReward = 6,
  NestlyCoinsClawback = 7,
}

export interface WalletBalanceResponse {
  balance: number;
}

export interface WalletLedgerEntryResponse {
  id: string;
  entryType: WalletEntryType;
  amount: number;
  balanceAfter: number;
  sourceType: WalletSourceType;
  sourceReferenceId: string | null;
  description: string;
  createdAtUtc: string;
}

/**
 * Referral shapes mirror the C# records in Nestly.Application.Referral
 * (ReferralCustomerContracts.cs) - see ReferralController (task 168).
 */

export interface ReferralSummaryResponse {
  referralCode: string;
  shareLink: string;
  invitedCount: number;
  qualifiedCount: number;
  rewardedCount: number;
  totalEarned: number;
}

export interface ReferralHistoryItemResponse {
  id: string;
  refereeName: string;
  /** Serialized as the ReferralStatus enum member name (e.g. "Registered") - rendered directly, not re-mapped through a numeric enum. */
  status: string;
  registeredAtUtc: string;
  qualifiedAtUtc: string | null;
  rewardedAtUtc: string | null;
  rewardEarned: number | null;
}

/**
 * Cancellation shapes mirror the C# records in
 * Nestly.Application.Cancellations (CancellationContracts.cs) - see
 * CancellationsController.
 */

export interface CancellationPolicyResponse {
  isEligible: boolean;
  ineligibilityReason: string | null;
  withinFreeCancellationWindow: boolean;
  cancellationFeeAmount: number;
  refundAmount: number;
  refundMethod: RefundMethod;
  freeCancellationWindowHours: number;
  lateCancellationFeePercentage: number;
}

export interface CancelBookingRequestBody {
  reason: string;
}

export interface CancellationOutcomeResponse {
  bookingId: string;
  bookingStatus: BookingStatus;
  withinFreeCancellationWindow: boolean;
  cancellationFeeAmount: number;
  refundAmount: number;
  refundStatus: RefundStatus | null;
  refundMethod: RefundMethod | null;
  refundTransactionId: string | null;
  cancelledAtUtc: string;
}

/**
 * Reschedule shapes mirror the C# records in Nestly.Application.Reschedules
 * (RescheduleContracts.cs) - see ReschedulesController.
 */

export interface RescheduleEligibilityResponse {
  isEligible: boolean;
  ineligibilityReason: string | null;
  reschedulesUsed: number;
  maxReschedulesPerBooking: number;
  minHoursBeforeSlot: number;
}

export interface RescheduleBookingRequestBody {
  localityId: string;
  slotWindowId: string;
  /** .NET DateOnly serialises as "yyyy-MM-dd". */
  slotDate: string;
  reason?: string | null;
}

export interface RescheduleOutcomeResponse {
  bookingId: string;
  bookingStatus: BookingStatus;
  previousSlot: BookingSlotSummary;
  newSlot: BookingSlotSummary;
  isLate: boolean;
  feeAmount: number;
  reschedulesUsed: number;
  maxReschedulesPerBooking: number;
  rescheduledAtUtc: string;
}

/**
 * Review shapes mirror the C# records in Nestly.Application.Reviews
 * (ReviewContracts.cs) - see ReviewsController.
 */

/**
 * Mirrors Nestly.Domain.ReviewStatus's declaration order exactly. Only two
 * states - admin task 122 split "flagged for abuse" out into its own
 * independent `Review.IsFlagged` boolean (not modeled here yet, since no
 * customer-facing screen reads it), so a review that is flagged for
 * moderation review can still report Visible here.
 */
export enum ReviewStatus {
  Visible = 0,
  Hidden = 1,
}

export interface ReviewEligibilityResponse {
  isEligible: boolean;
  ineligibilityReason: string | null;
}

export interface SubmitReviewRequestBody {
  rating: number;
  reviewText?: string | null;
  issueTags?: string | null;
}

export interface ReviewResponse {
  id: string;
  bookingId: string;
  serviceId: string;
  rating: number;
  reviewText: string | null;
  issueTags: string | null;
  status: ReviewStatus;
  createdAtUtc: string;
}

/**
 * Support ticket shapes mirror the C# records in Nestly.Application.Support
 * (SupportTicketContracts.cs) - see SupportTicketsController,
 * BookingSupportTicketsController.
 */

/** Mirrors Nestly.Domain.SupportTicketCategory's declaration order exactly. */
export enum SupportTicketCategory {
  BookingIssue = 0,
  PaymentIssue = 1,
  RefundIssue = 2,
  ServiceQuality = 3,
  ProfessionalConduct = 4,
  PricingDispute = 5,
  TechnicalIssue = 6,
  GeneralInquiry = 7,
}

/** Mirrors Nestly.Domain.SupportTicketPriority's declaration order exactly. */
export enum SupportTicketPriority {
  Low = 0,
  Normal = 1,
  High = 2,
  Urgent = 3,
}

/** Mirrors Nestly.Domain.SupportTicketStatus's declaration order exactly. */
export enum SupportTicketStatus {
  Open = 0,
  InProgress = 1,
  WaitingForCustomer = 2,
  Escalated = 3,
  Resolved = 4,
  Closed = 5,
}

/** Mirrors Nestly.Domain.SupportTicketCommentAuthorType's declaration order exactly. */
export enum SupportTicketCommentAuthorType {
  Customer = 0,
  Support = 1,
  System = 2,
}

/** Mirrors Nestly.Domain.DisputeResolutionOutcome's declaration order exactly. */
export enum DisputeResolutionOutcome {
  RefundValid = 0,
  ClosedInvalid = 1,
}

export interface CreateSupportTicketRequestBody {
  category: SupportTicketCategory;
  bookingId?: string | null;
  subject: string;
  description: string;
  priority?: SupportTicketPriority | null;
}

export interface AddSupportTicketCommentRequestBody {
  comment: string;
}

export interface SupportTicketSummaryResponse {
  id: string;
  category: SupportTicketCategory;
  priority: SupportTicketPriority;
  subject: string;
  status: SupportTicketStatus;
  bookingId: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface SupportTicketCommentResponse {
  id: string;
  authorType: SupportTicketCommentAuthorType;
  comment: string;
  createdAt: string;
}

export interface SupportTicketDetailResponse {
  id: string;
  customerId: string;
  bookingId: string | null;
  category: SupportTicketCategory;
  priority: SupportTicketPriority;
  subject: string;
  description: string;
  status: SupportTicketStatus;
  resolutionSummary: string | null;
  isDisputed: boolean;
  disputeOutcome: DisputeResolutionOutcome | null;
  comments: SupportTicketCommentResponse[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

/**
 * Subscription shapes mirror the C# records in Nestly.Application.Subscriptions
 * (CustomerSubscriptionContracts.cs) - see SubscriptionController (tasks 177-182).
 */

/** Mirrors Nestly.Domain.SubscriptionBillingCycle's declaration order exactly. */
export enum SubscriptionBillingCycle {
  Monthly = 0,
  Quarterly = 1,
  Yearly = 2,
}

/** Mirrors Nestly.Domain.CustomerSubscriptionStatus's declaration order exactly. */
export enum CustomerSubscriptionStatus {
  Active = 0,
  Cancelled = 1,
  Expired = 2,
  PaymentFailed = 3,
}

export interface SubscriptionPlanBrowseResponse {
  id: string;
  name: string;
  description: string | null;
  price: number;
  billingCycle: SubscriptionBillingCycle;
  freeVisitsIncluded: number;
  discountPercent: number;
  prioritySlotFlag: boolean;
}

export interface SubscribeRequestBody {
  planId: string;
}

export interface MySubscriptionResponse {
  id: string;
  planName: string;
  price: number;
  billingCycle: SubscriptionBillingCycle;
  freeVisitsIncluded: number;
  discountPercent: number;
  prioritySlotFlag: boolean;
  status: CustomerSubscriptionStatus;
  currentPeriodStartUtc: string;
  currentPeriodEndUtc: string;
  freeVisitsRemaining: number;
  nextBillingDateUtc: string;
  lastPaymentFailureReason: string | null;
  createdAtUtc: string;
  cancelledAtUtc: string | null;
}

/**
 * Completion proof shapes mirror the C# records in Nestly.Application.Bookings
 * (BookingCompletionProofContracts.cs) - see BookingCompletionProofController
 * (tasks 195-198).
 */
export interface CompletionChecklistAnswerResponse {
  item: string;
  completed: boolean;
  notes: string | null;
}

export interface BookingCompletionProofResponse {
  id: string;
  bookingId: string;
  photoRefs: string[];
  checklistAnswers: CompletionChecklistAnswerResponse[];
  submittedByProviderId: string;
  submittedAtUtc: string;
}

/**
 * Chat shapes mirror the C# records in Nestly.Application.Chat
 * (ChatContracts.cs) - see ChatController (consumer-api), ChatHub (live
 * delivery) and PRODUCT-ENHANCEMENTS.md "3. IN-APP CHAT" (tasks 189-192).
 */

/** Mirrors Nestly.Domain.ChatContextType's declaration order exactly. */
export enum ChatContextType {
  Booking = 0,
  SupportTicket = 1,
}

/** Mirrors Nestly.Domain.ChatSenderType's declaration order exactly. */
export enum ChatSenderType {
  Customer = 0,
  Admin = 1,
  Provider = 2,
}

export interface GetOrCreateChatThreadRequestBody {
  contextType: ChatContextType;
  contextId: string;
}

export interface ChatThreadResponse {
  id: string;
  contextType: ChatContextType;
  contextId: string;
  createdAtUtc: string;
  lastMessageAtUtc: string;
}

export interface SendChatMessageRequestBody {
  body: string;
}

export interface ChatMessageResponse {
  id: string;
  threadId: string;
  senderId: string;
  senderType: ChatSenderType;
  body: string;
  sentAtUtc: string;
  readAtUtc: string | null;
}

export interface ChatMessagePageResult {
  messages: ChatMessageResponse[];
  totalCount: number;
  page: number;
  pageSize: number;
}
