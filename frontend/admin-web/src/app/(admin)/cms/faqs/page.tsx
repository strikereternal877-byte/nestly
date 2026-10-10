"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Button, Card, PageHeading, Select } from "@/components/ui";
import { FilterBar, Pagination, countActiveFilters } from "@/components/data-table";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { describeError } from "@/lib/api";
import { createCmsFaq, searchCmsFaqs, setCmsFaqPublished, updateCmsFaq } from "@/lib/cms-api";
import {
  CmsContentStatus,
  type CmsFaqCreateRequest,
  type CmsFaqResponse,
  type CmsFaqUpdateRequest,
  type CmsPlacement,
} from "@/lib/cms-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CmsFaqForm } from "../_components/CmsFaqForm";
import { CmsFaqsTable } from "../_components/CmsFaqsTable";
import { PLACEMENT_FILTER_OPTIONS, STATUS_FILTER_OPTIONS } from "../_components/cmsDisplay";
import { CmsTabs } from "../_components/CmsTabs";

const PAGE_SIZE = 20;

interface FaqFilters {
  placement: string;
  status: string;
}

const EMPTY_FILTERS: FaqFilters = { placement: "", status: "" };

/**
 * Site-level FAQ management (SRS 12.16.1, task 125c): search/filter by
 * placement and status, create, edit, and publish/unpublish. Gated behind
 * the "cms" permission module - every mutating control checks
 * `canWriteModule` the same way CmsPagesPage does; the route itself is only
 * reachable once AdminSidebar already filtered it in by "cms.read".
 */
export default function CmsFaqsPage() {
  const claims = useAdminClaims();
  const [filters, setFilters] = useState<FaqFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [editingFaq, setEditingFaq] = useState<CmsFaqResponse | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const canWrite = canWriteModule(claims, "cms");
  const queryClient = useQueryClient();

  // Live filtering (no Search button): both fields are dropdowns, so no
  // debouncing is needed - a change just resets to page 1 (same pattern as
  // customers/page.tsx), staying on a now out-of-range page would otherwise
  // show an empty result.
  useResetOnChange([filters.placement, filters.status], () => setPage(1));

  const faqsQuery = useQuery({
    queryKey: ["cms", "faqs", "search", filters, page] as const,
    queryFn: () =>
      searchCmsFaqs({
        placement: filters.placement === "" ? undefined : (Number(filters.placement) as CmsPlacement),
        status: filters.status === "" ? undefined : (Number(filters.status) as CmsContentStatus),
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const invalidateFaqs = () => queryClient.invalidateQueries({ queryKey: ["cms", "faqs", "search"] });

  const createMutation = useMutation({
    mutationFn: (request: CmsFaqCreateRequest) => createCmsFaq(request),
    onSuccess: () => {
      invalidateFaqs();
      setFormError(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, request }: { id: string; request: CmsFaqUpdateRequest }) => updateCmsFaq(id, request),
    onSuccess: () => {
      invalidateFaqs();
      setFormError(null);
      setEditingFaq(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, published }: { id: string; published: boolean }) => setCmsFaqPublished(id, published),
    onSuccess: invalidateFaqs,
  });

  const handleSubmit = (request: CmsFaqCreateRequest | CmsFaqUpdateRequest) => {
    if (editingFaq) {
      updateMutation.mutate({ id: editingFaq.id, request: request as CmsFaqUpdateRequest });
    } else {
      createMutation.mutate(request as CmsFaqCreateRequest);
    }
  };

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
    setPage(1);
  };

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="CMS & Content"
        subtitle="Static pages, banners, and site-level FAQs with draft/publish, scheduling, and placement (SRS 12.16)."
      />

      <CmsTabs />

      <div className="flex flex-col gap-6">
        {canWrite ? (
          <Card
            title={editingFaq ? `Edit FAQ: ${editingFaq.question}` : "Create an FAQ"}
            description="Question, answer, placement, sort order, and an optional publish window (SRS 12.16.1/12.16.2)."
          >
            <CmsFaqForm
              faq={editingFaq}
              isSubmitting={createMutation.isPending || updateMutation.isPending}
              submitError={formError}
              onSubmit={handleSubmit}
              onCancel={
                editingFaq
                  ? () => {
                      setEditingFaq(null);
                      setFormError(null);
                    }
                  : undefined
              }
            />
          </Card>
        ) : null}

        <FilterBar
          columns={2}
          onClear={clearFilters}
          activeCount={countActiveFilters(filters)}
          busy={faqsQuery.isFetching}
        >
          <Select
            label="Placement"
            options={PLACEMENT_FILTER_OPTIONS}
            value={filters.placement}
            onChange={(event) => setFilters((current) => ({ ...current, placement: event.target.value }))}
          />
          <Select
            label="Status"
            options={STATUS_FILTER_OPTIONS}
            value={filters.status}
            onChange={(event) => setFilters((current) => ({ ...current, status: event.target.value }))}
          />
        </FilterBar>

        <CmsFaqsTable
          faqs={faqsQuery.data?.items}
          isLoading={faqsQuery.isPending}
          isFetching={faqsQuery.isFetching}
          error={faqsQuery.error}
          onRetry={() => faqsQuery.refetch()}
          canWrite={canWrite}
          onEdit={(faq) => {
            setEditingFaq(faq);
            setFormError(null);
          }}
          onTogglePublished={(faq) =>
            toggleMutation.mutate({ id: faq.id, published: faq.status !== CmsContentStatus.Published })
          }
          togglingId={toggleMutation.isPending ? toggleMutation.variables?.id : undefined}
          toggleError={toggleMutation.error}
          emptyAction={
            countActiveFilters(filters) > 0 ? (
              <Button variant="secondary" onClick={clearFilters}>
                Clear filters
              </Button>
            ) : undefined
          }
          footer={
            faqsQuery.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={faqsQuery.data.totalCount}
                onPageChange={setPage}
                busy={faqsQuery.isFetching}
                itemLabel="FAQ"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
