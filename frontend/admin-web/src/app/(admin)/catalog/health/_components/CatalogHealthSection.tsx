"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { listCatalogHealthIssues } from "@/lib/catalog-api";
import type { CatalogHealthIssueResponse, CatalogHealthReason } from "@/lib/catalog-types";

/**
 * Reason code → badge label/tone, mirroring the tone semantics
 * `status-badges.tsx` documents (colour is information, not decoration): the
 * mapping check gets its own `info` tone since it links straight to the fix
 * on the coverage gap map, the other three stay `warning` — none of them
 * blocks anything, but they still need a human to look.
 */
const REASON_LABELS: Record<CatalogHealthReason, string> = {
  NoPrice: "No price",
  NoImage: "No image",
  NoMapping: "No mapping",
  NeverBooked: "Never booked",
};

const REASON_TONES: Record<CatalogHealthReason, BadgeTone> = {
  NoPrice: "warning",
  NoImage: "warning",
  NoMapping: "info",
  NeverBooked: "warning",
};

/**
 * docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog
 * health": lists every active service missing a city price, a cover image,
 * an active serviceability mapping, or that has never been booked — a
 * pre-publish completeness audit, not a block. See
 * `IServiceManagementService.ListHealthIssuesAsync`'s doc comment for why
 * nothing here stops a service from being created or activated; an
 * incomplete catalog entry is a normal, valid state while it's still being
 * filled in, same reasoning as the coverage gap map's unmapped-services list.
 *
 * The "No mapping" reason cross-links to the coverage gap map
 * (`/serviceability/coverage-gaps`) rather than duplicating a "create
 * mapping" picker here — that screen already owns the fix for this exact
 * condition (`UnmappedActiveServicesSection`, which this endpoint reuses
 * server-side via `ListUnmappedActiveServicesAsync`).
 */
export function CatalogHealthSection() {
  const healthQuery = useQuery({ queryKey: ["catalog-health"], queryFn: listCatalogHealthIssues });

  const columns: DataTableColumn<CatalogHealthIssueResponse>[] = [
    {
      key: "service",
      header: "Service",
      sortValue: (row) => row.serviceName,
      cell: (row) => (
        <Link
          href={`/catalog/services/${row.serviceId}`}
          className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {row.serviceName}
        </Link>
      ),
    },
    {
      key: "category",
      header: "Category",
      sortValue: (row) => row.categoryName,
      cell: (row) => row.categoryName,
    },
    {
      key: "issues",
      header: "Failing checks",
      cell: (row) => (
        <div className="flex flex-wrap gap-1.5">
          {row.reasons.map((reason) =>
            reason === "NoMapping" ? (
              <Link key={reason} href="/serviceability/coverage-gaps">
                <Badge tone={REASON_TONES[reason]} className="cursor-pointer hover:opacity-80">
                  {REASON_LABELS[reason]}
                </Badge>
              </Link>
            ) : (
              <Badge key={reason} tone={REASON_TONES[reason]}>
                {REASON_LABELS[reason]}
              </Badge>
            ),
          )}
        </div>
      ),
    },
  ];

  return (
    <DataTable
      title="Incomplete active services"
      description="Active services missing a price, an image, a serviceability mapping, or that have never been booked — worth fixing before a customer hits them."
      columns={columns}
      rows={healthQuery.data}
      rowKey={(row) => row.serviceId}
      isLoading={healthQuery.isPending}
      isFetching={healthQuery.isFetching}
      error={healthQuery.error}
      onRetry={() => healthQuery.refetch()}
      caption="Active services failing at least one catalog completeness check"
      emptyTitle="Catalog is healthy"
      emptyDescription="Every active service has a price, an image, a serviceability mapping, and at least one booking."
      hideDensityToggle
      skeletonRows={4}
      minWidth="760px"
    />
  );
}
