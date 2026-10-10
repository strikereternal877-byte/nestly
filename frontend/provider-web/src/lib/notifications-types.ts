/**
 * Provider-facing in-app notification inbox (Provider Management UX pass) -
 * mirrors the C# records in Nestly.Application.Notifications
 * (ProviderNotificationContracts.cs). ProviderApi has no
 * JsonStringEnumConverter registered (see profile-types.ts's same caveat), so
 * `type` serialises over the wire as its ordinal and must stay in
 * declaration-order sync with its C# source.
 */

/** Mirrors Nestly.Domain.ProviderNotificationType's declaration order exactly - append-only. */
export enum ProviderNotificationType {
  JobOffered = 0,
  KycRejected = 1,
  Suspended = 2,
  PayoutProcessed = 3,
  SupportTicketReply = 4,
  BankAccountApproved = 5,
  BankAccountRejected = 6,
  JobRescheduled = 7,
  JobUnassigned = 8,
  JobCancelled = 9,
}

export interface ProviderNotification {
  id: string;
  type: ProviderNotificationType;
  title: string;
  body: string;
  deepLinkPath: string | null;
  isRead: boolean;
  createdAtUtc: string;
}

export interface ProviderNotificationListResponse {
  items: ProviderNotification[];
  totalCount: number;
  unreadCount: number;
  page: number;
  pageSize: number;
}
