/**
 * Response/request shapes for the Admin API's auth surface.
 *
 * `AdminAuthController` / `AdminTokenService` (SRS 12.1, 12.1.2, tasks
 * 94-95, 95a-95g) have since landed - this shape is confirmed against
 * `Nestly.Application.Identity.AdminLoginResponse`: a short-lived JWT plus a
 * rotate-on-refresh opaque refresh token, the same pair shape the Consumer
 * API's `LoginResponse` uses (see customer-web/src/lib/types.ts).
 */

export interface AdminLoginRequestBody {
  email: string;
  password: string;
}

export interface AdminLoginResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
}

/**
 * Claims this client reads off the decoded access token for UI purposes
 * (nav filtering, "signed in as" display) - never for authorization
 * decisions, which remain the API's job.
 *
 * `role` is expected to exist today (a JWT is not useful for RBAC without
 * one). `permissions` is anticipated from a parallel task and may be absent
 * or empty until that lands - every reader of this field must tolerate
 * that (see `lib/permissions.ts`).
 */
export interface AdminSessionClaims {
  subject: string | null;
  email: string | null;
  role: string | null;
  permissions: string[];
}

/**
 * Dashboard KPI filters (SRS 12.3.2), sent as query-string parameters to
 * `GET {API_V1}/dashboard/kpis`. Every field is optional - `dateFrom`/`dateTo`
 * are `yyyy-MM-dd` (what an `<input type="date">` produces, and what
 * ASP.NET Core's `DateOnly` model binder parses directly); an unset pair
 * makes the API default to today, and an unset `city`/`category` applies no
 * restriction on that dimension.
 */
export interface DashboardKpiFilters {
  dateFrom?: string;
  dateTo?: string;
  city?: string;
  category?: string;
}

/**
 * SRS 12.3.1's KPI widget set, mirroring the Admin API's `DashboardKpiResponse`
 * (task 99). `dateFrom`/`dateTo` echo back the window the API actually
 * resolved - relevant when the caller left them unset and the API defaulted
 * to today.
 */
export interface DashboardKpiResponse {
  dateFrom: string;
  dateTo: string;
  bookingsCount: number;
  revenueTotal: number;
  cancellationsCount: number;
  refundAmountTotal: number;
  openSupportTicketsCount: number;
}

/**
 * Customer management shapes (SRS 12.4, tasks 101a-102) mirror the C#
 * records in Nestly.Application.Customers (CustomerManagementContracts.cs) -
 * see CustomersController. AdminApi has no JsonStringEnumConverter
 * registered (same as ConsumerApi - see customer-web/src/lib/types.ts's
 * BookingStatus doc comment), so every enum below serialises over the wire
 * as its ordinal and must stay in declaration-order sync with its C# source.
 */

/** Mirrors Nestly.Application.CustomerStatus's declaration order exactly. */
export enum CustomerStatus {
  Active = 0,
  Blocked = 1,
  Unverified = 2,
  SoftDeleted = 3,
}

/**
 * Mirrors Nestly.Domain.BookingStatus's declaration order exactly. Because
 * the ordinal is the wire value, new statuses are only ever appended on the
 * C# side - the task-264 tracking states therefore sit at 14/15 rather than
 * between Assigned and InProgress where the lifecycle puts them.
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
  Expired = 13,
  ProviderEnRoute = 14,
  ProviderArrived = 15,
}

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
  BookingWalletCredit = 8,
  BookingWalletCreditReversal = 9,
  TopUp = 10,
  RescheduleFee = 11,
  RescheduleFeeReversal = 12,
}

/** Plain-language label for where a wallet ledger entry came from. */
export const WALLET_SOURCE_LABELS: Record<WalletSourceType, string> = {
  [WalletSourceType.Refund]: "Refund",
  [WalletSourceType.PromotionalCredit]: "Promotional credit",
  [WalletSourceType.ManualAdjustment]: "Manual adjustment",
  [WalletSourceType.ReferralReward]: "Referral reward",
  [WalletSourceType.ReferralMilestoneBonus]: "Referral milestone bonus",
  [WalletSourceType.ReferralCreditExpiry]: "Referral credit expired",
  [WalletSourceType.NestlyCoinsReward]: "Coins reward",
  [WalletSourceType.NestlyCoinsClawback]: "Coins clawback",
  [WalletSourceType.BookingWalletCredit]: "Used on a booking",
  [WalletSourceType.BookingWalletCreditReversal]: "Booking credit returned",
  [WalletSourceType.TopUp]: "Top-up",
  [WalletSourceType.RescheduleFee]: "Late reschedule fee",
  [WalletSourceType.RescheduleFeeReversal]: "Late reschedule fee returned",
};

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

export interface CustomerSummary {
  id: string;
  name: string;
  mobile: string;
  email: string | null;
  city: string | null;
  status: CustomerStatus;
  createdAtUtc: string;
  bookingCount: number;
}

export interface CustomerSearchResponse {
  items: CustomerSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Query parameters for the customer search endpoint (SRS 12.4.1, task 101a). All optional. */
export interface CustomerSearchParams {
  name?: string;
  mobile?: string;
  email?: string;
  city?: string;
  status?: CustomerStatus;
  registeredFromUtc?: string;
  registeredToUtc?: string;
  minBookingCount?: number;
  maxBookingCount?: number;
  page?: number;
  pageSize?: number;
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
  isDefault: boolean;
}

export interface CustomerBooking {
  id: string;
  status: BookingStatus;
  slotDate: string;
  totalPayableSnapshot: number;
  createdAtUtc: string;
}

export interface CustomerWalletEntry {
  id: string;
  entryType: WalletEntryType;
  amount: number;
  balanceAfter: number;
  sourceType: WalletSourceType;
  description: string;
  createdAtUtc: string;
}

export interface CustomerCouponUsage {
  couponId: string;
  couponCode: string;
  bookingId: string;
  discountAmount: number;
  redeemedAtUtc: string;
}

export interface CustomerSupportTicket {
  id: string;
  category: SupportTicketCategory;
  priority: SupportTicketPriority;
  subject: string;
  status: SupportTicketStatus;
  createdAtUtc: string;
}

export interface CustomerNote {
  id: string;
  authorAdminUserId: string;
  note: string;
  createdAtUtc: string;
}

/**
 * Review moderation shapes (SRS 12.15, task 122) mirror the C# records in
 * Nestly.Application.Reviews (ReviewModerationContracts.cs) - see
 * ReviewsController. Same no-JsonStringEnumConverter caveat as CustomerStatus
 * above: `status` serialises as its ordinal.
 */

/** Mirrors Nestly.Domain.ReviewStatus's declaration order exactly - only two states; "flagged" is the separate `isFlagged` boolean below, not a third status value. */
export enum ReviewStatus {
  Visible = 0,
  Hidden = 1,
}

/** One review row on the admin moderation screen (SRS 12.15). */
export interface ReviewModerationItem {
  id: string;
  bookingId: string;
  customerId: string;
  customerName: string;
  serviceId: string;
  serviceName: string;
  categoryId: string;
  categoryName: string;
  rating: number;
  reviewText: string | null;
  issueTags: string | null;
  status: ReviewStatus;
  isFlagged: boolean;
  moderatorNote: string | null;
  moderatedByAdminUserId: string | null;
  moderatedAtUtc: string | null;
  createdAtUtc: string;
}

export interface ReviewModerationSearchResponse {
  items: ReviewModerationItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** An optional moderator reason attached to a hide/unhide/flag/unflag action. */
export interface ModerateReviewRequestBody {
  note: string | null;
}

/**
 * Bidirectional reviews: the ratings providers have left about this customer
 * (admin-only - never shown to the customer). Mirrors
 * Nestly.Application.Customers.CustomerProviderRatingsResponse.
 * `averageRating`/`ratingCount` are null/0 when the customer has no ratings
 * yet, same "no rating" vs "rated 0" distinction the provider side uses.
 */
export interface CustomerProviderRating {
  id: string;
  bookingId: string;
  rating: number;
  note: string | null;
  providerDisplayName: string;
  createdAtUtc: string;
}

export interface CustomerProviderRatings {
  averageRating: number | null;
  ratingCount: number;
  recent: CustomerProviderRating[];
}

/** The customer 360 view (SRS 12.4.2, task 101b). */
export interface CustomerDetail {
  id: string;
  name: string;
  mobile: string;
  email: string | null;
  dateOfBirth: string | null;
  city: string | null;
  state: string | null;
  pincode: string | null;
  country: string | null;
  status: CustomerStatus;
  createdAtUtc: string;
  addresses: CustomerAddress[];
  bookings: CustomerBooking[];
  walletBalance: number;
  walletEntries: CustomerWalletEntry[];
  coupons: CustomerCouponUsage[];
  supportTickets: CustomerSupportTicket[];
  notes: CustomerNote[];
  providerRatings: CustomerProviderRatings;
}

/** One day of the Customer Analytics registration-trend series - always present for every day in the window, zero-filled where nothing registered. */
export interface CustomerRegistrationTrendPoint {
  date: string;
  count: number;
}

/** One row of the Customer Analytics "top cities" breakdown table. */
export interface CustomerCityBreakdown {
  city: string;
  count: number;
}

/**
 * Customer Analytics dashboard (Admin Web new page, customer counterpart to
 * the Provider Onboarding Overview/Performance dashboards): KPI counts plus
 * a registration-trend graph, computed from real Customer/Booking data only.
 * Mirrors `Nestly.Application.Customers.CustomerAnalyticsResponse` field for
 * field - see that record's doc comment for exactly what each count means
 * and what was deliberately left out (no KYC/verification concept, no
 * wallet/referral rollup).
 */
export interface CustomerAnalyticsResponse {
  trendDays: number;
  totalCustomers: number;
  activeCount: number;
  blockedCount: number;
  unverifiedCount: number;
  softDeletedCount: number;
  newToday: number;
  newLast7Days: number;
  newInTrendWindow: number;
  customersWithBookings: number;
  customersWithZeroBookings: number;
  registrationTrend: CustomerRegistrationTrendPoint[];
  topCities: CustomerCityBreakdown[];
}
