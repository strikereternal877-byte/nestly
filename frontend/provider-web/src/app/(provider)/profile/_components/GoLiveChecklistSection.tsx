"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { ErrorState } from "@/components/states";
import { Badge, Button, Card, Skeleton } from "@/components/ui";
import { getGoLiveCheckCopy } from "@/lib/go-live";
import { getGoLiveStatus } from "@/lib/profile-api";
import type { GoLiveCheck } from "@/lib/profile-types";

/**
 * Go-live checklist (docs/OPEN-FIXES-FEATURES.csv "Provider Web, Proposed new
 * page, Onboarding checklist and go-live status"): every prerequisite to
 * start receiving work, each with its own checkmark/pending state and a link
 * to fix it. The persistent banner in `(provider)/layout.tsx` (`GoLiveBanner`)
 * surfaces the same data everywhere in the app; this section is the one
 * place that shows the *whole* list, including what's already done, so a
 * provider isn't left guessing what else might be missing.
 *
 * Placed first on the profile page, ahead of the sections it links into - a
 * provider who isn't getting work should see why before anything else.
 */
export function GoLiveChecklistSection() {
  const query = useQuery({ queryKey: ["provider-go-live-status"], queryFn: getGoLiveStatus });

  if (query.isPending) {
    return (
      <Card title="Go-live checklist" description="What's needed before you can start receiving jobs.">
        <div className="flex flex-col gap-3" aria-hidden>
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-12 w-full rounded-xl" />
          ))}
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Go-live checklist">
        <ErrorState
          title="Couldn't load your go-live status"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  const { isGoLiveReady, checks } = query.data;

  return (
    <Card
      title="Go-live checklist"
      description={
        isGoLiveReady
          ? "You've met every prerequisite - keep this up to date to keep receiving jobs."
          : "Complete every item below to start receiving jobs."
      }
      actions={<Badge tone={isGoLiveReady ? "success" : "warning"}>{isGoLiveReady ? "Live" : "Not live"}</Badge>}
    >
      <ul className="flex flex-col gap-2.5">
        {checks.map((check) => (
          <GoLiveChecklistItem key={check.key} check={check} />
        ))}
      </ul>
    </Card>
  );
}

function GoLiveChecklistItem({ check }: { check: GoLiveCheck }) {
  const copy = getGoLiveCheckCopy(check);

  return (
    <li className="flex items-center justify-between gap-3 rounded-xl border border-line bg-surface-2 p-3.5">
      <div className="flex min-w-0 items-center gap-3">
        <CheckStatusIcon isComplete={check.isComplete} />
        <span className={check.isComplete ? "text-sm text-fg-muted line-through" : "text-sm font-medium text-fg"}>
          {check.label}
        </span>
      </div>
      {check.isComplete ? null : (
        <Link href={copy.href} className="shrink-0">
          <Button type="button" size="sm" variant="secondary">
            {copy.linkLabel}
          </Button>
        </Link>
      )}
    </li>
  );
}

function CheckStatusIcon({ isComplete }: { isComplete: boolean }) {
  if (isComplete) {
    return (
      <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-success-soft text-success">
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="h-3.5 w-3.5"
          aria-hidden
        >
          <path d="m5 13 4 4L19 7" />
        </svg>
      </span>
    );
  }

  return (
    <span
      className="h-6 w-6 shrink-0 rounded-full border-2 border-line"
      aria-hidden
    />
  );
}
