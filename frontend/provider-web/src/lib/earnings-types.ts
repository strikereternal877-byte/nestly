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
  [EarningSourceType.NestlyCoinsReward]: "Glavyx Coins earned",
  [EarningSourceType.NestlyCoinsClawback]: "Glavyx Coins clawed back",
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

/**
 * A completed job's payout status (ProviderJobPayoutStatus) - derived on the
 * backend from whether the job's credit date falls inside one of the
 * provider's payout batches, mirroring that batch's own PayoutStatus. Not the
 * same enum as {@link PayoutStatus}: `AwaitingBatch`/`PendingSettlement` have
 * no batch-level equivalent, and a job with no covering batch yet (the common
 * case right after completion) is `AwaitingBatch`, not `Pending`.
 */
export enum JobPayoutStatus {
  AwaitingBatch = 0,
  PendingSettlement = 1,
  Processing = 2,
  Paid = 3,
  Failed = 4,
}

const JOB_PAYOUT_STATUS_LABELS: Record<JobPayoutStatus, string> = {
  [JobPayoutStatus.AwaitingBatch]: "Awaiting payout batch",
  [JobPayoutStatus.PendingSettlement]: "Pending settlement",
  [JobPayoutStatus.Processing]: "Processing",
  [JobPayoutStatus.Paid]: "Paid",
  [JobPayoutStatus.Failed]: "Failed",
};

export function jobPayoutStatusLabel(status: JobPayoutStatus): string {
  return JOB_PAYOUT_STATUS_LABELS[status] ?? String(status);
}

/**
 * One completed job's earning breakdown (ProviderEarningJobResponse) - the
 * same gross/commission/net figures jobs/[id]'s payout panel shows, plus this
 * job's place in the payout timeline.
 */
export interface JobEarning {
  bookingId: string;
  bookingReference: string;
  serviceName: string;
  /** Calendar date (`YYYY-MM-DD`) - the booking's slot date, not an instant. */
  completionDate: string;
  grossAmount: number;
  commissionAmount: number;
  netAmountToProvider: number;
  payoutStatus: JobPayoutStatus;
  creditedAtUtc: string;
}

/**
 * GET /earnings/jobs's response (ProviderEarningJobSearchResponse).
 * `totalNetAmount`/`jobCount` summarize the *entire filtered period*, not
 * just `items` - the page the caller happens to have requested.
 */
export interface JobEarningsResponse {
  items: JobEarning[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalNetAmount: number;
  jobCount: number;
}
