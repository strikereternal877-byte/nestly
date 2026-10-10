/**
 * Provider support tickets (Provider Management UX pass) - mirrors the C#
 * records in Nestly.Application.ProviderSupport (ProviderSupportTicketContracts.cs).
 * ProviderApi has no JsonStringEnumConverter registered, so `category`/
 * `status` serialise over the wire as their ordinal and must stay in
 * declaration-order sync with their C# source.
 */

/** Mirrors Nestly.Domain.ProviderSupportTicketCategory's declaration order exactly. */
export enum ProviderSupportTicketCategory {
  Payout = 0,
  Kyc = 1,
  JobIssue = 2,
  Account = 3,
  Technical = 4,
  Other = 5,
}

/** Mirrors Nestly.Domain.ProviderSupportTicketStatus's declaration order exactly. */
export enum ProviderSupportTicketStatus {
  Open = 0,
  InProgress = 1,
  Resolved = 2,
}

/** Mirrors Nestly.Domain.ProviderSupportTicketCommentAuthorType's declaration order exactly. */
export enum ProviderSupportTicketCommentAuthorType {
  Provider = 0,
  Admin = 1,
}

export interface CreateProviderSupportTicketRequest {
  category: ProviderSupportTicketCategory;
  subject: string;
  description: string;
}

export interface AddProviderSupportTicketCommentRequest {
  comment: string;
}

export interface ProviderSupportTicketSummary {
  id: string;
  category: ProviderSupportTicketCategory;
  subject: string;
  status: ProviderSupportTicketStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface ProviderSupportTicketComment {
  id: string;
  authorType: ProviderSupportTicketCommentAuthorType;
  comment: string;
  createdAt: string;
}

export interface ProviderSupportTicketDetail {
  id: string;
  providerId: string;
  category: ProviderSupportTicketCategory;
  subject: string;
  description: string;
  status: ProviderSupportTicketStatus;
  resolutionSummary: string | null;
  comments: ProviderSupportTicketComment[];
  createdAtUtc: string;
  updatedAtUtc: string;
}
