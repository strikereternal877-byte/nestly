"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Button, Card, PageHeading, Select } from "@/components/ui";
import { FilterBar, Pagination, countActiveFilters } from "@/components/data-table";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { describeError } from "@/lib/api";
import { createBanner, searchBanners, setBannerPublished, updateBanner } from "@/lib/cms-api";
import {
  CmsContentStatus,
  type BannerCreateRequest,
  type BannerResponse,
  type BannerUpdateRequest,
  type CmsPlacement,
} from "@/lib/cms-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { BannerForm } from "../_components/BannerForm";
import { BannersTable } from "../_components/BannersTable";
import { PLACEMENT_FILTER_OPTIONS, STATUS_FILTER_OPTIONS } from "../_components/cmsDisplay";
import { CmsTabs } from "../_components/CmsTabs";

const PAGE_SIZE = 20;

interface BannerFilters {
  placement: string;
  status: string;
}

const EMPTY_FILTERS: BannerFilters = { placement: "", status: "" };

/**
 * Promotional banner management (SRS 12.16.1, task 125a): search/filter by
 * placement and status, create, edit, and publish/unpublish. Gated behind
 * the "cms" permission module - every mutating control checks
 * `canWriteModule` the same way CmsPagesPage does; the route itself is only
 * reachable once AdminSidebar already filtered it in by "cms.read".
 */
export default function CmsBannersPage() {
  const claims = useAdminClaims();
  const [filters, setFilters] = useState<BannerFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [editingBanner, setEditingBanner] = useState<BannerResponse | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const canWrite = canWriteModule(claims, "cms");
  const queryClient = useQueryClient();

  // Live filtering (no Search button): both fields are dropdowns, so no
  // debouncing is needed - a change just resets to page 1 (same pattern as
  // customers/page.tsx), staying on a now out-of-range page would otherwise
  // show an empty result.
  useResetOnChange([filters.placement, filters.status], () => setPage(1));

  const bannersQuery = useQuery({
    queryKey: ["cms", "banners", "search", filters, page] as const,
    queryFn: () =>
      searchBanners({
        placement: filters.placement === "" ? undefined : (Number(filters.placement) as CmsPlacement),
        status: filters.status === "" ? undefined : (Number(filters.status) as CmsContentStatus),
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const invalidateBanners = () => queryClient.invalidateQueries({ queryKey: ["cms", "banners", "search"] });

  const createMutation = useMutation({
    mutationFn: (request: BannerCreateRequest) => createBanner(request),
    onSuccess: () => {
      invalidateBanners();
      setFormError(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, request }: { id: string; request: BannerUpdateRequest }) => updateBanner(id, request),
    onSuccess: () => {
      invalidateBanners();
      setFormError(null);
      setEditingBanner(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, published }: { id: string; published: boolean }) => setBannerPublished(id, published),
    onSuccess: invalidateBanners,
  });

  const handleSubmit = (request: BannerCreateRequest | BannerUpdateRequest) => {
    if (editingBanner) {
      updateMutation.mutate({ id: editingBanner.id, request: request as BannerUpdateRequest });
    } else {
      createMutation.mutate(request as BannerCreateRequest);
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
            title={editingBanner ? `Edit banner: ${editingBanner.title}` : "Create a banner"}
            description="Image, link, placement, sort order, and an optional publish window (SRS 12.16.1/12.16.2)."
          >
            <BannerForm
              banner={editingBanner}
              isSubmitting={createMutation.isPending || updateMutation.isPending}
              submitError={formError}
              onSubmit={handleSubmit}
              onCancel={
                editingBanner
                  ? () => {
                      setEditingBanner(null);
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
          busy={bannersQuery.isFetching}
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

        <BannersTable
          banners={bannersQuery.data?.items}
          isLoading={bannersQuery.isPending}
          isFetching={bannersQuery.isFetching}
          error={bannersQuery.error}
          onRetry={() => bannersQuery.refetch()}
          canWrite={canWrite}
          onEdit={(banner) => {
            setEditingBanner(banner);
            setFormError(null);
          }}
          onTogglePublished={(banner) =>
            toggleMutation.mutate({ id: banner.id, published: banner.status !== CmsContentStatus.Published })
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
            bannersQuery.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={bannersQuery.data.totalCount}
                onPageChange={setPage}
                busy={bannersQuery.isFetching}
                itemLabel="banner"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
