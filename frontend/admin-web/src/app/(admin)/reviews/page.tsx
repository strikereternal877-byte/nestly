"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { Alert, Badge, Button, Card, EmptyState, Field, PageHeading, Select, Skeleton } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import {
  ConfirmDialog,
  FilterBar,
  Pagination,
  countActiveFilters,
  formatDateTime,
} from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { API_V1, apiFetch, apiFetchBlob, describeError } from "@/lib/api";
import { listCategories, listServices } from "@/lib/catalog-api";
import { canWriteModule } from "@/lib/permissions";
import {
  DEFAULT_REVIEW_MODERATION_FILTERS,
  FLAGGED_FILTER_OPTIONS,
  REVIEW_STATUS_FILTER_OPTIONS,
  buildReviewModerationQuery,
  reviewFiltersFromSearchParams,
  reviewStatusLabel,
} from "@/lib/reviews";
import type { ReviewModerationFilters } from "@/lib/reviews";
import { ReviewStatus } from "@/lib/types";
import { todayIsoDate } from "@/lib/date";
import { useAdminClaims } from "@/lib/use-admin-claims";
import type {
  ModerateReviewRequestBody,
  ReviewModerationItem,
  ReviewModerationSearchResponse,
} from "@/lib/types";

const PAGE_SIZE = 20;

/** Amber is the reserved rating/reward accent, so it is exactly right here. */
function StarRating({ rating }: { rating: number }) {
  return (
    <span aria-label={`${rating} out of 5 stars`} className="nums text-accent-500">
      {"★".repeat(rating)}
      <span className="text-fg-subtle">{"★".repeat(Math.max(0, 5 - rating))}</span>
    </span>
  );
}

function ReviewStatusBadge({ status }: { status: ReviewStatus }) {
  return (
    <Badge tone={status === ReviewStatus.Hidden ? "danger" : "success"}>{reviewStatusLabel(status)}</Badge>
  );
}

/**
 * Admin review moderation screen (SRS 12.15, task 123): filterable review
 * list (status, flagged, rating range, date, service/category) with
 * hide/unhide, flag/unflag and CSV export actions - see ReviewsController
 * (task 122) for the API this drives. Moderating actions are only shown to
 * admins holding "reviews.write"; the API enforces this server-side
 * regardless, same rationale as the customer detail screen's `canWrite` gate.
 *
 * Cards rather than a table: a review is a paragraph of free text plus tags
 * and a moderator note, which a row would have to truncate to be readable.
 *
 * Wrapped in Suspense: `useSearchParams` (reading the Customer 360 view's
 * "Reviews written" click-through) opts the tree below it out of static
 * rendering, and Next's App Router requires a Suspense boundary around that
 * or the production build fails (same pattern providers/page.tsx uses).
 */
export default function ReviewModerationPage() {
  return (
    <Suspense fallback={<div className="w-full max-w-5xl px-6 py-10" />}>
      <ReviewModerationPageContent />
    </Suspense>
  );
}

function ReviewModerationPageContent() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "reviews");
  const queryClient = useQueryClient();
  const searchParams = useSearchParams();

  // Lazy initializer, read once on mount - see reviewFiltersFromSearchParams's
  // own doc comment (same convention as providers/directory's own
  // filtersFromSearchParams).
  const [draft, setDraft] = useState<ReviewModerationFilters>(() => reviewFiltersFromSearchParams(searchParams));
  const [page, setPage] = useState(1);
  const [noteDrafts, setNoteDrafts] = useState<Record<string, string>>({});
  const [pendingHide, setPendingHide] = useState<ReviewModerationItem | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);
  const [isExporting, setIsExporting] = useState(false);

  // Real category/service lists to suggest against the Category ID / Service
  // ID fields, which stay plain GUID text inputs (never dropdowns) -
  // ReviewRepository.SearchAsync matches both as exact FK GUIDs
  // (criteria.CategoryId/ServiceId), exposed here as <datalist>s so the admin
  // can pick a real name and still see/paste the GUID the field submits.
  const categoriesQuery = useQuery({
    queryKey: ["catalog-categories"],
    queryFn: listCategories,
    staleTime: 5 * 60 * 1000,
  });

  const servicesQuery = useQuery({
    queryKey: ["catalog-services"],
    queryFn: () => listServices(),
    staleTime: 5 * 60 * 1000,
  });

  // Live filtering (no Search button): Service ID, Category ID and the
  // rating fields are free-text/number inputs, debounced 300ms before
  // hitting the query, same convention as customers/page.tsx; Status,
  // Flagged and the Posted from/to date pickers apply immediately.
  const [debouncedServiceId, setDebouncedServiceId] = useState(draft.serviceId);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedServiceId(draft.serviceId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.serviceId]);

  const [debouncedCategoryId, setDebouncedCategoryId] = useState(draft.categoryId);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedCategoryId(draft.categoryId.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [draft.categoryId]);

  const [debouncedMinRating, setDebouncedMinRating] = useState(draft.minRating);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedMinRating(draft.minRating), 300);
    return () => window.clearTimeout(handle);
  }, [draft.minRating]);

  const [debouncedMaxRating, setDebouncedMaxRating] = useState(draft.maxRating);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedMaxRating(draft.maxRating), 300);
    return () => window.clearTimeout(handle);
  }, [draft.maxRating]);

  const effectiveFilters: ReviewModerationFilters = {
    ...draft,
    serviceId: debouncedServiceId,
    categoryId: debouncedCategoryId,
    minRating: debouncedMinRating,
    maxRating: debouncedMaxRating,
  };

  // Any filter change resets to page 1 - staying on a now out-of-range page
  // would just show an empty result (same pattern as customers/page.tsx).
  useResetOnChange(
    [
      debouncedServiceId,
      debouncedCategoryId,
      debouncedMinRating,
      debouncedMaxRating,
      draft.status,
      draft.flagged,
      draft.fromDate,
      draft.toDate,
    ],
    () => setPage(1),
  );

  const query = useQuery({
    queryKey: ["admin-reviews", effectiveFilters, page],
    queryFn: () =>
      apiFetch<ReviewModerationSearchResponse>(
        `${API_V1}/reviews?${buildReviewModerationQuery(effectiveFilters, { page, pageSize: PAGE_SIZE })}`,
        { authenticated: true },
      ),
    placeholderData: keepPreviousData,
  });

  const invalidateList = () => queryClient.invalidateQueries({ queryKey: ["admin-reviews"] });

  const noteFor = (reviewId: string) => noteDrafts[reviewId]?.trim() || null;
  const setNoteFor = (reviewId: string, value: string) =>
    setNoteDrafts((current) => ({ ...current, [reviewId]: value }));

  function useModerationMutation(action: "hide" | "unhide" | "flag" | "unflag") {
    return useMutation({
      mutationFn: ({ reviewId, note }: { reviewId: string; note: string | null }) =>
        apiFetch<ReviewModerationItem>(`${API_V1}/reviews/${reviewId}/${action}`, {
          method: "POST",
          authenticated: true,
          body: JSON.stringify({ note } satisfies ModerateReviewRequestBody),
        }),
      onSuccess: (_data, variables) => {
        setNoteFor(variables.reviewId, "");
        setPendingHide(null);
        invalidateList();
      },
    });
  }

  const hideMutation = useModerationMutation("hide");
  const unhideMutation = useModerationMutation("unhide");
  const flagMutation = useModerationMutation("flag");
  const unflagMutation = useModerationMutation("unflag");

  /**
   * Every action button used to be disabled — and labelled "Hiding…" — on
   * every row as soon as one row's mutation started, because the checks read
   * the mutation's global `isPending`. Scoping to the row actually in flight
   * is what `variables` is for.
   */
  const busyReviewId = (mutation: { isPending: boolean; variables?: { reviewId: string } }) =>
    mutation.isPending ? mutation.variables?.reviewId : undefined;

  const moderationError =
    hideMutation.error ?? unhideMutation.error ?? flagMutation.error ?? unflagMutation.error;

  const onReset = () => {
    setDraft(DEFAULT_REVIEW_MODERATION_FILTERS);
    setDebouncedServiceId("");
    setDebouncedCategoryId("");
    setDebouncedMinRating("");
    setDebouncedMaxRating("");
    setPage(1);
  };

  const onExport = async () => {
    setIsExporting(true);
    setExportError(null);
    try {
      const blob = await apiFetchBlob(
        `${API_V1}/reviews/export?${buildReviewModerationQuery(effectiveFilters, { page: 1, pageSize: PAGE_SIZE })}`,
        { authenticated: true },
      );
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      // `todayIsoDate`, never `toISOString().slice(0, 10)`: the latter names
      // the file with yesterday's date all morning in IST.
      link.download = `reviews-export-${todayIsoDate()}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      setExportError(describeError(err));
    } finally {
      setIsExporting(false);
    }
  };

  const items = query.data?.items;

  return (
    <div className="w-full max-w-5xl">
      <PageHeading
        title="Review Moderation"
        subtitle="View, hide/unhide, flag and export customer reviews (SRS 12.15). The original rating and text are always retained, even once hidden or flagged."
      />

      <FilterBar
        columns={3}
        onClear={onReset}
        activeCount={countActiveFilters(draft)}
        busy={query.isFetching}
        actions={
          <Button type="button" variant="secondary" loading={isExporting} onClick={onExport}>
            Export CSV
          </Button>
        }
      >
        <Select
          label="Status"
          options={REVIEW_STATUS_FILTER_OPTIONS}
          value={draft.status}
          onChange={(e) => setDraft((f) => ({ ...f, status: e.target.value as ReviewModerationFilters["status"] }))}
        />
        <Select
          label="Flagged"
          options={FLAGGED_FILTER_OPTIONS}
          value={draft.flagged}
          onChange={(e) => setDraft((f) => ({ ...f, flagged: e.target.value as ReviewModerationFilters["flagged"] }))}
        />
        <Field
          label="Min rating"
          type="number"
          min={1}
          max={5}
          value={draft.minRating}
          onChange={(e) => setDraft((f) => ({ ...f, minRating: e.target.value }))}
        />
        <Field
          label="Max rating"
          type="number"
          min={draft.minRating || 1}
          max={5}
          // Reported here rather than left to a 400 from the server, which is
          // where an inverted range previously surfaced.
          error={
            draft.minRating && draft.maxRating && Number(draft.maxRating) < Number(draft.minRating)
              ? "Max rating cannot be below min rating."
              : undefined
          }
          value={draft.maxRating}
          onChange={(e) => setDraft((f) => ({ ...f, maxRating: e.target.value }))}
        />
        <Field
          label="Posted from"
          type="date"
          max={draft.toDate || undefined}
          value={draft.fromDate}
          onChange={(e) => setDraft((f) => ({ ...f, fromDate: e.target.value }))}
        />
        <Field
          label="Posted to"
          type="date"
          min={draft.fromDate || undefined}
          value={draft.toDate}
          onChange={(e) => setDraft((f) => ({ ...f, toDate: e.target.value }))}
        />
        <Field
          label="Service ID"
          name="serviceId"
          autoComplete="on"
          list="reviews-service-suggestions"
          placeholder="Service GUID"
          value={draft.serviceId}
          onChange={(e) => setDraft((f) => ({ ...f, serviceId: e.target.value }))}
        />
        {/* Options only appear once the admin has typed something - an empty
            field must not pop the entire service list on click. */}
        <datalist id="reviews-service-suggestions">
          {draft.serviceId.trim()
            ? (servicesQuery.data ?? [])
                .filter((service) => {
                  const term = draft.serviceId.trim().toLowerCase();
                  return service.name.toLowerCase().includes(term) || service.id.toLowerCase().includes(term);
                })
                .map((service) => <option key={service.id} value={service.id} label={service.name} />)
            : null}
        </datalist>
        <Field
          label="Category ID"
          name="categoryId"
          autoComplete="on"
          list="reviews-category-suggestions"
          placeholder="Category GUID"
          value={draft.categoryId}
          onChange={(e) => setDraft((f) => ({ ...f, categoryId: e.target.value }))}
        />
        <datalist id="reviews-category-suggestions">
          {(categoriesQuery.data ?? []).map((category) => (
            <option key={category.id} value={category.id} label={category.name} />
          ))}
        </datalist>
      </FilterBar>

      <div className="mt-6 flex flex-col gap-4">
        {exportError ? <Alert>{exportError}</Alert> : null}
        {/* Moderation failures surface here, immediately above the list the
            action was taken in, rather than at the top of the page. */}
        {moderationError ? <Alert>{describeError(moderationError)}</Alert> : null}

        {query.isPending ? (
          Array.from({ length: 4 }, (_, index) => (
            <div key={index} className="rounded-2xl bg-surface p-5 shadow-sm">
              <Skeleton className="h-5 w-48" />
              <Skeleton className="mt-3 h-4 w-64" />
              <Skeleton className="mt-4 h-4 w-full" />
              <Skeleton className="mt-2 h-4 w-3/4" />
            </div>
          ))
        ) : query.isError ? (
          <SectionError error={query.error} onRetry={() => query.refetch()} />
        ) : !items || items.length === 0 ? (
          <EmptyState
            title="No reviews match these filters"
            description="Try broadening the rating or date range, or clear the filters to see every review."
            action={
              countActiveFilters(draft) > 0 ? (
                <Button variant="secondary" onClick={onReset}>
                  Clear filters
                </Button>
              ) : undefined
            }
          />
        ) : (
          <>
            {items.map((review) => (
              <Card key={review.id}>
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex flex-wrap items-center gap-2">
                    <StarRating rating={review.rating} />
                    <ReviewStatusBadge status={review.status} />
                    {review.isFlagged ? <Badge tone="warning">Flagged</Badge> : null}
                  </div>
                  <span className="nums text-xs text-fg-subtle">{formatDateTime(review.createdAtUtc)}</span>
                </div>

                <p className="mt-2 text-sm text-fg-muted">
                  {review.customerName} · {review.serviceName} ({review.categoryName})
                </p>

                {review.reviewText ? (
                  <p className="mt-3 whitespace-pre-wrap text-sm leading-relaxed text-fg">
                    {review.reviewText}
                  </p>
                ) : null}
                {review.issueTags ? (
                  <p className="mt-2 text-xs text-fg-subtle">Tags: {review.issueTags}</p>
                ) : null}

                {review.moderatorNote ? (
                  <div className="mt-3 rounded-xl bg-surface-2 px-4 py-3 text-xs text-fg-muted">
                    <span className="font-medium text-fg">Moderator note:</span> {review.moderatorNote}
                    {review.moderatedAtUtc ? (
                      <span className="nums"> ({formatDateTime(review.moderatedAtUtc)})</span>
                    ) : null}
                  </div>
                ) : null}

                {canWrite ? (
                  <div className="mt-4 flex flex-col gap-3 border-t border-line pt-4 sm:flex-row sm:items-end">
                    <div className="flex-1">
                      <Field
                        label="Moderator note"
                        hint="Optional — kept on the review as the reason for this action."
                        value={noteDrafts[review.id] ?? ""}
                        onChange={(e) => setNoteFor(review.id, e.target.value)}
                        placeholder="Reason for this action"
                      />
                    </div>
                    <div className="flex flex-wrap gap-2">
                      {review.status === ReviewStatus.Hidden ? (
                        <Button
                          variant="secondary"
                          loading={busyReviewId(unhideMutation) === review.id}
                          onClick={() => unhideMutation.mutate({ reviewId: review.id, note: noteFor(review.id) })}
                        >
                          Unhide
                        </Button>
                      ) : (
                        // Hiding removes the review from the customer-facing
                        // site, so it is confirmed rather than fired on tap.
                        <Button variant="secondary" onClick={() => setPendingHide(review)}>
                          Hide
                        </Button>
                      )}

                      {review.isFlagged ? (
                        <Button
                          variant="ghost"
                          loading={busyReviewId(unflagMutation) === review.id}
                          onClick={() => unflagMutation.mutate({ reviewId: review.id, note: noteFor(review.id) })}
                        >
                          Unflag
                        </Button>
                      ) : (
                        <Button
                          variant="ghost"
                          loading={busyReviewId(flagMutation) === review.id}
                          onClick={() => flagMutation.mutate({ reviewId: review.id, note: noteFor(review.id) })}
                        >
                          Flag
                        </Button>
                      )}
                    </div>
                  </div>
                ) : null}
              </Card>
            ))}

            {query.data ? (
              <div className="rounded-2xl bg-surface px-4 py-3 shadow-sm sm:px-5">
                <Pagination
                  page={page}
                  pageSize={PAGE_SIZE}
                  totalCount={query.data.totalCount}
                  onPageChange={setPage}
                  busy={query.isFetching}
                  itemLabel="review"
                />
              </div>
            ) : null}
          </>
        )}
      </div>

      <ConfirmDialog
        open={pendingHide !== null}
        title="Hide this review?"
        description="It disappears from the customer-facing site. The rating and text are retained and it can be unhidden at any time."
        confirmLabel="Hide review"
        cancelLabel="Keep visible"
        loading={hideMutation.isPending}
        error={hideMutation.isError ? describeError(hideMutation.error) : null}
        onCancel={() => setPendingHide(null)}
        onConfirm={() => {
          if (pendingHide) {
            hideMutation.mutate({ reviewId: pendingHide.id, note: noteFor(pendingHide.id) });
          }
        }}
      >
        {pendingHide ? (
          <p className="text-sm text-fg-muted">
            <span className="font-medium text-fg">{pendingHide.customerName}</span> on{" "}
            {pendingHide.serviceName}
            {noteFor(pendingHide.id) ? ` — note: "${noteFor(pendingHide.id)}"` : " — no moderator note"}.
          </p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
