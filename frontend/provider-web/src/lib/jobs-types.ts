/**
 * Response/request shapes for the Provider API's jobs surface (`/api/v1/jobs`,
 * docs/PROVIDER.md's `booking_provider_assignment` bridge table).
 *
 * Mirrors backend/shared/Application/ProviderJobs/ProviderJobContracts.cs
 * (ProviderJobSummaryResponse / ProviderJobSearchResponse / ProviderJobDetailResponse)
 * field-for-field, camelCased per ASP.NET Core's default JSON naming policy -
 * keep the two in sync if that file changes.
 */

/**
 * Mirrors Nestly.Application.ProviderJobs.ProviderJobStatus's declaration order
 * exactly. provider-api has no JsonStringEnumConverter registered, so this
 * enum serialises over the wire as its ordinal (a plain number), not its
 * name - same convention as customer-web's BookingStatus (lib/types.ts) -
 * keep this in sync with ProviderJobContracts.cs if that enum's order changes.
 */
export enum JobStatus {
  Assigned = 0,
  Accepted = 1,
  Rejected = 2,
  Reassigned = 3,
  InProgress = 4,
  Completed = 5,
  Withdrawn = 6,
}

export const JOB_STATUS_LABELS: Record<JobStatus, string> = {
  [JobStatus.Assigned]: "Assigned",
  [JobStatus.Accepted]: "Accepted",
  [JobStatus.Rejected]: "Rejected",
  [JobStatus.Reassigned]: "Reassigned",
  [JobStatus.InProgress]: "In progress",
  [JobStatus.Completed]: "Completed",
  [JobStatus.Withdrawn]: "Cancelled by customer",
};

export function jobStatusLabel(status: JobStatus): string {
  return JOB_STATUS_LABELS[status] ?? String(status);
}

/** One row in the provider's job list (GET /jobs). */
export interface JobListItem {
  assignmentId: string;
  bookingId: string;
  status: JobStatus;
  customerNameSnapshot: string;
  customerMobileSnapshot: string;
  addressLine1Snapshot: string;
  addressCitySnapshot: string;
  addressPincodeSnapshot: string;
  slotDate: string;
  slotStartTimeSnapshot: string;
  slotEndTimeSnapshot: string;
  totalPayableSnapshot: number;
  assignedAt: string;
  responseDeadline: string | null;
}

/** GET /jobs's actual response envelope (ProviderJobSearchResponse). */
export interface JobListResponse {
  items: JobListItem[];
}

/** Query parameters for GET /jobs. Both optional. */
export interface JobListParams {
  status?: string;
  date?: string;
}

export interface JobLineItem {
  nameSnapshot: string;
  quantity: number;
  unitPriceSnapshot: number;
}

/** Detail view of a single assignment (GET /jobs/{bookingId}). */
export interface JobDetail {
  assignmentId: string;
  bookingId: string;
  status: JobStatus;
  customerNameSnapshot: string;
  customerMobileSnapshot: string;
  addressLabelSnapshot: string;
  addressLine1Snapshot: string;
  addressLine2Snapshot: string | null;
  addressLandmarkSnapshot: string | null;
  addressCitySnapshot: string;
  addressStateSnapshot: string;
  addressPincodeSnapshot: string;
  addressContactNameSnapshot: string;
  addressContactMobileSnapshot: string;
  slotDate: string;
  slotStartTimeSnapshot: string;
  slotEndTimeSnapshot: string;
  items: JobLineItem[];
  totalPayableSnapshot: number;
  assignedAt: string;
  respondedAt: string | null;
  responseDeadline: string | null;
  notes: string | null;
  completionProofRef: string | null;
}

export interface SubmitCompletionProofRequest {
  proofRef: string;
}

/**
 * Completion verification shapes mirror the C# records in
 * Nestly.Application.Bookings (BookingCompletionProofContracts.cs) - tasks
 * 195-197. Distinct from SubmitCompletionProofRequest's single legacy
 * proof-ref field above: this is the richer photos+checklist evidence that
 * gates the InProgress -> Completed transition (task 196).
 */

export interface CompletionChecklistAnswer {
  item: string;
  completed: boolean;
  notes: string | null;
}

export interface SubmitCompletionVerificationRequest {
  photoRefs: string[];
  checklistAnswers: CompletionChecklistAnswer[];
}

export interface BookingCompletionProofResponse {
  id: string;
  bookingId: string;
  photoRefs: string[];
  checklistAnswers: CompletionChecklistAnswer[];
  submittedByProviderId: string;
  submittedAtUtc: string;
}
