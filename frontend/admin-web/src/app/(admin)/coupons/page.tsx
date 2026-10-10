"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Button, Card, Field, PageHeading, Select } from "@/components/ui";
import { FilterBar, Pagination, countActiveFilters } from "@/components/data-table";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { describeError } from "@/lib/api";
import { createCoupon, searchCoupons, setCouponActive, updateCoupon } from "@/lib/coupon-api";
import type { CouponAdminResponse, CouponCreateRequest, CouponUpdateRequest } from "@/lib/coupon-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CouponForm } from "./_components/CouponForm";
import { CouponsTable } from "./_components/CouponsTable";
import { CouponsTabs } from "./_components/CouponsTabs";

const PAGE_SIZE = 20;

const STATUS_FILTER_OPTIONS = [
  { value: "", label: "All statuses" },
  { value: "true", label: "Active" },
  { value: "false", label: "Suspended" },
] as const;

interface CouponFilters {
  code: string;
  status: string;
}

const EMPTY_FILTERS: CouponFilters = { code: "", status: "" };

/**
 * Coupon management (SRS 12.12.1, task 119): search/filter, create, edit,
 * and suspend/reactivate. Gated behind the "coupons" permission module -
 * every mutating control checks `canWriteModule` the same way every other
 * admin screen does (see ServiceabilityPage's doc comment); the route itself
 * is only reachable once AdminSidebar already filtered it in by "coupons.read".
 */
export default function CouponsPage() {
  const claims = useAdminClaims();
  const [filters, setFilters] = useState<CouponFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [editingCoupon, setEditingCoupon] = useState<CouponAdminResponse | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const canWrite = canWriteModule(claims, "coupons");
  const queryClient = useQueryClient();

  // Live filtering (no Search button): Code is debounced 300ms, shared with
  // the typeahead below rather than debounced twice; Status (a dropdown)
  // applies immediately.
  const [debouncedCode, setDebouncedCode] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedCode(filters.code.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.code]);

  // Any filter change resets to page 1 (same pattern as customers/page.tsx).
  useResetOnChange([debouncedCode, filters.status], () => setPage(1));

  const codeSuggestionsQuery = useQuery({
    queryKey: ["coupons", "code-suggestions", debouncedCode] as const,
    queryFn: () => searchCoupons({ code: debouncedCode, page: 1, pageSize: 8 }),
    enabled: debouncedCode.length >= 2,
    placeholderData: keepPreviousData,
  });

  const couponsQuery = useQuery({
    queryKey: ["coupons", "search", debouncedCode, filters.status, page] as const,
    queryFn: () =>
      searchCoupons({
        code: debouncedCode || undefined,
        isActive: filters.status === "" ? undefined : filters.status === "true",
        page,
        pageSize: PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const invalidateCoupons = () => queryClient.invalidateQueries({ queryKey: ["coupons", "search"] });

  const createMutation = useMutation({
    mutationFn: (request: CouponCreateRequest) => createCoupon(request),
    onSuccess: () => {
      invalidateCoupons();
      setFormError(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, request }: { id: string; request: CouponUpdateRequest }) => updateCoupon(id, request),
    onSuccess: () => {
      invalidateCoupons();
      setFormError(null);
      setEditingCoupon(null);
    },
    onError: (error) => setFormError(describeError(error)),
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => setCouponActive(id, isActive),
    onSuccess: invalidateCoupons,
  });

  const handleSubmit = (request: CouponCreateRequest | CouponUpdateRequest) => {
    if (editingCoupon) {
      updateMutation.mutate({ id: editingCoupon.id, request: request as CouponUpdateRequest });
    } else {
      createMutation.mutate(request as CouponCreateRequest);
    }
  };

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
    setDebouncedCode("");
    setPage(1);
  };

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Coupons & Campaigns"
        subtitle="Discount codes with every rule dimension - type, value, caps, limits, validity, and applicability (SRS 12.12.1)."
      />

      <CouponsTabs />

      <div className="flex flex-col gap-6">
        {canWrite ? (
          <Card
            title={editingCoupon ? `Edit coupon: ${editingCoupon.code}` : "Create a coupon"}
            description={
              editingCoupon
                ? "The coupon code cannot be changed once created."
                : "Every field below maps to one of SRS 12.12.1's coupon creation fields."
            }
          >
            <CouponForm
              coupon={editingCoupon}
              isSubmitting={createMutation.isPending || updateMutation.isPending}
              submitError={formError}
              onSubmit={handleSubmit}
              onCancel={
                editingCoupon
                  ? () => {
                      setEditingCoupon(null);
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
          busy={couponsQuery.isFetching}
        >
          <Field
            label="Code"
            name="code"
            autoComplete="on"
            list="coupon-code-suggestions"
            placeholder="Search by code…"
            value={filters.code}
            onChange={(event) => setFilters((current) => ({ ...current, code: event.target.value }))}
          />
          <datalist id="coupon-code-suggestions">
            {(codeSuggestionsQuery.data?.items ?? []).map((coupon) => (
              <option key={coupon.id} value={coupon.code} />
            ))}
          </datalist>
          <Select
            label="Status"
            options={STATUS_FILTER_OPTIONS}
            value={filters.status}
            onChange={(event) => setFilters((current) => ({ ...current, status: event.target.value }))}
          />
        </FilterBar>

        <CouponsTable
          coupons={couponsQuery.data?.items}
          isLoading={couponsQuery.isPending}
          isFetching={couponsQuery.isFetching}
          error={couponsQuery.error}
          onRetry={() => couponsQuery.refetch()}
          canWrite={canWrite}
          onEdit={(coupon) => {
            setEditingCoupon(coupon);
            setFormError(null);
          }}
          onToggleActive={(coupon) => toggleMutation.mutate({ id: coupon.id, isActive: !coupon.isActive })}
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
            couponsQuery.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={couponsQuery.data.totalCount}
                onPageChange={setPage}
                busy={couponsQuery.isFetching}
                itemLabel="coupon"
              />
            ) : null
          }
        />
      </div>
    </div>
  );
}
