"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button } from "@/components/ui";
import { DataTable, ExportCsvButton } from "@/components/data-table";
import type { CsvColumn, DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import { createServicePincodeMapping, listServiceabilityCoverageGaps } from "@/lib/serviceability-api";
import type { ServiceabilityCoverageGapResponse } from "@/lib/serviceability-types";

const COVERAGE_GAP_CSV_COLUMNS: readonly CsvColumn<ServiceabilityCoverageGapResponse>[] = [
  { header: "Service", value: (row) => row.serviceName },
  { header: "Pincode", value: (row) => row.pincodeCode },
];

/**
 * Coverage gap map, quadrant 2 (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
 * Proposed new page, Coverage gap map"): (service, pincode) pairs where an
 * active provider already has matching skill + area coverage but no active
 * serviceability mapping exists, so the pincode still shows the service as
 * unbookable despite a qualified provider already being onboarded there. See
 * `IServiceabilityMappingManagementService.ListPincodesWithProviderCoverageButNoServiceMappingAsync`'s
 * doc comment - `AutoEnableProviderCoverageAsync` now closes most of these
 * proactively, so this list is mainly an audit trail and a safety net for
 * cases it doesn't cover.
 *
 * Both service and pincode are already known here, so "create mapping" is a
 * single reuse of `createServicePincodeMapping` per row - no picker needed.
 */
export function CoverageGapsSection({ canWrite }: { canWrite: boolean }) {
  const queryClient = useQueryClient();

  const gapsQuery = useQuery({ queryKey: ["serviceability-coverage-gaps"], queryFn: listServiceabilityCoverageGaps });

  const createMutation = useMutation({
    mutationFn: createServicePincodeMapping,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["serviceability-coverage-gaps"] });
      queryClient.invalidateQueries({ queryKey: ["unmapped-active-services"] });
      queryClient.invalidateQueries({ queryKey: ["mapped-without-provider-coverage"] });
    },
  });

  const columns: DataTableColumn<ServiceabilityCoverageGapResponse>[] = [
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
    <div className="flex flex-col gap-3">
      {createMutation.isError ? <Alert tone="error">{describeError(createMutation.error)}</Alert> : null}
      <DataTable
        title="Provider coverage but no mapping"
        description="A qualified, active provider already covers this pincode, but there is no active serviceability mapping to make it bookable there."
        actions={
          <ExportCsvButton
            rows={gapsQuery.data}
            columns={COVERAGE_GAP_CSV_COLUMNS}
            fileName={`coverage-gaps-export-${todayIsoDate()}.csv`}
          />
        }
        columns={columns}
        rows={gapsQuery.data}
        rowKey={(row) => `${row.serviceId}:${row.pincodeId}`}
        isLoading={gapsQuery.isPending}
        isFetching={gapsQuery.isFetching}
        error={gapsQuery.error}
        onRetry={() => gapsQuery.refetch()}
        caption="Service/pincode pairs with provider coverage and no mapping"
        emptyTitle="No gaps"
        emptyDescription="Every pincode with active provider coverage also has an active mapping."
        hideDensityToggle
        skeletonRows={3}
        minWidth="720px"
        maxHeight="360px"
        rowActions={
          canWrite
            ? (row) => {
                const isCreating =
                  createMutation.isPending &&
                  createMutation.variables?.serviceId === row.serviceId &&
                  createMutation.variables?.pincodeId === row.pincodeId;
                return (
                  <Button
                    type="button"
                    size="sm"
                    variant="secondary"
                    loading={isCreating}
                    disabled={isCreating}
                    onClick={() =>
                      createMutation.mutate({ serviceId: row.serviceId, pincodeId: row.pincodeId })
                    }
                  >
                    Create mapping
                  </Button>
                );
              }
            : undefined
        }
      />
    </div>
  );
}
