"use client";

import Link from "next/link";
import { useState } from "react";
import type { ReactNode } from "react";
import { ActiveBadge, ConfirmDialog, DataTable, formatCurrency, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Button } from "@/components/ui";
import { describeError } from "@/lib/api";
import { CouponCustomerSegment, CouponDiscountType, type CouponAdminResponse } from "@/lib/coupon-types";

/** Sentence-case label for the discount type, matching the codebase's Field/Select label convention. */
function formatDiscount(coupon: CouponAdminResponse): string {
  const value =
    coupon.discountType === CouponDiscountType.Percentage
      ? `${coupon.discountValue}%`
      : formatCurrency(coupon.discountValue);
  return coupon.maxDiscountAmount
    ? `${value} (capped at ${formatCurrency(coupon.maxDiscountAmount)})`
    : value;
}

function formatSegment(segment: CouponCustomerSegment): string {
  switch (segment) {
    case CouponCustomerSegment.FirstBookingOnly:
      return "First booking only";
    case CouponCustomerSegment.RepeatBookingOnly:
      return "Repeat customers only";
    default:
      return "All customers";
  }
}

function formatUsage(coupon: CouponAdminResponse): string {
  const total = coupon.usageLimitTotal === null ? "∞" : coupon.usageLimitTotal.toLocaleString("en-IN");
  return `${coupon.redemptionCount.toLocaleString("en-IN")} / ${total}`;
}

/**
 * Coupon list (SRS 12.12.1). Server-paged, so no sortable columns: the search
 * endpoint takes no sort parameter and a header sort would silently reorder
 * only the rows on this page.
 */
export function CouponsTable({
  coupons,
  isLoading,
  isFetching,
  error,
  onRetry,
  canWrite,
  onEdit,
  onToggleActive,
  togglingId,
  toggleError,
  emptyAction,
  footer,
}: {
  coupons: CouponAdminResponse[] | undefined;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  canWrite: boolean;
  onEdit: (coupon: CouponAdminResponse) => void;
  onToggleActive: (coupon: CouponAdminResponse) => void;
  togglingId?: string;
  toggleError?: unknown;
  emptyAction?: ReactNode;
  footer?: ReactNode;
}) {
  // Suspending a coupon stops every customer mid-checkout from applying it, so
  // it is confirmed. Reactivating is not — it is the recoverable direction.
  const [pendingSuspend, setPendingSuspend] = useState<CouponAdminResponse | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const isSuspending = pendingSuspend !== null && togglingId === pendingSuspend.id;

  // "Adjusting state when a prop changes" (react.dev/learn/you-might-not-
  // need-an-effect), not an effect: shouldSettle is true for exactly one
  // render (a confirmed toggle that has just stopped being pending), and
  // comparing against its own last-seen value during render is what fires
  // the close-out exactly once per settle rather than on every render where
  // both conditions still hold.
  const shouldSettle = confirmed && !isSuspending;
  const [wasSettling, setWasSettling] = useState(false);
  if (shouldSettle !== wasSettling) {
    setWasSettling(shouldSettle);
    if (shouldSettle) {
      setConfirmed(false);
      if (!toggleError) setPendingSuspend(null);
    }
  }

  const columns: DataTableColumn<CouponAdminResponse>[] = [
    {
      key: "code",
      header: "Code",
      cell: (coupon) => <span className="nums font-medium text-fg">{coupon.code}</span>,
    },
    { key: "discount", header: "Discount", cell: (coupon) => <span className="nums">{formatDiscount(coupon)}</span> },
    {
      key: "minOrder",
      header: "Min order",
      numeric: true,
      cell: (coupon) => formatCurrency(coupon.minOrderAmount),
    },
    {
      key: "validity",
      header: "Validity",
      cell: (coupon) => (
        <span className="nums whitespace-nowrap">
          {formatDate(coupon.validFromUtc)} – {formatDate(coupon.validToUtc)}
        </span>
      ),
    },
    { key: "usage", header: "Usage", numeric: true, cell: (coupon) => formatUsage(coupon) },
    { key: "segment", header: "Segment", cell: (coupon) => formatSegment(coupon.customerSegment) },
    {
      key: "category",
      header: "Category",
      cell: (coupon) => coupon.applicableCategoryName ?? <span className="text-fg-subtle">All categories</span>,
    },
    {
      key: "status",
      header: "Status",
      cell: (coupon) => <ActiveBadge active={coupon.isActive} inactiveLabel="Suspended" />,
    },
  ];

  return (
    <>
      <DataTable
        title="Coupons"
        description="Search and manage every coupon (SRS 12.12.1)."
        columns={columns}
        rows={coupons}
        rowKey={(coupon) => coupon.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={onRetry}
        caption="Coupons matching the current filters"
        emptyTitle="No coupons match the current filters"
        emptyDescription="Clear the filters, or create a coupon above."
        emptyAction={emptyAction}
        skeletonRows={6}
        minWidth="1240px"
        footer={footer}
        rowActions={(coupon) => (
          <>
            {/* A real link rather than a button that pushes: the report is a
                destination, so it must stay middle-clickable. */}
            <Link
              href={`/coupons/redemptions?couponId=${coupon.id}`}
              aria-label={`Redemption report for ${coupon.code}`}
              className="inline-flex h-8 items-center rounded-lg px-3 text-xs font-medium text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg"
            >
              Report
            </Link>
            {canWrite ? (
              <>
                <Button type="button" size="sm" variant="ghost" onClick={() => onEdit(coupon)}>
                  Edit
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant={coupon.isActive ? "secondary" : "subtle"}
                  disabled={togglingId === coupon.id}
                  loading={togglingId === coupon.id && !coupon.isActive}
                  onClick={() => (coupon.isActive ? setPendingSuspend(coupon) : onToggleActive(coupon))}
                >
                  {coupon.isActive ? "Suspend" : "Activate"}
                </Button>
              </>
            ) : null}
          </>
        )}
      />

      <ConfirmDialog
        open={pendingSuspend !== null}
        title="Suspend this coupon?"
        description="Customers stop being able to apply it immediately. Bookings that already used it are unaffected."
        confirmLabel="Suspend"
        cancelLabel="Keep active"
        loading={isSuspending}
        error={toggleError ? describeError(toggleError) : null}
        onCancel={() => {
          setConfirmed(false);
          setPendingSuspend(null);
        }}
        onConfirm={() => {
          if (!pendingSuspend) return;
          setConfirmed(true);
          onToggleActive(pendingSuspend);
        }}
      >
        {pendingSuspend ? (
          <p className="text-sm text-fg-muted">
            <span className="nums font-medium text-fg">{pendingSuspend.code}</span> —{" "}
            {formatDiscount(pendingSuspend)}, redeemed{" "}
            <span className="nums">{pendingSuspend.redemptionCount.toLocaleString("en-IN")}</span> times.
          </p>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
