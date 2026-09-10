/**
 * "Active job" selection for the Today screen (docs/OPEN-FIXES-FEATURES.csv,
 * "Provider Web, Proposed new page, Today / Now screen" - the app used to
 * land on the full filterable `/jobs` list, forcing a provider mid-shift to
 * filter and drill in to reach the job they are actually doing).
 *
 * Deliberately client-side over the existing `GET /jobs` response rather than
 * a new backend endpoint: `ProviderJobService.ListAsync` already loads every
 * assignment for the provider and filters status/date in memory (see its
 * comments), so a server-side `activeOnly` flag would save wire bytes but not
 * a single database round trip. `/jobs` itself already pays that same cost
 * on every unfiltered visit, so picking the active job out of the same
 * response here adds no new load.
 */
import { JobStatus } from "./jobs-types";
import type { JobListItem } from "./jobs-types";

/**
 * Every status where a job is still "in flight" for the provider: an open
 * offer awaiting accept/decline, or an accepted job somewhere between
 * accepted and completed. Deliberately includes `Accepted` itself, not just
 * EnRoute/Arrived/InProgress - an accepted-but-not-yet-started job still has
 * a next action waiting ("Start job"), so leaving it out would show "no
 * active job" for a provider who has, in fact, already committed to one.
 */
const ACTIVE_JOB_STATUSES: ReadonlySet<JobStatus> = new Set([
  JobStatus.Assigned,
  JobStatus.Accepted,
  JobStatus.EnRoute,
  JobStatus.Arrived,
  JobStatus.InProgress,
]);

export function isActiveJobStatus(status: JobStatus): boolean {
  return ACTIVE_JOB_STATUSES.has(status);
}

/**
 * Rank among active statuses - lower is more urgent. An open offer
 * (`Assigned`) always leads: it is the only status on a clock that runs out
 * on its own (`responseDeadline`), and losing it hands the job to another
 * provider, so it outranks even a job already in progress. Among
 * already-accepted jobs, rank by how exposed the provider is mid-visit:
 * InProgress/Arrived means they are literally on site with work to finish or
 * start; EnRoute/Accepted have not reached the address yet. Non-active
 * statuses are never looked up here - see `isActiveJobStatus` - so they carry
 * no ranking.
 */
const ACTIVE_STATUS_PRIORITY: Partial<Record<JobStatus, number>> = {
  [JobStatus.Assigned]: 0,
  [JobStatus.InProgress]: 1,
  [JobStatus.Arrived]: 2,
  [JobStatus.EnRoute]: 3,
  [JobStatus.Accepted]: 4,
};

/**
 * The single job to put front-and-centre on the Today screen, or `null` if
 * the provider has nothing in flight right now. When more than one job is
 * active at once (e.g. one already in progress plus a fresh offer), the most
 * urgent wins by `ACTIVE_STATUS_PRIORITY`; ties break on the earliest slot,
 * since that is the job whose window is running out soonest.
 */
export function pickActiveJob(jobs: readonly JobListItem[]): JobListItem | null {
  const active = jobs.filter((job) => isActiveJobStatus(job.status));
  if (active.length === 0) return null;

  return active.reduce((best, candidate) => {
    const priorityDiff = ACTIVE_STATUS_PRIORITY[candidate.status]! - ACTIVE_STATUS_PRIORITY[best.status]!;
    if (priorityDiff !== 0) return priorityDiff < 0 ? candidate : best;

    const bestKey = `${best.slotDate}T${best.slotStartTimeSnapshot}`;
    const candidateKey = `${candidate.slotDate}T${candidate.slotStartTimeSnapshot}`;
    return candidateKey < bestKey ? candidate : best;
  });
}
