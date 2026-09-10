"use client";

import { Badge } from "@/components/ui";
import { JobPayoutStatus, jobPayoutStatusLabel } from "@/lib/earnings-types";
import type { BadgeTone } from "@/components/ui";

/**
 * Tone for one job's payout status, in its own module for the same reason as
 * `PayoutStatusBadge` (a `page.tsx` may only export a default, and the ledger
 * table shouldn't drift from any other screen that renders this status).
 *
 * Mirrors `PayoutStatusBadge`'s tone choices where the two enums overlap:
 * `Paid` reads as done (success), `Processing` as in flight (info), `Failed`
 * as needing attention (danger). `AwaitingBatch`/`PendingSettlement` have no
 * batch-status equivalent - both are ordinary, not-yet-actionable waiting
 * states, so both stay neutral like `Pending`.
 */
function toneFor(status: JobPayoutStatus): BadgeTone {
  switch (status) {
    case JobPayoutStatus.Paid:
      return "success";
    case JobPayoutStatus.Processing:
      return "info";
    case JobPayoutStatus.Failed:
      return "danger";
    case JobPayoutStatus.PendingSettlement:
    case JobPayoutStatus.AwaitingBatch:
    default:
      return "neutral";
  }
}

export function JobPayoutStatusBadge({ status }: { status: JobPayoutStatus }) {
  return <Badge tone={toneFor(status)}>{jobPayoutStatusLabel(status)}</Badge>;
}
