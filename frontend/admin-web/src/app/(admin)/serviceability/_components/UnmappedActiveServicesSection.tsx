"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Alert, Button, Select } from "@/components/ui";
import { DataTable } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { describeError } from "@/lib/api";
import { createServicePincodeMapping, listPincodes, listUnmappedActiveServices } from "@/lib/serviceability-api";
import type { UnmappedActiveServiceResponse } from "@/lib/serviceability-types";
import { toLookupOptions } from "./lookup-options";

/**
 * Coverage gap map, quadrant 1 (docs/OPEN-FIXES-FEATURES.csv "Admin Web,
 * Proposed new page, Coverage gap map"): active services with zero active
 * pincode mappings anywhere - launched, but silently unbookable everywhere.
 * See `IServiceabilityMappingManagementService.ListUnmappedActiveServicesAsync`'s
 * doc comment for why this is a warning list, not a guard.
 *
 * A service has no mapped pincode by definition here, so "create mapping"
 * needs a pincode picked per row before it means anything - reuses the same
 * `createServicePincodeMapping` call the mapping screen's own form uses.
 */
export function UnmappedActiveServicesSection({ canWrite }: { canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [pincodeByService, setPincodeByService] = useState<Record<string, string>>({});

  const servicesQuery = useQuery({ queryKey: ["unmapped-active-services"], queryFn: listUnmappedActiveServices });
  const pincodesQuery = useQuery({ queryKey: ["pincodes", ""], queryFn: () => listPincodes(undefined) });
  const pincodeOptions = toLookupOptions(pincodesQuery.data, (p) => `${p.code} · ${p.cityName}`);

  const createMutation = useMutation({
    mutationFn: createServicePincodeMapping,
    onSuccess: (_mapping, variables) => {
      // A newly-mapped pair can move a row out of every quadrant on this
      // page, not just this one - a service may still be unmapped elsewhere,
      // but the new pincode could now also be a provider-coverage gap or a
      // mapped-without-coverage row.
      queryClient.invalidateQueries({ queryKey: ["unmapped-active-services"] });
      queryClient.invalidateQueries({ queryKey: ["serviceability-coverage-gaps"] });
      queryClient.invalidateQueries({ queryKey: ["mapped-without-provider-coverage"] });
      setPincodeByService((current) => {
        const next = { ...current };
        delete next[variables.serviceId];
        return next;
      });
    },
  });

  const columns: DataTableColumn<UnmappedActiveServiceResponse>[] = [
    {
      key: "service",
      header: "Service",
      sortValue: (row) => row.serviceName,
      cell: (row) => <span className="font-medium text-fg">{row.serviceName}</span>,
    },
    {
      key: "category",
      header: "Category",
      sortValue: (row) => row.categoryName,
      cell: (row) => row.categoryName,
    },
  ];

  return (
    <div className="flex flex-col gap-3">
      {createMutation.isError ? <Alert tone="error">{describeError(createMutation.error)}</Alert> : null}
      <DataTable
        title="No mapping"
        description="Active services with no active pincode mapping anywhere — launched, but unbookable everywhere until mapped."
        columns={columns}
        rows={servicesQuery.data}
        rowKey={(row) => row.serviceId}
        isLoading={servicesQuery.isPending}
        isFetching={servicesQuery.isFetching}
        error={servicesQuery.error}
        onRetry={() => servicesQuery.refetch()}
        caption="Active services with no serviceability mapping"
        emptyTitle="No gaps"
        emptyDescription="Every active service has at least one active pincode mapping."
        hideDensityToggle
        skeletonRows={3}
        minWidth="720px"
        maxHeight="360px"
        rowActions={
          canWrite
            ? (row) => {
                const selectedPincodeId = pincodeByService[row.serviceId] ?? "";
                const isCreating =
                  createMutation.isPending && createMutation.variables?.serviceId === row.serviceId;
                return (
                  <div className="flex items-center gap-2">
                    <div className="w-44">
                      <Select
                        label="Pincode"
                        value={selectedPincodeId}
                        onChange={(e) =>
                          setPincodeByService((current) => ({ ...current, [row.serviceId]: e.target.value }))
                        }
                        options={[{ value: "", label: "Select a pincode…" }, ...pincodeOptions]}
                      />
                    </div>
                    <Button
                      type="button"
                      size="sm"
                      variant="secondary"
                      disabled={selectedPincodeId === "" || isCreating}
                      loading={isCreating}
                      onClick={() =>
                        createMutation.mutate({ serviceId: row.serviceId, pincodeId: selectedPincodeId })
                      }
                    >
                      Create mapping
                    </Button>
                  </div>
                );
              }
            : undefined
        }
      />
    </div>
  );
}
