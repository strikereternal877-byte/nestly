"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/lib/api";
import { acceptJob, extendJobResponseDeadline, rejectJob } from "@/lib/jobs-api";
import { useToast } from "@/components/ui";

/**
 * Accept/decline mutations for one job offer (`docs/PROVIDER.md`'s
 * `booking_provider_assignment` bridge table), shared between `/jobs/[id]`
 * (one offer, full detail) and `/offers` (every pending offer at once,
 * task from `docs/OPEN-FIXES-FEATURES.csv` "Provider Web, Proposed new page,
 * Job offers with countdown"). Extracted out of `/jobs/[id]` rather than
 * reimplemented on `/offers`, so the two screens can never drift on what
 * "accept" and "decline" actually do.
 *
 * Carries row 38's (docs/OPEN-FIXES-FEATURES.csv) extend-on-failure repair
 * verbatim: a 401 is already retried transparently by `apiFetch` (task
 * d9f557c), so anything that still surfaces in `onError` here is either a
 * real business rejection (4xx other than 401 - AlreadyResponded, no
 * outstanding assignment) or a genuine non-provider-fault failure (a
 * transient 5xx, or no `ApiError` at all - a network error, since `fetch`
 * throws a plain Error/TypeError for those). Only the latter extends the
 * response window - a business rejection is accurate as-is and extending it
 * would just delay the provider learning the offer is gone.
 */
export function useJobResponseMutations(
  jobId: string,
  options: { onAccepted?: () => void; onDeclined?: () => void } = {},
) {
  const queryClient = useQueryClient();
  const toast = useToast();

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["provider-job", jobId] });
    queryClient.invalidateQueries({ queryKey: ["provider-job-completion-verification", jobId] });
    // The list's status column and "needs a response" styling are now stale
    // for every screen reading `GET /jobs` - `/jobs`, `/today` and `/offers`
    // alike, since they all share this same query key.
    queryClient.invalidateQueries({ queryKey: ["provider-jobs"] });
  };

  const acceptMutation = useMutation({
    mutationFn: () => acceptJob(jobId),
    onSuccess: () => {
      invalidate();
      toast("success", "Job accepted. It's yours.");
      options.onAccepted?.();
    },
    onError: (error) => {
      const isTransientFailure = !(error instanceof ApiError) || error.status >= 500;
      if (isTransientFailure) {
        extendJobResponseDeadline(jobId)
          .then(invalidate)
          .catch(() => {
            // Best-effort safety net - if this also fails there is nothing
            // more the client can do; the caller's error banner still tells
            // the provider to retry, and the original deadline still stands.
          });
      }
    },
  });

  const rejectMutation = useMutation({
    mutationFn: () => rejectJob(jobId),
    onSuccess: () => {
      invalidate();
      toast("info", "Job declined. It will be offered to another provider.");
      options.onDeclined?.();
    },
  });

  return { acceptMutation, rejectMutation };
}
