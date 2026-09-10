"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { ErrorState, NotYetAvailable } from "@/components/states";
import { Button, EmptyState, PageHeading, Skeleton } from "@/components/ui";
import { isNotImplemented } from "@/lib/api";
import { listJobs } from "@/lib/jobs-api";
import { listPendingOffers } from "@/lib/jobs-active";
import type { JobListItem } from "@/lib/jobs-types";
import { OfferCard } from "./_components/OfferCard";

/**
 * Offers screen (docs/OPEN-FIXES-FEATURES.csv, "Provider Web, Proposed new
 * page, Job offers with countdown"): before this existed, a new offer was
 * only visible as an Assigned row inside the general `/jobs` list, and its
 * 15/30-minute response deadline only as a static timestamp after opening
 * the job - easy to miss entirely with several offers in flight, or while
 * working a different job elsewhere in the app. This lists every offer
 * currently open to this provider (`listPendingOffers`, `lib/jobs-active.ts`)
 * with a live countdown and accept/decline right on the card.
 *
 * Deliberately not a replacement for `/today`: `/today` picks the single
 * most urgent *active* job (an offer if one is open, otherwise whatever
 * accepted job the provider is mid-visit on) so there is always exactly one
 * thing to look at right now. This screen only ever shows offers, all of
 * them, which is what a provider juggling more than one wants - `/today`
 * still says which one is most urgent when there is just one. Same
 * `GET /jobs` response both already fetch (this shares `/today`'s query key
 * so visiting either after the other never re-fetches), same warning/danger
 * countdown styling, so the two screens read as one system rather than
 * disagreeing about what's outstanding.
 *
 * Push notifications (this row's other recommended fix, so an offer reaches
 * a provider who isn't looking at the app) are intentionally out of scope
 * for this change: provider-web already has FCM web-push wired end to end
 * (`lib/push.ts`, registered on sign-in in `(provider)/layout.tsx`) for
 * *device registration*, but nothing today calls `POST` to actually notify
 * a provider - the backend's push provider is a logging sandbox in every
 * environment (see `lib/push.ts`'s header comment), so there is no live send
 * path for an "offer assigned" push to attach to without inventing one
 * end-to-end (a new server-side notification trigger + payload contract).
 * That is a genuinely separate, larger change and stays deferred, per this
 * row's own fix note.
 */
export default function OffersPage() {
  const query = useQuery({
    // Same key/query `/today` and `/jobs` (unfiltered) use - shares their
    // cache rather than firing a second, identical fetch.
    queryKey: ["provider-jobs", "", ""],
    queryFn: () => listJobs({}),
  });

  return (
    <div>
      <PageHeading
        title="Offers"
        subtitle="Every job waiting on your response, soonest deadline first."
      />

      {query.isPending ? (
        <OffersSkeleton />
      ) : query.isError && isNotImplemented(query.error) ? (
        <NotYetAvailable
          title="Offers aren't available yet"
          description="Job assignment is still being built on the platform side. Once your account can receive bookings, offers will appear here."
        />
      ) : query.isError ? (
        <ErrorState
          title="Couldn't load your offers"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      ) : (
        <OffersContent jobs={query.data} />
      )}
    </div>
  );
}

function OffersContent({ jobs }: { jobs: JobListItem[] }) {
  const offers = listPendingOffers(jobs);

  if (offers.length === 0) {
    return (
      <EmptyState
        title="No offers waiting"
        description="You're all caught up - new offers will show up here the instant they're assigned to you."
        action={
          <Link href="/jobs">
            <Button variant="secondary">View all jobs</Button>
          </Link>
        }
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {offers.map((job) => (
        <OfferCard key={job.assignmentId} job={job} />
      ))}
    </div>
  );
}

/** Mirrors the real card's shape so nothing jumps when data lands. */
function OffersSkeleton() {
  return (
    <div className="flex flex-col gap-4" aria-hidden>
      {[0, 1].map((i) => (
        <div key={i} className="flex flex-col gap-3 rounded-2xl bg-surface p-6 shadow-sm">
          <div className="flex items-start justify-between gap-3">
            <Skeleton className="h-4 w-24" />
            <Skeleton className="h-5 w-20" />
          </div>
          <Skeleton className="h-6 w-48" />
          <Skeleton className="h-4 w-56" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-8 w-40 rounded-lg" />
          <div className="flex gap-2.5">
            <Skeleton className="h-11 flex-1 rounded-lg" />
            <Skeleton className="h-11 flex-1 rounded-lg" />
          </div>
        </div>
      ))}
    </div>
  );
}
