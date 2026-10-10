/**
 * Response/request shapes for the Admin API's provider ticket workflow
 * surface (Provider Management UX pass): `ProviderSupportTicketsController`.
 * Mirrors the backend contracts in
 * `Application/ProviderSupport/AdminProviderSupportTicketContracts.cs` field
 * for field - see lib/support-types.ts's own doc comment for the same
 * no-JsonStringEnumConverter, ordinal-serialisation caveat.
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

export interface ProviderSupportTicketCommentResponse {
  id: string;
  authorType: ProviderSupportTicketCommentAuthorType;
  comment: string;
  createdAt: string;
}

export interface AdminProviderSupportTicketSummaryResponse {
  id: string;
  providerId: string;
  providerName: string;
  category: ProviderSupportTicketCategory;
  subject: string;
  status: ProviderSupportTicketStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface AdminProviderSupportTicketSearchResponse {
  items: AdminProviderSupportTicketSummaryResponse[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AdminProviderSupportTicketDetailResponse {
  id: string;
  providerId: string;
  providerName: string;
  category: ProviderSupportTicketCategory;
  subject: string;
  description: string;
  status: ProviderSupportTicketStatus;
  resolutionSummary: string | null;
  comments: ProviderSupportTicketCommentResponse[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface AddProviderSupportTicketCommentRequestBody {
  comment: string;
}

export interface ResolveProviderSupportTicketRequestBody {
  resolutionSummary: string;
}

export interface ProviderSupportTicketSearchParams {
  providerId?: string;
  category?: ProviderSupportTicketCategory;
  status?: ProviderSupportTicketStatus;
  fromUtc?: string;
  toUtc?: string;
  page?: number;
  pageSize?: number;
}
