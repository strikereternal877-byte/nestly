"use client";

import { useQuery } from "@tanstack/react-query";
import { DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { listMappedPincodesWithoutProviderCoverage } from "@/lib/serviceability-api";
import type { MappedPincodeWithoutProviderCoverageResponse } from "@/lib/serviceability-types";

/**
 * Coverage gap map, quadrant 3 (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
 * Proposed new page, Coverage gap map"): active service/pincode mappings
 * with no active provider actually able to fulfil them - the pincode looks
 * bookable, but there's nobody onboarded to serve it. See
 * `IServiceabilityMappingManagementService.ListMappedPincodesWithoutProviderCoverageAsync`'s
 * doc comment for why this is informational only: the mapping is correct,
 * the gap is in provider onboarding, which this screen has no action for.
 */
export function MappedWithoutProviderCoverageSection() {
  const gapsQuery = useQuery({
    queryKey: ["mapped-without-provider-coverage"],
    queryFn: listMappedPincodesWithoutProviderCoverage,
  });

  const columns: DataTableColumn<MappedPincodeWithoutProviderCoverageResponse>[] = [
    {
      key: "service",
      header: "Service",
      sortValue: (row) => row.serviceName,
      cell: (row) => <span className="font-medium text-fg">{row.serviceName}</span>,
    },
    {
      key: "pincode",
      header: "Pincode",
      sortValue: (row) => row.pincodeCode,
      cell: (row) => <span className="nums">{row.pincodeCode}</span>,
    },
  ];

  return (
    <DataTable
      title="Mapped but no active provider"
      description="The mapping is active and correct, but no active provider currently has matching skill + area coverage to fulfil it. Informational — onboard or reactivate a provider to close the gap."
      columns={columns}
      rows={gapsQuery.data}
      rowKey={(row) => row.mappingId}
      isLoading={gapsQuery.isPending}
      isFetching={gapsQuery.isFetching}
      error={gapsQuery.error}
      onRetry={() => gapsQuery.refetch()}
      caption="Active mappings with no active provider coverage"
      emptyTitle="No gaps"
      emptyDescription="Every active mapping has at least one active provider able to fulfil it."
      hideDensityToggle
      skeletonRows={3}
      minWidth="640px"
      maxHeight="360px"
    />
  );
}
