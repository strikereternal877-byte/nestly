/**
 * Response shapes for the Provider API's earnings surface (`/api/v1/earnings`).
 * Mirrors backend/shared/Application/ProviderManagement/ProviderFinancialContracts.cs
 * (ProviderEarningsSummaryResponse / ProviderEarningLedgerEntryResponse /
 * ProviderPayoutSearchResponse / ProviderPayoutResponse) field-for-field,
 * camelCased per ASP.NET Core's default JSON naming policy, with enums as
 * numeric ordinals (no JsonStringEnumConverter registered - same convention
 * as jobs-types.ts's JobStatus) - keep this in sync if that file changes.
 */

export enum EarningEntryType {
  Credit = 0,
  Debit = 1,
}

export enum EarningSourceType {
  JobCompletion = 0,
  Penalty = 1,
  AdminCorrection = 2,
  NestlyCoinsReward = 3,
  NestlyCoinsClawback = 4,
}

const EARNING_SOURCE_LABELS: Record<EarningSourceType, string> = {
  [EarningSourceType.JobCompletion]: "Job completed",
  [EarningSourceType.Penalty]: "Penalty",
  [EarningSourceType.AdminCorrection]: "Adjustment",
  [EarningSourceType.NestlyCoinsReward]: "Nestly Coins earned",
  [EarningSourceType.NestlyCoinsClawback]: "Nestly Coins clawed back",
};

/** Readable label for a ledger entry's source (task 203). */
export function earningSourceLabel(sourceType: EarningSourceType): string {
  return EARNING_SOURCE_LABELS[sourceType] ?? "Earnings";
}

/** One append-only entry in the provider's earning ledger. */
export interface EarningLedgerEntry {
  id: string;
  providerId: string;
  entryType: EarningEntryType;
  amount: number;
  balanceAfter: number;
  sourceType: EarningSourceType;
  sourceReferenceId: string | null;
  description: string;
  createdAtUtc: string;
}

/** GET /earnings/summary's response (ProviderEarningsSummaryResponse). */
export interface EarningsSummary {
  providerId: string;
  currentBalance: number;
  entries: EarningLedgerEntry[];
}

export enum PayoutStatus {
  Pending = 0,
  Processing = 1,
  Paid = 2,
  Failed = 3,
}

export const PAYOUT_STATUS_LABELS: Record<PayoutStatus, string> = {
  [PayoutStatus.Pending]: "Pending",
  [PayoutStatus.Processing]: "Processing",
  [PayoutStatus.Paid]: "Paid",
  [PayoutStatus.Failed]: "Failed",
};

export function payoutStatusLabel(status: PayoutStatus): string {
  return PAYOUT_STATUS_LABELS[status] ?? String(status);
}

/** One row in the provider's payout list (ProviderPayoutResponse). */
export interface PayoutSummary {
  id: string;
  providerId: string;
  providerDisplayName: string;
  periodStart: string;
  periodEnd: string;
  totalAmount: number;
  status: PayoutStatus;
  payoutReference: string | null;
  notes: string | null;
  createdAt: string;
}

/** GET /earnings/payouts's actual response envelope (ProviderPayoutSearchResponse). */
export interface PayoutListResponse {
  items: PayoutSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** GET /earnings/payouts/{id}'s response - same shape as a list row. */
export type PayoutDetail = PayoutSummary;
